using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>One listing on Universalis: price each, how many, quality, and whose (the world only when a data center or region was asked for).</summary>
    public sealed record MarketListing(double PricePerUnit, int Quantity, bool Hq, string? RetainerName, string? WorldName);

    /// <summary>A world's data center and region (as Universalis names them; region as NA, EU, JP, OCE...), null when unknown.</summary>
    public sealed record MarketPlaceNames(string World, string? Dc, string? Region);

    /// <summary>One recent sale on Universalis: price each, how many, quality, and when (Unix seconds; null: not given).</summary>
    public sealed record MarketSale(double PricePerUnit, int Quantity, bool Hq, long? Timestamp);

    /// <summary>For one world: its places, and listings by scope ("world", "dc", "region") and item; Stale when some are
    /// from an earlier check because Universalis didn't answer this time; Sales: the last week's sales, the same way.</summary>
    public sealed record WorldMarket(MarketPlaceNames Names, Dictionary<string, Dictionary<int, List<MarketListing>>> Listings, (string Problem, DateTime Since)? Stale,
        Dictionary<string, Dictionary<int, List<MarketSale>>>? Sales = null);

    /// <summary>A market price: price each and on which world (null when Universalis didn't say, or from sales); Adjusted
    /// when a lone low listing was skipped or recent sales were used, with a Note saying so in plain words.</summary>
    public sealed record MarketPrice(double Price, string? World, bool Adjusted = false, string? Note = null);

    /// <summary>
    /// Live market prices from Universalis (universalis.app, the crowd-sourced market board site), asked for by the plugin
    /// itself, the same way as the app (characters/universalis.js), and only when Live Market Prices is on in the app's
    /// Settings. For each item: the listings and the last week's sales on a character's home world, its data center and
    /// its region; its market price (Price) is the cheapest listing, a lone low one skipped, checked against those sales. Only item
    /// numbers and world, data center and region names are sent. Answers are kept for 30 minutes. The app's tests write
    /// canned listings and the app's answers (tests/market-fixture.js) that the plugin's tests check this against.
    /// </summary>
    public sealed class Universalis
    {
        public static readonly string[] Scopes = { "world", "dc", "region" };
        private const int Batch = 50;
        // Enough that the Shopping List's "buying all of them" covers what a fleet needs (as the app).
        private const int ListingsPerItem = 100;
        /// <summary>Requests at once (Universalis allows several).</summary>
        private const int Parallel = 4;
        private static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);

        private static readonly Dictionary<string, string> RegionLabels = new()
        {
            ["North-America"] = "NA", ["Europe"] = "EU", ["Japan"] = "JP", ["Oceania"] = "OCE", ["中国"] = "CN", ["한국"] = "KR",
        };

        // Used when Universalis's own list of data centers can't be read.
        private static readonly (string Name, string Region, string Worlds)[] KnownDataCenters =
        {
            ("Aether", "North-America", "Adamantoise Cactuar Faerie Gilgamesh Jenova Midgardsormr Sargatanas Siren"),
            ("Primal", "North-America", "Behemoth Excalibur Exodus Famfrit Hyperion Lamia Leviathan Ultros"),
            ("Crystal", "North-America", "Balmung Brynhildr Coeurl Diabolos Goblin Malboro Mateus Zalera"),
            ("Dynamis", "North-America", "Cuchulainn Golem Halicarnassus Kraken Maduin Marilith Rafflesia Seraph"),
            ("Chaos", "Europe", "Cerberus Louisoix Moogle Omega Phantom Ragnarok Sagittarius Spriggan"),
            ("Light", "Europe", "Alpha Lich Odin Phoenix Raiden Shiva Twintania Zodiark"),
            ("Shadow", "Europe", "Innocence Pixie Titania Tycoon"),
            ("Materia", "Oceania", "Bismarck Ravana Sephirot Sophia Zurvan"),
            ("Elemental", "Japan", "Aegis Atomos Carbuncle Garuda Gungnir Kujata Tonberry Typhon"),
            ("Gaia", "Japan", "Alexander Bahamut Durandal Fenrir Ifrit Ridill Tiamat Ultima"),
            ("Mana", "Japan", "Anima Asura Chocobo Hades Ixion Masamune Pandaemonium Titan"),
            ("Meteor", "Japan", "Belias Mandragora Ramuh Shinryu Unicorn Valefor Yojimbo Zeromus"),
        };

        private readonly HttpClient http;
        private readonly string baseUrl;
        private readonly Func<DateTime> now;
        private readonly Dictionary<string, (DateTime At, List<MarketListing> Listings, List<MarketSale> Sales)> cache = new();
        // Recent sales asked for with the listings: up to this many, from the last week (the check on a low listing).
        private const int SalesPerItem = 100;
        private const int SalesWithinSeconds = 7 * 24 * 60 * 60;
        /// <summary>A listing under this share of the typical price is an outlier: of the five cheapest listings' middle
        /// price, then of the last week's middle sale price (as the app's OUTLIER_SHARE).</summary>
        public const double OutlierShare = 0.5;
        private Dictionary<string, (string Name, string Dc, string Region)>? places;

        public Universalis(HttpClient? http = null, string baseUrl = "https://universalis.app", Func<DateTime>? now = null)
        {
            this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            this.baseUrl = baseUrl;
            this.now = now ?? (() => DateTime.UtcNow);
        }

        /// <summary>"name|world" in lowercase: a retainer, to tell the player's own listings apart (retainer names are only
        /// unique on one world).</summary>
        public static string RetainerKey(string name, string? world) => $"{name.ToLowerInvariant()}|{(world ?? "").ToLowerInvariant()}";

        /// <summary>The cheapest listing (of that quality when hq is set), leaving out the player's own retainers;
        /// world: the world of listings that don't name one (when one world was asked for). Null when there's none.</summary>
        public static MarketPrice? Cheapest(IEnumerable<MarketListing>? listings, bool? hq = null, ISet<string>? exclude = null, string? world = null)
        {
            MarketPrice? best = null;
            foreach (var l in listings ?? Enumerable.Empty<MarketListing>())
            {
                if (!(l.PricePerUnit > 0)) continue;
                if (hq != null && l.Hq != hq.Value) continue;
                if (exclude != null && !string.IsNullOrEmpty(l.RetainerName) && exclude.Contains(RetainerKey(l.RetainerName, l.WorldName ?? world))) continue;
                if (best == null || l.PricePerUnit < best.Price) best = new MarketPrice(l.PricePerUnit, string.IsNullOrEmpty(l.WorldName) ? null : l.WorldName);
            }
            return best;
        }

        private static double Middle(List<double> values)
        {
            var v = values.OrderBy(x => x).ToList();
            var m = v.Count / 2;
            return v.Count % 2 == 1 ? v[m] : (v[m - 1] + v[m]) / 2;
        }

        // JavaScript's Math.round and toLocaleString('en-US'): "1,450".
        private static string Gil(double n) => Math.Floor(n + 0.5).ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>
        /// The market price of an item, the same on every page and as the app's marketPrice (characters/universalis.js):
        /// 1. the cheapest listing someone else sells (not the player's own retainers; hq: that quality only);
        /// 2. with three or more listings, any under half the middle price of the five cheapest is skipped;
        /// 3. if that's still under half the middle price of the last week's sales (three or more), the sales' middle price
        ///    is used instead. No listings: the sales' middle price. Neither: null.
        /// </summary>
        public static MarketPrice? Price(IEnumerable<MarketListing>? listings, IEnumerable<MarketSale>? sales, bool? hq, ISet<string>? exclude, string? world, DateTime nowUtc)
        {
            var selling = (listings ?? Enumerable.Empty<MarketListing>())
                .Where(l => l.PricePerUnit > 0 && (hq == null || l.Hq == hq.Value))
                .Where(l => !(exclude != null && !string.IsNullOrEmpty(l.RetainerName) && exclude.Contains(RetainerKey(l.RetainerName, l.WorldName ?? world))))
                .Select(l => (Price: l.PricePerUnit, World: string.IsNullOrEmpty(l.WorldName) ? null : l.WorldName))
                .OrderBy(l => l.Price).ToList();
            var weekAgo = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() - SalesWithinSeconds * 1000L;
            var sold = (sales ?? Enumerable.Empty<MarketSale>())
                .Where(h => h.PricePerUnit > 0 && (h.Timestamp is not long t || t == 0 || t * 1000 >= weekAgo) && (hq == null || h.Hq == hq.Value))
                .Select(h => h.PricePerUnit).ToList();
            double? salesMiddle = sold.Count >= 3 ? Middle(sold) : null;
            if (selling.Count == 0)
            {
                if (salesMiddle is not double only) return null;
                return new MarketPrice(only, null, true, $"No listings: using the middle price of the last week's {sold.Count} sales ({Gil(only)} gil).");
            }
            var pick = selling[0];
            var skipped = new List<(double Price, string? World)>();
            double typical = 0;
            if (selling.Count >= 3)
            {
                typical = Middle(selling.Take(5).Select(l => l.Price).ToList());
                var at = selling.FindIndex(l => l.Price >= typical * OutlierShare);
                if (at > 0) { skipped = selling.Take(at).ToList(); pick = selling[at]; }
            }
            var notes = new List<string>();
            if (skipped.Count > 0)
                notes.Add($"Ignored {skipped.Count} listing{(skipped.Count == 1 ? "" : "s")} at {string.Join(", ", skipped.Select(l => $"{Gil(l.Price)} gil"))} (well below the others, around {Gil(typical)}).");
            var price = pick.Price;
            var usedWorld = pick.World;
            if (salesMiddle is double sm && price < sm * OutlierShare)
            {
                notes.Add($"The cheapest listing ({Gil(price)} gil) is well below the last week's {sold.Count} sales, around {Gil(sm)}.");
                price = sm;
                usedWorld = null;
            }
            return notes.Count == 0 ? new MarketPrice(price, usedWorld) : new MarketPrice(price, usedWorld, true, $"{string.Join(" ", notes)} Using {Gil(price)} gil.");
        }

        /// <summary>One item's market price in one scope of a world's market (its listings and recent sales).</summary>
        public static MarketPrice? PriceIn(WorldMarket market, string scope, int itemId, bool? hq, ISet<string>? exclude, string? world, DateTime nowUtc) =>
            Price(market.Listings[scope].TryGetValue(itemId, out var ls) ? ls : null,
                market.Sales != null && market.Sales.TryGetValue(scope, out var byItem) && byItem.TryGetValue(itemId, out var ss) ? ss : null, hq, exclude, world, nowUtc);

        /// <summary>Gil for each item at its market price in one scope (Price), else at vendor (items neither listed nor sold,
        /// such as salvage). hq: for HQ items, the HQ market price, else that of either quality. notes: gets item -> note
        /// for each price the outlier checks changed.</summary>
        public static Dictionary<int, double> ScopePrices(WorldMarket? market, string scope, IReadOnlyDictionary<int, double> vendor, ISet<string>? exclude, bool hq = false,
            Dictionary<int, string>? notes = null)
        {
            var prices = new Dictionary<int, double>(vendor);
            if (market == null) return prices;
            foreach (var (id, price) in CheapestPrices(market, scope, exclude, hq, notes)) prices[id] = price;
            return prices;
        }

        /// <summary>ScopePrices without the vendor fallback: only the items someone lists or sold in that scope.</summary>
        public static Dictionary<int, double> CheapestPrices(WorldMarket market, string scope, ISet<string>? exclude, bool hq = false, Dictionary<int, string>? notes = null)
        {
            var prices = new Dictionary<int, double>();
            var world = scope == "world" ? market.Names.World : null;
            var now = DateTime.UtcNow;
            foreach (var id in market.Listings[scope].Keys)
            {
                var best = (hq ? PriceIn(market, scope, id, true, exclude, world, now) : null) ?? PriceIn(market, scope, id, null, exclude, world, now);
                if (best == null) continue;
                prices[id] = best.Price;
                if (notes != null && best.Adjusted && best.Note != null) notes[id] = best.Note;
            }
            return prices;
        }

        private async Task<JsonDocument?> GetJson(string path, bool missingOk = false)
        {
            HttpResponseMessage res;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
                request.Headers.TryAddWithoutValidation("User-Agent", "XIVFleetCompanion");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                res = await http.SendAsync(request);
            }
            catch (TaskCanceledException) when (http.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                // The time allowed (20 seconds, the app's too) ran out, answer included.
                throw new InvalidOperationException($"Universalis could not be reached (no answer in {http.Timeout.TotalSeconds:0} seconds).");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Universalis could not be reached ({ex.Message}).");
            }
            using (res)
            {
                if (res.StatusCode == HttpStatusCode.NotFound && missingOk) return null;
                if ((int)res.StatusCode == 429) throw new InvalidOperationException("Universalis is busy (too many requests). Try again in a minute.");
                if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Universalis answered {(int)res.StatusCode}.");
                return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            }
        }

        // World name (lowercase) -> its name, data center and region: Universalis's list, or the built-in one.
        // The built-in list is used for an answer while Universalis's can't be read, but not kept: it's asked again after a
        // while (a network blip at the start shouldn't leave newer data centers out for the whole session).
        private DateTime placesAskAgainAt = DateTime.MinValue;

        private async Task<Dictionary<string, (string Name, string Dc, string Region)>> Places()
        {
            if (places != null) return places;
            if (DateTime.UtcNow < placesAskAgainAt) return BuiltInPlaces;
            try
            {
                using var dcs = await GetJson("/api/v2/data-centers");
                using var worlds = await GetJson("/api/v2/worlds");
                var names = worlds!.RootElement.EnumerateArray().ToDictionary(w => w.GetProperty("id").GetInt32(), w => w.GetProperty("name").GetString() ?? "");
                var list = dcs!.RootElement.EnumerateArray().Select(dc => (Name: dc.GetProperty("name").GetString() ?? "", Region: dc.GetProperty("region").GetString() ?? "",
                    Worlds: dc.TryGetProperty("worlds", out var ws) ? ws.EnumerateArray().Select(id => names.TryGetValue(id.GetInt32(), out var n) ? n : null).OfType<string>().ToList() : new List<string>())).ToList();
                if (list.Any(dc => dc.Worlds.Count > 0)) places = PlacesFrom(list);
            }
            catch { /* the built-in list below */ }
            if (places != null) return places;
            placesAskAgainAt = DateTime.UtcNow.AddMinutes(30);
            return BuiltInPlaces;
        }

        private static readonly Dictionary<string, (string Name, string Dc, string Region)> BuiltInPlaces =
            PlacesFrom(KnownDataCenters.Select(d => (d.Name, d.Region, d.Worlds.Split(' ').ToList())));

        private static Dictionary<string, (string, string, string)> PlacesFrom(IEnumerable<(string Name, string Region, List<string> Worlds)> dataCenters)
        {
            var map = new Dictionary<string, (string, string, string)>();
            foreach (var dc in dataCenters)
                foreach (var world in dc.Worlds) map[world.ToLowerInvariant()] = (world, dc.Name, dc.Region);
            return map;
        }

        // One answer: item -> listings and recent sales (several items answer { items: { id: {...} } }, one answers the item itself).
        private static Dictionary<int, (List<MarketListing> Listings, List<MarketSale> Sales)> ParseItems(JsonDocument? doc)
        {
            var items = new Dictionary<int, (List<MarketListing>, List<MarketSale>)>();
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object) return items;
            var root = doc.RootElement;
            var list = root.TryGetProperty("items", out var many) ? many.EnumerateObject().Select(p => p.Value).ToList()
                : root.TryGetProperty("itemID", out _) ? new List<JsonElement> { root } : new List<JsonElement>();
            foreach (var item in list)
            {
                if (!item.TryGetProperty("itemID", out var id)) continue;
                var listings = new List<MarketListing>();
                if (item.TryGetProperty("listings", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    foreach (var l in ls.EnumerateArray())
                        listings.Add(new MarketListing(
                            l.TryGetProperty("pricePerUnit", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0,
                            l.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : 1,
                            l.TryGetProperty("hq", out var hq) && hq.ValueKind == JsonValueKind.True,
                            l.TryGetProperty("retainerName", out var rn) && rn.ValueKind == JsonValueKind.String ? rn.GetString() : null,
                            l.TryGetProperty("worldName", out var wn) && wn.ValueKind == JsonValueKind.String ? wn.GetString() : null));
                var sales = new List<MarketSale>();
                if (item.TryGetProperty("recentHistory", out var hs) && hs.ValueKind == JsonValueKind.Array)
                    foreach (var h in hs.EnumerateArray())
                        sales.Add(new MarketSale(
                            h.TryGetProperty("pricePerUnit", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0,
                            h.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : 1,
                            h.TryGetProperty("hq", out var hq) && hq.ValueKind == JsonValueKind.True,
                            h.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : null));
                items[(int)id.GetDouble()] = (listings, sales);
            }
            return items;
        }

        // Item -> listings for one world, data center or region, from what's kept or Universalis, a few batches at a time
        // (gate). When Universalis doesn't answer, items asked for before keep their last listings (the problem and since
        // when come back with them); a new one fails.
        private async Task<(Dictionary<int, List<MarketListing>> Listings, (string Problem, DateTime Since)? Stale, Dictionary<int, List<MarketSale>> Sales)> ListingsFor(string scope, List<int> ids, SemaphoreSlim gate)
        {
            var result = new Dictionary<int, List<MarketListing>>();
            var salesOut = new Dictionary<int, List<MarketSale>>();
            (string Problem, DateTime Since)? stale = null;
            var missing = new List<int>();
            lock (cache)
                foreach (var id in ids)
                {
                    if (cache.TryGetValue($"{scope}|{id}", out var hit) && now() - hit.At < Keep) { result[id] = hit.Listings; salesOut[id] = hit.Sales; }
                    else missing.Add(id);
                }
            var batches = Enumerable.Range(0, (missing.Count + Batch - 1) / Batch).Select(i => missing.Skip(i * Batch).Take(Batch).ToList()).ToList();
            await Task.WhenAll(batches.Select(async batch =>
            {
                Dictionary<int, (List<MarketListing> Listings, List<MarketSale> Sales)> items;
                await gate.WaitAsync();
                try
                {
                    using var doc = await GetJson($"/api/v2/{Uri.EscapeDataString(scope)}/{string.Join(",", batch)}?listings={ListingsPerItem}&entries={SalesPerItem}&entriesWithin={SalesWithinSeconds}", missingOk: true);
                    items = ParseItems(doc);
                }
                catch (Exception ex)
                {
                    lock (cache)
                    {
                        var old = batch.Select(id => cache.TryGetValue($"{scope}|{id}", out var o) ? o : ((DateTime At, List<MarketListing> Listings, List<MarketSale> Sales)?)null).ToList();
                        if (old.Any(o => o == null)) throw;
                        for (var k = 0; k < batch.Count; k++) { result[batch[k]] = old[k]!.Value.Listings; salesOut[batch[k]] = old[k]!.Value.Sales; }
                        var since = old.Min(o => o!.Value.At);
                        stale = (ex.Message, stale == null || since < stale.Value.Since ? since : stale.Value.Since);
                    }
                    return;
                }
                finally
                {
                    gate.Release();
                }
                lock (cache)
                    foreach (var id in batch)
                    {
                        var (listings, sales) = items.TryGetValue(id, out var found) ? found : (new List<MarketListing>(), new List<MarketSale>());
                        cache[$"{scope}|{id}"] = (now(), listings, sales);
                        result[id] = listings;
                        salesOut[id] = sales;
                    }
            }));
            return (result, stale, salesOut);
        }

        /// <summary>The worlds Universalis knows (lowercase name -> its name, data center and region), else the built-in list.</summary>
        public Task<Dictionary<string, (string Name, string Dc, string Region)>> PlacesAsync() => Places();

        /// <summary>The listings of these items in one world, data center or region (scope: its name), from what's kept or
        /// Universalis; the problem and since when, when some are from an earlier check because it didn't answer.</summary>
        public async Task<(Dictionary<int, List<MarketListing>> Listings, (string Problem, DateTime Since)? Stale)> ListingsInAsync(string scope, List<int> ids)
        {
            var wanted = ids.Where(id => id > 0).Distinct().ToList();
            if (string.IsNullOrEmpty(scope) || wanted.Count == 0) return (new Dictionary<int, List<MarketListing>>(), null);
            using var gate = new SemaphoreSlim(Parallel);
            var (listings, stale, _) = await ListingsFor(scope, wanted, gate);
            return (listings, stale);
        }

        /// <summary>For each world: its data center and region, and the listings of the items wanted there (wanted: each
        /// world with an item listed or earned there) on the world, its data center and its region. Each world, data center
        /// and region is asked once for every item wanted there, a few requests at a time (Universalis allows several at
        /// once), so a slow answer doesn't hold up the rest.</summary>
        public async Task<Dictionary<string, WorldMarket>> MarketFor(IEnumerable<(string? World, int ItemId)> wanted)
        {
            var where = await Places();
            var byWorld = new Dictionary<string, List<int>>();
            foreach (var (world, id) in wanted)
            {
                if (string.IsNullOrEmpty(world)) continue;
                if (!byWorld.TryGetValue(world, out var list)) byWorld[world] = list = new List<int>();
                if (id > 0 && !list.Contains(id)) list.Add(id);
            }
            var placeOf = byWorld.Keys.ToDictionary(w => w, w => where.TryGetValue(w.ToLowerInvariant(), out var p) ? p : ((string, string Dc, string Region)?)null);
            Dictionary<string, string?> ScopeNames(string world) => new() { ["world"] = world, ["dc"] = placeOf[world]?.Dc, ["region"] = placeOf[world]?.Region };
            // Every place asked once, for every item wanted on a world in it.
            var askFor = new Dictionary<string, List<int>>();
            foreach (var (world, ids) in byWorld)
                foreach (var name in ScopeNames(world).Values.OfType<string>())
                {
                    if (!askFor.TryGetValue(name, out var list)) askFor[name] = list = new List<int>();
                    foreach (var id in ids) if (!list.Contains(id)) list.Add(id);
                }
            using var gate = new SemaphoreSlim(Parallel);
            var asked = await Task.WhenAll(askFor.Where(kv => kv.Value.Count > 0).Select(async kv => (Name: kv.Key, Answer: await ListingsFor(kv.Key, kv.Value, gate))));
            var answers = asked.ToDictionary(a => a.Name, a => a.Answer);
            var result = new Dictionary<string, WorldMarket>();
            foreach (var (world, ids) in byWorld)
            {
                var byScope = new Dictionary<string, Dictionary<int, List<MarketListing>>>();
                var salesByScope = new Dictionary<string, Dictionary<int, List<MarketSale>>>();
                (string Problem, DateTime Since)? stale = null;
                foreach (var (scope, name) in ScopeNames(world))
                {
                    var answer = name != null && answers.TryGetValue(name, out var a) ? a
                        : (Listings: new Dictionary<int, List<MarketListing>>(), Stale: ((string Problem, DateTime Since)?)null, Sales: new Dictionary<int, List<MarketSale>>());
                    byScope[scope] = ids.Where(answer.Listings.ContainsKey).ToDictionary(id => id, id => answer.Listings[id]);
                    salesByScope[scope] = ids.Where(answer.Sales.ContainsKey).ToDictionary(id => id, id => answer.Sales[id]);
                    if (answer.Stale is { } s) stale = (s.Problem, stale == null || s.Since < stale.Value.Since ? s.Since : stale.Value.Since);
                }
                var region = placeOf[world]?.Region;
                result[world] = new WorldMarket(new MarketPlaceNames(world, placeOf[world]?.Dc, region == null ? null : RegionLabels.TryGetValue(region, out var label) ? label : region), byScope, stale, salesByScope);
            }
            return result;
        }
    }
}
