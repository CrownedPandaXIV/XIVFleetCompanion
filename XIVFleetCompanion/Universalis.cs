using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>One listing on Universalis: price each, how many, quality, and whose (the world only when a data center or region was asked for).</summary>
    public sealed record MarketListing(double PricePerUnit, int Quantity, bool Hq, string? RetainerName, string? WorldName);

    /// <summary>A world's data center and region (as Universalis names them; region as NA, EU, JP, OCE...), null when unknown.</summary>
    public sealed record MarketPlaceNames(string World, string? Dc, string? Region);

    /// <summary>For one world: its places, and listings by scope ("world", "dc", "region") and item; Stale when some are
    /// from an earlier check because Universalis didn't answer this time.</summary>
    public sealed record WorldMarket(MarketPlaceNames Names, Dictionary<string, Dictionary<int, List<MarketListing>>> Listings, (string Problem, DateTime Since)? Stale);

    /// <summary>The cheapest someone else sells for: price each and on which world (null when Universalis didn't say).</summary>
    public sealed record MarketPrice(double Price, string? World);

    /// <summary>
    /// Live market prices from Universalis (universalis.app, the crowd-sourced market board site), asked for by the plugin
    /// itself, the same way as the app (characters/universalis.js), and only when Live Market Prices is on in the app's
    /// Settings. For each item: the listings on a character's home world, its data center and its region. Only item
    /// numbers and world, data center and region names are sent. Answers are kept for 30 minutes. The app's tests write
    /// canned listings and the app's answers (tests/market-fixture.js) that the plugin's tests check this against.
    /// </summary>
    public sealed class Universalis
    {
        public static readonly string[] Scopes = { "world", "dc", "region" };
        private const int Batch = 50;
        private const int ListingsPerItem = 20;
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
            ("Materia", "Oceania", "Bismarck Ravana Sephirot Sophia Zurvan"),
            ("Elemental", "Japan", "Aegis Atomos Carbuncle Garuda Gungnir Kujata Tonberry Typhon"),
            ("Gaia", "Japan", "Alexander Bahamut Durandal Fenrir Ifrit Ridill Tiamat Ultima"),
            ("Mana", "Japan", "Anima Asura Chocobo Hades Ixion Masamune Pandaemonium Titan"),
            ("Meteor", "Japan", "Belias Mandragora Ramuh Shinryu Unicorn Valefor Yojimbo Zeromus"),
        };

        private readonly HttpClient http;
        private readonly string baseUrl;
        private readonly Func<DateTime> now;
        private readonly Dictionary<string, (DateTime At, List<MarketListing> Listings)> cache = new();
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

        /// <summary>Gil for each item at the cheapest listing in one scope, else at vendor (items not on the market board,
        /// such as salvage). hq: for HQ items, the cheapest HQ listing, else the cheapest of either quality.</summary>
        public static Dictionary<int, double> ScopePrices(WorldMarket? market, string scope, IReadOnlyDictionary<int, double> vendor, ISet<string>? exclude, bool hq = false)
        {
            var prices = new Dictionary<int, double>(vendor);
            if (market == null) return prices;
            var world = scope == "world" ? market.Names.World : null;
            foreach (var (id, listings) in market.Listings[scope])
            {
                var best = (hq ? Cheapest(listings, true, exclude, world) : null) ?? Cheapest(listings, null, exclude, world);
                if (best != null) prices[id] = best.Price;
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
        private async Task<Dictionary<string, (string Name, string Dc, string Region)>> Places()
        {
            if (places != null) return places;
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
            return places ??= PlacesFrom(KnownDataCenters.Select(d => (d.Name, d.Region, d.Worlds.Split(' ').ToList())));
        }

        private static Dictionary<string, (string, string, string)> PlacesFrom(IEnumerable<(string Name, string Region, List<string> Worlds)> dataCenters)
        {
            var map = new Dictionary<string, (string, string, string)>();
            foreach (var dc in dataCenters)
                foreach (var world in dc.Worlds) map[world.ToLowerInvariant()] = (world, dc.Name, dc.Region);
            return map;
        }

        // One answer: item -> listings (several items answer { items: { id: {...} } }, one answers the item itself).
        private static Dictionary<int, List<MarketListing>> ParseItems(JsonDocument? doc)
        {
            var items = new Dictionary<int, List<MarketListing>>();
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
                items[(int)id.GetDouble()] = listings;
            }
            return items;
        }

        // Item -> listings for one world, data center or region, from what's kept or Universalis. When Universalis doesn't
        // answer, items asked for before keep their last listings (stale says since when and why); a new one fails.
        private async Task<Dictionary<int, List<MarketListing>>> ListingsFor(string scope, List<int> ids, Action<string, DateTime> stale)
        {
            var result = new Dictionary<int, List<MarketListing>>();
            var missing = new List<int>();
            foreach (var id in ids)
            {
                if (cache.TryGetValue($"{scope}|{id}", out var hit) && now() - hit.At < Keep) result[id] = hit.Listings;
                else missing.Add(id);
            }
            for (var i = 0; i < missing.Count; i += Batch)
            {
                var batch = missing.Skip(i).Take(Batch).ToList();
                Dictionary<int, List<MarketListing>> items;
                try
                {
                    using var doc = await GetJson($"/api/v2/{Uri.EscapeDataString(scope)}/{string.Join(",", batch)}?listings={ListingsPerItem}&entries=0", missingOk: true);
                    items = ParseItems(doc);
                }
                catch (Exception ex)
                {
                    var old = batch.Select(id => cache.TryGetValue($"{scope}|{id}", out var o) ? o : ((DateTime At, List<MarketListing> Listings)?)null).ToList();
                    if (old.Any(o => o == null)) throw;
                    for (var k = 0; k < batch.Count; k++) result[batch[k]] = old[k]!.Value.Listings;
                    stale(ex.Message, old.Min(o => o!.Value.At));
                    continue;
                }
                foreach (var id in batch)
                {
                    var listings = items.TryGetValue(id, out var found) ? found : new List<MarketListing>();
                    cache[$"{scope}|{id}"] = (now(), listings);
                    result[id] = listings;
                }
            }
            return result;
        }

        /// <summary>For each world: its data center and region, and the listings of each item there, on its data center and
        /// in its region.</summary>
        public async Task<Dictionary<string, WorldMarket>> MarketFor(IEnumerable<string?> worlds, IEnumerable<int> ids)
        {
            var where = await Places();
            var itemIds = ids.Where(id => id > 0).Distinct().ToList();
            var result = new Dictionary<string, WorldMarket>();
            foreach (var world in worlds.Where(w => !string.IsNullOrEmpty(w)).Select(w => w!).Distinct())
            {
                var place = where.TryGetValue(world.ToLowerInvariant(), out var p) ? p : ((string, string Dc, string Region)?)null;
                var scopes = new Dictionary<string, string?> { ["world"] = world, ["dc"] = place?.Dc, ["region"] = place?.Region };
                var byScope = new Dictionary<string, Dictionary<int, List<MarketListing>>>();
                (string Problem, DateTime Since)? stale = null;
                foreach (var s in Scopes)
                    byScope[s] = scopes[s] is { } name && itemIds.Count > 0
                        ? await ListingsFor(name, itemIds, (problem, since) => stale = (problem, stale == null || since < stale.Value.Since ? since : stale.Value.Since))
                        : new Dictionary<int, List<MarketListing>>();
                var region = place?.Region;
                result[world] = new WorldMarket(new MarketPlaceNames(world, place?.Dc, region == null ? null : RegionLabels.TryGetValue(region, out var label) ? label : region), byScope, stale);
            }
            return result;
        }
    }
}
