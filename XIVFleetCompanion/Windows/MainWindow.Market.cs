using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace XIVFleetCompanion.Windows;

// 2.x market prices, the Gil & Market page, as the app's Gil & Market tab: the fleet's gil per account, what the
// retainers have up for sale and what has sat unsold for a while (the app's days setting); with Live Market Prices on
// (the app's Settings), the cheapest someone else sells each item for on its world, data center and region, and the
// listings undercut on their own world. Read every 5 minutes (FleetReader.ReadMarket); Universalis is asked by the
// plugin itself, at most every 30 minutes for an item.
public partial class MainWindow
{
    private string marketFilter = "";
    private FleetTable<MarketAccountRow>? marketAccountsTable;
    private FleetTable<ComparedListing>? unsoldTable;
    private FleetTable<ComparedListing>? unsoldLiveTable;
    private FleetTable<ComparedListing>? undercutTable;

    private static string Age(double days)
    {
        var whole = (int)Math.Floor(days);
        return days < 1 ? "under a day" : $"{whole} day{(whole == 1 ? "" : "s")}";
    }

    // What a vendor pays for one (salvage at its fixed price, as the app).
    private static double ListingVendor(int itemId) => SalvagePrice(itemId) is > 0 and var fixedPrice ? fixedPrice : VendorPrice(itemId);

    private void RivalCell(ComparedListing l, string scope)
    {
        var p = l.In(scope);
        if (p == null) { Colored(Dim, "None"); return; }
        var text = Gil(p.Price) + (p.World != null && scope != "world" ? $" ({p.World})" : "");
        // Amber when someone sells it cheaper there.
        TableCells.Right(text, p.Price < l.Listing.UnitPrice ? Amber : (Vector4?)null);
        if (p.World != null) Hover($"On {p.World}");
    }

    private List<FleetColumn<ComparedListing>> ListingColumns(bool rivals, bool cheaperBy) => new List<FleetColumn<ComparedListing>>
    {
        new("item", "Item", x => ImGui.TextUnformatted(ItemName(x.Listing.ItemId, x.Listing.Hq)), x => ItemName(x.Listing.ItemId, x.Listing.Hq), Locked: true),
        new("qty", "Qty", x => RightNumber(x.Listing.Quantity), x => x.Listing.Quantity),
        new("price", "Price each", x => GilCell(x.Listing.UnitPrice), x => x.Listing.UnitPrice),
        new("vendor", "Vendor price", x =>
        {
            var v = ListingVendor(x.Listing.ItemId);
            if (v > 0) TableCells.Right(Gil(v), Dim); else Colored(Dim, "—");
            Hover("What a vendor pays for one (salvage at the Salvage page's prices)");
        }, x => ListingVendor(x.Listing.ItemId)),
    }
    .Concat(cheaperBy ? new List<FleetColumn<ComparedListing>> { new("cheaperBy", "Cheaper by", x => TableCells.Right(Gil(x.UndercutBy ?? 0), Amber), x => x.UndercutBy ?? 0) } : new())
    .Concat(rivals ? Universalis.Scopes.Select(scope => new FleetColumn<ComparedListing>($"cheapest{scope}", $"Cheapest on {(scope == "world" ? "World" : scope == "dc" ? "DC" : "Region")}",
        x => RivalCell(x, scope), x => x.In(scope)?.Price ?? double.MaxValue, x => x.In(scope) == null)) : Enumerable.Empty<FleetColumn<ComparedListing>>())
    .Concat(new List<FleetColumn<ComparedListing>>
    {
        new("total", "Total", x => GilCell(x.Listing.Value), x => x.Listing.Value),
        // Amber once it's unsold for a while.
        new("age", "Up for", x => { if (x.Listing.Stale) Colored(Amber, Age(x.Listing.AgeDays)); else ImGui.TextUnformatted(Age(x.Listing.AgeDays)); }, x => x.Listing.AgeDays),
        new("retainer", "Retainer", x => ImGui.TextUnformatted(x.Listing.RetainerName ?? "—"), x => x.Listing.RetainerName ?? ""),
        new("character", "Character", x => CharacterCell(x.Listing.OwnerName ?? "—", x.Listing.World ?? ""), x => x.Listing.OwnerName ?? ""),
        new("account", "Account", x => ImGui.TextUnformatted(x.Listing.Account is { Length: > 0 } a ? a : "—"), x => x.Listing.Account ?? "", Hidden: true),
    }).ToList();

    private FleetTable<MarketAccountRow> MarketAccountsTable => marketAccountsTable ??= new FleetTable<MarketAccountRow>("marketAccounts", plugin.Configuration, new List<FleetColumn<MarketAccountRow>>
    {
        new("account", "Account", a => ImGui.TextUnformatted(a.Account), a => a.Account, Locked: true),
        new("characters", "Characters", a => RightNumber(a.Characters), a => a.Characters),
        new("characterGil", "Character gil", a => GilCell(a.CharacterGil), a => a.CharacterGil),
        new("retainers", "Retainers", a => RightNumber(a.Retainers), a => a.Retainers),
        new("retainerGil", "Retainer gil", a => GilCell(a.RetainerGil), a => a.RetainerGil),
        new("total", "Total", a => GilCell(a.Total), a => a.Total),
    });

    /// <summary>The listings compared with live prices (rivals none without them).</summary>
    private static List<ComparedListing> Compared(MarketRead m) => m.Live is { Error: null } live
        ? MarketMath.Compare(m.Listings, live.Markets, live.Exclude)
        : m.Listings.Select(l => new ComparedListing(l, null, null, null)).ToList();

    private void DrawMarket()
    {
        reader.ReadIfDue();
        RefreshButton();
        FilterBox(ref marketFilter, "market");
        ReadLine(reader.Market, FleetReader.MarketEvery);
        var m = reader.Market.Data;
        if (m == null) return;
        var live = m.Live is { Error: null };

        var characterGil = m.Accounts.Sum(a => a.CharacterGil);
        var retainerGil = m.Accounts.Sum(a => a.RetainerGil);
        ImGui.Spacing();
        GilLine("Fleet gil:", characterGil + retainerGil);
        Colored(Dim, $"{Gil(characterGil)} on {m.Accounts.Sum(a => a.Characters)} characters · {Gil(retainerGil)} on {m.Accounts.Sum(a => a.Retainers)} retainers");
        var stale = m.Listings.Where(l => l.Stale).ToList();
        GilLine($"Up for sale: {Number(m.Listings.Count)} listing{(m.Listings.Count == 1 ? "" : "s")} ·", m.Listings.Sum(l => l.Value));
        var staleText = $"{Number(stale.Count)} up for {m.Settings.StaleDays}+ days ({Gil(stale.Sum(l => l.Value))} gil)";
        if (stale.Count > 0) Colored(Amber, staleText); else ImGui.TextUnformatted(staleText);
        ImGui.Separator();

        Heading("Gil per account");
        MarketAccountsTable.DrawControls();
        using (var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("##marketAccounts", new Vector2(0, Math.Min(200, (m.Accounts.Count + 2) * ImGui.GetFrameHeightWithSpacing())), false))
        {
            if (child.Success) MarketAccountsTable.Draw(m.Accounts);
        }

        var compared = Compared(m).Where(x => Matches(marketFilter, ItemName(x.Listing.ItemId, x.Listing.Hq), x.Listing.RetainerName, x.Listing.OwnerName, x.Listing.World, x.Listing.Account)).ToList();
        if (m.Live?.Error is { } error) Colored(Amber, $"Live prices: {error}");
        else if (m.Live?.Stale is { } old) Colored(Amber, $"Some live prices are from {old.Since.ToLocalTime():g}: {old.Problem} They update at the next read.");
        if (!m.Settings.LivePrices) Colored(Dim, "Live Market Prices is off (the app's Settings → General): turn it on to see the cheapest rivals and undercut listings.");
        if (live)
        {
            Heading("Undercut listings");
            Colored(Dim, "Your listings that someone else sells cheaper on the same world (same item and quality), from Universalis, with the cheapest on the data center and region. Biggest gap first.");
            var undercut = compared.Where(x => x.Undercut).OrderByDescending(x => (x.UndercutBy ?? 0) * x.Listing.Quantity).ToList();
            if (undercut.Count == 0) ImGui.TextUnformatted("None of your listings is undercut on its world.");
            else
            {
                undercutTable ??= new FleetTable<ComparedListing>("marketUndercut", plugin.Configuration, ListingColumns(rivals: true, cheaperBy: true));
                undercutTable.DrawControls();
                using var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("##marketUndercut", new Vector2(0, Math.Min(260, (undercut.Count + 2) * ImGui.GetFrameHeightWithSpacing())), false);
                if (child.Success) undercutTable.Draw(undercut);
            }
        }

        Heading("Listings unsold for a while");
        Colored(Dim, $"Up for {m.Settings.StaleDays} days or more (set in the app's Gil & Market tab), since the plugin first saw each listing (a repriced listing keeps its time). Oldest first.");
        var unsold = compared.Where(x => x.Listing.Stale).OrderByDescending(x => x.Listing.AgeDays).ToList();
        if (unsold.Count == 0) { ImGui.TextUnformatted($"Nothing has been up for {m.Settings.StaleDays} days or more."); return; }
        // With live prices, the cheapest rivals too (a table of its own, so each keeps its own columns).
        var table = live
            ? unsoldLiveTable ??= new FleetTable<ComparedListing>("marketUnsoldLive", plugin.Configuration, ListingColumns(rivals: true, cheaperBy: false))
            : unsoldTable ??= new FleetTable<ComparedListing>("marketUnsold", plugin.Configuration, ListingColumns(rivals: false, cheaperBy: false));
        table.DrawControls();
        table.Draw(unsold);
    }

    // The venture rewards at market prices per day, by scope, for the Income page and the Summary (null without live prices).
    private Dictionary<long, Dictionary<string, double>>? VentureMarketPerDay(VenturesRead ventures)
    {
        if (reader.Market.Data?.Live is not { Error: null } live) return null;
        var result = new Dictionary<long, Dictionary<string, double>>();
        foreach (var g in ventures.Rows.GroupBy(v => v.Cid))
        {
            var world = g.First().World;
            var market = live.Markets.TryGetValue(world, out var wm) ? wm : null;
            if (market == null) continue;
            var vendor = g.Select(v => v.ItemId).Distinct().ToDictionary(id => id, id => ListingVendor(id));
            result[g.Key] = Universalis.Scopes.ToDictionary(s => s, s =>
                MarketMath.VentureGil(g.Select(v => (v.ItemId, v.Quantity, v.HqQuantity)), market, s, vendor, live.Exclude) / Math.Max(1, ventures.WindowDays));
        }
        return result;
    }
}
