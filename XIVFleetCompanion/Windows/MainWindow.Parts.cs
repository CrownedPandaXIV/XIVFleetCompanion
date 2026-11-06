using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion.Windows;

// 2.x batch 4, the Parts page, as the app's Parts tab: every part a sub needs for the build it should have at its rank
// (its Sub Rules, or Fight Club's best build for a sub ticked Craft? from rank 85), a new workshop's first sub and every
// unlocked empty slot; and whether a spare covers it, the part another sub gives up when upgraded, or it needs crafting.
// Worked out here (PartsPlanner) from the database's fleet_parts_* views, read every 5 minutes. Sub Rules and Craft? are
// set in the app; Look ahead (builds coming up) comes later in 2.x, with the rank-up forecasts.
public partial class MainWindow
{
    private string partsFilter = "";
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
        var needs = parts.Needs;
        var toCraft = needs.Count(n => n.ToCraft);
        var ready = PartsPlanner.SubsReady(needs);
        Heading(needs.Count == 0 ? "Every sub has the build it should have." :
            $"{needs.Count} part{(needs.Count == 1 ? "" : "s")} needed, {toCraft} to craft");
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
