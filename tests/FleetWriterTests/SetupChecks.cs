using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using XIVFleetCompanion;
using XIVFleetCompanion.Setup;

// Managing the shared database from the game (Setup/DatabaseAdmin.cs, Setup/WindowsSetup.cs), as the app's Setup Help
// and Database tab: the same checks and answers as the app's (its outputs for these cases), a new database and its login,
// leftovers from a move, and moving the database to "this PC" between two scratch PostgreSQL servers, every table compared.
internal static partial class Program
{
    private static async Task SetupChecks(NpgsqlConnection admin, string given)
    {
        // The app's answers (setup/create-database.js checkRequest).
        var requests = new (string Db, string User, string Password, string? App)[]
        {
            ("fleet", "fleetapp", "longenough", null),
            ("Fleet", "x", "longenough", "The database name can only use lower-case letters, numbers and _, and must start with a letter."),
            ("fleet", "Bad-", "longenough", "The username can only use lower-case letters, numbers and _, and must start with a letter."),
            ("postgres", "fleetapp", "longenough", "\"postgres\" is one of PostgreSQL's own databases. Choose another name."),
            ("fleet", "postgres", "longenough", "Choose a username other than \"postgres\" for the app."),
            ("fleet", "fleetapp", "short", "The app's password needs at least 8 characters."),
        };
        Check(requests.All(r => DatabaseAdmin.CheckRequest(r.Db, r.User, r.Password) == r.App), "setup: a new database's names and password checked as the app checks them");

        // The app's move checks (setup/move-database.js): which PCs block a move and which are only noted.
        var now = new DateTime(2026, 11, 13, 12, 0, 0);
        var (blocking, notes) = DatabaseAdmin.CheckPcs(new[]
        {
            new DatabaseAdmin.PcRow("DESK", "Main", "2.3.1", new DateTime(2026, 11, 12), null, "fleetapp"),
            new DatabaseAdmin.PcRow("LAP", null, "0.13.9", new DateTime(2026, 11, 12), null, null),
            new DatabaseAdmin.PcRow("OLD", null, "2.0.0", new DateTime(2026, 10, 1), null, "fleetapp"),
            new DatabaseAdmin.PcRow("ALT", "Alt", "1.0.0", new DateTime(2026, 11, 12), null, null),
            new DatabaseAdmin.PcRow("OTHER", null, "2.0.0", null, new DateTime(2026, 11, 12), "someone"),
        }, "fleetapp", now);
        Check(blocking.Count == 3 && blocking[0].StartsWith("LAP runs plugin 0.13.9, which can't follow a move.") && blocking[1].StartsWith("ALT (Alt) hasn't synced since its plugin was updated.")
              && blocking[2].StartsWith("OTHER's plugin logs in as \"someone\"") && notes.Count == 1 && notes[0].StartsWith("OLD hasn't synced since "),
            "setup: a move's checks of every PC, as the app's (an old plugin, one not synced since updating, another login block it; one not seen lately is noted): " + string.Join(" | ", blocking.Concat(notes)));
        Check(DatabaseAdmin.KeptName("fleet", new DateTime(2026, 10, 19, 15, 30, 0)) == "fleet_before_move_20261019_1530" && DatabaseAdmin.KeptName(new string('x', 63), now).Length == 63
              && DatabaseAdmin.ToolMajor("pg_dump (PostgreSQL) 18.6") == 18 && DatabaseAdmin.ToolMajor("pg_dump (PostgreSQL) 16.15 (Ubuntu 16.15-0ubuntu0.24.04.1)") == 16,
            "setup: the name a copy already here is kept under, and the tools' version, as the app's");

        // Remove everything's plan, as the app's for the same facts (the removes and keeps that aren't about the app itself).
        var facts = new DatabaseAdmin.RemovalFacts(new WindowsSetup.Installed(5432, true), new DatabaseAdmin.Connection("localhost", 5432, "fleet", "fleetapp", ""), true, true, true,
            new List<string> { "other" }, null);
        var plan = DatabaseAdmin.Plan(facts);
        var steps = DatabaseAdmin.RemovalSteps(plan, true, true).Select(s => s.Label).ToList();
        Check(plan.RemovePostgres && !plan.DropDatabase && plan.Removes[0] == "PostgreSQL, with every database in it (\"fleet\", and also \"other\")"
              && string.Join("|", steps) == "Make a final backup|Remove the backup task|Remove PostgreSQL, remove Tailscale (Windows asks for permission)",
            "setup: Remove everything removes PostgreSQL only when it was installed for the fleet, as the app's: " + string.Join(" | ", plan.Removes.Concat(steps)));
        var plan2 = DatabaseAdmin.Plan(facts with { Installed = new WindowsSetup.Installed(null, false) });
        Check(plan2.DropDatabase && plan2.UndoConnections && plan2.Removes[0] == "The \"fleet\" database and its login \"fleetapp\"" && plan2.Keeps.Any(k => k.StartsWith("PostgreSQL itself"))
              && plan2.Keeps.Any(k => k.StartsWith("Tailscale")) && string.Join("|", DatabaseAdmin.RemovalSteps(plan2, false, false).Select(s => s.Label))
              == "Remove the \"fleet\" database and its login|Remove the backup task|Stop letting other PCs in (Windows asks for permission)",
            "setup: otherwise only the fleet's database and login go, and the access that was added; PostgreSQL and Tailscale stay");

        // This PC's addresses and the ranges let in, as the app's.
        var addresses = new List<WindowsSetup.Address>
        {
            new("Ethernet Intel", "192.168.1.20", 24), new("Tailscale Tunnel", "100.101.102.103", 32), new("vEthernet (WSL)", "172.20.0.1", 20), new("Wi-Fi", "10.0.5.7", 16),
        };
        var (ts, home) = WindowsSetup.PcAddresses(addresses);
        Check(ts.SequenceEqual(new[] { "100.101.102.103" }) && home.SequenceEqual(new[] { "192.168.1.20", "10.0.5.7" })
              && WindowsSetup.RangesToAllow(addresses).SequenceEqual(new[] { "100.64.0.0/10", "192.168.1.0/24", "10.0.0.0/16" }),
            "setup: this PC's Tailscale and home addresses (virtual adapters left out), and the ranges Let other PCs connect lets in");
        Check(WindowsSetup.TailscaleStatus("{\"BackendState\":\"Running\",\"Self\":{\"TailscaleIPs\":[\"fd7a::1\",\"100.80.1.2\"]}}") == (true, "100.80.1.2", "Running")
              && WindowsSetup.LoginUrl("To authenticate, visit:\n\n\thttps://login.tailscale.com/a/abc123\n") == "https://login.tailscale.com/a/abc123",
            "setup: Tailscale's status and sign-in address read as the app reads them");
        Check(WindowsSetup.PostgresOptions("pw", 5433) == "mode=unattended\r\nunattendedmodeui=none\r\nsuperpassword=pw\r\nserverport=5433\r\ndisable-components=pgAdmin,stackbuilder\r\n"
              && WindowsSetup.SuggestPassword().Length == 16 && WindowsSetup.SetupPlan("dbhost", false, true, false) is { InstallPostgres: false, AlreadyPostgres: true, InstallTailscale: true, OpenConnections: true },
            "setup: PostgreSQL's quiet-install answers, a password suggestion, and what a database PC needs, as the app's");
        var parsed = WindowsSetup.ParseServices("postgresql-x64-16|\"C:\\Program Files\\PostgreSQL\\16\\bin\\pg_ctl.exe\" runservice -N \"postgresql-x64-16\" -D \"C:\\Program Files\\PostgreSQL\\16\\data\" -w\r\nbad");
        Check(parsed.Count == 1 && parsed[0] == new WindowsSetup.PostgresService("postgresql-x64-16", "C:\\Program Files\\PostgreSQL\\16\\data", "C:\\Program Files\\PostgreSQL\\16"),
            "setup: PostgreSQL's Windows service found from its command (its data and install folders)");
        var folder = Path.Combine(Path.GetTempPath(), $"installed-{Guid.NewGuid():N}");
        WindowsSetup.RecordInstalled(folder, 5432, false);
        WindowsSetup.RecordInstalled(folder, null, true);
        var installed = WindowsSetup.ReadInstalled(folder);
        WindowsSetup.ForgetInstalled(folder, true, false);
        Check(installed == new WindowsSetup.Installed(5432, true) && WindowsSetup.ReadInstalled(folder) == new WindowsSetup.Installed(null, true)
              && File.ReadAllText(WindowsSetup.InstalledFile(folder)).Contains("\"tailscale\": true"),
            "setup: what was installed noted in the app's own file, kept across notes, and forgotten once removed");
        Directory.Delete(folder, true);
        Check(WindowsSetup.Script().Contains("function Open-Connections") && WindowsSetup.Script().Contains("$FirewallRule = 'XIV Fleet Companion PostgreSQL'"),
            "setup: the administrator script ships in the plugin");

        // The administrator script's edits of PostgreSQL's files, the same as the app's for the same files (run with
        // PowerShell where there is one: GitHub's test machines have it).
        var pwsh = new[] { Environment.GetEnvironmentVariable("FLEET_PWSH"), "/usr/bin/pwsh" }.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        if (pwsh != null)
        {
            var script = Path.Combine(Path.GetTempPath(), $"elevated-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(script, WindowsSetup.Script());
            var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
            var r = await PgBackups.Run(pwsh, new[] { "-NoProfile", "-File", Path.Combine(dir, "remote-access-check.ps1"), "-Cases", Path.Combine(dir, "remote-access-fixture.json"), "-Script", script });
            File.Delete(script);
            Check(r.Stdout.Trim().EndsWith("failures: 0"), "setup: Let other PCs connect edits postgresql.conf and pg_hba.conf as the app does, and undoes it: " + (r.Stdout + r.Stderr).Trim());
        }
        else Console.WriteLine("(PowerShell not here: the administrator script's file edits weren't checked.)");

        // A new database and its login, made as PostgreSQL's administrator (and refused the second time).
        var b = new NpgsqlConnectionStringBuilder(given);
        await Exec(admin, "DROP DATABASE IF EXISTS setup_made WITH (FORCE)");
        await Exec(admin, "DROP ROLE IF EXISTS setup_maker");
        var made = await DatabaseAdmin.CreateDatabase(admin, "setup_made", "setup_maker", "a password with 'quotes'");
        var again = await DatabaseAdmin.CreateDatabase(admin, "setup_made", "other_maker", "longenough");
        await using (var check = new NpgsqlCommand("SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'setup_made'", admin))
            Check(made == null && (string?)await check.ExecuteScalarAsync() == "setup_maker" && again == "A database called \"setup_made\" already exists. To use it, enter its details above instead.",
                $"setup: New database makes it and its own login, which owns it; a name that's taken is refused ({made ?? "made"})");

        // Leftovers: a copy kept under another name and one a move left behind are leftovers; the one in use isn't.
        await Exec(admin, "DROP DATABASE IF EXISTS setup_kept_before_move_20261019_1530 WITH (FORCE)");
        await Exec(admin, "CREATE DATABASE setup_kept_before_move_20261019_1530");
        Func<string, Task<NpgsqlConnection>> connectTo = async name =>
        {
            var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(given) { Database = name, Pooling = false }.ConnectionString);
            await c.OpenAsync();
            return c;
        };
        await using (var c = await connectTo("setup_made"))
            await Exec(c, "CREATE TABLE companion_database_move (id int PRIMARY KEY, state text, host text, port int, from_host text, started_at timestamptz, updated_at timestamptz, moved_at timestamptz); " +
                          "INSERT INTO companion_database_move VALUES (1, 'moved', '100.80.1.2', 5432, 'old', now(), now(), now())");
        var (left, others) = await DatabaseAdmin.FindLeftovers(admin, connectTo, "postgres_in_use");
        Check(left.Any(l => l.Name == "setup_made" && l.Kind == "moved-away" && l.MovedTo == "100.80.1.2:5432") && left.Any(l => l.Name == "setup_kept_before_move_20261019_1530" && l.Kind == "kept")
              && !left.Any(l => l.Name == "postgres_in_use") && others.Count > 0,
            "setup: leftovers from a move found (a copy kept, a copy left behind), as the app finds them");
        Check(await DatabaseAdmin.RemoveLeftover(admin, "postgres", connectTo, null) == "\"postgres\" isn't a leftover from a move, so it wasn't removed."
              && await DatabaseAdmin.RemoveLeftover(admin, "setup_kept_before_move_20261019_1530", connectTo, null) == null,
            "setup: only a leftover can be removed");
        var dropped = await DatabaseAdmin.DropDatabase(admin, "setup_made", "setup_maker");
        await using (var gone = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = 'setup_made'", admin))
            Check(dropped.Ok && (long)(await gone.ExecuteScalarAsync())! == 0 && (await DatabaseAdmin.DropDatabase(admin, "postgres", "x")).Ok == false,
                "setup: Remove everything drops the fleet's database and its login, never PostgreSQL's own");

        await MoveChecks2(admin, given);
    }

    // Moving the database to this PC: a second scratch server plays "this PC" (FLEET_TEST_DB2, or the one on port 55434).
    private static async Task MoveChecks2(NpgsqlConnection admin, string given)
    {
        var second = Environment.GetEnvironmentVariable("FLEET_TEST_DB2") ?? "Host=localhost;Port=55434;Username=postgres;Password=postgres;Database=postgres";
        var here = new NpgsqlConnectionStringBuilder(second);
        NpgsqlConnection there;
        try
        {
            there = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(second) { Pooling = false }.ConnectionString);
            await there.OpenAsync();
        }
        catch (Exception e)
        {
            Check(false, $"move to this PC: a second PostgreSQL to move to ({e.Message})");
            return;
        }
        await using (there)
        {
            var b = new NpgsqlConnectionStringBuilder(given) { Pooling = false };
            await Exec(admin, "DROP DATABASE IF EXISTS move_old WITH (FORCE)");
            await Exec(admin, "DROP ROLE IF EXISTS move_user");
            await Exec(admin, "CREATE ROLE move_user LOGIN PASSWORD 'move-password'");
            await Exec(admin, "CREATE DATABASE move_old OWNER move_user");
            await Exec(there, "DROP DATABASE IF EXISTS move_old WITH (FORCE)");
            await Exec(there, "DROP ROLE IF EXISTS move_user");
            var config = new DatabaseAdmin.Connection(b.Host == "/tmp" ? "localhost" : b.Host!, b.Port, "move_old", "move_user", "move-password");
            await using (var old = new NpgsqlConnection(DatabaseAdmin.ConnectionString(config.Host, config.Port, config.Database, config.User, config.Password)))
            {
                await old.OpenAsync();
                await DatabaseUpkeep.EnsureAsync(old, DatabaseUpkeep.Files.Embedded(), "move test", wait: true);
                await Exec(old, "INSERT INTO companion_character_current (cid, name, world, last_synced_at) VALUES (1, 'Ann', 'Ravana', now()), (2, 'Bo', 'Odin', now())");
                await Exec(old, "INSERT INTO companion_pc_status (pc_name, account_label, plugin_version, last_sync_at, db_user) VALUES ('DESK', '', '2.4.0', now(), 'move_user')");
            }
            var pgBin = Directory.Exists("/usr/lib/postgresql/16/bin") ? "/usr/lib/postgresql/16/bin" : "";
            var elevatedAsked = new List<string>();
            var deps = new DatabaseAdmin.MoveDeps
            {
                LocalHost = here.Host!, IsThisPc = _ => false, FindTools = () => PgBackups.FindTools(pgBin, "/nowhere"),
                RunElevated = (s, p) =>
                {
                    elevatedAsked.AddRange(s.Select(x => $"{x.Kind}:{x.Port}"));
                    return Task.FromResult(new WindowsSetup.Elevated(true, new List<WindowsSetup.StepResult> { new("openConnections", true, "Other PCs can connect.", new List<string>()) }, null));
                },
                Ranges = () => new List<string> { "100.64.0.0/10" }, Wait = _ => Task.CompletedTask, DrainEveryMs = 0,
            };
            DatabaseAdmin.Connection? saved = null;
            var steps = DatabaseAdmin.MoveSteps(false);
            var (ok, error, to) = await DatabaseAdmin.MoveToThisPc(config, "100.80.1.2", here.Port, here.Username!, here.Password ?? "", false, deps, steps, () => { }, c => saved = c);
            Check(ok && to == $"100.80.1.2:{here.Port}" && steps.All(s => s.State == "done") && elevatedAsked.SequenceEqual(new[] { $"openConnections:{here.Port}" })
                  && saved == config with { Host = here.Host!, Port = here.Port },
                $"move to this PC: every step done, other PCs let in (one permission prompt), and this PC switched: {error ?? to} [{string.Join(", ", steps.Select(s => $"{s.Id}={s.State}"))}]");
            await using (var moved = new NpgsqlConnection(DatabaseAdmin.ConnectionString(here.Host!, here.Port, "move_old", "move_user", "move-password")))
            await using (var old = new NpgsqlConnection(DatabaseAdmin.ConnectionString(config.Host, config.Port, config.Database, config.User, config.Password)))
            {
                await moved.OpenAsync();
                await old.OpenAsync();
                var before = await DatabaseAdmin.CountTables(old);
                var after = await DatabaseAdmin.CountTables(moved);
                var note = await DatabaseMove.ReadAsync(old);
                var arrived = await DatabaseMove.ReadAsync(moved);
                Check(before.Count > 20 && DatabaseAdmin.CountDifferences(before, after).Count == 0 && after["companion_character_current"] == 2
                      && note is { State: "moved", Host: "100.80.1.2" } && note.Port == here.Port && arrived is { State: "arrived" },
                    $"move to this PC: every table arrived with every row ({before.Count} tables), the old database sends everyone here, and the new one knows it arrived");
            }

            // A second move into a PC that has the database with characters in it is refused, and nothing changes.
            await using (var old = new NpgsqlConnection(DatabaseAdmin.ConnectionString(config.Host, config.Port, config.Database, config.User, config.Password)))
            {
                await old.OpenAsync();
                await Exec(old, "DELETE FROM companion_database_move");
            }
            var refused = await DatabaseAdmin.MoveToThisPc(config, "100.80.1.2", here.Port, here.Username!, here.Password ?? "", false, deps, DatabaseAdmin.MoveSteps(false), () => { }, _ => { });
            Check(!refused.Ok && refused.Error!.Contains("already has a database called \"move_old\" with characters in it"), "move to this PC: never over a database with characters in it: " + refused.Error);

            // One that fails part way (no copy tool) puts everything back: the copy made here goes, the old database carries on.
            await Exec(there, "DROP DATABASE IF EXISTS move_old WITH (FORCE)");
            await Exec(there, "DROP ROLE IF EXISTS move_user");
            var failing = new DatabaseAdmin.MoveDeps
            {
                LocalHost = deps.LocalHost, IsThisPc = deps.IsThisPc, RunElevated = deps.RunElevated, Ranges = deps.Ranges, Wait = deps.Wait, DrainEveryMs = 0,
                FindTools = () => Task.FromResult<(PgBackups.Tools?, string?)>((new PgBackups.Tools("", "/nowhere/pg_dump", "/nowhere/pg_restore", "pg_dump (PostgreSQL) 16.0", false), null)),
            };
            var failed = await DatabaseAdmin.MoveToThisPc(config, "100.80.1.2", here.Port, here.Username!, here.Password ?? "", false, failing, DatabaseAdmin.MoveSteps(false), () => { }, _ => { });
            await using (var gone = new NpgsqlCommand("SELECT (SELECT count(*) FROM pg_database WHERE datname = 'move_old') + (SELECT count(*) FROM pg_roles WHERE rolname = 'move_user')", there))
            await using (var old = new NpgsqlConnection(DatabaseAdmin.ConnectionString(config.Host, config.Port, config.Database, config.User, config.Password)))
            {
                await old.OpenAsync();
                Check(!failed.Ok && failed.Error!.StartsWith("Copying failed") && (long)(await gone.ExecuteScalarAsync())! == 0 && await DatabaseMove.ReadAsync(old) == null,
                    "move to this PC: one that fails part way puts it all back (nothing left here, the plugins carry on with the old one): " + failed.Error);
            }
            await Exec(there, "DROP DATABASE IF EXISTS move_old WITH (FORCE)");
            await Exec(there, "DROP ROLE IF EXISTS move_user");
            await Exec(admin, "DROP DATABASE IF EXISTS move_old WITH (FORCE)");
        }
    }
}
