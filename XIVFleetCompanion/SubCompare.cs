using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>
    /// The Sub Calculator's Compare page, the same as the app's subs/compare.js (its tests/db/calculator-fixture.js writes
    /// its answers, which the plugin's tests check this against): up to three builds side by side, on one route for all
    /// of them or each on its own best route on a map (RouteFinder). This part has no game data or window: whether a
    /// build can fly a route, the numbers each row shows, and which build is best in each row.
    /// </summary>
    public static class SubCompare
    {
        public const int MaxBuilds = 3;

        /// <summary>The rows that are numbers, in page order, and whether higher is better.</summary>
        public static readonly (string Key, bool High)[] Rows =
        {
            ("surveillance", true), ("retrieval", true), ("speed", true), ("range", true), ("favor", true),
            ("spare", true), ("hours", false), ("expPerVoyage", true), ("expPerDay", true), ("gilPerVoyage", true), ("gilPerDay", true),
            ("kitsPerDay", false), ("netPerDay", true),
        };

        public sealed record Check(int? Used, int Short, List<int> TooHigh);

        /// <summary>Whether a build can fly a route as given (sectors in route order): the range it uses, the range missing
        /// (0: in range), and the sectors above the build's rank.</summary>
        public static Check RouteCheck(IReadOnlyList<SubSector> sectors, SubSector? start, SubStats? stats, int rank)
        {
            var used = sectors.Count > 0 && start != null ? SubCalculator.RangeUsed(sectors, start) : null;
            return new Check(used, used is int u && stats != null ? (int)Math.Max(0, u - stats.Range) : 0,
                sectors.Where(s => s.RankReq > rank).Select(s => s.Id).ToList());
        }

        /// <summary>One build's numbers for the rows: its stats always; the voyage's only when it can fly the route (in
        /// range and every sector within its rank). plan: null when there is no route.</summary>
        public static Dictionary<string, double?> RowValues(SubStats? stats, SubCalculator.Plan? plan, bool flies)
        {
            var ok = stats != null && plan != null && flies && plan.Hours > 0;
            var perDay = ok ? plan!.VoyagesPerDay : 0;
            return new Dictionary<string, double?>
            {
                ["surveillance"] = stats?.Surveillance, ["retrieval"] = stats?.Retrieval, ["speed"] = stats?.Speed, ["range"] = stats?.Range, ["favor"] = stats?.Favor,
                ["spare"] = ok && plan!.Range is int r ? stats!.Range - r : null,
                ["hours"] = ok ? plan!.Hours : null,
                ["expPerVoyage"] = ok ? plan!.ExpSure : null,
                ["expPerDay"] = ok ? plan!.ExpSure * perDay : null,
                ["gilPerVoyage"] = ok ? plan!.GilPerVoyage : null,
                ["gilPerDay"] = ok ? plan!.GilPerDay : null,
                ["kitsPerDay"] = ok && plan!.KitsPerVoyage is double k ? k * perDay : null,
                ["netPerDay"] = ok && plan!.RepairGilPerDay is double rg ? plan.GilPerDay - rg : null,
            };
        }

        /// <summary>Which builds are best in each row (key → indices). Only builds with a number count; a row is marked only
        /// when at least two have one and they differ. Ties are all marked; numbers are compared to a millionth.</summary>
        public static Dictionary<string, List<int>> BestMarks(IReadOnlyList<Dictionary<string, double?>> values)
        {
            var marks = new Dictionary<string, List<int>>();
            static double R6(double v) => Math.Round(v * 1e6, MidpointRounding.AwayFromZero) / 1e6;
            foreach (var (key, high) in Rows)
            {
                var have = values.Select((v, i) => (I: i, V: v.GetValueOrDefault(key))).Where(x => x.V is double d && double.IsFinite(d)).Select(x => (x.I, V: R6(x.V!.Value))).ToList();
                if (have.Count < 2) continue;
                var top = high ? have.Max(x => x.V) : have.Min(x => x.V);
                if (have.All(x => x.V == top)) continue;
                marks[key] = have.Where(x => x.V == top).Select(x => x.I).ToList();
            }
            return marks;
        }
    }
}
