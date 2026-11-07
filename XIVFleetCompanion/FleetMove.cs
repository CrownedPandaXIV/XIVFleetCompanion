using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Npgsql;

namespace XIVFleetCompanion;

/// <summary>
/// Moves the fleet between the fleet file (Just this PC) and a shared PostgreSQL database, either way. Every table the
/// file has is copied (the lookups both already have aside), in one transaction on the side it goes to, while the side
/// it comes from is held still: on the file, other games wait to write; on PostgreSQL, the copy reads one moment of it.
/// Then the two are compared table by table, and only when every count matches is the move kept. The side it came from
/// is never changed (the file only gets a note that it was moved, so a later move back may reuse it).
/// </summary>
public static class FleetMove
{
    /// <summary>A move that can't be made, or failed and changed nothing, with why in plain words.</summary>
    public sealed class RefusedException : Exception
    {
        public RefusedException(string message) : base(message) { }
    }

    /// <summary>What was moved: rows per table.</summary>
    public sealed record Result(IReadOnlyList<(string Table, long Rows)> Tables, bool Trial = false, double Seconds = 0)
    {
        public long Rows => Tables.Sum(t => t.Rows);
        public string Text => Trial
            ? $"Trial run: {Rows:N0} rows in {Tables.Count} tables copied and checked in {Seconds:0.#} seconds, and every row matched. Nothing was changed: the move would work."
            : $"Moved {Rows:N0} rows in {Tables.Count} tables; every table matches.";
    }

    /// <summary>The tables that move: every table the fleet file has, except the lookups its tables file fills itself.</summary>
    public static List<string> TablesToMove(FileDatabase.Files files)
    {
        var seeded = Regex.Matches(files.Tables, @"^INSERT INTO (\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        return Regex.Matches(files.Tables, @"^CREATE TABLE (\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value)
            .Where(t => !seeded.Contains(t)).ToList();
    }

    /// <summary>The fleet's characters in a database (0 before it has the table).</summary>
    public static async Task<long> CharacterCount(DbConnection conn)
    {
        try
        {
            await using var cmd = Db.Command(conn, "SELECT count(*) FROM companion_character_current");
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        catch (DbException)
        {
            return 0;
        }
    }

    /// <summary>The file's note that its fleet moved away (null: it didn't), so a move back may use it again.</summary>
    public static async Task<string?> MovedAwayNote(DbConnection file)
    {
        try
        {
            await using var cmd = Db.Command(file, "SELECT value FROM companion_file_info WHERE key = 'moved_to'");
            return await cmd.ExecuteScalarAsync() as string;
        }
        catch (DbException)
        {
            return null;
        }
    }

    /// <summary>Why the fleet can't be moved into this fleet file (it has a fleet of its own), or null when it can.</summary>
    public static async Task<string?> WhyNotIntoFile(DbConnection file, string filePath)
    {
        var there = await CharacterCount(file);
        if (there == 0 || await MovedAwayNote(file) != null) return null;
        return $"This PC already has a fleet file with a fleet in it ({there} character{(there == 1 ? "" : "s")}), so nothing was copied. It's at {filePath}.";
    }

    // ---- Column types ----

    private sealed record Column(string Name, string Type);

    // PostgreSQL's columns of a table: name and type (udt name: timestamptz, timestamp, bool, numeric, _text, _int4...).
    private static async Task<List<Column>> PgColumns(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
    {
        var columns = new List<Column>();
        await using var cmd = new NpgsqlCommand(@"SELECT column_name, udt_name FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = @t ORDER BY ordinal_position", conn, tx);
        cmd.Parameters.AddWithValue("t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) columns.Add(new Column(r.GetString(0), r.GetString(1)));
        return columns;
    }

    // The file's columns of a table: name and declared type (INTEGER, REAL, TEXT, or none).
    private static async Task<List<Column>> FileColumns(DbConnection conn, DbTransaction? tx, string table)
    {
        var columns = new List<Column>();
        await using var cmd = Db.Command(conn, $"PRAGMA table_info(\"{table}\")", tx);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) columns.Add(new Column(r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2).ToUpperInvariant()));
        return columns;
    }

    // How a PostgreSQL column is read for the file: times as Unix seconds (a time without time zone as the database's
    // own time zone saw it, as now() stored it), yes/no as 0 or 1, lists as the file keeps them, numbers as text (Free
    // Company ids go past what a whole number holds).
    private static string PgRead(Column c) => c.Type switch
    {
        "timestamptz" => $"extract(epoch FROM \"{c.Name}\")::float8",
        "timestamp" => $"extract(epoch FROM \"{c.Name}\"::timestamptz)::float8",
        "bool" => $"\"{c.Name}\"::int",
        "_text" => $"array_to_string(\"{c.Name}\", E'\\n')",
        "numeric" or "_int4" or "_int8" or "jsonb" or "json" => $"\"{c.Name}\"::text",
        _ => $"\"{c.Name}\"",
    };

    // How a value from the file is written into a PostgreSQL column (the value is passed as text, or a number for times).
    private static string PgWrite(Column c, string p) => c.Type switch
    {
        "timestamptz" => $"to_timestamp({p}::float8)",
        "timestamp" => $"to_timestamp({p}::float8)::timestamp",
        "bool" => $"({p}::int <> 0)",
        "_text" => $"string_to_array({p}, E'\\n')",
        _ => $"{p}::{c.Type}",
    };

    private static string? AsText(object? v) => v switch
    {
        null or DBNull => null,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    // A value read from PostgreSQL (see PgRead) as the file's column keeps it.
    private static object? ForFile(object? v, string fileType, string? subjectType)
    {
        if (v is null or DBNull) return null;
        var type = fileType;
        // The history's subject_id has no type: a character's id is a whole number, a Free Company's text.
        if (type == "") type = subjectType == "fc" ? "TEXT" : "INTEGER";
        return type switch
        {
            "INTEGER" => v is string s ? (s.Contains('.') ? (long)decimal.Parse(s, CultureInfo.InvariantCulture) : long.Parse(s, CultureInfo.InvariantCulture))
                : Convert.ToInt64(v, CultureInfo.InvariantCulture),
            "REAL" => v is string s2 ? double.Parse(s2, CultureInfo.InvariantCulture) : Convert.ToDouble(v, CultureInfo.InvariantCulture),
            "TEXT" => AsText(v),
            _ => v,
        };
    }

    // ---- Moving ----

    /// <param name="progress">Told what's happening ("Copying companion_metric_history...").</param>
    /// <param name="trial">Copy and check everything, then undo it all (the shared database only keeps its tables, made
    /// ready as the app makes them): whether the move would work, and how long it takes.</param>
    public static async Task<Result> FileToSharedAsync(DbConnection file, NpgsqlConnection pg, FileDatabase.Files files, DatabaseUpkeep.Files pgFiles,
        string recordedBy, string movedTo, Action<string>? progress = null, bool trial = false)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        progress?.Invoke("Getting the shared database ready...");
        var ready = await DatabaseUpkeep.EnsureAsync(pg, pgFiles, recordedBy, wait: true);
        if (ready.Newer)
            throw new RefusedException("The shared database was updated by a newer app or plugin. Update this plugin first, then move.");
        var there = await CharacterCount(pg);
        if (there > 0)
            throw new RefusedException($"The shared database already has a fleet ({there} character{(there == 1 ? "" : "s")}), so nothing was copied: " +
                                       "moving into it would mix the two. Use a new, empty database (the app's Database Helper makes one).");

        var tables = TablesToMove(files);
        // Other games on this PC wait to write to the file until the copy is done (or give up and try at their next sync).
        await Db.Exec(file, "BEGIN IMMEDIATE");
        var moved = new List<(string, long)>();
        try
        {
            await using var tx = await pg.BeginTransactionAsync();
            try
            {
                foreach (var table in tables)
                {
                    progress?.Invoke($"Copying {table}...");
                    moved.Add((table, await CopyToPg(file, pg, tx, table)));
                }
                progress?.Invoke("Checking every table arrived...");
                await Compare(file, null, pg, tx, moved);
                if (trial) await tx.RollbackAsync();
                else await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
            if (trial)
            {
                await Db.Exec(file, "ROLLBACK");
                return new Result(moved, true, clock.Elapsed.TotalSeconds);
            }
            await Db.Exec(file, "INSERT INTO companion_file_info (key, value) VALUES ('moved_to', @v) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                null, ("v", movedTo));
            await Db.Exec(file, "COMMIT");
        }
        catch
        {
            await Db.Exec(file, "ROLLBACK");
            throw;
        }
        return new Result(moved);
    }

    /// <summary>
    /// Copies the shared database's fleet into the file. A file that already has a fleet is refused, unless its fleet was
    /// moved away earlier (its note): then a backup of it is made first and it's emptied.
    /// </summary>
    /// <param name="trial">Copy and check everything, then undo it (the file keeps only its tables, made ready).</param>
    public static async Task<Result> SharedToFileAsync(NpgsqlConnection pg, DbConnection file, string filePath, FileDatabase.Files files, string recordedBy,
        Action<string>? progress = null, bool trial = false)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        progress?.Invoke("Getting the fleet file ready...");
        var ready = await FileDatabase.EnsureAsync(file, files, recordedBy);
        if (ready.Newer)
            throw new RefusedException("The fleet file on this PC was updated by a newer plugin. Update this plugin first, then move.");
        if (await WhyNotIntoFile(file, filePath) is { } why) throw new RefusedException(why);
        var there = await CharacterCount(file);
        if (there > 0 && !trial)
        {
            progress?.Invoke("Backing up the old fleet file...");
            await FileDatabase.BackUpAsync(file, filePath, DateTime.Now);
        }

        var tables = TablesToMove(files);
        var moved = new List<(string, long)>();
        // One moment of the shared database, however other PCs write meanwhile.
        await using var read = await pg.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await using var tx = await file.BeginTransactionAsync();
        try
        {
            foreach (var table in tables)
            {
                progress?.Invoke($"Copying {table}...");
                await Db.Exec(file, $"DELETE FROM \"{table}\"", tx);
                moved.Add((table, await CopyToFile(pg, read, file, tx, table)));
            }
            await Db.Exec(file, "DELETE FROM companion_file_info WHERE key = 'moved_to'", tx);
            progress?.Invoke("Checking every table arrived...");
            await Compare(file, tx, pg, read, moved);
            if (trial) await tx.RollbackAsync();
            else await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        finally
        {
            await read.RollbackAsync();
        }
        return new Result(moved, trial, clock.Elapsed.TotalSeconds);
    }

    // Every row of a table from the file into PostgreSQL (its columns both have), a few hundred to a statement.
    private static async Task<long> CopyToPg(DbConnection file, NpgsqlConnection pg, NpgsqlTransaction tx, string table)
    {
        var pgColumns = await PgColumns(pg, tx, table);
        if (pgColumns.Count == 0) throw new RefusedException($"The shared database has no {table} table. Open the app (or its Database Helper) once so it can update the database, then move.");
        var fileColumns = (await FileColumns(file, null, table)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = pgColumns.Where(c => fileColumns.Contains(c.Name)).ToList();
        await new NpgsqlCommand($"DELETE FROM \"{table}\"", pg, tx).ExecuteNonQueryAsync();

        var rows = new List<object?[]>();
        await using (var cmd = Db.Command(file, $"SELECT {string.Join(", ", columns.Select(c => $"\"{c.Name}\""))} FROM \"{table}\""))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var row = new object?[columns.Count];
                for (var i = 0; i < columns.Count; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }

        var names = string.Join(", ", columns.Select(c => $"\"{c.Name}\""));
        foreach (var chunk in rows.Chunk(Math.Max(1, 2000 / Math.Max(1, columns.Count))))
        {
            await using var insert = new NpgsqlCommand { Connection = pg, Transaction = tx };
            var values = new List<string>();
            var n = 0;
            foreach (var row in chunk)
            {
                var one = new List<string>();
                for (var i = 0; i < columns.Count; i++)
                {
                    var p = $"@p{n++}";
                    if (columns[i].Type == "bytea")
                    {
                        // Raw bytes (a sub's route points) go as they are.
                        insert.Parameters.Add(new NpgsqlParameter(p.Substring(1), NpgsqlTypes.NpgsqlDbType.Bytea) { Value = (object?)(row[i] as byte[]) ?? DBNull.Value });
                        one.Add(p);
                        continue;
                    }
                    var value = columns[i].Type is "timestamptz" or "timestamp"
                        ? (row[i] is null ? DBNull.Value : Convert.ToDouble(row[i], CultureInfo.InvariantCulture))
                        : (object?)AsText(row[i]) ?? DBNull.Value;
                    insert.Parameters.Add(new NpgsqlParameter(p.Substring(1), value is DBNull ? NpgsqlTypes.NpgsqlDbType.Text : InferType(value)) { Value = value });
                    one.Add(PgWrite(columns[i], p));
                }
                values.Add("(" + string.Join(", ", one) + ")");
            }
            insert.CommandText = $"INSERT INTO \"{table}\" ({names}) VALUES {string.Join(", ", values)}";
            await insert.ExecuteNonQueryAsync();
        }
        // A numbered table carries on after the highest number copied.
        if (columns.Any(c => c.Name == "id"))
            await new NpgsqlCommand($@"SELECT setval(pg_get_serial_sequence('""{table}""', 'id'), GREATEST((SELECT max(id) FROM ""{table}""), 1),
                (SELECT max(id) IS NOT NULL FROM ""{table}""))", pg, tx).ExecuteScalarAsync();
        return rows.Count;
    }

    private static NpgsqlTypes.NpgsqlDbType InferType(object value) => value is double ? NpgsqlTypes.NpgsqlDbType.Double : NpgsqlTypes.NpgsqlDbType.Text;

    // Every row of a table from PostgreSQL into the file (its columns both have).
    private static async Task<long> CopyToFile(NpgsqlConnection pg, NpgsqlTransaction read, DbConnection file, DbTransaction tx, string table)
    {
        var pgColumns = await PgColumns(pg, read, table);
        if (pgColumns.Count == 0) return 0;
        var fileColumns = (await FileColumns(file, tx, table)).ToDictionary(c => c.Name, c => c.Type, StringComparer.OrdinalIgnoreCase);
        var columns = pgColumns.Where(c => fileColumns.ContainsKey(c.Name)).ToList();
        var subjectAt = columns.FindIndex(c => c.Name == "subject_type");
        var names = string.Join(", ", columns.Select(c => $"\"{c.Name}\""));
        var marks = string.Join(", ", columns.Select((_, i) => $"@v{i}"));
        long count = 0;
        await using var select = new NpgsqlCommand($"SELECT {string.Join(", ", columns.Select(PgRead))} FROM \"{table}\"", pg, read);
        await using var r = await select.ExecuteReaderAsync();
        await using var insert = Db.Command(file, $"INSERT INTO \"{table}\" ({names}) VALUES ({marks})", tx);
        var parameters = columns.Select((_, i) =>
        {
            var p = insert.CreateParameter();
            p.ParameterName = $"v{i}";
            insert.Parameters.Add(p);
            return p;
        }).ToArray();
        while (await r.ReadAsync())
        {
            var subject = subjectAt >= 0 && !r.IsDBNull(subjectAt) ? r.GetString(subjectAt) : null;
            for (var i = 0; i < columns.Count; i++)
            {
                // Each row's own type (SQLite would otherwise keep the first row's).
                parameters[i].ResetDbType();
                parameters[i].Value = ForFile(r.IsDBNull(i) ? null : r.GetValue(i), fileColumns[columns[i].Name], subject) ?? DBNull.Value;
            }
            await insert.ExecuteNonQueryAsync();
            count++;
        }
        return count;
    }

    // Each table on both sides, row by row: every value as the file keeps it (times to the millisecond, numbers by their
    // value), in any order. Anything that differs, and nothing is kept.
    private static async Task Compare(DbConnection file, DbTransaction? fileTx, NpgsqlConnection pg, NpgsqlTransaction pgTx, List<(string Table, long Rows)> moved)
    {
        foreach (var (table, rows) in moved)
        {
            var pgColumns = await PgColumns(pg, pgTx, table);
            var fileColumns = (await FileColumns(file, fileTx, table)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var columns = pgColumns.Where(c => fileColumns.Contains(c.Name)).ToList();
            var inFile = await Rows(Db.Command(file, $"SELECT {string.Join(", ", columns.Select(c => $"\"{c.Name}\""))} FROM \"{table}\"", fileTx));
            var inPg = await Rows(new NpgsqlCommand($"SELECT {string.Join(", ", columns.Select(PgRead))} FROM \"{table}\"", pg, pgTx));
            if (inFile.Count != rows || inPg.Count != rows)
                throw new RefusedException($"{table} didn't copy whole ({inFile.Count:N0} rows in the file, {inPg.Count:N0} in the shared database), so nothing was moved.");
            if (!inFile.SequenceEqual(inPg))
            {
                var first = inFile.Except(inPg).FirstOrDefault() ?? inPg.Except(inFile).FirstOrDefault();
                throw new RefusedException($"{table} didn't copy the same (for example the row {first}), so nothing was moved.");
            }
        }
    }

    // Every row of a query as text, sorted.
    private static async Task<List<string>> Rows(DbCommand cmd)
    {
        var rows = new List<string>();
        await using (cmd)
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var values = new string[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++) values[i] = Same(r.IsDBNull(i) ? null : r.GetValue(i));
                rows.Add(string.Join(" | ", values));
            }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    // A value as the comparison sees it: numbers by their value to three decimals (a time's milliseconds), bytes as base64.
    private static string Same(object? v)
    {
        switch (v)
        {
            case null:
                return "(none)";
            case byte[] bytes:
                return Convert.ToBase64String(bytes);
            case double or float or decimal or long or int or short:
                var d = Convert.ToDecimal(v, CultureInfo.InvariantCulture);
                return Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture);
            case string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !s.StartsWith("0") || s == "0":
                return Same(decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture));
            default:
                return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        }
    }

    // ---- Which way this PC goes (every game on it) ----

    /// <summary>
    /// Where this PC's fleet lives, for every game on it: a small file beside the fleet file (store.json), written when a
    /// game chooses or moves, read by the others so they follow at their next sync.
    /// </summary>
    public sealed record PcChoice(FleetStoreChoice Store, bool UseRemote, string ChangedBy, DateTime ChangedAt);

    public static string ChoicePath(string fleetFile) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fleetFile))!, "store.json");

    public static PcChoice? ReadChoice(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var store = root.GetProperty("store").GetString() switch
            {
                "thisPc" => FleetStoreChoice.ThisPc,
                "shared" => FleetStoreChoice.SharedDatabase,
                _ => FleetStoreChoice.NotChosen,
            };
            if (store == FleetStoreChoice.NotChosen) return null;
            return new PcChoice(store, root.TryGetProperty("useRemote", out var r) && r.GetBoolean(),
                root.TryGetProperty("changedBy", out var by) ? by.GetString() ?? "" : "",
                root.TryGetProperty("changedAt", out var at) && at.TryGetDateTime(out var when) ? when : DateTime.MinValue);
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void WriteChoice(string path, PcChoice choice)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["store"] = choice.Store == FleetStoreChoice.ThisPc ? "thisPc" : "shared",
            ["useRemote"] = choice.UseRemote,
            ["changedBy"] = choice.ChangedBy,
            ["changedAt"] = choice.ChangedAt.ToUniversalTime().ToString("o"),
        }, new JsonSerializerOptions { WriteIndented = true });
        var temp = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, json, Encoding.UTF8);
        File.Move(temp, path, true);
    }
}
