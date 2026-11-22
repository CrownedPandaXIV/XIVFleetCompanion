using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>
    /// The Sub Calculator's working out, the same as the app's subs/calculator.js (its tests/db/calculator-fixture.js writes
    /// its answers, which the plugin's tests check this against): a build and a route in, what the voyage gives out.
    /// Sectors, ranks and parts come from the game's files (SubGameData); breakpoints and loot from SubCalculatorData
    /// (XIVStats, with Pepper's breakpoints beside them). Range, damage and the voyage length are worked out as the
    /// SubmarineTracker plugin does (MIT licence, by Infi).
    /// </summary>
    public static class SubCalculator
    {
        public const int FullCondition = 30000;

        /// <summary>A breakpoint as a range: the lower and higher of XIVStats' and Pepper's figures (the same when they agree).</summary>
        public sealed record Breakpoint(int Low, int High);

        /// <summary>How much more of each stat makes each result sure.</summary>
        public sealed record Need(double T2, double T3, double Normal, double Optimal, double Favor);

        /// <summary>What a build reaches on one sector. Sure values: "yes", "maybe" (between the sources), "no".</summary>
        public sealed record SectorResult(int Tier, string TierSure, string Retrieval, string RetrievalSure, string DoubleDip,
            int SureTier, string SureRetrieval, Need Need);

        /// <summary>Bonus experience in percent: what the build is sure of, and the most it could get.</summary>
        public sealed record Bonus(int Sure, int Best);

        /// <summary>One item a sector can give: on average how many a voyage, the chance of it at all, and how many each time.</summary>
        public sealed record LootLine(uint ItemId, int Tier, double Chance, int Low, int High, double PerVoyage);

        /// <summary>One item over the route: the sectors giving it, how many a voyage, the gil, and the chance of getting it
        /// at all on a voyage (Chance, from any of its sectors).</summary>
        public sealed record RouteLoot(uint ItemId, int Tier, List<string> Sectors, double PerVoyage, int Low, int High, double Gil, double Chance = 0);

        /// <summary>A part's wear on one voyage: condition lost, voyages it lasts, repair materials a voyage.</summary>
        public sealed record Wear(int Slot, double Lost, int? VoyagesLasting, double KitsPerVoyage);

        public sealed record SectorPlan(SubSector Sector, SubCalculatorData.Sector? Data, SectorResult? Result, List<LootLine>? Loot, Bonus? Bonus);

        public sealed record Plan(List<SectorPlan> Sectors, SubSector? Start, int? Range, double Hours, double Exp, double ExpSure, double ExpBest, int Tanks,
            List<RouteLoot> Loot, double GilPerVoyage, double GilPerDay, double ExpPerDay, double VoyagesPerDay, List<Wear?> Wear, double? KitsPerVoyage,
            double? RepairGilPerDay, List<SubSector> MissingData);

        /// <summary>Range a route uses: each leg × 0.035 rounded down, plus each sector's survey distance; not the way back.</summary>
        public static int? RangeUsed(IReadOnlyList<SubSector> sectors, SubSector? start)
        {
            if (sectors.Count == 0 || start == null) return null;
            var total = 0;
            var previous = start;
            foreach (var s in sectors)
            {
                total += (int)Math.Floor(SubRoutes.Hypot(previous.X - s.X, previous.Y - s.Y, previous.Z - s.Z) * 0.035) + s.SurveyDistance;
                previous = s;
            }
            return total;
        }

        // Every order of a list, in the order the app's permutations() makes them.
        private static IEnumerable<List<T>> Permutations<T>(List<T> list)
        {
            if (list.Count <= 1) { yield return list; yield break; }
            for (var i = 0; i < list.Count; i++)
            {
                var rest = list.Take(i).Concat(list.Skip(i + 1)).ToList();
                foreach (var p in Permutations(rest)) yield return new List<T> { list[i] }.Concat(p).ToList();
            }
        }

        /// <summary>The order of these sectors using the least range (then the shortest voyage), as SubmarineTracker picks it.</summary>
        public static List<SubSector> BestOrder(List<SubSector> sectors, SubSector? start, double speed)
        {
            if (sectors.Count <= 1) return sectors;
            List<SubSector>? best = null;
            int? bestRange = null;
            double bestHours = 0;
            foreach (var order in Permutations(sectors))
            {
                var range = RangeUsed(order, start);
                var hours = SubRoutes.Voyage(order.Cast<SubSector?>().ToList(), start, speed)?.Hours ?? 0;
                if (best == null || range < bestRange || (range == bestRange && hours < bestHours)) { best = order; bestRange = range; bestHours = hours; }
            }
            return best!;
        }

        /// <summary>index: 0 T2, 1 T3, 2 normal, 3 optimal, 4 favor.</summary>
        public static Breakpoint BreakpointOf(SubCalculatorData.Sector data, int index)
        {
            var a = data.Xivstats[index];
            var b = data.Pepper != null ? data.Pepper[index] : a;
            return new Breakpoint(Math.Min(a, b), Math.Max(a, b));
        }

        /// <summary>"yes" at or past both sources' figure, "maybe" between them, "no" below both.</summary>
        public static string Meets(double value, Breakpoint bp) => value >= bp.High ? "yes" : value >= bp.Low ? "maybe" : "no";

        /// <summary>What a build reaches on one sector. For the expected loot a "maybe" counts as not reached.</summary>
        public static SectorResult Result(SubCalculatorData.Sector data, SubStats stats)
        {
            var t2 = Meets(stats.Surveillance, BreakpointOf(data, 0));
            var t3 = Meets(stats.Surveillance, BreakpointOf(data, 1));
            var normal = Meets(stats.Retrieval, BreakpointOf(data, 2));
            var optimal = Meets(stats.Retrieval, BreakpointOf(data, 3));
            var favor = Meets(stats.Favor, BreakpointOf(data, 4));
            var tier = t3 != "no" ? 3 : t2 != "no" ? 2 : 1;
            var retrieval = optimal != "no" ? "optimal" : normal != "no" ? "normal" : "poor";
            double More(int index, double stat) => Math.Max(0, BreakpointOf(data, index).High - stat);
            return new SectorResult(
                tier, tier == 3 ? t3 : tier == 2 ? t2 : "yes",
                retrieval, retrieval == "optimal" ? optimal : retrieval == "normal" ? normal : "yes",
                favor,
                t3 == "yes" ? 3 : t2 == "yes" ? 2 : 1,
                optimal == "yes" ? "optimal" : normal == "yes" ? "normal" : "poor",
                new Need(More(0, stats.Surveillance), More(1, stats.Surveillance), More(2, stats.Retrieval), More(3, stats.Retrieval), More(4, stats.Favor)));
        }

        /// <summary>Bonus experience as Pepper's FFXIV Ship Guide gives it: optimal retrieval +25% (the only sure one), T2 +25%,
        /// T3 +50%, HQ +25% and a double dip +25% (luck), the second roll able to earn the tier's bonus again; at most +100%.</summary>
        public static Bonus BonusExp(SectorResult r)
        {
            static bool Reached(string s) => s == "yes" || s == "maybe";
            var tierBonus = r.Tier == 3 ? 50 : r.Tier == 2 ? 25 : 0;
            var optimal = r.Retrieval == "optimal";
            var best = (optimal ? 25 : 0) + tierBonus + 25 + (Reached(r.DoubleDip) ? 25 + tierBonus : 0);
            return new Bonus(optimal && r.RetrievalSure == "yes" ? 25 : 0, Math.Min(100, best));
        }

        private static readonly Dictionary<int, string> BandOfTier = new() { [1] = "Min", [2] = "Mid", [3] = "High" };

        private static long StatOf(SubCalculatorData.Pool p, string key) => p.Stats.TryGetValue(key, out var v) ? v : 0;

        /// <summary>What one voyage brings back from a sector on average, from XIVStats' records (as the app's sectorLoot).</summary>
        public static List<LootLine> SectorLoot(SubCalculatorData.Sector data, SectorResult result)
        {
            var band = BandOfTier[result.SureTier];
            var pools = data.Pools.OrderBy(p => p.Key).ToList();
            double inBand = pools.Sum(p => StatOf(p.Value, band));
            double favorCount = pools.Sum(p => StatOf(p.Value, "Favor"));
            double dips = pools.Sum(p => StatOf(p.Value, "DoubleDips"));
            var rolls = 1 + (result.DoubleDip == "yes" && favorCount > 0 ? dips / favorCount : 0);
            var outList = new List<LootLine>();
            foreach (var (tier, pool) in pools)
            {
                var tierChance = inBand > 0 ? StatOf(pool, band) / inBand : (tier == 1 ? 1 : 0);
                foreach (var item in pool.Loot)
                {
                    var (low, high) = result.SureRetrieval switch
                    {
                        "optimal" => (item.OptimalMin, item.OptimalMax),
                        "normal" => (item.NormalMin, item.NormalMax),
                        _ => (item.PoorMin, item.PoorMax),
                    };
                    var itemChance = pool.Records > 0 ? (double)item.Times / pool.Records : 0;
                    var chance = tierChance * itemChance * rolls;
                    outList.Add(new LootLine(item.ItemId, tier, chance, low, high, chance * (low + high) / 2));
                }
            }
            return outList;
        }

        /// <summary>Condition one sector takes off one part (out of 30,000), as SubmarineTracker predicts it.</summary>
        public static double SectorDamage(int sectorRankReq, int partRank) => Math.Max(0, (335 + sectorRankReq - partRank) * 7);

        /// <summary>The whole plan for a build on a route (as the app's plan()). sectors: id to sector (no starting points);
        /// partClasses: class per slot 0-3; price: gil an item; repairKitCost: gil per repair material.</summary>
        public static Plan? PlanFor(IReadOnlyList<int> route, SubStats? stats, IReadOnlyList<int> partClasses, IReadOnlyDictionary<int, SubSector> sectorsById,
            List<SubSector> startingPoints, IReadOnlyList<SubPartStat> parts, SubCalculatorData.Data data, Func<uint, double> price, double repairKitCost)
        {
            var sectors = route.Select(id => sectorsById.GetValueOrDefault(id)).Where(s => s != null).Select(s => s!).ToList();
            if (sectors.Count == 0 || stats == null) return null;
            var start = SubRoutes.StartFor(sectors[0].Id, startingPoints);
            var v = SubRoutes.Voyage(sectors.Cast<SubSector?>().ToList(), start, stats.Speed) ?? (0, 0);
            var range = RangeUsed(sectors, start);

            var perSector = sectors.Select(s =>
            {
                var d = data.Find(s.Id);
                if (d == null) return new SectorPlan(s, null, null, null, null);
                var result = Result(d, stats);
                return new SectorPlan(s, d, result, SectorLoot(d, result), BonusExp(result));
            }).ToList();

            // Loot added up over the route, item by item, in the order first met.
            var order = new List<uint>();
            // Miss: the chance of not getting it from any sector so far (each sector's chance taken as at most 1).
            var byItem = new Dictionary<uint, (int Tier, List<string> Sectors, double PerVoyage, int Low, int High, double Miss)>();
            foreach (var p in perSector)
            {
                foreach (var l in p.Loot ?? new List<LootLine>())
                {
                    if (!byItem.TryGetValue(l.ItemId, out var was)) { was = (l.Tier, new List<string>(), 0, l.Low, l.High, 1); order.Add(l.ItemId); }
                    if (l.PerVoyage > 0 && !was.Sectors.Contains(p.Sector.Location)) was.Sectors.Add(p.Sector.Location);
                    was = (Math.Min(was.Tier, l.Tier), was.Sectors, was.PerVoyage + l.PerVoyage, Math.Min(was.Low, l.Low), Math.Max(was.High, l.High), was.Miss * (1 - Math.Min(1, l.Chance)));
                    byItem[l.ItemId] = was;
                }
            }
            var loot = order.Select(id => { var x = byItem[id]; return new RouteLoot(id, x.Tier, x.Sectors, x.PerVoyage, x.Low, x.High, x.PerVoyage * price(id), 1 - x.Miss); })
                .OrderByDescending(l => l.Gil).ThenByDescending(l => l.PerVoyage).ToList();
            var gilPerVoyage = loot.Sum(l => l.Gil);

            // Wear: each part's condition lost, and the repair materials that comes to.
            var wear = partClasses.Select((cls, slot) =>
            {
                var p = parts.FirstOrDefault(x => x.Class == cls && x.Slot == slot);
                if (p == null) return null;
                var lost = sectors.Sum(s => SectorDamage(s.RankReq, p.PartRank));
                return new Wear(p.Slot, lost, lost > 0 ? (int)Math.Floor(FullCondition / lost) : null, p.RepairKits * lost / FullCondition);
            }).ToList();
            double? kitsPerVoyage = wear.All(w => w != null) ? wear.Sum(w => w!.KitsPerVoyage) : null;
            var perDay = v.Hours > 0 ? 24 / v.Hours : 0;
            double WithBonus(bool sure) => perSector.Sum(p => p.Sector.ExpReward * (1 + (p.Bonus == null ? 0 : sure ? p.Bonus.Sure : p.Bonus.Best) / 100.0));

            return new Plan(perSector, start, range, v.Hours, v.Exp, WithBonus(true), WithBonus(false), sectors.Sum(s => s.Tanks),
                loot, gilPerVoyage, gilPerVoyage * perDay, v.Exp * perDay, perDay, wear, kitsPerVoyage,
                kitsPerVoyage is double k ? k * repairKitCost * perDay : null, perSector.Where(p => p.Data == null).Select(p => p.Sector).ToList());
        }
    }
}
