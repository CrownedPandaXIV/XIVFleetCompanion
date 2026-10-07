using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>
    /// The tables from sql/008 (plugin 0.10.0): each PC's status, every voyage's loot (from
    /// SubmarineTracker), every venture's reward (from AutoRetainer's statistics) and the retainers'
    /// market listings (from AllaganTools). Until sql/008 has been run, each write answers false and
    /// changes nothing. No Dalamud here, so it is tested on its own (tests/FleetWriterTests).
    /// </summary>
    public static class DetailsWriter
    {
        // ---- PCs ----

        public sealed class PcStatus
        {
            public string PcName = "";
            public string AccountLabel = "";
            public string PluginVersion = "";
            public bool AutoRetainerReady;
            public bool AllaganToolsReady;
            public bool FcTrackerFound;
            public bool SubmarineTrackerFound;
            public bool VentureStatsFound;
            public int CharactersSynced;
            public int CharactersLeftOut;
            // Plain sentences: what the plugin could not see this sync (a retainer to open, ...).
            public List<string> Problems = new();
        }

        // One row per PC and account label, rewritten every sync.
        public static async Task<bool> WritePcStatusAsync(NpgsqlConnection conn, PcStatus s)
        {
            return await IfTableExists(async () =>
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO companion_pc_status (pc_name, account_label, plugin_version, last_sync_at,
                        autoretainer_ready, allagantools_ready, fctracker_found, submarinetracker_found, venture_stats_found,
                        characters_synced, characters_left_out, problems)
                    VALUES (@pc, @account, @version, now(), @ar, @at, @fct, @st, @vs, @synced, @left, @problems)
                    ON CONFLICT (pc_name, account_label) DO UPDATE SET
                        plugin_version = EXCLUDED.plugin_version, last_sync_at = EXCLUDED.last_sync_at,
                        autoretainer_ready = EXCLUDED.autoretainer_ready, allagantools_ready = EXCLUDED.allagantools_ready,
                        fctracker_found = EXCLUDED.fctracker_found, submarinetracker_found = EXCLUDED.submarinetracker_found,
                        venture_stats_found = EXCLUDED.venture_stats_found, characters_synced = EXCLUDED.characters_synced,
                        characters_left_out = EXCLUDED.characters_left_out, problems = EXCLUDED.problems", conn);
                cmd.Parameters.AddWithValue("pc", s.PcName);
                cmd.Parameters.AddWithValue("account", s.AccountLabel);
                cmd.Parameters.AddWithValue("version", s.PluginVersion);
                cmd.Parameters.AddWithValue("ar", s.AutoRetainerReady);
                cmd.Parameters.AddWithValue("at", s.AllaganToolsReady);
                cmd.Parameters.AddWithValue("fct", s.FcTrackerFound);
                cmd.Parameters.AddWithValue("st", s.SubmarineTrackerFound);
                cmd.Parameters.AddWithValue("vs", s.VentureStatsFound);
                cmd.Parameters.AddWithValue("synced", s.CharactersSynced);
                cmd.Parameters.AddWithValue("left", s.CharactersLeftOut);
                cmd.Parameters.AddWithValue("problems", s.Problems.ToArray());
                await cmd.ExecuteNonQueryAsync();
            });
        }

        // The last "Check what I can see" result, so the app can show it.
        public static async Task<bool> WritePcCheckAsync(NpgsqlConnection conn, string pcName, string accountLabel, string pluginVersion, string text, int toFix)
        {
            return await IfTableExists(async () =>
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO companion_pc_status (pc_name, account_label, plugin_version, check_text, check_to_fix, check_at)
                    VALUES (@pc, @account, @version, @text, @fix, now())
                    ON CONFLICT (pc_name, account_label) DO UPDATE SET
                        plugin_version = EXCLUDED.plugin_version, check_text = EXCLUDED.check_text,
                        check_to_fix = EXCLUDED.check_to_fix, check_at = EXCLUDED.check_at", conn);
                cmd.Parameters.AddWithValue("pc", pcName);
                cmd.Parameters.AddWithValue("account", accountLabel);
                cmd.Parameters.AddWithValue("version", pluginVersion);
                cmd.Parameters.AddWithValue("text", text);
                cmd.Parameters.AddWithValue("fix", toFix);
                await cmd.ExecuteNonQueryAsync();
            });
        }

        // ---- Voyage loot (SubmarineTracker) ----

        // One sector of one voyage: what SubmarineTracker recorded when the sub came back.
        public sealed class LootRow
        {
            public ulong FcId;
            public uint Register;      // when the sub was registered: SubmarineTracker's id for it
            public uint Return;        // Unix seconds, when the loot was collected
            public uint Sector;
            public int Leg;            // 1 for the first sector visited, 2 for the next, ...
            public string? SubName;
            public uint PrimaryItem;
            public int PrimaryCount;
            public bool PrimaryHq;
            public uint AdditionalItem;
            public int AdditionalCount;
            public bool AdditionalHq;
            public bool Valid;
        }

        // The newest voyage already stored per FC (Unix seconds), so only newer ones are read. With sql/009,
        // only voyages whose sector order is stored count, so the first sync after it fills in the order of
        // those already stored.
        public static async Task<Dictionary<ulong, long>?> ReadNewestVoyagesAsync(NpgsqlConnection conn, IReadOnlyCollection<ulong> fcIds)
        {
            var newest = new Dictionary<ulong, long>();
            var ok = await IfTableExists(async () =>
            {
                var hasLeg = await HasLegAsync(conn);
                await using var cmd = new NpgsqlCommand($@"
                    SELECT fc_id, extract(epoch FROM max(returned_at))::bigint FROM companion_voyage_loot
                    WHERE fc_id = ANY(@ids::numeric[]){(hasLeg ? " AND leg IS NOT NULL" : "")} GROUP BY fc_id", conn);
                cmd.Parameters.AddWithValue("ids", fcIds.Select(f => (decimal)f).ToArray());
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) newest[(ulong)reader.GetDecimal(0)] = reader.GetInt64(1);
            });
            return ok ? newest : null;
        }

        // Whether sql/009 (the leg column: the order a voyage ran its sectors in) has been run.
        private static async Task<bool> HasLegAsync(NpgsqlConnection conn)
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT EXISTS (SELECT 1 FROM information_schema.columns
                               WHERE table_schema = current_schema() AND table_name = 'companion_voyage_loot' AND column_name = 'leg')", conn);
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }

        // Adds voyages; one already stored (same FC, sub, return time and sector) is left as is, except that
        // its sector order is filled in when missing (sql/009; without it, voyages are stored without it).
        public static async Task<bool> WriteVoyageLootAsync(NpgsqlConnection conn, IReadOnlyList<LootRow> rows)
        {
            if (rows.Count == 0) return true;
            return await IfTableExists(async () =>
            {
                var hasLeg = await HasLegAsync(conn);
                await using var cmd = new NpgsqlCommand(hasLeg ? @"
                    INSERT INTO companion_voyage_loot (fc_id, sub_register, returned_at, sector, sub_name,
                        primary_item, primary_count, primary_hq, additional_item, additional_count, additional_hq, valid, leg)
                    SELECT * FROM unnest(@fc::numeric[], @reg::bigint[], @ret::timestamptz[], @sector::int[], @name::text[],
                        @pi::int[], @pc::int[], @phq::boolean[], @ai::int[], @ac::int[], @ahq::boolean[], @valid::boolean[], @leg::int[])
                    ON CONFLICT (fc_id, sub_register, returned_at, sector) DO UPDATE SET leg = EXCLUDED.leg
                    WHERE companion_voyage_loot.leg IS NULL" : @"
                    INSERT INTO companion_voyage_loot (fc_id, sub_register, returned_at, sector, sub_name,
                        primary_item, primary_count, primary_hq, additional_item, additional_count, additional_hq, valid)
                    SELECT * FROM unnest(@fc::numeric[], @reg::bigint[], @ret::timestamptz[], @sector::int[], @name::text[],
                        @pi::int[], @pc::int[], @phq::boolean[], @ai::int[], @ac::int[], @ahq::boolean[], @valid::boolean[])
                    ON CONFLICT (fc_id, sub_register, returned_at, sector) DO NOTHING", conn);
                cmd.Parameters.AddWithValue("fc", rows.Select(r => (decimal)r.FcId).ToArray());
                cmd.Parameters.AddWithValue("reg", rows.Select(r => (long)r.Register).ToArray());
                cmd.Parameters.AddWithValue("ret", rows.Select(r => DateTimeOffset.FromUnixTimeSeconds(r.Return).UtcDateTime).ToArray());
                cmd.Parameters.AddWithValue("sector", rows.Select(r => (int)r.Sector).ToArray());
                cmd.Parameters.AddWithValue("name", rows.Select(r => r.SubName).ToArray());
                cmd.Parameters.AddWithValue("pi", rows.Select(r => (int)r.PrimaryItem).ToArray());
                cmd.Parameters.AddWithValue("pc", rows.Select(r => r.PrimaryCount).ToArray());
                cmd.Parameters.AddWithValue("phq", rows.Select(r => r.PrimaryHq).ToArray());
                cmd.Parameters.AddWithValue("ai", rows.Select(r => (int)r.AdditionalItem).ToArray());
                cmd.Parameters.AddWithValue("ac", rows.Select(r => r.AdditionalCount).ToArray());
                cmd.Parameters.AddWithValue("ahq", rows.Select(r => r.AdditionalHq).ToArray());
                cmd.Parameters.AddWithValue("valid", rows.Select(r => r.Valid).ToArray());
                if (hasLeg) cmd.Parameters.AddWithValue("leg", rows.Select(r => r.Leg).ToArray());
                await cmd.ExecuteNonQueryAsync();
            });
        }

        // ---- Venture rewards (AutoRetainer's statistics) ----

        public sealed class VentureRow
        {
            public ulong OwnerCid;
            public string RetainerName = "";
            public long At;            // Unix seconds
            public uint ItemId;
            public bool Hq;
            public uint Quantity;
            public uint VentureId;
        }

        // The newest reward already stored per character and retainer name (Unix seconds).
        public static async Task<Dictionary<(ulong Owner, string Retainer), long>?> ReadNewestVenturesAsync(NpgsqlConnection conn, IReadOnlyCollection<ulong> cids)
        {
            var newest = new Dictionary<(ulong, string), long>();
            var ok = await IfTableExists(async () =>
            {
                await using var cmd = new NpgsqlCommand(@"
                    SELECT owner_cid, retainer_name, extract(epoch FROM max(received_at))::bigint FROM companion_venture_result
                    WHERE owner_cid = ANY(@ids::numeric[]) GROUP BY owner_cid, retainer_name", conn);
                cmd.Parameters.AddWithValue("ids", cids.Select(c => (decimal)c).ToArray());
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) newest[((ulong)reader.GetDecimal(0), reader.GetString(1))] = reader.GetInt64(2);
            });
            return ok ? newest : null;
        }

        public static async Task<bool> WriteVenturesAsync(NpgsqlConnection conn, IReadOnlyList<VentureRow> rows)
        {
            if (rows.Count == 0) return true;
            return await IfTableExists(async () =>
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO companion_venture_result (owner_cid, retainer_name, received_at, item_id, hq, quantity, venture_id)
                    SELECT * FROM unnest(@owner::numeric[], @name::text[], @at::timestamptz[], @item::int[], @hq::boolean[], @qty::int[], @venture::int[])
                    ON CONFLICT (owner_cid, retainer_name, received_at, item_id, hq) DO NOTHING", conn);
                cmd.Parameters.AddWithValue("owner", rows.Select(r => (decimal)r.OwnerCid).ToArray());
                cmd.Parameters.AddWithValue("name", rows.Select(r => r.RetainerName).ToArray());
                cmd.Parameters.AddWithValue("at", rows.Select(r => DateTimeOffset.FromUnixTimeSeconds(r.At).UtcDateTime).ToArray());
                cmd.Parameters.AddWithValue("item", rows.Select(r => (int)r.ItemId).ToArray());
                cmd.Parameters.AddWithValue("hq", rows.Select(r => r.Hq).ToArray());
                cmd.Parameters.AddWithValue("qty", rows.Select(r => (int)r.Quantity).ToArray());
                cmd.Parameters.AddWithValue("venture", rows.Select(r => (int)r.VentureId).ToArray());
                await cmd.ExecuteNonQueryAsync();
            });
        }

        // ---- Market listings (AllaganTools) ----

        public sealed class Listing
        {
            public ulong RetainerId;
            public int Slot;
            public uint ItemId;
            public uint Quantity;
            public bool Hq;
            public long UnitPrice;
            // When this listing was first seen (null: new now).
            public DateTime? FirstSeenAt;
        }

        // A listing still up keeps the time it was first seen: matched by retainer, item, quantity and HQ,
        // not by slot (the list closes up when something sells). A changed price keeps it too, so a
        // listing the player only repriced still counts as unsold since it was first seen.
        public static List<Listing> CarryFirstSeen(IReadOnlyList<Listing> stored, IReadOnlyList<Listing> now)
        {
            var pool = stored.Where(s => s.FirstSeenAt != null)
                .GroupBy(s => (s.RetainerId, s.ItemId, s.Quantity, s.Hq))
                .ToDictionary(g => g.Key, g => new Queue<DateTime>(g.Select(s => s.FirstSeenAt!.Value).OrderBy(t => t)));
            var result = new List<Listing>();
            foreach (var l in now.OrderBy(l => l.RetainerId).ThenBy(l => l.Slot))
            {
                DateTime? first = pool.TryGetValue((l.RetainerId, l.ItemId, l.Quantity, l.Hq), out var q) && q.Count > 0 ? q.Dequeue() : null;
                result.Add(new Listing { RetainerId = l.RetainerId, Slot = l.Slot, ItemId = l.ItemId, Quantity = l.Quantity, Hq = l.Hq, UnitPrice = l.UnitPrice, FirstSeenAt = first });
            }
            return result;
        }

        // Replaces the listings of the retainers AllaganTools saw this sync (an unseen retainer keeps its
        // stored listings, as with its items).
        public static async Task<bool> WriteListingsAsync(NpgsqlConnection conn, ulong ownerCid, IReadOnlyCollection<ulong> seenRetainers, IReadOnlyList<Listing> listings)
        {
            if (seenRetainers.Count == 0) return true;
            return await IfTableExists(async () =>
            {
                var ids = seenRetainers.Select(r => (decimal)r).ToArray();
                var stored = new List<Listing>();
                await using (var read = new NpgsqlCommand(
                    "SELECT retainer_id, slot, item_id, quantity, hq, unit_price, first_seen_at FROM companion_market_listing WHERE retainer_id = ANY(@ids::numeric[])", conn))
                {
                    read.Parameters.AddWithValue("ids", ids);
                    await using var reader = await read.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        stored.Add(new Listing
                        {
                            RetainerId = (ulong)reader.GetDecimal(0), Slot = reader.GetInt32(1), ItemId = (uint)reader.GetInt32(2),
                            Quantity = (uint)reader.GetInt32(3), Hq = reader.GetBoolean(4), UnitPrice = reader.GetInt64(5),
                            FirstSeenAt = reader.GetFieldValue<DateTime>(6),
                        });
                }
                var rows = CarryFirstSeen(stored, listings.Where(l => seenRetainers.Contains(l.RetainerId)).ToList());

                await using var tx = await conn.BeginTransactionAsync();
                await using (var delete = new NpgsqlCommand("DELETE FROM companion_market_listing WHERE retainer_id = ANY(@ids::numeric[])", conn, tx))
                {
                    delete.Parameters.AddWithValue("ids", ids);
                    await delete.ExecuteNonQueryAsync();
                }
                if (rows.Count > 0)
                {
                    await using var insert = new NpgsqlCommand(@"
                        INSERT INTO companion_market_listing (retainer_id, owner_cid, slot, item_id, quantity, hq, unit_price, first_seen_at, updated_at)
                        SELECT r, @owner, s, i, q, h, p, coalesce(f, now()), now()
                        FROM unnest(@r::numeric[], @s::int[], @i::int[], @q::int[], @h::boolean[], @p::bigint[], @f::timestamptz[]) AS t(r, s, i, q, h, p, f)", conn, tx);
                    insert.Parameters.AddWithValue("owner", (decimal)ownerCid);
                    insert.Parameters.AddWithValue("r", rows.Select(l => (decimal)l.RetainerId).ToArray());
                    insert.Parameters.AddWithValue("s", rows.Select(l => l.Slot).ToArray());
                    insert.Parameters.AddWithValue("i", rows.Select(l => (int)l.ItemId).ToArray());
                    insert.Parameters.AddWithValue("q", rows.Select(l => (int)l.Quantity).ToArray());
                    insert.Parameters.AddWithValue("h", rows.Select(l => l.Hq).ToArray());
                    insert.Parameters.AddWithValue("p", rows.Select(l => l.UnitPrice).ToArray());
                    insert.Parameters.AddWithValue("f", rows.Select(l => l.FirstSeenAt is DateTime t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : (DateTime?)null).ToArray());
                    await insert.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            });
        }

        public static string Fingerprint(IEnumerable<Listing> listings)
            => string.Join(";", listings.OrderBy(l => l.RetainerId).ThenBy(l => l.Slot)
                .Select(l => $"{l.RetainerId}|{l.Slot}|{l.ItemId}|{l.Quantity}|{l.Hq}|{l.UnitPrice}"));

        // Runs a write; false (nothing changed) when its table is not there yet because sql/008 has not
        // been run.
        private static async Task<bool> IfTableExists(Func<Task> work)
        {
            try
            {
                await work();
                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable || ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                return false;
            }
        }
    }
}
