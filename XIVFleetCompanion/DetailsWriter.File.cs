using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    // The one-file database (Just this PC, SQLite) versions of DetailsWriter's writes and reads (see Db). The file has
    // no database logins, so no db_user; a setup's problems are kept one per line.
    public static partial class DetailsWriter
    {
        private const string FileUpsertPc = @"
            ON CONFLICT (pc_name, account_label) DO UPDATE SET ";

        private static Task FileWritePcStatus(DbConnection conn, PcStatus s) => Db.Exec(conn, @"
            INSERT INTO companion_pc_status (pc_name, account_label, plugin_version, last_sync_at,
                autoretainer_ready, allagantools_ready, fctracker_found, submarinetracker_found, venture_stats_found,
                characters_synced, characters_left_out, problems)
            VALUES (@pc, @account, @version, @now, @ar, @at, @fct, @st, @vs, @synced, @left, @problems)" + FileUpsertPc + @"
                plugin_version = excluded.plugin_version, last_sync_at = excluded.last_sync_at,
                autoretainer_ready = excluded.autoretainer_ready, allagantools_ready = excluded.allagantools_ready,
                fctracker_found = excluded.fctracker_found, submarinetracker_found = excluded.submarinetracker_found,
                venture_stats_found = excluded.venture_stats_found, characters_synced = excluded.characters_synced,
                characters_left_out = excluded.characters_left_out, problems = excluded.problems", null,
            ("pc", s.PcName), ("account", s.AccountLabel), ("version", s.PluginVersion), ("now", Db.Now()),
            ("ar", s.AutoRetainerReady), ("at", s.AllaganToolsReady), ("fct", s.FcTrackerFound), ("st", s.SubmarineTrackerFound),
            ("vs", s.VentureStatsFound), ("synced", s.CharactersSynced), ("left", s.CharactersLeftOut), ("problems", Lines(s.Problems)));

        private static Task FileWritePcRead(DbConnection conn, string pcName, string accountLabel, string pluginVersion) => Db.Exec(conn, @"
            INSERT INTO companion_pc_status (pc_name, account_label, plugin_version, last_read_at) VALUES (@pc, @account, @version, @now)" + FileUpsertPc + @"
                plugin_version = excluded.plugin_version, last_read_at = excluded.last_read_at", null,
            ("pc", pcName), ("account", accountLabel), ("version", pluginVersion), ("now", Db.Now()));

        private static Task FileWriteAutoRetainerNotRunning(DbConnection conn, string pcName, string accountLabel, string pluginVersion) => Db.Exec(conn, @"
            INSERT INTO companion_pc_status (pc_name, account_label, plugin_version, autoretainer_ready, problems)
            VALUES (@pc, @account, @version, 0, @problems)" + FileUpsertPc + @"
                plugin_version = excluded.plugin_version, autoretainer_ready = 0, problems = excluded.problems", null,
            ("pc", pcName), ("account", accountLabel), ("version", pluginVersion), ("problems", AutoRetainerNotRunning));

        private static Task FileWritePcCheck(DbConnection conn, string pcName, string accountLabel, string pluginVersion, string text, int toFix) => Db.Exec(conn, @"
            INSERT INTO companion_pc_status (pc_name, account_label, plugin_version, check_text, check_to_fix, check_at)
            VALUES (@pc, @account, @version, @text, @fix, @now)" + FileUpsertPc + @"
                plugin_version = excluded.plugin_version, check_text = excluded.check_text,
                check_to_fix = excluded.check_to_fix, check_at = excluded.check_at", null,
            ("pc", pcName), ("account", accountLabel), ("version", pluginVersion), ("text", text), ("fix", toFix), ("now", Db.Now()));

        private static string? Lines(IReadOnlyCollection<string> lines) => string.Join("\n", lines);

        private static async Task<Dictionary<ulong, long>> FileReadNewestVoyages(DbConnection conn, IReadOnlyCollection<ulong> fcIds)
        {
            var newest = new Dictionary<ulong, long>();
            await using var cmd = Db.Command(conn, "");
            cmd.CommandText = $@"SELECT fc_id, CAST(max(returned_at) AS INTEGER) FROM companion_voyage_loot
                                 WHERE fc_id IN {Db.In(cmd, "id", fcIds.Select(Db.FcText))} AND leg IS NOT NULL GROUP BY fc_id";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) newest[Db.Id(reader, 0)] = reader.GetInt64(1);
            return newest;
        }

        private static async Task FileWriteVoyageLoot(DbConnection conn, IReadOnlyList<LootRow> rows)
        {
            await using var tx = await conn.BeginTransactionAsync();
            foreach (var r in rows)
                await Db.Exec(conn, @"
                    INSERT INTO companion_voyage_loot (fc_id, sub_register, returned_at, sector, sub_name,
                        primary_item, primary_count, primary_hq, additional_item, additional_count, additional_hq, valid, leg)
                    VALUES (@fc, @reg, @ret, @sector, @name, @pi, @pc, @phq, @ai, @ac, @ahq, @valid, @leg)
                    ON CONFLICT (fc_id, sub_register, returned_at, sector) DO UPDATE SET leg = excluded.leg
                    WHERE companion_voyage_loot.leg IS NULL", tx,
                    ("fc", Db.FcText(r.FcId)), ("reg", (long)r.Register), ("ret", (double)r.Return), ("sector", (long)r.Sector), ("name", r.SubName),
                    ("pi", (long)r.PrimaryItem), ("pc", r.PrimaryCount), ("phq", r.PrimaryHq), ("ai", (long)r.AdditionalItem), ("ac", r.AdditionalCount),
                    ("ahq", r.AdditionalHq), ("valid", r.Valid), ("leg", r.Leg));
            await tx.CommitAsync();
        }

        private static async Task<Dictionary<(ulong Owner, string Retainer), long>> FileReadNewestVentures(DbConnection conn, IReadOnlyCollection<ulong> cids)
        {
            var newest = new Dictionary<(ulong, string), long>();
            await using var cmd = Db.Command(conn, "");
            cmd.CommandText = $@"SELECT owner_cid, retainer_name, CAST(max(received_at) AS INTEGER) FROM companion_venture_result
                                 WHERE owner_cid IN {Db.In(cmd, "id", cids)} GROUP BY owner_cid, retainer_name";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) newest[(Db.Id(reader, 0), reader.GetString(1))] = reader.GetInt64(2);
            return newest;
        }

        private static async Task FileWriteVentures(DbConnection conn, IReadOnlyList<VentureRow> rows)
        {
            await using var tx = await conn.BeginTransactionAsync();
            foreach (var r in rows)
                await Db.Exec(conn, @"
                    INSERT INTO companion_venture_result (owner_cid, retainer_name, received_at, item_id, hq, quantity, venture_id)
                    VALUES (@owner, @name, @at, @item, @hq, @qty, @venture)
                    ON CONFLICT (owner_cid, retainer_name, received_at, item_id, hq) DO NOTHING", tx,
                    ("owner", r.OwnerCid), ("name", r.RetainerName), ("at", (double)r.At), ("item", (long)r.ItemId), ("hq", r.Hq),
                    ("qty", (long)r.Quantity), ("venture", (long)r.VentureId));
            await tx.CommitAsync();
        }

        private static async Task FileWriteListings(DbConnection conn, ulong ownerCid, IReadOnlyCollection<ulong> seenRetainers, IReadOnlyList<Listing> listings)
        {
            var stored = new List<Listing>();
            await using (var read = Db.Command(conn, ""))
            {
                read.CommandText = $"SELECT retainer_id, slot, item_id, quantity, hq, unit_price, first_seen_at FROM companion_market_listing WHERE retainer_id IN {Db.In(read, "id", seenRetainers)}";
                await using var reader = await read.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    stored.Add(new Listing
                    {
                        RetainerId = Db.Id(reader, 0), Slot = (int)reader.GetInt64(1), ItemId = (uint)reader.GetInt64(2),
                        Quantity = (uint)reader.GetInt64(3), Hq = reader.GetInt64(4) != 0, UnitPrice = reader.GetInt64(5),
                        FirstSeenAt = Db.Time(reader, 6),
                    });
            }
            var rows = CarryFirstSeen(stored, listings.Where(l => seenRetainers.Contains(l.RetainerId)).ToList());
            var now = Db.Now();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var delete = Db.Command(conn, "", tx))
            {
                delete.CommandText = $"DELETE FROM companion_market_listing WHERE retainer_id IN {Db.In(delete, "id", seenRetainers)}";
                await delete.ExecuteNonQueryAsync();
            }
            foreach (var l in rows)
                await Db.Exec(conn, @"
                    INSERT INTO companion_market_listing (retainer_id, owner_cid, slot, item_id, quantity, hq, unit_price, first_seen_at, updated_at)
                    VALUES (@r, @owner, @s, @i, @q, @h, @p, @f, @now)", tx,
                    ("r", l.RetainerId), ("owner", ownerCid), ("s", l.Slot), ("i", (long)l.ItemId), ("q", (long)l.Quantity), ("h", l.Hq),
                    ("p", l.UnitPrice), ("f", l.FirstSeenAt is DateTime t ? Db.Unix(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : now), ("now", now));
            await tx.CommitAsync();
        }
    }
}
