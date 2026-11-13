using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace XIVFleetCompanion.Windows;

// Settings → Backups for a shared database, as the app's: where they go, a second copy, how many to keep, the daily one,
// Back up now, the list with Test and Restore, Restore from a file, and Get the backup tools. (The fleet file's backups
// are on the Status page.)
public partial class MainWindow
{
    private readonly FileDialogManager backupDialog = new();
    private PgBackups.Settings? backupDraft;
    private string backupDraftError = "";
    private string? confirmRestore;
    private bool restoreAsked;

    private void DrawSharedBackups()
    {
        if (plugin.Configuration.FleetStore != FleetStoreChoice.SharedDatabase) return;
        if (!ImGui.CollapsingHeader("Backups of the shared database")) return;
        var b = plugin.Backups;
        backupDialog.Draw();
        if (!b.ToolsLooked) b.FindTools();
        b.LookForAppTask();
        var (saved, last) = b.Load();
        var d = backupDraft ??= saved;
        var scale = ImGuiHelpers.GlobalScale;

        Ui.Wrapped("Copies of the whole database, made with PostgreSQL's own tools, the same backups the app makes (on a PC with both, they share them).");

        // The tools.
        if (b.Tools is { } tools)
            using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                Ui.Wrapped($"Backup tools: {tools.Version}{(tools.Bin.Length > 0 ? $" in {tools.Bin}" : "")}.");
        else if (b.ToolsError != null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Amber)) Ui.Wrapped(b.ToolsError);
            using (ImRaii.Disabled(b.Busy))
            {
                if (ImGui.Button("Get the backup tools"))
                {
                    var (connection, _) = PostgresWriter.BuildConnectionString(plugin.Configuration.UseRemoteConnection);
                    if (connection != null) b.GetTools(connection);
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Downloads PostgreSQL's backup tools (pg_dump and pg_restore) for the database's version from PostgreSQL's maker, checks the download and keeps only those tools. Nothing is installed.");
        }

        // The settings.
        ImGui.Spacing();
        var folder = d.Folder;
        ImGui.SetNextItemWidth(320 * scale);
        if (ImGui.InputText("Backup folder##backupFolder", ref folder, 260)) d = backupDraft = d with { Folder = folder };
        ImGui.SameLine();
        if (ImGui.Button("Choose...##backupFolderPick"))
            backupDialog.OpenFolderDialog("Folder for the backups", (ok, path) => { if (ok) backupDraft = (backupDraft ?? saved) with { Folder = path }; });
        var copy = d.CopyFolder;
        ImGui.SetNextItemWidth(320 * scale);
        if (ImGui.InputText("Second copy (optional)##backupCopy", ref copy, 260)) d = backupDraft = d with { CopyFolder = copy };
        ImGui.SameLine();
        if (ImGui.Button("Choose...##backupCopyPick"))
            backupDialog.OpenFolderDialog("Folder for a second copy (another PC or drive)", (ok, path) => { if (ok) backupDraft = (backupDraft ?? saved) with { CopyFolder = path }; });
        var keep = d.Keep;
        ImGui.SetNextItemWidth(100 * scale);
        if (Ui.InputInt("Backups kept in each folder##backupKeep", ref keep, 1)) d = backupDraft = d with { Keep = keep };
        var enabled = d.Enabled;
        if (Ui.Checkbox("Back up every day at##backupDaily", ref enabled)) d = backupDraft = d with { Enabled = enabled };
        ImGui.SameLine();
        var time = d.Time;
        ImGui.SetNextItemWidth(70 * scale);
        if (ImGui.InputText("##backupTime", ref time, 5)) d = backupDraft = d with { Time = time };
        using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
            Ui.Wrapped(b.AppTask == true
                ? "The app's scheduled task makes the daily backup on this PC (the game leaves it to the app)."
                : "Made by a running game on this PC when it's due (or as soon as a game starts after that time). With the app on this PC, its scheduled task can make it instead, with no game running.");
        ImGui.SetNextItemWidth(320 * scale);
        var bin = d.PgBin;
        if (ImGui.InputText("PostgreSQL's bin folder (only if not found)##backupBin", ref bin, 260)) d = backupDraft = d with { PgBin = bin };
        var changed = d != saved;
        using (ImRaii.Disabled(!changed))
        {
            if (ImGui.Button("Save backup settings"))
            {
                backupDraftError = b.Save(d) ?? "";
                if (backupDraftError.Length == 0) backupDraft = null;
            }
        }
        if (changed)
        {
            ImGui.SameLine();
            if (ImGui.Button("Undo##backupUndo")) { backupDraft = null; backupDraftError = ""; }
        }
        if (backupDraftError.Length > 0)
            using (ImRaii.PushColor(ImGuiCol.Text, Amber)) Ui.Wrapped(backupDraftError);

        // Back up now, and what happened last.
        ImGui.Spacing();
        using (ImRaii.Disabled(b.Busy || saved.Folder.Length == 0))
        {
            if (ImGui.Button("Back up now")) b.BackUpNow();
        }
        if (saved.Folder.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Open the folder##backupOpen") && Directory.Exists(saved.Folder))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{saved.Folder}\"") { UseShellExecute = true });
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(b.Busy))
        {
            if (ImGui.Button("Restore from a file..."))
                backupDialog.OpenFileDialog("Choose a backup (.dump)", ".dump,.*", (ok, paths) => { if (ok && paths.Count > 0) confirmRestore = paths[0]; }, 1, null);
        }
        if (b.Status != null)
            using (ImRaii.PushColor(ImGuiCol.Text, b.Busy ? Palette.Dim : b.StatusOk ? Green : Amber)) Ui.Wrapped(b.Status);
        else if (last != null)
            using (ImRaii.PushColor(ImGuiCol.Text, last.Ok ? Palette.Dim : Amber))
                Ui.Wrapped($"Last backup {DateTimeOffset.FromUnixTimeMilliseconds(last.At).LocalDateTime:g}: {last.Message}");
        if (b.TestRows is { Count: > 0 } rows && !b.Busy)
            using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
                Ui.Wrapped(string.Join(" · ", rows.Where(r => r.Live != null).Select(r => $"{r.Table.Replace("companion_", "")}: {r.Restored?.ToString("N0", CultureInfo.CurrentCulture) ?? "missing"} of {r.Live:N0}")));

        // The backups there are.
        var list = PgBackups.List(saved.Folder);
        if (saved.Folder.Length > 0 && list.Count == 0) Colored(Dim, "No backups in that folder yet.");
        if (list.Count > 0)
        {
            using var table = Ui.Table("##sharedBackups", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
            if (table)
            {
                foreach (var h in new[] { "Taken", "Size", "", "" }) Ui.SetupColumn(h);
                Ui.HeadersRow();
                foreach (var backup in list)
                {
                    using var id = ImRaii.PushId(backup.Name);
                    Ui.NextRow();
                    Ui.NextColumn();
                    Ui.Text(backup.TakenAt.ToString("g", CultureInfo.CurrentCulture));
                    Ui.NextColumn();
                    TableCells.Right(backup.Size is long size ? $"{size / 1048576.0:0.0} MB" : "—");
                    Ui.NextColumn();
                    using (ImRaii.Disabled(b.Busy))
                    {
                        if (ImGui.SmallButton("Test")) b.Test(backup.File);
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Loads it into a scratch database and compares its tables with the database's (the database is only read).");
                    Ui.NextColumn();
                    using (ImRaii.Disabled(b.Busy))
                    {
                        if (ImGui.SmallButton("Restore")) confirmRestore = backup.File;
                    }
                }
            }
        }

        // Restoring asks first.
        if (confirmRestore != null && !restoreAsked)
        {
            ImGui.OpenPopup("restoreSharedBackup");
            restoreAsked = true;
        }
        using var popup = ImRaii.Popup("restoreSharedBackup");
        if (!popup)
        {
            confirmRestore = null;
            restoreAsked = false;
            return;
        }
        Ui.Text(Path.GetFileName(confirmRestore ?? ""));
        Ui.Wrapped("The whole database goes back to how it was then, for every PC and the app. The database as it is now is backed up first, so this can be undone the same way.");
        if (ImGui.Button("Restore"))
        {
            if (confirmRestore != null) b.Restore(confirmRestore);
            confirmRestore = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            confirmRestore = null;
            ImGui.CloseCurrentPopup();
        }
    }
}
