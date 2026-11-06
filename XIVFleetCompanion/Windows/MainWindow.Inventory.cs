using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XIVFleetCompanion.Windows;

// 2.x, the Inventory page, as the app's Inventory tab: Characters (what chosen characters and their retainers hold),
// Free Companies (chosen FC chests, tabs 1 to 5), Fleet totals (each item across the fleet) and Where is it? (find an
// item by name, with every place it sits). Read on demand (FleetReader's inventory reads), not on a timer, except
// every minute while a sub-page is shown; item names come from the game's own files.
public partial class MainWindow
{
    private readonly HashSet<long> inventoryCharacters = new();
    private readonly HashSet<decimal> inventoryFcs = new();
    private string inventoryFilter = "";
    private string whereIsItSearch = "";
    private bool whereByAccount = true;
    private FleetTable<HoldingRow>? characterInventoryTable;
    private FleetTable<HoldingRow>? fcInventoryTable;
    private FleetTable<ItemTotalRow>? fleetTotalsTable;

    private static string ItemName(int itemId, bool hq)
    {
        var name = GameNames.Item(itemId);
        if (name.Length == 0) name = $"Item {itemId}";
        return hq ? $"{name} (HQ)" : name;
    }

    private FleetTable<HoldingRow> CharacterInventoryTable => characterInventoryTable ??= new FleetTable<HoldingRow>("inventoryCharacters", plugin.Configuration,
        new List<FleetColumn<HoldingRow>>
        {
            new("character", "Character", h => ImGui.TextUnformatted($"{h.Who}{(h.World.Length > 0 ? $" ({h.World})" : "")}"), h => h.Who),
            new("location", "Location", h => ImGui.TextUnformatted(FleetOrder.CharacterLocation(h)), h => FleetOrder.CharacterLocation(h)),
            new("item", "Item", h => ImGui.TextUnformatted(ItemName(h.ItemId, h.Hq)), h => ItemName(h.ItemId, h.Hq), Locked: true),
            new("quantity", "Quantity", h => ImGui.TextUnformatted(Number(h.Quantity)), h => h.Quantity),
        });

    private FleetTable<HoldingRow> FcInventoryTable => fcInventoryTable ??= new FleetTable<HoldingRow>("inventoryFcs", plugin.Configuration,
        new List<FleetColumn<HoldingRow>>
        {
            new("fc", "FC", h => ImGui.TextUnformatted(h.Who), h => h.Who),
            new("tab", "Tab", h => ImGui.TextUnformatted($"Tab {h.Container - 20000 + 1}"), h => h.Container),
            new("item", "Item", h => ImGui.TextUnformatted(ItemName(h.ItemId, h.Hq)), h => ItemName(h.ItemId, h.Hq), Locked: true),
            new("quantity", "Quantity", h => ImGui.TextUnformatted(Number(h.Quantity)), h => h.Quantity),
        });

    private FleetTable<ItemTotalRow> FleetTotalsTable => fleetTotalsTable ??= new FleetTable<ItemTotalRow>("inventoryTotals", plugin.Configuration,
        new List<FleetColumn<ItemTotalRow>>
        {
            new("item", "Item", t => ImGui.TextUnformatted(ItemName(t.ItemId, t.Hq)), t => ItemName(t.ItemId, t.Hq), Locked: true),
            new("total", "Total Quantity", t => ImGui.TextUnformatted(Number(t.Total)), t => t.Total),
        });

    private void DrawInventory()
    {
        reader.ReadIfDue(withLists: true); // the characters and FCs to choose from
        using var tabs = ImRaii.TabBar("##inventoryTabs");
        if (!tabs) return;
        using (var tab = ImRaii.TabItem("Characters"))
        {
            if (tab) DrawCharacterInventory();
        }
        using (var tab = ImRaii.TabItem("Free Companies"))
        {
            if (tab) DrawFcInventory();
        }
        using (var tab = ImRaii.TabItem("Fleet totals"))
        {
            if (tab) DrawFleetTotals();
        }
        using (var tab = ImRaii.TabItem("Where is it?"))
        {
            if (tab) DrawWhereIsIt();
        }
    }

    // A "Choose ... (N)" button with a list to tick, in the given order.
    private static void ChooseList<T, TKey>(string label, string id, IReadOnlyList<T> options, Func<T, TKey> key, Func<T, string> name, HashSet<TKey> chosen) where TKey : notnull
    {
        if (ImGui.Button($"{label} ({chosen.Count})##{id}")) ImGui.OpenPopup($"choose##{id}");
        using var popup = ImRaii.Popup($"choose##{id}");
        if (!popup) return;
        if (ImGui.Button("All")) foreach (var o in options) chosen.Add(key(o));
        ImGui.SameLine();
        if (ImGui.Button("None")) chosen.Clear();
        using var list = ImRaii.Child($"##chooseList{id}", new System.Numerics.Vector2(320 * ImGuiHelpers.GlobalScale, Math.Min(400, options.Count * ImGui.GetFrameHeightWithSpacing() + 8) * ImGuiHelpers.GlobalScale), false);
        if (!list) return;
        foreach (var o in options)
        {
            var on = chosen.Contains(key(o));
            if (ImGui.Checkbox($"{name(o)}##{key(o)}", ref on))
            {
                if (on) chosen.Add(key(o)); else chosen.Remove(key(o));
            }
        }
    }

    private static string Ids<TKey>(IEnumerable<TKey> ids) => string.Join(",", ids.OrderBy(x => x));

    private void DrawCharacterInventory()
    {
        var roster = reader.Roster.Data ?? new List<RosterRow>();
        // In the Roster's order.
        ChooseList("Choose characters", "invChars", roster, c => c.Cid, c => $"{c.Character} ({c.World})", inventoryCharacters);
        ImGui.SameLine();
        CharacterInventoryTable.DrawControls();
        FilterBox(ref inventoryFilter, "inventory");
        if (inventoryCharacters.Count == 0)
        {
            ImGui.TextWrapped("Choose one or more characters to see what they and their retainers hold.");
            return;
        }
        var ids = inventoryCharacters.ToArray();
        var key = "characters:" + Ids(ids);
        reader.ReadHoldingsIfDue(key, "kind <> 'fc' AND container <> 2000 AND owner_cid = ANY(CAST(@ids AS numeric[]))",
            p => p.AddWithValue("ids", ids));
        DrawHoldings(key, CharacterInventoryTable, rows => rows
            .OrderBy(h => h.SortIndex).ThenBy(h => h.Who).ThenBy(h => h.Kind == "retainer" ? 1 : 0).ThenBy(h => h.HireOrder)
            .ThenBy(h => FleetOrder.CharacterLocation(h)).ThenBy(h => ItemName(h.ItemId, h.Hq)).ToList());
    }

    private void DrawFcInventory()
    {
        var fcs = reader.Fcs.Data ?? new List<FcRow>();
        ChooseList("Choose Free Companies", "invFcs", fcs, f => f.FcId, f => f.Name.Length > 0 ? f.Name : $"FC {f.FcId}", inventoryFcs);
        ImGui.SameLine();
        FcInventoryTable.DrawControls();
        FilterBox(ref inventoryFilter, "inventory");
        if (inventoryFcs.Count == 0)
        {
            ImGui.TextWrapped("Choose one or more Free Companies to see what their chests hold.");
            return;
        }
        var ids = inventoryFcs.ToArray();
        var key = "fcs:" + Ids(ids);
        reader.ReadHoldingsIfDue(key, "kind = 'fc' AND container BETWEEN 20000 AND 20004 AND fc_id = ANY(CAST(@ids AS numeric[]))",
            p => p.AddWithValue("ids", ids));
        DrawHoldings(key, FcInventoryTable, rows => rows.OrderBy(h => h.Who).ThenBy(h => h.Container).ThenBy(h => ItemName(h.ItemId, h.Hq)).ToList());
    }

    // What the Inventory page read for key, or why not yet.
    private void DrawHoldings(string key, FleetTable<HoldingRow> table, Func<IEnumerable<HoldingRow>, List<HoldingRow>> order)
    {
        if (reader.HoldingsKey != key || reader.Holdings.Data == null)
        {
            if (reader.HoldingsKey == key && reader.Holdings.Error != null) Colored(Amber, reader.Holdings.Error);
            else ImGui.TextUnformatted("Reading...");
            return;
        }
        ReadLine(reader.Holdings, FleetReader.ListsEvery);
        var rows = reader.Holdings.Data.Where(h => Matches(inventoryFilter, ItemName(h.ItemId, h.Hq), h.Who, h.Place));
        var list = order(rows);
        if (list.Count == 0) { ImGui.TextUnformatted(inventoryFilter.Length > 0 ? "Nothing matches." : "Nothing held."); return; }
        table.Draw(list);
    }

    private void DrawFleetTotals()
    {
        reader.ReadItemTotalsIfDue();
        FleetTotalsTable.DrawControls();
        FilterBox(ref inventoryFilter, "inventory");
        ReadLine(reader.ItemTotals, FleetReader.ListsEvery);
        var totals = reader.ItemTotals.Data;
        if (totals == null) return;
        // The most held first, as the app.
        FleetTotalsTable.Draw(totals.Where(t => Matches(inventoryFilter, ItemName(t.ItemId, t.Hq)))
            .OrderByDescending(t => t.Total).ThenBy(t => ItemName(t.ItemId, t.Hq)).ToList());
    }

    private const int WhereIsItLimit = 50;

    private void DrawWhereIsIt()
    {
        reader.ReadHeldItemsIfDue();
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Item name##whereIsIt", ref whereIsItSearch, 64);
        ImGui.SameLine();
        ImGui.Checkbox("Group by account##whereIsIt", ref whereByAccount);
        var held = reader.HeldItems.Data;
        if (reader.HeldItems.Error != null) Colored(Amber, reader.HeldItems.Error);
        var term = whereIsItSearch.Trim();
        if (term.Length < 2)
        {
            ImGui.TextWrapped("Type at least two letters of an item's name to see every place the fleet holds it: bags, saddlebags, " +
                              "armoury, retainers (and what they have for sale) and the Free Company chests. HQ copies count with the normal ones.");
            return;
        }
        if (held == null) { ImGui.TextUnformatted("Reading..."); return; }

        // Which held items match, by their names in the game's files.
        var matches = held.Select(id => (Id: id, Name: GameNames.Item(id)))
            .Where(x => x.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name, StringComparer.CurrentCulture).ToList();
        if (matches.Count == 0) { ImGui.TextUnformatted("Nothing the fleet holds matches."); return; }
        var more = matches.Count > WhereIsItLimit;
        var ids = matches.Take(WhereIsItLimit).Select(x => x.Id).OrderBy(x => x).ToArray();
        var key = "where:" + string.Join(",", ids);
        reader.ReadHoldingsIfDue(key, "NOT money AND item_id = ANY(@ids)", p => p.AddWithValue("ids", ids));
        if (reader.HoldingsKey != key || reader.Holdings.Data == null)
        {
            if (reader.HoldingsKey == key && reader.Holdings.Error != null) Colored(Amber, reader.Holdings.Error);
            else ImGui.TextUnformatted("Reading...");
            return;
        }
        if (more) Colored(Dim, $"The first {WhereIsItLimit} items that match; type more of the name to narrow it down.");

        using var child = ImRaii.Child("##whereResults", System.Numerics.Vector2.Zero, false);
        if (!child.Success) return;
        foreach (var item in FleetOrder.WhereIsIt(reader.Holdings.Data, id => ItemName(id, false)))
        {
            if (!ImGui.CollapsingHeader($"{item.Name}: {item.Total:N0}##where{item.ItemId}", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            var groups = whereByAccount ? FleetOrder.ByAccount(item.Places, p => p.Account) : new List<(string, List<ItemPlace>)> { ("", item.Places) };
            using var table = ImRaii.Table($"##whereTable{item.ItemId}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
            if (!table) continue;
            ImGui.TableSetupColumn("Character / FC");
            ImGui.TableSetupColumn("World");
            ImGui.TableSetupColumn("Where");
            ImGui.TableSetupColumn("Quantity");
            ImGui.TableHeadersRow();
            foreach (var (label, places) in groups)
            {
                if (whereByAccount)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableHeaderBg));
                    ImGui.TextUnformatted(label);
                }
                foreach (var p in places)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.Kind == "fc" ? $"{p.Who} (FC)" : p.Who);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.World);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.Where);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.Quantity.ToString("N0", CultureInfo.CurrentCulture));
                }
            }
        }
    }
}
