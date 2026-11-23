using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>
    /// The Sub Calculator's Build Finder, the same as the app's subs/build-finder.js (its tests/db/calculator-fixture.js
    /// writes its answers, which the plugin's tests check this against): goals on chosen sectors in, the builds that
    /// reach them out, each at the lowest rank it does. Goals come down to surveillance, retrieval and favor, each a
    /// range where XIVStats and Pepper's guide disagree; a build past only the lower figure is listed as not sure.
    /// </summary>
    public static class BuildFinder
    {
        public static readonly string[] Letters = { "S", "U", "W", "C", "Y", "S+", "U+", "W+", "C+", "Y+" };

        /// <summary>What a sector is asked for: a loot tier (0, 2 or 3), optimal retrieval, a double dip, items.</summary>
        public sealed record Goal(int SectorId, int Tier, bool Optimal, bool DoubleDip, List<uint> Items);

        /// <summary>What the goals need of each stat (null: nothing asks), what asks for it, and sectors without data.</summary>
        public sealed record Needs(SubCalculator.Breakpoint? Surveillance, SubCalculator.Breakpoint? Retrieval, SubCalculator.Breakpoint? Favor,
            List<string> WhySurveillance, List<string> WhyRetrieval, List<string> WhyFavor, List<string> Missing);

        /// <summary>A build that reaches the goals: at Rank (Sure when past both figures there; SureRank when it is sure from),
        /// its stats and what it has to spare, the range it lacks and the rank it's in range from, the voyage, parts to get.</summary>
        public sealed record Row(string[] Parts, int Rank, bool Sure, int? SureRank, SubStats Stats, double? SpareSurveillance, double? SpareRetrieval,
            double? SpareFavor, int? RangeNeeded, double RangeShort, int? InRangeFrom, double? Hours, List<(string Letter, int Slot)>? MissingParts);

        /// <summary>The lowest loot tier dropping an item on a sector, or null.</summary>
        public static int? ItemTier(SubCalculatorData.Sector data, uint itemId)
        {
            foreach (var (tier, pool) in data.Pools.OrderBy(p => p.Key))
                if (pool.Loot.Any(l => l.ItemId == itemId)) return tier;
            return null;
        }

        public static Needs NeedsFor(IReadOnlyList<Goal> goals, Func<int, SubCalculatorData.Sector?> dataOf, Func<int, string> letterOf)
        {
            SubCalculator.Breakpoint? sv = null, rt = null, fv = null;
            List<string> whySv = new(), whyRt = new(), whyFv = new(), missing = new();
            static SubCalculator.Breakpoint Raise(SubCalculator.Breakpoint? was, SubCalculator.Breakpoint bp) =>
                was == null ? bp : new SubCalculator.Breakpoint(Math.Max(was.Low, bp.Low), Math.Max(was.High, bp.High));
            foreach (var g in goals)
            {
                var d = dataOf(g.SectorId);
                var letter = letterOf(g.SectorId);
                if (d == null) { if (g.Tier > 0 || g.Optimal || g.DoubleDip || g.Items.Count > 0) missing.Add(letter); continue; }
                var tier = g.Tier;
                foreach (var id in g.Items) tier = Math.Max(tier, ItemTier(d, id) ?? 0);
                if (tier >= 3) { sv = Raise(sv, SubCalculator.BreakpointOf(d, 1)); whySv.Add($"T3 on {letter}"); }
                else if (tier == 2) { sv = Raise(sv, SubCalculator.BreakpointOf(d, 0)); whySv.Add($"T2 on {letter}"); }
                if (g.Optimal) { rt = Raise(rt, SubCalculator.BreakpointOf(d, 3)); whyRt.Add($"optimal on {letter}"); }
                if (g.DoubleDip) { fv = Raise(fv, SubCalculator.BreakpointOf(d, 4)); whyFv.Add($"double dip on {letter}"); }
            }
            return new Needs(sv, rt, fv, whySv, whyRt, whyFv, missing);
        }

        /// <summary>Every build reaching the needs, each at the lowest rank from minRank it does and its parts can be fitted
        /// at (each part's PartRank; as the app's findBuilds).
        /// route: the sectors in the order using the least range; owned: "letter|slot" the player has (null: not marked).</summary>
        public static List<Row> FindBuilds(Needs need, Dictionary<int, SubRank> ranksById, Dictionary<(int, int), SubPartStat> partStats, int minRank, int maxRank,
            List<SubSector> route, SubSector? start, ISet<string>? owned, bool sureOnly)
        {
            var ranks = new List<(int Rank, SubRank R)>();
            for (var r = Math.Max(1, minRank); r <= maxRank; r++)
                if (ranksById.TryGetValue(r, out var s) && s.Surveillance != null && s.Retrieval != null && s.Speed != null && s.Range != null && s.Favor != null) ranks.Add((r, s));
            int? rangeNeeded = route.Count > 0 && start != null ? SubCalculator.RangeUsed(route, start) : null;
            bool Meets(SubStats st, bool high) =>
                (need.Surveillance == null || st.Surveillance >= (high ? need.Surveillance.High : need.Surveillance.Low))
                && (need.Retrieval == null || st.Retrieval >= (high ? need.Retrieval.High : need.Retrieval.Low))
                && (need.Favor == null || st.Favor >= (high ? need.Favor.High : need.Favor.Low));
            var rows = new List<Row>();
            foreach (var a in Letters) foreach (var b in Letters) foreach (var c in Letters) foreach (var d in Letters)
            {
                var parts = new[] { a, b, c, d };
                double sv = 0, rt = 0, sp = 0, rg = 0, fv = 0;
                var fitted = 0; // the rank its parts can be fitted from: a build is never put below it
                var known = true;
                for (var slot = 0; slot < 4; slot++)
                {
                    if (!partStats.TryGetValue((SubRoutes.PartClass[parts[slot]], slot), out var p) || p.Surveillance is not double psv || p.Retrieval is not double prt || p.Favor is not double pfv)
                    { known = false; break; }
                    sv += psv; rt += prt; sp += p.Speed; rg += p.Range; fv += pfv;
                    fitted = Math.Max(fitted, p.PartRank);
                }
                if (!known) continue;
                SubStats At(SubRank r) => new(r.Surveillance!.Value + sv, r.Retrieval!.Value + rt, r.Speed!.Value + sp, r.Range!.Value + rg, r.Favor!.Value + fv);
                (int Rank, SubStats Stats)? found = null;
                int? sureRank = null;
                foreach (var (r, s) in ranks)
                {
                    if (r < fitted) continue;
                    var st = At(s);
                    if (found == null && Meets(st, false)) found = (r, st);
                    if (Meets(st, true)) { sureRank = r; found ??= (r, st); break; }
                }
                if (found == null) continue;
                if (sureOnly)
                {
                    if (sureRank is not int sr) continue;
                    found = (sr, At(ranksById[sr]));
                }
                var (rank, stats) = found.Value;
                var rangeShort = rangeNeeded is int rn && stats.Range < rn ? rn - stats.Range : 0;
                int? inRangeFrom = rangeShort > 0 ? null : rank;
                if (rangeShort > 0)
                    foreach (var (r, s) in ranks)
                        if (r > rank && At(s).Range >= rangeNeeded) { inRangeFrom = r; break; }
                var v = route.Count > 0 && start != null ? SubRoutes.Voyage(route.Cast<SubSector?>().ToList(), start, stats.Speed) : null;
                rows.Add(new Row(parts, rank, sureRank != null && sureRank <= rank, sureRank, stats,
                    need.Surveillance != null ? stats.Surveillance - need.Surveillance.Low : null,
                    need.Retrieval != null ? stats.Retrieval - need.Retrieval.Low : null,
                    need.Favor != null ? stats.Favor - need.Favor.Low : null,
                    rangeNeeded, rangeShort, inRangeFrom, v?.Hours,
                    owned == null ? null : parts.Select((l, slot) => (l, slot)).Where(x => !owned.Contains($"{x.l}|{x.slot}")).ToList()));
            }
            return rows;
        }

        /// <summary>One slot's part filter: any part (null), one the player owns (Own), or these parts (Letters).</summary>
        public sealed record SlotFilter(bool Own, IReadOnlyList<string> Letters);

        /// <summary>The builds whose parts pass the part filter, slot by slot (as the app's filterByParts). slots: hull, stern,
        /// bow, bridge, each null for any; owned: "letter|slot" the player has.</summary>
        public static List<Row> FilterByParts(List<Row> rows, IReadOnlyList<SlotFilter?>? slots, ISet<string>? owned)
        {
            if (slots == null || slots.All(x => x == null)) return rows;
            return rows.Where(r => r.Parts.Select((letter, slot) => (letter, slot)).All(p =>
            {
                var f = p.slot < slots.Count ? slots[p.slot] : null;
                if (f == null) return true;
                if (f.Own) return owned != null && owned.Contains($"{p.letter}|{p.slot}");
                return f.Letters.Contains(p.letter);
            })).ToList();
        }

        public static readonly string[] Sorts = { "rank", "speed", "parts", "range" };

        private static int Cmp(double a, double b) => a.CompareTo(b);

        private static int Order(Row a, Row b, string by)
        {
            double H(Row r) => r.Hours ?? double.PositiveInfinity;
            int M(Row r) => r.MissingParts?.Count ?? 0;
            double Spare(Row r) => r.Stats.Range - (r.RangeNeeded ?? 0);
            int x;
            switch (by)
            {
                case "speed":
                    return (x = Cmp(H(a), H(b))) != 0 ? x : a.Rank.CompareTo(b.Rank);
                case "parts":
                    if ((x = M(a).CompareTo(M(b))) != 0) return x;
                    if ((x = a.Rank.CompareTo(b.Rank)) != 0) return x;
                    return Cmp(H(a), H(b));
                case "range":
                    return (x = Cmp(Spare(b), Spare(a))) != 0 ? x : a.Rank.CompareTo(b.Rank);
                default:
                    return (x = a.Rank.CompareTo(b.Rank)) != 0 ? x : Cmp(H(a), H(b));
            }
        }

        /// <summary>Sorted as the app's sortBuilds: in range first, then the chosen order, ties in build order.</summary>
        public static List<Row> SortBuilds(List<Row> rows, string by)
        {
            if (Array.IndexOf(Sorts, by) < 0) by = "rank";
            return rows.Select((r, i) => (r, i)).OrderBy(x => x, Comparer<(Row r, int i)>.Create((p, q) =>
            {
                var o = (p.r.RangeShort > 0).CompareTo(q.r.RangeShort > 0);
                if (o != 0) return o;
                o = Order(p.r, q.r, by);
                return o != 0 ? o : p.i.CompareTo(q.i);
            })).Select(x => x.r).ToList();
        }

        /// <summary>The parts a player has, as "letter|slot": fitted on subs or spare in a bag, a retainer or an FC chest
        /// (as the app's ownedParts). cid: only that character's (its subs, bags, retainers and Free Company chest).</summary>
        // A part class's letter (Syldra is Y, not S).
        private static readonly Dictionary<string, string> LetterOfClass = new() { ["Shark"] = "S", ["Unkiu"] = "U", ["Whale"] = "W", ["Coelacanth"] = "C", ["Syldra"] = "Y" };

        public static HashSet<string> Owned(PartsInputs inputs, long? cid)
        {
            var owned = new HashSet<string>();
            foreach (var s in inputs.Subs.Where(s => cid == null || s.Cid == cid))
            {
                var fitted = new[] { s.Part1, s.Part2, s.Part3, s.Part4 };
                for (var slot = 0; slot < 4; slot++)
                {
                    var l = PartsPlanner.PartLetter(fitted[slot]);
                    if (SubRoutes.PartClass.ContainsKey(l)) owned.Add($"{l}|{slot}");
                }
            }
            var fc = cid == null ? null : inputs.Characters.FirstOrDefault(c => c.Cid == cid)?.FcId;
            foreach (var sp in inputs.Spares)
            {
                if (sp.Quantity <= 0) continue;
                var mine = cid == null || (sp.Cid == cid) || (sp.FcId != null && fc != null && sp.FcId == fc);
                if (!mine || PartsPlanner.SparePart(sp.ItemId) is not { } p) continue;
                var letter = LetterOfClass[p.Class] + (p.Modified ? "+" : "");
                owned.Add($"{letter}|{Array.IndexOf(new[] { "Hull", "Stern", "Bow", "Bridge" }, p.Slot)}");
            }
            return owned;
        }
    }
}
