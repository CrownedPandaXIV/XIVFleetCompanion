using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>
    /// The Sub Calculator's Route Finder, the same as the app's subs/route-finder.js (its tests/db/calculator-fixture.js
    /// writes its answers, which the plugin's tests check this against): the best routes for a build on one map, what a
    /// Free Company can explore next, and Fight Club routes checked against what it has discovered.
    ///
    /// Routes: every set of up to 5 sectors the sub may go to, each in the fastest order its range allows (the least range
    /// on a tie), as SubCalculator.BestOrder picks it. Orders are built a stop at a time; one no faster and using no less
    /// range than another order of the same sectors ending at the same sector is dropped (a leg's time and range don't
    /// depend on the stops before it). A route over the sub's range or the longest voyage is never kept.
    /// </summary>
    public static class RouteFinder
    {
        public const int MaxStops = 5;
        private const double FixedVoyageSeconds = 12 * 60 * 60;

        /// <summary>A sector's state for one FC: "explored", "discovered" (unlocked, not explored), "chance" (not discovered,
        /// but the sector it opens from is), "locked"; null when the FC hasn't been read.</summary>
        public static Dictionary<int, string?> SectorStates(IEnumerable<int> sectorIds, IReadOnlyDictionary<int, (bool Unlocked, bool Explored)>? read,
            IReadOnlyDictionary<int, int> openerOf)
        {
            var states = new Dictionary<int, string?>();
            foreach (var id in sectorIds)
            {
                if (read == null) { states[id] = null; continue; }
                if (read.TryGetValue(id, out var r) && r.Explored) states[id] = "explored";
                else if (read.TryGetValue(id, out r) && r.Unlocked) states[id] = "discovered";
                else
                {
                    var opener = openerOf.TryGetValue(id, out var o) ? o : 0;
                    states[id] = read.TryGetValue(opener, out var f) && f.Unlocked ? "chance" : "locked";
                }
            }
            return states;
        }

        /// <summary>The sectors each sector opens.</summary>
        public static Dictionary<int, List<int>> OpensOf(IReadOnlyDictionary<int, int> openerOf)
        {
            var output = new Dictionary<int, List<int>>();
            foreach (var (id, from) in openerOf)
                if (from != 0) { if (!output.TryGetValue(from, out var list)) output[from] = list = new(); list.Add(id); }
            return output;
        }

        private static string? StateOf(IReadOnlyDictionary<int, string?> states, int id) => states.TryGetValue(id, out var s) ? s : null;

        /// <summary>The sectors on a map a route may use: within the sub's rank, discovered by the FC (any while it isn't read),
        /// and not left out.</summary>
        public static List<SubSector> UsableSectors(IEnumerable<SubSector> onMap, int rank, IReadOnlyDictionary<int, string?> states, ICollection<int>? leaveOut = null) =>
            onMap.Where(x => x.RankReq <= rank && (leaveOut == null || !leaveOut.Contains(x.Id))
                && StateOf(states, x.Id) is null or "explored" or "discovered").ToList();

        /// <summary>What one sector brings on a voyage for this build: experience with the sure bonus, gil and each item's
        /// average count, and the loot tier reached.</summary>
        public sealed record Value(double Exp, double Gil, Dictionary<uint, double> Items, int? Tier, string? TierSure);

        public static Value SectorValue(SubSector sector, SubCalculatorData.Sector? data, SubStats stats, Func<uint, double> price)
        {
            if (data == null) return new Value(sector.ExpReward, 0, new(), null, null);
            var result = SubCalculator.Result(data, stats);
            var items = new Dictionary<uint, double>();
            double gil = 0;
            foreach (var l in SubCalculator.SectorLoot(data, result))
            {
                items[l.ItemId] = (items.TryGetValue(l.ItemId, out var was) ? was : 0) + l.PerVoyage;
                gil += l.PerVoyage * price(l.ItemId);
            }
            return new Value(SubCalculator.SectorExp(sector.ExpReward, SubCalculator.BonusExp(result).Sure), gil, items, result.Tier, result.TierSure);
        }

        /// <summary>A route: its sectors in order, voyage, range, experience, gil and the item a day, and how many sectors it
        /// explores (discovered, not explored) and chances it has at new ones.</summary>
        public sealed record Route(List<int> Sectors, double Hours, int Range, double Exp, double ExpPerHour, double GilPerVoyage, double GilPerDay,
            double? ItemPerDay, int Explores, int Chances);

        private sealed class Label
        {
            public double Seconds;
            public int Range;
            public int Last;
            public Label? Before;
            public double Exp;
        }

        internal static (double Seconds, int Range) Leg(SubSector from, SubSector to, double speed)
        {
            var s = Math.Max(1, speed);
            var d = SubRoutes.Hypot(from.X - to.X, from.Y - to.Y, from.Z - to.Z);
            return (Math.Floor(d * 3990 / (s * 100) * 60) + Math.Floor(to.SurveyMinutes * 7000 / (s * 100) * 60), (int)Math.Floor(d * 0.035) + to.SurveyDistance);
        }

        /// <summary>The routes, best first (as the app's findRoutes). rankBy "exp", "gil" or "item" (itemId).</summary>
        public static List<Route> FindRoutes(IReadOnlyList<SubSector> sectors, SubSector? start, SubStats? stats, int maxStops, double? maxHours,
            IReadOnlyCollection<int> must, IReadOnlyDictionary<int, Value> values, string rankBy, uint? itemId,
            IReadOnlyDictionary<int, List<int>> opens, IReadOnlyDictionary<int, string?> states, bool exploreFirst, bool bestOnly = false, double atLeast = 0,
            int limit = 0, System.Threading.CancellationToken cancel = default)
        {
            // bestOnly (ranking by experience): only the best route is wanted (the Levelling Plan), so an order that can't
            // beat the best found so far however it goes on is not followed; atLeast: experience an hour already had on
            // another map, a route below it may be left out. limit: only the best this many are wanted (the Route Finder,
            // Compare), so, the same way, an order that can't reach the limit-th best so far is not followed (not with
            // exploreFirst). As the app's findRoutes.
            if (start == null || stats == null || sectors.Count == 0) return new();
            var list = sectors.OrderBy(s => s.Id).ToList();
            var n = list.Count;
            if (n > 30) throw new ArgumentException("A map has at most 30 sectors.");
            var stops = Math.Max(1, Math.Min(MaxStops, maxStops <= 0 ? MaxStops : maxStops));
            var secondsMax = maxHours is double h && h > 0 ? h * 3600 : double.PositiveInfinity;
            var mustMask = 0;
            for (var i = 0; i < n; i++) if (must.Contains(list[i].Id)) mustMask |= 1 << i;
            var first = list.Select(s => Leg(start, s, stats.Speed)).ToArray();
            var legs = list.Select(a => list.Select(b => Leg(a, b, stats.Speed)).ToArray()).ToArray();

            // bestOnly or limit: the most an hour (of what the routes are ranked by) any order could still reach: with m more
            // stops, at most the m best sectors' amounts, and at least the m shortest ways into a sector in time.
            var want = bestOnly && rankBy == "exp" ? 1 : Math.Max(0, limit);
            var pruning = want > 0 && !exploreFirst;
            double AmountOf(Value? v) => v == null ? 0 : rankBy == "gil" ? v.Gil
                : rankBy == "item" ? (itemId is uint it0 && v.Items.TryGetValue(it0, out var c0) ? c0 : 0) : v.Exp;
            var expOf = list.Select(s => AmountOf(values.TryGetValue(s.Id, out var v) ? v : null)).ToArray();
            var top = new List<double> { 0 };
            foreach (var e in expOf.OrderByDescending(e => e)) top.Add(top[^1] + Math.Max(0, e));
            var into = new double[n];
            for (var j = 0; j < n; j++)
            {
                var m = double.PositiveInfinity;
                for (var i = 0; i < n; i++) if (i != j) m = Math.Min(m, legs[i][j].Seconds);
                into[j] = double.IsFinite(m) ? m : 0;
            }
            var least = new List<double> { 0 };
            foreach (var c in into.OrderBy(c => c)) least.Add(least[^1] + c);
            var bestRate = pruning && want == 1 && rankBy == "exp" && atLeast > 0 ? atLeast / 3600 : double.NegativeInfinity;
            // With a limit above 1: each set's best rate so far, for the `want` best sets; bestRate is the lowest once full.
            var tops = new Dictionary<int, double>();
            void Ranked(int mask, double rate)
            {
                if (want == 1) { bestRate = Math.Max(bestRate, rate); return; }
                if (tops.TryGetValue(mask, out var was)) { if (rate <= was) return; tops[mask] = rate; }
                else if (tops.Count < want) tops[mask] = rate;
                else
                {
                    if (rate <= bestRate) return;
                    var lowMask = -1;
                    var low = double.PositiveInfinity;
                    foreach (var (m, r) in tops) if (r < low) { low = r; lowMask = m; }
                    tops.Remove(lowMask);
                    tops[mask] = rate;
                }
                if (tops.Count == want) bestRate = tops.Values.Min();
            }
            bool CouldBeat(double exp, double seconds, int size)
            {
                if (!pruning || bestRate <= 0) return true;
                for (var m = 0; m <= stops - size && m < top.Count; m++)
                    if ((exp + top[m]) / (seconds + least[m]) >= bestRate * (1 - 1e-9)) return true;
                return false;
            }
            // A good route to start from: from each sector, the next stop giving the most experience an hour so far.
            if (pruning && want == 1 && mustMask == 0)
            {
                for (var i = 0; i < n; i++)
                {
                    var at = i;
                    var seconds = FixedVoyageSeconds + first[i].Seconds;
                    double range = first[i].Range;
                    var exp = expOf[i];
                    var used = 1 << i;
                    if (range > stats.Range || seconds > secondsMax) continue;
                    bestRate = Math.Max(bestRate, exp / seconds);
                    for (var size = 1; size < stops; size++)
                    {
                        var pick = -1;
                        var pickRate = double.NegativeInfinity;
                        for (var j = 0; j < n; j++)
                        {
                            if ((used & (1 << j)) != 0) continue;
                            var l = legs[at][j];
                            if (range + l.Range > stats.Range || seconds + l.Seconds > secondsMax) continue;
                            var rate = (exp + expOf[j]) / (seconds + l.Seconds);
                            if (rate > pickRate) { pickRate = rate; pick = j; }
                        }
                        if (pick < 0) break;
                        seconds += legs[at][pick].Seconds; range += legs[at][pick].Range; exp += expOf[pick]; used |= 1 << pick; at = pick;
                        bestRate = Math.Max(bestRate, exp / seconds);
                    }
                }
            }

            var layer = new Dictionary<long, List<Label>>();
            var best = new Dictionary<int, Label>();
            // On an exact tie (same time and range) the order that comes first by its stops wins, whichever was found
            // first, so the answer doesn't depend on what the search left out.
            static List<int> OrderOf(Label label) { var o = new List<int>(); for (var at = label; at != null; at = at.Before) o.Insert(0, at.Last); return o; }
            static bool Earlier(Label a, Label b)
            {
                var x = OrderOf(a);
                var y = OrderOf(b);
                for (var i = 0; i < Math.Min(x.Count, y.Count); i++) if (x[i] != y[i]) return x[i] < y[i];
                return x.Count < y.Count;
            }
            void Offer(Dictionary<long, List<Label>> map, long key, Label label)
            {
                if (!map.TryGetValue(key, out var was)) { map[key] = new List<Label> { label }; return; }
                foreach (var o in was)
                {
                    if (o.Seconds == label.Seconds && o.Range == label.Range && Earlier(label, o)) continue;
                    if (o.Seconds <= label.Seconds && o.Range <= label.Range) return;
                }
                was.RemoveAll(o => label.Seconds <= o.Seconds && label.Range <= o.Range);
                was.Add(label);
            }
            void Keep(int mask, Label label)
            {
                if (pruning && (mask & mustMask) == mustMask) Ranked(mask, label.Exp / label.Seconds);
                if (!best.TryGetValue(mask, out var was) || label.Seconds < was.Seconds
                    || (label.Seconds == was.Seconds && (label.Range < was.Range || (label.Range == was.Range && Earlier(label, was))))) best[mask] = label;
            }
            for (var i = 0; i < n; i++)
            {
                var label = new Label { Seconds = FixedVoyageSeconds + first[i].Seconds, Range = first[i].Range, Last = i, Exp = expOf[i] };
                if (label.Range > stats.Range || label.Seconds > secondsMax) continue;
                Offer(layer, (long)(1 << i) * 32 + i, label);
            }
            for (var size = 1; size <= stops; size++)
            {
                var next = new Dictionary<long, List<Label>>();
                cancel.ThrowIfCancellationRequested();
                var tick = 0;
                foreach (var (key, labels) in layer)
                {
                    if ((++tick & 1023) == 0) cancel.ThrowIfCancellationRequested();
                    var mask = (int)(key / 32);
                    var last = (int)(key % 32);
                    foreach (var label in labels)
                    {
                        Keep(mask, label);
                        if (size == stops) continue;
                        for (var j = 0; j < n; j++)
                        {
                            if ((mask & (1 << j)) != 0) continue;
                            var l = legs[last][j];
                            var grown = new Label { Seconds = label.Seconds + l.Seconds, Range = label.Range + l.Range, Last = j, Before = label, Exp = label.Exp + expOf[j] };
                            if (grown.Range > stats.Range || grown.Seconds > secondsMax) continue;
                            if (!CouldBeat(grown.Exp, grown.Seconds, size + 1)) continue;
                            Offer(next, (long)(mask | (1 << j)) * 32 + j, grown);
                        }
                    }
                }
                layer = next;
            }

            var routes = new List<Route>();
            foreach (var (mask, label) in best)
            {
                if ((mask & mustMask) != mustMask) continue;
                var ids = new List<int>();
                for (var at = label; at != null; at = at.Before) ids.Insert(0, list[at.Last].Id);
                var hours = label.Seconds / 3600;
                var perDay = 24 / hours;
                double exp = 0, gil = 0, item = 0;
                int explores = 0, chances = 0;
                foreach (var id in ids)
                {
                    var v = values[id];
                    exp += v.Exp;
                    gil += v.Gil;
                    if (itemId is uint it) item += v.Items.TryGetValue(it, out var c) ? c : 0;
                    if (StateOf(states, id) == "discovered") explores++;
                    if (opens.TryGetValue(id, out var targets)) foreach (var t in targets) if (StateOf(states, t) == "chance") chances++;
                }
                routes.Add(new Route(ids, hours, label.Range, exp, exp / hours, gil, gil * perDay, itemId != null ? item * perDay : null, explores, chances));
            }
            double Score(Route r) => rankBy == "gil" ? r.GilPerDay : rankBy == "item" ? r.ItemPerDay ?? 0 : r.ExpPerHour;
            int New(Route r) => r.Explores + r.Chances;
            routes.Sort((a, b) =>
            {
                int x;
                if (exploreFirst && (x = New(b).CompareTo(New(a))) != 0) return x;
                if ((x = Score(b).CompareTo(Score(a))) != 0) return x;
                if ((x = a.Hours.CompareTo(b.Hours)) != 0) return x;
                if ((x = a.Sectors.Count.CompareTo(b.Sectors.Count)) != 0) return x;
                return string.CompareOrdinal(string.Join(",", a.Sectors), string.Join(",", b.Sectors));
            });
            return pruning && routes.Count > want ? routes.GetRange(0, want) : routes;
        }

        // ---- Unlocks ----

        public sealed record MapProgress(int Id, string Name, int Total, int Explored, int Discovered, bool Open);
        public sealed record Step(int Id, string How);
        public sealed record NextMap(int Id, string Name, int First, List<Step> Steps);
        public sealed record Unlocks(List<MapProgress> Maps, List<int> Sure, List<(int Id, int From)> Chance, NextMap? Next);

        /// <summary>What an FC can explore next (as the app's unlocks): each map's progress, the sectors sure to explore next,
        /// those that could open by chance and from where, and the way to the first map not open yet.</summary>
        public static Unlocks UnlocksOf(IReadOnlyList<SubSector> sectors, IReadOnlyList<(int Id, string Name)> maps, IReadOnlyDictionary<int, string?> states,
            IReadOnlyDictionary<int, int> openerOf)
        {
            var byMap = sectors.GroupBy(s => s.Map).ToDictionary(g => g.Key, g => g.ToList());
            var mapRows = maps.Where(m => byMap.ContainsKey(m.Id)).Select(m =>
            {
                var list = byMap[m.Id];
                var explored = list.Count(s => StateOf(states, s.Id) == "explored");
                var discovered = list.Count(s => StateOf(states, s.Id) == "discovered");
                return new MapProgress(m.Id, m.Name, list.Count, explored, discovered, explored + discovered > 0);
            }).ToList();
            var sure = sectors.Where(s => StateOf(states, s.Id) == "discovered").Select(s => s.Id).ToList();
            int OpenerOf(int id) => openerOf.TryGetValue(id, out var f) ? f : 0;
            var chance = sectors.Where(s => StateOf(states, s.Id) == "chance").Select(s => (s.Id, OpenerOf(s.Id))).ToList();
            NextMap? next = null;
            var closed = mapRows.FirstOrDefault(m => !m.Open);
            if (closed != null)
            {
                var first = byMap[closed.Id].OrderBy(s => s.Id).First();
                var chain = new List<int> { first.Id };
                var seen = new HashSet<int>(chain);
                var at = first.Id;
                while (true)
                {
                    var from = OpenerOf(at);
                    if (from == 0 || seen.Contains(from)) break;
                    chain.Insert(0, from);
                    seen.Add(from);
                    var st = StateOf(states, from);
                    if (st is "explored" or "discovered") break;
                    at = from;
                }
                var head = StateOf(states, chain[0]);
                if (head is "explored" or "discovered")
                    next = new NextMap(closed.Id, closed.Name, first.Id,
                        chain.Select((id, i) => new Step(id, i > 0 ? "chance" : head == "discovered" ? "explore" : "go")).ToList());
            }
            return new Unlocks(mapRows, sure, chance, next);
        }

        // ---- Fight Club routes against what the FC has discovered ----

        /// <summary>A Fight Club route's sector ids ("MROJZ"): its letters on the first map (lowest id) that has every one;
        /// null when no map has them all. maps: map id → letter → sector id.</summary>
        public static List<int>? RouteIds(string? route, IReadOnlyDictionary<int, Dictionary<string, int>> maps)
        {
            var letters = (route ?? "").Replace("-", "");
            foreach (var (_, byLetter) in maps.OrderBy(m => m.Key))
            {
                var ids = new List<int>();
                var i = 0;
                while (i < letters.Length)
                {
                    var two = letters.Substring(i, Math.Min(2, letters.Length - i));
                    if (two.Length == 2 && byLetter.TryGetValue(two, out var id2)) { ids.Add(id2); i += 2; }
                    else if (byLetter.TryGetValue(letters.Substring(i, 1), out var id1)) { ids.Add(id1); i += 1; }
                    else break;
                }
                if (i == letters.Length && ids.Count > 0) return ids;
            }
            return null;
        }

        /// <summary>The Fight Club route a sub at rank would run, checked against the sectors its FC has discovered (null: not
        /// read): the route, its sectors not discovered yet, and the best route the FC can run now with its gil a day.</summary>
        public sealed record FightClubCheck(string? Route, List<int> Missing, string? AltRoute, double? AltNet);

        public static FightClubCheck CheckFightClub(IReadOnlyList<FightClubRow> rows, int rank, IReadOnlyDictionary<int, Dictionary<string, int>> maps,
            IReadOnlyDictionary<int, (bool Unlocked, bool Explored)>? read)
        {
            var options = SubRoutes.Options(rows);
            var found = SubRoutes.BestOf(options, rank);
            if (found == null || read == null) return new FightClubCheck(found?.Row.Route, new(), null, null);
            bool Discovered(int id) => read.TryGetValue(id, out var r) && r.Unlocked;
            var ids = RouteIds(found.Row.Route, maps);
            if (ids == null) return new FightClubCheck(found.Row.Route, new(), null, null);
            var missing = ids.Where(id => !Discovered(id)).ToList();
            if (missing.Count == 0) return new FightClubCheck(found.Row.Route, missing, null, null);
            var runnable = options.Where(o => RouteIds(o.Row.Route, maps) is { } r && r.All(Discovered)).ToList();
            var alt = SubRoutes.BestOf(runnable, rank);
            return new FightClubCheck(found.Row.Route, missing, alt?.Row.Route, alt?.Net);
        }
    }
}
