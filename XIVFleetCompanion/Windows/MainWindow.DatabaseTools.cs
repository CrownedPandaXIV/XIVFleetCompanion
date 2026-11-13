using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using XIVFleetCompanion.Setup;

namespace XIVFleetCompanion.Windows;

// Settings → Database tools, as the app's Setup Help and Database tab: Set up this PC (PostgreSQL, Tailscale, letting
// other PCs connect, the database), a new database, moving the database to this PC, letting other PCs connect, the
// leftovers a move leaves, what another PC needs, and removing everything. Each long job lists its steps as it goes.
public partial class MainWindow
{
    private readonly List<JobStep> toolSteps = new();
    private string toolJob = "";
    private bool toolBusy;
    private string? toolResult;
    private bool toolOk;
    private (bool Postgres, bool Tailscale)? detected;
    private bool detecting;
    private (bool SignedIn, string? Address, string State)? tailscale;

    // Set up this PC.
    private string setupRole = "dbhost";
    private bool setupSameNetwork;
    private string setupMaster = "";
    private string setupDatabase = "fleet";
    private string setupUser = "fleetapp";
    private string setupAppPassword = "";
    private int setupPort = 5432;

    // Move, leftovers, new database.
    private string adminPassword = "";
    private int localPort = 5432;
    private string moveAddress = "";
    private bool moveInstall;
    private (bool Ok, string? Error, List<string> Blocking, List<string> Notes, int ServerVersion)? moveChecked;
    private List<DatabaseAdmin.Leftover>? leftovers;
    private bool postgresRemovable;
    private string newDatabase = "fleet";
    private string newUser = "fleetapp";
    private string newPassword = "";

    // Remove everything.
    private bool removeBackup = true;
    private string removeBackupFolder = "";
    private bool removeTailscale = true;
    private string removeConfirm = "";
    private DatabaseAdmin.RemovalPlan? removalPlan;
    private readonly FileDialogManager toolDialog = new();

    private DatabaseAdmin.Connection? SavedConnection() =>
        PostgresCredentialStore.Load(plugin.Configuration.UseRemoteConnection) is { } c ? new DatabaseAdmin.Connection(c.Host, c.Port, c.Database, c.Username, c.Password) : null;

    private void StartTool(string job, Func<Task<(bool Ok, string? Message)>> work)
    {
        if (toolBusy) return;
        toolBusy = true;
        toolJob = job;
        toolResult = null;
        toolSteps.Clear();
        Task.Run(async () =>
        {
            try
            {
                var (ok, message) = await work();
                toolOk = ok;
                toolResult = message;
            }
            catch (Exception e)
            {
                toolOk = false;
                toolResult = e.Message;
            }
            finally
            {
                toolBusy = false;
                detected = null;
            }
        });
    }

    // The steps of the job running (or last run) under its own section.
    private void ToolProgress(string job)
    {
        if (toolJob != job) return;
        foreach (var s in toolSteps.ToList())
        {
            var (mark, color) = s.State switch
            {
                "done" => ("✓", Green), "failed" => ("✗", Red), "working" => ("…", Palette.Dim), _ => ("·", Palette.Dim),
            };
            Colored(color, mark);
            ImGui.SameLine();
            Ui.Wrapped(s.Detail.Length > 0 ? $"{s.Label}: {s.Detail}" : s.Label);
        }
        if (toolBusy && toolSteps.Count == 0) Colored(Dim, "Working...");
        if (!toolBusy && toolResult != null)
            using (ImRaii.PushColor(ImGuiCol.Text, toolOk ? Green : Amber)) Ui.Wrapped(toolResult);
    }

    private static string Password(string label, ref string value, float width)
    {
        ImGui.SetNextItemWidth(width);
        ImGui.InputText(label, ref value, 100, ImGuiInputTextFlags.Password);
        return value;
    }

    private void DrawDatabaseTools()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!ImGui.CollapsingHeader("Database tools")) return;
        toolDialog.Draw();
        var scale = ImGuiHelpers.GlobalScale;
        if (detected == null && !detecting)
        {
            detecting = true;
            Task.Run(async () =>
            {
                try
                {
                    detected = await WindowsSetup.Detect();
                    if (WindowsSetup.TailscaleHere)
                        tailscale = WindowsSetup.TailscaleStatus((await PgBackups.Run(WindowsSetup.TailscaleExe, new[] { "status", "--json" })).Stdout);
                }
                finally { detecting = false; }
            });
        }
        var (pgHere, tsHere) = detected ?? (false, false);
        using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
            Ui.Wrapped($"This PC: PostgreSQL {(pgHere ? "installed" : "not installed")}, Tailscale {(tsHere ? tailscale is { SignedIn: true } t ? $"signed in ({t.Address})" : "installed, not signed in" : "not installed")}.");
        using var disabled = ImRaii.Disabled(toolBusy);

        // ---- Set up this PC ----
        if (ImGui.TreeNode("Set up this PC"))
        {
            foreach (var r in WindowsSetup.Roles)
            {
                if (ImGui.RadioButton($"{r.Label}##role{r.Key}", setupRole == r.Key)) setupRole = r.Key;
                Colored(Dim, $"    {r.Detail}");
            }
            Ui.Checkbox("Every PC is on one home network (no Tailscale needed)", ref setupSameNetwork);
            var plan = WindowsSetup.SetupPlan(setupRole, setupSameNetwork, pgHere, tsHere)!;
            if (plan.CreateDatabase)
            {
                Password(pgHere ? "PostgreSQL's master password (chosen when it was installed)##setupMaster" : "Choose PostgreSQL's master password##setupMaster", ref setupMaster, 220 * scale);
                ImGui.SetNextItemWidth(160 * scale); ImGui.InputText("Database name##setupDb", ref setupDatabase, 63);
                ImGui.SetNextItemWidth(160 * scale); ImGui.InputText("Its login##setupUser", ref setupUser, 63);
                Password("Its password##setupAppPassword", ref setupAppPassword, 220 * scale);
                ImGui.SameLine();
                if (ImGui.SmallButton("Suggest one##setupSuggest")) setupAppPassword = WindowsSetup.SuggestPassword();
                ImGui.SetNextItemWidth(100 * scale); Ui.InputInt("Port##setupPort", ref setupPort, 0);
            }
            Colored(Dim, "Steps: " + string.Join("; ", SetupRuns.SetupSteps(plan).Select(s => s.Label).DefaultIfEmpty("nothing to install here")) +
                         (plan.AskConnection ? ". Then enter the database PC's details in the Database settings above." : "."));
            if (ImGui.Button("Set up this PC##setupRun"))
            {
                var input = new SetupRuns.SetupInput(setupRole, setupSameNetwork, setupMaster, setupDatabase.Trim(), setupUser.Trim(), setupAppPassword, setupPort);
                StartTool("setup", async () =>
                {
                    var r = await SetupRuns.RunSetup(input, new SetupRuns.Deps(), toolSteps, () => { }, c => { plugin.UseConnection(c); settings.LoadSavedCredential(); });
                    return (r.Ok, r.Ok ? "This PC is set up." + (plan.Tailscale ? " Sign in to Tailscale below on every PC, with the same account." : "") : r.Error);
                });
            }
            if (tsHere && tailscale is not { SignedIn: true })
            {
                ImGui.SameLine();
                if (ImGui.Button("Sign in to Tailscale"))
                    StartTool("setup", async () =>
                    {
                        var r = await WindowsSetup.TailscaleSignIn(setupRole is "dbhost" or "onepc", url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
                        return (r.Ok, r.Ok ? "Finish signing in in your browser." : r.Error);
                    });
            }
            ToolProgress("setup");
            ImGui.TreePop();
        }

        // ---- A new database ----
        if (pgHere && ImGui.TreeNode("New database on this PC"))
        {
            Ui.Wrapped("Makes a database and its own login in this PC's PostgreSQL, using its administrator login (the master password is only used for this and never saved), then uses it.");
            Password("PostgreSQL's master password##newMaster", ref adminPassword, 220 * scale);
            ImGui.SetNextItemWidth(100 * scale); Ui.InputInt("Port##newPort", ref localPort, 0);
            ImGui.SetNextItemWidth(160 * scale); ImGui.InputText("Database name##newDb", ref newDatabase, 63);
            ImGui.SetNextItemWidth(160 * scale); ImGui.InputText("Its login##newUser", ref newUser, 63);
            Password("Its password##newPassword", ref newPassword, 220 * scale);
            ImGui.SameLine();
            if (ImGui.SmallButton("Suggest one##newSuggest")) newPassword = WindowsSetup.SuggestPassword();
            if (ImGui.Button("Make it and use it"))
            {
                var (db, user, pw, master, port) = (newDatabase.Trim(), newUser.Trim(), newPassword, adminPassword, localPort);
                StartTool("new", async () =>
                {
                    await using var admin = await DatabaseAdmin.Open("localhost", port, "postgres", "postgres", master, 5);
                    if (await DatabaseAdmin.CreateDatabase(admin, db, user, pw) is { } why) return (false, why);
                    plugin.UseConnection(new DatabaseAdmin.Connection("localhost", port, db, user, pw));
                    settings.LoadSavedCredential();
                    return (true, $"\"{db}\" is made, and this PC uses it. The plugin sets up its tables at the next sync.");
                });
            }
            ToolProgress("new");
            ImGui.TreePop();
        }

        // ---- Move the database to this PC ----
        var saved = SavedConnection();
        if (plugin.Configuration.FleetStore == FleetStoreChoice.SharedDatabase && saved != null && !WindowsSetup.IsThisPc(saved.Host, WindowsSetup.Addresses())
            && ImGui.TreeNode("Move the database to this PC"))
        {
            Ui.Wrapped($"Copies the database from {saved.Host} to this PC, checks every table, then sends every PC and app here. Until that last step, anything that goes wrong puts everything back; the old database is never deleted.");
            var (ts, home) = WindowsSetup.PcAddresses(WindowsSetup.Addresses());
            var choices = (WindowsSetup.IsTailscaleAddress(saved.Host) || home.Count == 0 ? ts.Concat(home) : home.Concat(ts)).ToList();
            if (moveAddress.Length == 0 && choices.Count > 0) moveAddress = choices[0];
            ImGui.SetNextItemWidth(200 * scale);
            using (var combo = Ui.Combo("Address other PCs use to reach this one##moveAddress", moveAddress.Length > 0 ? moveAddress : "—"))
                if (combo)
                    foreach (var a in choices)
                        if (Ui.Selectable($"{a} ({(WindowsSetup.IsTailscaleAddress(a) ? "Tailscale" : "home network")})", a == moveAddress)) moveAddress = a;
            if (!pgHere) Ui.Checkbox($"Install {WindowsSetup.Postgres.Name} here first", ref moveInstall);
            else moveInstall = false;
            Password(moveInstall ? "Choose PostgreSQL's master password##moveMaster" : "This PC's PostgreSQL master password##moveMaster", ref adminPassword, 220 * scale);
            ImGui.SetNextItemWidth(100 * scale); Ui.InputInt("Port here##movePort", ref localPort, 0);
            if (ImGui.Button("Check first"))
                StartTool("move", async () =>
                {
                    moveChecked = await DatabaseAdmin.CheckOld(saved, new DatabaseAdmin.MoveDeps { IsThisPc = h => WindowsSetup.IsThisPc(h, WindowsSetup.Addresses()) });
                    return (moveChecked.Value.Ok, moveChecked.Value.Ok ? "Everything is ready to move." : moveChecked.Value.Error);
                });
            ImGui.SameLine();
            if (ImGui.Button("Move it to this PC"))
            {
                var (address, port, master, install) = (moveAddress, localPort, adminPassword, moveInstall);
                StartTool("move", async () =>
                {
                    var deps = new DatabaseAdmin.MoveDeps
                    {
                        IsThisPc = h => WindowsSetup.IsThisPc(h, WindowsSetup.Addresses()),
                        FindTools = () => PgBackups.FindTools(plugin.Backups.Load().Settings.PgBin, SharedBackups.ToolsDir),
                        DownloadPostgres = p => WindowsSetup.DownloadOnce(WindowsSetup.Postgres, WindowsSetup.DownloadFolder, p),
                        RecordInstalled = p => WindowsSetup.RecordInstalled(SharedBackups.Folder, p, false),
                    };
                    toolSteps.AddRange(DatabaseAdmin.MoveSteps(install));
                    var r = await DatabaseAdmin.MoveToThisPc(saved, address, port, "postgres", master, install, deps, toolSteps, () => { },
                        c => { plugin.UseConnection(c); settings.LoadSavedCredential(); });
                    return (r.Ok, r.Ok ? $"Moved. Every PC and app switches to {r.To} at its next sync or refresh." : r.Error);
                });
            }
            if (moveChecked is { } mc && toolJob == "move" && !toolBusy)
            {
                foreach (var b in mc.Blocking) using (ImRaii.PushColor(ImGuiCol.Text, Amber)) Ui.Wrapped(b);
                foreach (var n in mc.Notes) Colored(Dim, n);
            }
            ToolProgress("move");
            ImGui.TreePop();
        }

        // ---- Let other PCs connect ----
        if (pgHere && ImGui.TreeNode("Let other PCs connect"))
        {
            var ranges = WindowsSetup.RangesToAllow(WindowsSetup.Addresses());
            Ui.Wrapped($"Sets this PC's PostgreSQL to let in your Tailscale PCs and this PC's home network ({string.Join(", ", ranges)}), with the password, and opens Windows Firewall for its port to those only. Copies of PostgreSQL's settings are kept first.");
            ImGui.SetNextItemWidth(100 * scale); Ui.InputInt("Port##connectPort", ref localPort, 0);
            if (ImGui.Button("Let other PCs connect (Windows asks for permission)"))
            {
                var port = localPort;
                StartTool("connect", async () =>
                {
                    var r = await WindowsSetup.RunElevated(new[] { new WindowsSetup.Step("openConnections", Port: port, Ranges: ranges) }, false, _ => { });
                    if (r.Results is not { Count: > 0 } results) return (false, r.Error);
                    return (results[0].Ok, results[0].Ok ? $"Other PCs can connect. {string.Join(". ", results[0].Changed)}" : results[0].Message);
                });
            }
            ToolProgress("connect");
            ImGui.TreePop();
        }

        // ---- Leftovers from moving the database ----
        if (pgHere && ImGui.TreeNode("Leftovers from moving the database"))
        {
            Ui.Wrapped("What a move leaves in this PC's PostgreSQL: the copy that was here under the same name, or the copy left when the database moved away. The database in use is never one.");
            Password("PostgreSQL's master password##leftMaster", ref adminPassword, 220 * scale);
            ImGui.SetNextItemWidth(100 * scale); Ui.InputInt("Port##leftPort", ref localPort, 0);
            var current = saved != null && WindowsSetup.IsThisPc(saved.Host, WindowsSetup.Addresses()) ? saved.Database : null;
            Func<string, Task<Npgsql.NpgsqlConnection>> connectTo = name => DatabaseAdmin.Open("localhost", localPort, name, "postgres", adminPassword, 5);
            if (ImGui.Button("Find them"))
            {
                var (port, master) = (localPort, adminPassword);
                StartTool("leftovers", async () =>
                {
                    await using var admin = await DatabaseAdmin.Open("localhost", port, "postgres", "postgres", master, 5);
                    var (found, others) = await DatabaseAdmin.FindLeftovers(admin, connectTo, current);
                    leftovers = found;
                    postgresRemovable = DatabaseAdmin.PostgresRemovable(others, WindowsSetup.ReadInstalled(SharedBackups.Folder), port, current);
                    return (true, found.Count == 0 ? "No leftovers here." : $"{found.Count} found.");
                });
            }
            foreach (var l in leftovers ?? new List<DatabaseAdmin.Leftover>())
            {
                Ui.Text($"{l.Name}: {(l.Kind == "kept" ? "the copy that was here before a move" : $"left here when it moved to {l.MovedTo}")}, {l.Bytes / 1048576.0:0} MB");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Remove##left{l.Name}"))
                {
                    var (port, master, name) = (localPort, adminPassword, l.Name);
                    StartTool("leftovers", async () =>
                    {
                        await using var admin = await DatabaseAdmin.Open("localhost", port, "postgres", "postgres", master, 5);
                        var why = await DatabaseAdmin.RemoveLeftover(admin, name, connectTo, current);
                        leftovers = leftovers?.Where(x => x.Name != name).ToList();
                        return (why == null, why ?? $"\"{name}\" is removed.");
                    });
                }
            }
            if (postgresRemovable && leftovers is { Count: 0 })
            {
                if (ImGui.Button("Remove PostgreSQL too (Windows asks for permission)"))
                {
                    var port = localPort;
                    StartTool("leftovers", async () =>
                    {
                        var r = await WindowsSetup.RunElevated(new[] { new WindowsSetup.Step("removePostgres", Port: port) }, false, _ => { });
                        if (r.Results is not { Count: > 0 } results) return (false, r.Error);
                        if (results[0].Ok) WindowsSetup.ForgetInstalled(SharedBackups.Folder, true, false);
                        return (results[0].Ok, results[0].Message);
                    });
                }
            }
            ToolProgress("leftovers");
            ImGui.TreePop();
        }

        // ---- What another PC needs ----
        if (saved != null && ImGui.TreeNode("What another PC needs"))
        {
            var addresses = WindowsSetup.Addresses();
            var (ts, home) = WindowsSetup.PcAddresses(addresses);
            var local = WindowsSetup.IsThisPc(saved.Host, addresses);
            var choices = local ? ts.Select(a => $"{a} (Tailscale)").Concat(home.Select(a => $"{a} (home network)")).ToList() : new List<string> { $"{saved.Host} (same as this PC)" };
            Ui.Wrapped($"In the plugin's (or the app's) database settings on the other PC: address {string.Join(" or ", choices.DefaultIfEmpty("(none found: is Tailscale signed in?)"))}, port {saved.Port}, database {saved.Database}, login {saved.User}, and the same password.");
            Colored(Dim, "The plugin's address for Dalamud's Custom Plugin Repositories: https://raw.githubusercontent.com/CrownedPandaXIV/XIVFleetCompanion-Releases/main/repo.json");
            ImGui.TreePop();
        }

        // ---- Found a problem? ----
        if (ImGui.TreeNode("Found a problem?"))
        {
            Ui.Wrapped("Report it on GitHub, with the details below (they hold no password).");
            var details = string.Join("\n", new[]
            {
                $"Plugin {Plugin.VersionText} on {Environment.OSVersion}",
                $"Fleet data: {plugin.Configuration.FleetStore}",
                $"Database: {(saved == null ? "none saved" : $"{saved.Host}:{saved.Port}/{saved.Database} as {saved.User}")}",
                $"PostgreSQL here: {pgHere}, Tailscale here: {tsHere}",
                $"Last sync: {plugin.Configuration.LastSyncTimestamp?.ToString("u") ?? "never"}",
                $"Sync problem: {plugin.SyncProblem ?? "none"}",
                $"Database version: {plugin.Keeper.StatusText}",
            });
            if (ImGui.Button("Copy the details")) ImGui.SetClipboardText(details);
            ImGui.SameLine();
            if (ImGui.Button("Report a problem"))
                Process.Start(new ProcessStartInfo("https://github.com/CrownedPandaXIV/XIVFleetCompanion-App/issues/new/choose") { UseShellExecute = true });
            Colored(Dim, details);
            ImGui.TreePop();
        }

        // ---- Remove everything ----
        if (ImGui.TreeNode("Remove everything from this PC"))
        {
            if (removalPlan == null && !toolBusy)
            {
                var c = saved;
                StartTool("remove-plan", async () =>
                {
                    var addresses = WindowsSetup.Addresses();
                    var installed = WindowsSetup.ReadInstalled(SharedBackups.Folder);
                    var dbHere = c != null && WindowsSetup.IsThisPc(c.Host, addresses);
                    var (pg, tsh) = await WindowsSetup.Detect();
                    removalPlan = DatabaseAdmin.Plan(new DatabaseAdmin.RemovalFacts(installed, c, dbHere, pg, tsh, new List<string>(), null));
                    return (true, null);
                });
            }
            if (removalPlan is { } plan)
            {
                Ui.Text("Removes:");
                foreach (var r in plan.Removes) Ui.Bullet(r);
                Ui.Text("Leaves:");
                foreach (var k in plan.Keeps) Ui.Bullet(k);
                if (plan.OtherPcsLoseDatabase) using (ImRaii.PushColor(ImGuiCol.Text, Amber)) Ui.Wrapped("Your other PCs use this database: they lose it too.");
                if (plan.OfferBackup)
                {
                    Ui.Checkbox("Make a final backup first, into", ref removeBackup);
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(260 * scale);
                    ImGui.InputText("##removeFolder", ref removeBackupFolder, 260);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Choose...##removeFolderPick")) toolDialog.OpenFolderDialog("Folder for the final backup", (ok, path) => { if (ok) removeBackupFolder = path; });
                }
                if (plan.CanRemoveTailscale) Ui.Checkbox("Remove Tailscale too", ref removeTailscale);
                if (plan.NeedsMasterPassword) Password("PostgreSQL's master password##removeMaster", ref adminPassword, 220 * scale);
                ImGui.SetNextItemWidth(120 * scale);
                ImGui.InputText($"Type {DatabaseAdmin.ConfirmWord} to confirm##removeConfirm", ref removeConfirm, 10);
                using (ImRaii.Disabled(removeConfirm.Trim() != DatabaseAdmin.ConfirmWord))
                {
                    if (ImGui.Button("Remove everything"))
                    {
                        var input = new SetupRuns.RemoveInput(removeBackup, removeBackupFolder, removeTailscale, adminPassword, removeConfirm);
                        StartTool("remove", async () =>
                        {
                            var b = plugin.Backups;
                            var r = await SetupRuns.RemoveEverything(input, plan, new SetupRuns.Deps(), toolSteps, () => { },
                                async folder =>
                                {
                                    var (tools, error) = await PgBackups.FindTools(b.Load().Settings.PgBin, SharedBackups.ToolsDir);
                                    var login = saved == null ? null : new PgLogin(saved.Host, saved.Port, saved.Database, saved.User, saved.Password);
                                    if (tools == null || login == null) return (false, error ?? "No database connection.");
                                    var made = await PgBackups.BackupNow(tools, login, PgBackups.Settings.Defaults with { Folder = folder, Keep = int.MaxValue }, DateTime.Now);
                                    return (made.Ok, made.Ok ? $"Saved as {made.File}" : made.Message);
                                },
                                async () =>
                                {
                                    var q = await PgBackups.Run("schtasks.exe", new[] { "/Query", "/TN", SharedBackups.AppTaskName });
                                    if (!q.Ok) return (true, null);
                                    var d = await PgBackups.Run("schtasks.exe", new[] { "/Delete", "/TN", SharedBackups.AppTaskName, "/F" });
                                    return d.Ok ? (true, null) : (false, $"Windows could not remove the scheduled task: {(d.Stderr + d.Stdout).Trim()}");
                                },
                                () =>
                                {
                                    plugin.ForgetConnection();
                                    var s = b.Load().Settings;
                                    b.Save(s with { Enabled = false });
                                });
                            removalPlan = null;
                            return (r.Ok, r.Ok ? "Everything is removed. Remove the plugin in the game's plugin list if you're done with it." : r.Error);
                        });
                    }
                }
            }
            ToolProgress("remove");
            ImGui.TreePop();
        }
    }
}
