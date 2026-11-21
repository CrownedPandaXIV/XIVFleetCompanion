using Npgsql;
using System;
using System.Data.Common;
using System.Globalization;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Keeps Trends' history (companion_metric_history) from growing for ever. Each value is written again every hour
    /// even when it hasn't changed (MetricWriter's "still the same" repeats). Everything charted and every view reads
    /// "the last value at or before a time", so a repeat of the value just before it changes no figure. Every real change
    /// is kept for good, and so is the first value of each day; the other repeats go once they're over 30 days old.
    ///
    /// The history is worked through from its oldest row a week at a time (at most once an hour, from the sync), so a
    /// large backlog never makes one sync slow. How far it got is kept in fleet_economics_settings, so every PC carries on
    /// from the same place. The same on PostgreSQL and the fleet file.
    /// </summary>
    public static class HistoryThinning
    {
        public const string MarkerKey = "history_thinned_until";
        public const double KeepEverythingDays = 30;
        public const double StepDays = 7;
        private const double Day = 86400;

        /// <summary>One step at `now` (seconds since 1970): the repeats in the next week of history past the marker, if
        /// that week is over 30 days old. Returns how many rows went (0 when there's nothing old enough yet).</summary>
        public static async Task<int> StepAsync(DbConnection conn, double now)
        {
            var file = Db.IsFile(conn);
            await using var tx = await conn.BeginTransactionAsync();
            var from = await ReadMarker(conn, tx, file) ?? await Oldest(conn, tx, file);
            if (from == null) return 0;
            var start = Math.Floor(from.Value / Day) * Day;
            var cutoff = now - KeepEverythingDays * Day;
            // Whole days, so each day's first value is seen with the rest of its day.
            var end = Math.Floor(Math.Min(start + StepDays * Day, cutoff) / Day) * Day;
            if (end <= start) return 0;

            var removed = await Db.Exec(conn, file ? FileSql : PgSql, tx, ("from", start), ("to", end));
            await Db.Exec(conn, file
                    ? "INSERT INTO fleet_economics_settings (setting_key, setting_value, updated_at) VALUES (@key, @value, @now) " +
                      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = excluded.setting_value, updated_at = excluded.updated_at"
                    : "INSERT INTO fleet_economics_settings (setting_key, setting_value, updated_at) VALUES (@key, @value, now()) " +
                      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value, updated_at = now()",
                tx, ("key", MarkerKey), ("value", file ? end : (object)(decimal)end), ("now", now));
            await tx.CommitAsync();
            return removed;
        }

        private static async Task<double?> ReadMarker(DbConnection conn, DbTransaction tx, bool file)
        {
            await using var cmd = Db.Cmd("SELECT setting_value FROM fleet_economics_settings WHERE setting_key = @key", conn, tx);
            Db.Add(cmd, "key", MarkerKey);
            var v = await cmd.ExecuteScalarAsync();
            return v is null or DBNull ? null : Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        private static async Task<double?> Oldest(DbConnection conn, DbTransaction tx, bool file)
        {
            await using var cmd = Db.Cmd(file ? "SELECT min(recorded_at) FROM companion_metric_history"
                : "SELECT extract(epoch FROM min(recorded_at))::float8 FROM companion_metric_history", conn, tx);
            var v = await cmd.ExecuteScalarAsync();
            return v is null or DBNull ? null : Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        // The rows to go: within [from, to), the same value and label as the row before in its series, and not the first
        // of its day (UTC). A series' first row in the range has nothing before it here, so it stays.
        private const string PgSql = @"
            DELETE FROM companion_metric_history h
            USING (
              SELECT row_id FROM (
                SELECT ctid AS row_id, value, label,
                       lag(value) OVER series AS prev_value, lag(label) OVER series AS prev_label,
                       row_number() OVER (PARTITION BY subject_type, subject_id, metric, floor(extract(epoch FROM recorded_at) / 86400)
                                          ORDER BY recorded_at) AS nth
                FROM companion_metric_history
                WHERE metric IN (SELECT DISTINCT metric FROM companion_metric_latest)
                  AND recorded_at >= to_timestamp(@from) AND recorded_at < to_timestamp(@to)
                WINDOW series AS (PARTITION BY subject_type, subject_id, metric ORDER BY recorded_at)) x
              WHERE nth > 1 AND value = prev_value AND label IS NOT DISTINCT FROM prev_label) d
            WHERE h.ctid = d.row_id";

        private const string FileSql = @"
            DELETE FROM companion_metric_history WHERE rowid IN (
              SELECT row_id FROM (
                SELECT rowid AS row_id, value, label,
                       lag(value) OVER series AS prev_value, lag(label) OVER series AS prev_label,
                       row_number() OVER (PARTITION BY subject_type, subject_id, metric, CAST(recorded_at / 86400 AS INTEGER)
                                          ORDER BY recorded_at) AS nth
                FROM companion_metric_history
                WHERE metric IN (SELECT DISTINCT metric FROM companion_metric_latest)
                  AND recorded_at >= @from AND recorded_at < @to
                WINDOW series AS (PARTITION BY subject_type, subject_id, metric ORDER BY recorded_at)) x
              WHERE nth > 1 AND value = prev_value AND label IS prev_label)";
    }
}
