using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>The Gil &amp; Market settings (the app's): days up for sale that count as unsold for a while, and whether Live
    /// Market Prices is on.</summary>
    public sealed record MarketSettings(int StaleDays, bool LivePrices);

    /// <summary>Gil per account, from fleet_market_accounts_view ("Ungrouped" without an account label).</summary>
    public sealed record MarketAccountRow(string Account, int Characters, int Retainers, long CharacterGil, long RetainerGil, long Total);

    /// <summary>A retainer's listing, from fleet_market_listings_view: price each, value (× quantity), days since the plugin
    /// first saw it and whether that makes it unsold for a while.</summary>
    public sealed record ListingRow(long RetainerId, long OwnerCid, int Slot, int ItemId, int Quantity, bool Hq, long UnitPrice, DateTime? FirstSeen,
        string? RetainerName, string? OwnerName, string? World, string? Account, int? SortIndex, long Value, double AgeDays, bool Stale);

    /// <summary>A listing against the market: the cheapest someone else sells the same item and quality for on its world,
    /// data center and region; undercut when someone sells it cheaper on its own world (where it sells).</summary>
    public sealed record ComparedListing(ListingRow Listing, MarketPrice? World, MarketPrice? Dc, MarketPrice? Region)
    {
        public bool Undercut => World != null && World.Price < Listing.UnitPrice;
        public double? UndercutBy => World == null ? null : Listing.UnitPrice - World.Price;
        public MarketPrice? In(string scope) => scope switch { "world" => World, "dc" => Dc, _ => Region };
    }

    /// <summary>Live prices for the fleet (when Live Market Prices is on): each world's market, the player's own retainers
    /// (their listings aren't the competition), and why it couldn't be read.</summary>
    public sealed record LiveMarket(Dictionary<string, WorldMarket> Markets, HashSet<string> Exclude, string? Error)
    {
        /// <summary>When some prices are from an earlier check: why, and since when.</summary>
        public (string Problem, DateTime Since)? Stale => Markets.Values.Select(m => m.Stale).Where(s => s != null).Select(s => s!.Value)
            .Aggregate(((string Problem, DateTime Since)?)null, (a, s) => a == null ? s : (a.Value.Problem, s.Since < a.Value.Since ? s.Since : a.Value.Since));
    }

    /// <summary>The Gil &amp; Market page's read.</summary>
    public sealed record MarketRead(MarketSettings Settings, List<MarketAccountRow> Accounts, List<ListingRow> Listings, LiveMarket? Live);

    /// <summary>The market's working out, as the app's (characters/market.js compareListings, subs/loot.js ventureMarketGil).</summary>
    public static class MarketMath
    {
        /// <summary>Each listing against the market on its world, data center and region (no market: no rivals).</summary>
        public static List<ComparedListing> Compare(IEnumerable<ListingRow> listings, IReadOnlyDictionary<string, WorldMarket> market, ISet<string> exclude) =>
            listings.Select(l =>
            {
                var m = l.World != null && market.TryGetValue(l.World, out var found) ? found : null;
                MarketPrice? Rival(string scope) => m == null ? null
                    : Universalis.Cheapest(m.Listings[scope].TryGetValue(l.ItemId, out var ls) ? ls : null, l.Hq, exclude, scope == "world" ? l.World : null);
                return new ComparedListing(l, Rival("world"), Rival("dc"), Rival("region"));
            }).ToList();

        /// <summary>Venture rewards at market prices in one scope: normal ones at the cheapest listing, high quality at the
        /// cheapest HQ listing (else either), anything not on the market board at what a vendor pays.</summary>
        public static double VentureGil(IEnumerable<(int ItemId, long Quantity, long HqQuantity)> items, WorldMarket? market, string scope,
            IReadOnlyDictionary<int, double> vendor, ISet<string> exclude)
        {
            var prices = Universalis.ScopePrices(market, scope, vendor, exclude);
            var hqPrices = Universalis.ScopePrices(market, scope, vendor, exclude, hq: true);
            return items.Sum(i => (i.Quantity - i.HqQuantity) * (prices.TryGetValue(i.ItemId, out var p) ? p : 0)
                                  + i.HqQuantity * (hqPrices.TryGetValue(i.ItemId, out var h) ? h : 0));
        }
    }
}
