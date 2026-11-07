using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    // The one-file database (Just this PC, SQLite) versions of FleetWriter's writes and reads: the same rows, a row at a
    // time inside one transaction instead of PostgreSQL's arrays (see Db). The tests check both give the same tables.
    public static partial class FleetWriter
    {
        private static async Task<int> FileWriteCurrentCharacters(DbConnection conn, IReadOnlyList<CharacterSnapshot> rows)
        {
            var now = Db.Now();
            await using var tx = await conn.BeginTransactionAsync();
            var n = 0;
            foreach (var r in rows)
                n += await Db.Exec(conn, @"
                    INSERT INTO companion_character_current
                        (cid, name, world, account_label, gil, ceruleum, repair_kits, retainer_count, submarine_count, num_sub_slots, fc_id, last_synced_at)
                    VALUES (@cid, @name, @world, @account, @gil, @ceruleum, @kits, @retainers, @subs, @slots, @fc, @now)
                    ON CONFLICT (cid) DO UPDATE SET
                        name = excluded.name, world = excluded.world, account_label = excluded.account_label, gil = excluded.gil,
                        ceruleum = excluded.ceruleum, repair_kits = excluded.repair_kits, retainer_count = excluded.retainer_count,
                        submarine_count = excluded.submarine_count, num_sub_slots = excluded.num_sub_slots, fc_id = excluded.fc_id,
                        last_synced_at = excluded.last_synced_at", tx,
                    ("cid", r.Cid), ("name", r.Name), ("world", r.World), ("account", string.IsNullOrEmpty(r.AccountLabel) ? null : r.AccountLabel),
                    ("gil", r.Gil), ("ceruleum", r.Ceruleum), ("kits", r.RepairKits), ("retainers", r.RetainerCount), ("subs", r.SubmarineCount),
                    ("slots", r.NumSubSlots), ("fc", Db.FcText(r.FcId)), ("now", now));
            await tx.CommitAsync();
            return n;
        }

        private static async Task FileRecordLogins(DbConnection conn, string pcName, string accountLabel, IReadOnlyCollection<KeyValuePair<ulong, DateTime>> logins)
        {
            await using var tx = await conn.BeginTransactionAsync();
            foreach (var l in logins)
                await Db.Exec(conn, @"
                    INSERT INTO companion_character_login (cid, pc_name, account_label, logged_in_at) VALUES (@cid, @pc, @label, @at)
                    ON CONFLICT (cid) DO UPDATE SET
                        pc_name = excluded.pc_name, account_label = excluded.account_label, logged_in_at = excluded.logged_in_at
                    WHERE companion_character_login.logged_in_at < excluded.logged_in_at", tx,
                    ("cid", l.Key), ("pc", pcName), ("label", accountLabel), ("at", Db.Unix(DateTime.SpecifyKind(l.Value, DateTimeKind.Utc))));
            await tx.CommitAsync();
        }

        private static async Task<Dictionary<ulong, LoginOwner>> FileReadLoginOwners(DbConnection conn, IReadOnlyCollection<ulong> cids)
        {
            var owners = new Dictionary<ulong, LoginOwner>();
            await using var cmd = Db.Command(conn, "");
            cmd.CommandText = $"SELECT cid, pc_name, account_label FROM companion_character_login WHERE cid IN {Db.In(cmd, "cid", cids)}";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                owners[Db.Id(reader, 0)] = new LoginOwner(reader.GetString(1), reader.GetString(2));
            return owners;
        }

        private static async Task<int> FileReleaseLogins(DbConnection conn, string pcName, string accountLabel, IReadOnlyCollection<ulong> cids)
        {
            await using var cmd = Db.Command(conn, "");
            cmd.CommandText = $"DELETE FROM companion_character_login WHERE pc_name = @pc AND account_label = @label AND cid IN {Db.In(cmd, "cid", cids)}";
            Db.Add(cmd, "pc", pcName);
            Db.Add(cmd, "label", accountLabel);
            return await cmd.ExecuteNonQueryAsync();
        }

        private static async Task FileWriteInventory(DbConnection conn, ulong ownerCid, InventoryPlan plan)
        {
            var now = Db.Now();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var delete = Db.Command(conn, "", tx))
            {
                var read = Db.In(delete, "read", plan.Read);
                delete.CommandText = plan.Current == null
                    ? $"DELETE FROM companion_inventory_snapshot WHERE owner_cid = @owner AND retainer_id IN {read}"
                    : $"DELETE FROM companion_inventory_snapshot WHERE owner_cid = @owner AND (retainer_id IN {read} OR retainer_id NOT IN {Db.In(delete, "current", plan.Current)})";
                Db.Add(delete, "owner", ownerCid);
                await delete.ExecuteNonQueryAsync();
            }
            await using (var insert = Db.Command(conn, @"
                INSERT INTO companion_inventory_snapshot
                    (owner_cid, retainer_id, sorted_container, sorted_slot_index, item_id, quantity, gear_set_ids, updated_at)
                VALUES (@owner, @retainer, @container, @slot, @item, @qty, @gear, @now)", tx))
            {
                foreach (var i in plan.Items)
                {
                    insert.Parameters.Clear();
                    Db.Add(insert, "owner", ownerCid);
                    Db.Add(insert, "retainer", i.RetainerId);
                    Db.Add(insert, "container", (long)i.SortedContainer);
                    Db.Add(insert, "slot", i.SortedSlotIndex);
                    Db.Add(insert, "item", (long)i.ItemId);
                    Db.Add(insert, "qty", (long)i.Quantity);
                    Db.Add(insert, "gear", GearSetText(i.GearSetIds));
                    Db.Add(insert, "now", now);
                    await insert.ExecuteNonQueryAsync();
                }
            }
            await tx.CommitAsync();
        }

        private static async Task<Dictionary<uint, long>> FileReadItemTotals(DbConnection conn, ulong ownerCid, IReadOnlyCollection<uint> itemIds)
        {
            var totals = itemIds.ToDictionary(id => id, _ => 0L);
            await using var cmd = Db.Command(conn, "");
            cmd.CommandText = $@"SELECT item_id, sum(quantity) FROM companion_inventory_snapshot
                                 WHERE owner_cid = @owner AND item_id IN {Db.In(cmd, "item", itemIds)} GROUP BY item_id";
            Db.Add(cmd, "owner", ownerCid);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                totals[(uint)reader.GetInt64(0)] = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            return totals;
        }

        private static async Task FileWriteFcInventory(DbConnection conn, ulong fcId, IReadOnlyList<InventoryItem> items)
        {
            var now = Db.Now();
            await using var tx = await conn.BeginTransactionAsync();
            await Db.Exec(conn, "DELETE FROM companion_fc_inventory_snapshot WHERE fc_id = @fc", tx, ("fc", Db.FcText(fcId)));
            foreach (var i in items)
                await Db.Exec(conn, @"
                    INSERT INTO companion_fc_inventory_snapshot (fc_id, sorted_container, sorted_slot_index, item_id, quantity, updated_at)
                    VALUES (@fc, @container, @slot, @item, @qty, @now)", tx,
                    ("fc", Db.FcText(fcId)), ("container", (long)i.SortedContainer), ("slot", i.SortedSlotIndex), ("item", (long)i.ItemId),
                    ("qty", (long)i.Quantity), ("now", now));
            await tx.CommitAsync();
        }

        private static async Task FileWriteSubmarines(DbConnection conn, ulong cid, IReadOnlyList<SubmarineRecord> subs)
        {
            var now = Db.Now();
            await using var tx = await conn.BeginTransactionAsync();
            var oldNameBySlot = new Dictionary<int, string>();
            await using (var old = Db.Command(conn, "SELECT sub_name, slot FROM companion_submarine_snapshot WHERE cid = @cid", tx))
            {
                Db.Add(old, "cid", cid);
                await using var reader = await old.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(0);
                    var slot = reader.IsDBNull(1) ? SlotFromDefaultName(name) : (int)reader.GetInt64(1);
                    if (slot != null) oldNameBySlot[slot.Value] = name;
                }
            }
            await Db.Exec(conn, "DELETE FROM companion_submarine_snapshot WHERE cid = @cid", tx, ("cid", cid));
            foreach (var s in subs)
                await Db.Exec(conn, @"
                    INSERT INTO companion_submarine_snapshot
                        (cid, sub_name, level, part1, part2, part3, part4, points, return_time, current_exp, next_level_exp, slot, updated_at)
                    VALUES (@cid, @name, @level, @p1, @p2, @p3, @p4, @points, @ret, @exp, @next, @slot, @now)", tx,
                    ("cid", cid), ("name", s.SubName), ("level", s.Level), ("p1", s.Part1), ("p2", s.Part2), ("p3", s.Part3), ("p4", s.Part4),
                    ("points", s.Points), ("ret", s.ReturnTime), ("exp", s.CurrentExp), ("next", s.NextLevelExp), ("slot", s.Slot), ("now", now));
            var newNames = new HashSet<string>(subs.Select(s => s.SubName), StringComparer.Ordinal);
            foreach (var sub in subs)
            {
                if (sub.Slot == null || !oldNameBySlot.TryGetValue(sub.Slot.Value, out var oldName)) continue;
                if (oldName == sub.SubName || newNames.Contains(oldName)) continue;
                await Db.Exec(conn, @"
                    UPDATE sub_craft_toggle SET sub_name = @new, updated_at = @now
                    WHERE cid = @cid AND sub_name = @old
                      AND NOT EXISTS (SELECT 1 FROM sub_craft_toggle WHERE cid = @cid AND sub_name = @new)", tx,
                    ("cid", cid), ("old", oldName), ("new", sub.SubName), ("now", now));
            }
            await tx.CommitAsync();
        }

        private static async Task FileWriteRetainers(DbConnection conn, ulong ownerCid, IReadOnlyList<RetainerRecord> retainers)
        {
            var now = Db.Now();
            await using var tx = await conn.BeginTransactionAsync();
            foreach (var r in retainers)
                await Db.Exec(conn, @"
                    INSERT INTO companion_retainer_lookup
                        (retainer_id, owner_cid, name, job, gil, has_venture, venture_id, venture_begins_at, venture_ends_at, level, hire_order_index, updated_at)
                    VALUES (@id, @owner, @name, @job, @gil, @hasv, @vid, @vbegin, @vend, @level, @hire, @now)
                    ON CONFLICT (retainer_id) DO UPDATE SET
                        owner_cid = excluded.owner_cid, name = excluded.name, job = excluded.job, gil = excluded.gil,
                        has_venture = excluded.has_venture, venture_id = excluded.venture_id, venture_begins_at = excluded.venture_begins_at,
                        venture_ends_at = excluded.venture_ends_at, level = excluded.level, hire_order_index = excluded.hire_order_index,
                        updated_at = excluded.updated_at", tx,
                    ("id", r.RetainerId), ("owner", ownerCid), ("name", r.Name), ("job", (long)r.Job), ("gil", (long)r.Gil), ("hasv", r.HasVenture),
                    ("vid", (long)r.VentureId), ("vbegin", r.VentureBeginsAt), ("vend", r.VentureEndsAt), ("level", r.Level), ("hire", r.HireOrderIndex),
                    ("now", now));
            await using (var remove = Db.Command(conn, "", tx))
            {
                remove.CommandText = $"DELETE FROM companion_retainer_lookup WHERE owner_cid = @owner AND retainer_id NOT IN {Db.In(remove, "id", retainers.Select(r => r.RetainerId))}";
                Db.Add(remove, "owner", ownerCid);
                await remove.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        private static async Task<int> FileRemoveHousing(DbConnection conn, IReadOnlyCollection<ulong> cids)
        {
            await using var cmd = Db.Command(conn, "");
            cmd.CommandText = $"DELETE FROM companion_character_housing WHERE cid IN {Db.In(cmd, "cid", cids)}";
            return await cmd.ExecuteNonQueryAsync();
        }

        private static Task<int> FileRemoveOrphanFcChests(DbConnection conn) => Db.Exec(conn, @"
            DELETE FROM companion_fc_inventory_snapshot
            WHERE NOT EXISTS (SELECT 1 FROM companion_character_current c WHERE c.fc_id = companion_fc_inventory_snapshot.fc_id)");

        private static Task<int> FileWriteHousing(DbConnection conn, ulong cid, FCTrackerConnector.HousingInfo housing) => Db.Exec(conn, @"
            INSERT INTO companion_character_housing
                (cid, fc_id, fc_name, fc_points, fc_rank, total_members,
                 has_house, house_city, house_ward, house_plot, house_last_visited,
                 fc_master, fc_home_world_id, fc_founding_date, fc_eligibility_override, updated_at)
            VALUES
                (@cid, @fc_id, @fc_name, @fc_points, @fc_rank, @total_members,
                 @has_house, @house_city, @house_ward, @house_plot, @house_last_visited,
                 @fc_master, @fc_home_world_id, @fc_founding_date, @fc_eligibility_override, @now)
            ON CONFLICT (cid) DO UPDATE SET
                fc_id = excluded.fc_id, fc_name = excluded.fc_name, fc_points = excluded.fc_points, fc_rank = excluded.fc_rank,
                total_members = excluded.total_members, has_house = excluded.has_house, house_city = excluded.house_city,
                house_ward = excluded.house_ward, house_plot = excluded.house_plot, house_last_visited = excluded.house_last_visited,
                fc_master = excluded.fc_master, fc_home_world_id = excluded.fc_home_world_id, fc_founding_date = excluded.fc_founding_date,
                fc_eligibility_override = excluded.fc_eligibility_override, updated_at = excluded.updated_at", null,
            ("cid", cid), ("fc_id", Db.FcText(housing.FcId)), ("fc_name", housing.FcName), ("fc_points", housing.FcPoints),
            ("fc_rank", housing.FcRank.ToString(CultureInfo.InvariantCulture)), ("total_members", housing.TotalMembers), ("has_house", housing.HasHouse),
            ("house_city", housing.HouseCity), ("house_ward", housing.HouseWard), ("house_plot", housing.HousePlot),
            ("house_last_visited", Db.Unix(housing.HouseLastVisited)), ("fc_master", housing.FcMaster), ("fc_home_world_id", housing.FcHomeWorldId),
            ("fc_founding_date", Db.Unix(housing.FcFoundingDate)), ("fc_eligibility_override", Db.Unix(housing.FcEligibilityOverride)), ("now", Db.Now()));

        private static async Task<int> FileReadStoredSourceCount(DbConnection conn, ulong ownerCid)
        {
            await using var cmd = Db.Command(conn, "SELECT count(DISTINCT retainer_id) FROM companion_inventory_snapshot WHERE owner_cid = @owner");
            Db.Add(cmd, "owner", ownerCid);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0, CultureInfo.InvariantCulture);
        }

        private static async Task FileMarkRetainersSeen(DbConnection conn, IReadOnlyCollection<ulong> retainerIds)
        {
            await using var cmd = Db.Command(conn, "");
            cmd.CommandText = $"UPDATE companion_retainer_lookup SET items_seen_at = @now WHERE retainer_id IN {Db.In(cmd, "id", retainerIds)}";
            Db.Add(cmd, "now", Db.Now());
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<StoredAges> FileReadStoredAges(DbConnection conn, IReadOnlyCollection<ulong> cids, IReadOnlyCollection<ulong> fcIds)
        {
            var ages = new StoredAges();
            var now = Db.Now();
            async Task Read(string sql, IEnumerable<object> ids, Action<DbDataReader> add)
            {
                await using var cmd = Db.Command(conn, "");
                cmd.CommandText = sql.Replace("@ids", Db.In(cmd, "id", ids));
                Db.Add(cmd, "now", now);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) add(reader);
            }
            static TimeSpan Seconds(DbDataReader r, int i) => TimeSpan.FromSeconds(Math.Max(0, r.GetDouble(i)));
            await Read("SELECT cid, @now - last_synced_at FROM companion_character_current WHERE cid IN @ids", cids.Cast<object>(),
                r => ages.Synced[Db.Id(r, 0)] = Seconds(r, 1));
            await Read("SELECT owner_cid, retainer_id, @now - max(updated_at) FROM companion_inventory_snapshot WHERE owner_cid IN @ids GROUP BY 1, 2",
                cids.Cast<object>(), r => ages.Inventory[(Db.Id(r, 0), Db.Id(r, 1))] = Seconds(r, 2));
            await Read("SELECT fc_id, @now - max(updated_at) FROM companion_fc_inventory_snapshot WHERE fc_id IN @ids GROUP BY 1",
                fcIds.Select(f => (object)Db.FcText(f)!), r => ages.Chest[Db.Id(r, 0)] = Seconds(r, 1));
            return ages;
        }
    }
}
