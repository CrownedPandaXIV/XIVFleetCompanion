using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace XIVFleetCompanion.Windows;

// 2.x, the rest of the tabs: the Roster, Retainers, PCs, Free Companies and Inventory pages, from the database's views
// (FleetReader). Each reads every minute while it's shown, in the app's order (the Roster's for characters), and has
// the app's table controls (FleetTable: sorting, Reset Sort, Columns, Group by account). They only show; changing
// things (reordering or removing characters, gear targets, removing a PC) stays in the app for now.
public partial class MainWindow
{
    private string rosterFilter = "";
    private string retainersFilter = "";
    private string fcsFilter = "";

    private FleetTable<RosterRow>? rosterTable;
    private FleetTable<(RetainerRow R, string Job, string Venture, double? Ilvl)>? retainersTable;
    private FleetTable<PcRow>? pcsTable;
    private FleetTable<FcRow>? fcsTable;

    private static bool Matches(string filter, params string?[] values) =>
        string.IsNullOrWhiteSpace(filter) || values.Any(v => v != null && v.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private void FilterBox(ref string filter, string id)
    {
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        ImGui.InputText($"Filter##filter{id}", ref filter, 64);
    }

    private static string Number(long? value) => value.HasValue ? value.Value.ToString("N0", CultureInfo.CurrentCulture) : "—";

    private static void Hover(string text)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }

    // ---- Roster ----

    private FleetTable<RosterRow> RosterTable => rosterTable ??= new FleetTable<RosterRow>("roster", plugin.Configuration, new List<FleetColumn<RosterRow>>
    {
        new("character", "Character", c =>
        {
            ImGui.TextUnformatted($"{c.Character} ({c.World})");
            Hover($"Account: {(c.Account.Length > 0 ? c.Account : "none")}");
            if (c.BehindMinutes is int behind)
            {
                ImGui.SameLine();
                Colored(Amber, "not synced");
                Hover($"{behind / 60} h {behind % 60} min behind the rest of its account: unticked in the plugin, or its AutoRetainer no longer has it.");
            }
        }, c => c.Character, Locked: true),
        new("world", "World", c => ImGui.TextUnformatted(c.World), c => c.World, Hidden: true),
        new("account", "Account", c => ImGui.TextUnformatted(c.Account.Length > 0 ? c.Account : "—"), c => c.Account, Hidden: true),
        new("fc", "FC", c =>
        {
            ImGui.TextUnformatted(c.FcName ?? "—");
            if (c.FcPoints.HasValue) Hover($"{c.FcPoints.Value:N0} FC points");
        }, c => c.FcName ?? "", c => c.FcName == null),
        new("fcPoints", "FC Points", c => ImGui.TextUnformatted(Number(c.FcPoints)), c => c.FcPoints ?? 0, c => c.FcPoints == null, Hidden: true),
        new("house", "House", c => ImGui.TextUnformatted(c.House ?? "—"), c => c.House ?? "", c => c.House == null),
        new("gil", "Gil", c => ImGui.TextUnformatted(Number(c.Gil)), c => c.Gil),
        new("ceruleum", "Ceruleum", c =>
        {
            if (c.LowCeruleum) Colored(Red, Number(c.Ceruleum)); else ImGui.TextUnformatted(Number(c.Ceruleum));
            if (c.LowCeruleum) Hover("Below the low-ceruleum warning (set in the app: Settings → General → Warnings)");
        }, c => c.Ceruleum ?? 0),
        new("repairKits", "Repair kits", c =>
        {
            if (c.LowRepairKits) Colored(Red, Number(c.RepairKits)); else ImGui.TextUnformatted(Number(c.RepairKits));
            if (c.LowRepairKits) Hover("Below the low-repair-kit warning (set in the app: Settings → General → Warnings)");
        }, c => c.RepairKits ?? 0),
        new("retainers", "Retainers", c => ImGui.TextUnformatted(c.Retainers.ToString(CultureInfo.InvariantCulture)), c => c.Retainers),
        new("subs", "Subs", c => ImGui.TextUnformatted(c.Submarines.ToString(CultureInfo.InvariantCulture)), c => c.Submarines),
        new("lastSynced", "Last synced", c =>
        {
            ImGui.TextUnformatted(c.LastSynced.HasValue ? FleetReader.Ago(c.LastSynced.Value, DateTime.Now) : "—");
            if (c.LastSynced.HasValue) Hover(c.LastSynced.Value.ToString("g", CultureInfo.CurrentCulture));
        }, c => c.LastSynced ?? DateTime.MinValue),
        new("syncedBy", "Synced by", c =>
        {
            ImGui.TextUnformatted(FleetReader.SyncedBy(c.LoginPc, c.LoginLabel));
            Hover(c.LoginPc != null && c.LoggedInAt.HasValue
                ? $"Last logged in on this setup {c.LoggedInAt.Value:g}. Only this setup syncs the character; others that have it leave it alone."
                : "No login recorded yet: every setup that has the character syncs it.");
        }, c => FleetReader.SyncedBy(c.LoginPc, c.LoginLabel)),
    }, c => c.Account);

    private void DrawRoster()
    {
        reader.ReadIfDue(withLists: true);
        RefreshButton();
        ImGui.SameLine();
        RosterTable.DrawControls();
        FilterBox(ref rosterFilter, "roster");
        ReadLine(reader.Roster, FleetReader.ListsEvery);
        var roster = reader.Roster.Data;
        if (roster == null) return;
        RosterTable.Draw(roster.Where(c => Matches(rosterFilter, c.Character, c.World, c.Account, c.FcName)).ToList());
    }

    // ---- Retainers ----

    private FleetTable<(RetainerRow R, string Job, string Venture, double? Ilvl)> RetainersTable => retainersTable ??=
        new FleetTable<(RetainerRow R, string Job, string Venture, double? Ilvl)>("retainers", plugin.Configuration, new List<FleetColumn<(RetainerRow R, string Job, string Venture, double? Ilvl)>>
    {
        new("retainer", "Retainer", x => ImGui.TextUnformatted(x.R.Name), x => x.R.Name, Locked: true),
        new("owner", "Owner", x =>
        {
            ImGui.TextUnformatted(x.R.Owner.Length > 0 ? $"{x.R.Owner} ({x.R.World})" : "—");
            if (x.R.Account.Length > 0) Hover($"Account: {x.R.Account}");
        }, x => x.R.Owner),
        new("account", "Account", x => ImGui.TextUnformatted(x.R.Account.Length > 0 ? x.R.Account : "—"), x => x.R.Account, Hidden: true),
        new("job", "Class/Level", x => ImGui.TextUnformatted(x.Job.Length > 0 ? $"{x.Job}/{(x.R.Level.HasValue ? x.R.Level.Value.ToString(CultureInfo.InvariantCulture) : "?")}" : "—"),
            x => x.Job, x => x.Job.Length == 0),
        new("gil", "Gil", x => ImGui.TextUnformatted(Number(x.R.Gil)), x => x.R.Gil ?? 0, x => x.R.Gil == null),
        new("ilvl", "Avg ilvl", x =>
        {
            if (x.Ilvl.HasValue) ImGui.TextUnformatted(x.Ilvl.Value.ToString("0.0", CultureInfo.CurrentCulture)); else Colored(Dim, "—");
            if (!x.R.ItemsSeen) Hover("Allagan Tools hasn't seen this retainer's items yet: open it at a summoning bell on a PC running the plugin.");
        }, x => x.Ilvl ?? 0, x => x.Ilvl == null),
        new("venture", "Venture", x => ImGui.TextUnformatted(x.Venture.Length > 0 ? x.Venture : "—"), x => x.Venture, x => x.Venture.Length == 0),
        // Status: done first, then the soonest done; idle last either way.
        new("status", "Status", x =>
        {
            var status = FleetReader.VentureStatus(x.R.HasVenture, x.R.VentureEndsAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (status == "Venture Complete") Colored(Green, status);
            else if (status == "Idle") Colored(x.R.Job > 0 ? Amber : Dim, status);
            else ImGui.TextUnformatted(status);
        }, x => x.R.VentureEndsAt, x => !x.R.HasVenture || x.R.VentureEndsAt <= 0),
        new("target", "Gear target", x =>
        {
            if (x.R.TargetMode == null) Colored(Dim, "—");
            else ImGui.TextUnformatted($"{(x.R.TargetMode == "level" ? "Level" : "ilvl")} {x.R.TargetValue}{(x.R.OnlyCrafted ? ", crafted only" : "")}");
            Hover("Gear targets and the slot-by-slot check are set and shown in the app's Retainers tab for now.");
        }, x => $"{x.R.TargetMode} {x.R.TargetValue:D4}", x => x.R.TargetMode == null),
    }, x => x.R.Account);

    private void DrawRetainers()
    {
        reader.ReadIfDue(withLists: true);
        RefreshButton();
        ImGui.SameLine();
        RetainersTable.DrawControls();
        FilterBox(ref retainersFilter, "retainers");
        ReadLine(reader.Retainers, FleetReader.ListsEvery);
        var read = reader.Retainers.Data;
        if (read == null) return;
        var unseen = read.Retainers.Count(r => !r.ItemsSeen);
        if (unseen > 0)
        {
            Colored(Amber, $"{unseen} retainer{(unseen == 1 ? "'s" : "s'")} items {(unseen == 1 ? "hasn't" : "haven't")} been seen yet (hover Avg ilvl for which).");
            Hover("Allagan Tools only knows a retainer's items once it has been opened at a summoning bell on a PC running the plugin.");
        }
        // In the Roster's order (each owner's place in it), then hiring order, as the app.
        var rows = read.Retainers
            .Select(r => (R: r, Job: GameNames.Job(r.Job), Venture: GameNames.Venture(r.VentureId),
                Ilvl: read.Gear.TryGetValue(r.RetainerId, out var items) ? FleetReader.AverageItemLevel(items, GameNames.ItemLevel) : null))
            .Where(x => Matches(retainersFilter, x.R.Owner, x.R.Name, x.R.Account, x.Job, x.Venture))
            .ToList();
        RetainersTable.Draw(rows);
    }

    // ---- PCs ----

    private static string YesNo(bool? b) => b == true ? "Yes" : b == false ? "No" : "—";

    private FleetTable<PcRow> PcsTable => pcsTable ??= new FleetTable<PcRow>("pcs", plugin.Configuration, new List<FleetColumn<PcRow>>
    {
        new("pc", "PC", p => ImGui.TextUnformatted(p.Account.Length > 0 ? $"{p.Pc} ({p.Account})" : p.Pc), p => p.Pc, Locked: true),
        new("plugin", "Plugin", p =>
        {
            var newest = reader.Pcs.Data?.Select(x => x.PluginVersion).Aggregate("0", (v, x) => FleetReader.CompareVersions(x, v) > 0 ? x! : v) ?? "0";
            var old = FleetReader.CompareVersions(p.PluginVersion, newest) < 0;
            if (old) Colored(Amber, p.PluginVersion ?? "—"); else ImGui.TextUnformatted(p.PluginVersion ?? "—");
            if (old) Hover($"Older than {newest} on another PC: update it in /xlplugins there.");
        }, p => string.Join(".", (p.PluginVersion ?? "").Split('.').Select(n => n.PadLeft(5, '0'))), p => p.PluginVersion == null),
        new("lastSync", "Last sync", p => ImGui.TextUnformatted(p.LastSync.HasValue ? FleetReader.Ago(p.LastSync.Value, DateTime.Now) : "never"),
            p => p.LastSync ?? DateTime.MinValue, p => p.LastSync == null),
        new("characters", "Characters", p =>
        {
            ImGui.TextUnformatted(FleetReader.PcCharacters(p.Characters, p.LeftOut, p.Elsewhere));
            if (p.Elsewhere > 0) Hover($"{p.Elsewhere} of this setup's characters last logged in on another setup, so that setup syncs them (the Roster's Synced by column says which).");
        }, p => p.Characters ?? 0, p => p.Characters == null),
        new("autoretainer", "AutoRetainer", p =>
        {
            if (p.AutoRetainer == false) Colored(Amber, "No"); else ImGui.TextUnformatted(YesNo(p.AutoRetainer));
            Hover("AutoRetainer running (needed: nothing syncs without it)");
        }, p => YesNo(p.AutoRetainer)),
        new("allagan", "Allagan Tools", p =>
        {
            if (p.AllaganTools == false) Colored(Amber, "No"); else ImGui.TextUnformatted(YesNo(p.AllaganTools));
            Hover("Allagan Tools running (bags, retainers' items, FC chests and listings)");
        }, p => YesNo(p.AllaganTools)),
        new("fctracker", "FC Tracker", p => { ImGui.TextUnformatted(YesNo(p.FcTracker)); Hover("FC Tracker running in that game (optional: Free Company house details)"); },
            p => YesNo(p.FcTracker)),
        new("subtracker", "Submarine Tracker", p => { ImGui.TextUnformatted(YesNo(p.SubmarineTracker)); Hover("Submarine Tracker running in that game (loot per voyage; only needed with subs)"); },
            p => YesNo(p.SubmarineTracker)),
        new("ventures", "Venture Log", p => { ImGui.TextUnformatted(YesNo(p.VentureLog)); Hover("AutoRetainer's venture statistics found: what each retainer's ventures brought back (only with retainers)"); },
            p => YesNo(p.VentureLog), Hidden: true),
        new("problems", "Could not see", p =>
        {
            if (p.Problems.Count == 0) { ImGui.TextUnformatted("Nothing"); return; }
            Colored(Amber, $"{p.Problems.Count} thing{(p.Problems.Count == 1 ? "" : "s")}");
            Hover(string.Join("\n", p.Problems));
        }, p => p.Problems.Count),
    });

    private void DrawPcs()
    {
        reader.ReadIfDue(withLists: true);
        RefreshButton();
        ImGui.SameLine();
        PcsTable.DrawControls();
        ImGui.SameLine();
        Colored(Dim, "Each setup of the plugin (PC and account label), as of its last sync.");
        ReadLine(reader.Pcs, FleetReader.ListsEvery);
        var pcs = reader.Pcs.Data;
        if (pcs == null) return;
        if (pcs.Count == 0)
        {
            ImGui.TextWrapped("No PC has reported yet. Each setup reports here at its next sync.");
            return;
        }
        var checks = pcs.Where(p => p.CheckText != null).ToList();
        // The table above, each setup's last "Check what I can see" below (folded), sharing the page.
        var below = checks.Count * ImGui.GetFrameHeightWithSpacing();
        using (var top = ImRaii.Child("##pcsTable", new Vector2(0, Math.Max(120 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y - below)), false))
        {
            if (top.Success) PcsTable.Draw(pcs);
        }
        var now = DateTime.Now;
        foreach (var p in checks)
        {
            var who = p.Account.Length > 0 ? $"{p.Pc} ({p.Account})" : p.Pc;
            var toFix = p.CheckToFix > 0 ? $"{p.CheckToFix} thing{(p.CheckToFix == 1 ? "" : "s")} to fix" : "nothing to fix";
            var when = p.CheckAt.HasValue ? FleetReader.Ago(p.CheckAt.Value, now) : "";
            if (ImGui.CollapsingHeader($"Check what I can see: {who}, {when}, {toFix}##check{p.Pc}|{p.Account}"))
            {
                foreach (var line in p.CheckText!.Split('\n'))
                    ImGui.TextWrapped(line.TrimEnd('\r'));
            }
        }
    }

    // ---- Free Companies ----

    private FleetTable<FcRow> FcsTable => fcsTable ??= new FleetTable<FcRow>("fcs", plugin.Configuration, new List<FleetColumn<FcRow>>
    {
        new("name", "FC", f => ImGui.TextUnformatted(f.Name.Length > 0 ? f.Name : "—"), f => f.Name, Locked: true),
        new("leader", "Leader", f => ImGui.TextUnformatted(f.Leader ?? "—"), f => f.Leader ?? "", f => f.Leader == null),
        new("world", "World", f => ImGui.TextUnformatted(GameNames.World(f.HomeWorld) is { Length: > 0 } w ? w : "—"), f => GameNames.World(f.HomeWorld)),
        new("account", "Account", f => ImGui.TextUnformatted(f.Account.Length > 0 ? f.Account : "—"), f => f.Account, Hidden: true),
        new("points", "FC Points", f => ImGui.TextUnformatted(Number(f.Points)), f => f.Points ?? 0, f => f.Points == null),
        new("members", "Members", f => ImGui.TextUnformatted(f.Members.HasValue ? f.Members.Value.ToString(CultureInfo.InvariantCulture) : "—"),
            f => f.Members ?? 0, f => f.Members == null),
        new("house", "House", f =>
        {
            if (f.HasHouse) { ImGui.TextUnformatted(f.House ?? "—"); return; }
            var text = f.Eligibility ?? "—";
            if (text == "Eligible") Colored(Green, text); else ImGui.TextUnformatted(text);
            if (f.EligibleAt.HasValue) Hover($"Can bid on a house from {f.EligibleAt.Value:g} (30 days after it was founded), at rank 6 or higher.");
        }, f => f.HasHouse ? 1 : 0),
        // Unknown dates sort as the oldest, since they are the ones to check.
        new("lastEntered", "Last entered", f =>
        {
            if (!f.HasHouse) { Colored(Dim, "—"); return; }
            var text = f.HouseDays == null ? "Unknown" : f.HouseDays == 0 ? "Today" : $"{f.HouseDays} day{(f.HouseDays == 1 ? "" : "s")} ago";
            var color = f.HouseLevel switch { "danger" or "unknown" => (Vector4?)Red, "warn" => Amber, _ => null };
            if (color is Vector4 c) Colored(c, text); else ImGui.TextUnformatted(text);
            Hover(f.LastEntered.HasValue
                ? $"Last entered {f.LastEntered.Value:g}. The game can demolish a house after 45 days without a member entering{(f.DemolitionInDays is int left ? $" ({left} days left)" : "")}."
                : "FC Tracker has no date for this house yet.");
        }, f => f.HasHouse ? f.HouseDays ?? 9999 : -1, f => !f.HasHouse),
    }, f => f.Account);

    private void DrawFcs()
    {
        reader.ReadIfDue(withLists: true);
        RefreshButton();
        ImGui.SameLine();
        FcsTable.DrawControls();
        FilterBox(ref fcsFilter, "fcs");
        ReadLine(reader.Fcs, FleetReader.ListsEvery);
        var fcs = reader.Fcs.Data;
        if (fcs == null) return;
        if (fcs.Count == 0)
        {
            ImGui.TextWrapped("No Free Companies yet. They appear once FC Tracker has seen them on a PC running the plugin.");
            return;
        }
        FcsTable.Draw(fcs.Where(f => Matches(fcsFilter, f.Name, f.Leader, f.Account, f.House)).ToList());
    }
}
