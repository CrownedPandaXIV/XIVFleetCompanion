using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>
    /// The market's working out, kept until the next read: the window draws 60 times a second, and pricing every listing
    /// and venture reward each time took most of a second a frame. Each answer is worked out again only when a new read
    /// (the database's, or Universalis's prices) is in, told apart by the read itself, and in the background (run), so the
    /// game doesn't stop for it either: until it's done, the answers from the read before are shown.
    /// </summary>
    public sealed class MarketCache
    {
        private readonly Func<Action, Task> run;
        private MarketRead? comparedFor;
        private volatile List<ComparedListing> compared = new();
        private volatile int undercut;
        private readonly Dictionary<List<VentureIncomeRow>, (LiveMarket? Live, Dictionary<long, Dictionary<string, double>>? PerDay)> ventureMarket = new();
        private readonly Dictionary<List<VentureIncomeRow>, List<FleetReader.VentureSummary>> ventureSummaries = new();
        private int working;

        /// <param name="run">Where the working out runs (the background by default; the tests run it at once).</param>
        public MarketCache(Func<Action, Task>? run = null) => this.run = run ?? (a => Task.Run(a));

        /// <summary>True while something is being worked out in the background.</summary>
        public bool Working => Volatile.Read(ref working) > 0;

        private void Background(Action work)
        {
            Interlocked.Increment(ref working);
            run(() =>
            {
                try { work(); }
                finally { Interlocked.Decrement(ref working); }
            });
        }

        /// <summary>The listings compared with live prices (rivals none without them).</summary>
        public List<ComparedListing> Compared(MarketRead m)
        {
            Refresh(m);
            return compared;
        }

        /// <summary>How many listings someone else sells cheaper on the same world (0 without live prices).</summary>
        public int Undercut(MarketRead m)
        {
            Refresh(m);
            return undercut;
        }

        private void Refresh(MarketRead m)
        {
            if (ReferenceEquals(comparedFor, m)) return;
            comparedFor = m;
            if (m.Live is not { Error: null } live)
            {
                compared = m.Listings.Select(l => new ComparedListing(l, null, null, null)).ToList();
                undercut = 0;
                return;
            }
            Background(() =>
            {
                var result = MarketMath.Compare(m.Listings, live.Markets, live.Exclude);
                var count = result.Count(x => x.Undercut);
                lock (this)
                {
                    if (!ReferenceEquals(comparedFor, m)) return;
                    compared = result;
                    undercut = count;
                }
            });
        }

        /// <summary>The venture rewards at market prices per day for each character, by scope (null without live prices).
        /// vendorOf: what a vendor pays for one (asked here, not in the background, as it reads the game's files).</summary>
        public Dictionary<long, Dictionary<string, double>>? VenturesPerDay(MarketRead? m, List<VentureIncomeRow> rows, int windowDays, Func<int, double> vendorOf)
        {
            var live = m?.Live is { Error: null } l ? l : null;
            lock (ventureMarket)
            {
                if (ventureMarket.TryGetValue(rows, out var kept) && ReferenceEquals(kept.Live, live)) return kept.PerDay;
                var before = kept.PerDay;
                if (ventureMarket.Count > 4) ventureMarket.Clear();
                // Until it's worked out: the answers from before (none without live prices).
                ventureMarket[rows] = (live, live == null ? null : before);
                if (live == null) return null;
                var vendor = rows.Select(v => v.ItemId).Distinct().ToDictionary(id => id, vendorOf);
                Background(() =>
                {
                    var result = VentureMarket(rows, windowDays, live, vendor);
                    lock (ventureMarket)
                        if (ventureMarket.TryGetValue(rows, out var now) && ReferenceEquals(now.Live, live)) ventureMarket[rows] = (live, result);
                });
                return before;
            }
        }

        // As MarketMath.VentureGil for each character, the cheapest prices worked out once per world, scope and quality
        // (the same for every character there) rather than once per character.
        private static Dictionary<long, Dictionary<string, double>> VentureMarket(List<VentureIncomeRow> rows, int windowDays, LiveMarket live, Dictionary<int, double> vendor)
        {
            var cheapest = new Dictionary<(string World, string Scope, bool Hq), Dictionary<int, double>>();
            double Price(string world, WorldMarket market, string scope, bool hq, int id)
            {
                if (!cheapest.TryGetValue((world, scope, hq), out var prices))
                    cheapest[(world, scope, hq)] = prices = Universalis.CheapestPrices(market, scope, live.Exclude, hq);
                return prices.TryGetValue(id, out var p) ? p : vendor.TryGetValue(id, out var v) ? v : 0;
            }
            var result = new Dictionary<long, Dictionary<string, double>>();
            foreach (var g in rows.GroupBy(v => v.Cid))
            {
                var world = g.First().World;
                if (!live.Markets.TryGetValue(world, out var market)) continue;
                result[g.Key] = Universalis.Scopes.ToDictionary(s => s, s =>
                    g.Sum(i => (i.Quantity - i.HqQuantity) * Price(world, market, s, false, i.ItemId) + i.HqQuantity * Price(world, market, s, true, i.ItemId)) / Math.Max(1, windowDays));
            }
            return result;
        }

        /// <summary>FleetReader.SummarizeVentures, kept for the same rows.</summary>
        public List<FleetReader.VentureSummary> VentureSummaries(List<VentureIncomeRow> rows, int windowDays, Func<int, double> vendorOf)
        {
            if (ventureSummaries.TryGetValue(rows, out var kept)) return kept;
            if (ventureSummaries.Count > 4) ventureSummaries.Clear();
            return ventureSummaries[rows] = FleetReader.SummarizeVentures(rows, windowDays, vendorOf);
        }
    }
}
