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
