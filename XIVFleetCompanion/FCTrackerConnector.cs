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

        private class CharEntry
        {
            [JsonPropertyName("CID")]
            public ulong CID { get; set; }

            [JsonPropertyName("FC")]
            public ulong FC { get; set; }
        }

        private class FcEntry
        {
            [JsonPropertyName("FCName")]
            public string FCName { get; set; } = string.Empty;

            [JsonPropertyName("FCPoints")]
            public int FCPoints { get; set; }

            [JsonPropertyName("Rank")]
            public int Rank { get; set; }

            [JsonPropertyName("TotalMembers")]
            public int TotalMembers { get; set; }

            [JsonPropertyName("House")]
            public HouseEntry? House { get; set; }
        }

        private class HouseEntry
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
        /// Never throws — returns an empty dictionary on any failure.
        /// </summary>
        public static Dictionary<ulong, HousingInfo> ReadHousingData(string path)
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
                    if (character.CID == 0 || character.FC == 0)
                        continue;

                    var fcKey = character.FC.ToString();
                    if (!fcData.TryGetValue(fcKey, out var fc))
                        continue;

                    result[character.CID] = new HousingInfo
                    {
                        FcId = character.FC,
                        FcName = fc.FCName,
                        FcPoints = fc.FCPoints,
                        FcRank = fc.Rank,
                        TotalMembers = fc.TotalMembers,
                        HasHouse = fc.House != null,
                        HouseCity = fc.House?.City,
                        HouseWard = fc.House?.Ward,
                        HousePlot = fc.House?.Plot,
                        HouseLastVisited = fc.House?.LastVisited
                    };
                }
            }
            catch
            {
                // Missing, corrupted, or unreadable file — caller treats this as "no data available".
            }

            return result;
        }
    }
}
