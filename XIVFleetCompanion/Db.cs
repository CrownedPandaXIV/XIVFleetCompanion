using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Npgsql;

namespace XIVFleetCompanion;

/// <summary>
/// The little that differs between the two places the fleet can live: PostgreSQL (Shared between PCs) and the one-file
/// SQLite database (Just this PC, the app's db/sqlite/tables.sql). In the file, times are Unix seconds (REAL), yes/no is
/// 0 or 1, character and retainer ids are whole numbers and Free Company ids are text (the game's go past what a whole
/// number holds). The PostgreSQL code stays as it was; each writer and reader has a file version beside it, and the
/// tests check both give the same answers.
/// </summary>
public static class Db
{
    /// <summary>True for the one-file database (anything that isn't PostgreSQL).</summary>
    public static bool IsFile(DbConnection conn) => conn is not NpgsqlConnection;

    /// <summary>Now, as the file keeps times: Unix seconds.</summary>
    public static double Now() => Unix(DateTime.UtcNow);

    public static double Unix(DateTime t)
    {
        var utc = t.Kind switch
        {
            DateTimeKind.Local => t.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(t, DateTimeKind.Utc),
            _ => t,
        };
        return (utc - DateTime.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;
    }

    public static double? Unix(DateTime? t) => t == null ? null : Unix(t.Value);

    /// <summary>A time read from either database (a timestamp from PostgreSQL, Unix seconds from the file), in UTC.</summary>
    public static DateTime Time(DbDataReader r, int i)
    {
        var v = r.GetValue(i);
        return v switch
        {
            DateTime d => d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime(),
            DateTimeOffset o => o.UtcDateTime,
            _ => DateTime.UnixEpoch.AddTicks((long)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond)),
        };
    }

    public static DateTime? TimeOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : Time(r, i);

    /// <summary>Now in the file's terms (Unix seconds), as SQL.</summary>
    public const string FileNow = "((julianday('now') - 2440587.5) * 86400.0)";

    /// <summary>A statement written for PostgreSQL that only differs in now(): the file's version.</summary>
    public static string FileSql(string sql) => sql.Replace("now()", FileNow);

    /// <summary>A Free Company id as the file keeps it (text); 0 (none) as null.</summary>
    public static string? FcText(ulong fcId) => fcId == 0 ? null : fcId.ToString(CultureInfo.InvariantCulture);

    /// <summary>A whole-number id read from either database (numeric in PostgreSQL; INTEGER or TEXT in the file).</summary>
    public static ulong Id(DbDataReader r, int i) => r.GetValue(i) switch
    {
        decimal d => (ulong)d,
        long l => unchecked((ulong)l),
        int n => (ulong)n,
        string s => ulong.Parse(s, CultureInfo.InvariantCulture),
        double x => (ulong)x,
        var v => Convert.ToUInt64(v, CultureInfo.InvariantCulture),
    };

    /// <summary>A command, in the order NpgsqlCommand takes them (the readers were written for it).</summary>
    public static DbCommand Cmd(string sql, DbConnection conn, DbTransaction? tx = null) => Command(conn, sql, tx);

    public static DbCommand Command(DbConnection conn, string sql, DbTransaction? tx = null)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (tx != null) cmd.Transaction = tx;
        return cmd;
    }

    /// <summary>Adds a parameter; null becomes NULL, ulong a signed whole number, bool 0 or 1 (the file has no booleans).</summary>
    public static void Add(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value switch
        {
            null => DBNull.Value,
            ulong u => unchecked((long)u),
            uint u => (long)u,
            bool b when IsFileCommand(cmd) => b ? 1L : 0L,
            // The file keeps whole numbers as INTEGER (System.Data.SQLite would store a decimal as text).
            decimal d when IsFileCommand(cmd) => d == decimal.Truncate(d) && d >= long.MinValue && d <= long.MaxValue ? (long)d : (double)d,
            DateTime t when IsFileCommand(cmd) => Unix(t),
            _ => value,
        };
        cmd.Parameters.Add(p);
    }

    private static bool IsFileCommand(DbCommand cmd) => cmd is not NpgsqlCommand;

    /// <summary>
    /// "(@name0, @name1, ...)" with each value added, for "x IN (...)" (the file has no arrays); "(NULL)" when there are
    /// none, which matches nothing.
    /// </summary>
    public static string In<T>(DbCommand cmd, string name, IEnumerable<T> values)
    {
        var names = new List<string>();
        foreach (var v in values)
        {
            var n = $"{name}{names.Count}";
            Add(cmd, n, v);
            names.Add("@" + n);
        }
        return names.Count == 0 ? "(NULL)" : "(" + string.Join(", ", names) + ")";
    }

    public static async System.Threading.Tasks.Task<int> Exec(DbConnection conn, string sql, DbTransaction? tx = null, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(conn, sql, tx);
        foreach (var (n, v) in parameters) Add(cmd, n, v);
        return await cmd.ExecuteNonQueryAsync();
    }
}
