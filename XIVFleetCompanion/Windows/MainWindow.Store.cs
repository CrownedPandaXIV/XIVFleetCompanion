using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Windows;

// 2.2 "Just this PC": the question a new setup is asked first (where the fleet lives), and the fleet file's part of the
// Status page (where it is, its backups, Back up now). A setup from before 2.2 already uses a shared database and is
// never asked.
public partial class MainWindow
{
    private string? storeProblem;

    // True when the app also shows this fleet (Shared between PCs); on Just this PC the game is the only place, so text
    // that points at the app says something else.
    private bool WithApp => !plugin.OnThisPc;

    // Where the Warnings are changed.
    private string WarningsWhere => WithApp ? "set in Settings, here or in the app" : "set in Settings";
    private bool backingUp;
    private string? backupResult;
    // The newest backup, looked up every few seconds rather than every frame.
    private List<FileInfo> backupList = new();
    private DateTime newestBackupAt = DateTime.MinValue;

    // The question, in place of every page until it's answered (the Status and Settings pages still open).
    private bool NeedsStoreChoice => plugin.Configuration.FleetStore == FleetStoreChoice.NotChosen && shown is not (Tab.Status or Tab.Settings);

    private void DrawStoreChoice()
    {
        ImGui.Spacing();
        Ui.Text("Where should your fleet data live?");
        using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
            Ui.Wrapped("The plugin keeps your characters, subs, retainers and their history somewhere it can read them back. Choose one; " +
                              "you can move your fleet to the other later (Settings → Move your fleet). Every game on this PC uses the same.");
        ImGui.Spacing();

        var cardWidth = Math.Max(240f * ImGuiHelpers.GlobalScale, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f);
        var side = ImGui.GetContentRegionAvail().X >= cardWidth * 2 + ImGui.GetStyle().ItemSpacing.X;

        using (var card = ImRaii.Child("##thispc", new Vector2(cardWidth, 230f * ImGuiHelpers.GlobalScale), true))
        {
            if (card.Success)
            {
                Ui.Text("Just this PC");
                ImGui.Spacing();
                Ui.Wrapped("Every game on this PC keeps your fleet in one file. Nothing else to install or set up.");
                ImGui.Spacing();
                using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                    Ui.Wrapped("You see your fleet here, in the game, and in the XIV Fleet Companion app on this PC (1.10 or later). " +
                                      "Backed up once a day; the last 7 are kept.");
                ImGui.Spacing();
                if (ImGui.Button("Keep it on this PC")) ChooseThisPc();
            }
        }
        if (side) ImGui.SameLine();
        using (var card = ImRaii.Child("##shared", new Vector2(cardWidth, 230f * ImGuiHelpers.GlobalScale), true))
        {
            if (card.Success)
            {
                Ui.Text("Shared between PCs");
                ImGui.Spacing();
                Ui.Wrapped("Your fleet lives in a database that several PCs and the XIV Fleet Companion app can all use.");
                ImGui.Spacing();
                using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                    Ui.Wrapped("Set the database up with the app (its Database Helper is enough on the PC that keeps it), " +
                                      "then enter the same details in this plugin's Settings.");
                ImGui.Spacing();
                if (ImGui.Button("Use a shared database"))
                {
                    plugin.Configuration.FleetStore = FleetStoreChoice.SharedDatabase;
                    plugin.Configuration.Save();
                    Plugin.SaveChoiceForPc(plugin.Configuration);
                    storeProblem = null;
                    Show(Tab.Settings);
                }
            }
        }

        if (storeProblem != null)
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                Ui.Wrapped(storeProblem);
        }
    }

    // A file in a cloud or network folder is refused before anything is chosen, with why.
    private void ChooseThisPc()
    {
        var why = FileDatabase.WhyNotHere(FileDatabase.DefaultPath);
        if (why != null)
        {
            storeProblem = why;
            return;
        }
        storeProblem = null;
        plugin.Configuration.FleetStore = FleetStoreChoice.ThisPc;
        // Nothing else to set up, so syncing starts straight away.
        plugin.Configuration.Enabled = true;
        plugin.Configuration.Save();
        // Every other game on this PC uses the file too, without being asked.
        Plugin.SaveChoiceForPc(plugin.Configuration);
        reader.ReadIfDue(everything: true);
        Show(Tab.Summary);
    }

    // The Status page's lines on the fleet file: where it is, whether it's whole (checked weekly; Check now), and its
    // backups (Back up now; Restore any of them).
    private string? restoreArmed;
    private DateTime restoreArmedUntil = DateTime.MinValue;

    private void DrawFileStatus()
    {
        Ui.Bullet("Fleet data: Just this PC");
        using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
            Ui.Wrapped($"File: {FileDatabase.DefaultPath}");

        // Health.
        var health = plugin.Keeper.Health;
        using (ImRaii.PushColor(ImGuiCol.Text, health is { Ok: false } ? Amber : Palette.Dim))
            Ui.Wrapped(health?.Text ?? "Not checked for damage yet (once a week, the first time the game reads the file).");
        using (ImRaii.Disabled(plugin.Moving))
        {
            if (ImGui.Button("Check now")) plugin.CheckFileNow();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("SQLite's own check that the file isn't damaged (it also runs once a week). Games carry on meanwhile.");

        // Backups.
        if (DateTime.UtcNow - newestBackupAt > TimeSpan.FromSeconds(5))
        {
            newestBackupAt = DateTime.UtcNow;
            try { backupList = FileDatabase.Backups(FileDatabase.DefaultPath); }
            catch (IOException) { backupList = new List<FileInfo>(); }
        }
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
            Ui.Wrapped(backupList.Count == 0
                ? "Backups: none yet (one is made the first time the game reads or syncs each day)."
                : $"Backups: {backupList.Count}, the newest from {backupList[0].LastWriteTime:g}, in {FileDatabase.BackupFolder(FileDatabase.DefaultPath)} " +
                  $"(the last {FileDatabase.BackupsKept} are kept).");
        if (plugin.Keeper.BackupProblem is { } problem)
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                Ui.Wrapped($"The last backup didn't work: {problem}");

        using (ImRaii.Disabled(backingUp || plugin.Moving))
        {
            if (ImGui.Button("Back up now"))
            {
                backingUp = true;
                backupResult = "Backing up...";
                Task.Run(async () =>
                {
                    try
                    {
                        await using var conn = await FileDatabase.OpenAsync(FileDatabase.DefaultPath);
                        var made = await FileDatabase.BackUpAsync(conn, FileDatabase.DefaultPath, DateTime.Now);
                        backupResult = $"Backed up to {Path.GetFileName(made)}.";
                        newestBackupAt = DateTime.MinValue;
                    }
                    catch (Exception ex)
                    {
                        backupResult = $"Couldn't back up: {ex.Message}";
                    }
                    finally
                    {
                        backingUp = false;
                    }
                });
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies the fleet file into the Backups folder beside it now. Safe while games are syncing.");
        if (backupResult != null)
        {
            ImGui.SameLine();
            Ui.Wrapped(backupResult);
        }

        if (backupList.Count > 0 && ImGui.TreeNode($"Restore a backup##restore"))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                Ui.Wrapped("Puts the fleet back as it was in that backup. Games can keep running: they see it straight away. " +
                                  "Your fleet as it is now is backed up first, so a restore can be undone the same way.");
            foreach (var backup in backupList)
            {
                var armed = restoreArmed == backup.FullName && DateTime.UtcNow < restoreArmedUntil;
                using (ImRaii.Disabled(plugin.Moving))
                {
                    if (ImGui.Button((armed ? "Click again to restore" : "Restore") + $"##{backup.Name}"))
                    {
                        if (armed)
                        {
                            restoreArmed = null;
                            plugin.RestoreBackup(backup.FullName);
                        }
                        else
                        {
                            restoreArmed = backup.FullName;
                            restoreArmedUntil = DateTime.UtcNow.AddSeconds(5);
                        }
                    }
                }
                ImGui.SameLine();
                Ui.Text($"{backup.LastWriteTime:g}");
                ImGui.SameLine();
                Colored(Dim, $"{backup.Name}, {backup.Length / 1024.0 / 1024.0:0.0} MB");
            }
            ImGui.TreePop();
        }
        if (plugin.FileActionStatus is { } done)
            using (ImRaii.PushColor(ImGuiCol.Text, plugin.FileActionFailed ? Amber : Palette.Dim))
                Ui.Wrapped(done);
    }

    // ---- Moving the fleet (Settings) ----

    private DateTime moveArmedUntil = DateTime.MinValue;

    private void DrawMoveFleet()
    {
        if (plugin.Configuration.FleetStore == FleetStoreChoice.NotChosen) return;
        if (!ImGui.CollapsingHeader("Move your fleet")) return;
        var toShared = plugin.OnThisPc;
        if (toShared)
        {
            Ui.Wrapped("Your fleet is in a file on this PC. Moving it to a shared database lets your other PCs and the XIV Fleet Companion app use it too.");
            using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                Ui.Wrapped("1. Set up an empty database with the app (its Database Helper is enough). 2. Enter its details below and save them. " +
                                  "3. Move. Every table is copied and checked before anything switches; the file stays as it is, as a backup.");
            ImGui.Spacing();
            settings.DrawDatabaseConnection();
        }
        else
        {
            Ui.Wrapped("Your fleet is in a shared database. Moving it to this PC keeps it in one file here instead, with nothing else to install: " +
                              "for when this is the only PC you play on.");
            using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                Ui.Wrapped("Every table is copied and checked before anything switches. The shared database is left as it is. " +
                                  "Afterwards you see your fleet in the game, and in the XIV Fleet Companion app on this PC (1.10 or later).");
            // Other PCs still syncing to the database would carry on there, not in this PC's file.
            reader.ReadIfDue(withLists: true);
            var others = reader.Pcs.Data?.Where(p => !string.Equals(p.Pc, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                                                     && p.LastSync is { } at && DateTime.Now - at < TimeSpan.FromDays(1)).Select(p => p.Pc).Distinct().ToList();
            if (others is { Count: > 0 })
                using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                    Ui.Wrapped($"{string.Join(", ", others)} synced to the shared database in the last day. After the move, {(others.Count == 1 ? "it keeps" : "they keep")} " +
                                      "syncing there, not to this PC: only move if you've stopped playing there.");
        }

        ImGui.Spacing();
        // A trial first: everything a move does, checked, then undone.
        using (ImRaii.Disabled(plugin.Moving))
        {
            if (ImGui.Button("Try the move first")) plugin.MoveFleet(toShared, trial: true);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies your whole fleet and checks every row, exactly as a move does, then undoes it all. Nothing switches and nothing is lost. " +
                             "It tells you whether the move would work and how long it takes.");
        ImGui.SameLine();
        var armed = DateTime.UtcNow < moveArmedUntil;
        var label = toShared ? "Move my fleet to the shared database" : "Move my fleet to this PC";
        using (ImRaii.Disabled(plugin.Moving))
        {
            if (ImGui.Button(armed ? "Click again to move" : label))
            {
                if (armed)
                {
                    moveArmedUntil = DateTime.MinValue;
                    plugin.MoveFleet(toShared);
                }
                else
                    moveArmedUntil = DateTime.UtcNow.AddSeconds(5);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Syncing waits while it copies. If anything goes wrong, nothing changes.");
        if (plugin.MoveStatus is { } status)
            using (ImRaii.PushColor(ImGuiCol.Text, plugin.MoveFailed ? Amber : plugin.MoveWasTrial ? Palette.Good : Palette.Dim))
                Ui.Wrapped(status);
    }
}
