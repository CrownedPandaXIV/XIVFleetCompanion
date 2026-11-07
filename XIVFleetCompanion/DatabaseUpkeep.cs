using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Npgsql;

namespace XIVFleetCompanion;

/// <summary>
/// Keeps the database's tables and views up to date, exactly as the app does (its db/apply-updates.js), so a player who
/// only runs the plugin never needs the app open after an update. It carries a copy of the app's own files (Database/,
/// copied there by the app's tests/db/plugin-database-fixture.js, which also writes what the database must look like
/// after each way of updating it, for this project's tests):
///  - on an empty database, creates every table from schema.sql;
///  - on a database made before the app kept a version record, adds what catch-up.sql adds, recorded as version 9;
///  - then applies each numbered update newer than the recorded version, in order, each in its own transaction together
///    with its row in companion_schema_version;
///  - then replaces the views (views.sql) when that file changed, unless a newer app's views are there.
/// The app and the plugin take the same advisory lock, so only one of them updates at a time. Where the app waits its
/// turn, the plugin doesn't: if another copy is updating, it leaves it to that one and looks again later.
/// </summary>
public static class DatabaseUpkeep
{
    // The same as the app's (db/apply-updates.js LOCK_KEY and BASELINE).
    public const long LockKey = 73104;
    public const int Baseline = 9;

    public sealed record Update(int Version, string Name, string Sql);

    /// <summary>The app's files: AppVersion is the app version they came from, recorded with the views.</summary>
    public sealed record Files(string Schema, string CatchUp, string? Views, IReadOnlyList<Update> Updates, string AppVersion)
    {
        public int Latest => Updates.Count == 0 ? Baseline : Math.Max(Baseline, Updates.Max(u => u.Version));

        /// <summary>Only the updates up to `last` and no views, as an older copy had them (for the tests).</summary>
        public Files UpTo(int last) => this with { Updates = Updates.Where(u => u.Version <= last).ToList(), Views = null };

        private static Files? embedded;

        /// <summary>The copy built into the plugin (Database/ in this project).</summary>
        public static Files Embedded() => embedded ??= Load(typeof(DatabaseUpkeep).Assembly);

        private static Files Load(Assembly assembly)
        {
            string Read(string name)
            {
                using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is missing from the plugin.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            var updates = new List<Update>();
            foreach (var name in assembly.GetManifestResourceNames())
            {
                var m = Regex.Match(name, @"^Database/updates/(\d+)_(.+)\.sql$");
                if (!m.Success) continue;
                var version = int.Parse(m.Groups[1].Value);
                if (version <= Baseline) throw new InvalidOperationException($"{name}: numbers up to {Baseline} are taken.");
                if (updates.Any(u => u.Version == version)) throw new InvalidOperationException($"Two database updates are number {version}.");
                updates.Add(new Update(version, m.Groups[2].Value, Read(name)));
            }
            return new Files(Read("Database/schema.sql"), Read("Database/catch-up.sql"), Read("Database/views.sql"),
                updates.OrderBy(u => u.Version).ToList(), Read("Database/app-version.txt").Trim());
        }
    }

    /// <summary>
    /// What happened. Busy: another copy (the app or another game) was updating, so nothing was looked at. Tracked: false
    /// when the login can't make the version table (only fine while nothing needs changing). Newer: a newer app or plugin
    /// already updated it further, so it's left alone.
    /// </summary>
    public sealed record Result(int? Version, int Latest, bool Created, IReadOnlyList<string> Applied, bool Tracked, bool Newer, bool Busy)
    {
        public string Text => Busy ? "Another copy of the app or plugin was updating the database; looking again soon."
            : Newer ? $"A newer app or plugin already updated the database (version {Version}). Update this plugin so everything shows correctly."
            : Applied.Count > 0 ? $"Brought the database up to date (version {Version}): {string.Join(", ", Applied)}."
            : $"The database is up to date (version {Version}).";
    }

    /// <summary>
    /// Brings the database up to date. recordedBy goes with each version record (which app or plugin applied it).
    /// wait: take turns like the app (for the tests); otherwise leave it to whichever copy is updating. Throws with a plain
    /// message when it can't.
    /// </summary>
    public static async Task<Result> EnsureAsync(NpgsqlConnection conn, Files files, string recordedBy, bool wait = false)
    {
        var latest = files.Latest;
        var applied = new List<string>();
        if (wait) await Exec(conn, $"SELECT pg_advisory_lock({LockKey})");
        else if (!await Scalar<bool>(conn, $"SELECT pg_try_advisory_lock({LockKey})"))
            return new Result(null, latest, false, applied, true, false, true);
        try
        {
            int? version = null;
            var created = false;
            var tracked = await Scalar<bool>(conn, "SELECT to_regclass('companion_schema_version') IS NOT NULL");
            if (tracked) version = await Scalar<int?>(conn, "SELECT max(version) FROM companion_schema_version");

            if (version == null)
            {
                switch (await Layout(conn, SchemaTables(files.Schema)))
                {
                    case "empty":
                        await RunScript(conn, files.Schema);
                        await Record(conn, latest, "new database", recordedBy);
                        created = true;
                        applied.Add("created every table");
                        version = latest;
                        tracked = true;
                        break;
                    case "current":
                        var notices = new List<string>();
                        void OnNotice(object sender, NpgsqlNoticeEventArgs e)
                        {
                            if (e.Notice.MessageText.StartsWith("added ")) notices.Add(e.Notice.MessageText);
                        }
                        conn.Notice += OnNotice;
                        try
                        {
                            await Exec(conn, files.CatchUp);
                        }
                        finally
                        {
                            conn.Notice -= OnNotice;
                        }
                        applied.AddRange(notices);
                        if (!tracked)
                        {
                            try
                            {
                                await Exec(conn, CreateVersionTable);
                                tracked = true;
                            }
                            catch (PostgresException e) when (e.SqlState == "42501")
                            {
                            }
                        }
                        if (tracked) await Record(conn, Baseline, "catch-up", recordedBy);
                        version = Baseline;
                        break;
                    case "old":
                        throw new InvalidOperationException("This database is from before plugin 0.4.0 (it still has the old companion_character_snapshot table), and it can't be brought up to date. Make a new, empty database and connect the app and the plugin to it.");
                    default:
                        throw new InvalidOperationException("This database has some of the fleet's tables but not companion_character_current, so the plugin will not change it. Connect to an empty database or to the one the plugin writes to.");
                }
            }

            var pending = files.Updates.Where(u => u.Version > version).ToList();
            if (pending.Count > 0 && !tracked) throw NoRights(conn);
            foreach (var u in pending)
            {
                await using (var tx = await conn.BeginTransactionAsync())
                {
                    try
                    {
                        await using (var cmd = new NpgsqlCommand(u.Sql, conn, tx)) await cmd.ExecuteNonQueryAsync();
                        await Record(conn, u.Version, u.Name, recordedBy, tx);
                        await tx.CommitAsync();
                    }
                    catch (PostgresException e) when (e.SqlState == "42501")
                    {
                        throw NoRights(conn, e);
                    }
                    catch (Exception e)
                    {
                        throw new InvalidOperationException($"Database update {u.Version} ({u.Name}) failed, so nothing of it was applied: {e.Message}", e);
                    }
                }
                applied.Add($"update {u.Version}: {u.Name}");
                version = u.Version;
            }

            var newer = version > latest;
            // A newer copy's database may expect its own views; leave them to it.
            if (!newer && await EnsureViews(conn, files, tracked)) applied.Add("views updated");
            return new Result(version, latest, created, applied, tracked, newer, false);
        }
        catch (PostgresException e) when (e.SqlState == "42501")
        {
            throw NoRights(conn, e);
        }
        finally
        {
            try
            {
                await Exec(conn, $"SELECT pg_advisory_unlock({LockKey})");
            }
            catch (Exception)
            {
                // the lock goes with the connection anyway
            }
        }
    }

    // The views are applied again whenever views.sql changes: its checksum is recorded in companion_views (database update
    // 15) with the app version it came from. Replaced in one transaction when the checksum differs, unless a newer app's
    // views are there (two versions on two PCs would otherwise keep swapping them).
    private static async Task<bool> EnsureViews(NpgsqlConnection conn, Files files, bool tracked)
    {
        if (files.Views == null) return false;
        var checksum = Checksum(files.Views);
        if (!await Scalar<bool>(conn, "SELECT to_regclass('companion_views') IS NOT NULL")) return false; // before update 15
        string? savedChecksum = null, savedVersion = null;
        await using (var cmd = new NpgsqlCommand("SELECT checksum, app_version FROM companion_views WHERE id = 1", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            if (await r.ReadAsync())
            {
                savedChecksum = r.GetString(0);
                savedVersion = r.IsDBNull(1) ? null : r.GetString(1);
            }
        }
        if (savedChecksum == checksum) return false;
        if (savedVersion != null && CompareVersions(savedVersion, files.AppVersion) > 0) return false;
        // A login that may only read keeps working with the views it has.
        if (!tracked) return false;
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await using (var cmd = new NpgsqlCommand(files.Views, conn, tx)) await cmd.ExecuteNonQueryAsync();
            await using (var cmd = new NpgsqlCommand(
                @"INSERT INTO companion_views (id, checksum, app_version, applied_at) VALUES (1, @c, @v, now())
                  ON CONFLICT (id) DO UPDATE SET checksum = EXCLUDED.checksum, app_version = EXCLUDED.app_version, applied_at = now()", conn, tx))
            {
                cmd.Parameters.AddWithValue("c", checksum);
                cmd.Parameters.AddWithValue("v", files.AppVersion);
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch (PostgresException e) when (e.SqlState == "42501")
        {
            return false;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"The fleet views could not be updated, so nothing of them was changed: {e.Message}", e);
        }
        return true;
    }

    /// <summary>The views file's checksum, as the app works it out (sha256 of the text with Unix line endings).</summary>
    public static string Checksum(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n")))).ToLowerInvariant();

    /// <summary>Compares "1.9.0"-style versions as the app does (shared/changelog.js): -1, 0 or 1; "-test.3" is ignored.</summary>
    public static int CompareVersions(string a, string b)
    {
        static double[] Parts(string v) => (v ?? "0").Split('-')[0].Split('.').Select(p => int.TryParse(p, out var n) ? n : double.NaN).ToArray();
        var pa = Parts(a);
        var pb = Parts(b);
        for (var i = 0; i < 3; i++)
        {
            var d = (i < pa.Length ? pa[i] : 0) - (i < pb.Length ? pb[i] : 0);
            if (double.IsNaN(d) || d == 0) continue;
            return d < 0 ? -1 : 1;
        }
        return 0;
    }

    // The tables schema.sql creates, to tell an empty database from one with someone else's tables.
    private static string[] SchemaTables(string schema) =>
        Regex.Matches(schema, @"^CREATE TABLE (\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();

    private static async Task<string> Layout(NpgsqlConnection conn, string[] tables)
    {
        await using var cmd = new NpgsqlCommand(
            @"SELECT to_regclass('companion_character_current') IS NOT NULL AS current,
                     to_regclass('companion_character_snapshot') IS NOT NULL AS snapshot,
                     EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = current_schema() AND tablename = ANY(@t)) AS known", conn);
        cmd.Parameters.AddWithValue("t", tables);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        if (r.GetBoolean(1)) return "old";
        if (r.GetBoolean(0)) return "current";
        return r.GetBoolean(2) ? "partial" : "empty";
    }

    private const string CreateVersionTable = @"CREATE TABLE companion_schema_version (
    version     int         PRIMARY KEY,
    name        text        NOT NULL,
    applied_at  timestamptz NOT NULL DEFAULT now(),
    app_version text
)";

    private static async Task Record(NpgsqlConnection conn, int version, string name, string recordedBy, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO companion_schema_version (version, name, app_version) VALUES (@v, @n, @a) ON CONFLICT (version) DO NOTHING", conn, tx);
        cmd.Parameters.AddWithValue("v", version);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("a", recordedBy);
        await cmd.ExecuteNonQueryAsync();
    }

    // A script of several statements; if it fails part way, ends its transaction before passing the error on.
    private static async Task RunScript(NpgsqlConnection conn, string sql)
    {
        try
        {
            await Exec(conn, sql);
        }
        catch
        {
            try
            {
                await Exec(conn, "ROLLBACK");
            }
            catch (Exception)
            {
                // no transaction was open
            }
            throw;
        }
    }

    private static Exception NoRights(NpgsqlConnection conn, Exception? cause = null)
    {
        var user = new NpgsqlConnectionStringBuilder(conn.ConnectionString).Username;
        return new InvalidOperationException(
            $"The database login{(string.IsNullOrEmpty(user) ? "" : $" \"{user}\"")} is not allowed to change the tables, so the plugin cannot update them. " +
            "Connect with the login that owns the tables (the one the app uses), or open the app once to update them.", cause);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }
}

/// <summary>
/// When this plugin looks at the database's version (DatabaseUpkeep): on the first read or sync, then every 10 minutes
/// (a minute after another copy was busy updating, 5 after a problem). Never stops a read or a sync: a problem is kept
/// for the Status page and said once in the log.
/// </summary>
public sealed class DatabaseKeeper
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AfterBusy = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan AfterProblem = TimeSpan.FromMinutes(5);

    private readonly Func<DatabaseUpkeep.Files> files;
    private readonly string recordedBy;
    private readonly Action<string>? log;
    private DateTime nextAt = DateTime.MinValue;
    private int running;
    private string? lastLogged;

    public DatabaseUpkeep.Result? LastResult { get; private set; }
    public string? Problem { get; private set; }
    public DateTime? LastAt { get; private set; }

    /// <param name="recordedBy">Recorded with each version this plugin applies, e.g. "plugin 2.1.0".</param>
    /// <param name="log">Where to say what changed, and a problem (once until it changes).</param>
    public DatabaseKeeper(Func<DatabaseUpkeep.Files> files, string recordedBy, Action<string>? log = null)
    {
        this.files = files;
        this.recordedBy = recordedBy;
        this.log = log;
    }

    /// <summary>For the Status page: what the last look found.</summary>
    public string StatusText => Problem != null ? $"Couldn't update the database: {Problem}"
        : LastResult?.Text ?? "Not looked at yet.";

    public async Task RunIfDueAsync(NpgsqlConnection conn)
    {
        if (DateTime.UtcNow < nextAt) return;
        if (System.Threading.Interlocked.Exchange(ref running, 1) == 1) return;
        try
        {
            var files = this.files();
            var result = await DatabaseUpkeep.EnsureAsync(conn, files, $"{files.AppVersion} ({recordedBy})");
            LastAt = DateTime.UtcNow;
            if (!result.Busy)
            {
                LastResult = result;
                Problem = null;
            }
            nextAt = DateTime.UtcNow + (result.Busy ? AfterBusy : Every);
            if (result.Applied.Count > 0 || (result.Newer && lastLogged != result.Text)) Say(result.Text);
            else if (!result.Busy) lastLogged = null;
        }
        catch (Exception ex)
        {
            LastAt = DateTime.UtcNow;
            Problem = ex.Message;
            nextAt = DateTime.UtcNow + AfterProblem;
            if (lastLogged != Problem) Say($"Couldn't update the database (trying again in a few minutes): {Problem}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref running, 0);
        }
    }

    private void Say(string text)
    {
        lastLogged = text;
        log?.Invoke(text);
    }
}
