using Npgsql;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Writes one sync's data over a single open connection, each table in one statement
    /// instead of one statement per row. Kept free of Dalamud so it can be tested on its own
    /// against a database (see tests/FleetWriterTests).
    /// </summary>
    public static class FleetWriter
    {
        public class CharacterSnapshot
        {
            public ulong Cid;
            public string Name = "";
            public string World = "";
            public int RetainerCount;
            public int SubmarineCount;
            public long Gil;
            public int Ceruleum;
            public int RepairKits;
            public string? AccountLabel;
            public ulong FcId;
            public int NumSubSlots;
        }

        public class InventoryItem
        {
            public ulong RetainerId;
            public uint SortedContainer;
            public int SortedSlotIndex;
            public uint ItemId;
            public uint Quantity;
            public uint[] GearSetIds = Array.Empty<uint>();
        }

        // One row of raw AutoRetainer submarine data - built by Plugin.cs
        // from AdditionalSubmarineData (build/rank) + OfflineSubmarineData
        // (voyage return time), matched by sub name. Deliberately RAW:
        // no route decoding, no gil/day math, no "current setup" string
        // formatting - that's business logic and belongs in the app,
        // matching the same philosophy as companion_inventory_snapshot
        // storing every item with zero curation.
        public class SubmarineRecord
        {
            public string SubName = "";
            public int Level;
            public int Part1;
            public int Part2;
            public int Part3;
            public int Part4;
            public byte[] Points = Array.Empty<byte>();
            public long? ReturnTime;
            public long CurrentExp;
            public long NextLevelExp;
            // Workshop slot, 1-4 (from the position in AutoRetainer's sub list). Null when unknown.
            public int? Slot;
        }

        // Which subs to write, and their slots. registered: AutoRetainer's list of the character's subs
        // (OfflineSubmarineData), in workshop order; withBuild: the names AutoRetainer has build data
        // for (AdditionalSubmarineData, keyed by name). A sub is written when it is in both; its slot
        // is its position in the list. Build data under a name that is no longer in the list (a sub
        // that was renamed) is left out. If the list is empty (not read yet), every sub with build data
        // is written, with the slot taken from a default name (Submersible-2 -> 2) or left unknown.
        public static List<(string Name, int? Slot)> PlanSubmarines(IReadOnlyList<string> registered, IEnumerable<string> withBuild)
        {
            var built = new HashSet<string>(withBuild, StringComparer.Ordinal);
            var plan = new List<(string Name, int? Slot)>();
            if (registered.Count == 0)
            {
                foreach (var name in built.OrderBy(n => n, StringComparer.Ordinal))
                    plan.Add((name, SlotFromDefaultName(name)));
                return plan;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < registered.Count; i++)
            {
                var name = registered[i];
                if (built.Contains(name) && seen.Add(name))
                    plan.Add((name, i + 1));
            }
            return plan;
        }

        // "Submersible-3" -> 3; any other name -> null.
        public static int? SlotFromDefaultName(string name)
        {
            const string prefix = "Submersible-";
            return name.StartsWith(prefix, StringComparison.Ordinal)
                   && int.TryParse(name.AsSpan(prefix.Length), out var n) && n >= 1 && n <= 4
                ? n
                : null;
        }

        public class RetainerRecord
        {
            public ulong RetainerId;
            public string Name = "";
            public uint Job;
            public uint Gil;
            public bool HasVenture;
            public uint VentureId;
            public long VentureBeginsAt;
            public long VentureEndsAt;
            public int Level;
            public int? HireOrderIndex;
        }

        // Every character's current row (sql/004_character_current.sql), in one statement. Changes of
        // name, world, account label or Free Company are logged by a trigger on that table.
        public static async Task<int> WriteCurrentCharactersAsync(NpgsqlConnection conn, IReadOnlyList<CharacterSnapshot> rows)
        {
            if (rows.Count == 0) return 0;
            const string sql = @"
                INSERT INTO companion_character_current
                    (cid, name, world, account_label, gil, ceruleum, repair_kits, retainer_count, submarine_count, num_sub_slots, fc_id, last_synced_at)
                SELECT t.*, now()
                FROM unnest(@cid::numeric[], @name::text[], @world::text[], @account::text[], @gil::bigint[], @ceruleum::int[],
                            @kits::int[], @retainers::int[], @subs::int[], @slots::int[], @fc::numeric[]) AS t
                ON CONFLICT (cid) DO UPDATE SET
                    name = EXCLUDED.name,
                    world = EXCLUDED.world,
                    account_label = EXCLUDED.account_label,
                    gil = EXCLUDED.gil,
                    ceruleum = EXCLUDED.ceruleum,
                    repair_kits = EXCLUDED.repair_kits,
                    retainer_count = EXCLUDED.retainer_count,
                    submarine_count = EXCLUDED.submarine_count,
                    num_sub_slots = EXCLUDED.num_sub_slots,
                    fc_id = EXCLUDED.fc_id,
                    last_synced_at = now()";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("cid", rows.Select(r => (decimal)r.Cid).ToArray());
            cmd.Parameters.AddWithValue("name", rows.Select(r => r.Name).ToArray());
            cmd.Parameters.AddWithValue("world", rows.Select(r => r.World).ToArray());
            cmd.Parameters.AddWithValue("account", rows.Select(r => string.IsNullOrEmpty(r.AccountLabel) ? null : r.AccountLabel).ToArray());
            cmd.Parameters.AddWithValue("gil", rows.Select(r => r.Gil).ToArray());
            cmd.Parameters.AddWithValue("ceruleum", rows.Select(r => r.Ceruleum).ToArray());
            cmd.Parameters.AddWithValue("kits", rows.Select(r => r.RepairKits).ToArray());
            cmd.Parameters.AddWithValue("retainers", rows.Select(r => r.RetainerCount).ToArray());
            cmd.Parameters.AddWithValue("subs", rows.Select(r => r.SubmarineCount).ToArray());
            cmd.Parameters.AddWithValue("slots", rows.Select(r => r.NumSubSlots).ToArray());
            cmd.Parameters.AddWithValue("fc", rows.Select(r => r.FcId == 0 ? (decimal?)null : r.FcId).ToArray());
            return await cmd.ExecuteNonQueryAsync();
        }

        // Where one character's items come from: its own bags (Id is the character's cid) or one of its
        // retainers. SeenItems is null when AllaganTools has never seen that source (it answers with
        // nothing at all, not even empty slots, for a retainer that was never opened at a bell), so its
        // stored items are kept instead of being cleared.
        public sealed class InventorySource
        {
            public ulong Id;
            public string Name = "";
            public List<InventoryItem>? SeenItems;
        }

        // What one character's inventory write does: replace the stored items of the sources read this
        // sync (Read), with Items; and when the character's retainer list is known (from AutoRetainer),
        // drop the stored items of any source not in Current (a dismissed retainer). Unseen are the
        // retainers whose last stored items are kept.
        public sealed class InventoryPlan
        {
            public List<ulong> Read = new();
            public List<ulong>? Current;
            public List<InventoryItem> Items = new();
            public List<InventorySource> Unseen = new();
            public bool BagsSeen;
        }

        public static InventoryPlan PlanInventory(ulong ownerCid, IReadOnlyList<InventorySource> sources, bool retainerListKnown)
        {
            var plan = new InventoryPlan();
            foreach (var source in sources)
            {
                if (source.SeenItems == null)
                {
                    if (source.Id != ownerCid) plan.Unseen.Add(source);
                    continue;
                }
                if (source.Id == ownerCid) plan.BagsSeen = true;
                plan.Read.Add(source.Id);
                // Stored under the source they were read from, so they replace exactly that source's rows.
                plan.Items.AddRange(source.SeenItems.Select(i => new InventoryItem
                {
                    RetainerId = source.Id,
                    SortedContainer = i.SortedContainer,
                    SortedSlotIndex = i.SortedSlotIndex,
                    ItemId = i.ItemId,
                    Quantity = i.Quantity,
                    GearSetIds = i.GearSetIds,
                }));
            }
            if (retainerListKnown)
                plan.Current = sources.Select(s => s.Id).Append(ownerCid).Distinct().ToList();
            return plan;
        }

        // Writes one character's inventory plan in one transaction: the read sources' stored items are
        // replaced, unread sources keep theirs, and (with plan.Current) a source no longer there loses its.
        public static async Task WriteInventoryAsync(NpgsqlConnection conn, ulong ownerCid, InventoryPlan plan)
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using (var delete = new NpgsqlCommand(@"
                DELETE FROM companion_inventory_snapshot
                WHERE owner_cid = @owner
                  AND (retainer_id = ANY(@read::numeric[])
                       OR (@current::numeric[] IS NOT NULL AND NOT retainer_id = ANY(@current::numeric[])))", conn, tx))
            {
                delete.Parameters.AddWithValue("owner", (decimal)ownerCid);
                delete.Parameters.AddWithValue("read", plan.Read.Select(id => (decimal)id).ToArray());
                delete.Parameters.Add(new NpgsqlParameter("current", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Numeric)
                {
                    Value = plan.Current == null ? DBNull.Value : plan.Current.Select(id => (decimal)id).ToArray(),
                });
                await delete.ExecuteNonQueryAsync();
            }
            var items = plan.Items;
            if (items.Count > 0)
            {
                // Gear set lists differ in length per item, so they travel as array text ("{1,4}")
                // and are turned back into int[] in the statement.
                const string sql = @"
                    INSERT INTO companion_inventory_snapshot
                        (owner_cid, retainer_id, sorted_container, sorted_slot_index, item_id, quantity, gear_set_ids)
                    SELECT @owner, r, c, s, i, q, g::int[]
                    FROM unnest(@retainer::numeric[], @container::int[], @slot::int[], @item::int[], @qty::int[], @gear::text[])
                         AS t(r, c, s, i, q, g)";
                await using var insert = new NpgsqlCommand(sql, conn, tx);
                insert.Parameters.AddWithValue("owner", (decimal)ownerCid);
                insert.Parameters.AddWithValue("retainer", items.Select(i => (decimal)i.RetainerId).ToArray());
                insert.Parameters.AddWithValue("container", items.Select(i => (int)i.SortedContainer).ToArray());
                insert.Parameters.AddWithValue("slot", items.Select(i => i.SortedSlotIndex).ToArray());
                insert.Parameters.AddWithValue("item", items.Select(i => (int)i.ItemId).ToArray());
                insert.Parameters.AddWithValue("qty", items.Select(i => (int)i.Quantity).ToArray());
                insert.Parameters.AddWithValue("gear", items.Select(i => GearSetText(i.GearSetIds)).ToArray());
                await insert.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        // How many of each item one character has stored across its bags and retainers (including the
        // last stored items of retainers AllaganTools did not see this time). Items it has none of are
        // returned as 0.
        public static async Task<Dictionary<uint, long>> ReadItemTotalsAsync(NpgsqlConnection conn, ulong ownerCid, IReadOnlyCollection<uint> itemIds)
        {
            var totals = itemIds.ToDictionary(id => id, _ => 0L);
            await using var cmd = new NpgsqlCommand(@"
                SELECT item_id, sum(quantity)::bigint FROM companion_inventory_snapshot
                WHERE owner_cid = @owner AND item_id = ANY(@items::int[])
                GROUP BY item_id", conn);
            cmd.Parameters.AddWithValue("owner", (decimal)ownerCid);
            cmd.Parameters.AddWithValue("items", itemIds.Select(i => (int)i).ToArray());
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                totals[(uint)reader.GetInt32(0)] = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            return totals;
        }

        // Replaces one Free Company's stored chest in one transaction. Several characters in the
        // same FC write the same rows, so this is an overwrite of the same chest, not a copy each.
        public static async Task WriteFcInventoryAsync(NpgsqlConnection conn, ulong fcId, IReadOnlyList<InventoryItem> items)
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using (var delete = new NpgsqlCommand("DELETE FROM companion_fc_inventory_snapshot WHERE fc_id = @fc", conn, tx))
            {
                delete.Parameters.AddWithValue("fc", (decimal)fcId);
                await delete.ExecuteNonQueryAsync();
            }
            if (items.Count > 0)
            {
                const string sql = @"
                    INSERT INTO companion_fc_inventory_snapshot (fc_id, sorted_container, sorted_slot_index, item_id, quantity)
                    SELECT @fc, c, s, i, q FROM unnest(@container::int[], @slot::int[], @item::int[], @qty::int[]) AS t(c, s, i, q)";
                await using var insert = new NpgsqlCommand(sql, conn, tx);
                insert.Parameters.AddWithValue("fc", (decimal)fcId);
                insert.Parameters.AddWithValue("container", items.Select(i => (int)i.SortedContainer).ToArray());
                insert.Parameters.AddWithValue("slot", items.Select(i => i.SortedSlotIndex).ToArray());
                insert.Parameters.AddWithValue("item", items.Select(i => (int)i.ItemId).ToArray());
                insert.Parameters.AddWithValue("qty", items.Select(i => (int)i.Quantity).ToArray());
                await insert.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        // Replaces one character's stored submarines in one transaction. When a slot now holds a sub
        // with a different name and the old name is gone (the sub was renamed), the app's Craft?
        // setting (sub_craft_toggle, kept per name) moves to the new name. The slot column comes from
        // sql/006; without it the subs are written as before, without slots.
        public static async Task WriteSubmarinesAsync(NpgsqlConnection conn, ulong cid, IReadOnlyList<SubmarineRecord> subs)
        {
            await using var tx = await conn.BeginTransactionAsync();

            bool hasSlot, hasCraftToggle;
            await using (var check = new NpgsqlCommand(@"
                SELECT EXISTS (SELECT 1 FROM information_schema.columns
                               WHERE table_schema = current_schema() AND table_name = 'companion_submarine_snapshot' AND column_name = 'slot'),
                       to_regclass('sub_craft_toggle') IS NOT NULL", conn, tx))
            await using (var reader = await check.ExecuteReaderAsync())
            {
                await reader.ReadAsync();
                hasSlot = reader.GetBoolean(0);
                hasCraftToggle = reader.GetBoolean(1);
            }

            // The names stored per slot before this write (older rows without a slot: from the default name).
            var oldNameBySlot = new Dictionary<int, string>();
            if (hasSlot)
            {
                await using var old = new NpgsqlCommand(
                    "SELECT sub_name, slot FROM companion_submarine_snapshot WHERE cid = @cid", conn, tx);
                old.Parameters.AddWithValue("cid", (decimal)cid);
                await using var reader = await old.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(0);
                    var slot = reader.IsDBNull(1) ? SlotFromDefaultName(name) : reader.GetInt32(1);
                    if (slot != null) oldNameBySlot[slot.Value] = name;
                }
            }

            await using (var delete = new NpgsqlCommand("DELETE FROM companion_submarine_snapshot WHERE cid = @cid", conn, tx))
            {
                delete.Parameters.AddWithValue("cid", (decimal)cid);
                await delete.ExecuteNonQueryAsync();
            }
            if (subs.Count > 0)
            {
                var sql = hasSlot
                    ? @"
                    INSERT INTO companion_submarine_snapshot
                        (cid, sub_name, level, part1, part2, part3, part4, points, return_time, current_exp, next_level_exp, slot, updated_at)
                    SELECT @cid, n, l, p1, p2, p3, p4, pts, rt, ce, ne, sl, now()
                    FROM unnest(@name::text[], @level::int[], @p1::int[], @p2::int[], @p3::int[], @p4::int[],
                                @points::bytea[], @ret::bigint[], @exp::bigint[], @next::bigint[], @slot::int[])
                         AS t(n, l, p1, p2, p3, p4, pts, rt, ce, ne, sl)"
                    : @"
                    INSERT INTO companion_submarine_snapshot
                        (cid, sub_name, level, part1, part2, part3, part4, points, return_time, current_exp, next_level_exp, updated_at)
                    SELECT @cid, n, l, p1, p2, p3, p4, pts, rt, ce, ne, now()
                    FROM unnest(@name::text[], @level::int[], @p1::int[], @p2::int[], @p3::int[], @p4::int[],
                                @points::bytea[], @ret::bigint[], @exp::bigint[], @next::bigint[])
                         AS t(n, l, p1, p2, p3, p4, pts, rt, ce, ne)";
                await using var insert = new NpgsqlCommand(sql, conn, tx);
                insert.Parameters.AddWithValue("cid", (decimal)cid);
                insert.Parameters.AddWithValue("name", subs.Select(s => s.SubName).ToArray());
                insert.Parameters.AddWithValue("level", subs.Select(s => s.Level).ToArray());
                insert.Parameters.AddWithValue("p1", subs.Select(s => s.Part1).ToArray());
                insert.Parameters.AddWithValue("p2", subs.Select(s => s.Part2).ToArray());
                insert.Parameters.AddWithValue("p3", subs.Select(s => s.Part3).ToArray());
                insert.Parameters.AddWithValue("p4", subs.Select(s => s.Part4).ToArray());
                insert.Parameters.AddWithValue("points", subs.Select(s => s.Points).ToArray());
                insert.Parameters.AddWithValue("ret", subs.Select(s => s.ReturnTime).ToArray());
                insert.Parameters.AddWithValue("exp", subs.Select(s => s.CurrentExp).ToArray());
                insert.Parameters.AddWithValue("next", subs.Select(s => s.NextLevelExp).ToArray());
                if (hasSlot)
                    insert.Parameters.AddWithValue("slot", subs.Select(s => s.Slot).ToArray());
                await insert.ExecuteNonQueryAsync();
            }

            if (hasCraftToggle)
            {
                var newNames = new HashSet<string>(subs.Select(s => s.SubName), StringComparer.Ordinal);
                foreach (var sub in subs)
                {
                    if (sub.Slot == null || !oldNameBySlot.TryGetValue(sub.Slot.Value, out var oldName)) continue;
                    if (oldName == sub.SubName || newNames.Contains(oldName)) continue;
                    await using var move = new NpgsqlCommand(@"
                        UPDATE sub_craft_toggle SET sub_name = @new, updated_at = now()
                        WHERE cid = @cid AND sub_name = @old
                          AND NOT EXISTS (SELECT 1 FROM sub_craft_toggle WHERE cid = @cid AND sub_name = @new)", conn, tx);
                    move.Parameters.AddWithValue("cid", (decimal)cid);
                    move.Parameters.AddWithValue("old", oldName);
                    move.Parameters.AddWithValue("new", sub.SubName);
                    await move.ExecuteNonQueryAsync();
                }
            }
            await tx.CommitAsync();
        }

        // Inserts or updates every retainer of one character, and removes that character's retainers
        // that are no longer in the list (dismissed), in one transaction. An empty list changes
        // nothing: it is more likely missing data than a character who dismissed every retainer.
        public static async Task WriteRetainersAsync(NpgsqlConnection conn, ulong ownerCid, IReadOnlyList<RetainerRecord> retainers)
        {
            if (retainers.Count == 0) return;
            await using var tx = await conn.BeginTransactionAsync();
            const string sql = @"
                INSERT INTO companion_retainer_lookup
                    (retainer_id, owner_cid, name, job, gil, has_venture, venture_id, venture_begins_at, venture_ends_at, level, hire_order_index, updated_at)
                SELECT r, @owner, n, j, g, hv, vi, vb, ve, l, h, now()
                FROM unnest(@id::numeric[], @name::text[], @job::int[], @gil::bigint[], @hasv::boolean[], @vid::int[],
                            @vbegin::bigint[], @vend::bigint[], @level::int[], @hire::int[])
                     AS t(r, n, j, g, hv, vi, vb, ve, l, h)
                ON CONFLICT (retainer_id) DO UPDATE SET
                    owner_cid = EXCLUDED.owner_cid,
                    name = EXCLUDED.name,
                    job = EXCLUDED.job,
                    gil = EXCLUDED.gil,
                    has_venture = EXCLUDED.has_venture,
                    venture_id = EXCLUDED.venture_id,
                    venture_begins_at = EXCLUDED.venture_begins_at,
                    venture_ends_at = EXCLUDED.venture_ends_at,
                    level = EXCLUDED.level,
                    hire_order_index = EXCLUDED.hire_order_index,
                    updated_at = now()";
            var ids = retainers.Select(r => (decimal)r.RetainerId).ToArray();
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("owner", (decimal)ownerCid);
            cmd.Parameters.AddWithValue("id", ids);
            cmd.Parameters.AddWithValue("name", retainers.Select(r => r.Name).ToArray());
            cmd.Parameters.AddWithValue("job", retainers.Select(r => (int)r.Job).ToArray());
            cmd.Parameters.AddWithValue("gil", retainers.Select(r => (long)r.Gil).ToArray());
            cmd.Parameters.AddWithValue("hasv", retainers.Select(r => r.HasVenture).ToArray());
            cmd.Parameters.AddWithValue("vid", retainers.Select(r => (int)r.VentureId).ToArray());
            cmd.Parameters.AddWithValue("vbegin", retainers.Select(r => r.VentureBeginsAt).ToArray());
            cmd.Parameters.AddWithValue("vend", retainers.Select(r => r.VentureEndsAt).ToArray());
            cmd.Parameters.AddWithValue("level", retainers.Select(r => r.Level).ToArray());
            cmd.Parameters.AddWithValue("hire", retainers.Select(r => r.HireOrderIndex).ToArray());
            await cmd.ExecuteNonQueryAsync();

            await using var remove = new NpgsqlCommand(
                "DELETE FROM companion_retainer_lookup WHERE owner_cid = @owner AND retainer_id <> ALL(@id)", conn, tx);
            remove.Parameters.AddWithValue("owner", (decimal)ownerCid);
            remove.Parameters.AddWithValue("id", ids);
            await remove.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        // Removes the Free Company and housing rows of characters that are no longer in any Free
        // Company. Returns how many were removed.
        public static async Task<int> RemoveHousingAsync(NpgsqlConnection conn, IReadOnlyCollection<ulong> cids)
        {
            if (cids.Count == 0) return 0;
            await using var cmd = new NpgsqlCommand("DELETE FROM companion_character_housing WHERE cid = ANY(@cids)", conn);
            cmd.Parameters.AddWithValue("cids", cids.Select(c => (decimal)c).ToArray());
            return await cmd.ExecuteNonQueryAsync();
        }

        // Removes the chests of Free Companies that no tracked character belongs to any more (a
        // character left, or the FC was disbanded), going by companion_character_current, which
        // holds every account's characters. Returns how many chest slots were removed.
        public static async Task<int> RemoveOrphanFcChestsAsync(NpgsqlConnection conn)
        {
            const string sql = @"
                DELETE FROM companion_fc_inventory_snapshot f
                WHERE NOT EXISTS (SELECT 1 FROM companion_character_current c WHERE c.fc_id = f.fc_id)";
            await using var cmd = new NpgsqlCommand(sql, conn);
            return await cmd.ExecuteNonQueryAsync();
        }

        // Inserts or updates one character's Free Company and housing details.
        public static async Task WriteHousingAsync(NpgsqlConnection conn, ulong cid, FCTrackerConnector.HousingInfo housing)
        {
            const string sql = @"
                INSERT INTO companion_character_housing
                    (cid, fc_id, fc_name, fc_points, fc_rank, total_members,
                     has_house, house_city, house_ward, house_plot, house_last_visited,
                     fc_master, fc_home_world_id, fc_founding_date, fc_eligibility_override, updated_at)
                VALUES
                    (@cid, @fc_id, @fc_name, @fc_points, @fc_rank, @total_members,
                     @has_house, @house_city, @house_ward, @house_plot, @house_last_visited,
                     @fc_master, @fc_home_world_id, @fc_founding_date, @fc_eligibility_override, now())
                ON CONFLICT (cid) DO UPDATE SET
                    fc_id = EXCLUDED.fc_id,
                    fc_name = EXCLUDED.fc_name,
                    fc_points = EXCLUDED.fc_points,
                    fc_rank = EXCLUDED.fc_rank,
                    total_members = EXCLUDED.total_members,
                    has_house = EXCLUDED.has_house,
                    house_city = EXCLUDED.house_city,
                    house_ward = EXCLUDED.house_ward,
                    house_plot = EXCLUDED.house_plot,
                    house_last_visited = EXCLUDED.house_last_visited,
                    fc_master = EXCLUDED.fc_master,
                    fc_home_world_id = EXCLUDED.fc_home_world_id,
                    fc_founding_date = EXCLUDED.fc_founding_date,
                    fc_eligibility_override = EXCLUDED.fc_eligibility_override,
                    updated_at = now()";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("cid", (decimal)cid);
            cmd.Parameters.AddWithValue("fc_id", (decimal)housing.FcId);
            cmd.Parameters.AddWithValue("fc_name", housing.FcName);
            cmd.Parameters.AddWithValue("fc_points", housing.FcPoints);
            cmd.Parameters.AddWithValue("fc_rank", housing.FcRank.ToString(CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("total_members", housing.TotalMembers);
            cmd.Parameters.AddWithValue("has_house", housing.HasHouse);
            cmd.Parameters.AddWithValue("house_city", (object?)housing.HouseCity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("house_ward", (object?)housing.HouseWard ?? DBNull.Value);
            cmd.Parameters.AddWithValue("house_plot", (object?)housing.HousePlot ?? DBNull.Value);
            cmd.Parameters.AddWithValue("house_last_visited", (object?)housing.HouseLastVisited ?? DBNull.Value);
            cmd.Parameters.AddWithValue("fc_master", (object?)housing.FcMaster ?? DBNull.Value);
            cmd.Parameters.AddWithValue("fc_home_world_id", (object?)housing.FcHomeWorldId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("fc_founding_date", (object?)housing.FcFoundingDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("fc_eligibility_override", (object?)housing.FcEligibilityOverride ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        private static string? GearSetText(uint[]? ids)
            => ids == null || ids.Length == 0 ? null : "{" + string.Join(",", ids) + "}";

        // How long ago things were last stored, for "Check what I can see" (reads only): each
        // character's row, each inventory source (bags under the character's own id, or a retainer)
        // and each FC chest. Missing from a dictionary: nothing stored.
        public sealed class StoredAges
        {
            public Dictionary<ulong, TimeSpan> Synced = new();
            public Dictionary<(ulong Owner, ulong Source), TimeSpan> Inventory = new();
            public Dictionary<ulong, TimeSpan> Chest = new();
        }

        public static async Task<StoredAges> ReadStoredAgesAsync(NpgsqlConnection conn, IReadOnlyCollection<ulong> cids, IReadOnlyCollection<ulong> fcIds)
        {
            var ages = new StoredAges();
            var cidArray = cids.Select(c => (decimal)c).ToArray();
            async Task Read(string sql, decimal[] ids, Action<NpgsqlDataReader> add)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("ids", ids);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) add(reader);
            }
            static TimeSpan Seconds(NpgsqlDataReader r, int i) => TimeSpan.FromSeconds(Math.Max(0, r.GetDouble(i)));

            await Read(@"SELECT cid, extract(epoch FROM now() - last_synced_at)::float8
                         FROM companion_character_current WHERE cid = ANY(@ids::numeric[])", cidArray,
                r => ages.Synced[(ulong)r.GetDecimal(0)] = Seconds(r, 1));
            await Read(@"SELECT owner_cid, retainer_id, extract(epoch FROM now() - max(updated_at))::float8
                         FROM companion_inventory_snapshot WHERE owner_cid = ANY(@ids::numeric[]) GROUP BY 1, 2", cidArray,
                r => ages.Inventory[((ulong)r.GetDecimal(0), (ulong)r.GetDecimal(1))] = Seconds(r, 2));
            await Read(@"SELECT fc_id, extract(epoch FROM now()::timestamp - max(updated_at))::float8
                         FROM companion_fc_inventory_snapshot WHERE fc_id = ANY(@ids::numeric[]) GROUP BY 1",
                fcIds.Select(f => (decimal)f).ToArray(),
                r => ages.Chest[(ulong)r.GetDecimal(0)] = Seconds(r, 1));
            return ages;
        }

        // ---- Fingerprints: a short digest of everything a write would store, so an unchanged
        //      inventory, chest, sub list, retainer list or housing entry is not written again. ----

        public static string Fingerprint(IEnumerable<InventoryItem> items)
            => Digest(items
                .OrderBy(i => i.RetainerId).ThenBy(i => i.SortedContainer).ThenBy(i => i.SortedSlotIndex)
                .Select(i => $"{i.RetainerId}|{i.SortedContainer}|{i.SortedSlotIndex}|{i.ItemId}|{i.Quantity}|{string.Join(",", i.GearSetIds ?? Array.Empty<uint>())}"));

        // An inventory plan: its items and which sources it replaces or keeps.
        public static string Fingerprint(InventoryPlan plan)
            => Fingerprint(plan.Items) + "|" + string.Join(",", plan.Read.OrderBy(id => id))
               + "|" + (plan.Current == null ? "-" : string.Join(",", plan.Current.OrderBy(id => id)));

        public static string Fingerprint(IEnumerable<SubmarineRecord> subs)
            => Digest(subs.OrderBy(s => s.SubName, StringComparer.Ordinal)
                .Select(s => $"{s.SubName}|{s.Level}|{s.Part1}|{s.Part2}|{s.Part3}|{s.Part4}|{Convert.ToHexString(s.Points ?? Array.Empty<byte>())}|{s.ReturnTime}|{s.CurrentExp}|{s.NextLevelExp}|{s.Slot}"));

        public static string Fingerprint(IEnumerable<RetainerRecord> retainers)
            => Digest(retainers.OrderBy(r => r.RetainerId)
                .Select(r => $"{r.RetainerId}|{r.Name}|{r.Job}|{r.Gil}|{r.HasVenture}|{r.VentureId}|{r.VentureBeginsAt}|{r.VentureEndsAt}|{r.Level}|{r.HireOrderIndex}"));

        public static string Fingerprint(FCTrackerConnector.HousingInfo h)
            => Digest(new[] { string.Join("|", h.FcId, h.FcName, h.FcPoints, h.FcRank, h.TotalMembers, h.HasHouse, h.HouseCity, h.HouseWard,
                h.HousePlot, h.HouseLastVisited?.ToString("O"), h.FcMaster, h.FcHomeWorldId, h.FcFoundingDate?.ToString("O"),
                h.FcEligibilityOverride?.ToString("O")) });

        private static string Digest(IEnumerable<string> lines)
        {
            var text = string.Join("\n", lines);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
    }

    /// <summary>
    /// Remembers what was last written for each thing (a character's inventory, an FC chest, ...)
    /// so unchanged data is skipped. Everything is forgotten every <c>refreshEvery</c>, so the
    /// database is fully rewritten now and then even if it was changed from elsewhere.
    /// </summary>
    public class ChangeTracker
    {
        private readonly Dictionary<string, string> lastWritten = new();
        private readonly TimeSpan refreshEvery;
        private DateTime startedAt;

        public ChangeTracker(TimeSpan refreshEvery, DateTime now)
        {
            this.refreshEvery = refreshEvery;
            startedAt = now;
        }

        // Call once at the start of each sync.
        public void BeginSync(DateTime now)
        {
            if (now - startedAt >= refreshEvery)
            {
                lastWritten.Clear();
                startedAt = now;
            }
        }

        public bool IsUnchanged(string key, string fingerprint)
            => lastWritten.TryGetValue(key, out var previous) && previous == fingerprint;

        // Call only after the write succeeded.
        public void Remember(string key, string fingerprint) => lastWritten[key] = fingerprint;

        public void Forget(string key) => lastWritten.Remove(key);
    }
}
