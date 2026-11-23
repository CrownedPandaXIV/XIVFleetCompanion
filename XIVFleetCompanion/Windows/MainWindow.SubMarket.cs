using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Windows;

// Market prices for the Sub Calculator's Route Finder and Compare Builds (Live Market Prices on), as the app prices them:
// the data center of the sub's world (else the world most of your subs are on), for the map's loot, asked of Universalis
// in the background as the Voyage Planner does. Vendor prices until the answer is in, or when it fails.
public partial class MainWindow
{
    private sealed record SubMarketAnswer(string Key, HashSet<int> Ids, WorldMarket? Market, string? Error);
    private readonly Dictionary<string, (SubMarketAnswer? Answer, Task? Work)> subMarkets = new();

    // The world prices start from: the sub's own, else the one most of your subs are on.
    private string? SubHomeWorld(string? world) => world is { Length: > 0 } w ? w : (reader.Subs.Data ?? new List<SubRow>()).Where(x => x.World.Length > 0)
        .GroupBy(x => x.World).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).FirstOrDefault();

    /// <summary>page: whose answer (one each); world: the sub's (null: your usual one); ids: the items wanted. Market: the
    /// answer once in (null meanwhile, when off, or when it failed); Place: the data center; Error: why it failed.</summary>
    private (WorldMarket? Market, string? Place, string? Error) SubMarketFor(string page, string? world, HashSet<int> ids)
    {
        if (reader.Market.Data?.Settings.LivePrices != true || ids.Count == 0) return (null, null, null);
        if (plannerPlaces == null && plannerPlacesWork == null)
            plannerPlacesWork = Task.Run(async () =>
            {
                try { plannerPlaces = (await reader.MarketPlacesAsync()).Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(); }
                catch (Exception ex) { Plugin.Log.Warning(ex, "Sub Calculator: Universalis's worlds couldn't be read"); plannerPlaces = new(); }
            });
        var home = SubHomeWorld(world);
        var dc = plannerPlaces?.FirstOrDefault(p => string.Equals(p.Name, home, StringComparison.OrdinalIgnoreCase)).Dc;
        if (string.IsNullOrEmpty(dc)) return (null, null, null);
        var key = $"dc|{dc}";
        var (answer, work) = subMarkets.TryGetValue(page, out var had) ? had : (null, null);
        if (work is { IsCompleted: true }) work = null;
        if (work == null && !(answer is { } a && a.Key == key && (a.Error != null || ids.IsSubsetOf(a.Ids))))
        {
            var want = (answer is { } was && was.Key == key ? ids.Union(was.Ids) : ids).ToHashSet();
            var place = dc;
            work = Task.Run(async () =>
            {
                SubMarketAnswer got;
                try { got = new SubMarketAnswer(key, want, await reader.MarketInAsync(place, "dc", want), null); }
                catch (Exception ex) { got = new SubMarketAnswer(key, want, null, ex.Message); }
                lock (subMarkets) subMarkets[page] = (got, subMarkets.TryGetValue(page, out var now) ? now.Work : null);
            });
        }
        lock (subMarkets)
        {
            var now = subMarkets.TryGetValue(page, out var n) ? n.Answer : answer;
            subMarkets[page] = (now, work);
            return now is { } r && r.Key == key ? (r.Market, dc, r.Error) : (null, dc, null);
        }
    }

    // Item prices from a market answer (the shared outlier-checked rule), vendor prices for the rest; safe off the game's
    // frame.
    private static Func<uint, double> SubMarketPrices(WorldMarket? market, IEnumerable<int> ids, ISet<string>? exclude)
    {
        if (market == null) return id => ListingVendor((int)id);
        var vendor = ids.Distinct().ToDictionary(id => id, id => ListingVendor(id));
        var prices = Universalis.ScopePrices(market, "dc", vendor, exclude);
        return id => prices.TryGetValue((int)id, out var p) ? p : ListingVendor((int)id);
    }
}
