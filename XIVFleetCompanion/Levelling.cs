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
    /// parts make a stage. With suggestions on, part swaps are offered wherever they make the whole plan shorter. Through
    /// an FC's reading, sectors open as the plan goes (by chance, counted as the expected voyages), and it goes out of its
    /// way to open one when that gets there sooner.
    /// </summary>
    public static class Levelling
    {
        public const int MaxVoyages = 3000;
        /// <summary>A swap is looked at only for at least 3% more experience an hour.</summary>
        public const double MinGain = 0.03;
        /// <summary>No cap on swaps (the fewest days): this many at most, only so a plan can't run on.</summary>
        public const int MaxSwaps = 24;
        /// <summary>The chance each sector on a voyage opens each sector it leads to (Pepper's guide: 20-30%), unless asked
        /// otherwise.</summary>
        public const double UnlockChance = 0.25;
        /// <summary>How far ahead (ranks) the plan starts opening a sector a later stage wants: none, or as soon as it can.</summary>
        public static readonly double?[] Lookaheads = { null, double.PositiveInfinity };
        public static readonly string[] Standard = { "S", "U", "W", "C", "Y" };
        public static readonly string[] AllParts = { "S", "U", "W", "C", "Y", "S+", "U+", "W+", "C+", "Y+" };

        /// <summary>The voyage a sub would run: its route, length, experience (with the sure bonus), tanks and repair kits.</summary>
        public sealed record Voyage(List<int> Route, double Hours, double Exp, double ExpPerHour, int Range, int Tanks, double Kits);

        public sealed record Better(int AtRank, List<int> Route, double ExpPerHour, double Gain, List<int> Missing);

        /// <summary>The sectors a plan may use at some point (opened as it goes), with a key for the searches' memory.</summary>
        public sealed class Open
        {
            public readonly HashSet<int> Set;
            public readonly string Key;
            public Open(IEnumerable<int> ids) { Set = new HashSet<int>(ids); Key = string.Join(",", Set.OrderBy(x => x)); }
        }

        /// <summary>A stage going out of its way to open a sector: the open sector it goes through, the next sector along
        /// the chain, and the sector the plan wants in the end.</summary>
        public sealed record ToOpen(int Via, int Next, int Target);

        /// <summary>What the plan needs to open sectors as it goes (as the app's simulate's unlock).</summary>
        public sealed class Unlock
        {
            public HashSet<int> Open = new();
            public double Chance = UnlockChance;
            public IReadOnlyDictionary<int, List<int>> Opens = new Dictionary<int, List<int>>();
            public IReadOnlyDictionary<int, int> OpenerOf = new Dictionary<int, int>();
            public IReadOnlyDictionary<int, int> RankOf = new Dictionary<int, int>();
            public List<(int Id, int Rank)> Targets = new();
            public double? Lookahead;
            public Unlock With(double? lookahead) => new() { Open = Open, Chance = Chance, Opens = Opens, OpenerOf = OpenerOf, RankOf = RankOf, Targets = Targets, Lookahead = lookahead };
        }

        public sealed class Stage
        {
            public int FromRank, ToRank, Voyages, Tanks;
            public List<int> Route = new();
            public string[] Parts = Array.Empty<string>();
            public double Hours, Exp, ExpPerVoyage, ExpPerHour, Kits; // ExpPerVoyage, ExpPerHour: the stage's averages
            public Better? Better;
            /// <summary>The sectors expected to open on this stage, and why it goes where it does (null: for experience).</summary>
            public List<int> Discovers = new();
            public ToOpen? ToOpen;
            /// <summary>The sectors open when the stage started (for the working out).</summary>
            public Open? OpenAt;
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
            public Open? StuckOpen;
            public List<Swap> Swaps = new();
            public double? Lookahead;
            public double? UnlockChance;
        }

        private static string Key(IEnumerable<int> route) => string.Join("-", route);

        /// <summary>The plan with the parts each rank uses (as the app's simulate). expToNext: rank → experience to the next.
        /// bestAt(rank, parts, open, must): the voyage it would run (open: the sectors it may use, null for the searches' own;
        /// must: a sector the route has to go to). unlock: sectors opening as it goes (null: as they are).</summary>
        public static Plan Simulate(int rank, double exp, int target, IReadOnlyDictionary<int, double> expToNext, Func<int, string[]> partsAt,
            Func<int, string[], Open?, int?, Voyage?> bestAt, Unlock? unlock = null)
        {
            double Step(int r) => expToNext.TryGetValue(r, out var e) ? e : 0;
            var plan = new Plan();
            var r = rank;
            var into = exp;
            var needed = -into;
            for (var k = r; k < target; k++) needed += Step(k);
            var open = unlock != null ? new HashSet<int>(unlock.Open) : null;
            // What's open now, as a stage and the searches see it: a copy, as `open` keeps growing.
            var openNow = open != null ? new Open(open) : null;
            var progress = new Dictionary<int, double>();
            for (var n = 0; n < MaxVoyages && r < target; n++)
            {
                var parts = partsAt(r);
                var best = bestAt(r, parts, openNow, null);
                ToOpen? toOpen = null;
                if (unlock?.Lookahead is double ahead && open != null)
                {
                    foreach (var (t, need) in unlock.Targets)
                    {
                        if (open.Contains(t) || need > r + ahead) continue;
                        // The open sector its chain starts from, and the next one along it.
                        var x = t;
                        var next = t;
                        for (var guard = 0; x != 0 && !open.Contains(x) && guard < 300; guard++) { next = x; x = unlock.OpenerOf.TryGetValue(x, out var f) ? f : 0; }
                        if (x == 0 || !open.Contains(x) || (unlock.RankOf.TryGetValue(x, out var xr) ? xr : 0) > r) continue;
                        if (best != null && best.Route.Contains(x)) break; // its route passes it already
                        var via = bestAt(r, parts, openNow, x);
                        if (via != null) { best = via; toOpen = new ToOpen(x, next, t); }
                        break;
                    }
                }
                if (best == null || !(best.Exp > 0) || !(best.Hours > 0)) { plan.StuckAt = r; break; }
                var last = plan.Stages.Count > 0 ? plan.Stages[^1] : null;
                var stage = last != null && Key(last.Route) == Key(best.Route) && string.Join(",", last.Parts) == string.Join(",", parts) && last.ToOpen?.Via == toOpen?.Via ? last : null;
                if (stage == null)
                {
                    stage = new Stage { FromRank = r, ToRank = r, Route = best.Route, Parts = parts.ToArray(), ToOpen = toOpen, OpenAt = openNow };
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
                if (unlock != null && open != null)
                {
                    var changed = false;
                    foreach (var id in best.Route)
                    {
                        if (!unlock.Opens.TryGetValue(id, out var leads)) continue;
                        foreach (var t in leads)
                        {
                            if (open.Contains(t)) continue;
                            var p = (progress.TryGetValue(t, out var had) ? had : 0) + unlock.Chance;
                            if (p >= 1 - 1e-9) { open.Add(t); progress.Remove(t); stage.Discovers.Add(t); changed = true; } else progress[t] = p;
                        }
                    }
                    if (changed) openNow = new Open(open);
                }
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
            plan.StuckOpen = openNow;
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

        /// <summary>The plan, and with suggestions the part swaps on the way: the one with the fewest hours found (as the
        /// app's levellingPlan). At the start of each stage, and at each rank a part can first be fitted from, every one-part
        /// swap (only parts the sub's rank allows, partRank) is looked at two ways: on the route being run there (score(rank,
        /// parts, route): experience an hour, null when it can't fly it) and on the best route the new parts allow. The best
        /// of each way, at least MinGain better, has the whole plan worked out with it; the shorter is kept when it makes the
        /// plan shorter, and the same rank looked at again for another part. owned: "letter|slot". unlock: as Simulate's,
        /// each way of opening sectors ahead (Lookaheads) tried and the shortest kept.</summary>
        public static Plan LevellingPlan(int rank, double exp, int target, IReadOnlyDictionary<int, double> expToNext, string[] parts,
            Func<int, string[], Open?, int?, Voyage?> bestAt, Func<int, string[], List<int>, double?>? score, bool suggest, bool standardOnly, IReadOnlySet<string> owned,
            Func<string, int, int>? partRank = null, Unlock? unlock = null)
        {
            partRank ??= (_, _) => 0;
            static bool Better(Plan a, Plan? b) => b == null || (a.Reached && !b.Reached) || (a.Reached == b.Reached && a.Hours < b.Hours - 1e-9);
            // The way of opening sectors ahead that makes the build's own plan shortest.
            double? lookahead = null;
            Plan? keep = null;
            foreach (var la in unlock != null ? Lookaheads : new double?[] { null })
            {
                var tried = Simulate(rank, exp, target, expToNext, _ => parts, bestAt, unlock?.With(la));
                if (Better(tried, keep)) { keep = tried; lookahead = la; }
            }
            Plan Sim(List<Swap> list) => Simulate(rank, exp, target, expToNext, PartsWith(parts, list), bestAt, unlock?.With(lookahead));
            keep!.KeepHours = keep.Hours;
            keep.Lookahead = lookahead;
            if (!suggest) return keep;
            var letters = standardOnly ? Standard : AllParts;
            // The ranks a part becomes possible at, on the way.
            var opens = new HashSet<int>();
            foreach (var to in letters) for (var slot = 0; slot < 4; slot++) { var r = partRank(to, slot); if (r > rank && r < target) opens.Add(r); }
            var swaps = new List<Swap>();
            var plan = keep;
            // A plan stuck where its parts can't fly any route: the one-part swap at that rank that gets it to the target
            // soonest, if any does.
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
                        if (bestAt(stuck, tried, plan.StuckOpen, null) == null) continue;
                        var swap = new Swap(stuck, slot, now[slot], to, owned.Contains($"{to}|{slot}"), null);
                        var next = Sim(new List<Swap> { swap });
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
                // The stage being run at that rank.
                Stage? stage = null;
                foreach (var st in plan.Stages) if (st.FromRank <= at) stage = st;
                if (stage == null) { after = at; continue; }
                var now = PartsWith(parts, swaps)(at);
                var onRoute = score?.Invoke(at, now, stage.Route);
                var onBest = bestAt(at, now, stage.OpenAt, null)?.ExpPerHour;
                // The best swap each way: the most gain; on a tie, a part you own, then the earlier slot and letter.
                Swap? topRoute = null, topBest = null;
                void Offer(ref Swap? was, Swap cand)
                {
                    if (cand.Gain >= MinGain && (was == null || cand.Gain > was.Gain!.Value + 1e-9 || (Math.Abs(cand.Gain!.Value - was.Gain!.Value) <= 1e-9 && cand.Owned && !was.Owned))) was = cand;
                }
                for (var slot = 0; slot < 4; slot++)
                {
                    foreach (var to in letters)
                    {
                        if (to == now[slot] || partRank(to, slot) > at) continue;
                        var tried = now.ToArray();
                        tried[slot] = to;
                        var isOwned = owned.Contains($"{to}|{slot}");
                        if (onRoute is double rb && rb != 0 && score!(at, tried, stage.Route) is double v) Offer(ref topRoute, new Swap(at, slot, now[slot], to, isOwned, v / rb - 1));
                        if (onBest is double bb && bb != 0 && bestAt(at, tried, stage.OpenAt, null) is { } bv) Offer(ref topBest, new Swap(at, slot, now[slot], to, isOwned, bv.ExpPerHour / bb - 1));
                    }
                }
                Swap? pickSwap = null;
                Plan? pickNext = null;
                var seen = new HashSet<string>();
                foreach (var cand in new[] { topRoute, topBest })
                {
                    if (cand == null || !seen.Add($"{cand.Slot}|{cand.To}")) continue;
                    var next = Sim(swaps.Append(cand).ToList());
                    // Kept only when the whole plan gets shorter.
                    if (!(next.Reached && (!plan.Reached || next.Hours < plan.Hours - 1e-9))) continue;
                    if (pickNext == null || next.Hours < pickNext.Hours - 1e-9) { pickSwap = cand; pickNext = next; }
                }
                // Nothing more at this rank: on to the next. Else this rank is looked at again, for another part.
                if (pickSwap == null) { after = at; continue; }
                pickSwap.SavedHours = plan.Hours - pickNext!.Hours;
                swaps.Add(pickSwap);
                plan = pickNext;
            }
            plan.Swaps = swaps;
            plan.KeepHours = keep.Hours;
            plan.Lookahead = lookahead;
            return plan;
        }

        /// <summary>What the plan needs to open sectors as it goes, for one sub through an FC's reading (as the app's
        /// unlockFor): the sectors discovered, how each opens, and the targets: each sector the same sub's plan with every
        /// sector uses, from the rank it first uses it.</summary>
        public static Unlock UnlockFor(IReadOnlyDictionary<int, SubSector> sectors, IReadOnlyDictionary<int, (bool Unlocked, bool Explored)> read, Searches search, Searches every,
            double chance, int rank, double exp, int target, IReadOnlyDictionary<int, double> expToNext, string[] parts)
        {
            var all = Simulate(rank, exp, target, expToNext, _ => parts, every.BestAt);
            var targets = new List<(int, int)>();
            var seen = new HashSet<int>();
            foreach (var st in all.Stages) foreach (var id in st.Route) if (seen.Add(id)) targets.Add((id, st.FromRank));
            return new Unlock
            {
                Open = sectors.Keys.Where(id => read.TryGetValue(id, out var x) && (x.Unlocked || x.Explored)).ToHashSet(),
                Chance = Math.Min(1, Math.Max(0.01, chance)),
                Opens = search.Opens, OpenerOf = search.OpenerOf,
                RankOf = sectors.Values.ToDictionary(x => x.Id, x => x.RankReq),
                Targets = targets,
            };
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
            public IReadOnlyDictionary<int, List<int>> Opens => opens;
            public IReadOnlyDictionary<int, int> OpenerOf { get; }
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
                OpenerOf = openerOf;
                States = RouteFinder.SectorStates(sectors.Keys, read, openerOf);
                opens = RouteFinder.OpensOf(openerOf);
                maps = mapIds.OrderBy(m => m).Select(m => sectors.Values.Where(x => x.Map == m).OrderBy(x => x.Id).ToList()).Where(l => l.Count > 0).ToList();
            }

            /// <summary>open: the sectors it may use (the plan opening them as it goes), else the reading's; must: a sector every
            /// route has to go to (a voyage to open what comes after it).</summary>
            public Voyage? BestAt(int rank, string[] parts, Open? open = null, int? must = null)
            {
                var key = $"{rank}|{string.Join(",", parts)}|{(open != null ? open.Key : "-")}|{must}";
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
                        if (must is int m0 && !onMap.Any(x => x.Id == m0)) continue;
                        var allowed = open != null ? onMap.Where(x => x.RankReq <= rank && open.Set.Contains(x.Id)).ToList() : RouteFinder.UsableSectors(onMap, rank, States);
                        if (allowed.Count == 0 || (must is int m1 && !allowed.Any(x => x.Id == m1))) continue;
                        var values = allowed.ToDictionary(x => x.Id, x => RouteFinder.SectorValue(x, data.Find(x.Id), stats, price));
                        var searchKey = $"{onMap[0].Map}|{string.Join(",", allowed.Select(x => x.Id))}|{stats.Speed}|{stats.Range}|{string.Join(",", allowed.Select(x => values[x.Id].Exp))}|{must}";
                        // To matter here, this map's route must beat the best so far (or tie it, from an earlier map).
                        var atLeast = best != null ? best.ExpPerHour * (1 - 1e-6) : 0;
                        RouteFinder.Route? r;
                        if (searches.TryGetValue(searchKey, out var was) && (was.Below == 0 || was.Below <= atLeast)) r = was.Route;
                        else
                        {
                            var found = RouteFinder.FindRoutes(allowed, SubRoutes.StartFor(onMap[0].Id, starts), stats, 5, maxHours, must is int m2 ? new[] { m2 } : Array.Empty<int>(), values, "exp", null, opens, States, false,
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
                    // Not open yet at this stage: as the plan has opened them by then, else as the FC was read.
                    var missing = better.Route.Where(id => st.OpenAt != null ? !st.OpenAt.Set.Contains(id) : !(read.TryGetValue(id, out var x) && x.Unlocked)).ToList();
                    if (missing.Count == 0) continue;
                    var now = own?.BestAt(r, st.Parts, st.OpenAt);
                    var baseRate = now != null ? now.ExpPerHour : st.ExpPerHour;
                    if (better.ExpPerHour >= baseRate * 1.01) { st.Better = new Better(r, better.Route, better.ExpPerHour, better.ExpPerHour / baseRate - 1, missing); break; }
                }
            }
        }
    }
}
