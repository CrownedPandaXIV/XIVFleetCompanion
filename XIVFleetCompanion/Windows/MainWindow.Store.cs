using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
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
    private FileInfo? newestBackup;
    private DateTime newestBackupAt = DateTime.MinValue;

    // The question, in place of every page until it's answered (the Status and Settings pages still open).
    private bool NeedsStoreChoice => plugin.Configuration.FleetStore == FleetStoreChoice.NotChosen && shown is not (Tab.Status or Tab.Settings);

    private void DrawStoreChoice()
    {
        ImGui.Spacing();
        ImGui.Text("Where should your fleet data live?");
        using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
            ImGui.TextWrapped("The plugin keeps your characters, subs, retainers and their history somewhere it can read them back. Choose one; " +
                              "you can move your fleet to the other later (Settings → Move your fleet). Every game on this PC uses the same.");
        ImGui.Spacing();

        var cardWidth = Math.Max(240f * ImGuiHelpers.GlobalScale, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f);
        var side = ImGui.GetContentRegionAvail().X >= cardWidth * 2 + ImGui.GetStyle().ItemSpacing.X;

        using (var card = ImRaii.Child("##thispc", new Vector2(cardWidth, 230f * ImGuiHelpers.GlobalScale), true))
        {
            if (card.Success)
            {
                ImGui.Text("Just this PC");
                ImGui.Spacing();
                ImGui.TextWrapped("Every game on this PC keeps your fleet in one file. Nothing else to install or set up.");
                ImGui.Spacing();
                using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                    ImGui.TextWrapped("The XIV Fleet Companion app can't open the file, so you see your fleet here, in the game. " +
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
                ImGui.Text("Shared between PCs");
                ImGui.Spacing();
                ImGui.TextWrapped("Your fleet lives in a database that several PCs and the XIV Fleet Companion app can all use.");
                ImGui.Spacing();
                using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                    ImGui.TextWrapped("Set the database up with the app (its Database Helper is enough on the PC that keeps it), " +
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
                ImGui.TextWrapped(storeProblem);
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

    // The Status page's database lines on the fleet file.
    private void DrawFileStatus()
    {
        ImGui.BulletText("Fleet data: Just this PC");
        using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
        {
            ImGui.TextWrapped($"File: {FileDatabase.DefaultPath}");
            if (DateTime.UtcNow - newestBackupAt > TimeSpan.FromSeconds(5))
            {
                newestBackupAt = DateTime.UtcNow;
                try { newestBackup = FileDatabase.Backups(FileDatabase.DefaultPath).FirstOrDefault(); }
                catch (IOException) { newestBackup = null; }
            }
            var newest = newestBackup;
            ImGui.TextWrapped(newest == null
                ? "Backups: none yet (one is made the first time the game reads or syncs each day)."
                : $"Backups: the newest is from {newest.LastWriteTime:g}, in {FileDatabase.BackupFolder(FileDatabase.DefaultPath)} (the last {FileDatabase.BackupsKept} are kept).");
        }
        if (plugin.Keeper.BackupProblem is { } problem)
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                ImGui.TextWrapped($"The last backup didn't work: {problem}");

        using (ImRaii.Disabled(backingUp))
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
            ImGui.TextWrapped(backupResult);
        }
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
            ImGui.TextWrapped("Your fleet is in a file on this PC. Moving it to a shared database lets your other PCs and the XIV Fleet Companion app use it too.");
            using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                ImGui.TextWrapped("1. Set up an empty database with the app (its Database Helper is enough). 2. Enter its details below and save them. " +
                                  "3. Move. Every table is copied and checked before anything switches; the file stays as it is, as a backup.");
            ImGui.Spacing();
            settings.DrawDatabaseConnection();
        }
        else
        {
            ImGui.TextWrapped("Your fleet is in a shared database. Moving it to this PC keeps it in one file here instead, with nothing else to install: " +
                              "for when this is the only PC you play on.");
            using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                ImGui.TextWrapped("Every table is copied and checked before anything switches. The shared database is left as it is. " +
                                  "The XIV Fleet Companion app can't open the file, so afterwards you see your fleet in the game.");
            // Other PCs still syncing to the database would carry on there, not in this PC's file.
            reader.ReadIfDue(withLists: true);
            var others = reader.Pcs.Data?.Where(p => !string.Equals(p.Pc, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                                                     && p.LastSync is { } at && DateTime.Now - at < TimeSpan.FromDays(1)).Select(p => p.Pc).Distinct().ToList();
            if (others is { Count: > 0 })
                using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                    ImGui.TextWrapped($"{string.Join(", ", others)} synced to the shared database in the last day. After the move, {(others.Count == 1 ? "it keeps" : "they keep")} " +
                                      "syncing there, not to this PC: only move if you've stopped playing there.");
        }

        ImGui.Spacing();
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
            using (ImRaii.PushColor(ImGuiCol.Text, plugin.MoveFailed ? Amber : Palette.Dim))
                ImGui.TextWrapped(status);
    }
}
