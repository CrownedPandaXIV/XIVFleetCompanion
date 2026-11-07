using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace XIVFleetCompanion.Windows;

// 2.x, the Salvage and Income pages, as the app's tabs: the salvage each character holds (fleet_salvage_view), and the
// real net income from the subs plus the venture rewards over the Income window (fleet_income_view and
// fleet_venture_income_view). Read every 5 minutes while one of them is shown. Expected: the subs' Current Gil/Day
// (SubRoutes); Loot per Voyage (LootMath) from fleet_voyage_loot_view; items are valued at what a vendor pays, from the
// game's files (salvage at its fixed price), and with live prices at the market too.
public partial class MainWindow
{
    private string salvageFilter = "";
    private string incomeFilter = "";
    private FleetTable<SalvageRow>? salvageTable;
    private FleetTable<IncomeRow>? incomeTable;
    private FleetTable<FleetReader.VentureSummary>? venturesTable;
    private FleetTable<FleetReader.VentureSummary>? venturesLiveTable;
    private Dictionary<long, Dictionary<string, double>>? ventureMarket;

    private static string Gil(double value) => Math.Round(value).ToString("N0", CultureInfo.CurrentCulture);

    private static int SalvagePrice(int itemId) => itemId switch
    {
        22500 => 8000, 22501 => 9000, 22502 => 10000, 22503 => 13000,
        22504 => 27000, 22505 => 28500, 22506 => 30000, 22507 => 34500,
        _ => 0,
    };

    // What a vendor pays for an item (the game's files); 0 when it can't be sold.
    private static double VendorPrice(int itemId) => GameNames.VendorPrice(itemId);

    // The pieces making up a row's standard (22500 to 22503) or extravagant (22504 to 22507) salvage, on hover.
    private static void SalvageBreakdown(SalvageRow s, bool standard)
    {
        var lines = Enumerable.Range(0, 8).Where(i => standard ? i < 4 : i >= 4).Where(i => s.Pieces[i] > 0)
            .Select(i => $"{ItemName(22500 + i, false)}: {Number(s.Pieces[i])} × {Gil(SalvagePrice(22500 + i))} = {Gil(s.Pieces[i] * (double)SalvagePrice(22500 + i))}")
            .ToList();
        if (lines.Count == 0) return;
        var pieces = Enumerable.Range(0, 8).Where(i => standard ? i < 4 : i >= 4).Sum(i => s.Pieces[i]);
        var gil = Enumerable.Range(0, 8).Where(i => standard ? i < 4 : i >= 4).Sum(i => s.Pieces[i] * (double)SalvagePrice(22500 + i));
        lines.Add($"Total: {Number(pieces)} piece{(pieces == 1 ? "" : "s")}, {Gil(gil)} gil");
        Hover(string.Join("\n", lines));
    }

    private FleetTable<SalvageRow> SalvageTable => salvageTable ??= new FleetTable<SalvageRow>("salvage", plugin.Configuration, new List<FleetColumn<SalvageRow>>
    {
        new("character", "Character", s =>
        {
            CharacterCell(s.Character, s.World);
            TrendButton("character", s.Cid.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Character);
        }, s => s.Character, Locked: true),
        new("account", "Account", s => ImGui.TextUnformatted(s.Account.Length > 0 ? s.Account : "—"), s => s.Account, Hidden: true),
        new("standard", "Standard Salvage", s => { RightNumber(s.Standard); SalvageBreakdown(s, true); }, s => s.Standard),
        new("extravagant", "Extravagant Salvage", s => { RightNumber(s.Extravagant); SalvageBreakdown(s, false); }, s => s.Extravagant),
        new("gil", "Value", s => GilCell(s.Gil), s => s.Gil),
    }, s => s.Account, "characters");

    private void DrawSalvage()
    {
        reader.ReadIfDue(withMoney: true);
        RefreshButton();
        ImGui.SameLine();
        SalvageTable.DrawControls();
        FilterBox(ref salvageFilter, "salvage");
        ReadLine(reader.Salvage, FleetReader.MoneyEvery);
        TrendCardSection("Salvage value, whole fleet (gil)", "salvage_value");
        var salvage = reader.Salvage.Data;
        if (salvage == null) return;
        var rows = salvage.Where(s => Matches(salvageFilter, s.Character, s.World, s.Account)).ToList();
        // The fleet's total first, as the app's Summary tile (the characters shown, when a filter is typed).
        ImGui.Spacing();
        GilLine("Fleet-Wide Salvage Total:", rows.Sum(s => s.Gil), rows.Count < salvage.Count ? " (the characters shown)" : "");
        ImGui.Separator();
        ImGui.TextUnformatted($"{Number(rows.Sum(s => s.Standard))} standard, {Number(rows.Sum(s => s.Extravagant))} extravagant");
        Colored(Dim, "Salvage in bags, retainers and saddlebags, and in the FC chest (counted on one member), at the fixed salvage prices.");
        if (rows.Count == 0) { ImGui.TextUnformatted(salvage.Count == 0 ? "No salvage held." : "Nothing matches."); return; }
        SalvageTable.Draw(rows);
    }

    private FleetTable<IncomeRow> IncomeTable => incomeTable ??= new FleetTable<IncomeRow>("income", plugin.Configuration, new List<FleetColumn<IncomeRow>>
    {
        new("character", "Character", c => CharacterCell(c.Character, c.World), c => c.Character, Locked: true),
        new("account", "Account", c => ImGui.TextUnformatted(c.Account.Length > 0 ? c.Account : "—"), c => c.Account, Hidden: true),
        new("subs", "Subs", c => RightNumber(c.Subs), c => c.Subs),
        new("salvage", "Salvage / day", c =>
        {
            if (!c.Enough) { Colored(Dim, "Not enough history"); return; }
            TableCells.Right(Gil(c.SalvagePerDay), Green);
            var hover = $"{Number(c.Pieces)} salvage pieces came in over {c.SpanDays:0.0} days";
            if (c.FromChest > 0) hover += $"; {Number(c.FromChest)} taken out of the FC chest are not counted";
            if (c.FirstStored > 0) hover += $"; {Number(c.FirstStored)} seen for the first time (a retainer first opened on a PC) are not counted";
            Hover(hover);
        }, c => c.Enough ? c.SalvagePerDay : 0, c => !c.Enough),
        new("repairs", "Repairs / day", c =>
        {
            if (!c.Enough) return;
            TableCells.Right(c.KitPerDay > 0 ? $"−{Gil(c.KitPerDay)}" : "0", c.KitPerDay > 0 ? Amber : (Vector4?)null);
            Hover($"{Number(c.KitsUsed)} repair kits used over {c.SpanDays:0.0} days");
        }, c => c.Enough ? c.KitPerDay : 0, c => !c.Enough),
        new("real", "Real net / day", c => { if (c.Enough) TableCells.Right(Gil(c.PerDay), c.PerDay > 0 ? Green : Red); }, c => c.Enough ? c.PerDay : 0, c => !c.Enough),
        // Routes stage: what Fight Club says its subs' routes should make (their Current Gil/Day added up), and how much of
        // that came in: amber under 90%, red under 75%, as the app.
        new("expected", "Expected / day", c =>
        {
            var e = ExpectedOf(c.Cid);
            if (e?.Expected is not double x) { Colored(Dim, "—"); Hover("None of its subs is on a route Fight Club lists."); return; }
            TableCells.Right(Gil(x) + (e.Value.Unmatched > 0 ? " *" : ""), Palette.Gil);
            if (e.Value.Unmatched > 0)
                Hover($"{e.Value.Unmatched} of {c.Subs} sub{(c.Subs == 1 ? " is" : "s are")} on a route Fight Club doesn't list, so {(e.Value.Unmatched == 1 ? "it is" : "they are")} left out of expected.");
        }, c => ExpectedOf(c.Cid)?.Expected ?? double.MinValue, c => ExpectedOf(c.Cid)?.Expected == null),
        new("share", "Of expected", c =>
        {
            if (Share(c) is not double sh) return;
            var text = $"{SubRoutes.Round(sh * 100):0}%";
            if (sh < 0.75) TableCells.Right(text, Red); else if (sh < 0.9) TableCells.Right(text, Amber); else TableCells.Right(text);
        }, c => Share(c) ?? double.MinValue, c => Share(c) == null),
    }, c => c.Account, "characters");

    // Real net a day against expected (null without enough history or nothing expected).
    private double? Share(IncomeRow c) => c.Enough && ExpectedOf(c.Cid)?.Expected is double e && e > 0 ? c.PerDay / e : null;

    private FleetTable<FleetReader.VentureSummary> VenturesTable => venturesTable ??= new FleetTable<FleetReader.VentureSummary>("ventureIncome", plugin.Configuration, VentureColumns(false), v => v.Account, "characters");
    private FleetTable<FleetReader.VentureSummary> VenturesLiveTable => venturesLiveTable ??= new FleetTable<FleetReader.VentureSummary>("ventureIncomeLive", plugin.Configuration, VentureColumns(true), v => v.Account, "characters");

    // With live prices, the rewards a day at the cheapest listing on the character's world, data center and region too.
    private List<FleetColumn<FleetReader.VentureSummary>> VentureColumns(bool live) => new List<FleetColumn<FleetReader.VentureSummary>>
        {
            new("character", "Character", v => CharacterCell(v.Character, v.World), v => v.Character, Locked: true),
            new("account", "Account", v => ImGui.TextUnformatted(v.Account.Length > 0 ? v.Account : "—"), v => v.Account, Hidden: true),
            new("retainers", "Retainers", v => RightNumber(v.Retainers), v => v.Retainers),
            new("rewards", "Rewards", v => RightNumber(v.Rewards), v => v.Rewards),
            new("perDay", "Vendor value / day", v => TableCells.Right(Gil(v.PerDay), Green), v => v.PerDay),
            new("top", "Most valuable", v => ImGui.TextUnformatted(string.Join(", ", v.Top.Select(t => $"{ItemName(t.ItemId, false)} ×{Number(t.Quantity)}")))),
        }.Concat(live ? Universalis.Scopes.Select(s => new FleetColumn<FleetReader.VentureSummary>($"market{s}", $"Market / day ({(s == "world" ? "World" : s == "dc" ? "DC" : "Region")})",
            v => { if (VentureMarketOf(v.Cid, s) is double g) TableCells.Right(Gil(g), Green); else Colored(Dim, "—"); },
            v => VentureMarketOf(v.Cid, s) ?? 0)) : Enumerable.Empty<FleetColumn<FleetReader.VentureSummary>>()).ToList();

    private double? VentureMarketOf(long cid, string scope) =>
        ventureMarket != null && ventureMarket.TryGetValue(cid, out var byScope) && byScope.TryGetValue(scope, out var gil) ? gil : null;

    private void DrawIncome()
    {
        reader.ReadIfDue(withMoney: true);
        RefreshButton();
        FilterBox(ref incomeFilter, "income");
        ReadLine(reader.Income, FleetReader.MoneyEvery);
        var income = reader.Income.Data;
        if (income == null) return;
        using var tabs = ImRaii.TabBar("##incomeTabs");
        if (!tabs) return;
        using (var tab = ImRaii.TabItem("Subs"))
        {
            if (tab) DrawSubIncome(income);
        }
        using (var tab = ImRaii.TabItem("Loot per voyage"))
        {
            if (tab) DrawLoot(income);
        }
        using (var tab = ImRaii.TabItem("Retainer ventures"))
        {
            if (tab) DrawVentureIncome(income);
        }
    }

    private void DrawSubIncome(IncomeRead income)
    {
        var counted = income.Characters.Where(c => c.Enough).ToList();
        var notEnough = income.Characters.Count - counted.Count;
        HeadingRuns(T("Fleet real net per day: "), GilRun(counted.Sum(c => c.PerDay)));
        Runs(GilRun(counted.Sum(c => c.SalvagePerDay), ""), T(" salvage − ", Dim), GilRun(counted.Sum(c => c.KitPerDay), ""), T($" repairs, over the last {income.WindowDays} days.", Dim));
        IncomeWindowEditor(income.WindowDays);
        ImGui.TextWrapped("Salvage: every piece that came into a character's bags or retainers, at its vendor price (selling or moving it is not counted).");
        if (income.KitCost > 0) Runs(T("Repairs: repair kits used at "), GilRun(income.KitCost), T(" each."));
        else ImGui.TextWrapped("Repairs: set the repair kit cost in the app's Settings to count them.");
        ImGui.TextWrapped((notEnough > 0 ? $"{notEnough} character{(notEnough == 1 ? " has" : "s have")} less than a day of salvage history. " : "") +
                          "Expected: what Fight Club says the subs' routes make a day after repair kits (the Subs page's Current Gil/Day).");
        // As the app's Against expected and Below expected tiles.
        var compared = counted.Where(c => Share(c) != null).ToList();
        if (compared.Count > 0)
        {
            var real = compared.Sum(c => c.PerDay);
            var expected = compared.Sum(c => ExpectedOf(c.Cid)!.Value.Expected!.Value);
            var share = real / expected;
            Vector4? tone = share < 0.75 ? Red : share < 0.9 ? Amber : null;
            Runs(T($"Against expected: {SubRoutes.Round(share * 100):0}% of ", tone), GilRun(expected), T($" a day ({compared.Count} character{(compared.Count == 1 ? "" : "s")} compared: {(real - expected >= 0 ? "+" : "−")}", tone),
                GilRun(Math.Abs(real - expected)), T(" a day)", tone));
            var below = compared.Count(c => Share(c) < 0.9);
            if (below > 0) Colored(Amber, $"{below} character{(below == 1 ? " is" : "s are")} below expected (amber under 90%, red under 75%).");
        }
        IncomeTable.DrawControls();
        var rows = income.Characters.Where(c => Matches(incomeFilter, c.Character, c.World, c.Account)).ToList();
        if (rows.Count == 0) { ImGui.TextUnformatted(income.Characters.Count == 0 ? "No characters with subs built yet." : "Nothing matches."); return; }
        IncomeTable.Draw(rows);
    }

    // Loot per Voyage, worked out in the background once per read (of the loot, the routes or the characters).
    private LootRead? lootRead;
    private (object? Rows, object? Routes, object? Parts) lootFor;
    private volatile bool lootWorking;
    private Dictionary<int, string> lootLetters = new();
    private FleetTable<LootSub>? lootSubsTable;
    private FleetTable<LootSub>? lootSubsLiveTable;
    private FleetTable<LootRoute>? lootRoutesTable;
    private FleetTable<LootRoute>? lootRoutesLiveTable;
    private Dictionary<string, LootMarket>? lootMarket;

    private LootRead? LootSummary(IncomeRead income)
    {
        var rows = reader.Loot.Data;
        if (rows == null) return null;
        var routes = reader.Routes.Data;
        var parts = reader.Parts.Data;
        if ((!ReferenceEquals(rows, lootFor.Rows) || !ReferenceEquals(routes, lootFor.Routes) || !ReferenceEquals(parts, lootFor.Parts)) && !lootWorking)
        {
            lootFor = (rows, routes, parts);
            lootWorking = true;
            // Asked here, on the game's thread: the vendor prices and the sectors' letters come from the game's files.
            var prices = rows.SelectMany(r => new[] { r.PrimaryItem, r.AdditionalItem }).OfType<int>().Distinct().ToDictionary(id => id, id => ListingVendor(id));
            lootLetters = GameNames.SubGame()?.Sectors.Where(x => x.Location.Length > 0).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Location) ?? new();
            var letters = lootLetters;
            var characters = parts?.Inputs?.Characters ?? new List<PartsCharacter>();
            var subs = routes?.Subs ?? new List<SubRoute>();
            var days = rows.Count > 0 ? rows[0].WindowDays : income.WindowDays;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { lootRead = LootMath.Summary(rows, days, id => prices.GetValueOrDefault(id), letters, characters, subs); }
                catch (Exception ex) { Plugin.Log.Warning(ex, "Could not work out Loot per Voyage"); }
                finally { lootWorking = false; }
            });
        }
        return lootRead;
    }

    private (double Gil, int Voyages, double PerVoyage, double PerDay)? LootSubMarket(LootSub s, string scope) =>
        lootMarket != null && lootMarket.TryGetValue(scope, out var m) && m.Subs.TryGetValue(s.Key, out var v) ? v : null;
    private (double Gil, int Voyages, double PerVoyage)? LootRouteMarket(LootRoute r, string scope) =>
        lootMarket != null && lootMarket.TryGetValue(scope, out var m) && m.Routes.TryGetValue(r.Route, out var v) ? v : null;
    private static string ScopeLabel(string scope) => scope == "world" ? "World" : scope == "dc" ? "DC" : "Region";

    private List<FleetColumn<LootSub>> LootSubColumns(bool live) => new List<FleetColumn<LootSub>>
        {
            new("character", "Character", s => { if (s.Character != null) CharacterCell(s.Character, s.World ?? ""); else Colored(Dim, "—"); }, s => s.Character ?? "", Locked: true),
            new("account", "Account", s => ImGui.TextUnformatted(string.IsNullOrEmpty(s.Account) ? "—" : s.Account), s => s.Account ?? "", Hidden: true),
            new("sub", "Sub", s => ImGui.TextUnformatted(s.SubName ?? "—"), s => s.SubName ?? ""),
            new("route", "Route", s => { ImGui.TextUnformatted(s.Route); Hover("The route it ran most often"); }, s => s.Route),
            new("voyages", "Voyages", s => RightNumber(s.Voyages), s => s.Voyages),
            new("perVoyage", "Vendor gil / voyage", s => GilCell((long)SubRoutes.Round(s.PerVoyage)), s => s.PerVoyage),
            new("perDay", "Vendor value / day", s => TableCells.Right(Gil(s.PerDay), Green), s => s.PerDay),
            new("expected", "Expected / day", s => GilDayCell(s.ExpectedPerDay, "No sub of this name on its character's Subs page matches Fight Club's routes."),
                s => s.ExpectedPerDay ?? double.MinValue, s => s.ExpectedPerDay == null),
        }.Concat(live ? Universalis.Scopes.Select(scope => new FleetColumn<LootSub>($"market{scope}", $"Market / day ({ScopeLabel(scope)})",
            s => { if (LootSubMarket(s, scope) is { } m) TableCells.Right(Gil(m.PerDay), Green); else Colored(Dim, "—"); },
            s => LootSubMarket(s, scope)?.PerDay ?? double.MinValue)) : Enumerable.Empty<FleetColumn<LootSub>>()).ToList();

    private List<FleetColumn<LootRoute>> LootRouteColumns(bool live) => new List<FleetColumn<LootRoute>>
        {
            new("route", "Route", r => ImGui.TextUnformatted(r.Route), r => r.Route, Locked: true),
            new("subs", "Subs", r => RightNumber(r.Subs), r => r.Subs),
            new("voyages", "Voyages", r => RightNumber(r.Voyages), r => r.Voyages),
            new("perVoyage", "Vendor gil / voyage", r => GilCell((long)SubRoutes.Round(r.PerVoyage)), r => r.PerVoyage),
        }.Concat(live ? Universalis.Scopes.Select(scope => new FleetColumn<LootRoute>($"market{scope}", $"Market / voyage ({ScopeLabel(scope)})",
            r => { if (LootRouteMarket(r, scope) is { } m) GilCell((long)SubRoutes.Round(m.PerVoyage)); else Colored(Dim, "—"); },
            r => LootRouteMarket(r, scope)?.PerVoyage ?? double.MinValue)) : Enumerable.Empty<FleetColumn<LootRoute>>()).ToList();

    private void DrawLoot(IncomeRead income)
    {
        if (reader.Loot.Error is { } error) { Colored(Amber, $"Loot per Voyage: {error}"); return; }
        var loot = LootSummary(income);
        if (loot == null) { ImGui.TextUnformatted("Working out..."); return; }
        lootMarket = marketCache.LootMarketFor(reader.Market.Data, loot, lootLetters, id => ListingVendor(id));
        var live = lootMarket != null;
        HeadingRuns(T("Loot per Voyage: "), GilRun(loot.PerDay), T(" a day"));
        Colored(Dim, $"What each voyage really brought back over the last {loot.WindowDays} days, from Submarine Tracker (items at what a vendor pays; salvage at the " +
                     "Salvage page's prices).");
        Runs(T($"{Number(loot.VoyageCount)} voyage{(loot.VoyageCount == 1 ? "" : "s")}, ", Dim), GilRun(loot.Gil), T($", {Number(loot.Salvage)} pieces of salvage.", Dim));
        if (live)
            Runs(new[] { T("At market prices: ", Dim) }.Concat(Universalis.Scopes.SelectMany((sc, i) => new[] { T(i > 0 ? ", " : "", Dim), GilRun(lootMarket![sc].PerDay, ""), T($" a day ({ScopeLabel(sc)})", Dim) }))
                .Append(T(", from Universalis.", Dim)).ToArray());
        else if (reader.Market.Data?.Settings.LivePrices == true && marketCache.Working) Colored(Dim, "Working out market prices...");
        if (loot.Subs.Count == 0)
        {
            ImGui.TextUnformatted("No voyages recorded in this time. Submarine Tracker records a voyage when its loot is collected on a PC running it.");
            return;
        }
        var subTable = live ? lootSubsLiveTable ??= new FleetTable<LootSub>("lootSubsLive", plugin.Configuration, LootSubColumns(true), s => s.Account ?? "", "subs")
                            : lootSubsTable ??= new FleetTable<LootSub>("lootSubs", plugin.Configuration, LootSubColumns(false), s => s.Account ?? "", "subs");
        subTable.DrawControls();
        var subs = loot.Subs.Where(s => Matches(incomeFilter, s.Character, s.World, s.Account, s.SubName, s.Route)).ToList();
        if (subs.Count == 0) ImGui.TextUnformatted("Nothing matches.");
        else subTable.Draw(FleetOrder.Roster(subs, s => s.SortIndex ?? int.MaxValue, s => s.Character ?? ""));
        Heading("By route");
        Colored(Dim, "Each route's voyages, best gil a voyage first.");
        var routeTable = live ? lootRoutesLiveTable ??= new FleetTable<LootRoute>("lootRoutesLive", plugin.Configuration, LootRouteColumns(true))
                              : lootRoutesTable ??= new FleetTable<LootRoute>("lootRoutes", plugin.Configuration, LootRouteColumns(false));
        routeTable.DrawControls();
        routeTable.Draw(loot.Routes.Where(r => Matches(incomeFilter, r.Route)).ToList());
    }

    private void DrawVentureIncome(IncomeRead income)
    {
        var ventures = FleetOrder.Roster(marketCache.VentureSummaries(income.Ventures, income.WindowDays, VendorPrice), v => v.SortIndex, v => v.Character);
        HeadingRuns(T("Retainer ventures: "), GilRun(ventures.Sum(v => v.PerDay)), T(" a day"));
        Colored(Dim, $"Venture rewards AutoRetainer recorded over the last {income.WindowDays} days, at what a vendor pays (items are often worth more on the market board).");
        Runs(T($"{Number(ventures.Sum(v => (long)v.Rewards))} rewards, ", Dim), GilRun(ventures.Sum(v => v.Gil)), T(".", Dim));
        ventureMarket = VentureMarketPerDay(income.Ventures, income.WindowDays);
        if (ventureMarket != null)
            Runs(new[] { T("At market prices: ", Dim) }.Concat(Universalis.Scopes.SelectMany((s, i) => new[] { T(i > 0 ? ", " : "", Dim), GilRun(ventureMarket.Values.Sum(v => v[s]), ""),
                T($" ({(s == "world" ? "World" : s == "dc" ? "DC" : "Region")})", Dim) })).Append(T(" a day, from Universalis.", Dim)).ToArray());
        var table = ventureMarket != null ? VenturesLiveTable : VenturesTable;
        table.DrawControls();
        var rows = ventures.Where(v => Matches(incomeFilter, v.Character, v.World, v.Account)).ToList();
        if (rows.Count == 0) { ImGui.TextUnformatted(ventures.Count == 0 ? "No venture rewards recorded in this time (AutoRetainer: \"Record statistics\")." : "Nothing matches."); return; }
        table.Draw(rows);
    }
}
