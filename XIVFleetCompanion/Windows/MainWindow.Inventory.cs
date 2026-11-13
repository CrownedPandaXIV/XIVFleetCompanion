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
            new("character", "Character", h => CharacterCell(h.Who, h.World), h => h.Who),
            new("location", "Location", h => Ui.Text(FleetOrder.CharacterLocation(h)), h => FleetOrder.CharacterLocation(h)),
            new("item", "Item", h => Ui.Text(ItemName(h.ItemId, h.Hq)), h => ItemName(h.ItemId, h.Hq), Locked: true),
            new("quantity", "Quantity", h => RightNumber(h.Quantity), h => h.Quantity),
        });

    private FleetTable<HoldingRow> FcInventoryTable => fcInventoryTable ??= new FleetTable<HoldingRow>("inventoryFcs", plugin.Configuration,
        new List<FleetColumn<HoldingRow>>
        {
            new("fc", "FC", h => Ui.Text(h.Who), h => h.Who),
            new("tab", "Tab", h => Ui.Text($"Tab {h.Container - 20000 + 1}"), h => h.Container),
            new("item", "Item", h => Ui.Text(ItemName(h.ItemId, h.Hq)), h => ItemName(h.ItemId, h.Hq), Locked: true),
            new("quantity", "Quantity", h => RightNumber(h.Quantity), h => h.Quantity),
        });

    private FleetTable<ItemTotalRow> FleetTotalsTable => fleetTotalsTable ??= new FleetTable<ItemTotalRow>("inventoryTotals", plugin.Configuration,
        new List<FleetColumn<ItemTotalRow>>
        {
            new("item", "Item", t => Ui.Text(ItemName(t.ItemId, t.Hq)), t => ItemName(t.ItemId, t.Hq), Locked: true),
            new("total", "Total Quantity", t => RightNumber(t.Total), t => t.Total),
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
            if (Ui.Checkbox($"{name(o)}##{key(o)}", ref on))
            {
                if (on) chosen.Add(key(o)); else chosen.Remove(key(o));
            }
        }
    }

    // "Hide equipped gear": one choice for Characters, Fleet totals and Where is it?, saved.
    private void HideEquippedBox()
    {
        ImGui.SameLine();
        var hide = plugin.Configuration.HideEquippedGear;
        if (Ui.Checkbox("Hide equipped gear##hideEquipped", ref hide))
        {
            plugin.Configuration.HideEquippedGear = hide;
            plugin.Configuration.Save();
        }
        Hover("Leave out gear being worn by the characters and their retainers.");
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
        HideEquippedBox();
        if (inventoryCharacters.Count == 0)
        {
            Ui.Wrapped("Choose one or more characters to see what they and their retainers hold.");
            return;
        }
        var ids = inventoryCharacters.ToArray();
        var key = "characters:" + Ids(ids);
        reader.ReadHoldingsIfDue(key, "kind <> 'fc' AND container <> 2000", "owner_cid", ids.Cast<object>().ToList());
        DrawHoldings(key, CharacterInventoryTable, rows => rows
            .Where(h => !(plugin.Configuration.HideEquippedGear && h.Equipped))
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
            Ui.Wrapped("Choose one or more Free Companies to see what their chests hold.");
            return;
        }
        var ids = inventoryFcs.ToArray();
        var key = "fcs:" + Ids(ids);
        reader.ReadHoldingsIfDue(key, "kind = 'fc' AND container BETWEEN 20000 AND 20004", "fc_id", ids.Cast<object>().ToList());
        DrawHoldings(key, FcInventoryTable, rows => rows.OrderBy(h => h.Who).ThenBy(h => h.Container).ThenBy(h => ItemName(h.ItemId, h.Hq)).ToList());
    }

    // What the Inventory page read for key, or why not yet.
    private void DrawHoldings(string key, FleetTable<HoldingRow> table, Func<IEnumerable<HoldingRow>, List<HoldingRow>> order)
    {
        if (reader.HoldingsKey != key || reader.Holdings.Data == null)
        {
            if (reader.HoldingsKey == key && reader.Holdings.Error != null) Colored(Amber, reader.Holdings.Error);
            else Ui.Text("Reading...");
            return;
        }
        ReadLine(reader.Holdings, FleetReader.ListsEvery);
        var rows = reader.Holdings.Data.Where(h => Matches(inventoryFilter, ItemName(h.ItemId, h.Hq), h.Who, h.Place));
        var list = order(rows);
        if (list.Count == 0) { Ui.Text(inventoryFilter.Length > 0 ? "Nothing matches." : "Nothing held."); return; }
        table.Draw(list);
    }

    private void DrawFleetTotals()
    {
        reader.ReadItemTotalsIfDue();
        FleetTotalsTable.DrawControls();
        FilterBox(ref inventoryFilter, "inventory");
        HideEquippedBox();
        ReadLine(reader.ItemTotals, FleetReader.ListsEvery);
        var totals = reader.ItemTotals.Data;
        if (totals == null) return;
        // Hiding equipped gear counts only what isn't being worn; an item that is only worn drops out.
        if (plugin.Configuration.HideEquippedGear)
            totals = totals.Select(t => t with { Total = t.Total - t.Equipped, Equipped = 0 }).Where(t => t.Total > 0).ToList();
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
        Ui.Checkbox("Group by account##whereIsIt", ref whereByAccount);
        HideEquippedBox();
        var held = reader.HeldItems.Data;
        if (reader.HeldItems.Error != null) Colored(Amber, reader.HeldItems.Error);
        var term = whereIsItSearch.Trim();
        if (term.Length < 2)
        {
            Ui.Wrapped("Type at least two letters of an item's name to see every place the fleet holds it: bags, saddlebags, " +
                              "armoury, retainers (and what they have for sale) and the Free Company chests. HQ copies count with the normal ones.");
            return;
        }
        if (held == null) { Ui.Text("Reading..."); return; }

        // Which held items match, by their names in the game's files.
        var matches = held.Select(id => (Id: id, Name: GameNames.Item(id)))
            .Where(x => FleetOrder.MatchesWords(x.Name, term))
            .OrderBy(x => x.Name, StringComparer.CurrentCulture).ToList();
        if (matches.Count == 0) { Ui.Text("Nothing the fleet holds matches."); return; }
        var more = matches.Count > WhereIsItLimit;
        var ids = matches.Take(WhereIsItLimit).Select(x => x.Id).OrderBy(x => x).ToArray();
        var key = "where:" + string.Join(",", ids);
        reader.ReadHoldingsIfDue(key, "NOT money", "item_id", ids.Cast<object>().ToList());
        if (reader.HoldingsKey != key || reader.Holdings.Data == null)
        {
            if (reader.HoldingsKey == key && reader.Holdings.Error != null) Colored(Amber, reader.Holdings.Error);
            else Ui.Text("Reading...");
            return;
        }
        if (more) Colored(Dim, $"The first {WhereIsItLimit} items that match; type more of the name to narrow it down.");

        using var child = ImRaii.Child("##whereResults", System.Numerics.Vector2.Zero, false);
        if (!child.Success) return;
        var all = FleetOrder.WhereIsIt(reader.Holdings.Data, id => ItemName(id, false)).ToList();
        // Hiding equipped gear leaves out the places where it's being worn, and the items held only as worn gear. The
        // widths come from everything, so the columns don't move when it's ticked.
        var items = plugin.Configuration.HideEquippedGear
            ? FleetOrder.WhereIsIt(reader.Holdings.Data.Where(h => !h.Equipped), id => ItemName(id, false)).ToList()
            : all;
        var widths = WhereWidths(reader.Holdings.Data, all);
        TableExportUi.Button("whereIsIt", "Where is it", () =>
        {
            var rows = new List<List<string>> { new() { "Item", "Character / FC", "World", "Where", "Quantity" } };
            foreach (var item in items)
                foreach (var p in item.Places)
                    rows.Add(new List<string> { item.Name, WhoText(p), p.World, p.Where, p.Quantity.ToString("N0", CultureInfo.CurrentCulture) });
            return rows;
        });
        if (items.Count == 0) Ui.Text("Only equipped gear matches.");
        foreach (var item in items)
        {
            if (!ImGui.CollapsingHeader($"{item.Name}: {item.Total:N0}##where{item.ItemId}", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            var groups = whereByAccount ? FleetOrder.ByAccount(item.Places, p => p.Account) : new List<(string, List<ItemPlace>)> { ("", item.Places) };
            using var table = Ui.Table($"##whereTable{item.ItemId}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
            if (!table) continue;
            // The same widths in every item's table (from the widest text in all of them), so the columns line up.
            Ui.SetupColumn("Character / FC", ImGuiTableColumnFlags.WidthFixed, widths[0]);
            Ui.SetupColumn("World", ImGuiTableColumnFlags.WidthFixed, widths[1]);
            Ui.SetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, widths[2]);
            Ui.SetupColumn("Quantity", ImGuiTableColumnFlags.WidthStretch);
            Ui.HeadersRow();
            foreach (var (label, places) in groups)
            {
                if (whereByAccount)
                {
                    Ui.NextRow();
                    Ui.SetColumnIndex(0);
                    Ui.SetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableHeaderBg));
                    Ui.Text(label);
                }
                foreach (var p in places)
                {
                    Ui.NextRow();
                    Ui.NextColumn();
                    Ui.Text(WhoText(p));
                    Ui.NextColumn();
                    Ui.Text(p.World);
                    Ui.NextColumn();
                    Ui.Text(p.Where);
                    Ui.NextColumn();
                    Ui.Text(p.Quantity.ToString("N0", CultureInfo.CurrentCulture));
                }
            }
        }
    }

    private static string WhoText(ItemPlace p) => p.Kind == "fc" ? $"{p.Who} (FC)" : p.Who;

    // Widths for Where is it?'s columns, worked out once per read (and again if the font size changes).
    private object? whereWidthsFor;
    private float whereWidthsFont;
    private float[] whereWidths = new float[3];

    private float[] WhereWidths(object data, List<ItemPlaces> items)
    {
        var font = ImGui.GetFontSize();
        if (ReferenceEquals(whereWidthsFor, data) && whereWidthsFont == font) return whereWidths;
        float Width(string text) => ImGui.CalcTextSize(text).X;
        var w = new[] { Width("Character / FC"), Width("World"), Width("Where") };
        foreach (var p in items.SelectMany(i => i.Places))
        {
            w[0] = Math.Max(w[0], Math.Max(Width(WhoText(p)), Width(p.Account.Length > 0 ? p.Account : "Ungrouped")));
            w[1] = Math.Max(w[1], Width(p.World));
            w[2] = Math.Max(w[2], Width(p.Where));
        }
        // A little room between the text and the next column.
        var pad = ImGui.GetStyle().CellPadding.X * 2 + ImGui.GetStyle().ItemSpacing.X;
        whereWidths = w.Select(x => x + pad).ToArray();
        whereWidthsFor = data;
        whereWidthsFont = font;
        return whereWidths;
    }
}
