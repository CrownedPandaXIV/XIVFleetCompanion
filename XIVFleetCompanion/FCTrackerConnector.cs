using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XIVFleetCompanion
{
    public static class FCTrackerConnector
    {
        public class HousingInfo
        {
            public ulong FcId { get; set; }
            public string FcName { get; set; } = string.Empty;
            public int FcPoints { get; set; }
            public int FcRank { get; set; }
            public int TotalMembers { get; set; }
            public bool HasHouse { get; set; }
            public int? HouseCity { get; set; }
            public int? HouseWard { get; set; }
            public int? HousePlot { get; set; }
            public DateTime? HouseLastVisited { get; set; }
            public string FcMaster { get; set; } = string.Empty;
            public int? FcHomeWorldId { get; set; }
            public DateTime? FcFoundingDate { get; set; }
            public DateTime? FcEligibilityOverride { get; set; }
        }

        private class RootData
        {
            [JsonPropertyName("GatheredData")]
            public GatheredData? GatheredData { get; set; }
        }

        private class GatheredData
        {
            [JsonPropertyName("CharByCID")]
            public Dictionary<string, CharEntry>? CharByCID { get; set; }

            [JsonPropertyName("FCData")]
            public Dictionary<string, FcEntry>? FCData { get; set; }
        }

        // FCTracker's own shapes, only the parts used here. The same names serve its config file and its
        // IPC (Dalamud hands another plugin's objects over by matching property names).
        public class CharEntry
        {
            [JsonPropertyName("CID")]
            public ulong CID { get; set; }

            [JsonPropertyName("FC")]
            public ulong? FC { get; set; }
        }

        public class FcEntry
        {
            [JsonPropertyName("FCName")]
            public string FCName { get; set; } = string.Empty;

            [JsonPropertyName("FCPoints")]
            public int FCPoints { get; set; }

            [JsonPropertyName("Rank")]
            public int Rank { get; set; }

            [JsonPropertyName("TotalMembers")]
            public int TotalMembers { get; set; }

            [JsonPropertyName("MasterString")]
            public string MasterString { get; set; } = string.Empty;

            [JsonPropertyName("HomeWorldId")]
            public int HomeWorldId { get; set; }

            [JsonPropertyName("FoundingDate")]
            public DateTime FoundingDate { get; set; }

            [JsonPropertyName("EligibilityDateReferenceOverride")]
            public DateTime? EligibilityDateReferenceOverride { get; set; }

            [JsonPropertyName("House")]
            public HouseEntry? House { get; set; }
        }

        public class HouseEntry
        {
            [JsonPropertyName("City")]
            public int City { get; set; }

            [JsonPropertyName("Ward")]
            public int Ward { get; set; }

            [JsonPropertyName("Plot")]
            public int Plot { get; set; }

            [JsonPropertyName("LastVisited")]
            public DateTime? LastVisited { get; set; }
        }

        /// <summary>
        /// Reads FCTracker's config JSON from the given path and returns a lookup of
        /// CID -> housing info, resolved through each character's FC entry.
        /// When <paramref name="withoutFc"/> is given, it receives the characters FCTracker
        /// knows to be in no Free Company (a character it has no entry for is left out: unknown).
        /// Never throws — returns an empty dictionary on any failure.
        /// </summary>
        public static Dictionary<ulong, HousingInfo> ReadHousingData(string path, HashSet<ulong>? withoutFc = null)
        {
            var result = new Dictionary<ulong, HousingInfo>();

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return result;

            try
            {
                var json = File.ReadAllText(path);
                var root = JsonSerializer.Deserialize<RootData>(json);

                var charByCid = root?.GatheredData?.CharByCID;
                var fcData = root?.GatheredData?.FCData;
                if (charByCid == null || fcData == null)
                    return result;

                foreach (var kvp in charByCid)
                {
                    var character = kvp.Value;
                    if (character.CID == 0)
                        continue;
                    if (character.FC == null || character.FC == 0)
                    {
                        withoutFc?.Add(character.CID);
                        continue;
                    }

                    var fcKey = character.FC.Value.ToString();
                    if (!fcData.TryGetValue(fcKey, out var fc))
                        continue;

                    result[character.CID] = ToHousingInfo(character.FC.Value, fc);
                }
            }
            catch
            {
                // Missing, corrupted, or unreadable file — caller treats this as "no data available".
            }

            return result;
        }

        /// <summary>
        /// <see cref="ReadHousingLive"/> with FCTracker's newer calls that hand over every character and every Free
        /// Company at once (<paramref name="getAllChars"/>, <paramref name="getAllFcs"/>): two calls per sync instead
        /// of one per character and Free Company. When either fails (an FCTracker without them), the one-at-a-time
        /// calls are used. Exceptions from those are left to the caller, as in <see cref="ReadHousingLive"/>.
        /// </summary>
        public static Dictionary<ulong, HousingInfo> ReadHousingLiveAll(IEnumerable<ulong> cids,
            Func<Dictionary<ulong, CharEntry>?> getAllChars, Func<Dictionary<ulong, FcEntry>?> getAllFcs,
            Func<ulong, CharEntry?> getChar, Func<ulong, FcEntry?> getFc, HashSet<ulong>? withoutFc = null)
        {
            Dictionary<ulong, CharEntry>? chars = null;
            Dictionary<ulong, FcEntry>? fcs = null;
            try
            {
                chars = getAllChars();
                fcs = getAllFcs();
            }
            catch
            {
                chars = null;
            }
            if (chars == null || fcs == null)
                return ReadHousingLive(cids, getChar, getFc, withoutFc);
            return ReadHousingLive(cids, cid => chars.GetValueOrDefault(cid), id => fcs.GetValueOrDefault(id), withoutFc);
        }

        /// <summary>
        /// The same lookup as <see cref="ReadHousingData"/>, asked of a running FCTracker (its IPC) instead of read
        /// from its file: <paramref name="getChar"/> and <paramref name="getFc"/> return its entry for a character or
        /// Free Company id, or null when it has none. Each Free Company is asked for once. Exceptions are left to the
        /// caller (FCTracker not offering the calls, or a call failing), and then nothing is added to
        /// <paramref name="withoutFc"/>.
        /// </summary>
        public static Dictionary<ulong, HousingInfo> ReadHousingLive(IEnumerable<ulong> cids, Func<ulong, CharEntry?> getChar,
            Func<ulong, FcEntry?> getFc, HashSet<ulong>? withoutFc = null)
        {
            var result = new Dictionary<ulong, HousingInfo>();
            var none = new HashSet<ulong>();
            var fcs = new Dictionary<ulong, FcEntry?>();
            foreach (var cid in cids)
            {
                if (cid == 0 || result.ContainsKey(cid)) continue;
                var character = getChar(cid);
                if (character == null) continue;
                if (character.FC == null || character.FC == 0)
                {
                    none.Add(cid);
                    continue;
                }
                var fcId = character.FC.Value;
                if (!fcs.TryGetValue(fcId, out var fc))
                    fcs[fcId] = fc = getFc(fcId);
                if (fc != null)
                    result[cid] = ToHousingInfo(fcId, fc);
            }
            withoutFc?.UnionWith(none);
            return result;
        }

        private static HousingInfo ToHousingInfo(ulong fcId, FcEntry fc) => new()
        {
            FcId = fcId,
            FcName = fc.FCName,
            FcPoints = fc.FCPoints,
            FcRank = fc.Rank,
            TotalMembers = fc.TotalMembers,
            HasHouse = fc.House != null,
            HouseCity = fc.House?.City,
            // FCTracker keeps the game's 0-based numbers (Ward 7 Plot 28 is kept as 6 and 27) and adds 1 only
            // when it shows them; the real address is stored here.
            HouseWard = fc.House?.Ward + 1,
            HousePlot = fc.House?.Plot + 1,
            HouseLastVisited = fc.House?.LastVisited,
            FcMaster = fc.MasterString,
            FcHomeWorldId = fc.HomeWorldId,
            FcFoundingDate = fc.FoundingDate == default ? null : fc.FoundingDate,
            FcEligibilityOverride = fc.EligibilityDateReferenceOverride,
        };
    }
}
