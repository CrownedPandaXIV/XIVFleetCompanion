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

        public sealed record Better(int AtRank, List<int> Route, double ExpPerHour, double Gain, List<int> Missing);

        public sealed class Stage
        {
            public int FromRank, ToRank, Voyages, Tanks;
            public List<int> Route = new();
            public string[] Parts = Array.Empty<string>();
            public double Hours, Exp, ExpPerVoyage, ExpPerHour, Kits; // ExpPerVoyage, ExpPerHour: the stage's averages
            public Better? Better;
        }

        /// <summary>Gain and SavedHours are null for a swap that lets a stuck plan fly at all.</summary>
        public sealed record Swap(int AtRank, int Slot, string From, string To, bool Owned, double? Gain)
        {
            public double? SavedHours { get; set; }
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
                    stage = new Stage { FromRank = r, ToRank = r, Route = best.Route, Parts = parts.ToArray() };
                    plan.Stages.Add(stage);
                }
                stage.Voyages += 1;
                stage.Hours += best.Hours;
                // The stage's own averages: experience a voyage still grows with rank inside a stage.
                stage.Exp += best.Exp;
                stage.ExpPerVoyage = stage.Exp / stage.Voyages;
                stage.ExpPerHour = stage.Exp / stage.Hours;
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

        /// <summary>The rank each part needs, from the game's parts: (letter, slot) -> rank (0 when unknown). As the app's
        /// partRanks.</summary>
        public static Func<string, int, int> PartRanks(IEnumerable<SubPartStat> gameParts)
        {
            var byKey = new Dictionary<(int, int), int>();
            foreach (var p in gameParts) byKey.TryAdd((p.Class, p.Slot), p.PartRank);
            return (letter, slot) => SubRoutes.PartClass.TryGetValue(letter, out var c) && byKey.TryGetValue((c, slot), out var r) ? r : 0;
        }

        /// <summary>The plan, and with suggestions the part swaps on the way (as the app's levellingPlan): looked at the start
        /// of each stage and at each rank a part can first be fitted from, only parts the sub's rank allows (partRank).
        /// score(rank, parts, route): experience an hour on that route, null when it can't fly it. owned: "letter|slot".</summary>
        public static Plan LevellingPlan(int rank, double exp, int target, IReadOnlyDictionary<int, double> expToNext, string[] parts,
            Func<int, string[], Voyage?> bestAt, Func<int, string[], List<int>, double?>? score, bool suggest, bool standardOnly, IReadOnlySet<string> owned,
            Func<string, int, int>? partRank = null)
        {
            partRank ??= (_, _) => 0;
            var keep = Simulate(rank, exp, target, expToNext, _ => parts, bestAt);
            keep.KeepHours = keep.Hours;
            if (!suggest || score == null) return keep;
            var letters = standardOnly ? Standard : AllParts;
            // The ranks a part becomes possible at, on the way.
            var opens = new HashSet<int>();
            foreach (var to in letters) for (var slot = 0; slot < 4; slot++) { var r = partRank(to, slot); if (r > rank && r < target) opens.Add(r); }
            var swaps = new List<Swap>();
            var plan = keep;
            // A plan stuck where its parts can't fly any route (none could be scored there): the one-part swap at that rank
            // that gets it to the target soonest, if any does.
            if (plan.StuckAt is int stuck && !plan.Reached)
            {
                var now = PartsWith(parts, swaps)(stuck);
                Swap? pick = null;
                Plan? pickPlan = null;
                for (var slot = 0; slot < 4; slot++)
                {
                    foreach (var to in letters)
                    {
                        if (to == now[slot] || partRank(to, slot) > stuck) continue;
                        var tried = now.ToArray();
                        tried[slot] = to;
                        if (bestAt(stuck, tried) == null) continue;
                        var swap = new Swap(stuck, slot, now[slot], to, owned.Contains($"{to}|{slot}"), null);
                        var next = Simulate(rank, exp, target, expToNext, PartsWith(parts, new List<Swap> { swap }), bestAt);
                        if (!next.Reached) continue;
                        if (pickPlan == null || next.Hours < pickPlan.Hours - 1e-9 || (Math.Abs(next.Hours - pickPlan.Hours) <= 1e-9 && swap.Owned && !pick!.Owned))
                        {
                            pick = swap;
                            pickPlan = next;
                        }
                    }
                }
                if (pick != null) { pick.SavedHours = null; swaps.Add(pick); plan = pickPlan!; }
            }
            var after = int.MinValue; // the last rank looked at
            while (swaps.Count < MaxSwaps)
            {
                var points = plan.Stages.Select(st => st.FromRank).Concat(opens).Distinct().Where(r => r > after && r < target).OrderBy(r => r).ToList();
                if (points.Count == 0) break;
                var at = points[0];
                after = at;
                // The stage being run at that rank.
                Stage? stage = null;
                foreach (var st in plan.Stages) if (st.FromRank <= at) stage = st;
                if (stage == null) continue;
                var now = PartsWith(parts, swaps)(at);
                var baseScore = score(at, now, stage.Route);
                Swap? pick = null;
                if (baseScore is double b)
                {
                    for (var slot = 0; slot < 4; slot++)
                    {
                        foreach (var to in letters)
                        {
                            if (to == now[slot] || partRank(to, slot) > at) continue;
                            var tried = now.ToArray();
                            tried[slot] = to;
                            if (score(at, tried, stage.Route) is not double v) continue;
                            var gain = v / b - 1;
                            var isOwned = owned.Contains($"{to}|{slot}");
                            if (gain >= MinGain && (pick == null || gain > pick.Gain!.Value + 1e-9 || (Math.Abs(gain - pick.Gain!.Value) <= 1e-9 && isOwned && !pick.Owned)))
                                pick = new Swap(at, slot, now[slot], to, isOwned, gain);
                        }
                    }
                }
                if (pick == null) continue;
                var next = Simulate(rank, exp, target, expToNext, PartsWith(parts, swaps.Append(pick).ToList()), bestAt);
                if (!(next.Reached && (!plan.Reached || next.Hours < plan.Hours - 1e-9))) continue;
                pick.SavedHours = plan.Hours - next.Hours;
                swaps.Add(pick);
                plan = next;
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
            // A map's search, kept for whatever doesn't change it: the best route (or null), and Below: the rate it was asked
            // to beat when it found none at least that good (0: the true best).
            private readonly Dictionary<string, (RouteFinder.Route? Route, double Below)> searches = new();
            private readonly Dictionary<string, Voyage?> answers = new();
            private int? lead; // the map that gave the last answer, searched first so the others can stop sooner
            private readonly System.Threading.CancellationToken cancel;
            public readonly Dictionary<int, string?> States;

            public Searches(IReadOnlyDictionary<int, SubSector> sectors, List<SubSector> starts, IEnumerable<int> mapIds, Dictionary<int, SubRank> ranks,
                Dictionary<(int, int), SubPartStat> partStats, IReadOnlyList<SubPartStat> partList, SubCalculatorData.Data data, Func<uint, double> price, double kitCost,
                IReadOnlyDictionary<int, (bool Unlocked, bool Explored)>? read, double? maxHours, System.Threading.CancellationToken cancel = default)
            {
                this.cancel = cancel;
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
                cancel.ThrowIfCancellationRequested();
                var stats = SubRoutes.BuildStats(rank, parts, ranks, partStats);
                RouteFinder.Route? best = null;
                var bestIndex = -1;
                if (stats != null)
                {
                    // The maps in their order, the last answer's first; the answer is the same as in map order (a later map
                    // only wins when it is better, an earlier one when it is at least as good).
                    var order = Enumerable.Range(0, maps.Count).ToList();
                    if (lead is int l0) { order.Remove(l0); order.Insert(0, l0); }
                    foreach (var i in order)
                    {
                        var onMap = maps[i];
                        var allowed = RouteFinder.UsableSectors(onMap, rank, States);
                        if (allowed.Count == 0) continue;
                        var values = allowed.ToDictionary(x => x.Id, x => RouteFinder.SectorValue(x, data.Find(x.Id), stats, price));
                        var searchKey = $"{onMap[0].Map}|{string.Join(",", allowed.Select(x => x.Id))}|{stats.Speed}|{stats.Range}|{string.Join(",", allowed.Select(x => values[x.Id].Exp))}";
                        // To matter here, this map's route must beat the best so far (or tie it, from an earlier map).
                        var atLeast = best != null ? best.ExpPerHour * (1 - 1e-6) : 0;
                        RouteFinder.Route? r;
                        if (searches.TryGetValue(searchKey, out var was) && (was.Below == 0 || was.Below <= atLeast)) r = was.Route;
                        else
                        {
                            var found = RouteFinder.FindRoutes(allowed, SubRoutes.StartFor(onMap[0].Id, starts), stats, 5, maxHours, Array.Empty<int>(), values, "exp", null, opens, States, false,
                                bestOnly: true, atLeast: atLeast).FirstOrDefault();
                            var sure = found != null && found.ExpPerHour >= atLeast;
                            r = sure ? found : null;
                            searches[searchKey] = (r, sure || atLeast == 0 ? 0 : atLeast);
                        }
                        if (r != null && (best == null || r.ExpPerHour > best.ExpPerHour + 1e-9 || (Math.Abs(r.ExpPerHour - best.ExpPerHour) <= 1e-9 && i < bestIndex)))
                        {
                            best = r;
                            bestIndex = i;
                        }
                    }
                }
                if (best != null) lead = bestIndex;
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

        /// <summary>Where each part swap shows: before the first stage that uses it (the first voyage leaving at its rank or
        /// above; a swap found where a new part opens can fall inside a stage, as when a voyage goes from rank 49 to 51),
        /// at that stage's rank, when the sub is back in port to change it. As the app's swapsByStage.</summary>
        public static List<List<(Swap Swap, int ShowRank)>> SwapsByStage(Plan plan)
        {
            var output = plan.Stages.Select(_ => new List<(Swap, int)>()).ToList();
            foreach (var s in plan.Swaps)
            {
                var i = plan.Stages.FindIndex(st => st.FromRank >= s.AtRank);
                if (i >= 0) output[i].Add((s, plan.Stages[i].FromRank));
            }
            return output;
        }

        /// <summary>Each stage told when a sector the FC hasn't discovered would give a route at least 1% better at one of
        /// its ranks (the first rank it happens at; as the app's markBetter). every: searches with every sector; own: through
        /// the reading.</summary>
        public static void MarkBetter(Plan plan, Searches? every, IReadOnlyDictionary<int, (bool Unlocked, bool Explored)> read, Searches? own = null)
        {
            foreach (var st in plan.Stages)
            {
                st.Better = null;
                if (every == null) continue;
                for (var r = st.FromRank; r < Math.Max(st.ToRank, st.FromRank + 1); r++)
                {
                    var better = every.BestAt(r, st.Parts);
                    if (better == null) continue;
                    var missing = better.Route.Where(id => !(read.TryGetValue(id, out var x) && x.Unlocked)).ToList();
                    if (missing.Count == 0) continue;
                    var now = own?.BestAt(r, st.Parts);
                    var baseRate = now != null ? now.ExpPerHour : st.ExpPerHour;
                    if (better.ExpPerHour >= baseRate * 1.01) { st.Better = new Better(r, better.Route, better.ExpPerHour, better.ExpPerHour / baseRate - 1, missing); break; }
                }
            }
        }
    }
}
