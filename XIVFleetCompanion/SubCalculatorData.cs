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
    /// Each day the app's sub-data.yml makes it afresh and publishes it in the Releases repository when it changed and
    /// passes its checks; the plugin downloads that copy once a day (or when the game next opens after a day missed) and
    /// uses it when it is newer than the copy shipped (Current).
    /// </summary>
    public static class SubCalculatorData
    {
        public const string ResourceName = "SubCalculatorData.json";

        /// <summary>Where the data comes from and when: XIVStats' last update, Pepper's sheet when it was read.</summary>
        public sealed record About(DateTime? XivstatsUpdated, string? XivstatsCommit, DateTime? PepperFetched, DateTime? Made = null);

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

        /// <summary>The published copy (the format number in the name: a later plugin that needs another file asks for its own).</summary>
        public const string Url = "https://raw.githubusercontent.com/CrownedPandaXIV/XIVFleetCompanion-Releases/main/sub-data/calculator-data-v1.json";
        public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

        private static readonly object Gate = new();
        private static string? downloadedFile;
        private static Data? current;

        /// <summary>Where the downloaded copy is kept (the plugin's config folder); set once at start.</summary>
        public static void UseFolder(string dir)
        {
            lock (Gate) { downloadedFile = Path.Combine(dir, "SubCalculatorData.json"); current = null; }
        }

        /// <summary>The data to use: the downloaded copy when it is fine and newer than the shipped one, else the shipped one.</summary>
        public static Data Current()
        {
            lock (Gate)
            {
                if (current != null) return current;
                current = Shipped();
                try
                {
                    if (downloadedFile != null && File.Exists(downloadedFile) && Parse(File.ReadAllText(downloadedFile)) is var got && UsableOver(got, current))
                        current = got;
                }
                catch (Exception) { /* not readable: the shipped data */ }
                return current;
            }
        }

        /// <summary>What is wrong with the data, as the app's problems() checks it (none when it is fine to use).</summary>
        public static List<string> Problems(Data data)
        {
            var output = new List<string>();
            if (data.About.XivstatsUpdated == null) output.Add("It does not say when XIVStats was last updated.");
            if (data.Sectors.Count == 0) output.Add("It has no sectors.");
            var ids = new HashSet<int>();
            foreach (var s in data.Sectors)
            {
                if (s.Id <= 0 || !ids.Add(s.Id)) output.Add($"Sector {s.Id}: no id, or listed twice.");
                if (s.Xivstats.Length != 5 || s.Xivstats.Any(v => v < 0)) output.Add($"Sector {s.Id}: no XIVStats breakpoints.");
                if (s.Pepper != null && s.Pepper.Length != 5) output.Add($"Sector {s.Id}: Pepper's breakpoints are not five numbers.");
                if (!s.Pools.ContainsKey(1)) output.Add($"Sector {s.Id}: no Tier 1 loot.");
                if (s.Pools.Keys.Any(t => t is < 1 or > 3)) output.Add($"Sector {s.Id}: a loot tier other than 1 to 3.");
            }
            return output;
        }

        private static bool UsableOver(Data data, Data than) => Problems(data).Count == 0 && (data.About.Made ?? DateTime.MinValue) > (than.About.Made ?? DateTime.MinValue);

        /// <summary>A downloaded copy: kept and used from now on when it is fine and newer than the data in use. The file is
        /// written whole first, then swapped in, so a cut-off write never leaves half a file. Returns whether it was.</summary>
        public static bool Adopt(string json)
        {
            var data = Parse(json);
            lock (Gate)
            {
                if (downloadedFile == null || !UsableOver(data, Current())) return false;
                var temp = downloadedFile + ".new";
                File.WriteAllText(temp, json);
                File.Move(temp, downloadedFile, true);
                current = data;
                return true;
            }
        }

        /// <summary>Whether a check is due: none yet, a day or more since the last, or the last in the future (the clock changed).</summary>
        public static bool Due(DateTime? lastUtc, DateTime nowUtc) => lastUtc is not DateTime last || nowUtc - last >= CheckEvery || last > nowUtc;

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
                new About(Date(xivstats, "updated"), commit.ValueKind == JsonValueKind.String ? commit.GetString() : null, Date(Prop(about, "pepper"), "fetched"), Date(about, "made")),
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
