using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion.Windows;

// 2.x batch 4, the Parts page, as the app's Parts tab: every part a sub needs for the build it should have at its rank
// (its Sub Rules, or Fight Club's best build for a sub ticked Craft? from rank 85), a new workshop's first sub and every
// unlocked empty slot; and whether a spare covers it, the part another sub gives up when upgraded, or it needs crafting.
// Worked out here (PartsPlanner) from the database's fleet_parts_* views, read every 5 minutes. Sub Rules and Craft? are
// set in the app. Look ahead: also the parts of each sub's next build due within 3, 7 or 14 days (the Subs page's Next
// Build), after what's needed now.
public partial class MainWindow
{
    private string partsFilter = "";
    private int partsAheadDays;
    private static readonly int[] AheadChoices = { 0, 3, 7, 14 };
    private (PartsRead Parts, RoutesRead? Routes, int Days, List<PartNeed> Needs)? partsAhead;

    // What's needed now, plus (with Look ahead) the builds coming up: worked out again only when a read or the days change.
    private List<PartNeed> PartsWithAhead(PartsRead parts)
    {
        var routes = reader.Routes.Data;
        if (partsAheadDays == 0 || parts.Inputs == null || routes == null) return parts.Needs;
        if (partsAhead is { } kept && ReferenceEquals(kept.Parts, parts) && ReferenceEquals(kept.Routes, routes) && kept.Days == partsAheadDays) return kept.Needs;
        var needs = PartsPlanner.Plan(parts.Inputs, SubRoutes.Upcoming(routes.Subs, NowMs, partsAheadDays));
        partsAhead = (parts, routes, partsAheadDays, needs);
        return needs;
    }
    private FleetTable<PartNeed>? partsTable;
    private FleetTable<(string Slot, string Needed, int Count)>? partsCraftTable;

    // Where a part comes from, as the app's Status: a spare, another sub's old part, or crafting.
    private static string PartStatus(PartNeed n) =>
        n.RecycledFrom != null ? $"Reuse: {n.SpareLocation}" : n.SpareLocation != null ? $"Spare: {n.SpareLocation}" : "Needs crafting";

    private FleetTable<PartNeed> PartsTable => partsTable ??= new FleetTable<PartNeed>("parts", plugin.Configuration, new List<FleetColumn<PartNeed>>
    {
        // The character once, on its first part (while the table isn't sorted by a column).
        new("character", "Character", n => { if (!PartsTable.SameAsAbove) CharacterCell(n.Character, n.World); }, n => n.Character),
        new("account", "Account", n => ImGui.TextUnformatted(n.Account.Length > 0 ? n.Account : "—"), n => n.Account, Hidden: true),
        new("sub", "Sub", n => ImGui.TextUnformatted(n.SubName), n => n.SubName, Locked: true),
        new("rank", "Rank", n =>
        {
            if (n.Rank > 0) RightNumber(n.Rank);
            else { TableCells.Right("new", Dim); Hover("A sub not built yet: its first build."); }
        }, n => n.Rank),
        new("slot", "Slot", n => ImGui.TextUnformatted(n.Slot), n => System.Array.IndexOf(PartsPlanner.Slots, n.Slot)),
        new("needed", "Needed", n => ImGui.TextUnformatted(n.Needed), n => n.Needed),
        // Look ahead: when a build coming up is due (needed now: blank).
        new("due", "Due", n =>
        {
            if (n.DueAt is not double due) return;
            Colored(Dim, TimeUntil(due, NowMs));
            Hover($"Its next build (rank {n.Rank}), due about {LocalDate(due)}: the Subs page's Next Build.");
        }, n => n.DueAt ?? 0),
        // Covered (a spare, or another sub's old part) in green, to craft in amber.
        new("status", "Status", n =>
        {
            if (n.ToCraft) Colored(Amber, "Needs crafting");
            else
            {
                Colored(Green, PartStatus(n));
                if (n.RecycledFrom != null) Hover($"When {n.RecycledFrom} gets its new {n.Slot}, its old one can go on {n.SubName}.");
            }
        }, n => PartStatus(n)),
    }, n => n.Account, "parts", runKey: n => $"{n.Cid}");

    private FleetTable<(string Slot, string Needed, int Count)> PartsCraftTable => partsCraftTable ??= new FleetTable<(string Slot, string Needed, int Count)>("partsCraft",
        plugin.Configuration, new List<FleetColumn<(string Slot, string Needed, int Count)>>
        {
            new("slot", "Slot", x => ImGui.TextUnformatted(x.Slot), x => System.Array.IndexOf(PartsPlanner.Slots, x.Slot), Locked: true),
            new("needed", "Needed", x => ImGui.TextUnformatted(x.Needed), x => x.Needed),
            new("count", "Still to craft", x => RightNumber(x.Count, Amber), x => x.Count),
        });

    private void DrawParts()
    {
        reader.ReadIfDue();
        RefreshButton();
        FilterBox(ref partsFilter, "parts");
        ReadLine(reader.Parts, FleetReader.PartsEvery);
        var parts = reader.Parts.Data;
        if (parts == null) return;
        // Look ahead, as the app's Parts tab.
        ImGui.SetNextItemWidth(120 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("Look ahead##partsAhead", partsAheadDays == 0 ? "Now only" : $"{partsAheadDays} days"))
        {
            if (combo)
                foreach (var d in AheadChoices)
                    if (ImGui.Selectable(d == 0 ? "Now only" : $"{d} days", d == partsAheadDays)) partsAheadDays = d;
        }
        Hover("Also the parts of each sub's next build due within that many days (the Subs page's Next Build), after what's needed now, soonest first.");
        if (partsAheadDays > 0 && reader.Routes.Data == null) Colored(Dim, reader.Routes.Error is { } routesError ? $"Look ahead needs the routes: {routesError}" : "Reading the routes...");
        var now = parts.Needs;
        var needs = PartsWithAhead(parts);
        var toCraft = needs.Count(n => n.ToCraft);
        var comingUp = needs.Count(n => n.Ahead);
        var ready = PartsPlanner.SubsReady(now);
        Heading(needs.Count == 0 ? "Every sub has the build it should have." :
            $"{needs.Count} part{(needs.Count == 1 ? "" : "s")} needed, {toCraft} to craft" + (comingUp > 0 ? $" ({comingUp} for builds coming up)" : ""));
        if (ready.Count > 0) Colored(Green, PartsPlanner.ReadyText(ready));
        Colored(Dim, "From each sub's Sub Rules, or Fight Club's best build for a sub ticked Craft? from rank 85 (both set in the app). Spares in bags, " +
                     "retainers and the FC chest are used first, then the parts other subs give up when upgraded.");

        using var tabs = ImRaii.TabBar("##partsTabs");
        if (!tabs) return;
        using (var tab = ImRaii.TabItem("Detail"))
        {
            if (tab)
            {
                PartsTable.DrawControls();
                var rows = needs.Where(n => Matches(partsFilter, n.Character, n.World, n.Account, n.SubName, n.Slot, n.Needed, PartStatus(n))).ToList();
                if (rows.Count == 0) ImGui.TextUnformatted(needs.Count == 0 ? "Nothing to change." : "Nothing matches.");
                else PartsTable.Draw(FleetOrder.Roster(rows, n => n.SortIndex ?? int.MaxValue, n => n.Character));
            }
        }
        using (var tab = ImRaii.TabItem("To craft"))
        {
            if (tab)
            {
                var craft = PartsPlanner.ToCraft(needs.Where(n => Matches(partsFilter, n.Character, n.World, n.Account, n.SubName, n.Slot, n.Needed)));
                if (craft.Count == 0) ImGui.TextUnformatted("Nothing needs crafting: every part needed is covered by a spare or another sub's old part.");
                else
                {
                    PartsCraftTable.DrawControls();
                    PartsCraftTable.Draw(craft);
                }
            }
        }
    }
}
