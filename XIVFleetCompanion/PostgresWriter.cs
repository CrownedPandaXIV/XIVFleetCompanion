using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
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

                // Keeps the newest row per character per downsample interval among the rows
                // older than the retention window, and deletes the rest. The old rows are
                // pulled out first (MATERIALIZED) and ranked, then removed by row id: written
                // this way, a first cleanup of 4 million rows took about 20 s in testing,
                // where the previous NOT IN form had not finished after 15 minutes. Results
                // are identical. An index on snapshot_at (sql/003) makes the daily run faster.
                const string sql = @"
                    WITH old AS MATERIALIZED (
                        SELECT ctid AS row_id, cid, snapshot_at
                        FROM companion_character_snapshot
                        WHERE snapshot_at < (now() - (@retention_seconds || ' seconds')::interval)
                    ), ranked AS (
                        SELECT row_id,
                               row_number() OVER (
                                   PARTITION BY cid, floor(extract(epoch from snapshot_at) / @bucket_seconds)
                                   ORDER BY snapshot_at DESC) AS rn
                        FROM old
                    )
                    DELETE FROM companion_character_snapshot
                    WHERE ctid = ANY (ARRAY(SELECT row_id FROM ranked WHERE rn > 1))";

                // The first cleanup after shortening the retention can delete millions of
                // rows, which can take longer than the 30 s default command timeout.
                await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 600 };
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
            uint gil, int ceruleum, int repairKits, string accountLabel, ulong fcId, int numSubSlots, bool useRemote)
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
                        (cid, name, world, retainer_count, submarine_count, gil, ceruleum, repair_kits, account_label, fc_id, num_sub_slots)
                    VALUES
                        (@cid, @name, @world, @retainer_count, @submarine_count, @gil, @ceruleum, @repair_kits, @account_label, @fc_id, @num_sub_slots)";

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
                cmd.Parameters.AddWithValue("fc_id", fcId == 0 ? (object)DBNull.Value : (decimal)fcId);
                cmd.Parameters.AddWithValue("num_sub_slots", numSubSlots);

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
            public long CurrentExp;
            public long NextLevelExp;
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
                        (cid, sub_name, level, part1, part2, part3, part4, points, return_time, current_exp, next_level_exp, updated_at)
                    VALUES
                        (@cid, @sub_name, @level, @part1, @part2, @part3, @part4, @points, @return_time, @current_exp, @next_level_exp, now())";

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
                    insertCmd.Parameters.AddWithValue("current_exp", sub.CurrentExp);
                    insertCmd.Parameters.AddWithValue("next_level_exp", sub.NextLevelExp);

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
                cmd.Parameters.AddWithValue("fc_rank", housing.FcRank);
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

                return "Success.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }

        public static async Task<string> WriteRetainerLookupAsync(
            ulong retainerId, ulong ownerCid, string name,
            uint job, uint gil, bool hasVenture, uint ventureId,
            long ventureBeginsAt, long ventureEndsAt, int level, int? hireOrderIndex, bool useRemote)
        {
            var (connectionString, connError) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return connError!;

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO companion_retainer_lookup
                        (retainer_id, owner_cid, name, job, gil, has_venture, venture_id, venture_begins_at, venture_ends_at, level, hire_order_index, updated_at)
                    VALUES
                        (@retainer_id, @owner_cid, @name, @job, @gil, @has_venture, @venture_id, @venture_begins_at, @venture_ends_at, @level, @hire_order_index, now())
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

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("retainer_id", (decimal)retainerId);
                cmd.Parameters.AddWithValue("owner_cid", (decimal)ownerCid);
                cmd.Parameters.AddWithValue("name", name);
                cmd.Parameters.AddWithValue("job", (int)job);
                cmd.Parameters.AddWithValue("gil", (long)gil);
                cmd.Parameters.AddWithValue("has_venture", hasVenture);
                cmd.Parameters.AddWithValue("venture_id", (int)ventureId);
                cmd.Parameters.AddWithValue("venture_begins_at", ventureBeginsAt);
                cmd.Parameters.AddWithValue("venture_ends_at", ventureEndsAt);
                cmd.Parameters.AddWithValue("level", level);
                cmd.Parameters.AddWithValue("hire_order_index", (object?)hireOrderIndex ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync();

                return "Success.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }
        // One value to record in companion_metric_history (see sql/001_metric_history.sql).
        // SubjectType is "character" or "fc"; SubjectId is the character id or FC id.
        public class MetricPoint
        {
            public string SubjectType { get; set; } = string.Empty;
            public ulong SubjectId { get; set; }
            public string Metric { get; set; } = string.Empty;
            public decimal Value { get; set; }
            // Optional context stored with the value (e.g. the FC leader's name).
            public string? Label { get; set; }
        }

        // A metric is stored again only when its value changed, or when its last
        // stored row is older than this ("still the same" marker so a chart can tell
        // an unchanged value from a plugin that was not running).
        private const double MetricHeartbeatSeconds = 3600;

        // Records a batch of metrics in one round trip. Each (SubjectType, SubjectId,
        // Metric) must appear at most once per batch (the caller de-duplicates).
        public static async Task<string> WriteMetricsAsync(List<MetricPoint> metrics, bool useRemote)
        {
            if (metrics.Count == 0)
                return "Success — no metrics to write.";

            var (connectionString, connError) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return connError!;

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    WITH incoming AS (
                        SELECT subject_type, subject_id, metric, value, label
                        FROM unnest(@subject_types::text[], @subject_ids::numeric[], @metrics::text[], @vals::numeric[], @labels::text[])
                             AS t(subject_type, subject_id, metric, value, label)
                    ),
                    changed AS (
                        INSERT INTO companion_metric_latest AS l (subject_type, subject_id, metric, value, label, recorded_at)
                        SELECT subject_type, subject_id, metric, value, label, now() FROM incoming
                        ON CONFLICT (subject_type, subject_id, metric) DO UPDATE
                            SET value = EXCLUDED.value, label = EXCLUDED.label, recorded_at = EXCLUDED.recorded_at
                            WHERE l.value IS DISTINCT FROM EXCLUDED.value
                               OR l.label IS DISTINCT FROM EXCLUDED.label
                               OR l.recorded_at < now() - make_interval(secs => @heartbeat_seconds::double precision)
                        RETURNING subject_type, subject_id, metric, value, label, recorded_at
                    )
                    INSERT INTO companion_metric_history (recorded_at, subject_type, subject_id, metric, value, label)
                    SELECT recorded_at, subject_type, subject_id, metric, value, label FROM changed";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("subject_types", metrics.Select(m => m.SubjectType).ToArray());
                cmd.Parameters.AddWithValue("subject_ids", metrics.Select(m => (decimal)m.SubjectId).ToArray());
                cmd.Parameters.AddWithValue("metrics", metrics.Select(m => m.Metric).ToArray());
                cmd.Parameters.AddWithValue("vals", metrics.Select(m => m.Value).ToArray());
                cmd.Parameters.AddWithValue("labels", metrics.Select(m => m.Label).ToArray());
                cmd.Parameters.AddWithValue("heartbeat_seconds", MetricHeartbeatSeconds);

                var recorded = await cmd.ExecuteNonQueryAsync();

                return $"Success — {recorded} of {metrics.Count} metrics recorded.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }
    }
}
