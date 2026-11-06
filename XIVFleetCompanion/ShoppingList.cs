using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace XIVFleetCompanion
{
    // 2.x Shopping List stage, as the app's Parts tab → Shopping List (ipc/shopping.js, subs/shopping-list.js,
    // subs/shopping-progress.js, subs/shopping-prices.js): the sub parts to craft, everything to craft on the way, and the
    // raw materials and crystals to gather or buy, less what the fleet holds; with Live Market Prices on, what buying
    // instead costs. The recipes come from the game's own files (Recipe, the FC workshop's CompanyCraft sheets, GilShopItem
    // and Item's vendor price); what's held and the ticks from the database (fleet_shopping_held_view,
    // fleet_shopping_progress_view). The app's tests write made-up fleets, game data and its answers
    // (tests/db/routes-fixture.js) that the plugin's tests check this against.

    /// <summary>How one item is made: how many one craft makes, which crafter ("FC workshop" for a sub part; null when not known), and from what.</summary>
    public sealed record ShopRecipe(int Yield, string? Job, List<(int ItemId, int Amount)> Ingredients);

    /// <summary>A row of the game's Recipe sheet: its id, what it makes and how many, the crafter (CraftType) and the ingredients.</summary>
    public sealed record ShopRecipeRow(int Id, int Result, int Yield, int CraftType, List<(int ItemId, int Amount)> Ingredients);

    /// <summary>The FC workshop's: a sequence (what it builds and its parts), a part (its phases) and a phase (its supply items, how many a set and how many sets).</summary>
    public sealed record ShopSequence(int Id, int Result, List<int> Parts);
    public sealed record ShopProcess(int Id, List<int> Supply, List<int> PerSet, List<int> Sets);

    /// <summary>The game's crafting data the Shopping List needs, in the shape of its sheets.</summary>
    public sealed class ShoppingGame
    {
        private static readonly string[] Crafters = { "Carpenter", "Blacksmith", "Armorer", "Goldsmith", "Leatherworker", "Weaver", "Alchemist", "Culinarian" };

        /// <summary>Item -> its recipe: the one with the lowest id that asks for something (more than one crafter can make some).</summary>
        public Dictionary<int, ShopRecipe> Recipes { get; } = new();
        private readonly List<ShopSequence> sequences;
        private readonly Dictionary<int, List<int>> procsOfPart;
        private readonly Dictionary<int, ShopProcess> processes;
        private readonly Dictionary<int, int> itemOfSupply;
        /// <summary>Item -> what a vendor charges, for what a gil shop sells (its PriceMid; none when 0).</summary>
        public Dictionary<int, double> Vendor { get; }
        /// <summary>Whether the game's list of what gil shops sell was read (always, from the game's own files).</summary>
        public bool VendorsLoaded { get; init; } = true;
        private readonly Func<int, string?> name;

        public ShoppingGame(IEnumerable<ShopRecipeRow> recipes, IEnumerable<ShopSequence> sequences, IEnumerable<(int Id, List<int> Processes)> craftParts,
            IEnumerable<ShopProcess> processes, IEnumerable<(int Id, int Item)> supplyItems, IReadOnlyDictionary<int, double> vendor, Func<int, string?> name)
        {
            foreach (var r in recipes.OrderBy(r => r.Id))
            {
                if (Recipes.ContainsKey(r.Result)) continue;
                var ingredients = r.Ingredients.Where(i => i.ItemId > 0 && i.Amount > 0).ToList();
                if (ingredients.Count == 0) continue;
                Recipes[r.Result] = new ShopRecipe(Math.Max(1, r.Yield), r.CraftType >= 0 && r.CraftType < Crafters.Length ? Crafters[r.CraftType] : null, ingredients);
            }
            this.sequences = sequences.ToList();
            procsOfPart = new Dictionary<int, List<int>>();
            foreach (var (id, procs) in craftParts) procsOfPart[id] = procs.Where(p => p > 0).ToList();
            this.processes = new Dictionary<int, ShopProcess>();
            foreach (var p in processes) this.processes[p.Id] = p;
            itemOfSupply = new Dictionary<int, int>();
            foreach (var (id, item) in supplyItems) itemOfSupply[id] = item;
            Vendor = vendor.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            this.name = name;
        }

        public string NameOf(int id) => name(id) is { Length: > 0 } n ? n : $"Item {id}";

        /// <summary>How each of these sub parts is built at the FC workshop, every phase's supply items added up (the first
        /// sequence for a part counts; a part whose phases ask for nothing has none). Empty when none of them has a sequence.</summary>
        public Dictionary<int, ShopRecipe> Workshop(IEnumerable<int> partIds)
        {
            var wanted = new HashSet<int>(partIds);
            var recipes = new Dictionary<int, ShopRecipe>();
            foreach (var seq in sequences)
            {
                // The first sequence for a part that asks for something counts.
                if (!wanted.Contains(seq.Result) || recipes.ContainsKey(seq.Result)) continue;
                var amounts = new Dictionary<int, int>();
                var order = new List<int>();
                foreach (var partId in seq.Parts.Where(p => p > 0))
                    foreach (var procId in procsOfPart.GetValueOrDefault(partId) ?? new List<int>())
                    {
                        if (!processes.TryGetValue(procId, out var p)) continue;
                        for (var i = 0; i < p.Supply.Count; i++)
                        {
                            var ingredient = itemOfSupply.GetValueOrDefault(p.Supply[i]);
                            var amount = (i < p.PerSet.Count ? p.PerSet[i] : 0) * (i < p.Sets.Count ? p.Sets[i] : 0);
                            if (ingredient <= 0 || amount <= 0) continue;
                            if (!amounts.ContainsKey(ingredient)) order.Add(ingredient);
                            amounts[ingredient] = amounts.GetValueOrDefault(ingredient) + amount;
                        }
                    }
                if (amounts.Count > 0) recipes[seq.Result] = new ShopRecipe(1, "FC workshop", order.Select(id => (id, amounts[id])).ToList());
            }
            return recipes;
        }

        /// <summary>Whether any of these parts has a sequence at all (the app's "game data for crafting not loaded" otherwise).</summary>
        public bool HasWorkshop(IEnumerable<int> partIds) => partIds.Any(id => sequences.Any(s => s.Result == id));
    }

    /// <summary>Somewhere an item is held: who ("Name (World)", or the Free Company), where ("Bags", "Retainer Amy", "FC chest") and how many.</summary>
    public sealed record HeldPlace(string Who, string Where, long Qty);

    /// <summary>A row of fleet_shopping_held_view.</summary>
    public sealed record HeldRow(int ItemId, string Who, string Place, long Qty);

    /// <summary>A market board listing someone else has up: price each, how many, and on which world.</summary>
    public sealed record ShopListing(double Price, int Quantity, string? World);

    /// <summary>An item's cheapest listing in the place chosen, and every listing there, cheapest first.</summary>
    public sealed record ShopMarket(double Price, string? World, List<ShopListing> Listings);

    /// <summary>What buying the ones wanted costs going up the listings, and how many the listings cover.</summary>
    public sealed record ShopBuying(double Gil, int Got, int Wanted);

    /// <summary>Where making one gets an ingredient: how many (per one made), gil each, how ("vendor", "market", "make"), its world, and one level down for one that is made.</summary>
    public sealed record ShopBreakdown(int ItemId, string Name, double Amount, double? Each, string? How, string? World, List<ShopBreakdown>? Parts);

    /// <summary>A choice for "Prices from": value ("world:Maduin", "dc:Dynamis", "region:North-America"), label, kind and the name Universalis takes.</summary>
    public sealed record PriceChoice(string Value, string Label, string Kind, string Scope);

    /// <summary>One line of the list. Which fields mean something depends on the section (as the app's).</summary>
    public sealed class ShopRow
    {
        public int ItemId { get; init; }
        public long Need { get; init; }
        public long Have { get; init; }
        /// <summary>Gather, crystals, buy: how many are still short. Craft first: Make. Parts: none.</summary>
        public long Short { get; init; }
        public long Crafts { get; init; }
        public int Yield { get; init; }
        public string? Job { get; init; }
        public bool NoRecipe { get; init; }
        public string Name { get; set; } = "";
        public List<HeldPlace> Places { get; set; } = new();
        public double? VendorPrice { get; set; }
        public long Made { get; set; }
        public double Cost { get; set; }
        public ShopMarket? Market { get; set; }
        public double? MakeCost { get; set; }
        public List<ShopBreakdown>? MakeBreakdown { get; set; }
        public string? Cheaper { get; set; }
        public string? Cheapest { get; set; }
        public ShopBuying? Buying { get; set; }
    }

    public sealed record ShoppingTotals(double Parts, int PartsUnlisted, int PartsTooFew, double Materials, int MaterialsUnlisted, int MaterialsTooFew);

    /// <summary>Where the prices are from: whether Live Market Prices is on, the choices, the one used, and anything that went wrong.</summary>
    public sealed record ShoppingMarketInfo(bool Enabled, List<PriceChoice>? Choices, string? Scope, string? Label, string? Kind, string? Error,
        (string Problem, DateTime Since)? Stale);

    /// <summary>The list, as the app's get-shopping-list answers it: an error, empty (nothing to craft), or the sections.</summary>
    public sealed class ShoppingResult
    {
        public string? Error { get; init; }
        public bool Empty { get; init; }
        public bool VendorsLoaded { get; init; }
        public bool BuyFromVendors { get; init; }
        public List<ShopRow> Parts { get; init; } = new();
        public List<ShopRow> Craft { get; init; } = new();
        public List<ShopRow> Gather { get; init; } = new();
        public List<ShopRow> Crystals { get; init; } = new();
        public List<ShopRow> Buy { get; init; } = new();
        public double BuyCost { get; init; }
        public ShoppingMarketInfo Market { get; set; } = new(false, null, null, null, null, null, null);
        public ShoppingTotals? Totals { get; set; }
        public bool Priced => Market.Scope != null && Market.Error == null;
    }

    /// <summary>What Live Market Prices gives the list: the places Universalis knows (lowercase world -> name, data center,
    /// region), and the listings of some items in one place (item -> listings; the problem and since when, when some are old).</summary>
    public interface IShoppingMarket
    {
        Dictionary<string, (string Name, string Dc, string Region)> Places();
        (Dictionary<int, List<MarketListing>> Listings, (string Problem, DateTime Since)? Stale) ListingsIn(string scope, List<int> ids);
    }

    public static class ShoppingMath
    {
        private const double Day = 24 * 60 * 60 * 1000.0;
        public static readonly int[] AheadChoices = { 0, 3, 7, 14 };
        private static bool IsCrystal(int id) => id >= 2 && id <= 19;

        private static readonly Dictionary<string, string> RegionLabels = new()
        {
            ["North-America"] = "NA", ["Europe"] = "EU", ["Japan"] = "JP", ["Oceania"] = "OCE", ["中国"] = "CN", ["한국"] = "KR",
        };

        // As JavaScript's localeCompare for the names here.
        private static int Compare(string? a, string? b) => string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.None);

        /// <summary>The list from the parts to craft (subs/shopping-list.js buildShoppingList): parts (item -> how many),
        /// recipe(item), held (item -> how many the fleet holds) and what to buy from a vendor instead.</summary>
        public static (List<ShopRow> Parts, List<ShopRow> Craft, List<ShopRow> Gather, List<ShopRow> Crystals, List<ShopRow> Buy) Build(
            IReadOnlyDictionary<int, int> parts, List<int> partOrder, Func<int, ShopRecipe?> recipeOf, IReadOnlyDictionary<int, long> held, ISet<int> buy)
        {
            // Every item reachable from the parts through recipes, and how many recipes on the list use each one.
            var users = new Dictionary<int, int>();
            var usersOrder = new List<int>();
            var seen = new HashSet<int>();
            var seenOrder = new List<int>();
            var queue = new Queue<int>(partOrder);
            void Use(int id, int add)
            {
                if (!users.ContainsKey(id)) { users[id] = 0; usersOrder.Add(id); }
                users[id] += add;
            }
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!seen.Add(id)) continue;
                seenOrder.Add(id);
                Use(id, 0);
                if (buy.Contains(id) && !parts.ContainsKey(id)) continue;
                foreach (var (ing, _) in recipeOf(id)?.Ingredients ?? new List<(int, int)>())
                {
                    Use(ing, 1);
                    queue.Enqueue(ing);
                }
            }

            // Items in an order where each comes after everything that uses it; a recipe loop leaves items behind, which
            // count as raw materials.
            var order = new List<int>();
            var waiting = new Dictionary<int, int>(users);
            var ready = new Queue<int>(seenOrder.Where(id => waiting[id] == 0));
            while (ready.Count > 0)
            {
                var id = ready.Dequeue();
                order.Add(id);
                if (buy.Contains(id) && !parts.ContainsKey(id)) continue;
                foreach (var (ing, _) in recipeOf(id)?.Ingredients ?? new List<(int, int)>())
                {
                    waiting[ing] -= 1;
                    if (waiting[ing] == 0) ready.Enqueue(ing);
                }
            }
            var inOrder = new HashSet<int>(order);
            var looped = new HashSet<int>(seenOrder.Where(id => !inOrder.Contains(id)));
            order.AddRange(seenOrder.Where(looped.Contains));

            var need = parts.ToDictionary(kv => kv.Key, kv => (long)kv.Value);
            var rParts = new List<ShopRow>();
            var craft = new List<ShopRow>();
            var gather = new List<ShopRow>();
            var crystals = new List<ShopRow>();
            var bought = new List<ShopRow>();
            foreach (var id in order)
            {
                var total = need.GetValueOrDefault(id);
                if (total <= 0) continue;
                var isPart = parts.ContainsKey(id);
                var have = isPart ? 0 : held.GetValueOrDefault(id);
                var shortBy = Math.Max(0, total - have);
                var recipe = looped.Contains(id) ? null : recipeOf(id);
                if (buy.Contains(id) && !isPart) bought.Add(new ShopRow { ItemId = id, Need = total, Have = have, Short = shortBy });
                else if (recipe != null)
                {
                    var crafts = (long)Math.Ceiling(shortBy / (double)recipe.Yield);
                    foreach (var (ing, amount) in recipe.Ingredients) need[ing] = need.GetValueOrDefault(ing) + crafts * amount;
                    if (isPart) rParts.Add(new ShopRow { ItemId = id, Need = total, Crafts = crafts, Job = recipe.Job });
                    else craft.Add(new ShopRow { ItemId = id, Need = total, Have = have, Short = shortBy, Crafts = crafts, Yield = recipe.Yield, Job = recipe.Job });
                }
                else if (isPart) rParts.Add(new ShopRow { ItemId = id, Need = total, Crafts = total, NoRecipe = true });
                else (IsCrystal(id) ? crystals : gather).Add(new ShopRow { ItemId = id, Need = total, Have = have, Short = shortBy });
            }
            craft.Reverse();
            List<ShopRow> ByShort(List<ShopRow> rows) => rows.OrderByDescending(r => r.Short).ThenBy(r => r.ItemId).ToList();
            return (rParts, craft, ByShort(gather), crystals.OrderBy(r => r.ItemId).ToList(), ByShort(bought));
        }

        // ---- Ticks (subs/shopping-progress.js) ----

        /// <summary>JavaScript's Number(x) for a saved value (NaN for what it can't read).</summary>
        private static double Number(JsonElement? e)
        {
            if (e is not { } v) return double.NaN;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.True => 1,
                JsonValueKind.False or JsonValueKind.Null => 0,
                JsonValueKind.String => v.GetString()!.Trim() is var t && t.Length == 0 ? 0
                    : double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
                _ => double.NaN,
            };
        }
        private static double OrZero(double x) => double.IsNaN(x) || x == 0 ? 0 : x;
        private static JsonElement? Field(JsonElement entry, string name) =>
            entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var v) ? v : null;

        /// <summary>A Look ahead the Parts page offers (0, 3, 7 or 14 days), else 0 (ipc/parts.js lookAheadDays).</summary>
        public static int LookAheadDays(double value) => AheadChoices.Contains((int)value) && value == Math.Floor(value) ? (int)value : 0;

        /// <summary>The saved ticks (parseProgress): item (as text) -> its entry, in the order JavaScript goes through them
        /// (whole-number keys smallest first, then the rest as written; a key written twice keeps its last entry); none when
        /// missing or unreadable.</summary>
        public static List<(string Key, JsonElement Entry)> ParseProgress(string? text)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return new();
                var entries = new List<(string Key, JsonElement Entry)>();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var at = entries.FindIndex(e => e.Key == p.Name);
                    if (at >= 0) entries[at] = (p.Name, p.Value.Clone());
                    else entries.Add((p.Name, p.Value.Clone()));
                }
                static bool IsIndex(string k) => k.Length > 0 && (k == "0" || k[0] != '0') && k.All(char.IsAsciiDigit) && k.Length <= 10
                    && ulong.Parse(k, CultureInfo.InvariantCulture) < uint.MaxValue;
                return entries.Where(e => IsIndex(e.Key)).OrderBy(e => ulong.Parse(e.Key, CultureInfo.InvariantCulture))
                    .Concat(entries.Where(e => !IsIndex(e.Key))).ToList();
            }
            catch (JsonException)
            {
                return new();
            }
        }

        /// <summary>How many of each part on the list have been made (reconcileByAhead): ticks made with each Look ahead
        /// against that Look ahead's list, less the parts the plugin has since seen held. lists: Look ahead -> its parts (item, crafts).</summary>
        public static Dictionary<int, long> Made(List<(string Key, JsonElement Entry)> stored, List<(int Ahead, List<(int ItemId, long Crafts)> Parts)> lists)
        {
            var made = new Dictionary<int, long>();
            foreach (var (ahead, parts) in lists)
            {
                var own = new Dictionary<string, JsonElement>();
                foreach (var (key, entry) in stored)
                    if (OrZero(Number(Field(entry, "ahead"))) == ahead) own[key] = entry;
                foreach (var (itemId, crafts) in parts)
                {
                    if (!own.TryGetValue(itemId.ToString(CultureInfo.InvariantCulture), out var entry) || entry.ValueKind is JsonValueKind.Null or JsonValueKind.False
                        || (entry.ValueKind == JsonValueKind.Number && entry.GetDouble() == 0) || (entry.ValueKind == JsonValueKind.String && entry.GetString() == "")) continue;
                    var seenHeld = Math.Max(0, OrZero(Number(Field(entry, "outOf"))) - crafts);
                    var n = Math.Min(crafts, Math.Max(0, OrZero(Number(Field(entry, "made"))) - seenHeld));
                    if (n > 0) made[itemId] = (long)n;
                }
            }
            return made;
        }

        // ---- Market prices (subs/shopping-prices.js) ----

        /// <summary>The choices for "Prices from" (priceChoices): every world, data center and region Universalis knows (the
        /// characters' regions first), plus any world a character is on that it doesn't; and the starting choice (the data
        /// center with the most characters, else a character's world, else the first).</summary>
        public static (List<PriceChoice> Choices, string? Fallback) PriceChoices(List<(string World, int Characters)> worlds,
            IReadOnlyDictionary<string, (string Name, string Dc, string Region)> places)
        {
            var regionCount = new Dictionary<string, int>();
            var dcCount = new Dictionary<string, int>();
            foreach (var (world, characters) in worlds)
            {
                if (string.IsNullOrEmpty(world) || !places.TryGetValue(world.ToLowerInvariant(), out var place)) continue;
                if (!string.IsNullOrEmpty(place.Region)) regionCount[place.Region] = regionCount.GetValueOrDefault(place.Region) + characters;
                if (!string.IsNullOrEmpty(place.Dc)) dcCount[place.Dc] = dcCount.GetValueOrDefault(place.Dc) + characters;
            }
            var known = places.Values.Where(p => !string.IsNullOrEmpty(p.Name)).ToList();
            known.Sort((a, b) =>
            {
                var r = regionCount.GetValueOrDefault(b.Region ?? "") - regionCount.GetValueOrDefault(a.Region ?? "");
                if (r != 0) return r;
                var c = Compare(a.Region ?? "undefined", b.Region ?? "undefined");
                if (c != 0) return c;
                c = Compare(a.Dc ?? "undefined", b.Dc ?? "undefined");
                return c != 0 ? c : Compare(a.Name, b.Name);
            });
            var choices = known.Select(p => new PriceChoice($"world:{p.Name}", string.IsNullOrEmpty(p.Dc) ? p.Name : $"{p.Name} ({p.Dc})", "world", p.Name)).ToList();
            var unknown = worlds.Select(w => w.World).Where(w => !string.IsNullOrEmpty(w) && !places.ContainsKey(w.ToLowerInvariant()))
                .OrderBy(w => w, StringComparer.Ordinal).ToList();
            foreach (var world in unknown.Distinct()) choices.Add(new PriceChoice($"world:{world}", world, "world", world));
            foreach (var dc in known.Select(p => p.Dc).Where(d => !string.IsNullOrEmpty(d)).Distinct()) choices.Add(new PriceChoice($"dc:{dc}", dc, "dc", dc));
            foreach (var region in known.Select(p => p.Region).Where(r => !string.IsNullOrEmpty(r)).Distinct())
                choices.Add(new PriceChoice($"region:{region}", RegionLabels.TryGetValue(region, out var label) ? label : region, "region", region));
            var busiest = dcCount.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, Comparer<string>.Create(Compare)).Select(kv => kv.Key).FirstOrDefault();
            return (choices, busiest != null ? $"dc:{busiest}" : unknown.Count > 0 ? $"world:{unknown[0]}" : choices.FirstOrDefault()?.Value);
        }

        /// <summary>Someone else's listings, cheapest first (sellingListings): free ones and the fleet's own retainers' left out;
        /// world: the world of listings that don't name one (when one world was asked).</summary>
        public static List<ShopListing> Selling(IEnumerable<MarketListing>? listings, ISet<string>? exclude, string? world) =>
            (listings ?? Enumerable.Empty<MarketListing>())
                .Where(l => l.PricePerUnit > 0)
                .Where(l => !(exclude != null && !string.IsNullOrEmpty(l.RetainerName) && exclude.Contains(Universalis.RetainerKey(l.RetainerName, l.WorldName ?? world))))
                .Select(l => new ShopListing(l.PricePerUnit, Math.Max(1, l.Quantity == 0 ? 1 : l.Quantity), l.WorldName ?? world))
                .OrderBy(l => l.Price).ToList();

        private sealed record Source(double Gil, string How, string? World);

        /// <summary>Gil for one of each item the cheapest way (costs): a vendor, a listing, or making it from its recipe (each
        /// ingredient its own cheapest way); on a tie the vendor wins, then the market.</summary>
        private sealed class Costs
        {
            private readonly IReadOnlyDictionary<int, ShopMarket> market;
            private readonly IReadOnlyDictionary<int, double> vendor;
            private readonly Func<int, ShopRecipe?> recipeOf;
            private readonly Dictionary<int, Source?> memo = new();
            private readonly HashSet<int> visiting = new();

            public Costs(IReadOnlyDictionary<int, ShopMarket> market, IReadOnlyDictionary<int, double> vendor, Func<int, ShopRecipe?> recipeOf)
            {
                this.market = market;
                this.vendor = vendor;
                this.recipeOf = recipeOf;
            }

            public double? MakeCost(int id)
            {
                var recipe = recipeOf(id);
                if (recipe == null || recipe.Ingredients.Count == 0) return null;
                double total = 0;
                foreach (var (ing, amount) in recipe.Ingredients)
                {
                    if (Cheapest(ing) is not double each) return null;
                    total += each * amount;
                }
                return total / (recipe.Yield == 0 ? 1 : recipe.Yield);
            }

            public Source? SourceOf(int id)
            {
                if (memo.TryGetValue(id, out var kept)) return kept;
                if (visiting.Contains(id)) return null;
                visiting.Add(id);
                var options = new List<Source>();
                if (vendor.TryGetValue(id, out var v) && v > 0) options.Add(new Source(v, "vendor", null));
                if (market.TryGetValue(id, out var listed) && listed.Price > 0) options.Add(new Source(listed.Price, "market", listed.World));
                if (MakeCost(id) is double make && make > 0) options.Add(new Source(make, "make", null));
                visiting.Remove(id);
                Source? best = null;
                foreach (var o in options) if (best == null || o.Gil < best.Gil) best = o;
                memo[id] = best;
                return best;
            }

            public double? Cheapest(int id) => SourceOf(id)?.Gil;

            public List<ShopBreakdown>? Breakdown(int id, Func<int, string> nameOf, int depth = 0)
            {
                var recipe = recipeOf(id);
                if (recipe == null || recipe.Ingredients.Count == 0) return null;
                return recipe.Ingredients.Select(ing =>
                {
                    var s = SourceOf(ing.ItemId);
                    return new ShopBreakdown(ing.ItemId, nameOf(ing.ItemId), ing.Amount / (double)(recipe.Yield == 0 ? 1 : recipe.Yield), s?.Gil, s?.How, s?.World,
                        s is { How: "make" } && depth < 1 ? Breakdown(ing.ItemId, nameOf, depth + 1) : null);
                }).ToList();
            }
        }

        /// <summary>What buying `wanted` costs going up the listings from the cheapest (buyingCost).</summary>
        public static (double Gil, int Got) BuyingCost(List<ShopListing>? listings, int wanted)
        {
            double gil = 0;
            var got = 0;
            foreach (var l in listings ?? new List<ShopListing>())
            {
                if (got >= wanted) break;
                var take = Math.Min(l.Quantity, wanted - got);
                gil += take * l.Price;
                got += take;
            }
            return (gil, got);
        }

        /// <summary>Market prices on the list (priceList): each part's cheapest listing against what making it costs; each
        /// material's listing and cheapest way; and the totals for buying the parts not made yet and every material short.</summary>
        public static void PriceList(ShoppingResult list, IReadOnlyDictionary<int, ShopMarket> market, IReadOnlyDictionary<int, double> vendor,
            Func<int, ShopRecipe?> recipeOf, Func<int, string> nameOf)
        {
            var costs = new Costs(market, vendor, recipeOf);
            foreach (var p in list.Parts)
            {
                var listed = market.GetValueOrDefault(p.ItemId);
                var make = costs.MakeCost(p.ItemId) is double m ? SubRoutes.Round(m) : (double?)null;
                p.Market = listed;
                p.MakeCost = make;
                p.Cheaper = listed != null && make != null ? (listed.Price < make ? "buy" : "make") : null;
                var wanted = (int)Math.Max(0, p.Crafts - p.Made);
                p.Buying = listed != null && wanted > 0 ? Buying(listed, wanted) : null;
                p.MakeBreakdown = costs.Breakdown(p.ItemId, nameOf);
            }
            foreach (var r in list.Craft.Concat(list.Gather).Concat(list.Crystals))
            {
                var listed = market.GetValueOrDefault(r.ItemId);
                // Craft first's rows have Make, not Short, so buying isn't worked out for them (as the app).
                var wanted = list.Craft.Contains(r) ? 0 : (int)r.Short;
                r.Market = listed;
                r.Cheapest = costs.SourceOf(r.ItemId)?.How;
                r.Buying = listed != null && wanted > 0 ? Buying(listed, wanted) : null;
            }
            var partsLeft = list.Parts.Where(p => p.Crafts - p.Made > 0).ToList();
            var materialsShort = list.Gather.Concat(list.Crystals).Where(r => r.Short > 0).ToList();
            static double Total(List<ShopRow> rows) => rows.Sum(r => r.Buying?.Gil ?? 0);
            static int TooFew(List<ShopRow> rows) => rows.Count(r => r.Buying != null && r.Buying.Got < r.Buying.Wanted);
            list.Totals = new ShoppingTotals(Total(partsLeft), partsLeft.Count(p => p.Market == null), TooFew(partsLeft),
                Total(materialsShort), materialsShort.Count(r => r.Market == null), TooFew(materialsShort));
        }

        private static ShopBuying Buying(ShopMarket listed, int wanted)
        {
            var (gil, got) = BuyingCost(listed.Listings, wanted);
            return new ShopBuying(gil, got, wanted);
        }

        // ---- The whole list (ipc/shopping.js get-shopping-list) ----

        /// <summary>The list as the app's Shopping List shows it. needs: the Parts page's, looking `longest` days ahead (the
        /// longest of `aheadDays` and the ticks' Look aheads, LongestAhead); held: fleet_shopping_held_view; progress: the
        /// saved ticks; market: Live Market Prices (null when off) with the characters' worlds, the fleet's own retainers
        /// (Universalis.RetainerKey) and the "Prices from" chosen (null: the starting choice).</summary>
        public static ShoppingResult Make(List<PartNeed> needs, double now, int aheadDays, string? progress, ShoppingGame game, List<HeldRow> held, bool buyFromVendors,
            IShoppingMarket? market = null, List<(string World, int Characters)>? worlds = null, ISet<string>? exclude = null, string? priceScope = null)
        {
            var ahead = LookAheadDays(aheadDays);
            var stored = ParseProgress(progress);
            var aheads = new List<int> { ahead };
            foreach (var (_, entry) in stored)
            {
                var d = LookAheadDays(Number(Field(entry, "ahead")));
                if (!aheads.Contains(d)) aheads.Add(d);
            }
            (Dictionary<int, int> Counts, List<int> Order) PartsWithin(int days)
            {
                var counts = new Dictionary<int, int>();
                var order = new List<int>();
                foreach (var n in needs)
                {
                    if (n.SpareLocation != null) continue;
                    if (n.DueAt is double due && due > now + days * Day) continue;
                    if (PartsPlanner.PartItemId(n.NeededClass, n.Slot, n.NeededModified) is not int id) continue;
                    if (!counts.ContainsKey(id)) order.Add(id);
                    counts[id] = counts.GetValueOrDefault(id) + 1;
                }
                return (counts, order);
            }
            var byAhead = aheads.ToDictionary(d => d, PartsWithin);
            var parts = byAhead[ahead];
            var longest = byAhead[aheads.Max()];
            if (longest.Counts.Count == 0) return new ShoppingResult { Empty = true };
            if (!game.HasWorkshop(longest.Order))
                return new ShoppingResult { Error = "The game's crafting data isn't there for these parts." };
            var workshop = game.Workshop(longest.Order);
            ShopRecipe? RecipeOf(int id) => workshop.TryGetValue(id, out var w) ? w : game.Recipes.GetValueOrDefault(id);

            // Every item on any of the lists: the parts and everything their recipes ask for, all the way down.
            var allIds = new HashSet<int>(longest.Order);
            var look = new Queue<int>(longest.Order);
            while (look.Count > 0)
                foreach (var (ing, _) in RecipeOf(look.Dequeue())?.Ingredients ?? new List<(int, int)>())
                    if (allIds.Add(ing)) look.Enqueue(ing);
            var heldTotal = new Dictionary<int, long>();
            var heldPlaces = new Dictionary<int, List<HeldPlace>>();
            foreach (var h in held)
            {
                if (!allIds.Contains(h.ItemId) || h.Qty <= 0) continue;
                heldTotal[h.ItemId] = heldTotal.GetValueOrDefault(h.ItemId) + h.Qty;
                if (!heldPlaces.TryGetValue(h.ItemId, out var list)) heldPlaces[h.ItemId] = list = new List<HeldPlace>();
                list.Add(new HeldPlace(h.Who, h.Place, h.Qty));
            }
            foreach (var list in heldPlaces.Values)
            {
                var sorted = list.OrderByDescending(p => p.Qty).ThenBy(p => p.Who, Comparer<string>.Create(Compare)).ThenBy(p => p.Where, Comparer<string>.Create(Compare)).ToList();
                list.Clear();
                list.AddRange(sorted);
            }
            var vendorsLoaded = game.VendorsLoaded;
            var buy = buyFromVendors && vendorsLoaded ? new HashSet<int>(game.Vendor.Keys) : new HashSet<int>();
            var lists = byAhead.ToDictionary(kv => kv.Key, kv => Build(kv.Value.Counts, kv.Value.Order, RecipeOf, heldTotal, buy));
            var built = lists[ahead];

            var made = Made(stored, aheads.Select(d => (d, lists[d].Parts.Select(p => (p.ItemId, p.Crafts)).ToList())).ToList());
            if (parts.Counts.Count == 0) return new ShoppingResult { Empty = true };
            void Describe(ShopRow r)
            {
                r.Name = game.NameOf(r.ItemId);
                r.Places = r.Have > 0 && heldPlaces.TryGetValue(r.ItemId, out var p) ? p : new List<HeldPlace>();
                r.VendorPrice = game.Vendor.TryGetValue(r.ItemId, out var v) ? v : null;
            }
            foreach (var r in built.Parts.Concat(built.Craft).Concat(built.Gather).Concat(built.Crystals).Concat(built.Buy)) Describe(r);
            foreach (var p in built.Parts) p.Made = made.TryGetValue(p.ItemId, out var n) ? Math.Min(n, p.Crafts) : 0;
            foreach (var b in built.Buy) b.Cost = b.Short * (b.VendorPrice ?? 0);
            var result = new ShoppingResult
            {
                VendorsLoaded = vendorsLoaded, BuyFromVendors = buyFromVendors && vendorsLoaded,
                Parts = built.Parts, Craft = built.Craft, Gather = built.Gather, Crystals = built.Crystals, Buy = built.Buy,
                BuyCost = built.Buy.Sum(b => b.Cost),
            };
            if (market == null) return result;

            // Live Market Prices: from the place chosen (or the starting choice), the fleet's own retainers' listings left out.
            var (choices, fallback) = PriceChoices(worlds ?? new List<(string, int)>(), market.Places());
            var chosen = choices.FirstOrDefault(c => c.Value == priceScope) ?? choices.FirstOrDefault(c => c.Value == fallback);
            if (chosen == null)
            {
                result.Market = new ShoppingMarketInfo(true, choices, null, null, null, "No character has a world yet.", null);
                return result;
            }
            (Dictionary<int, List<MarketListing>> Listings, (string Problem, DateTime Since)? Stale) answer;
            try
            {
                answer = market.ListingsIn(chosen.Scope, allIds.ToList());
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException or JsonException)
            {
                result.Market = new ShoppingMarketInfo(true, choices, chosen.Value, chosen.Label, chosen.Kind, ex.Message, null);
                return result;
            }
            var oneWorld = chosen.Kind == "world" ? chosen.Scope : null;
            var prices = new Dictionary<int, ShopMarket>();
            foreach (var (id, listings) in answer.Listings)
            {
                var selling = Selling(listings, exclude, oneWorld);
                if (selling.Count > 0) prices[id] = new ShopMarket(selling[0].Price, selling[0].World, selling);
            }
            PriceList(result, prices, game.Vendor, RecipeOf, game.NameOf);
            result.Market = new ShoppingMarketInfo(true, choices, chosen.Value, chosen.Label, chosen.Kind, null, answer.Stale);
            return result;
        }

        /// <summary>How far ahead the parts must be worked out for the list and its ticks: the longest of `aheadDays` and the ticks' Look aheads.</summary>
        public static int LongestAhead(int aheadDays, string? progress)
        {
            var longest = LookAheadDays(aheadDays);
            foreach (var (_, entry) in ParseProgress(progress)) longest = Math.Max(longest, LookAheadDays(Number(Field(entry, "ahead"))));
            return longest;
        }

        /// <summary>The list as plain text, for pasting somewhere (shoppingListText).</summary>
        public static string Text(ShoppingResult list)
        {
            static string Gil(double n) => $"{n.ToString("N0", CultureInfo.InvariantCulture)} gil";
            var lines = new List<string> { "Sub parts to craft:" };
            foreach (var p in list.Parts) lines.Add($"  {p.Crafts}x {p.Name}{(p.Job != null ? $" ({p.Job})" : "")}{(p.Made > 0 ? $", {p.Made} made" : "")}");
            var bought = list.Buy.Where(r => r.Short > 0).ToList();
            if (list.BuyFromVendors && bought.Count > 0)
            {
                lines.Add("");
                lines.Add($"Buy from vendors ({Gil(list.BuyCost)}):");
                foreach (var r in bought) lines.Add($"  {r.Short}x {r.Name} ({Gil(r.Cost)})");
            }
            var craft = list.Craft.Where(r => r.Short > 0).ToList();
            if (craft.Count > 0)
            {
                lines.Add("");
                lines.Add("Craft first:");
                foreach (var r in craft) lines.Add($"  {r.Short}x {r.Name}{(r.Job != null ? $" ({r.Job})" : "")}");
            }
            foreach (var (title, rows) in new[] { ("Gather or buy:", list.Gather), ("Crystals:", list.Crystals) })
            {
                var shortRows = rows.Where(r => r.Short > 0).ToList();
                if (shortRows.Count == 0) continue;
                lines.Add("");
                lines.Add(title);
                foreach (var r in shortRows) lines.Add($"  {r.Short}x {r.Name}");
            }
            return string.Join("\n", lines);
        }
    }
}
