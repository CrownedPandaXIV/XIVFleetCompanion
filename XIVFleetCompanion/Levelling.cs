using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>
    /// The Sub Calculator's Levelling Plan, the same as the app's subs/levelling.js (its tests/db/calculator-fixture.js
    /// writes its answers, which the plugin's tests check this against): a sub's way from its rank to a target rank,
    /// voyage by voyage, each on the route best for experience an hour at the rank it leaves at (RouteFinder), the
    /// experience arriving when it's back and carrying over into the next rank. Voyages on the same route with the same
    /// parts make a stage. With suggestions on, a one-part swap is offered where it makes the route being run at least 3%
    /// faster for experience and the whole plan shorter.
    /// </summary>
    public static class Levelling
    {
        public const int MaxVoyages = 3000;
        public const double MinGain = 0.03;
        public const int MaxSwaps = 4;
        public static readonly string[] Standard = { "S", "U", "W", "C", "Y" };
        public static readonly string[] AllParts = { "S", "U", "W", "C", "Y", "S+", "U+", "W+", "C+", "Y+" };

        /// <summary>The voyage a sub would run: its route, length, experience (with the sure bonus), tanks and repair kits.</summary>
        public sealed record Voyage(List<int> Route, double Hours, double Exp, double ExpPerHour, int Range, int Tanks, double Kits);

        public sealed record Better(List<int> Route, double ExpPerHour, List<int> Missing);

        public sealed class Stage
        {
            public int FromRank, ToRank, Voyages, Tanks;
            public List<int> Route = new();
            public string[] Parts = Array.Empty<string>();
            public double Hours, ExpPerVoyage, ExpPerHour, Kits;
            public Better? Better;
        }

        public sealed record Swap(int AtRank, int Slot, string From, string To, bool Owned, double Gain)
        {
            public double SavedHours { get; set; }
        }

        public sealed class Plan
        {
            public List<Stage> Stages = new();
            public int Voyages, Tanks;
            public double Hours, Kits, ExpNeeded, KeepHours;
            public bool Reached;
            public int? StuckAt;
            public List<Swap> Swaps = new();
        }

        private static string Key(IEnumerable<int> route) => string.Join("-", route);

        /// <summary>The plan with the parts each rank uses (as the app's simulate). expToNext: rank → experience to the next.</summary>
        public static Plan Simulate(int rank, double exp, int target, IReadOnlyDictionary<int, double> expToNext, Func<int, string[]> partsAt, Func<int, string[], Voyage?> bestAt)
        {
            double Step(int r) => expToNext.TryGetValue(r, out var e) ? e : 0;
            var plan = new Plan();
            var r = rank;
            var into = exp;
            var needed = -into;
            for (var k = r; k < target; k++) needed += Step(k);
            for (var n = 0; n < MaxVoyages && r < target; n++)
            {
                var parts = partsAt(r);
                var best = bestAt(r, parts);
                if (best == null || !(best.Exp > 0) || !(best.Hours > 0)) { plan.StuckAt = r; break; }
                var last = plan.Stages.Count > 0 ? plan.Stages[^1] : null;
                var stage = last != null && Key(last.Route) == Key(best.Route) && string.Join(",", last.Parts) == string.Join(",", parts) ? last : null;
                if (stage == null)
                {
                    stage = new Stage { FromRank = r, ToRank = r, Route = best.Route, Parts = parts.ToArray(), ExpPerVoyage = best.Exp, ExpPerHour = best.ExpPerHour };
                    plan.Stages.Add(stage);
                }
                stage.Voyages += 1;
                stage.Hours += best.Hours;
                stage.Tanks += best.Tanks;
                stage.Kits += best.Kits;
                into += best.Exp;
                for (var step = Step(r); step > 0 && into >= step && r < target; step = Step(r))
                {
                    into -= step;
                    r += 1;
                }
                stage.ToRank = r;
                if (!(Step(r) > 0) && r < target) { plan.StuckAt = r; break; }
            }
            plan.Voyages = plan.Stages.Sum(s => s.Voyages);
            plan.Hours = plan.Stages.Sum(s => s.Hours);
            plan.Tanks = plan.Stages.Sum(s => s.Tanks);
            plan.Kits = plan.Stages.Sum(s => s.Kits);
            plan.ExpNeeded = Math.Max(0, needed);
            plan.Reached = r >= target;
            return plan;
        }

        public static Func<int, string[]> PartsWith(string[] parts, IReadOnlyList<Swap> swaps) => rank =>
        {
            var o = parts.ToArray();
            foreach (var s in swaps) if (rank >= s.AtRank) o[s.Slot] = s.To;
            return o;
        };

        /// <summary>The plan, and with suggestions the part swaps on the way (as the app's levellingPlan). score(rank, parts,
        /// route): experience an hour on that route, null when it can't fly it. owned: "letter|slot".</summary>
        public static Plan LevellingPlan(int rank, double exp, int target, IReadOnlyDictionary<int, double> expToNext, string[] parts,
            Func<int, string[], Voyage?> bestAt, Func<int, string[], List<int>, double?>? score, bool suggest, bool standardOnly, IReadOnlySet<string> owned)
        {
            var keep = Simulate(rank, exp, target, expToNext, _ => parts, bestAt);
            keep.KeepHours = keep.Hours;
            if (!suggest || score == null) return keep;
            var letters = standardOnly ? Standard : AllParts;
            var swaps = new List<Swap>();
            var plan = keep;
            var from = 0;
            while (swaps.Count < MaxSwaps && from < plan.Stages.Count)
            {
                var stage = plan.Stages[from];
                var at = stage.FromRank;
                var now = PartsWith(parts, swaps)(at);
                var baseScore = score(at, now, stage.Route);
                Swap? pick = null;
                if (baseScore is double b)
                {
                    for (var slot = 0; slot < 4; slot++)
                    {
                        foreach (var to in letters)
                        {
                            if (to == now[slot]) continue;
                            var tried = now.ToArray();
                            tried[slot] = to;
                            if (score(at, tried, stage.Route) is not double v) continue;
                            var gain = v / b - 1;
                            var isOwned = owned.Contains($"{to}|{slot}");
                            if (gain >= MinGain && (pick == null || gain > pick.Gain + 1e-9 || (Math.Abs(gain - pick.Gain) <= 1e-9 && isOwned && !pick.Owned)))
                                pick = new Swap(at, slot, now[slot], to, isOwned, gain);
                        }
                    }
                }
                if (pick == null) { from += 1; continue; }
                var next = Simulate(rank, exp, target, expToNext, PartsWith(parts, swaps.Append(pick).ToList()), bestAt);
                if (!(next.Reached && (!plan.Reached || next.Hours < plan.Hours - 1e-9))) { from += 1; continue; }
                pick.SavedHours = plan.Hours - next.Hours;
                swaps.Add(pick);
                plan = next;
                from = plan.Stages.FindIndex(s => s.FromRank > at);
                if (from < 0) break;
            }
            plan.Swaps = swaps;
            plan.KeepHours = keep.Hours;
            return plan;
        }

        /// <summary>The searches over every map for one FC's reading (null: every sector) and a longest voyage (as the app's
        /// searches): BestAt(rank, parts) and Score(rank, parts, route). A route search is kept for whatever doesn't change it.</summary>
        public sealed class Searches
        {
            private readonly IReadOnlyDictionary<int, SubSector> sectors;
            private readonly List<SubSector> starts;
            private readonly List<List<SubSector>> maps;
            private readonly Dictionary<int, SubRank> ranks;
            private readonly Dictionary<(int, int), SubPartStat> partStats;
            private readonly IReadOnlyList<SubPartStat> partList;
            private readonly SubCalculatorData.Data data;
            private readonly Func<uint, double> price;
            private readonly double kitCost;
            private readonly double? maxHours;
            private readonly Dictionary<int, List<int>> opens;
            private readonly Dictionary<string, RouteFinder.Route?> searches = new();
            private readonly Dictionary<string, Voyage?> answers = new();
            public readonly Dictionary<int, string?> States;

            public Searches(IReadOnlyDictionary<int, SubSector> sectors, List<SubSector> starts, IEnumerable<int> mapIds, Dictionary<int, SubRank> ranks,
                Dictionary<(int, int), SubPartStat> partStats, IReadOnlyList<SubPartStat> partList, SubCalculatorData.Data data, Func<uint, double> price, double kitCost,
                IReadOnlyDictionary<int, (bool Unlocked, bool Explored)>? read, double? maxHours)
            {
                (this.sectors, this.starts, this.ranks, this.partStats, this.partList, this.data, this.price, this.kitCost, this.maxHours) =
                    (sectors, starts, ranks, partStats, partList, data, price, kitCost, maxHours);
                var openerOf = sectors.Keys.ToDictionary(id => id, id => data.Find(id)?.UnlockedFrom ?? 0);
                States = RouteFinder.SectorStates(sectors.Keys, read, openerOf);
                opens = RouteFinder.OpensOf(openerOf);
                maps = mapIds.OrderBy(m => m).Select(m => sectors.Values.Where(x => x.Map == m).OrderBy(x => x.Id).ToList()).Where(l => l.Count > 0).ToList();
            }

            public Voyage? BestAt(int rank, string[] parts)
            {
                var key = $"{rank}|{string.Join(",", parts)}";
                if (answers.TryGetValue(key, out var had)) return had;
                var stats = SubRoutes.BuildStats(rank, parts, ranks, partStats);
                RouteFinder.Route? best = null;
                if (stats != null)
                {
                    foreach (var onMap in maps)
                    {
                        var allowed = RouteFinder.UsableSectors(onMap, rank, States);
                        if (allowed.Count == 0) continue;
                        var values = allowed.ToDictionary(x => x.Id, x => RouteFinder.SectorValue(x, data.Find(x.Id), stats, price));
                        var searchKey = $"{onMap[0].Map}|{string.Join(",", allowed.Select(x => x.Id))}|{stats.Speed}|{stats.Range}|{string.Join(",", allowed.Select(x => values[x.Id].Exp))}";
                        if (!searches.TryGetValue(searchKey, out var r))
                        {
                            r = RouteFinder.FindRoutes(allowed, SubRoutes.StartFor(onMap[0].Id, starts), stats, 5, maxHours, Array.Empty<int>(), values, "exp", null, opens, States, false)
                                .FirstOrDefault();
                            searches[searchKey] = r;
                        }
                        if (r != null && (best == null || r.ExpPerHour > best.ExpPerHour + 1e-9)) best = r;
                    }
                }
                Voyage? answer = null;
                if (best != null)
                {
                    var p = SubCalculator.PlanFor(best.Sectors, stats, parts.Select(l => SubRoutes.PartClass[l]).ToList(), sectors, starts, partList, data, price, kitCost);
                    answer = new Voyage(best.Sectors, best.Hours, best.Exp, best.ExpPerHour, best.Range, best.Sectors.Sum(id => sectors[id].Tanks), p?.KitsPerVoyage ?? 0);
                }
                answers[key] = answer;
                return answer;
            }

            public double? Score(int rank, string[] parts, List<int> route)
            {
                var stats = SubRoutes.BuildStats(rank, parts, ranks, partStats);
                var list = route.Select(id => sectors.GetValueOrDefault(id)).ToList();
                if (stats == null || list.Count == 0 || list.Any(x => x == null || x.RankReq > rank)) return null;
                var start = SubRoutes.StartFor(list[0]!.Id, starts);
                if (start == null || SubCalculator.RangeUsed(list!, start) > stats.Range) return null;
                double seconds = 12 * 3600;
                double exp = 0;
                var at = start;
                foreach (var x in list)
                {
                    seconds += RouteFinder.Leg(at, x!, stats.Speed).Seconds;
                    exp += RouteFinder.SectorValue(x!, data.Find(x!.Id), stats, price).Exp;
                    at = x!;
                }
                if (maxHours is double h && h > 0 && seconds > h * 3600) return null;
                return exp / (seconds / 3600);
            }
        }

        /// <summary>Each stage told when a sector the FC hasn't discovered would give a better route at that rank (as the
        /// app's markBetter). every: searches with every sector.</summary>
        public static void MarkBetter(Plan plan, Searches? every, IReadOnlyDictionary<int, (bool Unlocked, bool Explored)> read)
        {
            foreach (var st in plan.Stages)
            {
                var better = every?.BestAt(st.FromRank, st.Parts);
                var missing = better?.Route.Where(id => !(read.TryGetValue(id, out var r) && r.Unlocked)).ToList() ?? new List<int>();
                st.Better = better != null && missing.Count > 0 && better.ExpPerHour > st.ExpPerHour * 1.0001 ? new Better(better.Route, better.ExpPerHour, missing) : null;
            }
        }
    }
}
