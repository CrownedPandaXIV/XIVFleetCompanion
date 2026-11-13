using Npgsql;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Setup
{
    /// <summary>
    /// Set up this PC and Remove everything, step by step, as the app's (setup/run-setup.js and remove-everything.js):
    /// downloads first, then one Windows permission prompt for everything that needs it, then the database. What was
    /// installed is noted (the app's own installed-by-app.json), so Remove everything only removes what was set up.
    /// </summary>
    public static class SetupRuns
    {
        public sealed class Deps
        {
            public string SettingsFolder = SharedBackups.Folder;
            public Func<Task<(bool Postgres, bool Tailscale)>> Detect = WindowsSetup.Detect;
            public Func<WindowsSetup.Download, Action<double?>, Task<string>> Download = (f, p) => WindowsSetup.DownloadOnce(f, WindowsSetup.DownloadFolder, p);
            public Func<IReadOnlyList<WindowsSetup.Step>, bool, Action<List<WindowsSetup.StepResult>>, Task<WindowsSetup.Elevated>> RunElevated = WindowsSetup.RunElevated;
            public Func<List<string>> Ranges = () => WindowsSetup.RangesToAllow(WindowsSetup.Addresses());
            public Func<int, string, Task<NpgsqlConnection>> ConnectAdmin = (port, password) => DatabaseAdmin.Open("localhost", port, "postgres", "postgres", password, 5);
            public Func<int, Task> Wait = ms => Task.Delay(ms);
            public bool Windows = OperatingSystem.IsWindows();
        }

        public static List<JobStep> SetupSteps(WindowsSetup.Plan plan)
        {
            var steps = new List<JobStep>();
            if (plan.InstallPostgres) steps.Add(new("downloadPostgres", $"Download {WindowsSetup.Postgres.Name} (about {WindowsSetup.Postgres.SizeMb} MB)"));
            if (plan.InstallTailscale) steps.Add(new("downloadTailscale", $"Download Tailscale (about {WindowsSetup.TailscaleX64.SizeMb} MB)"));
            var admin = new[] { plan.InstallPostgres ? "install PostgreSQL" : null, plan.InstallTailscale ? "install Tailscale" : null, plan.OpenConnections ? "let other PCs connect" : null }
                .OfType<string>().ToList();
            if (admin.Count > 0)
            {
                var text = string.Join(", ", admin);
                steps.Add(new("admin", char.ToUpperInvariant(text[0]) + text[1..] + " (Windows asks for permission)"));
            }
            if (plan.CreateDatabase) steps.Add(new("database", "Make the database and its login"));
            return steps;
        }

        public sealed record SetupInput(string Role, bool SameNetwork, string MasterPassword, string Database, string User, string AppPassword, int Port);

        /// <summary>Runs Set up this PC. saveConnection gets the new database's connection once it's made.</summary>
        public static async Task<(bool Ok, string? Error)> RunSetup(SetupInput input, Deps deps, List<JobStep> steps, Action changed, Action<DatabaseAdmin.Connection> saveConnection)
        {
            if (!deps.Windows) return (false, "Setting up this PC can only be done on Windows.");
            var (pgHere, tsHere) = await deps.Detect();
            var plan = WindowsSetup.SetupPlan(input.Role, input.SameNetwork, pgHere, tsHere);
            if (plan == null) return (false, "Choose what this PC is for first.");
            if (plan.CreateDatabase && DatabaseAdmin.CheckRequest(input.Database, input.User, input.AppPassword) is { } bad) return (false, bad);
            if (plan.CreateDatabase && DatabaseAdmin.PortProblem(input.Port) is { } badPort) return (false, badPort);
            if (plan.CreateDatabase && input.MasterPassword.Length == 0) return (false, "Choose PostgreSQL's master password (or enter it, if PostgreSQL is already on this PC).");
            steps.Clear();
            steps.AddRange(SetupSteps(plan));
            changed();
            void Set(string id, string state, string detail = "")
            {
                var s = steps.FirstOrDefault(x => x.Id == id);
                if (s != null) { s.State = state; s.Detail = detail; }
                changed();
            }
            (bool, string?) Fail(string id, string message) { Set(id, "failed", message); return (false, message); }

            var files = new Dictionary<string, string>();
            foreach (var (what, id, file) in new[] { ("postgres", "downloadPostgres", WindowsSetup.Postgres), ("tailscale", "downloadTailscale", WindowsSetup.TailscaleForThisPc()) })
            {
                if (!(what == "postgres" ? plan.InstallPostgres : plan.InstallTailscale)) continue;
                Set(id, "working", "starting...");
                try
                {
                    files[what] = await deps.Download(file, f => Set(id, "working", f == null ? "downloading..." : $"{Math.Round(f.Value * 100)}%"));
                    Set(id, "done");
                }
                catch (Exception e)
                {
                    return Fail(id, e.Message);
                }
            }

            var adminSteps = new List<WindowsSetup.Step>();
            string? optionDir = null;
            if (plan.InstallPostgres)
            {
                optionDir = Path.Combine(Path.GetTempPath(), $"xiv-fleet-pg-{Guid.NewGuid():N}");
                Directory.CreateDirectory(optionDir);
                var optionFile = Path.Combine(optionDir, "options.txt");
                File.WriteAllText(optionFile, WindowsSetup.PostgresOptions(input.MasterPassword, input.Port));
                adminSteps.Add(new WindowsSetup.Step("installPostgres", Installer: files["postgres"], OptionFile: optionFile));
            }
            if (plan.InstallTailscale) adminSteps.Add(new WindowsSetup.Step("installTailscale", Msi: files["tailscale"]));
            if (plan.OpenConnections) adminSteps.Add(new WindowsSetup.Step("openConnections", Port: input.Port, Ranges: deps.Ranges()));
            if (adminSteps.Count > 0)
            {
                Set("admin", "working", "waiting for Windows' permission...");
                WindowsSetup.Elevated r;
                try
                {
                    r = await deps.RunElevated(adminSteps, false, results => { if (results.Count > 0) Set("admin", "working", results[^1].Message); });
                }
                finally
                {
                    // The option file holds the master password: gone as soon as PostgreSQL's installer has read it.
                    if (optionDir != null) try { Directory.Delete(optionDir, true); } catch { /* the temp folder */ }
                }
                if (r.Results == null) return Fail("admin", r.Error ?? "Windows didn't run the steps.");
                // Noted so Remove everything knows it may remove them (never a PostgreSQL that was already here).
                var done = r.Results.Where(x => x.Ok).Select(x => x.Kind).ToHashSet();
                if (done.Contains("installPostgres") || done.Contains("installTailscale"))
                    WindowsSetup.RecordInstalled(deps.SettingsFolder, done.Contains("installPostgres") ? input.Port : null, done.Contains("installTailscale"));
                if (r.Results.FirstOrDefault(x => !x.Ok) is { } failed) return Fail("admin", failed.Message);
                Set("admin", "done", string.Join(" ", r.Results.Select(x => x.Message)));
                foreach (var f in files.Values) try { File.Delete(f); } catch { /* the download folder */ }
            }

            if (plan.CreateDatabase)
            {
                Set("database", "working", "waiting for PostgreSQL to start...");
                NpgsqlConnection? admin = null;
                Exception? last = null;
                for (var i = 0; i < 30 && admin == null; i++)
                {
                    try { admin = await deps.ConnectAdmin(input.Port, input.MasterPassword); }
                    catch (Exception e)
                    {
                        last = e;
                        if (e is PostgresException { SqlState: "28P01" }) break;
                        await deps.Wait(2000);
                    }
                }
                if (admin == null)
                    return Fail("database", last is PostgresException { SqlState: "28P01" }
                        ? "PostgreSQL refused the master password. If PostgreSQL was already on this PC, it's the password chosen when it was installed."
                        : $"PostgreSQL didn't answer: {last?.Message ?? "unknown error"}");
                await using (admin)
                {
                    if (await DatabaseAdmin.CreateDatabase(admin, input.Database, input.User, input.AppPassword) is { } why) return Fail("database", why);
                }
                saveConnection(new DatabaseAdmin.Connection("localhost", input.Port, input.Database, input.User, input.AppPassword));
                Set("database", "done", $"\"{input.Database}\" is ready.");
            }
            return (true, null);
        }

        public sealed record RemoveInput(bool Backup, string BackupFolder, bool RemoveTailscale, string MasterPassword, string Confirm);

        /// <summary>Runs Remove everything for a plan (DatabaseAdmin.Plan). Stops at the first step that fails, so nothing
        /// is removed without the final backup asked for. makeBackup: a backup into a folder (ok, file or why not);
        /// removeTask: the app's daily backup task; forgetConnection: this PC's saved connection.</summary>
        public static async Task<(bool Ok, string? Error)> RemoveEverything(RemoveInput input, DatabaseAdmin.RemovalPlan plan, Deps deps, List<JobStep> steps, Action changed,
            Func<string, Task<(bool Ok, string Message)>> makeBackup, Func<Task<(bool Ok, string? Error)>> removeTask, Action forgetConnection)
        {
            if (!deps.Windows) return (false, "Removing everything can only be done on Windows.");
            if (input.Confirm.Trim() != DatabaseAdmin.ConfirmWord) return (false, $"Type {DatabaseAdmin.ConfirmWord} to confirm.");
            if (plan.NeedsMasterPassword && input.MasterPassword.Length == 0) return (false, "Enter PostgreSQL's master password, so the database and its login can be removed.");
            if (plan.OfferBackup && input.Backup && input.BackupFolder.Trim().Length == 0) return (false, "Choose a folder for the final backup.");
            steps.Clear();
            steps.AddRange(DatabaseAdmin.RemovalSteps(plan, input.Backup, input.RemoveTailscale));
            changed();
            void Set(string id, string state, string detail = "")
            {
                var s = steps.FirstOrDefault(x => x.Id == id);
                if (s != null) { s.State = state; s.Detail = detail; }
                changed();
            }
            (bool, string?) Fail(string id, string message) { Set(id, "failed", message); return (false, message); }

            if (steps.Any(s => s.Id == "backup"))
            {
                Set("backup", "working", "backing up...");
                var b = await makeBackup(input.BackupFolder.Trim());
                if (!b.Ok) return Fail("backup", $"The final backup didn't work, so nothing was removed: {b.Message}");
                Set("backup", "done", b.Message);
            }
            if (plan.DropDatabase)
            {
                Set("database", "working", "removing...");
                try
                {
                    await using var admin = await deps.ConnectAdmin(plan.Port, input.MasterPassword);
                    var (ok, note) = await DatabaseAdmin.DropDatabase(admin, plan.Database!, plan.User!);
                    if (!ok) return Fail("database", note ?? "");
                    Set("database", "done", note ?? "");
                }
                catch (Exception e)
                {
                    return Fail("database", e is PostgresException { SqlState: "28P01" } ? "PostgreSQL refused the master password." : e.Message);
                }
            }
            Set("tasks", "working");
            var task = await removeTask();
            if (!task.Ok) return Fail("tasks", task.Error ?? "");
            Set("tasks", "done");
            var adminSteps = new List<WindowsSetup.Step>();
            if (plan.RemovePostgres) adminSteps.Add(new WindowsSetup.Step("removePostgres", Port: plan.Port));
            if (plan.CanRemoveTailscale && input.RemoveTailscale) adminSteps.Add(new WindowsSetup.Step("removeTailscale"));
            if (plan.UndoConnections) adminSteps.Add(new WindowsSetup.Step("undoOpenConnections", Port: plan.Port));
            if (adminSteps.Count > 0)
            {
                Set("admin", "working", "waiting for Windows' permission...");
                var r = await deps.RunElevated(adminSteps, true, results => { if (results.Count > 0) Set("admin", "working", results[^1].Message); });
                if (r.Results == null) return Fail("admin", r.Error ?? "Windows didn't run the steps.");
                WindowsSetup.ForgetInstalled(deps.SettingsFolder, r.Results.Any(x => x.Ok && x.Kind == "removePostgres"), r.Results.Any(x => x.Ok && x.Kind == "removeTailscale"));
                if (r.Results.FirstOrDefault(x => !x.Ok) is { } failed) return Fail("admin", failed.Message);
                Set("admin", "done", string.Join(" ", r.Results.Select(x => x.Message)));
            }
            forgetConnection();
            return (true, null);
        }
    }
}
