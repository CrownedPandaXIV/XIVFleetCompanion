using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XIVFleetCompanion;

/// <summary>Where the fleet lives: a PostgreSQL database shared between PCs (with the app), or one file on this PC.</summary>
public enum FleetStoreChoice
{
    NotChosen = 0,
    SharedDatabase = 1,
    ThisPc = 2,
}

/// <summary>
/// "Just this PC": the fleet kept in one file (SQLite) instead of a PostgreSQL database. Every game on the PC shares the
/// same file (%APPDATA%\XIV Fleet Companion\fleet.db), so several can sync at once; the file waits its turn rather than
/// fail. The plugin keeps it up to date itself with the app's file versions of the tables and views (Database/sqlite/,
/// copied from the app), and copies it to Backups\ once a day while a game runs, keeping the last 7.
/// A file in a cloud or network folder is refused: syncing services copy it half-written and damage it.
/// </summary>
public static class FileDatabase
{
    public const string FolderName = "XIV Fleet Companion";
    public const string FileName = "fleet.db";
    public const string BackupFolderName = "Backups";
    public const int BackupsKept = 7;

    /// <summary>Where every game on this PC keeps the fleet: %APPDATA%\XIV Fleet Companion\fleet.db.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName, FileName);

    public static string BackupFolder(string path) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, BackupFolderName);

    /// <summary>Opening refused, with why in plain words (a cloud or network folder).</summary>
    public sealed class RefusedException : Exception
    {
        public RefusedException(string message) : base(message) { }
    }

    private static readonly (string Folder, string Service)[] CloudFolders =
    {
        ("OneDrive", "OneDrive"), ("Dropbox", "Dropbox"), ("Google Drive", "Google Drive"), ("GoogleDrive", "Google Drive"),
        ("My Drive", "Google Drive"), ("iCloudDrive", "iCloud"), ("iCloud Drive", "iCloud"), ("Box Sync", "Box"),
        ("pCloudDrive", "pCloud"),
    };

    /// <summary>
    /// Why the file can't be kept at path (a cloud-synced or network folder), or null when it can. cloudRoots: the folders
    /// cloud services say they sync (OneDrive sets these; none: this PC's).
    /// </summary>
    public static string? WhyNotHere(string path, IEnumerable<string?>? cloudRoots = null)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\") || full.StartsWith("//"))
            return "The fleet file can't be kept in a network folder: two PCs writing to it over the network can damage it. Use a folder on this PC.";
        foreach (var root in cloudRoots ?? CloudRoots())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var r = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
                return CloudText("OneDrive");
        }
        var folders = full.Split('\\', '/');
        foreach (var part in folders.Take(folders.Length - 1))
            foreach (var (folder, service) in CloudFolders)
                if (part.Equals(folder, StringComparison.OrdinalIgnoreCase) || part.StartsWith(folder + " - ", StringComparison.OrdinalIgnoreCase))
                    return CloudText(service);
        return null;
    }

    private static string CloudText(string service) =>
        $"The fleet file can't be kept in a {service} folder: {service} copies it while the game is writing to it, which can damage it. " +
        "Windows may be keeping your AppData folder in the cloud; ask for help on the plugin's page if you're not sure how to change that.";

    private static IEnumerable<string?> CloudRoots() => new[]
    {
        Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"),
        Environment.GetEnvironmentVariable("OneDriveCommercial"),
    };

    /// <summary>
    /// The file, open and ready (made, with its folder, when it isn't there yet). Waits up to 15 seconds for another game
    /// that is writing, rather than fail. Throws RefusedException for a cloud or network folder.
    /// </summary>
    public static async Task<SQLiteConnection> OpenAsync(string path, IEnumerable<string?>? cloudRoots = null)
    {
        var why = WhyNotHere(path, cloudRoots);
        if (why != null) throw new RefusedException(why);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var builder = new SQLiteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 };
        var conn = new SQLiteConnection(builder.ToString());
        try
        {
            await conn.OpenAsync();
            // Readers don't wait for writers and the reverse (write-ahead log); a write waits for another's to finish.
            await Db.Exec(conn, "PRAGMA busy_timeout = 15000");
            await Db.Exec(conn, "PRAGMA journal_mode = WAL");
            await Db.Exec(conn, "PRAGMA synchronous = NORMAL");
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
        return conn;
    }

    /// <summary>The app's file versions of the tables and views, and the app version they came from.</summary>
    public sealed record Files(string Tables, string Views, string AppVersion)
    {
        private static Files? embedded;

        /// <summary>The copy built into the plugin (Database/sqlite/ in this project).</summary>
        public static Files Embedded() => embedded ??= Load(typeof(FileDatabase).Assembly);

        private static Files Load(Assembly assembly)
        {
            string Read(string name)
            {
                using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is missing from the plugin.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            return new Files(Read("Database/sqlite/tables.sql"), Read("Database/sqlite/views.sql"), Read("Database/app-version.txt").Trim());
        }
    }

    /// <summary>What a look at the file did. Newer: a newer plugin (in another game) already updated it, so it's left alone.</summary>
    public sealed record Result(bool Created, IReadOnlyList<string> Applied, bool Newer, string AppVersion)
    {
        public string Text => Newer ? $"A newer plugin in another game already updated the fleet file (to {AppVersion}). Update this plugin so everything shows correctly."
            : Created ? "Made the fleet file."
            : Applied.Count > 0 ? $"Brought the fleet file up to date: {string.Join(", ", Applied)}."
            : "The fleet file is up to date.";
    }

    private const string InfoTable = "CREATE TABLE IF NOT EXISTS companion_file_info (key TEXT PRIMARY KEY, value TEXT)";

    /// <summary>
    /// Brings the file's tables and views up to date: on a new file, makes every table; on an older one, adds the tables,
    /// columns and indexes it's missing (nothing is ever removed, so an older plugin in another game keeps working); then
    /// replaces the views when they changed. All in one transaction, which other games wait for. Throws with a plain
    /// message when it can't, leaving the file as it was.
    /// </summary>
    public static async Task<Result> EnsureAsync(DbConnection conn, Files files, string recordedBy)
    {
        var applied = new List<string>();
        await using var tx = await conn.BeginTransactionAsync();
        await Db.Exec(conn, InfoTable, tx);
        var info = await ReadInfo(conn, tx);
        if (info.TryGetValue("app_version", out var saved) && saved != null && DatabaseUpkeep.CompareVersions(saved, files.AppVersion) > 0)
        {
            await tx.RollbackAsync();
            return new Result(false, applied, true, saved);
        }
        var have = await Tables(conn, tx);
        var created = !have.Keys.Any(t => t.StartsWith("companion_") && t != "companion_file_info");
        try
        {
            if (created)
                await Db.Exec(conn, files.Tables, tx);
            else
                applied.AddRange(await AddMissing(conn, tx, files.Tables, have));

            var checksum = DatabaseUpkeep.Checksum(files.Views);
            if (created || !info.TryGetValue("views_checksum", out var savedViews) || savedViews != checksum)
            {
                // Every view goes, so one an update renamed or left out doesn't linger.
                foreach (var view in await Names(conn, tx, "view"))
                    await Db.Exec(conn, $"DROP VIEW IF EXISTS \"{view}\"", tx);
                await Db.Exec(conn, files.Views, tx);
                if (!created) applied.Add("the fleet views");
            }
            if (created || applied.Count > 0 || saved != files.AppVersion)
                foreach (var (key, value) in new[] { ("app_version", files.AppVersion), ("views_checksum", checksum), ("updated_by", recordedBy),
                             ("updated_at", DateTime.UtcNow.ToString("o")) })
                    await Db.Exec(conn, "INSERT INTO companion_file_info (key, value) VALUES (@k, @v) ON CONFLICT (key) DO UPDATE SET value = excluded.value", tx,
                        ("k", key), ("v", value));
            await tx.CommitAsync();
        }
        catch (Exception e)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException($"The fleet file could not be updated, so nothing in it was changed: {e.Message}", e);
        }
        return new Result(created, applied, false, files.AppVersion);
    }

    private static async Task<Dictionary<string, string?>> ReadInfo(DbConnection conn, DbTransaction tx)
    {
        var info = new Dictionary<string, string?>();
        await using var cmd = Db.Command(conn, "SELECT key, value FROM companion_file_info", tx);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) info[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        return info;
    }

    // name → the SQL that made it, for the tables (or indexes, views) in a database.
    private static async Task<Dictionary<string, string>> Tables(DbConnection conn, DbTransaction? tx, string type = "table")
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = Db.Command(conn, "SELECT name, sql FROM sqlite_master WHERE type = @t AND name NOT LIKE 'sqlite_%'", tx);
        Db.Add(cmd, "t", type);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) found[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
        return found;
    }

    private static async Task<List<string>> Names(DbConnection conn, DbTransaction? tx, string type) => (await Tables(conn, tx, type)).Keys.ToList();

    private static async Task<List<(string Name, string Type, bool NotNull, string? Default, bool Key)>> Columns(DbConnection conn, DbTransaction? tx, string table)
    {
        var columns = new List<(string, string, bool, string?, bool)>();
        await using var cmd = Db.Command(conn, $"PRAGMA table_info(\"{table}\")", tx);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            columns.Add((r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), Convert.ToInt64(r.GetValue(3)) != 0,
                r.IsDBNull(4) ? null : r.GetString(4), Convert.ToInt64(r.GetValue(5)) != 0));
        return columns;
    }

    // What the tables file makes, worked out in a scratch database in memory, compared with the file: the tables, columns
    // and indexes it doesn't have yet are added.
    private static async Task<List<string>> AddMissing(DbConnection conn, DbTransaction tx, string tablesSql, Dictionary<string, string> have)
    {
        var added = new List<string>();
        await using var scratch = new SQLiteConnection("Data Source=:memory:");
        await scratch.OpenAsync();
        await Db.Exec(scratch, tablesSql);
        foreach (var (table, sql) in await Tables(scratch, null))
        {
            if (!have.ContainsKey(table))
            {
                await Db.Exec(conn, sql, tx);
                added.Add($"added {table}");
                continue;
            }
            var present = (await Columns(conn, tx, table)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in await Columns(scratch, null, table))
            {
                if (present.Contains(c.Name)) continue;
                if (c.Key) throw new InvalidOperationException($"{table} needs a new key column ({c.Name}), which can't be added to a file in use.");
                // SQLite only adds a column whose default is a plain value; one that can't be blank needs one.
                var dflt = c.Default != null && !c.Default.StartsWith("(") ? $" DEFAULT {c.Default}" : "";
                var notNull = c.NotNull && dflt.Length > 0 ? " NOT NULL" : "";
                await Db.Exec(conn, $"ALTER TABLE \"{table}\" ADD COLUMN \"{c.Name}\" {c.Type}{notNull}{dflt}", tx);
                added.Add($"added {table}.{c.Name}");
            }
        }
        var indexes = await Tables(conn, tx, "index");
        foreach (var (index, sql) in await Tables(scratch, null, "index"))
            if (!indexes.ContainsKey(index) && sql.Length > 0)
            {
                await Db.Exec(conn, sql, tx);
                added.Add($"added {index}");
            }
        return added;
    }

    /// <summary>The backups kept, newest first.</summary>
    public static List<FileInfo> Backups(string path)
    {
        var folder = new DirectoryInfo(BackupFolder(path));
        return folder.Exists
            ? folder.GetFiles("fleet-*.db").OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).ToList()
            : new List<FileInfo>();
    }

    /// <summary>
    /// Copies the file into Backups\ (fleet-2026-11-07.db; a second the same day gets the time too) and keeps the newest
    /// 7. A copy taken while games write is whole: SQLite copies one moment of it. Another game making the same backup at
    /// the same moment is fine: one of the two copies is kept.
    /// </summary>
    public static async Task<string> BackUpAsync(DbConnection conn, string path, DateTime now)
    {
        var folder = BackupFolder(path);
        Directory.CreateDirectory(folder);
        var name = Path.Combine(folder, $"fleet-{now:yyyy-MM-dd}.db");
        if (File.Exists(name)) name = Path.Combine(folder, $"fleet-{now:yyyy-MM-dd-HHmmss}.db");
        var temp = Path.Combine(folder, $"making-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            await Db.Exec(conn, "VACUUM INTO @to", null, ("to", temp));
            try
            {
                File.Move(temp, name);
            }
            catch (IOException) when (File.Exists(name))
            {
                // another game made it at the same moment
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        foreach (var old in Backups(path).Skip(BackupsKept))
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
                // in use; next time
            }
        return name;
    }

    /// <summary>True when no backup was made today (local time).</summary>
    public static bool BackupDue(string path, DateTime now) =>
        !Backups(path).Any(f => f.LastWriteTime.Date == now.Date);
}
