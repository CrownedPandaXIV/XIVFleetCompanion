using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    public static class PostgresWriter
    {
        // Built with NpgsqlConnectionStringBuilder so a password (or any other value)
        // containing ';' or '=' cannot break or change the connection settings.
        internal static (string? connectionString, string? error) BuildConnectionString(bool useRemote)
        {
            var cred = PostgresCredentialStore.Load(useRemote);
            if (cred == null)
                return (null, "Not configured — no saved credential found.");

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = cred.Host,
                Port = cred.Port,
                Database = cred.Database,
                Username = cred.Username,
                Password = cred.Password,
                Timeout = 5,
                IncludeErrorDetail = true,
            };

            return (builder.ConnectionString, null);
        }

        // An open connection for one sync, or null when no credential is saved. Connection
        // errors are thrown to the caller, which logs them and tries again next sync.
        public static async Task<NpgsqlConnection?> OpenConnectionAsync(bool useRemote)
        {
            var (connectionString, _) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return null;

            var conn = new NpgsqlConnection(connectionString);
            try
            {
                await conn.OpenAsync();
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
            return conn;
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
        public static async Task<string> WriteMetricsAsync(NpgsqlConnection conn, List<MetricPoint> metrics)
        {
            if (metrics.Count == 0)
                return "Success — no metrics to write.";

            try
            {
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
