using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Reads what SubmarineTracker recorded for each voyage (its submarine-sqlite.db, in its plugin
    /// config folder): one row per sector with the items found. Only read, never written; SubmarineTracker
    /// keeps the file open, which is fine for reading. No Dalamud here, so it is tested on its own.
    /// </summary>
    public static class SubmarineTrackerReader
    {
        public const string FileName = "submarine-sqlite.db";

        // SubmarineTracker stores Free Company ids MessagePack-encoded. Null when the bytes are not an
        // unsigned MessagePack integer.
        public static ulong? DecodeId(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            var tag = bytes[0];
            if (tag <= 0x7f) return bytes.Length == 1 ? tag : null;
            int size = tag switch { 0xcc => 1, 0xcd => 2, 0xce => 4, 0xcf => 8, _ => 0 };
            if (size == 0 || bytes.Length != size + 1) return null;
            ulong value = 0;
            for (var i = 1; i <= size; i++) value = (value << 8) | bytes[i];
            return value;
        }

        // Voyages that came back after `since` (Unix seconds) for each Free Company asked for (an FC
        // missing from `since` gets all of its voyages), with the sub's current name. SubmarineTracker adds
        // a voyage's sectors in the order they were visited, so that order is each sector's leg (1, 2, ...).
        public static List<DetailsWriter.LootRow> ReadLoot(string path, IReadOnlyDictionary<ulong, long> since, IReadOnlyCollection<ulong> fcIds)
        {
            var rows = new List<DetailsWriter.LootRow>();
            if (!File.Exists(path) || fcIds.Count == 0) return rows;
            var wanted = new HashSet<ulong>(fcIds);
            var oldest = long.MaxValue;
            foreach (var fc in wanted) oldest = Math.Min(oldest, since.TryGetValue(fc, out var s) ? s : 0);

            var builder = new SQLiteConnectionStringBuilder { DataSource = path, ReadOnly = true, Pooling = false, DefaultTimeout = 5 };
            using var conn = new SQLiteConnection(builder.ToString());
            conn.Open();

            var names = new Dictionary<(ulong, long), string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT FreeCompanyId, SubmarineId, Name FROM submarine";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (DecodeId(reader.GetFieldValue<byte[]>(0)) is ulong fc)
                        names[(fc, reader.GetInt64(1))] = reader.GetString(2);
                }
            }

            var legs = new Dictionary<(ulong, long, long), int>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT FreeCompanyId, SubmarineId, Return, Sector, PrimaryItem, PrimaryCount, PrimaryHQ,
                                           AdditionalItem, AdditionalCount, AdditionalHQ, Valid
                                    FROM loot WHERE Return > $since ORDER BY Return, rowid";
                cmd.Parameters.AddWithValue("$since", oldest);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (DecodeId(reader.GetFieldValue<byte[]>(0)) is not ulong fc || !wanted.Contains(fc)) continue;
                    var returned = reader.GetInt64(2);
                    if (since.TryGetValue(fc, out var after) && returned <= after) continue;
                    var register = reader.GetInt64(1);
                    var leg = legs.TryGetValue((fc, register, returned), out var before) ? before + 1 : 1;
                    legs[(fc, register, returned)] = leg;
                    rows.Add(new DetailsWriter.LootRow
                    {
                        FcId = fc,
                        Register = (uint)register,
                        Return = (uint)returned,
                        Sector = (uint)reader.GetInt64(3),
                        Leg = leg,
                        SubName = names.TryGetValue((fc, register), out var name) ? name : null,
                        PrimaryItem = (uint)reader.GetInt64(4),
                        PrimaryCount = (int)reader.GetInt64(5),
                        PrimaryHq = reader.GetBoolean(6),
                        AdditionalItem = (uint)reader.GetInt64(7),
                        AdditionalCount = (int)reader.GetInt64(8),
                        AdditionalHq = reader.GetBoolean(9),
                        Valid = reader.GetBoolean(10),
                    });
                }
            }
            return rows;
        }
    }
}
