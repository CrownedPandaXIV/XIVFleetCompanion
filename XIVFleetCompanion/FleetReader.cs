using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Npgsql;

namespace XIVFleetCompanion
{
    /// <summary>One built sub, from fleet_subs_view.</summary>
    public sealed record SubRow(string Character, string World, string Account, int SortIndex, string SubName, int Slot,
        int Rank, string Build, long ReturnTime, long CurrentExp, long NextLevelExp);

    /// <summary>One character with subs, from fleet_supplies_view. PerDay null: not enough history yet; 0: none used.</summary>
    public sealed record SupplyRow(string Character, string World, string Account, int SortIndex, DateTime? LastSynced,
        int Ceruleum, double? CeruleumPerDay, double? CeruleumDaysLeft, int CeruleumRestock,
        int RepairKits, double? RepairKitsPerDay, double? RepairKitsDaysLeft, int RepairKitsRestock, int WindowDays, int TargetDays);

    /// <summary>The fleet at a glance, from fleet_summary_view (the subs' own counts are worked out live from SubRow).</summary>
    public sealed record SummaryRow(int Retainers, int VenturesDone, int RetainersIdle, int LowCeruleum, int LowRepairKits, double? SoonestRunsOutDays);

    /// <summary>What was last read, when, and why the last read failed (null when it didn't).</summary>
    public sealed record FleetRead<T>(T? Data, DateTime? At, string? Error) where T : class;

    /// <summary>
    /// 2.0: reads the ready-made fleet views (app 1.8.0 and later, db/views.sql) for the main window's tabs, in the
    /// background and never while drawing. Subs every 30 seconds (cheap), supplies and the summary every 5 minutes
    /// (they change slowly and take the database longer). Only while the window asks: nothing is read while it's
    /// closed. No Dalamud here.
    /// </summary>
    public sealed class FleetReader
    {
        public static readonly TimeSpan SubsEvery = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan SuppliesEvery = TimeSpan.FromMinutes(5);
        public const string MissingViewsText =
            "The database doesn't have the fleet views yet. Update the XIV Fleet Companion app to 1.8.0 or later and open it once; it adds them.";

        private readonly Func<string?> connectionString;
        private volatile bool busy;
        private DateTime subsAsked = DateTime.MinValue;
        private DateTime suppliesAsked = DateTime.MinValue;

        public FleetRead<List<SubRow>> Subs { get; private set; } = new(null, null, null);
        public FleetRead<List<SupplyRow>> Supplies { get; private set; } = new(null, null, null);
        public FleetRead<SummaryRow> Summary { get; private set; } = new(null, null, null);
        public bool Busy => busy;

        /// <param name="connectionString">The database to read (null when none is set up), asked each time.</param>
        public FleetReader(Func<string?> connectionString) => this.connectionString = connectionString;

        /// <summary>Called by the window while it's open: starts a read in the background when one is due.</summary>
        public void ReadIfDue(bool everything = false)
        {
            if (busy) return;
            var now = DateTime.UtcNow;
            var subs = everything || now - subsAsked >= SubsEvery;
            var supplies = everything || now - suppliesAsked >= SuppliesEvery;
            if (!subs && !supplies) return;
            var cs = connectionString();
            if (cs == null)
            {
                const string notSetUp = "The database isn't set up yet (Settings tab).";
                Subs = Subs with { Error = notSetUp };
                Supplies = Supplies with { Error = notSetUp };
                Summary = Summary with { Error = notSetUp };
                return;
            }
            busy = true;
            if (subs) subsAsked = now;
            if (supplies) suppliesAsked = now;
            Task.Run(async () =>
            {
                try
                {
                    await using var conn = new NpgsqlConnection(cs);
                    await conn.OpenAsync();
                    if (subs) Subs = await Read(conn, ReadSubs, Subs);
                    if (supplies)
                    {
                        Supplies = await Read(conn, ReadSupplies, Supplies);
                        Summary = await Read(conn, ReadSummary, Summary);
                    }
                }
                catch (Exception ex)
                {
                    var text = $"Could not reach the database: {ex.Message}";
                    if (subs) Subs = Subs with { Error = text };
                    if (supplies) { Supplies = Supplies with { Error = text }; Summary = Summary with { Error = text }; }
                }
                finally
                {
                    busy = false;
                }
            });
        }

        // A failed read keeps what was read before, with the reason.
        private static async Task<FleetRead<T>> Read<T>(NpgsqlConnection conn, Func<NpgsqlConnection, Task<T>> read, FleetRead<T> before) where T : class
        {
            try
            {
                return new FleetRead<T>(await read(conn), DateTime.Now, null);
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
            {
                return before with { Error = MissingViewsText };
            }
            catch (PostgresException ex)
            {
                return before with { Error = ex.MessageText };
            }
        }

        // Values of whatever numeric type the view gives, and nulls.
        private static string Text(NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? ""; }
        private static long Long(NpgsqlDataReader r, string col, long fallback = 0) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? fallback : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static int Int(NpgsqlDataReader r, string col, int fallback = 0) => (int)Long(r, col, fallback);
        private static double? Double(NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static DateTime? Time(NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetFieldValue<DateTime>(i).ToLocalTime(); }

        public static async Task<List<SubRow>> ReadSubs(NpgsqlConnection conn)
        {
            var list = new List<SubRow>();
            await using var cmd = new NpgsqlCommand(
                @"SELECT character_name, world, account_label, sort_index, sub_name, slot, rank, build, return_time, current_exp, next_level_exp
                  FROM fleet_subs_view ORDER BY account_label NULLS LAST, sort_index NULLS LAST, character_name, slot NULLS LAST, sub_name", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SubRow(Text(r, "character_name"), Text(r, "world"), Text(r, "account_label"), Int(r, "sort_index", int.MaxValue),
                    Text(r, "sub_name"), Int(r, "slot", 9), Int(r, "rank"), Text(r, "build"), Long(r, "return_time"),
                    Long(r, "current_exp"), Long(r, "next_level_exp")));
            return list;
        }

        public static async Task<List<SupplyRow>> ReadSupplies(NpgsqlConnection conn)
        {
            var list = new List<SupplyRow>();
            await using var cmd = new NpgsqlCommand(
                "SELECT * FROM fleet_supplies_view ORDER BY account_label NULLS LAST, sort_index NULLS LAST, name", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SupplyRow(Text(r, "name"), Text(r, "world"), Text(r, "account_label"), Int(r, "sort_index", int.MaxValue), Time(r, "last_synced_at"),
                    Int(r, "ceruleum"), Double(r, "ceruleum_per_day"), Double(r, "ceruleum_days_left"), Int(r, "ceruleum_restock"),
                    Int(r, "repair_kits"), Double(r, "repair_kits_per_day"), Double(r, "repair_kits_days_left"), Int(r, "repair_kits_restock"),
                    Int(r, "window_days", 14), Int(r, "target_days", 14)));
            return list;
        }

        public static async Task<SummaryRow> ReadSummary(NpgsqlConnection conn)
        {
            await using var cmd = new NpgsqlCommand("SELECT * FROM fleet_summary_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return new SummaryRow(0, 0, 0, 0, 0, null);
            return new SummaryRow(Int(r, "retainers"), Int(r, "ventures_done"), Int(r, "retainers_idle"),
                Int(r, "low_ceruleum"), Int(r, "low_repair_kits"), Double(r, "soonest_runs_out_days"));
        }

        /// <summary>Where a sub is right now: "In port", "Back" (waiting to be collected) or the time until it's back.</summary>
        public static string SubStatus(long returnTime, long nowUnix)
        {
            if (returnTime <= 0) return "In port";
            if (returnTime <= nowUnix) return "Back";
            return Countdown(returnTime - nowUnix);
        }

        /// <summary>"45:10", "3:05:09" or "2d 4:00:00".</summary>
        public static string Countdown(long seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}:{t.Minutes:00}:{t.Seconds:00}";
            return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        /// <summary>"12s ago", "4m ago", "2h ago".</summary>
        public static string Ago(DateTime at, DateTime now)
        {
            var s = Math.Max(0, (now - at).TotalSeconds);
            return s < 60 ? $"{(int)s}s ago" : s < 3600 ? $"{(int)(s / 60)}m ago" : $"{(int)(s / 3600)}h ago";
        }
    }
}
