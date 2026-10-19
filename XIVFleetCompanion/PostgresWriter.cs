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
            return (BuildConnectionString(cred), null);
        }

        private static string BuildConnectionString(PostgresCredentialStore.PostgresCredential cred)
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = cred.Host,
                Port = cred.Port,
                Database = cred.Database,
                Username = cred.Username,
                Password = cred.Password,
                Timeout = 5,
                IncludeErrorDetail = true,
                ApplicationName = DatabaseMove.ApplicationName,
            };
            return builder.ConnectionString;
        }

        private static async Task<NpgsqlConnection> OpenAsync(PostgresCredentialStore.PostgresCredential cred)
        {
            var conn = new NpgsqlConnection(BuildConnectionString(cred));
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

        // An open connection for one sync, or null when no credential is saved. Connection
        // errors are thrown to the caller, which logs them and tries again next sync.
        // When the app has moved the database to another PC (DatabaseMove), this PC follows it: the new
        // address is tried with the same database, login and password, and saved once it answers. While a
        // move is copying the database, DatabaseMove.WaitException is thrown instead, and
        // DatabaseMove.UnreachableException when the new address doesn't answer from this PC.
        public static async Task<NpgsqlConnection?> OpenConnectionAsync(bool useRemote)
        {
            var cred = PostgresCredentialStore.Load(useRemote);
            if (cred == null)
                return null;

            NpgsqlConnection? conn = await OpenAsync(cred);
            try
            {
                // A database moved twice is followed twice; never more than that, in case two notes point at each other.
                for (var hop = 0; hop < 3; hop++)
                {
                    var note = await DatabaseMove.ReadAsync(conn!);
                    switch (DatabaseMove.Decide(note))
                    {
                        case DatabaseMove.Next.Sync:
                            var ready = conn;
                            conn = null;
                            return ready;
                        case DatabaseMove.Next.Wait:
                            throw new DatabaseMove.WaitException();
                    }

                    var moved = new PostgresCredentialStore.PostgresCredential
                    {
                        Host = note!.Host.Trim(),
                        Port = note.Port,
                        Database = cred.Database,
                        Username = cred.Username,
                        Password = cred.Password,
                    };
                    NpgsqlConnection next;
                    try
                    {
                        next = await OpenAsync(moved);
                    }
                    catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
                    {
                        throw new DatabaseMove.UnreachableException($"{moved.Host}:{moved.Port}", ex.Message);
                    }
                    PostgresCredentialStore.Save(useRemote, moved.Host, moved.Port, moved.Database, moved.Username, moved.Password);
                    Plugin.Log.Information($"Fleet Companion: the database moved from {cred.Host}:{cred.Port} to {moved.Host}:{moved.Port}. This PC now syncs there (saved in the plugin's settings).");
                    await conn!.DisposeAsync();
                    conn = next;
                    cred = moved;
                }
                var last = conn;
                conn = null;
                return last;
            }
            finally
            {
                if (conn != null) await conn.DisposeAsync();
            }
        }

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
