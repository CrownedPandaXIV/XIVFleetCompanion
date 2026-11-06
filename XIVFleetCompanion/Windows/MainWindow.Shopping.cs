using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Windows;

// 2.x Shopping List stage, the Parts page's Shopping List tab, as the app's (Parts tab → Shopping List): the sub parts
// to craft (those no spare or old part covers, with Look ahead's too), everything to craft on the way, and the raw
// materials and crystals to gather or buy, less what the fleet holds; with Live Market Prices on, what buying instead
// costs. Worked out in the background (ShoppingMath) from the Parts page's needs, the game's own recipes and what the
// database says is held, whenever one of them or a choice here changes. Ticking parts off as they're made is done in
// the app for now; the ticks show here.
public partial class MainWindow
{
    private ShoppingResult? shoppingList;
    private object? shoppingKey;
    private volatile bool shoppingWorking;
    private FleetTable<ShopRow>? shopPartsTable, shopPartsPricedTable, shopBuyTable;
    // Craft first, Gather or buy and Crystals, with and without the market columns, by their saved names.
    private readonly Dictionary<string, FleetTable<ShopRow>> shopItemTables = new();

    // Universalis for the Shopping List: the places it knows, and one place's listings (kept for 30 minutes, shared with
    // the Gil & Market page's).
    private sealed class ShoppingUniversalis : IShoppingMarket
    {
        private readonly Universalis universalis;
        public ShoppingUniversalis(Universalis universalis) => this.universalis = universalis;
        public Dictionary<string, (string Name, string Dc, string Region)> Places() => universalis.PlacesAsync().GetAwaiter().GetResult();
        public (Dictionary<int, List<MarketListing>> Listings, (string Problem, DateTime Since)? Stale) ListingsIn(string scope, List<int> ids) =>
            universalis.ListingsInAsync(scope, ids).GetAwaiter().GetResult();
    }

    // Works the list out again in the background when the parts, the routes (Look ahead), what's held or a choice changed.
    private void UpdateShopping(PartsRead parts, ShoppingRead shop)
    {
        var routes = reader.Routes.Data;
        var config = plugin.Configuration;
        var key = (parts, routes, shop, partsAheadDays, config.ShoppingBuyFromVendors, config.ShoppingPriceScope);
        if (shoppingWorking || Equals(shoppingKey, key)) return;
        shoppingWorking = true;
        var days = partsAheadDays;
        var buy = config.ShoppingBuyFromVendors;
        var scope = config.ShoppingPriceScope;
        var now = NowMs;
        var universalis = reader.UniversalisClient;
        Task.Run(() =>
        {
            try
            {
                var game = GameNames.ShopGame();
                if (game == null)
                {
                    shoppingList = new ShoppingResult { Error = "The crafting data couldn't be read from the game's files." };
                    return;
                }
                var longest = ShoppingMath.LongestAhead(days, shop.Progress);
                var needs = longest == 0 || parts.Inputs == null || routes == null
                    ? parts.Needs
                    : PartsPlanner.Plan(parts.Inputs, SubRoutes.Upcoming(routes.Subs, now, longest));
                shoppingList = ShoppingMath.Make(needs, now, days, shop.Progress, game, shop.Held, buy,
                    shop.LivePrices ? new ShoppingUniversalis(universalis) : null, shop.Worlds, shop.Exclude, scope);
            }
            catch (Exception ex)
            {
                shoppingList = new ShoppingResult { Error = $"Could not work out the shopping list: {ex.Message}" };
            }
            finally
            {
                shoppingKey = key;
                shoppingWorking = false;
            }
        });
    }

    // ---- Cells ----

    private static string HeldText(List<HeldPlace> places) =>
        string.Join("\n", places.GroupBy(p => p.Who).Select(g => $"{g.Key}: {string.Join(", ", g.Select(p => $"{p.Where} ({Number(p.Qty)})"))}"));

    // Where it's held: one character (or Free Company) in full; more as "3 places", each on hover.
    private void HeldCell(ShopRow r)
    {
        if (r.Places.Count == 0) return;
        var who = r.Places.Select(p => p.Who).Distinct().Count();
        if (who == 1) ImGui.TextUnformatted(HeldText(r.Places));
        else
        {
            Colored(Dim, $"{who} places");
            Hover(HeldText(r.Places));
        }
    }

    private void ShopGil(double? gil)
    {
        if (gil is double g) GilCell((long)SubRoutes.Round(g));
    }

    // The cheapest listing in gold, with its world when prices are from a data center or region; "none listed" when
    // nobody sells it there; "only 3 of 5 listed" when fewer are up than needed. Hover: what buying all those costs.
    private void MarketCell(ShopRow r, string? kind)
    {
        if (r.Market is not { } m)
        {
            Colored(Dim, "none listed");
            Hover("Nobody is selling it there right now.");
            return;
        }
        Tinted(Palette.Gil, $"{Number((long)SubRoutes.Round(m.Price))} gil");
        var buying = r.Buying;
        var title = buying == null ? null
            : $"Buying {(buying.Wanted == 1 ? "it" : $"all {Number(buying.Wanted)}")}: {Number((long)SubRoutes.Round(buying.Gil))} gil, going up the listings from the cheapest"
              + (buying.Got < buying.Wanted ? $". Only {Number(buying.Got)} of {Number(buying.Wanted)} are listed there; that price is for those." : ".");
        if (title != null) Hover(title);
        if (kind != "world" && m.World != null)
        {
            ImGui.SameLine(0, ImGui.CalcTextSize(" ").X);
            Colored(Dim, $"({m.World})");
        }
        if (buying != null && buying.Got < buying.Wanted)
        {
            ImGui.SameLine(0, ImGui.CalcTextSize(" ").X);
            Colored(Amber, $"· only {Number(buying.Got)} of {Number(buying.Wanted)} listed");
            if (title != null) Hover(title);
        }
    }

    private static readonly Dictionary<string, string> SourceLabels = new() { ["vendor"] = "Vendor", ["market"] = "Market board", ["make"] = "Make" };

    private const string MakeCostTitle = "One part made from its materials, each at its cheapest: a vendor, the market board, or making it. What you already hold is not taken off.";

    // The To make hover: each ingredient and where it comes from, one level deeper for ones that are made.
    private static string MakeTitle(List<ShopBreakdown>? breakdown, string? kind)
    {
        if (breakdown == null) return MakeCostTitle;
        static string Amount(double n) => n.ToString("#,0.##", System.Globalization.CultureInfo.CurrentCulture);
        static string GilText(double n) => $"{Number((long)SubRoutes.Round(n))} gil";
        string Where(ShopBreakdown x) => x.How == "vendor" ? "from the vendor"
            : x.How == "market" ? $"from the market board{(kind != "world" && x.World != null ? $" ({x.World})" : "")}" : "made";
        var lines = breakdown.Select(x =>
        {
            var head = $"{x.Name} ×{Amount(x.Amount)}";
            if (x.Each is not double each) return $"{head}: no price (not sold by a vendor or listed there)";
            if (x.How == "make" && x.Parts != null)
                return $"{head}: {GilText(each)} each (make: {string.Join(", ", x.Parts.Select(p => p.Each is double e ? $"{p.Name} ×{Amount(p.Amount)} at {GilText(e)} {Where(p)}" : $"{p.Name} ×{Amount(p.Amount)} with no price"))})";
            return $"{head}: {GilText(each)} each {Where(x)}";
        });
        return $"{MakeCostTitle}\n\n{string.Join("\n", lines)}";
    }

    // ---- Tables ----

    private FleetTable<ShopRow> ShopPartsTable(bool priced)
    {
        if (priced && shopPartsPricedTable != null) return shopPartsPricedTable;
        if (!priced && shopPartsTable != null) return shopPartsTable;
        var columns = new List<FleetColumn<ShopRow>>
        {
            new("part", "Part", p => ImGui.TextUnformatted(p.Name), p => p.Name, Locked: true),
            new("craft", "Craft", p => RightNumber(p.Crafts, p.Made >= p.Crafts ? Dim : Amber), p => p.Crafts),
            new("crafter", "Crafter", p => ImGui.TextUnformatted(p.Job ?? "?"), p => p.Job ?? ""),
        };
        if (priced)
        {
            columns.Add(new("market", "Market board", p => MarketCell(p, shoppingList?.Market.Kind), p => p.Market?.Price ?? double.MaxValue));
            columns.Add(new("makeCost", "To make", p =>
            {
                if (p.MakeCost is double cost)
                {
                    ShopGil(cost);
                    Hover(MakeTitle(p.MakeBreakdown, shoppingList?.Market.Kind));
                }
                else
                {
                    Colored(Dim, "—");
                    Hover("Some of its materials have no price: not sold by a vendor or listed there.");
                }
            }, p => p.MakeCost ?? double.MaxValue));
            columns.Add(new("cheaper", "Cheaper", p =>
            {
                if (p.Cheaper != null) Colored(Green, p.Cheaper == "buy" ? "Buy" : "Make");
            }, p => p.Cheaper ?? ""));
        }
        columns.Add(new("made", "Made", p =>
        {
            Colored(p.Made >= p.Crafts ? Green : p.Made > 0 ? Amber : Dim, $"{Number(p.Made)} of {Number(p.Crafts)}");
            Hover("Ticked off in the app (Parts → Shopping List), shared by every PC. Once the plugin sees a crafted part in bags, a retainer or the FC chest, it leaves the list by itself.");
        }, p => p.Made));
        var table = new FleetTable<ShopRow>(priced ? "shoppingPartsPriced" : "shoppingParts", plugin.Configuration, columns);
        if (priced) shopPartsPricedTable = table; else shopPartsTable = table;
        return table;
    }

    private FleetTable<ShopRow> ShopBuyTable => shopBuyTable ??= new FleetTable<ShopRow>("shoppingBuy", plugin.Configuration, new List<FleetColumn<ShopRow>>
    {
        new("item", "Item", r => ImGui.TextUnformatted(r.Name), r => r.Name, Locked: true),
        new("need", "Need", r => RightNumber(r.Need), r => r.Need),
        new("have", "Have", r => RightNumber(r.Have, r.Have > 0 ? null : Dim), r => r.Have),
        new("buy", "Buy", r => RightNumber(r.Short, r.Short > 0 ? Amber : Dim), r => r.Short),
        new("price", "Price", r => ShopGil(r.VendorPrice), r => r.VendorPrice ?? 0),
        new("cost", "Cost", r => ShopGil(r.Cost), r => r.Cost),
        new("held", "Held in", HeldCell),
    });

    // Craft first ("craft": Make and Crafter) or Gather or buy and Crystals (Short); market columns with prices on.
    private FleetTable<ShopRow> ShopItemTable(string kind, bool priced)
    {
        var key = (kind == "craft" ? "shoppingCraft" : kind == "gather" ? "shoppingGather" : "shoppingCrystals") + (priced ? "Priced" : "");
        if (shopItemTables.TryGetValue(key, out var kept)) return kept;
        var craft = kind == "craft";
        var columns = new List<FleetColumn<ShopRow>>
        {
            new("item", "Item", r => ImGui.TextUnformatted(r.Name), r => r.Name, Locked: true),
            new("need", "Need", r => RightNumber(r.Need), r => r.Need),
            new("have", "Have", r => RightNumber(r.Have, r.Have > 0 ? null : Dim), r => r.Have),
            new("missing", craft ? "Make" : "Short", r =>
            {
                RightNumber(r.Short, r.Short > 0 ? Amber : Dim);
                if (craft && r.Yield > 1 && r.Crafts > 0)
                {
                    ImGui.SameLine(0, ImGui.CalcTextSize(" ").X);
                    Colored(Dim, $"({Number(r.Crafts)} crafts of {r.Yield})");
                }
            }, r => r.Short),
        };
        if (craft) columns.Add(new("crafter", "Crafter", r => ImGui.TextUnformatted(r.Job ?? "?"), r => r.Job ?? ""));
        columns.Add(new("vendor", "Vendor", r => ShopGil(r.VendorPrice), r => r.VendorPrice ?? 0));
        if (priced)
        {
            columns.Add(new("market", "Market board", r => MarketCell(r, shoppingList?.Market.Kind), r => r.Market?.Price ?? double.MaxValue));
            columns.Add(new("cheapest", "Cheapest", r =>
            {
                if (r.Cheapest != null) ImGui.TextUnformatted(SourceLabels[r.Cheapest]);
                else
                {
                    Colored(Dim, "—");
                    Hover("Not sold by a vendor or listed there, and no price to make it from.");
                }
            }, r => r.Cheapest ?? ""));
        }
        columns.Add(new("held", "Held in", HeldCell));
        return shopItemTables[key] = new FleetTable<ShopRow>(key, plugin.Configuration, columns);
    }

    // One section: its heading, rows still short (all of them with "Show items already held in full"), and how many more are held in full.
    private void ShopSection(string title, string? note, List<ShopRow> rows, FleetTable<ShopRow> table, string? total = null)
    {
        var showHeld = plugin.Configuration.ShoppingShowHeld;
        var visible = showHeld ? rows : rows.Where(r => r.Short > 0).ToList();
        ImGui.Spacing();
        ImGui.TextUnformatted(title);
        if (total != null)
        {
            ImGui.SameLine(0, ImGui.CalcTextSize("  ").X);
            Tinted(Palette.Gil, total);
        }
        ImGui.Separator();
        if (note != null) Colored(Dim, note);
        if (visible.Count == 0) ImGui.TextUnformatted(rows.Count > 0 ? "Everything here is already held." : "Nothing.");
        else
        {
            table.DrawControls();
            table.Draw(visible);
        }
        if (rows.Count > visible.Count) Colored(Dim, $"{rows.Count - visible.Count} more already held in full.");
    }

    // "Prices from": a world, data center or region (each world with its data center).
    private void PriceScopeCombo(ShoppingMarketInfo market)
    {
        var config = plugin.Configuration;
        ImGui.SameLine(0, ImGui.CalcTextSize("    ").X);
        ImGui.SetNextItemWidth(200 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("Prices from##shoppingScope", market.Label ?? "—"))
        {
            if (combo)
                foreach (var (kind, label) in new[] { ("world", "World"), ("dc", "Data center"), ("region", "Region") })
                {
                    var these = (market.Choices ?? new()).Where(c => c.Kind == kind).ToList();
                    if (these.Count == 0) continue;
                    ImGui.TextDisabled(label);
                    foreach (var c in these)
                        if (ImGui.Selectable($"  {c.Label}##{c.Value}", c.Value == market.Scope))
                        {
                            config.ShoppingPriceScope = c.Value;
                            config.Save();
                        }
                }
        }
        Hover("Where the market board prices come from: one world, a data center, or a whole region. For a data center or region, each price says which world it is on.");
    }

    // Where the prices are from, what buying instead costs, and anything that went wrong.
    private void ShoppingMarketNote(ShoppingResult list)
    {
        var market = list.Market;
        if (market.Error != null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                ImGui.TextWrapped($"Market board prices are not shown: {market.Error}");
            return;
        }
        static string Missing(int none, int few)
        {
            var bits = new List<string>();
            if (none > 0) bits.Add($"{none} not listed");
            if (few > 0) bits.Add($"{few} with too few listed");
            return bits.Count > 0 ? $" ({string.Join(", ", bits)})" : "";
        }
        Colored(Dim, $"Market board prices from {market.Label}: the cheapest listing of each, leaving out your own retainers'. Totals go up the listings from the cheapest, as buying them would.");
        if (list.Totals is { } t)
        {
            GilLine("Buying the parts not made yet:", t.Parts, Missing(t.PartsUnlisted, t.PartsTooFew));
            GilLine("Buying every material still short:", t.Materials, Missing(t.MaterialsUnlisted, t.MaterialsTooFew));
        }
        if (market.Stale is { } stale)
            Colored(Dim, $"Universalis did not answer ({stale.Problem}), so some prices are from {stale.Since.ToLocalTime():t}.");
    }

    private bool shoppingCopied;
    private DateTime shoppingCopiedAt;

    private void DrawShopping(PartsRead parts)
    {
        reader.ReadShoppingIfDue();
        var config = plugin.Configuration;
        if (reader.Shopping.Error != null && reader.Shopping.Data == null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                ImGui.TextWrapped(reader.Shopping.Error);
            return;
        }
        if (reader.Shopping.Data is not { } shop)
        {
            ImGui.TextUnformatted("Reading what the fleet holds...");
            return;
        }
        UpdateShopping(parts, shop);
        var list = shoppingList;
        if (list == null)
        {
            ImGui.TextUnformatted("Working out the shopping list...");
            return;
        }
        if (list.Error != null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                ImGui.TextWrapped(list.Error);
            return;
        }
        if (list.Empty)
        {
            ImGui.TextWrapped("Nothing needs crafting: every need is either met, covered by a spare, or covered by a reused old part"
                + (partsAheadDays > 0 ? ", with no new build due in that time." : "."));
            return;
        }

        var buy = config.ShoppingBuyFromVendors;
        if (ImGui.Checkbox("Buy from vendors where they sell it", ref buy))
        {
            config.ShoppingBuyFromVendors = buy;
            config.Save();
        }
        Hover("Anything an NPC sells for gil is bought at its vendor price instead of being crafted or gathered, so its materials aren't needed.");
        ImGui.SameLine(0, ImGui.CalcTextSize("    ").X);
        var showHeld = config.ShoppingShowHeld;
        if (ImGui.Checkbox("Show items already held in full", ref showHeld))
        {
            config.ShoppingShowHeld = showHeld;
            config.Save();
        }
        if (list.Market.Enabled && list.Market.Choices is { Count: > 0 }) PriceScopeCombo(list.Market);
        ImGui.SameLine(0, ImGui.CalcTextSize("    ").X);
        if (shoppingCopied && DateTime.UtcNow - shoppingCopiedAt > TimeSpan.FromSeconds(2)) shoppingCopied = false;
        if (ImGui.Button(shoppingCopied ? "Copied##shoppingCopy" : "Copy as Text##shoppingCopy"))
        {
            ImGui.SetClipboardText(ShoppingMath.Text(list));
            shoppingCopied = true;
            shoppingCopiedAt = DateTime.UtcNow;
        }
        Hover("The list as plain text, to paste somewhere.");
        if (shoppingWorking)
        {
            ImGui.SameLine(0, ImGui.CalcTextSize("    ").X);
            Colored(Dim, list.Market.Enabled ? "Updating (asking Universalis)..." : "Updating...");
        }
        if (list.Market.Enabled) ShoppingMarketNote(list);
        Colored(Dim, "Held counts every character's bags, saddlebags and crystals, their retainers, and the Free Company chests, as of each character's last sync.");
        var noRecipe = list.Parts.Where(p => p.NoRecipe).ToList();
        if (noRecipe.Count > 0) Colored(Amber, $"No recipe found for {string.Join(", ", noRecipe.Select(p => p.Name))}.");

        var priced = list.Priced;
        ImGui.Spacing();
        ImGui.TextUnformatted("Sub parts to craft");
        ImGui.Separator();
        Colored(Dim, "Tick parts off as you craft them in the app (Parts → Shopping List); every PC sees it. Once the plugin sees a crafted part in bags, " +
                     "a retainer or the FC chest, it leaves the list by itself. The materials below are for every part on the list, made or not.");
        var partsTable = ShopPartsTable(priced);
        partsTable.DrawControls();
        partsTable.Draw(list.Parts);
        if (list.BuyFromVendors)
            ShopSection("Buy from vendors", "Sold by NPC vendors for gil, so these are not crafted and their materials are not needed.", list.Buy, ShopBuyTable,
                $"{Number((long)SubRoutes.Round(list.BuyCost))} gil");
        ShopSection("Craft first", "In-between items, in the order to make them.", list.Craft, ShopItemTable("craft", priced));
        ShopSection("Gather or buy", null, list.Gather, ShopItemTable("gather", priced));
        ShopSection("Crystals", null, list.Crystals, ShopItemTable("crystals", priced));
    }
}
