using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XIVFleetCompanion
{
    /// <summary>
    /// The Sub Calculator's outside data, the same file the app ships (its subs/calculator-data.json, copied here by its
    /// tests/db/plugin-database-fixture.js and made again at each release): per sector, the loot each tier can give and
    /// the breakpoints, from XIVStats (MIT licence, collected from players running the Tracky plugin), with Pepper's FFXIV
    /// Ship Guide's breakpoints beside them. Sector names, ranks, positions and parts come from the game's own files.
    /// </summary>
    public static class SubCalculatorData
    {
        public const string ResourceName = "SubCalculatorData.json";

        /// <summary>Where the data comes from and when: XIVStats' last update, Pepper's sheet when it was read.</summary>
        public sealed record About(DateTime? XivstatsUpdated, string? XivstatsCommit, DateTime? PepperFetched);

        /// <summary>One item a tier can give: how often it came in XIVStats' records and how many in all, and how many
        /// each time at poor, normal and optimal retrieval.</summary>
        public sealed record Loot(uint ItemId, long Times, long Total, long WasT3, int PoorMin, int PoorMax, int NormalMin, int NormalMax, int OptimalMin, int OptimalMax);

        /// <summary>A loot tier: how many voyages rolled it in XIVStats' records, their counts, and its items.</summary>
        public sealed record Pool(long Records, Dictionary<string, long> Stats, List<Loot> Loot);

        /// <summary>A sector. Breakpoints are [T2, T3, normal, optimal, favor]: XIVStats' table, Pepper's sheet (null when it
        /// has no row), and the lowest stat XIVStats' records show getting each (null where none did).</summary>
        public sealed record Sector(int Id, string Name, string Letter, int Rank, int? UnlockedFrom, long Records,
            int[] Xivstats, int[]? Pepper, int?[]? Seen, Dictionary<int, Pool> Pools);

        public sealed record Data(About About, List<Sector> Sectors)
        {
            private Dictionary<int, Sector>? byId;
            public Sector? Find(int id) => (byId ??= Sectors.ToDictionary(s => s.Id)).GetValueOrDefault(id);
        }

        private static Data? shipped;

        /// <summary>The data shipped with the plugin (read once).</summary>
        public static Data Shipped()
        {
            if (shipped != null) return shipped;
            using var stream = typeof(SubCalculatorData).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("The Sub Calculator data is missing from the plugin.");
            using var reader = new StreamReader(stream);
            return shipped = Parse(reader.ReadToEnd());
        }

        public static Data Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            static DateTime? Date(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
            static JsonElement Prop(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
            static int[]? Ints(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetInt32()).ToArray() : null;

            var about = Prop(root, "about");
            var xivstats = Prop(about, "xivstats");
            var commit = Prop(xivstats, "commit");
            var data = new Data(
                new About(Date(xivstats, "updated"), commit.ValueKind == JsonValueKind.String ? commit.GetString() : null, Date(Prop(about, "pepper"), "fetched")),
                new List<Sector>());

            foreach (var s in root.GetProperty("sectors").EnumerateArray())
            {
                var pools = new Dictionary<int, Pool>();
                foreach (var p in s.GetProperty("pools").EnumerateObject())
                {
                    var stats = new Dictionary<string, long>();
                    if (p.Value.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object)
                        foreach (var kv in st.EnumerateObject()) if (kv.Value.ValueKind == JsonValueKind.Number) stats[kv.Name] = kv.Value.GetInt64();
                    var loot = p.Value.GetProperty("loot").EnumerateArray().Select(l =>
                    {
                        var v = l.EnumerateArray().Select(x => x.GetInt64()).ToArray();
                        return new Loot((uint)v[0], v[1], v[2], v[3], (int)v[4], (int)v[5], (int)v[6], (int)v[7], (int)v[8], (int)v[9]);
                    }).ToList();
                    pools[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = new Pool(p.Value.GetProperty("records").GetInt64(), stats, loot);
                }
                var unlocked = Prop(s, "unlockedFrom");
                var seen = Prop(s, "seen");
                data.Sectors.Add(new Sector(
                    s.GetProperty("id").GetInt32(), s.GetProperty("name").GetString() ?? "", s.GetProperty("letter").GetString() ?? "",
                    s.GetProperty("rank").GetInt32(), unlocked.ValueKind == JsonValueKind.Number ? unlocked.GetInt32() : null,
                    s.GetProperty("records").GetInt64(), Ints(s.GetProperty("xivstats")) ?? Array.Empty<int>(), Ints(Prop(s, "pepper")),
                    seen.ValueKind == JsonValueKind.Array ? seen.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? (int?)x.GetInt32() : null).ToArray() : null,
                    pools));
            }
            return data;
        }
    }
}
