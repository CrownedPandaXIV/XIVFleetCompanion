using Npgsql;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Setup
{
    /// <summary>A step of a long job as the window lists it: waiting, working, done or failed, with what's happening.</summary>
    public sealed class JobStep
    {
        public JobStep(string id, string label) { Id = id; Label = label; }
        public string Id { get; }
        public string Label { get; }
        public string State { get; set; } = "waiting";
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// The shared database's management from the game, as the app's (setup/create-database.js, leftovers.js,
    /// move-database.js and remove-everything.js): a new database and its login, the copies a move leaves behind, moving
    /// the database to this PC, and removing what was set up. Each checks the same things and refuses in the same words.
    /// No Dalamud here, so it is tested on its own (tests/FleetWriterTests, against scratch PostgreSQL servers).
    /// </summary>
    public static class DatabaseAdmin
    {
        private static readonly Regex Name = new("^[a-z_][a-z0-9_]{0,62}$");
        private static readonly string[] OwnDatabases = { "postgres", "template0", "template1" };

        public static string ConnectionString(string host, int port, string database, string user, string password, int timeout = 15) =>
            new NpgsqlConnectionStringBuilder
            {
                Host = host, Port = port, Database = database, Username = user, Password = password, Timeout = timeout, Pooling = false,
                CommandTimeout = 0,
            }.ConnectionString;

        public static async Task<NpgsqlConnection> Open(string host, int port, string database, string user, string password, int timeout = 15)
        {
            var c = new NpgsqlConnection(ConnectionString(host, port, database, user, password, timeout));
            try
            {
                await c.OpenAsync();
                return c;
            }
            catch
            {
                await c.DisposeAsync();
                throw;
            }
        }

        private static async Task<object?> Scalar(NpgsqlConnection c, string sql, params object[] values)
        {
            await using var cmd = new NpgsqlCommand(sql, c);
            foreach (var v in values) cmd.Parameters.Add(new NpgsqlParameter { Value = v });
            return await cmd.ExecuteScalarAsync();
        }

        private static async Task Exec(NpgsqlConnection c, string sql, params object[] values)
        {
            await using var cmd = new NpgsqlCommand(sql, c);
            foreach (var v in values) cmd.Parameters.Add(new NpgsqlParameter { Value = v });
            await cmd.ExecuteNonQueryAsync();
        }

        // Names and passwords go in through format(), which quotes them safely; the template is always a fixed string.
        private static async Task Formatted(NpgsqlConnection c, string template, params string[] values)
        {
            var args = string.Join(", ", values.Select((_, i) => $"${i + 1}::text"));
            var sql = (string)(await Scalar(c, $"SELECT format('{template}', {args})", values.Cast<object>().ToArray()))!;
            await Exec(c, sql);
        }

        private static async Task<int> ServerVersion(NpgsqlConnection c) =>
            int.Parse((string)(await Scalar(c, "SHOW server_version_num"))!, CultureInfo.InvariantCulture);

        // ---- A new database (the app's setup/create-database.js) ----

        /// <summary>Why a port typed on a database tool can't be used, or null when it can: a whole number from 1 to 65535,
        /// refused before anything starts (Set up this PC and a move install PostgreSQL on it). As the app's portProblem.</summary>
        public static string? PortProblem(int port) =>
            port is >= 1 and <= 65535 ? null : "The port must be a whole number from 1 to 65535 (PostgreSQL's usual one is 5432).";

        public static string? CheckRequest(string database, string user, string password)
        {
            if (!Name.IsMatch(database ?? "")) return "The database name can only use lower-case letters, numbers and _, and must start with a letter.";
            if (!Name.IsMatch(user ?? "")) return "The username can only use lower-case letters, numbers and _, and must start with a letter.";
            if (OwnDatabases.Contains(database)) return $"\"{database}\" is one of PostgreSQL's own databases. Choose another name.";
            if (user == "postgres") return "Choose a username other than \"postgres\" for the app.";
            if (string.IsNullOrEmpty(password) || password.Length < 8) return "The app's password needs at least 8 characters.";
            return null;
        }

        /// <summary>Makes the database and its own login, as PostgreSQL's administrator: null when made, else why not.</summary>
        public static async Task<string?> CreateDatabase(NpgsqlConnection admin, string database, string user, string password)
        {
            if (CheckRequest(database, user, password) is { } problem) return problem;
            if (await Scalar(admin, "SELECT 1 FROM pg_database WHERE datname = $1", database) != null)
                return $"A database called \"{database}\" already exists. To use it, enter its details above instead.";
            if (await Scalar(admin, "SELECT 1 FROM pg_roles WHERE rolname = $1", user) != null)
                return $"A login called \"{user}\" already exists. Choose another username.";
            if (await Scalar(admin, "SELECT rolsuper OR (rolcreaterole AND rolcreatedb) FROM pg_roles WHERE rolname = current_user") is not true)
                return "That login isn't PostgreSQL's administrator. Use \"postgres\" and the password chosen when PostgreSQL was installed.";
            await Formatted(admin, "CREATE ROLE %I LOGIN PASSWORD %L", user, password);
            try
            {
                await Formatted(admin, "CREATE DATABASE %I OWNER %I", database, user);
            }
            catch (Exception e)
            {
                try { await Formatted(admin, "DROP ROLE IF EXISTS %I", user); } catch { /* left as it was */ }
                return $"The database couldn't be made: {e.Message}";
            }
            return null;
        }

        /// <summary>Drops a database and its login (Remove everything). PostgreSQL's own are never dropped. The note, or
        /// why not.</summary>
        public static async Task<(bool Ok, string? Message)> DropDatabase(NpgsqlConnection admin, string database, string user)
        {
            if (!Name.IsMatch(database ?? "") || OwnDatabases.Contains(database)) return (false, $"\"{database}\" isn't a database the app can remove.");
            if (await Scalar(admin, "SELECT rolsuper FROM pg_roles WHERE rolname = current_user") is not true)
                return (false, "That login isn't PostgreSQL's administrator. Use the password chosen when PostgreSQL was installed.");
            if (await ServerVersion(admin) >= 130000) await Formatted(admin, "DROP DATABASE IF EXISTS %I WITH (FORCE)", database!);
            else
            {
                await Exec(admin, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid()", database!);
                await Formatted(admin, "DROP DATABASE IF EXISTS %I", database!);
            }
            var super = await Scalar(admin, "SELECT rolsuper FROM pg_roles WHERE rolname = $1", user);
            if (super is false && Name.IsMatch(user) && user != "postgres")
            {
                try { await Formatted(admin, "DROP ROLE %I", user); }
                catch (Exception e) { return (true, $"The database is removed, but its login \"{user}\" couldn't be: {e.Message}"); }
            }
            return (true, super is true ? $"The login \"{user}\" is one of PostgreSQL's administrators, so it was left." : null);
        }

        // ---- Leftovers from moving the database (the app's setup/leftovers.js) ----

        private static readonly Regex Kept = new(@"_before_move_\d{8}_\d{4}$");

        public sealed record Leftover(string Name, string Kind, long Bytes, string? MovedTo);

        /// <summary>The copies a move left in this PC's PostgreSQL (kept under another name, or left behind when the
        /// database moved away), and everything else. The database in use is never one.</summary>
        public static async Task<(List<Leftover> Leftovers, List<string> Others)> FindLeftovers(NpgsqlConnection admin, Func<string, Task<NpgsqlConnection>> connectTo, string? current)
        {
            var rows = new List<(string Name, long Bytes)>();
            await using (var cmd = new NpgsqlCommand("SELECT datname, pg_database_size(datname)::bigint FROM pg_database WHERE NOT datistemplate AND datname <> 'postgres' ORDER BY datname", admin))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetInt64(1)));
            var leftovers = new List<Leftover>();
            var others = new List<string>();
            foreach (var (name, bytes) in rows)
            {
                if (name == current) { others.Add(name); continue; }
                if (Kept.IsMatch(name)) { leftovers.Add(new Leftover(name, "kept", bytes, null)); continue; }
                DatabaseMove.Note? note = null;
                try
                {
                    await using var c = await connectTo(name);
                    note = await DatabaseMove.ReadAsync(c);
                }
                catch
                {
                    note = null;
                }
                if (note is { State: "moved" }) leftovers.Add(new Leftover(name, "moved-away", bytes, $"{note.Host}:{note.Port}"));
                else others.Add(name);
            }
            return (leftovers, others);
        }

        /// <summary>Whether PostgreSQL itself can go: only leftovers are in it, no database in use is in it, and it was
        /// installed by the app or the plugin on this port.</summary>
        public static bool PostgresRemovable(List<string> others, WindowsSetup.Installed installed, int port, string? current) =>
            current == null && others.Count == 0 && installed.PostgresPort == port;

        /// <summary>Removes one leftover, after finding them again (only a database that is one right now can go).</summary>
        public static async Task<string?> RemoveLeftover(NpgsqlConnection admin, string name, Func<string, Task<NpgsqlConnection>> connectTo, string? current)
        {
            var (leftovers, _) = await FindLeftovers(admin, connectTo, current);
            if (leftovers.All(l => l.Name != name)) return $"\"{name}\" isn't a leftover from a move, so it wasn't removed.";
            await Exec(admin, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid()", name);
            await Formatted(admin, "DROP DATABASE %I", name);
            return null;
        }

        // ---- Moving the database to this PC (the app's setup/move-database.js) ----

        public const string MovePluginVersion = "0.14.0";
        public const int RecentDays = 14;

        public sealed record PcRow(string PcName, string? AccountLabel, string? PluginVersion, DateTime? LastSync, DateTime? LastRead, string? DbUser);

        /// <summary>The PCs against what a move needs: every PC that synced lately runs a plugin that follows a move and
        /// uses the database's own login.</summary>
        public static (List<string> Blocking, List<string> Notes) CheckPcs(IEnumerable<PcRow> pcs, string user, DateTime now)
        {
            var blocking = new List<string>();
            var notes = new List<string>();
            var since = now.AddDays(-RecentDays);
            foreach (var pc in pcs)
            {
                var name = string.IsNullOrEmpty(pc.AccountLabel) ? pc.PcName : $"{pc.PcName} ({pc.AccountLabel})";
                var last = new[] { pc.LastSync, pc.LastRead }.Where(t => t != null).Select(t => t!.Value).DefaultIfEmpty().Max();
                var seen = pc.LastSync != null || pc.LastRead != null;
                if (!seen || last < since)
                {
                    notes.Add($"{name} hasn't synced since {(seen ? last.ToString("d", CultureInfo.CurrentCulture) : "it was set up")}. It follows the move when it next syncs, as long as its plugin is updated by then and the old database PC is still on.");
                    continue;
                }
                if (DatabaseUpkeep.CompareVersions(pc.PluginVersion ?? "", MovePluginVersion) < 0)
                    blocking.Add($"{name} runs plugin {pc.PluginVersion ?? "(unknown)"}, which can't follow a move. Update it to {MovePluginVersion} or newer (Dalamud does this when the game starts) and let it sync once.");
                else if (string.IsNullOrEmpty(pc.DbUser))
                    blocking.Add($"{name} hasn't synced since its plugin was updated. Let it sync once (a few minutes with the game running).");
                else if (pc.DbUser != user)
                    blocking.Add($"{name}'s plugin logs in as \"{pc.DbUser}\", but the move only brings the database's login \"{user}\". Change {name}'s plugin settings to use \"{user}\" first.");
            }
            return (blocking, notes);
        }

        /// <summary>"pg_dump (PostgreSQL) 18.6" → 18.</summary>
        public static int? ToolMajor(string version) => Regex.Match(Regex.Replace(version ?? "", @"^[^\d]*", ""), @"(\d+)(?:\.\d+)?") is { Success: true } m
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

        public static int ServerMajor(int versionNum) => versionNum / 10000;

        /// <summary>Where a database already on this PC under the same name is kept: fleet_before_move_20261019_1530.</summary>
        public static string KeptName(string database, DateTime now)
        {
            var suffix = $"_before_move_{now:yyyyMMdd}_{now:HHmm}";
            return database[..Math.Min(database.Length, 63 - suffix.Length)] + suffix;
        }

        public static async Task<Dictionary<string, long>> CountTables(NpgsqlConnection c)
        {
            var tables = new List<string>();
            await using (var cmd = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", c))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) tables.Add(r.GetString(0));
            var counts = new Dictionary<string, long>();
            foreach (var t in tables) counts[t] = Convert.ToInt64(await Scalar(c, $"SELECT count(*) FROM \"{t.Replace("\"", "\"\"")}\""), CultureInfo.InvariantCulture);
            return counts;
        }

        public static List<(string Table, long Before, long? After)> CountDifferences(Dictionary<string, long> before, Dictionary<string, long> after) =>
            before.Where(kv => !after.TryGetValue(kv.Key, out var n) || n != kv.Value)
                .Select(kv => (kv.Key, kv.Value, after.TryGetValue(kv.Key, out var n) ? (long?)n : null)).ToList();

        public sealed record Connection(string Host, int Port, string Database, string User, string Password);

        /// <summary>What the move uses that a test replaces: where this PC's PostgreSQL answers (localhost), whether a host
        /// is this PC, the tools, and the steps that need administrator rights.</summary>
        public sealed class MoveDeps
        {
            public string LocalHost = "localhost";
            public Func<string, bool> IsThisPc = _ => false;
            public Func<Task<(PgBackups.Tools? Tools, string? Error)>> FindTools = () => Task.FromResult<(PgBackups.Tools?, string?)>((null, "No tools."));
            public Func<Action<double?>, Task<string>>? DownloadPostgres;
            public Func<IReadOnlyList<WindowsSetup.Step>, Action<List<WindowsSetup.StepResult>>, Task<WindowsSetup.Elevated>> RunElevated =
                (s, p) => WindowsSetup.RunElevated(s, false, p);
            public Func<List<string>> Ranges = () => WindowsSetup.RangesToAllow(WindowsSetup.Addresses());
            public Action<int> RecordInstalled = _ => { };
            public Func<DateTime> Now = () => DateTime.Now;
            public Func<int, Task> Wait = ms => Task.Delay(ms);
            public string TempDir = Path.GetTempPath();
            public int DrainTries = 60;
            public int DrainEveryMs = 3000;
        }

        public static List<JobStep> MoveSteps(bool installPostgres)
        {
            var steps = new List<JobStep> { new("check", "Check everything first") };
            if (installPostgres) steps.Add(new("download", "Download PostgreSQL"));
            steps.Add(new("admin", $"{(installPostgres ? "Install PostgreSQL and let" : "Let")} your other PCs connect to this one (Windows asks for permission)"));
            steps.Add(new("prepare", "Make the database and its login on this PC"));
            steps.Add(new("pause", "Pause the plugins while the copy is made"));
            steps.Add(new("copy", "Copy the database"));
            steps.Add(new("load", "Load it on this PC"));
            steps.Add(new("compare", "Compare every table"));
            steps.Add(new("switch", "Switch every PC over"));
            return steps;
        }

        /// <summary>The checks that need nothing changed: the move's blocking reasons and notes, and the old server's version.</summary>
        public static async Task<(bool Ok, string? Error, List<string> Blocking, List<string> Notes, int ServerVersion)> CheckOld(Connection config, MoveDeps deps)
        {
            if (string.IsNullOrEmpty(config.Host)) return (false, "This PC isn't connected to a database yet. Connect it to the database you want to move first.", new(), new(), 0);
            if (deps.IsThisPc(config.Host))
                return (false, "The database is already on this PC. Run this on the PC you want to move it to, connected to this database.", new(), new(), 0);
            NpgsqlConnection old;
            try { old = await Open(config.Host, config.Port, config.Database, config.User, config.Password); }
            catch (Exception e) { return (false, $"Can't reach the database at {config.Host}: {e.Message}", new(), new(), 0); }
            await using (old)
            {
                if (await Scalar(old, "SELECT to_regclass('companion_database_move') IS NOT NULL") is not true)
                    return (false, "The database hasn't been updated for moving yet. Let the plugin sync once and try again.", new(), new(), 0);
                var note = await DatabaseMove.ReadAsync(old);
                if (note is { State: "moved" }) return (false, $"This database has already moved to {note.Host}:{note.Port}. The plugin follows it at its next sync.", new(), new(), 0);
                if (note is { State: "pausing", Fresh: true }) return (false, "Another move of this database is running right now. Wait for it to finish.", new(), new(), 0);
                var pcs = new List<PcRow>();
                await using (var cmd = new NpgsqlCommand(@"SELECT pc_name, account_label, plugin_version, last_sync_at, db_user,
                    (to_jsonb(companion_pc_status) ->> 'last_read_at')::timestamptz FROM companion_pc_status ORDER BY pc_name, account_label", old))
                await using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync())
                        pcs.Add(new PcRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                            r.IsDBNull(3) ? null : r.GetDateTime(3).ToLocalTime(), r.IsDBNull(5) ? null : r.GetDateTime(5).ToLocalTime(), r.IsDBNull(4) ? null : r.GetString(4)));
                var (blocking, notes) = CheckPcs(pcs, config.User, deps.Now());
                var version = await ServerVersion(old);
                return (blocking.Count == 0, blocking.Count > 0 ? "Some PCs need something first (listed below)." : null, blocking, notes, version);
            }
        }

        // What's on this PC under the database's name: a copy left behind before, or one with no characters, is kept
        // under another name; anything else is left alone and the move refused.
        private static async Task<(bool Exists, bool Replaceable, string Why)> LocalDatabase(NpgsqlConnection admin, string host, int port, string database, string adminUser, string adminPassword)
        {
            if (await Scalar(admin, "SELECT 1 FROM pg_database WHERE datname = $1", database) == null) return (false, false, "");
            await using var c = await Open(host, port, database, adminUser, adminPassword);
            var note = await DatabaseMove.ReadAsync(c);
            if (note is { State: "moved" }) return (true, true, "it's the copy left here when the database moved away before");
            var hasCharacters = await Scalar(c, "SELECT to_regclass('companion_character_current') IS NOT NULL") is true
                && Convert.ToInt64(await Scalar(c, "SELECT count(*) FROM companion_character_current"), CultureInfo.InvariantCulture) > 0;
            return hasCharacters ? (true, false, "") : (true, true, "it has no characters in it");
        }

        /// <summary>
        /// Moves the shared database to this PC, run on the PC it moves to: checks everything first, installs PostgreSQL if
        /// asked and lets other PCs connect (one Windows permission prompt), makes the same database and login here, pauses
        /// the plugins, copies one consistent snapshot (pg_dump, loaded with pg_restore), compares every table's row count,
        /// and only then leaves the note in the old database that sends every plugin and app here. Until that last step, a
        /// failure puts everything back. The old database is never deleted. Returns the new address, or why not.
        /// </summary>
        public static async Task<(bool Ok, string? Error, string? To)> MoveToThisPc(Connection config, string address, int port, string adminUser, string adminPassword,
            bool installPostgres, MoveDeps deps, List<JobStep> steps, Action changed, Action<Connection> saveConfig)
        {
            void Set(string id, string state, string detail = "")
            {
                var s = steps.FirstOrDefault(x => x.Id == id);
                if (s != null) { s.State = state; s.Detail = detail; }
                changed();
            }
            var current = "check";
            bool pausing = false, createdRole = false, createdDatabase = false;
            string? kept = null, dump = null;
            NpgsqlConnection? admin = null, old = null;
            using var heartbeat = new CancellationTokenSource();
            var local = deps.LocalHost;

            async Task<NpgsqlConnection> ConnectAdmin(int tries)
            {
                Exception? last = null;
                for (var i = 0; i < tries; i++)
                {
                    try { return await Open(local, port, "postgres", adminUser, adminPassword, 5); }
                    catch (Exception e)
                    {
                        last = e;
                        if (e is PostgresException { SqlState: "28P01" }) break;
                        if (i < tries - 1) await deps.Wait(2000);
                    }
                }
                throw new InvalidOperationException(last is PostgresException { SqlState: "28P01" }
                    ? "PostgreSQL on this PC refused the master password. It's the one chosen when PostgreSQL was installed here."
                    : $"PostgreSQL on this PC didn't answer: {last?.Message ?? "unknown error"}");
            }

            try
            {
                // 1. Everything that can be checked without changing anything.
                Set("check", "working", "the database and every PC...");
                if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("Choose the address your other PCs will use to reach this PC.");
                var checkedOld = await CheckOld(config, deps);
                if (!checkedOld.Ok) throw new InvalidOperationException(string.Join(" ", new[] { checkedOld.Error }.Concat(checkedOld.Blocking).Where(x => !string.IsNullOrEmpty(x))));
                if (!installPostgres)
                {
                    Set("check", "working", "PostgreSQL on this PC...");
                    admin = await ConnectAdmin(1);
                    var here = await ServerVersion(admin);
                    if (ServerMajor(here) < ServerMajor(checkedOld.ServerVersion))
                        throw new InvalidOperationException($"PostgreSQL on this PC is version {ServerMajor(here)}, older than the old database's {ServerMajor(checkedOld.ServerVersion)}. Install PostgreSQL {ServerMajor(checkedOld.ServerVersion)} or newer here first.");
                    var existing = await LocalDatabase(admin, local, port, config.Database, adminUser, adminPassword);
                    if (existing.Exists && !existing.Replaceable)
                        throw new InvalidOperationException($"This PC already has a database called \"{config.Database}\" with characters in it, so nothing was changed. If it's an old copy you don't need, remove it first.");
                    await admin.DisposeAsync();
                    admin = null;
                }
                Set("check", "done", string.Join(" ", checkedOld.Notes));

                // 2. PostgreSQL (if this PC doesn't have it) and letting other PCs in, with one Windows permission prompt.
                string? installer = null;
                if (installPostgres)
                {
                    current = "download";
                    Set("download", "working", "starting...");
                    if (deps.DownloadPostgres == null) throw new InvalidOperationException("PostgreSQL can't be downloaded here.");
                    installer = await deps.DownloadPostgres(f => Set("download", "working", f == null ? "downloading..." : $"{Math.Round(f.Value * 100)}%"));
                    Set("download", "done");
                }
                current = "admin";
                Set("admin", "working", "waiting for Windows' permission...");
                var adminSteps = new List<WindowsSetup.Step>();
                string? optionDir = null;
                if (installPostgres)
                {
                    optionDir = Path.Combine(deps.TempDir, $"xiv-fleet-pg-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(optionDir);
                    var optionFile = Path.Combine(optionDir, "options.txt");
                    File.WriteAllText(optionFile, WindowsSetup.PostgresOptions(adminPassword, port));
                    adminSteps.Add(new WindowsSetup.Step("installPostgres", Installer: installer, OptionFile: optionFile));
                }
                adminSteps.Add(new WindowsSetup.Step("openConnections", Port: port, Ranges: deps.Ranges()));
                WindowsSetup.Elevated elevated;
                try
                {
                    elevated = await deps.RunElevated(adminSteps, results => { if (results.Count > 0) Set("admin", "working", results[^1].Message); });
                }
                finally
                {
                    // The option file holds the master password: gone as soon as PostgreSQL's installer has read it.
                    if (optionDir != null) try { Directory.Delete(optionDir, true); } catch { /* the temp folder */ }
                }
                if (elevated.Results == null) throw new InvalidOperationException(elevated.Error);
                if (elevated.Results.Any(r => r.Ok && r.Kind == "installPostgres")) deps.RecordInstalled(port);
                if (elevated.Results.FirstOrDefault(r => !r.Ok) is { } failedStep) throw new InvalidOperationException(failedStep.Message);
                Set("admin", "done", string.Join(" ", elevated.Results.Select(r => r.Message)));
                if (installer != null) try { File.Delete(installer); } catch { /* PostgreSQL's installer isn't needed any more */ }

                // 3. The database and its login, with the same names and password the database already uses.
                current = "prepare";
                Set("prepare", "working", "waiting for PostgreSQL...");
                admin = await ConnectAdmin(installPostgres ? 30 : 1);
                if (installPostgres && ServerMajor(await ServerVersion(admin)) < ServerMajor(checkedOld.ServerVersion))
                    throw new InvalidOperationException($"The PostgreSQL just installed is older than the old database's ({ServerMajor(checkedOld.ServerVersion)}).");
                var (tools, toolsError) = await deps.FindTools();
                if (tools == null) throw new InvalidOperationException(toolsError);
                if ((ToolMajor(tools.Version) ?? 0) < ServerMajor(checkedOld.ServerVersion))
                    throw new InvalidOperationException($"This PC's copy tool ({tools.Version}) is older than the old database (version {ServerMajor(checkedOld.ServerVersion)}). Install PostgreSQL {ServerMajor(checkedOld.ServerVersion)} or newer here first.");
                var local2 = await LocalDatabase(admin, local, port, config.Database, adminUser, adminPassword);
                if (local2.Exists && !local2.Replaceable) throw new InvalidOperationException($"This PC already has a database called \"{config.Database}\" with characters in it, so nothing was changed.");
                if (local2.Exists)
                {
                    kept = KeptName(config.Database, deps.Now());
                    await Exec(admin, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid()", config.Database);
                    await Formatted(admin, "ALTER DATABASE %I RENAME TO %I", config.Database, kept);
                }
                var roleExists = await Scalar(admin, "SELECT 1 FROM pg_roles WHERE rolname = $1", config.User) != null;
                await Formatted(admin, roleExists ? "ALTER ROLE %I WITH LOGIN PASSWORD %L" : "CREATE ROLE %I LOGIN PASSWORD %L", config.User, config.Password);
                createdRole = !roleExists;
                await Formatted(admin, "CREATE DATABASE %I OWNER %I", config.Database, config.User);
                createdDatabase = true;
                Set("prepare", "done", local2.Exists ? $"The \"{config.Database}\" that was here ({local2.Why}) is kept as \"{kept}\"." : $"\"{config.Database}\" is ready.");

                // 4. Pause the plugins, and wait for any sync still running to finish.
                current = "pause";
                Set("pause", "working", "telling the plugins...");
                old = await Open(config.Host, config.Port, config.Database, config.User, config.Password);
                await Exec(old, @"INSERT INTO companion_database_move (id, state, host, port, from_host, started_at, updated_at, moved_at)
                    VALUES (1, 'pausing', $1, $2, $3, now(), now(), NULL)
                    ON CONFLICT (id) DO UPDATE SET state = 'pausing', host = $1, port = $2, from_host = $3, started_at = now(), updated_at = now(), moved_at = NULL",
                    address, port, config.Host);
                pausing = true;
                // The pause is refreshed while the copy runs (plugins ignore one older than 5 minutes), on its own connection.
                var beat = Task.Run(async () =>
                {
                    try
                    {
                        await using var hb = await Open(config.Host, config.Port, config.Database, config.User, config.Password);
                        while (!heartbeat.IsCancellationRequested)
                        {
                            await Task.Delay(30000, heartbeat.Token);
                            await Exec(hb, "UPDATE companion_database_move SET updated_at = now() WHERE id = 1 AND state = 'pausing'");
                        }
                    }
                    catch
                    {
                        // Stopped, or the old database went away: the move's own steps say what happened.
                    }
                });
                var quiet = 0;
                for (var i = 0; i < deps.DrainTries && quiet < 2; i++)
                {
                    var busy = Convert.ToInt64(await Scalar(old, @"SELECT count(*) FROM pg_stat_activity
                        WHERE datname = current_database() AND application_name = $1 AND pid <> pg_backend_pid()
                          AND (state <> 'idle' OR state_change > now() - make_interval(secs => $2))", DatabaseMove.ApplicationName, 15.0), CultureInfo.InvariantCulture);
                    quiet = busy == 0 ? quiet + 1 : 0;
                    if (quiet < 2)
                    {
                        Set("pause", "working", busy > 0 ? $"waiting for {(busy == 1 ? "a plugin" : $"{busy} plugins")} to finish syncing..." : "checking...");
                        await deps.Wait(deps.DrainEveryMs);
                    }
                }
                if (quiet < 2) throw new InvalidOperationException("A plugin was still syncing after 3 minutes, so nothing was copied. Try again in a few minutes.");
                Set("pause", "done");

                // 5. One consistent copy: the row counts and pg_dump read the same snapshot of the database.
                current = "copy";
                Set("copy", "working", "counting rows...");
                Dictionary<string, long> before;
                await using (var snap = await Open(config.Host, config.Port, config.Database, config.User, config.Password))
                {
                    await Exec(snap, "BEGIN ISOLATION LEVEL REPEATABLE READ");
                    var snapshot = (string)(await Scalar(snap, "SELECT pg_export_snapshot()"))!;
                    before = await CountTables(snap);
                    Directory.CreateDirectory(deps.TempDir);
                    dump = Path.Combine(deps.TempDir, $"xiv-fleet-move-{DateTime.Now.Ticks}.dump");
                    var login = new PgLogin(config.Host, config.Port, config.Database, config.User, config.Password);
                    var dumping = PgBackups.Run(tools.Dump, new[] { "-h", config.Host, "-p", config.Port.ToString(CultureInfo.InvariantCulture), "-U", config.User,
                        "-d", config.Database, "-Fc", $"--snapshot={snapshot}", "-f", dump }, login);
                    while (!dumping.IsCompleted)
                    {
                        await Task.WhenAny(dumping, Task.Delay(2000));
                        try { Set("copy", "working", $"{new FileInfo(dump).Length / 1048576} MB so far..."); } catch { /* not started */ }
                    }
                    var dumped = await dumping;
                    if (!dumped.Ok) throw new InvalidOperationException($"Copying failed: {Tail(dumped)}");
                    await Exec(snap, "COMMIT");
                }
                Set("copy", "done", $"{new FileInfo(dump).Length / 1048576} MB");

                // 6. Load it into the new database, as the database's own login, which then owns every table.
                current = "load";
                Set("load", "working", "this takes a few minutes for a big database...");
                var loaded = await PgBackups.Run(tools.Restore, new[] { "-h", local, "-p", port.ToString(CultureInfo.InvariantCulture), "-U", config.User, "-d", config.Database,
                    "--no-owner", "--no-privileges", "--single-transaction", "--exit-on-error", dump }, new PgLogin(local, port, config.Database, config.User, config.Password));
                if (!loaded.Ok) throw new InvalidOperationException($"Loading the copy failed: {Tail(loaded)}");
                Set("load", "done");

                // 7. Every table must have exactly the rows it had in the snapshot.
                current = "compare";
                Set("compare", "working", $"{before.Count} tables...");
                await using (var fresh = await Open(local, port, config.Database, config.User, config.Password))
                {
                    var after = await CountTables(fresh);
                    var differ = CountDifferences(before, after);
                    if (differ.Count > 0)
                        throw new InvalidOperationException("The copy doesn't match, so nothing was switched: " +
                            string.Join("; ", differ.Take(5).Select(d => $"{d.Table} has {(d.After == null ? "no table" : d.After.Value.ToString(CultureInfo.InvariantCulture))} instead of {d.Before}")) + ".");
                    await Exec(fresh, "UPDATE companion_database_move SET state = 'arrived', host = $1, port = $2, from_host = $3, updated_at = now(), moved_at = now() WHERE id = 1",
                        address, port, config.Host);
                }
                Set("compare", "done", $"{before.Count} tables, {before.Values.Sum():N0} rows, all the same.");

                // 8. The point of no return: the old database sends everyone here, and this PC switches first.
                current = "switch";
                Set("switch", "working", "leaving the new address in the old database...");
                heartbeat.Cancel();
                await Exec(old, "UPDATE companion_database_move SET state = 'moved', updated_at = now(), moved_at = now() WHERE id = 1");
                pausing = false;
                saveConfig(config with { Host = local, Port = port });
                Set("switch", "done", $"Every app and plugin switches to {address}:{port} at its next refresh or sync.");
                return (true, null, $"{address}:{port}");
            }
            catch (Exception e)
            {
                Set(current, "failed", e.Message);
                heartbeat.Cancel();
                var problems = new List<string>();
                if (pausing && old != null)
                {
                    try { await Exec(old, "DELETE FROM companion_database_move WHERE id = 1 AND state = 'pausing'"); }
                    catch { problems.Add("The plugins carry on syncing by themselves within 5 minutes."); }
                }
                if (createdDatabase || kept != null || createdRole)
                {
                    NpgsqlConnection? a = admin;
                    try
                    {
                        a ??= await ConnectAdmin(1);
                        if (createdDatabase)
                        {
                            await Exec(a, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid()", config.Database);
                            await Formatted(a, "DROP DATABASE IF EXISTS %I", config.Database);
                        }
                        if (kept != null) await Formatted(a, "ALTER DATABASE %I RENAME TO %I", kept, config.Database);
                        if (createdRole) await Formatted(a, "DROP ROLE IF EXISTS %I", config.User);
                    }
                    catch (Exception t)
                    {
                        problems.Add($"Tidying up on this PC didn't finish ({t.Message}).");
                    }
                    finally
                    {
                        if (a != null && a != admin) await a.DisposeAsync();
                    }
                }
                if (problems.Count == 0) problems.Add("The database is still where it was, and every PC carries on using it.");
                return (false, $"{e.Message} {string.Join(" ", problems)}", null);
            }
            finally
            {
                if (old != null) await old.DisposeAsync();
                if (admin != null) await admin.DisposeAsync();
                if (dump != null) try { File.Delete(dump); } catch { /* the temp folder */ }
            }
        }

        private static string Tail(PgBackups.RunResult r) =>
            string.Join(" ", (r.Stderr.Length > 0 ? r.Stderr : r.Stdout).Trim().Split('\n').Select(l => l.TrimEnd('\r')).TakeLast(3));

        // ---- Remove everything (the app's setup/remove-everything.js) ----

        public const string ConfirmWord = "DELETE";

        public sealed record RemovalFacts(WindowsSetup.Installed Installed, Connection? Connection, bool DbOnThisPc, bool PostgresHere, bool TailscaleHere,
            List<string> OtherDatabases, (int Port, string MovedTo)? MovedAway);

        public sealed record RemovalPlan(bool RemovePostgres, bool DropDatabase, bool UndoConnections, bool CanRemoveTailscale, int Port, string? Database, string? User,
            bool MovedAway, bool OfferBackup, bool NeedsMasterPassword, bool OtherPcsLoseDatabase, List<string> Removes, List<string> Keeps);

        /// <summary>What Remove everything removes and what it leaves: only what was set up (PostgreSQL and Tailscale only
        /// when the app or the plugin installed them), otherwise just the fleet's database and login, and the access "Let
        /// other PCs connect" added.</summary>
        public static RemovalPlan Plan(RemovalFacts f)
        {
            var c = f.Connection;
            var local = c != null && f.DbOnThisPc;
            var movedAway = !local && c != null && f.MovedAway != null ? f.MovedAway : null;
            var here = local || movedAway != null;
            var port = movedAway?.Port ?? c?.Port ?? 5432;
            var installedPort = f.Installed.PostgresPort;
            var removePostgres = here && f.PostgresHere && installedPort == port;
            var dropDatabase = here && !removePostgres;
            var undoConnections = here && f.PostgresHere && !removePostgres;
            var canRemoveTailscale = f.Installed.Tailscale && f.TailscaleHere;
            var what = movedAway != null ? $"the copy of \"{c!.Database}\" left here when the database moved to {movedAway.Value.MovedTo}" : $"\"{c?.Database}\"";
            var removes = new List<string>();
            var keeps = new List<string>();
            if (removePostgres)
                removes.Add($"PostgreSQL, with every database in it ({what}{(f.OtherDatabases.Count > 0 ? $", and also {string.Join(", ", f.OtherDatabases.Select(d => $"\"{d}\""))}" : "")})");
            if (dropDatabase)
            {
                var login = c!.User != "postgres" ? $" and its login \"{c.User}\"" : "";
                removes.Add(movedAway != null ? $"The copy of the \"{c.Database}\" database left here when it moved to {movedAway.Value.MovedTo}{login}" : $"The \"{c.Database}\" database{login}");
            }
            if (undoConnections) removes.Add("Other PCs' access to this PC's PostgreSQL (the firewall rule and the lines that were added)");
            removes.Add("The daily backup task");
            removes.Add("This PC's database settings, including the saved connection");
            if (f.PostgresHere && !removePostgres)
                keeps.Add(installedPort != null && !here
                    ? "PostgreSQL itself (none of the fleet's databases is in it now; Leftovers from moving the database can remove it)"
                    : "PostgreSQL itself (it wasn't installed for the fleet, so other things may use it)");
            if (c != null && !f.DbOnThisPc) keeps.Add($"The database on {c.Host} (it's on another PC)");
            if (f.TailscaleHere && !canRemoveTailscale) keeps.Add("Tailscale (it wasn't installed for the fleet)");
            keeps.Add("Your backup files");
            keeps.Add("The plugin itself (remove it in the game's plugin list)");
            return new RemovalPlan(removePostgres, dropDatabase, undoConnections, canRemoveTailscale, port, here ? c!.Database : null, here ? c!.User : null,
                movedAway != null, local, dropDatabase, local, removes, keeps);
        }

        public static List<JobStep> RemovalSteps(RemovalPlan plan, bool backup, bool removeTailscale)
        {
            var steps = new List<JobStep>();
            if (plan.OfferBackup && backup) steps.Add(new("backup", "Make a final backup"));
            if (plan.DropDatabase) steps.Add(new("database", $"Remove {(plan.MovedAway ? "the copy of " : "")}the \"{plan.Database}\" database and its login"));
            steps.Add(new("tasks", "Remove the backup task"));
            var admin = new[] { plan.RemovePostgres ? "remove PostgreSQL" : null, plan.CanRemoveTailscale && removeTailscale ? "remove Tailscale" : null,
                plan.UndoConnections ? "stop letting other PCs in" : null }.OfType<string>().ToList();
            if (admin.Count > 0)
            {
                var text = string.Join(", ", admin);
                steps.Add(new("admin", char.ToUpperInvariant(text[0]) + text[1..] + " (Windows asks for permission)"));
            }
            return steps;
        }
    }
}
