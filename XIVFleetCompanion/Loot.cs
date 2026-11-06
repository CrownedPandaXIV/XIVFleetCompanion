using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    // 2.x routes stage: Loot per Voyage, as the app's Income tab (ipc/details.js loadLoot, subs/loot.js): what each
    // voyage really brought back over the Income window, from the loot Submarine Tracker records (fleet_voyage_loot_view),
    // per sub and per route, at what a vendor pays (salvage at its fixed price), next to Fight Club's expected; with live
    // prices, at the cheapest listing on the sub's world, data center and region too. The app's tests write made-up fleets
    // and its answers (tests/db/routes-fixture.js) that the plugin's tests check this against.

    /// <summary>One sector of a voyage, from fleet_voyage_loot_view (At in milliseconds; WindowDays: the Income window's).</summary>
    public sealed record VoyageLootRow(string FcId, long Register, double At, int Sector, string? SubName, int? PrimaryItem, int? PrimaryCount,
        int? AdditionalItem, int? AdditionalCount, int? Leg, int WindowDays);

    /// <summary>A voyage: its FC and Submarine Tracker's id for the sub, when it came back, its sectors in the order run, and what it brought back.</summary>
    public sealed record Voyage(string FcId, long Register, double At, string? SubName, List<int> Sectors, Dictionary<int, long> Items)
    {
        public string Key => $"{FcId}|{Register}";
    }

    /// <summary>A sub's voyages over the window: gil at vendor prices, the route taken most often, its newest name, its
    /// character (a tracked member of its FC with a sub of that name, else any) and Fight Club's Current Gil/Day for it.</summary>
    public sealed record LootSub(string FcId, long Register, string? SubName, int Voyages, double Gil, double PerVoyage, double PerDay, string Route, double LastAt,
        long? Cid, string? Character, string? World, string? Account, int? SortIndex, double? ExpectedPerDay)
    {
        public string Key => $"{FcId}|{Register}";
    }

    public sealed record LootRoute(string Route, int Voyages, int Subs, double Gil, double PerVoyage);

    public sealed record LootRead(int WindowDays, List<Voyage> Voyages, List<LootSub> Subs, List<LootRoute> Routes, int VoyageCount, double Gil, double PerDay, long Salvage);

    /// <summary>Loot per Voyage at market prices in one scope: per sub and per route, and the total (voyages without prices left out).</summary>
    public sealed record LootMarket(Dictionary<string, (double Gil, int Voyages, double PerVoyage, double PerDay)> Subs,
        Dictionary<string, (double Gil, int Voyages, double PerVoyage)> Routes, double Gil, double PerDay, int Voyages);

    public static class LootMath
    {
        private static bool IsSalvage(int id) => id >= 22500 && id <= 22507;

        /// <summary>Sector rows into voyages, oldest first, each one's sectors in the order run (by sector number when the
        /// order wasn't recorded for all of them).</summary>
        public static List<Voyage> VoyagesFrom(IEnumerable<VoyageLootRow> rows)
        {
            var voyages = new Dictionary<string, (Voyage V, List<(int Sector, int? Leg)> Legs)>();
            var order = new List<string>();
            foreach (var r in rows)
            {
                var key = $"{r.FcId}|{r.Register}|{r.At}";
                if (!voyages.TryGetValue(key, out var entry))
                {
                    entry = (new Voyage(r.FcId, r.Register, r.At, r.SubName, new List<int>(), new Dictionary<int, long>()), new List<(int, int?)>());
                    voyages[key] = entry;
                    order.Add(key);
                }
                if (!string.IsNullOrEmpty(r.SubName) && entry.V.SubName != r.SubName) { entry = (entry.V with { SubName = r.SubName }, entry.Legs); voyages[key] = entry; }
                entry.Legs.Add((r.Sector, r.Leg));
                foreach (var (item, count) in new[] { (r.PrimaryItem, r.PrimaryCount), (r.AdditionalItem, r.AdditionalCount) })
                {
                    if (item is not int id || id == 0 || count is not int n || n == 0) continue;
                    entry.V.Items[id] = entry.V.Items.GetValueOrDefault(id) + n;
                }
            }
            return order.Select(k =>
            {
                var (v, legs) = voyages[k];
                var ordered = legs.All(l => l.Leg != null);
                v.Sectors.AddRange(legs.OrderBy(l => ordered ? l.Leg!.Value : l.Sector).Select(l => l.Sector));
                return v;
            }).OrderBy(v => v.At).ToList();
        }

        public static double ItemsValue(Dictionary<int, long> items, Func<int, double> price) => items.Sum(kv => kv.Value * price(kv.Key));

        /// <summary>The route a voyage took, as its sectors' letters in the order run ("J-O-V"); a sector with none shows its number.</summary>
        public static string RouteText(List<int> sectors, IReadOnlyDictionary<int, string> letters) =>
            string.Join("-", sectors.Select(s => letters.TryGetValue(s, out var l) ? l : s.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        /// <summary>Per sub and per route over the window (subs/loot.js lootSummary), with each sub's character and expected
        /// (ipc/details.js loadLoot). subs: the Subs page's, in the app's order (Roster order, character, slot, name).</summary>
        public static LootRead Summary(List<VoyageLootRow> rows, int windowDays, Func<int, double> price, IReadOnlyDictionary<int, string> letters,
            List<PartsCharacter> characters, List<SubRoute> subs)
        {
            var voyages = VoyagesFrom(rows);
            var bySub = new Dictionary<string, (string FcId, long Register, string? SubName, int Voyages, double Gil, Dictionary<string, int> Routes, List<string> RouteOrder, double LastAt)>();
            var subOrder = new List<string>();
            var routes = new Dictionary<string, (int Voyages, double Gil, HashSet<string> Subs)>();
            var routeOrder = new List<string>();
            foreach (var v in voyages)
            {
                var gil = ItemsValue(v.Items, price);
                var route = RouteText(v.Sectors, letters);
                if (!bySub.TryGetValue(v.Key, out var s))
                {
                    s = (v.FcId, v.Register, v.SubName, 0, 0, new Dictionary<string, int>(), new List<string>(), 0);
                    subOrder.Add(v.Key);
                }
                s.Voyages += 1;
                s.Gil += gil;
                if (!s.Routes.ContainsKey(route)) { s.Routes[route] = 0; s.RouteOrder.Add(route); }
                s.Routes[route] += 1;
                if (v.At >= s.LastAt) { s.LastAt = v.At; if (v.SubName != null) s.SubName = v.SubName; }
                bySub[v.Key] = s;
                if (!routes.TryGetValue(route, out var r)) { r = (0, 0, new HashSet<string>()); routeOrder.Add(route); }
                r.Voyages += 1;
                r.Gil += gil;
                r.Subs.Add(v.Key);
                routes[route] = r;
            }

            // Each sub's character: a tracked member of its FC with a sub of that name (the last in the app's order), else
            // the first member by id; and Fight Club's Current Gil/Day for that sub.
            var byCid = characters.ToDictionary(c => c.Cid);
            var subFor = new Dictionary<string, (SubRoute Sub, PartsCharacter Character)>();
            foreach (var s in subs.OrderBy(x => byCid.TryGetValue(x.Cid, out var c) && c.SortIndex != null ? 0 : 1)
                         .ThenBy(x => byCid.TryGetValue(x.Cid, out var c) ? c.SortIndex ?? 0 : 0).ThenBy(x => x.Cid)
                         .ThenBy(x => x.Slot == null ? 1 : 0).ThenBy(x => x.Slot ?? 0).ThenBy(x => x.SubName, StringComparer.Ordinal))
                if (byCid.TryGetValue(s.Cid, out var c) && !string.IsNullOrEmpty(c.FcId)) subFor[$"{c.FcId}|{s.SubName}"] = (s, c);

            var subRows = subOrder.Select(k =>
            {
                var s = bySub[k];
                var match = subFor.TryGetValue($"{s.FcId}|{s.SubName ?? "null"}", out var m) ? m : ((SubRoute Sub, PartsCharacter Character)?)null;
                var character = match?.Character ?? characters.OrderBy(c => c.Cid).FirstOrDefault(c => c.FcId == s.FcId);
                var mostRun = s.RouteOrder.OrderByDescending(x => s.Routes[x]).First();
                return new LootSub(s.FcId, s.Register, s.SubName, s.Voyages, s.Gil, s.Gil / s.Voyages, s.Gil / windowDays, mostRun, s.LastAt,
                    character?.Cid, character?.Name, character?.World, character?.Account, character?.SortIndex, match?.Sub.CurrentGilDay);
            }).ToList();
            var routeRows = routeOrder.Select(x => new LootRoute(x, routes[x].Voyages, routes[x].Subs.Count, routes[x].Gil, routes[x].Gil / routes[x].Voyages))
                .OrderByDescending(x => x.PerVoyage).ToList();
            var total = subRows.Sum(s => s.Gil);
            var salvage = voyages.Sum(v => v.Items.Where(kv => IsSalvage(kv.Key)).Sum(kv => kv.Value));
            return new LootRead(windowDays, voyages, subRows, routeRows, voyages.Count, total, total / windowDays, salvage);
        }

        /// <summary>Each voyage's loot at market prices in one scope on its sub's world (subs/loot.js marketLoot): prices(world)
        /// gives item prices there (null: none for that world, so its voyages are left out).</summary>
        public static LootMarket Market(List<Voyage> voyages, IReadOnlyDictionary<string, string> worldOf, Func<string, IReadOnlyDictionary<int, double>?> prices,
            IReadOnlyDictionary<int, string> letters, int windowDays)
        {
            var subs = new Dictionary<string, (double Gil, int Voyages)>();
            var routes = new Dictionary<string, (double Gil, int Voyages)>();
            double gilTotal = 0;
            var count = 0;
            foreach (var v in voyages)
            {
                if (!worldOf.TryGetValue(v.Key, out var world) || prices(world) is not { } p) continue;
                var gil = ItemsValue(v.Items, id => p.TryGetValue(id, out var x) ? x : 0);
                var route = RouteText(v.Sectors, letters);
                var s = subs.GetValueOrDefault(v.Key);
                subs[v.Key] = (s.Gil + gil, s.Voyages + 1);
                var r = routes.GetValueOrDefault(route);
                routes[route] = (r.Gil + gil, r.Voyages + 1);
                gilTotal += gil;
                count += 1;
            }
            return new LootMarket(subs.ToDictionary(kv => kv.Key, kv => (kv.Value.Gil, kv.Value.Voyages, kv.Value.Gil / kv.Value.Voyages, kv.Value.Gil / windowDays)),
                routes.ToDictionary(kv => kv.Key, kv => (kv.Value.Gil, kv.Value.Voyages, kv.Value.Gil / kv.Value.Voyages)), gilTotal, gilTotal / windowDays, count);
        }
    }
}
