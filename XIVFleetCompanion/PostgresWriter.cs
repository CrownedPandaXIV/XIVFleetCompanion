using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    public static class PostgresWriter
    {
        private static (string? connectionString, string? error) BuildConnectionString(bool useRemote)
        {
            var cred = PostgresCredentialStore.Load(useRemote);
            if (cred == null)
                return (null, "Not configured — no saved credential found.");

            var connectionString =
                $"Host={cred.Host};Port={cred.Port};Database={cred.Database};" +
                $"Username={cred.Username};Password={cred.Password};Timeout=5;" +
                $"Include Error Detail=true";

            return (connectionString, null);
        }
        public static async Task<string> RunRetentionCleanupAsync(
    int retentionValue, string retentionUnit,
    int downsampleValue, string downsampleUnit,
    bool useRemote)
        {
            var (connectionString, connError) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return connError!;

            // Months are approximated as 30-day blocks, not calendar months —
            // matches the note shown in the config UI.
            double UnitToDays(string unit) => unit switch
            {
                "Days" => 1.0,
                "Weeks" => 7.0,
                "Months" => 30.0,
                _ => 1.0
            };

            var retentionSeconds = retentionValue * UnitToDays(retentionUnit) * 86400.0;
            var bucketSeconds = downsampleValue * UnitToDays(downsampleUnit) * 86400.0;

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    DELETE FROM companion_character_snapshot
                    WHERE snapshot_at < (now() - (@retention_seconds || ' seconds')::interval)
                    AND ctid NOT IN (
                        SELECT DISTINCT ON (cid, bucket) ctid
                        FROM (
                            SELECT ctid, cid, snapshot_at,
                                   floor(extract(epoch from snapshot_at) / @bucket_seconds) AS bucket
                            FROM companion_character_snapshot
                            WHERE snapshot_at < (now() - (@retention_seconds || ' seconds')::interval)
                        ) sub
                        ORDER BY cid, bucket, snapshot_at DESC
                    )";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("retention_seconds", retentionSeconds);
                cmd.Parameters.AddWithValue("bucket_seconds", bucketSeconds);

                var deletedCount = await cmd.ExecuteNonQueryAsync();

                return $"Success — compressed {deletedCount} old rows.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }
        public static async Task<string> WriteCharacterSnapshotAsync(
            ulong cid, string name, string world,
            int retainerCount, int submarineCount,
            uint gil, int ceruleum, int repairKits, string accountLabel, bool useRemote)
        {
            var (connectionString, connError) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return connError!;

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO companion_character_snapshot
                        (cid, name, world, retainer_count, submarine_count, gil, ceruleum, repair_kits, account_label)
                    VALUES
                        (@cid, @name, @world, @retainer_count, @submarine_count, @gil, @ceruleum, @repair_kits, @account_label)";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("cid", (decimal)cid);
                cmd.Parameters.AddWithValue("name", name);
                cmd.Parameters.AddWithValue("world", world);
                cmd.Parameters.AddWithValue("retainer_count", retainerCount);
                cmd.Parameters.AddWithValue("submarine_count", submarineCount);
                cmd.Parameters.AddWithValue("gil", (long)gil);
                cmd.Parameters.AddWithValue("ceruleum", ceruleum);
                cmd.Parameters.AddWithValue("repair_kits", repairKits);
                cmd.Parameters.AddWithValue("account_label", string.IsNullOrEmpty(accountLabel) ? (object)DBNull.Value : accountLabel);

                await cmd.ExecuteNonQueryAsync();

                return "Success.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }

        public static async Task<string> WriteInventorySnapshotAsync(
            ulong ownerCid, List<AllaganToolsConnector.ParsedItem> items, bool useRemote)
        {
            var (connectionString, connError) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return connError!;

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();
                await using var transaction = await conn.BeginTransactionAsync();

                const string deleteSql = "DELETE FROM companion_inventory_snapshot WHERE owner_cid = @owner_cid";
                await using (var deleteCmd = new NpgsqlCommand(deleteSql, conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("owner_cid", (decimal)ownerCid);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                const string insertSql = @"
                    INSERT INTO companion_inventory_snapshot
                        (owner_cid, retainer_id, sorted_container, sorted_slot_index, item_id, quantity, gear_set_ids)
                    VALUES
                        (@owner_cid, @retainer_id, @sorted_container, @sorted_slot_index, @item_id, @quantity, @gear_set_ids)";

                foreach (var item in items)
                {
                    await using var insertCmd = new NpgsqlCommand(insertSql, conn, transaction);
                    insertCmd.Parameters.AddWithValue("owner_cid", (decimal)ownerCid);
                    insertCmd.Parameters.AddWithValue("retainer_id", (decimal)item.RetainerId);
                    insertCmd.Parameters.AddWithValue("sorted_container", (int)item.SortedContainer);
                    insertCmd.Parameters.AddWithValue("sorted_slot_index", item.SortedSlotIndex);
                    insertCmd.Parameters.AddWithValue("item_id", (int)item.ItemId);
                    insertCmd.Parameters.AddWithValue("quantity", (int)item.Quantity);

                    int[]? gearSetIdsAsInt = item.GearSetIds != null && item.GearSetIds.Length > 0
                        ? Array.ConvertAll(item.GearSetIds, x => (int)x)
                        : null;
                    insertCmd.Parameters.AddWithValue("gear_set_ids", (object?)gearSetIdsAsInt ?? DBNull.Value);

                    await insertCmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();

                return $"Success — wrote {items.Count} items.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }

        // One row of raw AutoRetainer submarine data - built by Plugin.cs
        // from AdditionalSubmarineData (build/rank) + OfflineSubmarineData
        // (voyage return time), matched by sub name. Deliberately RAW:
        // no route decoding, no gil/day math, no "current setup" string
        // formatting - that's business logic and belongs in Tier 3,
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
        }

        public static async Task<string> WriteSubmarineSnapshotAsync(
            ulong ownerCid, List<SubmarineRecord> subs, bool useRemote)
        {
            var (connectionString, connError) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return connError!;

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();
                await using var transaction = await conn.BeginTransactionAsync();

                const string deleteSql = "DELETE FROM companion_submarine_snapshot WHERE cid = @cid";
                await using (var deleteCmd = new NpgsqlCommand(deleteSql, conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("cid", (decimal)ownerCid);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                const string insertSql = @"
                    INSERT INTO companion_submarine_snapshot
                        (cid, sub_name, level, part1, part2, part3, part4, points, return_time, updated_at)
                    VALUES
                        (@cid, @sub_name, @level, @part1, @part2, @part3, @part4, @points, @return_time, now())";

                foreach (var sub in subs)
                {
                    await using var insertCmd = new NpgsqlCommand(insertSql, conn, transaction);
                    insertCmd.Parameters.AddWithValue("cid", (decimal)ownerCid);
                    insertCmd.Parameters.AddWithValue("sub_name", sub.SubName);
                    insertCmd.Parameters.AddWithValue("level", sub.Level);
                    insertCmd.Parameters.AddWithValue("part1", sub.Part1);
                    insertCmd.Parameters.AddWithValue("part2", sub.Part2);
                    insertCmd.Parameters.AddWithValue("part3", sub.Part3);
                    insertCmd.Parameters.AddWithValue("part4", sub.Part4);
                    insertCmd.Parameters.AddWithValue("points", sub.Points);
                    insertCmd.Parameters.AddWithValue("return_time", (object?)sub.ReturnTime ?? DBNull.Value);

                    await insertCmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();

                return $"Success — wrote {subs.Count} submarines.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }

        // Same "no curation" philosophy as WriteInventorySnapshotAsync -
        // every non-empty FC chest item stored as-is, no filtering for
        // "which items matter." Keyed by fc_id (not owner_cid), so
        // multiple characters from the same FC syncing independently all
        // write to the SAME rows - an upsert of the same real chest
        // contents, not a duplicate copy per character.
        public static async Task<string> WriteFCInventorySnapshotAsync(
            ulong fcId, List<AllaganToolsConnector.ParsedItem> items, bool useRemote)
        {
            var (connectionString, connError) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return connError!;

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();
                await using var transaction = await conn.BeginTransactionAsync();

                const string deleteSql = "DELETE FROM companion_fc_inventory_snapshot WHERE fc_id = @fc_id";
                await using (var deleteCmd = new NpgsqlCommand(deleteSql, conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("fc_id", (decimal)fcId);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                const string insertSql = @"
                    INSERT INTO companion_fc_inventory_snapshot
                        (fc_id, sorted_container, sorted_slot_index, item_id, quantity)
                    VALUES
                        (@fc_id, @sorted_container, @sorted_slot_index, @item_id, @quantity)";

                foreach (var item in items)
                {
                    await using var insertCmd = new NpgsqlCommand(insertSql, conn, transaction);
                    insertCmd.Parameters.AddWithValue("fc_id", (decimal)fcId);
                    insertCmd.Parameters.AddWithValue("sorted_container", (int)item.SortedContainer);
                    insertCmd.Parameters.AddWithValue("sorted_slot_index", item.SortedSlotIndex);
                    insertCmd.Parameters.AddWithValue("item_id", (int)item.ItemId);
                    insertCmd.Parameters.AddWithValue("quantity", (int)item.Quantity);

                    await insertCmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();

                return $"Success — wrote {items.Count} FC chest items.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }

        public static async Task<string> WriteHousingSnapshotAsync(
            ulong cid, FCTrackerConnector.HousingInfo housing, bool useRemote)
        {
            var (connectionString, connError) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return connError!;

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO companion_character_housing
                        (cid, fc_id, fc_name, fc_points, fc_rank, total_members,
                         has_house, house_city, house_ward, house_plot, house_last_visited, updated_at)
                    VALUES
                        (@cid, @fc_id, @fc_name, @fc_points, @fc_rank, @total_members,
                         @has_house, @house_city, @house_ward, @house_plot, @house_last_visited, now())
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
                        updated_at = now()";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("cid", (decimal)cid);
                cmd.Parameters.AddWithValue("fc_id", (decimal)housing.FcId);
                cmd.Parameters.AddWithValue("fc_name", housing.FcName);
                cmd.Parameters.AddWithValue("fc_points", housing.FcPoints);
                cmd.Parameters.AddWithValue("fc_rank", housing.FcRank);
                cmd.Parameters.AddWithValue("total_members", housing.TotalMembers);
                cmd.Parameters.AddWithValue("has_house", housing.HasHouse);
                cmd.Parameters.AddWithValue("house_city", (object?)housing.HouseCity ?? DBNull.Value);
                cmd.Parameters.AddWithValue("house_ward", (object?)housing.HouseWard ?? DBNull.Value);
                cmd.Parameters.AddWithValue("house_plot", (object?)housing.HousePlot ?? DBNull.Value);
                cmd.Parameters.AddWithValue("house_last_visited", (object?)housing.HouseLastVisited ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync();

                return "Success.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }
    }
}
