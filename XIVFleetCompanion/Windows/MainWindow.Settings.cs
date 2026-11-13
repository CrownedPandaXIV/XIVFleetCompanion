using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Windows;

// The Settings page's part shared with the app (saved in the database, so the app and every PC's plugin use the same):
// What do you run?, Live Market Prices, the repair kit cost and the Warnings, as the app's Settings → General. Each is
// saved with the same checks as the app's (FleetEdits), and read back through fleet_settings_view.
public partial class MainWindow
{
    private static readonly (string Value, string Label)[] PlayChoices =
    {
        ("auto", "Work it out from my characters"), ("both", "Subs and retainers"), ("subs", "Subs only"), ("retainers", "Retainers only"),
    };

    // The boxes as typed; they follow what's read until changed here, and again once saved.
    private int kitsBox, ceruleumBox, houseBox = 7, notSyncedBox = 30, costBox;
    private bool warningsTyped, costTyped;
    private DateTime? settingsShownAt;
    // A choice just saved here, shown until the next read has it.
    private (string? Play, bool? Live, DateTime At) settingsPending;

    private void DrawSharedSettings()
    {
        if (!ImGui.CollapsingHeader(WithApp ? "Shared with the app###sharedSettings" : "Fleet settings###sharedSettings", ImGuiTreeNodeFlags.DefaultOpen)) return;
        Colored(Dim, WithApp ? "Saved in the database: the app and every PC's plugin use the same." : "Saved in the fleet file: every game on this PC uses the same.");
        var read = reader.Settings;
        if (read.Data is not { } st)
        {
            Colored(read.Error != null ? Amber : Dim, read.Error ?? "Reading...");
            return;
        }
        if (read.Error != null) Colored(Amber, read.Error);
        if (settingsShownAt != read.At)
        {
            if (!warningsTyped)
                (kitsBox, ceruleumBox, houseBox, notSyncedBox) = ((int)st.RepairKitThreshold, (int)st.CeruleumThreshold, st.HouseWarnDays, st.NotSyncedMinutes);
            if (!costTyped) costBox = (int)(st.RepairKitCost ?? 0);
            settingsShownAt = read.At;
        }
        var pending = DateTime.Now - settingsPending.At < TimeSpan.FromSeconds(15) && !(read.At > settingsPending.At);
        var width = 110 * ImGuiHelpers.GlobalScale;

        // What do you run?
        ImGui.Spacing();
        Ui.Text("What do you run?");
        ImGui.SameLine();
        var choice = pending && settingsPending.Play != null ? settingsPending.Play : st.PlayChoice;
        ImGui.SetNextItemWidth(230 * ImGuiHelpers.GlobalScale);
        using (var combo = Ui.Combo("##playStyle", Array.Find(PlayChoices, c => c.Value == choice).Label ?? choice))
        {
            if (combo)
                foreach (var (value, label) in PlayChoices)
                    if (Ui.Selectable(label, value == choice) && value != choice)
                    {
                        settingsPending = (value, null, DateTime.Now);
                        reader.Edit("What do you run?", c => FleetEdits.SavePlayStyle(c, value));
                    }
        }
        var names = st.PlayStyle switch { "subs" => "subs only", "retainers" => "retainers only", _ => "subs and retainers" };
        Colored(Dim, $"Your characters have {st.SubsBuilt} sub{(st.SubsBuilt == 1 ? "" : "s")} built and {st.RetainersWithClass} retainer{(st.RetainersWithClass == 1 ? "" : "s")} with a class. Showing: {names}.");
        Colored(Dim, "Pages for what you don't run are left out: with subs only, Retainers; with retainers only, Salvage, Subs, Supplies, Parts and Route Optimization.");

        // Live Market Prices.
        ImGui.Spacing();
        var live = pending && settingsPending.Live is { } l ? l : st.LivePrices;
        if (Ui.Checkbox("Use live market prices from Universalis", ref live))
        {
            var on = live;
            settingsPending = (null, on, DateTime.Now);
            reader.Edit("Live Market Prices", async c => { await FleetEdits.SaveLivePrices(c, on); return null; });
        }
        Hover("Universalis (universalis.app) collects market board prices from players. With this on, the cheapest listing of each item on the "
              + "character's world, data center and region is shown next to the vendor prices: on Income, Gil & Market and the Parts page's "
              + "Shopping List. Only item numbers and world, data center or region names are sent. Prices are kept for 30 minutes.");

        // Repair kit cost.
        ImGui.Spacing();
        Ui.Text("Repair kit cost");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width);
        if (Ui.InputInt("##repairKitCost", ref costBox, 0)) costTyped = true;
        Hover("What one repair kit costs you, for the routes' profit (Route Optimization and Income).");
        ImGui.SameLine();
        Tinted(Palette.Gil, "gil");
        ImGui.SameLine();
        if (ImGui.Button("Save##repairKitCost"))
        {
            var gil = costBox;
            reader.Edit("the repair kit cost", c => FleetEdits.SaveRepairKitCost(c, gil), error => { if (error == null) costTyped = false; });
        }

        // Warnings.
        ImGui.Spacing();
        Ui.Text("Warnings");
        void Box(string label, string id, ref int value, string after, string help)
        {
            Ui.Text(label);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(width);
            if (Ui.InputInt($"##{id}", ref value, 0)) warningsTyped = true;
            if (after.Length > 0)
            {
                ImGui.SameLine();
                Ui.Text(after);
            }
            Hover(help);
        }
        ImGui.Indent();
        {
            Box("Low repair kits below", "lowKits", ref kitsBox, "", "A character with fewer repair kits than this shows as low (0 turns it off).");
            Box("Low ceruleum below", "lowCeruleum", ref ceruleumBox, "", "A character with less ceruleum than this shows as low (0 turns it off).");
            Box("FC house not entered for", "houseDays", ref houseBox, "days", "1 to 44 days: the game demolishes a house not entered for 45.");
            Box("A character counts as not synced when it is", "notSynced", ref notSyncedBox, "minutes behind the rest of its account",
                "10 to 1440 minutes. Used by the Summary and the Roster's not-synced marks.");
            if (ImGui.Button("Save warnings"))
            {
                var (k, c2, h, n) = (kitsBox, ceruleumBox, houseBox, notSyncedBox);
                reader.Edit("the Warnings", c => FleetEdits.SaveWarnings(c, k, c2, h, n), error => { if (error == null) warningsTyped = false; });
            }
            if (warningsTyped)
            {
                ImGui.SameLine();
                if (ImGui.Button("Undo changes"))
                {
                    warningsTyped = false;
                    settingsShownAt = null;
                }
            }
        }
        ImGui.Unindent();
        EditLine();
    }

    // ---- Import Fight Club Data (as the app's Settings → Data) ----

    private readonly FileDialogManager fightClubDialog = new();
    private FightClubImport.ReadResult? fightClubRead;
    private string fightClubFile = "";
    private HashSet<int> fightClubTicked = new();
    private string fightClubLabel = "";
    private volatile bool fightClubReading;
    private string? fightClubDone;

    private void DrawFightClubImport()
    {
        fightClubDialog.Draw();
        if (!ImGui.CollapsingHeader("Fight Club Data")) return;
        Colored(Dim, "Fight Club's route spreadsheet: the routes, their gil and the builds they need, for Route Optimization, Parts and Income." + (WithApp ? " Saved in the database, for the app too." : ""));
        using (ImRaii.Disabled(fightClubReading || reader.Editing))
        {
            if (ImGui.Button("Import Fight Club Data"))
            {
                fightClubDone = null;
                fightClubDialog.OpenFileDialog("Choose Fight Club's spreadsheet (.xlsx)", ".xlsx", (picked, paths) =>
                {
                    if (!picked || paths.Count == 0) return;
                    var path = paths[0];
                    fightClubRead = null;
                    fightClubReading = true;
                    // Read away from the game's thread; shown once it's all there.
                    Task.Run(() =>
                    {
                        try
                        {
                            var read = FightClubImport.ReadFile(path);
                            fightClubTicked = read.Rows.Where(r => !r.HasIssue).Select(r => r.RowNumber).ToHashSet();
                            fightClubFile = Path.GetFileName(path);
                            fightClubLabel = "";
                            fightClubRead = read;
                        }
                        finally
                        {
                            fightClubReading = false;
                        }
                    });
                }, 1, null);
            }
        }
        Hover("Save the spreadsheet as an Excel file (.xlsx) first: in Google Sheets, File → Download → Microsoft Excel.");
        if (fightClubReading) Colored(Dim, "Reading the file...");
        if (fightClubDone != null) Colored(fightClubDone.StartsWith("Imported", StringComparison.Ordinal) ? Green : Amber, fightClubDone);

        if (fightClubRead is not { } read) return;
        if (!read.Success)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                Ui.Wrapped($"{fightClubFile}: {read.Error}");
            if (read.ActualHeaders.Count > 0) Colored(Dim, $"Columns found in the file: {string.Join(", ", read.ActualHeaders)}");
            if (ImGui.Button("Close##fightClub")) fightClubRead = null;
            return;
        }
        Ui.Text($"From {fightClubFile}:");
        if (read.MissingOptional.Count > 0)
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                Ui.Wrapped($"This file has no column for {string.Join(", ", read.MissingOptional)}. Those are left blank for every row.");
        Ui.Text("Patch label (optional)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##fightClubLabel", "e.g. Patch 7.5", ref fightClubLabel, 40);
        var count = read.Rows.Count(r => fightClubTicked.Contains(r.RowNumber));
        Colored(Dim, $"{count} of {read.Rows.Count} rows will be imported. This replaces all the Fight Club data there is now. Rows with a problem start unticked.");

        var height = Math.Min(read.Rows.Count + 1, 12) * ImGui.GetFrameHeightWithSpacing() + 8;
        using (var table = Ui.Table("##fightClubRows", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit, new Vector2(0, height)))
        {
            if (table)
            {
                Ui.ScrollFreeze(0, 1);
                Ui.SetupColumn("##tick");
                Ui.SetupColumn("Row");
                Ui.SetupColumn("Route");
                Ui.SetupColumn("Rec Build");
                Ui.SetupColumn("Gil/Hr");
                Ui.SetupColumn("Problem", ImGuiTableColumnFlags.WidthStretch);
                Ui.HeadersRow();
                foreach (var row in read.Rows)
                {
                    Ui.NextRow();
                    Ui.NextColumn();
                    var on = fightClubTicked.Contains(row.RowNumber);
                    if (Ui.Checkbox($"##fc{row.RowNumber}", ref on))
                    {
                        if (on) fightClubTicked.Add(row.RowNumber); else fightClubTicked.Remove(row.RowNumber);
                    }
                    Ui.NextColumn();
                    RightNumber(row.RowNumber);
                    Ui.NextColumn();
                    if (row.Data["route"] is string route) Ui.Text(route); else Colored(Dim, "(blank)");
                    Ui.NextColumn();
                    if (row.Data["rec_build_notes"] is string build) Ui.Text(build); else Colored(Dim, "-");
                    Ui.NextColumn();
                    if (row.Data["gil_hr"] is double gil) Tinted(Palette.Gil, gil.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)); else Colored(Dim, "-");
                    Ui.NextColumn();
                    if (row.HasIssue)
                        using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                            Ui.Wrapped(string.Join("; ", row.Issues));
                }
            }
        }
        using (ImRaii.Disabled(count == 0 || reader.Editing))
        {
            if (ImGui.Button($"Import {count} route{(count == 1 ? "" : "s")}"))
            {
                var rows = read.Rows.Where(r => fightClubTicked.Contains(r.RowNumber)).ToList();
                var label = fightClubLabel.Trim();
                reader.Edit("the Fight Club data", async c =>
                {
                    await FightClubImport.Commit(c, rows, label);
                    return null;
                }, error =>
                {
                    fightClubDone = error == null ? $"Imported {rows.Count} route{(rows.Count == 1 ? "" : "s")}." : $"Import failed: {error}";
                    if (error == null) fightClubRead = null;
                });
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##fightClub")) fightClubRead = null;
    }
}
