using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace XIVFleetCompanion.Windows;

// 2.x, the rest of the tabs: the Roster, Retainers and PCs pages, from the database's views (FleetReader). Each reads
// every minute while it's shown. They only show; changing things (reordering or removing characters, retainers' gear
// targets, removing a PC) stays in the app for now.
public partial class MainWindow
{
    private string rosterFilter = "";
    private string retainersFilter = "";

    private static bool Matches(string filter, params string?[] values) =>
        string.IsNullOrWhiteSpace(filter) || values.Any(v => v != null && v.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private void ListToolbar(ref string filter, string id)
    {
        RefreshButton();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.InputText($"Filter##{id}", ref filter, 64);
        ImGui.SameLine();
        Colored(Dim, "Click a column to sort by it.");
    }

    private static string Number(long? value) => value.HasValue ? value.Value.ToString("N0", CultureInfo.CurrentCulture) : "—";

    private void DrawRoster()
    {
        reader.ReadIfDue(withLists: true);
        ListToolbar(ref rosterFilter, "roster");
        ReadLine(reader.Roster, FleetReader.ListsEvery);
        var roster = reader.Roster.Data;
        if (roster == null) return;

        var rows = roster.Select((c, order) => (c, order))
            .Where(x => Matches(rosterFilter, x.c.Character, x.c.World, x.c.Account, x.c.FcName));
        using var table = ImRaii.Table("##roster", 9, TableFlags);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("FC");
        ImGui.TableSetupColumn("House");
        ImGui.TableSetupColumn("Gil");
        ImGui.TableSetupColumn("Ceruleum");
        ImGui.TableSetupColumn("Repair kits");
        ImGui.TableSetupColumn("Retainers / subs");
        ImGui.TableSetupColumn("Last synced");
        ImGui.TableSetupColumn("Synced by");
        ImGui.TableHeadersRow();
        var (column, ascending) = SortedBy();
        var list = column switch
        {
            1 => Sorted(rows, ascending, x => x.c.FcName ?? "", x => x.c.FcName == null),
            2 => Sorted(rows, ascending, x => x.c.House ?? "", x => x.c.House == null),
            3 => Sorted(rows, ascending, x => x.c.Gil),
            4 => Sorted(rows, ascending, x => x.c.Ceruleum ?? 0),
            5 => Sorted(rows, ascending, x => x.c.RepairKits ?? 0),
            6 => Sorted(rows, ascending, x => x.c.Retainers * 100 + x.c.Submarines),
            7 => Sorted(rows, ascending, x => x.c.LastSynced ?? DateTime.MinValue),
            8 => Sorted(rows, ascending, x => FleetReader.SyncedBy(x.c.LoginPc, x.c.LoginLabel)),
            _ => Sorted(rows, ascending, x => x.order),
        };
        var now = DateTime.Now;
        foreach (var (c, _) in list)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{c.Character} ({c.World})");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Account: {(c.Account.Length > 0 ? c.Account : "none")}");
            if (c.BehindMinutes is int behind)
            {
                ImGui.SameLine();
                Colored(Amber, "not synced");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{behind / 60} h {behind % 60} min behind the rest of its account: unticked in the plugin, or its AutoRetainer no longer has it.");
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(c.FcName ?? "—");
            if (c.FcPoints.HasValue && ImGui.IsItemHovered()) ImGui.SetTooltip($"{c.FcPoints.Value:N0} FC points");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(c.House ?? "—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Number(c.Gil));
            ImGui.TableNextColumn();
            if (c.LowCeruleum) Colored(Red, Number(c.Ceruleum)); else ImGui.TextUnformatted(Number(c.Ceruleum));
            if (c.LowCeruleum && ImGui.IsItemHovered()) ImGui.SetTooltip("Below the low-ceruleum warning (set in the app: Settings → General → Warnings)");
            ImGui.TableNextColumn();
            if (c.LowRepairKits) Colored(Red, Number(c.RepairKits)); else ImGui.TextUnformatted(Number(c.RepairKits));
            if (c.LowRepairKits && ImGui.IsItemHovered()) ImGui.SetTooltip("Below the low-repair-kit warning (set in the app: Settings → General → Warnings)");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{c.Retainers} / {c.Submarines}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(c.LastSynced.HasValue ? FleetReader.Ago(c.LastSynced.Value, now) : "—");
            if (c.LastSynced.HasValue && ImGui.IsItemHovered()) ImGui.SetTooltip(c.LastSynced.Value.ToString("g", CultureInfo.CurrentCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FleetReader.SyncedBy(c.LoginPc, c.LoginLabel));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(c.LoginPc != null && c.LoggedInAt.HasValue
                    ? $"Last logged in on this setup {c.LoggedInAt.Value:g}. Only this setup syncs the character; others that have it leave it alone."
                    : "No login recorded yet: every setup that has the character syncs it.");
        }
    }

    private void DrawRetainers()
    {
        reader.ReadIfDue(withLists: true);
        ListToolbar(ref retainersFilter, "retainers");
        ReadLine(reader.Retainers, FleetReader.ListsEvery);
        var read = reader.Retainers.Data;
        if (read == null) return;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var rows = read.Retainers
            .Select((r, order) => (r, order, job: GameNames.Job(r.Job), venture: GameNames.Venture(r.VentureId),
                ilvl: read.Gear.TryGetValue(r.RetainerId, out var items) ? FleetReader.AverageItemLevel(items, GameNames.ItemLevel) : null))
            .Where(x => Matches(retainersFilter, x.r.Owner, x.r.Name, x.r.Account, x.job, x.venture));
        using var table = ImRaii.Table("##retainers", 8, TableFlags);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableSetupColumn("Retainer");
        ImGui.TableSetupColumn("Owner");
        ImGui.TableSetupColumn("Class/Level");
        ImGui.TableSetupColumn("Gil");
        ImGui.TableSetupColumn("Avg ilvl");
        ImGui.TableSetupColumn("Venture");
        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Gear target");
        ImGui.TableHeadersRow();
        var (column, ascending) = SortedBy();
        // Status: done first, then the soonest done; idle last either way.
        long StatusKey(RetainerRow r) => r.HasVenture && r.VentureEndsAt > 0 ? r.VentureEndsAt : long.MaxValue;
        var list = column switch
        {
            0 => Sorted(rows, ascending, x => x.r.Name),
            1 => Sorted(rows, ascending, x => x.r.Owner),
            2 => Sorted(rows, ascending, x => x.job, x => x.job.Length == 0),
            3 => Sorted(rows, ascending, x => x.r.Gil ?? 0, x => x.r.Gil == null),
            4 => Sorted(rows, ascending, x => x.ilvl ?? 0, x => x.ilvl == null),
            5 => Sorted(rows, ascending, x => x.venture, x => x.venture.Length == 0),
            6 => Sorted(rows, ascending, x => StatusKey(x.r), x => StatusKey(x.r) == long.MaxValue),
            7 => Sorted(rows, ascending, x => $"{x.r.TargetMode} {x.r.TargetValue:D4}", x => x.r.TargetMode == null),
            _ => Sorted(rows, ascending, x => x.order),
        };
        // Until a column is clicked: in the app's order (account, the Roster's order, hiring order).
        if (ImGui.TableGetSortSpecs().SpecsCount < 1) list = rows.OrderBy(x => x.order).ToList();
        foreach (var (r, _, job, venture, ilvl) in list)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Owner.Length > 0 ? $"{r.Owner} ({r.World})" : "—");
            if (ImGui.IsItemHovered() && r.Account.Length > 0) ImGui.SetTooltip($"Account: {r.Account}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(job.Length > 0 ? $"{job}/{(r.Level.HasValue ? r.Level.Value.ToString(CultureInfo.InvariantCulture) : "?")}" : "—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Number(r.Gil));
            ImGui.TableNextColumn();
            if (ilvl.HasValue) ImGui.TextUnformatted(ilvl.Value.ToString("0.0", CultureInfo.CurrentCulture));
            else Colored(Dim, "—");
            if (!r.ItemsSeen && ImGui.IsItemHovered())
                ImGui.SetTooltip("Allagan Tools hasn't seen this retainer's items yet: open it at a summoning bell on a PC running the plugin.");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(venture.Length > 0 ? venture : "—");
            ImGui.TableNextColumn();
            var status = FleetReader.VentureStatus(r.HasVenture, r.VentureEndsAt, nowUnix);
            if (status == "Venture Complete") Colored(Green, status);
            else if (status == "Idle") Colored(r.Job > 0 ? Amber : Dim, status);
            else ImGui.TextUnformatted(status);
            ImGui.TableNextColumn();
            if (r.TargetMode == null) Colored(Dim, "—");
            else ImGui.TextUnformatted($"{(r.TargetMode == "level" ? "Level" : "ilvl")} {r.TargetValue}{(r.OnlyCrafted ? ", crafted only" : "")}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Gear targets and the slot-by-slot check are set and shown in the app's Retainers tab for now.");
        }
    }

    private void DrawPcs()
    {
        reader.ReadIfDue(withLists: true);
        RefreshButton();
        ImGui.SameLine();
        Colored(Dim, "Each setup of the plugin (PC and account label), as of its last sync. Click a column to sort by it.");
        ReadLine(reader.Pcs, FleetReader.ListsEvery);
        var pcs = reader.Pcs.Data;
        if (pcs == null) return;
        if (pcs.Count == 0)
        {
            ImGui.TextWrapped("No PC has reported yet. Each setup reports here at its next sync.");
            return;
        }
        var newest = pcs.Select(p => p.PluginVersion).Aggregate("0", (v, x) => FleetReader.CompareVersions(x, v) > 0 ? x! : v);
        var now = DateTime.Now;

        using (var table = ImRaii.Table("##pcs", 9, TableFlags & ~ImGuiTableFlags.ScrollY))
        {
            if (table)
            {
                ImGui.TableSetupColumn("PC");
                ImGui.TableSetupColumn("Plugin");
                ImGui.TableSetupColumn("Last sync");
                ImGui.TableSetupColumn("Characters");
                ImGui.TableSetupColumn("AutoRetainer");
                ImGui.TableSetupColumn("Allagan Tools");
                ImGui.TableSetupColumn("FC Tracker");
                ImGui.TableSetupColumn("Submarine Tracker");
                ImGui.TableSetupColumn("Could not see");
                ImGui.TableHeadersRow();
                var (column, ascending) = SortedBy();
                var rows = pcs.Select((p, order) => (p, order));
                static string YesNo(bool? b) => b == true ? "Yes" : b == false ? "No" : "—";
                var list = column switch
                {
                    1 => Sorted(rows, ascending, x => x.p.PluginVersion ?? "", x => x.p.PluginVersion == null),
                    2 => Sorted(rows, ascending, x => x.p.LastSync ?? DateTime.MinValue, x => x.p.LastSync == null),
                    3 => Sorted(rows, ascending, x => x.p.Characters ?? 0, x => x.p.Characters == null),
                    4 => Sorted(rows, ascending, x => YesNo(x.p.AutoRetainer)),
                    5 => Sorted(rows, ascending, x => YesNo(x.p.AllaganTools)),
                    6 => Sorted(rows, ascending, x => YesNo(x.p.FcTracker)),
                    7 => Sorted(rows, ascending, x => YesNo(x.p.SubmarineTracker)),
                    8 => Sorted(rows, ascending, x => x.p.Problems.Count),
                    _ => Sorted(rows, ascending, x => x.order),
                };
                foreach (var (p, _) in list)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.Account.Length > 0 ? $"{p.Pc} ({p.Account})" : p.Pc);
                    ImGui.TableNextColumn();
                    var old = FleetReader.CompareVersions(p.PluginVersion, newest) < 0;
                    if (old) Colored(Amber, p.PluginVersion ?? "—"); else ImGui.TextUnformatted(p.PluginVersion ?? "—");
                    if (old && ImGui.IsItemHovered()) ImGui.SetTooltip($"Older than {newest} on another PC: update it in /xlplugins there.");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.LastSync.HasValue ? FleetReader.Ago(p.LastSync.Value, now) : "never");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FleetReader.PcCharacters(p.Characters, p.LeftOut, p.Elsewhere));
                    if (p.Elsewhere > 0 && ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{p.Elsewhere} of this setup's characters last logged in on another setup, so that setup syncs them (the Roster's Synced by column says which).");
                    ImGui.TableNextColumn();
                    if (p.AutoRetainer == false) Colored(Amber, "No"); else ImGui.TextUnformatted(YesNo(p.AutoRetainer));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("AutoRetainer running (needed: nothing syncs without it)");
                    ImGui.TableNextColumn();
                    if (p.AllaganTools == false) Colored(Amber, "No"); else ImGui.TextUnformatted(YesNo(p.AllaganTools));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Allagan Tools running (bags, retainers' items, FC chests and listings)");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(YesNo(p.FcTracker));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("FC Tracker running in that game (optional: Free Company house details)");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(YesNo(p.SubmarineTracker));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Submarine Tracker running in that game (loot per voyage; only needed with subs)");
                    ImGui.TableNextColumn();
                    if (p.Problems.Count == 0) ImGui.TextUnformatted("Nothing");
                    else
                    {
                        Colored(Amber, $"{p.Problems.Count} thing{(p.Problems.Count == 1 ? "" : "s")}");
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", p.Problems));
                    }
                }
            }
        }

        // Each setup's last "Check what I can see", folded.
        foreach (var p in pcs.Where(p => p.CheckText != null))
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
}
