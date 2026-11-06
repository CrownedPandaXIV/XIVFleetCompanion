using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>One thing that needs attention, from fleet_alerts_view: Level "danger" or "warn", Kind (house,
    /// low_ceruleum, low_repair_kits, not_synced, autoretainer), who it's about and what.</summary>
    public sealed record AlertRow(string Level, string Kind, string Subject, string Detail);

    /// <summary>One character, from fleet_roster_view. BehindMinutes: how far behind its account when the plugin no longer
    /// syncs it, else null. House null: none.</summary>
    public sealed record RosterRow(long Cid, string Character, string World, string Account, int SortIndex, string? FcName, int? FcPoints, string? House,
        long Gil, int? Ceruleum, int? RepairKits, int Retainers, int Submarines, DateTime? LastSynced,
        string? LoginPc, string? LoginLabel, DateTime? LoggedInAt, int? BehindMinutes, bool LowCeruleum, bool LowRepairKits);

    /// <summary>One retainer, from fleet_retainers_view. Job and VentureId are the game's numbers (ClassJob, RetainerTask),
    /// named in the window from the game's own files; VentureEndsAt in Unix seconds.</summary>
    public sealed record RetainerRow(long RetainerId, string Owner, string World, string Account, int SortIndex, string Name,
        int Job, int? Level, long? Gil, bool HasVenture, int VentureId, long VentureEndsAt, int HireOrder,
        string? TargetMode, int? TargetValue, bool OnlyCrafted, bool ItemsSeen);

    /// <summary>The retainers and the items each wears (fleet_retainer_gear_view: retainer, item number).</summary>
    public sealed record RetainersRead(List<RetainerRow> Retainers, Dictionary<long, List<int>> Gear);

    /// <summary>One setup of the plugin (PC and account label), from fleet_pcs_view. Problems: what it couldn't see.</summary>
    public sealed record PcRow(string Pc, string Account, string? PluginVersion, DateTime? LastSync, bool? AutoRetainer, bool? AllaganTools,
        bool? FcTracker, bool? SubmarineTracker, bool? VentureLog, int? Characters, int? LeftOut, int? Elsewhere,
        List<string> Problems, string? CheckText, int? CheckToFix, DateTime? CheckAt);

    /// <summary>The retainers with a class right now, from fleet_retainer_counts_view (as the app's Summary).</summary>
    public sealed record RetainerCountsRow(int Retainers, int VenturesDone, int Away, int Idle);

    /// <summary>One Free Company, from fleet_fcs_view. HomeWorld: a World number, named from the game's files.
    /// HouseLevel: "ok", "warn", "danger" or "unknown" (null without a house). Eligibility: when it can bid, without a house.</summary>
    public sealed record FcRow(long FcId, string Name, string? Leader, int? Points, int? Members, bool HasHouse, string? House,
        int HomeWorld, int? HouseDays, string? HouseLevel, int? DemolitionInDays, DateTime? LastEntered, string? Eligibility,
        DateTime? EligibleAt, string Account, int SortIndex);

    /// <summary>One stack of an item, from fleet_holdings_view. Kind: "character", "retainer" or "fc". ItemId is the plain
    /// item (Hq: high quality). OwnerCid: the character, or for an FC chest the member it's counted on. Money: gil, not items.</summary>
    public sealed record HoldingRow(string Kind, long? OwnerCid, long? FcId, int Container, int ItemId, bool Hq, long Quantity,
        string Who, string World, string? OwnerName, string Account, int SortIndex, string? RetainerName, int HireOrder,
        string? ContainerName, bool Money, string Place);

    /// <summary>How many of an item the fleet holds, from fleet_item_totals_view (high quality apart).</summary>
    public sealed record ItemTotalRow(int ItemId, bool Hq, long Total);

    /// <summary>What was last read, when, and why the last read failed (null when it didn't).</summary>
    public sealed record FleetRead<T>(T? Data, DateTime? At, string? Error) where T : class;

    /// <summary>
    /// 2.0: reads the ready-made fleet views (app 1.8.0 and later, db/views.sql) for the main window's tabs, in the
    /// background and never while drawing. Subs and what needs attention every 30 seconds (cheap), supplies and the
    /// summary every 5 minutes (they change slowly and take the database longer). Only while something asks (the main
    /// window while it's open, the info bar line while it's shown). No Dalamud here.
    /// </summary>
    public sealed class FleetReader
    {
        public static readonly TimeSpan SubsEvery = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan SuppliesEvery = TimeSpan.FromMinutes(5);
        /// <summary>The Roster, Retainers and PCs: every minute, and only while one of them is shown.</summary>
        public static readonly TimeSpan ListsEvery = TimeSpan.FromMinutes(1);
        public const string MissingViewsText =
            "The database doesn't have the fleet views yet. Update the XIV Fleet Companion app to 1.8.0 or later and open it once; it adds them.";

        private readonly Func<string?> connectionString;
        private volatile bool busy;
        private DateTime subsAsked = DateTime.MinValue;
        private DateTime suppliesAsked = DateTime.MinValue;
        private DateTime listsAsked = DateTime.MinValue;

        public FleetRead<List<SubRow>> Subs { get; private set; } = new(null, null, null);
        public FleetRead<List<SupplyRow>> Supplies { get; private set; } = new(null, null, null);
        public FleetRead<SummaryRow> Summary { get; private set; } = new(null, null, null);
        public FleetRead<List<AlertRow>> Alerts { get; private set; } = new(null, null, null);
        public FleetRead<List<RosterRow>> Roster { get; private set; } = new(null, null, null);
        public FleetRead<RetainersRead> Retainers { get; private set; } = new(null, null, null);
        public FleetRead<List<PcRow>> Pcs { get; private set; } = new(null, null, null);
        public FleetRead<RetainerCountsRow> RetainerCounts { get; private set; } = new(null, null, null);
        public FleetRead<List<FcRow>> Fcs { get; private set; } = new(null, null, null);
        public bool Busy => busy;

        /// <param name="connectionString">The database to read (null when none is set up), asked each time.</param>
        public FleetReader(Func<string?> connectionString) => this.connectionString = connectionString;

        /// <summary>
        /// Called while something shows the fleet: starts a read in the background when one is due. withSupplies:
        /// false for the info bar line, which only needs the subs and what needs attention. withLists: true while the
        /// Roster, Retainers or PCs page is shown (everything reads them too).
        /// </summary>
        public void ReadIfDue(bool everything = false, bool withSupplies = true, bool withLists = false)
        {
            if (busy) return;
            var now = DateTime.UtcNow;
            var subs = everything || now - subsAsked >= SubsEvery;
            var supplies = everything || (withSupplies && now - suppliesAsked >= SuppliesEvery);
            var lists = everything || (withLists && now - listsAsked >= ListsEvery);
            if (!subs && !supplies && !lists) return;
            var cs = connectionString();
            if (cs == null)
            {
                const string notSetUp = "The database isn't set up yet (Settings tab).";
                Subs = Subs with { Error = notSetUp };
                Alerts = Alerts with { Error = notSetUp };
                Supplies = Supplies with { Error = notSetUp };
                Summary = Summary with { Error = notSetUp };
                Roster = Roster with { Error = notSetUp };
                Retainers = Retainers with { Error = notSetUp };
                Pcs = Pcs with { Error = notSetUp };
                RetainerCounts = RetainerCounts with { Error = notSetUp };
                Fcs = Fcs with { Error = notSetUp };
                return;
            }
            busy = true;
            if (subs) subsAsked = now;
            if (supplies) suppliesAsked = now;
            if (lists) listsAsked = now;
            Task.Run(async () =>
            {
                try
                {
                    await using var conn = new NpgsqlConnection(cs);
                    await conn.OpenAsync();
                    if (subs)
                    {
                        Subs = await Read(conn, ReadSubs, Subs);
                        Alerts = await Read(conn, ReadAlerts, Alerts);
                        RetainerCounts = await Read(conn, ReadRetainerCounts, RetainerCounts);
                    }
                    if (supplies)
                    {
                        Supplies = await Read(conn, ReadSupplies, Supplies);
                        Summary = await Read(conn, ReadSummary, Summary);
                    }
                    if (lists)
                    {
                        Roster = await Read(conn, ReadRoster, Roster);
                        Retainers = await Read(conn, ReadRetainers, Retainers);
                        Pcs = await Read(conn, ReadPcs, Pcs);
                        Fcs = await Read(conn, ReadFcs, Fcs);
                    }
                }
                catch (Exception ex)
                {
                    var text = $"Could not reach the database: {ex.Message}";
                    if (subs) { Subs = Subs with { Error = text }; Alerts = Alerts with { Error = text }; RetainerCounts = RetainerCounts with { Error = text }; }
                    if (supplies) { Supplies = Supplies with { Error = text }; Summary = Summary with { Error = text }; }
                    if (lists) { Roster = Roster with { Error = text }; Retainers = Retainers with { Error = text }; Pcs = Pcs with { Error = text }; Fcs = Fcs with { Error = text }; }
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
        private static string? TextOrNull(NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static long? LongOrNull(NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static int? IntOrNull(NpgsqlDataReader r, string col) => (int?)LongOrNull(r, col);
        private static bool? BoolOrNull(NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetBoolean(i); }
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

        public static async Task<List<AlertRow>> ReadAlerts(NpgsqlConnection conn)
        {
            var list = new List<AlertRow>();
            await using var cmd = new NpgsqlCommand("SELECT level, kind, subject, detail FROM fleet_alerts_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new AlertRow(Text(r, "level"), Text(r, "kind"), Text(r, "subject"), Text(r, "detail")));
            return list;
        }

        public static async Task<List<RosterRow>> ReadRoster(NpgsqlConnection conn)
        {
            var list = new List<RosterRow>();
            await using var cmd = new NpgsqlCommand(
                "SELECT * FROM fleet_roster_view ORDER BY account_label NULLS LAST, sort_index NULLS LAST, name", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new RosterRow(Long(r, "cid"), Text(r, "name"), Text(r, "world"), Text(r, "account_label"), Int(r, "sort_index", int.MaxValue),
                    TextOrNull(r, "fc_name"), IntOrNull(r, "fc_points"), TextOrNull(r, "house"),
                    Long(r, "gil"), IntOrNull(r, "ceruleum"), IntOrNull(r, "repair_kits"), Int(r, "retainer_count"), Int(r, "submarine_count"),
                    Time(r, "last_synced_at"), TextOrNull(r, "login_pc"), TextOrNull(r, "login_label"), Time(r, "logged_in_at"),
                    IntOrNull(r, "behind_minutes"), BoolOrNull(r, "low_ceruleum") == true, BoolOrNull(r, "low_repair_kits") == true));
            return FleetOrder.Roster(list);
        }

        public static async Task<RetainersRead> ReadRetainers(NpgsqlConnection conn)
        {
            var list = new List<RetainerRow>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT * FROM fleet_retainers_view ORDER BY account_label NULLS LAST, sort_index NULLS LAST, owner_name, hire_order_index NULLS LAST, retainer_name", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                    list.Add(new RetainerRow(Long(r, "retainer_id"), Text(r, "owner_name"), Text(r, "world"), Text(r, "account_label"), Int(r, "sort_index", int.MaxValue),
                        Text(r, "retainer_name"), Int(r, "job"), IntOrNull(r, "level"), LongOrNull(r, "gil"), BoolOrNull(r, "has_venture") == true,
                        Int(r, "venture_id"), Long(r, "venture_ends_at"), Int(r, "hire_order_index", int.MaxValue),
                        TextOrNull(r, "target_mode"), IntOrNull(r, "target_value"), BoolOrNull(r, "only_crafted") == true, BoolOrNull(r, "items_seen") == true));
            }
            var gear = new Dictionary<long, List<int>>();
            await using (var cmd = new NpgsqlCommand("SELECT retainer_id, item_id FROM fleet_retainer_gear_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    var id = Long(r, "retainer_id");
                    if (!gear.TryGetValue(id, out var items)) gear[id] = items = new List<int>();
                    items.Add(Int(r, "item_id"));
                }
            }
            return new RetainersRead(FleetOrder.Retainers(list), gear);
        }

        public static async Task<List<PcRow>> ReadPcs(NpgsqlConnection conn)
        {
            var list = new List<PcRow>();
            await using var cmd = new NpgsqlCommand("SELECT * FROM fleet_pcs_view ORDER BY account_label, pc_name", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new PcRow(Text(r, "pc_name"), Text(r, "account_label"), TextOrNull(r, "plugin_version"), Time(r, "last_sync_at"),
                    BoolOrNull(r, "autoretainer_ready"), BoolOrNull(r, "allagantools_ready"), BoolOrNull(r, "fctracker_found"),
                    BoolOrNull(r, "submarinetracker_found"), BoolOrNull(r, "venture_stats_found"),
                    IntOrNull(r, "characters_synced"), IntOrNull(r, "characters_left_out"), IntOrNull(r, "characters_elsewhere"),
                    Text(r, "problems").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    TextOrNull(r, "check_text"), IntOrNull(r, "check_to_fix"), Time(r, "check_at")));
            return list;
        }

        public static async Task<RetainerCountsRow> ReadRetainerCounts(NpgsqlConnection conn)
        {
            await using var cmd = new NpgsqlCommand("SELECT * FROM fleet_retainer_counts_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return new RetainerCountsRow(0, 0, 0, 0);
            return new RetainerCountsRow(Int(r, "retainers"), Int(r, "ventures_done"), Int(r, "retainers_away"), Int(r, "retainers_idle"));
        }

        public static async Task<List<FcRow>> ReadFcs(NpgsqlConnection conn)
        {
            var list = new List<FcRow>();
            await using var cmd = new NpgsqlCommand("SELECT * FROM fleet_fcs_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FcRow(Long(r, "fc_id"), Text(r, "fc_name"), TextOrNull(r, "fc_master"), IntOrNull(r, "fc_points"), IntOrNull(r, "total_members"),
                    BoolOrNull(r, "has_house") == true, TextOrNull(r, "house"), Int(r, "home_world_id"), IntOrNull(r, "house_days"), TextOrNull(r, "house_level"),
                    IntOrNull(r, "demolition_in_days"), Time(r, "house_last_visited"), TextOrNull(r, "eligibility"), Time(r, "eligible_at"),
                    Text(r, "account_label"), Int(r, "sort_index", int.MaxValue)));
            return FleetOrder.Fcs(list);
        }

        /// <summary>The stacks matching a condition on fleet_holdings_view (for example "owner_cid = ANY(@cids)"), with its
        /// parameters added by addParameters.</summary>
        public static async Task<List<HoldingRow>> ReadHoldings(NpgsqlConnection conn, string where, Action<NpgsqlParameterCollection> addParameters)
        {
            var list = new List<HoldingRow>();
            await using var cmd = new NpgsqlCommand($"SELECT * FROM fleet_holdings_view WHERE {where}", conn);
            addParameters(cmd.Parameters);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new HoldingRow(Text(r, "kind"), LongOrNull(r, "owner_cid"), LongOrNull(r, "fc_id"), Int(r, "container"), Int(r, "item_id"),
                    BoolOrNull(r, "hq") == true, Long(r, "quantity"), Text(r, "who"), Text(r, "world"), TextOrNull(r, "owner_name"), Text(r, "account_label"),
                    Int(r, "sort_index", int.MaxValue), TextOrNull(r, "retainer_name"), Int(r, "hire_order_index", int.MaxValue), TextOrNull(r, "container_name"),
                    BoolOrNull(r, "money") == true, Text(r, "place")));
            return list;
        }

        public static async Task<List<ItemTotalRow>> ReadItemTotals(NpgsqlConnection conn)
        {
            var list = new List<ItemTotalRow>();
            await using var cmd = new NpgsqlCommand("SELECT * FROM fleet_item_totals_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new ItemTotalRow(Int(r, "item_id"), BoolOrNull(r, "hq") == true, Long(r, "total")));
            return list;
        }

        /// <summary>Every item the fleet holds (not gil), for searching by name in Where is it?.</summary>
        public static async Task<List<int>> ReadHeldItems(NpgsqlConnection conn)
        {
            var list = new List<int>();
            await using var cmd = new NpgsqlCommand("SELECT DISTINCT item_id FROM fleet_holdings_view WHERE NOT money AND item_id IS NOT NULL", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(Int(r, "item_id"));
            return list;
        }

        // --- The Inventory page's reads: on demand, one at a time, apart from the regular ones above. ---

        private volatile bool inventoryBusy;
        public bool InventoryBusy => inventoryBusy;
        /// <summary>What the Inventory page last asked for (Key says which: the characters, FCs or search), and the answer.</summary>
        public FleetRead<List<HoldingRow>> Holdings { get; private set; } = new(null, null, null);
        public string? HoldingsKey { get; private set; }
        public FleetRead<List<ItemTotalRow>> ItemTotals { get; private set; } = new(null, null, null);
        public FleetRead<List<int>> HeldItems { get; private set; } = new(null, null, null);

        /// <summary>Reads the stacks for key (say "characters:1,2") when it isn't what was last read, or that is over a
        /// minute old.</summary>
        public void ReadHoldingsIfDue(string key, string where, Action<NpgsqlParameterCollection> addParameters, bool force = false)
        {
            if (!force && HoldingsKey == key && Holdings.At.HasValue && DateTime.Now - Holdings.At.Value < ListsEvery) return;
            RunInventoryRead(async conn =>
            {
                var before = HoldingsKey == key ? Holdings : new FleetRead<List<HoldingRow>>(null, null, null);
                HoldingsKey = key;
                Holdings = before;
                Holdings = await Read(conn, c => ReadHoldings(c, where, addParameters), before);
            }, text => Holdings = Holdings with { Error = text });
        }

        public void ReadItemTotalsIfDue(bool force = false)
        {
            if (!force && ItemTotals.At.HasValue && DateTime.Now - ItemTotals.At.Value < ListsEvery) return;
            RunInventoryRead(async conn => ItemTotals = await Read(conn, ReadItemTotals, ItemTotals), text => ItemTotals = ItemTotals with { Error = text });
        }

        public void ReadHeldItemsIfDue(bool force = false)
        {
            if (!force && HeldItems.At.HasValue && DateTime.Now - HeldItems.At.Value < ListsEvery) return;
            RunInventoryRead(async conn => HeldItems = await Read(conn, ReadHeldItems, HeldItems), text => HeldItems = HeldItems with { Error = text });
        }

        private void RunInventoryRead(Func<NpgsqlConnection, Task> read, Action<string> failed)
        {
            if (inventoryBusy) return;
            var cs = connectionString();
            if (cs == null)
            {
                failed("The database isn't set up yet (Settings tab).");
                return;
            }
            inventoryBusy = true;
            Task.Run(async () =>
            {
                try
                {
                    await using var conn = new NpgsqlConnection(cs);
                    await conn.OpenAsync();
                    await read(conn);
                }
                catch (Exception ex)
                {
                    failed($"Could not reach the database: {ex.Message}");
                }
                finally
                {
                    inventoryBusy = false;
                }
            });
        }

        /// <summary>A retainer's venture, as the Retainers tab: "Idle", "Venture Complete" (waiting to be collected) or the
        /// time until it's done.</summary>
        public static string VentureStatus(bool hasVenture, long endsAt, long nowUnix)
        {
            if (!hasVenture || endsAt <= 0) return "Idle";
            if (endsAt <= nowUnix) return "Venture Complete";
            return Countdown(endsAt - nowUnix);
        }

        /// <summary>The average item level of what a retainer wears, as the Retainers tab: every item whose level is known
        /// (itemLevel null: not in the game's data, left out); null when none is.</summary>
        public static double? AverageItemLevel(IEnumerable<int>? items, Func<int, int?> itemLevel)
        {
            var levels = (items ?? Enumerable.Empty<int>()).Select(itemLevel).Where(l => l.HasValue).Select(l => l!.Value).ToList();
            return levels.Count == 0 ? null : levels.Average();
        }

        /// <summary>"Account 2 on DESKTOP-1": the setup a character last logged in on (only it syncs the character), or "—".</summary>
        public static string SyncedBy(string? loginPc, string? loginLabel) =>
            loginPc == null ? "—" : $"{(string.IsNullOrEmpty(loginLabel) ? "(no label)" : loginLabel)} on {loginPc}";

        /// <summary>"12 (2 left out) (3 left to another setup)", as the PCs tab.</summary>
        public static string PcCharacters(int? synced, int? leftOut, int? elsewhere) =>
            $"{(synced.HasValue ? synced.Value.ToString(CultureInfo.InvariantCulture) : "—")}{(leftOut > 0 ? $" ({leftOut} left out)" : "")}{(elsewhere > 0 ? $" ({elsewhere} left to another setup)" : "")}";

        /// <summary>Compares "2.0.0.9"-style versions, part by part.</summary>
        public static int CompareVersions(string? a, string? b)
        {
            static int[] Parts(string? v) => (v ?? "0").Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            var pa = Parts(a);
            var pb = Parts(b);
            for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                var x = i < pa.Length ? pa[i] : 0;
                var y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        /// <summary>
        /// The info bar line: "Fleet" when nothing needs you, else what does, for example "Fleet: 4 back · 1 low · 2 to
        /// check" (subs back and waiting; characters low on supplies; anything else that needs attention).
        /// </summary>
        public static string InfoBarText(int subsBack, IReadOnlyCollection<AlertRow>? alerts)
        {
            var parts = new List<string>();
            if (subsBack > 0) parts.Add($"{subsBack} back");
            if (alerts != null)
            {
                var low = alerts.Where(a => a.Kind is "low_ceruleum" or "low_repair_kits").Select(a => a.Subject).Distinct().Count();
                var other = alerts.Count(a => a.Kind is not ("low_ceruleum" or "low_repair_kits"));
                if (low > 0) parts.Add($"{low} low");
                if (other > 0) parts.Add($"{other} to check");
            }
            return parts.Count == 0 ? "Fleet" : "Fleet: " + string.Join(" · ", parts);
        }

        /// <summary>Subs back and waiting to be collected, right now.</summary>
        public static int SubsBack(IEnumerable<SubRow>? subs, long nowUnix) => subs?.Count(x => x.ReturnTime > 0 && x.ReturnTime <= nowUnix) ?? 0;

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
