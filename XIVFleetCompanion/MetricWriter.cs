using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>The values charted on the Trends page (companion_metric_history), on either database (see Db).</summary>
    public static class MetricWriter
    {
        // One value to record in companion_metric_history (see database version 1).
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
        public static async Task<string> WriteMetricsAsync(System.Data.Common.DbConnection db, List<MetricPoint> metrics)
        {
            if (metrics.Count == 0)
                return "Success — no metrics to write.";
            if (Db.IsFile(db)) return await FileWriteMetricsAsync(db, metrics);
            var conn = (NpgsqlConnection)db;

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

        // The one-file database's version (Just this PC): the same rule a metric at a time. In the file a character's id is
        // a whole number and a Free Company's text (see the app's db/sqlite/tables.sql).
        private static async Task<string> FileWriteMetricsAsync(System.Data.Common.DbConnection conn, List<MetricPoint> metrics)
        {
            try
            {
                var now = Db.Now();
                var recorded = 0;
                await using var tx = await conn.BeginTransactionAsync();
                foreach (var m in metrics)
                {
                    object subject = m.SubjectType == "fc" ? Db.FcText(m.SubjectId)! : m.SubjectId;
                    var changed = await Db.Exec(conn, @"
                        INSERT INTO companion_metric_latest (subject_type, subject_id, metric, value, label, recorded_at)
                        VALUES (@type, @id, @metric, @value, @label, @now)
                        ON CONFLICT (subject_type, subject_id, metric) DO UPDATE
                            SET value = excluded.value, label = excluded.label, recorded_at = excluded.recorded_at
                            WHERE companion_metric_latest.value IS NOT excluded.value
                               OR companion_metric_latest.label IS NOT excluded.label
                               OR companion_metric_latest.recorded_at < @now - @heartbeat", tx,
                        ("type", m.SubjectType), ("id", subject), ("metric", m.Metric), ("value", (double)m.Value), ("label", m.Label),
                        ("now", now), ("heartbeat", MetricHeartbeatSeconds));
                    if (changed == 0) continue;
                    recorded += await Db.Exec(conn, @"
                        INSERT INTO companion_metric_history (recorded_at, subject_type, subject_id, metric, value, label)
                        VALUES (@now, @type, @id, @metric, @value, @label)", tx,
                        ("type", m.SubjectType), ("id", subject), ("metric", m.Metric), ("value", (double)m.Value), ("label", m.Label), ("now", now));
                }
                await tx.CommitAsync();
                return $"Success — {recorded} of {metrics.Count} metrics recorded.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }
    }
}
