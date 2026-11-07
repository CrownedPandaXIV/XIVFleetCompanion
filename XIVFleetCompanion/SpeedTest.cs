using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Npgsql;

namespace XIVFleetCompanion
{
    /// <summary>One thing the speed test timed: a query (or the connection), over every round.</summary>
    public sealed record SpeedResult(string Name, int Rows, double FirstMs, double BestMs, double WorstMs, string? Error);

    /// <summary>
    /// 2.0 stage 1 (/xivfleet speedtest): times reading from the database what the in-game Summary, Subs and
    /// Supplies windows will need, so we know it's quick enough before building them. Only reads, never writes.
    /// Runs on a background thread, never on the game's. No Dalamud here, so the writer tests run it too.
    /// </summary>
    public static class SpeedTest
    {
        public const int Rounds = 3;
        public const string ConnectName = "Connect";
        public const string TotalName = "Everything (one round)";

        /// <summary>What the windows will read: first roughly as the app reads it now, then the ready-made views
        /// (a database the app hasn't updated to 1.8.0 yet reports those as failed).</summary>
        public static readonly IReadOnlyList<(string Name, string Sql)> Queries = new[]
        {
            ("Characters", @"SELECT cid, name, world, account_label, gil, ceruleum, repair_kits, fc_id, last_synced_at
                FROM companion_character_current"),
            ("Subs", @"SELECT s.cid, s.sub_name, s.level, s.part1, s.part2, s.part3, s.part4, s.return_time,
                    s.current_exp, s.next_level_exp, c.name, c.world, c.account_label
                FROM companion_submarine_snapshot s LEFT JOIN companion_character_current c ON c.cid = s.cid
                WHERE s.level > 0"),
            ("Retainers", @"SELECT retainer_id, owner_cid, name, job, level, gil, has_venture, venture_ends_at
                FROM companion_retainer_lookup"),
            ("FC chests (ceruleum, repair kits)", @"SELECT fc_id, item_id, sum(quantity) AS quantity
                FROM companion_fc_inventory_snapshot WHERE item_id IN (10155, 10373) GROUP BY fc_id, item_id"),
            ("Supplies used (last 14 days)", @"SELECT subject_id, metric, value, recorded_at FROM companion_metric_history
                WHERE metric IN ('item_qty:10155', 'item_qty:10373') AND recorded_at > now() - interval '14 days'"),
            ("PCs", @"SELECT pc_name, account_label, plugin_version, last_sync_at, characters_synced
                FROM companion_pc_status"),
            // Stage 2: the ready-made views (app 1.8.0, database update 14), which work things out in the database.
            ("Subs (view)", "SELECT * FROM fleet_subs_view"),
            ("Supplies (view)", "SELECT * FROM fleet_supplies_view"),
            ("Summary (view)", "SELECT * FROM fleet_summary_view"),
        };

        /// <summary>
        /// Opens a connection and runs every query, <see cref="Rounds"/> times. The first round is the slowest
        /// (nothing cached yet); the best shows what a window refreshing every so often would see. A query that
        /// fails (an older database without its table) is reported and the rest still run. Throws only when it
        /// can't connect at all.
        /// </summary>
        public static async Task<List<SpeedResult>> RunAsync(string connectionString, int rounds = Rounds)
        {
            var times = new Dictionary<string, List<double>>();
            var rows = new Dictionary<string, int>();
            var errors = new Dictionary<string, string>();
            void Add(string name, double ms) => (times.TryGetValue(name, out var list) ? list : times[name] = new List<double>()).Add(ms);

            for (var round = 0; round < rounds; round++)
            {
                var total = Stopwatch.StartNew();
                var watch = Stopwatch.StartNew();
                // A new connection each round, as a window would open when it refreshes (Npgsql keeps it pooled
                // after the first, so later rounds show the pooled cost).
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();
                Add(ConnectName, watch.Elapsed.TotalMilliseconds);

                foreach (var (name, sql) in Queries)
                {
                    if (errors.ContainsKey(name)) continue;
                    watch.Restart();
                    try
                    {
                        await using var cmd = new NpgsqlCommand(sql, conn);
                        await using var reader = await cmd.ExecuteReaderAsync();
                        var count = 0;
                        var values = new object[reader.FieldCount];
                        while (await reader.ReadAsync())
                        {
                            reader.GetValues(values); // read every value, as a window would
                            count++;
                        }
                        Add(name, watch.Elapsed.TotalMilliseconds);
                        rows[name] = count;
                    }
                    catch (PostgresException ex)
                    {
                        errors[name] = ex.MessageText;
                    }
                }
                Add(TotalName, total.Elapsed.TotalMilliseconds);
            }

            var names = new[] { ConnectName }.Concat(Queries.Select(q => q.Name)).Append(TotalName);
            return names.Select(name =>
            {
                var list = times.TryGetValue(name, out var l) ? l : new List<double>();
                return new SpeedResult(name, rows.TryGetValue(name, out var r) ? r : 0,
                    list.Count > 0 ? list[0] : 0, list.Count > 0 ? list.Min() : 0, list.Count > 0 ? list.Max() : 0,
                    errors.TryGetValue(name, out var e) ? e : null);
            }).ToList();
        }

        private static string Ms(double ms) => ms.ToString(ms < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " ms";

        /// <summary>One line per result, for the window and to copy and share.</summary>
        public static string ToText(IEnumerable<SpeedResult> results, string where, double? longestFrameMs = null, double? longestFrameBeforeMs = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"XIV Fleet Companion {where}: speed test ({Rounds} rounds; first / best / worst)");
            foreach (var r in results)
            {
                if (r.Error != null)
                {
                    sb.AppendLine($"{r.Name}: failed ({r.Error})");
                    continue;
                }
                var rowText = r.Name is ConnectName or TotalName ? "" : $" ({r.Rows} row{(r.Rows == 1 ? "" : "s")})";
                sb.AppendLine($"{r.Name}{rowText}: {Ms(r.FirstMs)} / {Ms(r.BestMs)} / {Ms(r.WorstMs)}");
            }
            if (longestFrameMs.HasValue)
                sb.AppendLine($"Longest game frame while testing: {Ms(longestFrameMs.Value)}" +
                    (longestFrameBeforeMs.HasValue ? $" (before: {Ms(longestFrameBeforeMs.Value)})" : ""));
            return sb.ToString().TrimEnd();
        }
    }
}
