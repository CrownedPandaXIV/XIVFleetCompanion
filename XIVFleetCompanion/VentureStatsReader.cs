using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Reads the venture rewards AutoRetainer records (its "Record statistics" option, on by default):
    /// one file per retainer in AutoRetainer's config folder, named "{character id in hex}_{retainer}.statistic.json",
    /// with one record per reward. Only read, never written. No Dalamud here, so it is tested on its own.
    /// </summary>
    public static class VentureStatsReader
    {
        public const string Pattern = "*.statistic.json";

        // The character id and retainer name from a file name, or null when it is not one of these files.
        public static (ulong Cid, string Retainer)? ParseFileName(string fileName)
        {
            const string suffix = ".statistic.json";
            if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
            var stem = fileName[..^suffix.Length];
            var cut = stem.IndexOf('_');
            if (cut != 16) return null;
            if (!ulong.TryParse(stem[..cut], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cid)) return null;
            var retainer = stem[(cut + 1)..];
            return retainer.Length == 0 ? null : (cid, retainer);
        }

        // The rewards in one file newer than `since` (Unix seconds). Missing fields take AutoRetainer's
        // defaults (not HQ, amount 1, venture 0).
        public static List<DetailsWriter.VentureRow> ReadFile(string path, ulong cid, string retainerFromName, long since)
        {
            var rows = new List<DetailsWriter.VentureRow>();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var retainer = root.TryGetProperty("RetainerName", out var rn) && rn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rn.GetString())
                && !rn.GetString()!.StartsWith("Unnamed", StringComparison.Ordinal)
                ? rn.GetString()! : retainerFromName;
            if (!root.TryGetProperty("Records", out var records) || records.ValueKind != JsonValueKind.Array) return rows;
            foreach (var r in records.EnumerateArray())
            {
                var at = Number(r, "T", 0);
                if (at <= since) continue;
                var item = (uint)Number(r, "I", 0);
                if (item == 0) continue;
                rows.Add(new DetailsWriter.VentureRow
                {
                    OwnerCid = cid,
                    RetainerName = retainer,
                    At = at,
                    ItemId = item,
                    Hq = Number(r, "H", 0) != 0,
                    Quantity = (uint)Number(r, "A", 1),
                    VentureId = (uint)Number(r, "V", 0),
                });
            }
            return rows;
        }

        private static long Number(JsonElement e, string name, long fallback)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : fallback;
    }
}
