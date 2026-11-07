using System;
using System.Data.Common;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;

namespace XIVFleetCompanion
{
    /// <summary>One built sub, from fleet_subs_view.</summary>
    public sealed record SubRow(string Character, string World, string Account, int SortIndex, string SubName, int Slot,
        int Rank, string Build, long ReturnTime, long CurrentExp, long NextLevelExp, long Cid = 0);

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
        string? LoginPc, string? LoginLabel, DateTime? LoggedInAt, int? BehindMinutes, bool LowCeruleum, bool LowRepairKits,
        int? SubSlots = null, double CeruleumWarning = 0, double RepairKitWarning = 0, int SubsBuilt = 0);

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
        List<string> Problems, string? CheckText, int? CheckToFix, DateTime? CheckAt, DateTime? LastRead = null);

    /// <summary>The retainers with a class right now, from fleet_retainer_counts_view (as the app's Summary).</summary>
    public sealed record RetainerCountsRow(int Retainers, int VenturesDone, int Away, int Idle);

    /// <summary>One Free Company, from fleet_fcs_view. HomeWorld: a World number, named from the game's files.
    /// HouseLevel: "ok", "warn", "danger" or "unknown" (null without a house). Eligibility: when it can bid, without a house.</summary>
    public sealed record FcRow(decimal FcId, string Name, string? Leader, int? Points, int? Members, bool HasHouse, string? House,
        int HomeWorld, int? HouseDays, string? HouseLevel, int? DemolitionInDays, DateTime? LastEntered, string? Eligibility,
        DateTime? EligibleAt, string Account, int SortIndex);

    /// <summary>One stack of an item, from fleet_holdings_view. Kind: "character", "retainer" or "fc". ItemId is the plain
    /// item (Hq: high quality). OwnerCid: the character, or for an FC chest the member it's counted on. Money: gil, not items.</summary>
    /// Equipped: gear being worn by a character or retainer (for "Hide equipped gear").</summary>
    public sealed record HoldingRow(string Kind, long? OwnerCid, decimal? FcId, int Container, int ItemId, bool Hq, long Quantity,
        string Who, string World, string? OwnerName, string Account, int SortIndex, string? RetainerName, int HireOrder,
        string? ContainerName, bool Money, string Place, bool Equipped = false);

    /// <summary>How many of an item the fleet holds, from fleet_item_totals_view (high quality apart); Equipped: how many of
    /// them are being worn.</summary>
    public sealed record ItemTotalRow(int ItemId, bool Hq, long Total, long Equipped = 0);

    /// <summary>The salvage a character holds, from fleet_salvage_view (bags, retainers and the FC chest it's counted on):
    /// standard and extravagant pieces, their worth at the fixed salvage prices, and how many of each piece (Pieces[0] is
    /// item 22500, up to Pieces[7], 22507).</summary>
    public sealed record SalvageRow(long Cid, string Character, string World, string Account, int SortIndex, long Standard, long Extravagant,
        long Gil, long[] Pieces);

    /// <summary>A character's real net income from its subs over the Income window, from fleet_income_view. Enough: a day or
    /// more of salvage history (the numbers are 0 otherwise).</summary>
    public sealed record IncomeRow(long Cid, string Character, string World, string Account, int SortIndex, int Subs, bool Enough, double SpanDays,
        long Pieces, long FromChest, long FirstStored, double SalvageGil, long KitsUsed, double KitGil, double Net, double PerDay,
        double SalvagePerDay, double KitPerDay);

    /// <summary>One character's venture rewards of one item over the Income window, from fleet_venture_income_view, with its
    /// rewards and retainers in that time. SalvagePrice: salvage's fixed price; anything else is priced from the game's files.</summary>
    public sealed record VentureIncomeRow(long Cid, string Character, string World, string Account, int SortIndex, int ItemId, long Quantity,
        int? SalvagePrice, int Rewards, int Retainers, long HqQuantity = 0);

    /// <summary>The venture rewards over the Income window (days; 7 when none were recorded, the app's default).</summary>
    public sealed record VenturesRead(List<VentureIncomeRow> Rows, int WindowDays);

    /// <summary>Every built sub's route answers (SubRoutes), as the app's Subs tab, worked out at At (milliseconds since 1970).</summary>
    public sealed record RoutesRead(List<SubRoute> Subs, double At);

    /// <summary>The Income page's read: each character's income, the venture rewards, the window (days) and repair kit cost.</summary>
    public sealed record IncomeRead(List<IncomeRow> Characters, List<VentureIncomeRow> Ventures, int WindowDays, double KitCost);

    /// <summary>The Parts page's read: every part needed (PartsPlanner, from the fleet_parts_* views).</summary>
    /// <summary>Inputs: what they were worked out from, for Look ahead (worked out again with the builds coming up).</summary>
    public sealed record PartsRead(List<PartNeed> Needs, PartsInputs? Inputs = null);

    /// <summary>What the Shopping List needs from the database: what's held where (fleet_shopping_held_view), the ticks
    /// (fleet_shopping_progress_view), the fleet's own retainers (their listings aren't the market), whether Live Market
    /// Prices is on and how many characters are on each world (for "Prices from").</summary>
    public sealed record ShoppingRead(List<HeldRow> Held, string? Progress, HashSet<string> Exclude, bool LivePrices, List<(string World, int Characters)> Worlds);

    /// <summary>What was last read, when, and why the last read failed (null when it didn't).</summary>
    /// <summary>The settings the app and the plugin share (fleet_settings_view): the Warnings, the repair kit cost (null:
    /// not set), Live Market Prices, and "What do you run?" with what "auto" decides from.</summary>
    public sealed record FleetSettings(double RepairKitThreshold, double CeruleumThreshold, int HouseWarnDays, int NotSyncedMinutes,
        double? RepairKitCost, bool LivePrices, string PlayChoice, int SubsBuilt, int RetainersWithClass)
    {
        /// <summary>What the player runs: "both", "subs" or "retainers" (auto: subs only with built subs and no retainer
        /// with a class, retainers only the other way round, else both), as the app's shared/play-style.js.</summary>
        public string PlayStyle => PlayChoice switch
        {
            "both" or "subs" or "retainers" => PlayChoice,
            _ => SubsBuilt > 0 && RetainersWithClass == 0 ? "subs" : RetainersWithClass > 0 && SubsBuilt == 0 ? "retainers" : "both",
        };
        public bool UsesSubs => PlayStyle != "retainers";
        public bool UsesRetainers => PlayStyle != "subs";
    }

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
        /// <summary>Salvage and Income: every 5 minutes while one of them is shown (Income takes the database a moment).</summary>
        public static readonly TimeSpan MoneyEvery = TimeSpan.FromMinutes(5);
        /// <summary>Parts: every 5 minutes whenever the fleet is shown (the Summary, the sidebar and the info bar count
        /// the subs ready for new parts).</summary>
        public static readonly TimeSpan PartsEvery = TimeSpan.FromMinutes(5);
        /// <summary>Gil and the market: every 5 minutes whenever the fleet is shown (the Summary counts the listings unsold
        /// for a while). Live prices are kept for 30 minutes, so Universalis is asked at most that often for an item.</summary>
        public static readonly TimeSpan MarketEvery = TimeSpan.FromMinutes(5);
        public const string MissingViewsText =
            "The database doesn't have the fleet views yet. Update the XIV Fleet Companion app to 1.8.0 or later and open it once; it adds them.";

        public const string MissingFileViewsText =
            "The fleet file doesn't have the fleet views yet. The plugin adds them itself; the Status page says what stopped it.";

        private readonly Func<string?> connectionString;
        private volatile bool busy;
        private DateTime subsAsked = DateTime.MinValue;
        private DateTime suppliesAsked = DateTime.MinValue;
        private DateTime listsAsked = DateTime.MinValue;
        private DateTime moneyAsked = DateTime.MinValue;
        private DateTime partsAsked = DateTime.MinValue;
        private DateTime marketAsked = DateTime.MinValue;
        private readonly Universalis universalis = new();

        public FleetRead<List<SubRow>> Subs { get; private set; } = new(null, null, null);
        public FleetRead<List<SupplyRow>> Supplies { get; private set; } = new(null, null, null);
        public FleetRead<SummaryRow> Summary { get; private set; } = new(null, null, null);
        public FleetRead<List<AlertRow>> Alerts { get; private set; } = new(null, null, null);
        public FleetRead<List<RosterRow>> Roster { get; private set; } = new(null, null, null);
        public FleetRead<RetainersRead> Retainers { get; private set; } = new(null, null, null);
        public FleetRead<List<PcRow>> Pcs { get; private set; } = new(null, null, null);
        public FleetRead<RetainerCountsRow> RetainerCounts { get; private set; } = new(null, null, null);
        public FleetRead<List<FcRow>> Fcs { get; private set; } = new(null, null, null);
        public FleetRead<List<SalvageRow>> Salvage { get; private set; } = new(null, null, null);
        public FleetRead<IncomeRead> Income { get; private set; } = new(null, null, null);
        /// <summary>Loot per Voyage's sector rows over the Income window (fleet_voyage_loot_view): read with Income.</summary>
        public FleetRead<List<VoyageLootRow>> Loot { get; private set; } = new(null, null, null);
        /// <summary>The venture rewards alone (quick), for the Summary's venture income: read with the supplies.</summary>
        public FleetRead<VenturesRead> Ventures { get; private set; } = new(null, null, null);
        public FleetRead<PartsRead> Parts { get; private set; } = new(null, null, null);
        /// <summary>Routes, gil a day and rank-up dates: read with Parts (the Subs page, Route Optimization, the Summary, Income's expected).</summary>
        public FleetRead<RoutesRead> Routes { get; private set; } = new(null, null, null);
        /// <summary>How close the Next Rank dates came (the Subs page's forecast line): read with the routes.</summary>
        public FleetRead<ForecastAccuracy> Forecasts { get; private set; } = new(null, null, null);
        public FleetRead<MarketRead> Market { get; private set; } = new(null, null, null);
        /// <summary>The shared settings, read with the parts (every 5 minutes) and after an edit.</summary>
        public FleetRead<FleetSettings> Settings { get; private set; } = new(null, null, null);
        public bool Busy => busy;
        private volatile bool askingUniversalis;
        /// <summary>True while live prices are being asked of Universalis (apart from the database reads).</summary>
        public bool AskingUniversalis => askingUniversalis;

        /// <param name="connectionString">The database to read (null when none is set up), asked each time.</param>
        /// <param name="subGame">The sectors, ranks and parts from the game's own files, for the routes (asked on the
        /// calling thread; null when they can't be read).</param>
        /// <param name="open">Opens the database for a read (null: none set up), following it when the app has moved it to
        /// another PC (DatabaseMove), as a sync does, so a PC that only reads switches too; none: the connection string as it is.</param>
        /// <param name="checkIn">This PC, its account label and plugin version, recorded as it reads (every few minutes),
        /// so a PC that syncs no characters is still on the app's PCs tab and in a move's checks; null: not recorded.</param>
        /// <param name="keeper">Brings the database up to date before reading, when it's due (DatabaseUpkeep); null: never.</param>
        public FleetReader(Func<string?> connectionString, Func<SubGameData?>? subGame = null, Func<Task<DbConnection?>>? open = null,
            Func<(string Pc, string Account, string Version)>? checkIn = null, DatabaseKeeper? keeper = null)
        {
            this.keeper = keeper;
            this.connectionString = connectionString;
            this.subGame = subGame;
            this.open = open;
            this.checkIn = checkIn;
        }
        private readonly Func<(string Pc, string Account, string Version)>? checkIn;
        private readonly DatabaseKeeper? keeper;
        public static readonly TimeSpan CheckInEvery = TimeSpan.FromMinutes(5);
        private DateTime checkedIn = DateTime.MinValue;
        private bool checkInRefused;

        // Records that this PC read the database (a database without update 16 yet, or a login that may only read: not
        // again). Never stops a read.
        private async Task CheckInIfDue(DbConnection conn)
        {
            if (checkIn == null || checkInRefused || DateTime.UtcNow - checkedIn < CheckInEvery) return;
            checkedIn = DateTime.UtcNow;
            try
            {
                var (pc, account, version) = checkIn();
                checkInRefused = !await DetailsWriter.WritePcReadAsync(conn, pc, account, version);
            }
            catch (PostgresException)
            {
                checkInRefused = true;
            }
            catch (Exception)
            {
                // tried again next time
            }
        }
        private readonly Func<Task<DbConnection?>>? open;

        // The connection for one read: through `open` (which follows a moved database), else the connection string.
        private async Task<DbConnection> OpenAsync(string cs)
        {
            if (open != null) return await open() ?? throw new InvalidOperationException("The database isn't set up yet (Settings tab).");
            var conn = new NpgsqlConnection(cs);
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

        // Why a read couldn't reach the database, in plain words.
        private static string ReachFailure(Exception ex) => ex switch
        {
            DatabaseMove.WaitException => "The database is being moved to another PC. The pages show what was read before until that's done.",
            FileDatabase.RefusedException => ex.Message,
            System.Data.SQLite.SQLiteException => $"Could not open the fleet file: {ex.Message}",
            _ => $"Could not reach the database: {ex.Message}",
        };
        private readonly Func<SubGameData?>? subGame;

        /// <summary>
        /// Called while something shows the fleet: starts a read in the background when one is due. withSupplies:
        /// false for the info bar line, which only needs the subs and what needs attention. withLists: true while the
        /// Roster, Retainers or PCs page is shown (everything reads them too). withMoney: true while Salvage or Income is
        /// shown (everything reads them too).
        /// </summary>
        public void ReadIfDue(bool everything = false, bool withSupplies = true, bool withLists = false, bool withMoney = false)
        {
            if (busy) return;
            var now = DateTime.UtcNow;
            var subs = everything || now - subsAsked >= SubsEvery;
            var supplies = everything || (withSupplies && now - suppliesAsked >= SuppliesEvery);
            var lists = everything || (withLists && now - listsAsked >= ListsEvery);
            var money = everything || (withMoney && now - moneyAsked >= MoneyEvery);
            var parts = everything || now - partsAsked >= PartsEvery;
            var market = everything || now - marketAsked >= MarketEvery;
            if (!subs && !supplies && !lists && !money && !parts && !market) return;
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
                Salvage = Salvage with { Error = notSetUp };
                Income = Income with { Error = notSetUp };
                Loot = Loot with { Error = notSetUp };
                Ventures = Ventures with { Error = notSetUp };
                Parts = Parts with { Error = notSetUp };
                Routes = Routes with { Error = notSetUp };
                Market = Market with { Error = notSetUp };
                Settings = Settings with { Error = notSetUp };
                return;
            }
            busy = true;
            if (subs) subsAsked = now;
            if (supplies) suppliesAsked = now;
            if (lists) listsAsked = now;
            if (money) moneyAsked = now;
            if (parts) partsAsked = now;
            var game = parts ? subGame?.Invoke() : null;
            if (market) marketAsked = now;
            Task.Run(async () =>
            {
                try
                {
                    await using var conn = await OpenAsync(cs);
                    // On the fleet file, cs is the file (for its daily backup).
                    if (keeper != null) await keeper.RunIfDueAsync(conn, Db.IsFile(conn) ? cs : null);
                    await CheckInIfDue(conn);
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
                        // The Summary's salvage total (quick), unless the Salvage page reads it now anyway.
                        if (!money) Salvage = await Read(conn, ReadSalvage, Salvage);
                        Ventures = await Read(conn, ReadVentures, Ventures);
                    }
                    if (lists)
                    {
                        Roster = await Read(conn, ReadRoster, Roster);
                        Retainers = await Read(conn, ReadRetainers, Retainers);
                        Pcs = await Read(conn, ReadPcs, Pcs);
                        Fcs = await Read(conn, ReadFcs, Fcs);
                    }
                    if (money)
                    {
                        Salvage = await Read(conn, ReadSalvage, Salvage);
                        Income = await Read(conn, ReadIncome, Income);
                        Loot = await Read(conn, ReadLoot, Loot);
                    }
                    if (parts)
                    {
                        Settings = await Read(conn, ReadSettings, Settings);
                        Parts = await Read(conn, ReadParts, Parts);
                        Routes = await Read(conn, c => ReadRoutes(c, game, (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds), Routes);
                        Forecasts = await Read(conn, ReadForecasts, Forecasts);
                    }
                    if (market) await ReadMarketNow(conn);
                }
                catch (Exception ex)
                {
                    var text = ReachFailure(ex);
                    if (subs) { Subs = Subs with { Error = text }; Alerts = Alerts with { Error = text }; RetainerCounts = RetainerCounts with { Error = text }; }
                    if (supplies) { Supplies = Supplies with { Error = text }; Summary = Summary with { Error = text }; Salvage = Salvage with { Error = text }; Ventures = Ventures with { Error = text }; }
                    if (lists) { Roster = Roster with { Error = text }; Retainers = Retainers with { Error = text }; Pcs = Pcs with { Error = text }; Fcs = Fcs with { Error = text }; }
                    if (money) { Salvage = Salvage with { Error = text }; Income = Income with { Error = text }; Loot = Loot with { Error = text }; }
                    if (parts) { Parts = Parts with { Error = text }; Routes = Routes with { Error = text }; Settings = Settings with { Error = text }; }
                    if (market) Market = Market with { Error = text };
                }
                finally
                {
                    busy = false;
                }
            });
        }

        // The Gil & Market page's database part now, keeping the live prices from before; then, with Live Market Prices
        // on, Universalis in the background (unless it's still being asked), so a slow answer holds up nothing else.
        private async Task ReadMarketNow(DbConnection conn)
        {
            MarketAsk? ask = null;
            Market = await Read(conn, async c =>
            {
                var (read, a) = await ReadMarketData(c);
                ask = a;
                return a == null ? read : read with { Live = Market.Data?.Live };
            }, Market);
            if (ask is not { } toAsk || askingUniversalis) return;
            askingUniversalis = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var live = await LivePrices(universalis, toAsk);
                    if (Market.Data is { } data) Market = Market with { Data = data with { Live = live } };
                }
                catch (Exception ex)
                {
                    if (Market.Data is { } data) Market = Market with { Data = data with { Live = new LiveMarket(new Dictionary<string, WorldMarket>(), toAsk.Exclude, ex.Message) } };
                }
                finally
                {
                    askingUniversalis = false;
                }
            });
        }

        // A failed read keeps what was read before, with the reason.
        internal static async Task<FleetRead<T>> Read<T>(DbConnection conn, Func<DbConnection, Task<T>> read, FleetRead<T> before) where T : class
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
            catch (System.Data.SQLite.SQLiteException ex) when (ex.Message.Contains("no such table") || ex.Message.Contains("no such view"))
            {
                return before with { Error = MissingFileViewsText };
            }
            // Something in what was read the plugin couldn't take (not the connection): only this read fails, and says so.
            catch (Exception ex) when (ex is not NpgsqlException)
            {
                return before with { Error = $"Could not read this from the database: {ex.Message}" };
            }
        }

        // Values of whatever numeric type the view gives, and nulls.
        private static string Text(DbDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? ""; }
        private static long Long(DbDataReader r, string col, long fallback = 0) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? fallback : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static int Int(DbDataReader r, string col, int fallback = 0) => (int)Long(r, col, fallback);
        private static double? Double(DbDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static string? TextOrNull(DbDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture); }
        // Free Company ids go past what a long holds (the game's are unsigned 64-bit numbers): read them as decimals.
        private static decimal Decimal(DbDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? 0 : Convert.ToDecimal(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static decimal? DecimalOrNull(DbDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static long? LongOrNull(DbDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
        private static int? IntOrNull(DbDataReader r, string col) => (int?)LongOrNull(r, col);
        private static bool? BoolOrNull(DbDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Convert.ToBoolean(r.GetValue(i), CultureInfo.InvariantCulture); }
        // A column a newer app's views have (an older one's don't, until the app is updated): its value, or the fallback.
        private static bool Has(DbDataReader r, string col)
        {
            for (var i = 0; i < r.FieldCount; i++) if (r.GetName(i) == col) return true;
            return false;
        }
        private static DateTime? Time(DbDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Db.Time(r, i).ToLocalTime(); }

        public static async Task<FleetSettings> ReadSettings(DbConnection conn)
        {
            await using var cmd = Db.Cmd("SELECT * FROM fleet_settings_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) throw new InvalidOperationException("No settings row.");
            return new FleetSettings(Double(r, "repair_kit_threshold") ?? 0, Double(r, "ceruleum_threshold") ?? 0, Int(r, "house_warn_days", 7),
                Int(r, "not_synced_minutes", 30), Double(r, "repair_kit_cost"), BoolOrNull(r, "live_prices") == true, Text(r, "play_style_choice"),
                Int(r, "subs_built"), Int(r, "retainers_with_class"));
        }

        public static async Task<List<SubRow>> ReadSubs(DbConnection conn)
        {
            var list = new List<SubRow>();
            await using var cmd = Db.Cmd(
                @"SELECT cid, character_name, world, account_label, sort_index, sub_name, slot, rank, build, return_time, current_exp, next_level_exp
                  FROM fleet_subs_view ORDER BY account_label NULLS LAST, sort_index NULLS LAST, character_name, slot NULLS LAST, sub_name", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SubRow(Text(r, "character_name"), Text(r, "world"), Text(r, "account_label"), Int(r, "sort_index", int.MaxValue),
                    Text(r, "sub_name"), Int(r, "slot", 9), Int(r, "rank"), Text(r, "build"), Long(r, "return_time"),
                    Long(r, "current_exp"), Long(r, "next_level_exp"), Long(r, "cid")));
            return list;
        }

        public static async Task<List<SupplyRow>> ReadSupplies(DbConnection conn)
        {
            var list = new List<SupplyRow>();
            await using var cmd = Db.Cmd(
                "SELECT * FROM fleet_supplies_view ORDER BY account_label NULLS LAST, sort_index NULLS LAST, name", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SupplyRow(Text(r, "name"), Text(r, "world"), Text(r, "account_label"), Int(r, "sort_index", int.MaxValue), Time(r, "last_synced_at"),
                    Int(r, "ceruleum"), Double(r, "ceruleum_per_day"), Double(r, "ceruleum_days_left"), Int(r, "ceruleum_restock"),
                    Int(r, "repair_kits"), Double(r, "repair_kits_per_day"), Double(r, "repair_kits_days_left"), Int(r, "repair_kits_restock"),
                    Int(r, "window_days", 14), Int(r, "target_days", 14)));
            return list;
        }

        public static async Task<SummaryRow> ReadSummary(DbConnection conn)
        {
            await using var cmd = Db.Cmd("SELECT * FROM fleet_summary_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return new SummaryRow(0, 0, 0, 0, 0, null);
            return new SummaryRow(Int(r, "retainers"), Int(r, "ventures_done"), Int(r, "retainers_idle"),
                Int(r, "low_ceruleum"), Int(r, "low_repair_kits"), Double(r, "soonest_runs_out_days"));
        }

        public static async Task<List<AlertRow>> ReadAlerts(DbConnection conn)
        {
            var list = new List<AlertRow>();
            await using var cmd = Db.Cmd("SELECT level, kind, subject, detail FROM fleet_alerts_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new AlertRow(Text(r, "level"), Text(r, "kind"), Text(r, "subject"), Text(r, "detail")));
            return list;
        }

        public static async Task<List<RosterRow>> ReadRoster(DbConnection conn)
        {
            var list = new List<RosterRow>();
            await using var cmd = Db.Cmd(
                "SELECT * FROM fleet_roster_view ORDER BY account_label NULLS LAST, sort_index NULLS LAST, name", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new RosterRow(Long(r, "cid"), Text(r, "name"), Text(r, "world"), Text(r, "account_label"), Int(r, "sort_index", int.MaxValue),
                    TextOrNull(r, "fc_name"), IntOrNull(r, "fc_points"), TextOrNull(r, "house"),
                    Long(r, "gil"), IntOrNull(r, "ceruleum"), IntOrNull(r, "repair_kits"), Int(r, "retainer_count"), Int(r, "submarine_count"),
                    Time(r, "last_synced_at"), TextOrNull(r, "login_pc"), TextOrNull(r, "login_label"), Time(r, "logged_in_at"),
                    IntOrNull(r, "behind_minutes"), BoolOrNull(r, "low_ceruleum") == true, BoolOrNull(r, "low_repair_kits") == true,
                    Has(r, "num_sub_slots") ? IntOrNull(r, "num_sub_slots") : null, Has(r, "ceruleum_warning") ? Double(r, "ceruleum_warning") ?? 0 : 0,
                    Has(r, "repair_kit_warning") ? Double(r, "repair_kit_warning") ?? 0 : 0, Has(r, "subs_built") ? Int(r, "subs_built") : 0));
            return FleetOrder.Roster(list);
        }

        public static async Task<RetainersRead> ReadRetainers(DbConnection conn)
        {
            var list = new List<RetainerRow>();
            await using (var cmd = Db.Cmd(
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
            await using (var cmd = Db.Cmd("SELECT retainer_id, item_id FROM fleet_retainer_gear_view", conn))
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

        public static async Task<List<PcRow>> ReadPcs(DbConnection conn)
        {
            var list = new List<PcRow>();
            await using var cmd = Db.Cmd("SELECT * FROM fleet_pcs_view ORDER BY account_label, pc_name", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new PcRow(Text(r, "pc_name"), Text(r, "account_label"), TextOrNull(r, "plugin_version"), Time(r, "last_sync_at"),
                    BoolOrNull(r, "autoretainer_ready"), BoolOrNull(r, "allagantools_ready"), BoolOrNull(r, "fctracker_found"),
                    BoolOrNull(r, "submarinetracker_found"), BoolOrNull(r, "venture_stats_found"),
                    IntOrNull(r, "characters_synced"), IntOrNull(r, "characters_left_out"), IntOrNull(r, "characters_elsewhere"),
                    Text(r, "problems").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    TextOrNull(r, "check_text"), IntOrNull(r, "check_to_fix"), Time(r, "check_at"),
                    Has(r, "last_read_at") ? Time(r, "last_read_at") : null));
            return list;
        }

        public static async Task<RetainerCountsRow> ReadRetainerCounts(DbConnection conn)
        {
            await using var cmd = Db.Cmd("SELECT * FROM fleet_retainer_counts_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return new RetainerCountsRow(0, 0, 0, 0);
            return new RetainerCountsRow(Int(r, "retainers"), Int(r, "ventures_done"), Int(r, "retainers_away"), Int(r, "retainers_idle"));
        }

        public static async Task<List<FcRow>> ReadFcs(DbConnection conn)
        {
            var list = new List<FcRow>();
            await using var cmd = Db.Cmd("SELECT * FROM fleet_fcs_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FcRow(Decimal(r, "fc_id"), Text(r, "fc_name"), TextOrNull(r, "fc_master"), IntOrNull(r, "fc_points"), IntOrNull(r, "total_members"),
                    BoolOrNull(r, "has_house") == true, TextOrNull(r, "house"), Int(r, "home_world_id"), IntOrNull(r, "house_days"), TextOrNull(r, "house_level"),
                    IntOrNull(r, "demolition_in_days"), Time(r, "house_last_visited"), TextOrNull(r, "eligibility"), Time(r, "eligible_at"),
                    Text(r, "account_label"), Int(r, "sort_index", int.MaxValue)));
            return FleetOrder.Fcs(list);
        }

        /// <summary>The stacks matching a condition on fleet_holdings_view (for example "owner_cid = ANY(@cids)"), with its
        /// parameters added by addParameters.</summary>
        /// <param name="idColumn">With ids: only the stacks whose idColumn is one of them (owner_cid, fc_id or item_id).</param>
        public static async Task<List<HoldingRow>> ReadHoldings(DbConnection conn, string where, string? idColumn = null, IEnumerable<object>? ids = null)
        {
            var list = new List<HoldingRow>();
            await using var cmd = Db.Cmd("", conn);
            // In the file a Free Company's id is text (see Db).
            var values = (ids ?? Enumerable.Empty<object>()).Select(v => Db.IsFile(conn) && idColumn == "fc_id" ? (object)Convert.ToString(v, CultureInfo.InvariantCulture)! : v);
            cmd.CommandText = $"SELECT * FROM fleet_holdings_view WHERE {where}" + (idColumn == null ? "" : $" AND {idColumn} IN {Db.In(cmd, "id", values)}");
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new HoldingRow(Text(r, "kind"), LongOrNull(r, "owner_cid"), DecimalOrNull(r, "fc_id"), Int(r, "container"), Int(r, "item_id"),
                    BoolOrNull(r, "hq") == true, Long(r, "quantity"), Text(r, "who"), Text(r, "world"), TextOrNull(r, "owner_name"), Text(r, "account_label"),
                    Int(r, "sort_index", int.MaxValue), TextOrNull(r, "retainer_name"), Int(r, "hire_order_index", int.MaxValue), TextOrNull(r, "container_name"),
                    BoolOrNull(r, "money") == true, Text(r, "place"), Has(r, "equipped") && BoolOrNull(r, "equipped") == true));
            return list;
        }

        public static async Task<List<ItemTotalRow>> ReadItemTotals(DbConnection conn)
        {
            var list = new List<ItemTotalRow>();
            await using var cmd = Db.Cmd("SELECT * FROM fleet_item_totals_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new ItemTotalRow(Int(r, "item_id"), BoolOrNull(r, "hq") == true, Long(r, "total"), Has(r, "equipped") ? Long(r, "equipped") : 0));
            return list;
        }

        /// <summary>
        /// The Gil &amp; Market page (fleet_market_* views, app 1.8.0 and later): its settings, gil per account and every
        /// listing; with Live Market Prices on, Universalis's listings for the listed items and the venture rewards on each
        /// character's world, data center and region (the player's own retainers' listings left out). Universalis not
        /// answering doesn't fail the read: it says why instead. The window reads the two apart (ReadMarketData, then
        /// LivePrices in the background), so asking Universalis never holds up the database reads.
        /// </summary>
        public static async Task<MarketRead> ReadMarket(DbConnection conn, Universalis? universalis)
        {
            var (read, ask) = await ReadMarketData(conn);
            return ask != null && universalis != null ? read with { Live = await LivePrices(universalis, ask) } : read;
        }

        /// <summary>What to ask Universalis: the player's own retainers (left out of the rivals), and each world with an
        /// item listed or earned on a venture there.</summary>
        public sealed record MarketAsk(HashSet<string> Exclude, List<(string? World, int ItemId)> Wanted);

        /// <summary>The database's part of the Gil &amp; Market page (Live left empty), and what to ask Universalis when
        /// Live Market Prices is on (else null).</summary>
        public static async Task<(MarketRead Read, MarketAsk? Ask)> ReadMarketData(DbConnection conn)
        {
            MarketSettings settings;
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_market_settings_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                settings = await r.ReadAsync() ? new MarketSettings(Int(r, "stale_days", 7), BoolOrNull(r, "live_prices") == true) : new MarketSettings(7, false);
            var accounts = new List<MarketAccountRow>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_market_accounts_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    accounts.Add(new MarketAccountRow(Text(r, "account_label"), Int(r, "characters"), Int(r, "retainers"), Long(r, "character_gil"), Long(r, "retainer_gil"), Long(r, "total")));
            // By name, sorted here: the two databases order text differently (capitals first in the file).
            accounts.Sort((a, b) => StringComparer.InvariantCultureIgnoreCase.Compare(a.Account, b.Account));
            var listings = new List<ListingRow>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_market_listings_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    listings.Add(new ListingRow(Long(r, "retainer_id"), Long(r, "owner_cid"), Int(r, "slot"), Int(r, "item_id"), Int(r, "quantity"), BoolOrNull(r, "hq") == true,
                        Long(r, "unit_price"), Time(r, "first_seen_at"), TextOrNull(r, "retainer_name"), TextOrNull(r, "owner_name"), TextOrNull(r, "world"),
                        TextOrNull(r, "account_label"), IntOrNull(r, "sort_index"), Long(r, "value"), Double(r, "age_days") ?? 0, BoolOrNull(r, "stale") == true));
            var read = new MarketRead(settings, accounts, listings, null);
            if (!settings.LivePrices) return (read, null);
            var exclude = new HashSet<string>();
            await using (var cmd = Db.Cmd("SELECT retainer_name AS name, world FROM fleet_retainers_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    if (Text(r, "name").Length > 0) exclude.Add(Universalis.RetainerKey(Text(r, "name"), TextOrNull(r, "world")));
            var ventures = (await ReadVentures(conn)).Rows;
            // Loot per Voyage's items, on the world of each voyage's Free Company (an FC is on one world). A database
            // whose app has no Loot view yet just leaves them out.
            var loot = new List<(string? World, int ItemId)>();
            try
            {
                await using var cmd = Db.Cmd(
                    @"SELECT DISTINCT ch.world, x.item FROM (SELECT fc_id, primary_item AS item FROM fleet_voyage_loot_view
                        UNION SELECT fc_id, additional_item FROM fleet_voyage_loot_view) x
                      JOIN fleet_parts_characters_view ch ON ch.fc_id = x.fc_id WHERE x.item IS NOT NULL AND x.item <> 0", conn);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) loot.Add((TextOrNull(r, "world"), Int(r, "item")));
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01") { }
            return (read, new MarketAsk(exclude, listings.Select(l => (l.World, l.ItemId)).Concat(ventures.Select(v => ((string?)v.World, v.ItemId))).Concat(loot).ToList()));
        }

        /// <summary>Universalis's listings for what the database says to ask (MarketAsk); why not, when it doesn't answer.</summary>
        public static async Task<LiveMarket> LivePrices(Universalis universalis, MarketAsk ask)
        {
            try
            {
                return new LiveMarket(await universalis.MarketFor(ask.Wanted), ask.Exclude, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return new LiveMarket(new Dictionary<string, WorldMarket>(), ask.Exclude, ex.Message);
            }
        }

        private static double Ms(DbDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? 0 : (Db.Time(r, i) - DateTime.UnixEpoch).TotalMilliseconds;
        }

        /// <summary>Every built sub's route answers (the fleet_route_* views, app 1.8.0 and later, and the game's own
        /// files), worked out here (SubRoutes) at `now` (milliseconds since 1970).</summary>
        public static async Task<RoutesRead> ReadRoutes(DbConnection conn, SubGameData? game, double now)
        {
            if (game == null) throw new InvalidOperationException("The game's sea map data couldn't be read.");
            return new RoutesRead(SubRoutes.Plan(await ReadRoutesInputs(conn), game, now), now);
        }

        public static async Task<RoutesInputs> ReadRoutesInputs(DbConnection conn)
        {
            var parts = await ReadPartsInputs(conn);
            var subs = new List<RouteSub>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_route_subs_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    var points = r.GetOrdinal("points");
                    subs.Add(new RouteSub(Long(r, "cid"), Text(r, "sub_name"), IntOrNull(r, "slot"), Int(r, "rank"), IntOrNull(r, "part1"), IntOrNull(r, "part2"),
                        IntOrNull(r, "part3"), IntOrNull(r, "part4"), r.IsDBNull(points) ? null : (byte[])r.GetValue(points), LongOrNull(r, "return_time"),
                        LongOrNull(r, "current_exp"), LongOrNull(r, "next_level_exp"), BoolOrNull(r, "craft_enabled") == true, TextOrNull(r, "fc_id")));
                }
            var fightClub = new List<FightClubRow>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_route_fight_club_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    fightClub.Add(new FightClubRow(Long(r, "id"), Text(r, "route"), Double(r, "hours"), TextOrNull(r, "repair_times"), Double(r, "gil_sub_day"),
                        TextOrNull(r, "rec_build_notes"), TextOrNull(r, "unmod_min_rank"), TextOrNull(r, "min_rank_build"), Double(r, "repair_kit_cost") ?? 0));
            var history = new List<SubHistoryPoint>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_sub_history_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    history.Add(new SubHistoryPoint(Long(r, "cid"), Text(r, "metric"), Ms(r, "recorded_at"), Double(r, "value") ?? 0));
            var voyages = new List<VoyageSector>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_voyage_sectors_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    voyages.Add(new VoyageSector(Text(r, "fc_id"), Long(r, "sub_register"), TextOrNull(r, "sub_name"), Ms(r, "returned_at"), Int(r, "sector"), IntOrNull(r, "leg")));
            return new RoutesInputs(parts.Characters, subs, parts.Rules, fightClub, history, voyages);
        }

        /// <summary>How close the Next Rank dates the app saved came to the real rank-ups (fleet_rankup_forecast_view,
        /// fleet_rankup_history_view and fleet_voyage_returns_view, app 1.8.0 and later), worked out here (ForecastMath).</summary>
        public static async Task<ForecastAccuracy> ReadForecasts(DbConnection conn)
        {
            var saved = new List<SavedForecast>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_rankup_forecast_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    saved.Add(new SavedForecast(Long(r, "cid"), Int(r, "slot"), Int(r, "rank"), Ms(r, "made_at"), Ms(r, "predicted_at"), TextOrNull(r, "sub_name"),
                        TextOrNull(r, "fc_id"), TextOrNull(r, "character_name")));
            var history = new List<SubHistoryPoint>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_rankup_history_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    history.Add(new SubHistoryPoint(Long(r, "cid"), Text(r, "metric"), Ms(r, "recorded_at"), Double(r, "value") ?? 0));
            var returns = new List<VoyageReturn>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_voyage_returns_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    returns.Add(new VoyageReturn(Text(r, "fc_id"), Text(r, "sub_name"), Ms(r, "returned_at")));
            return ForecastMath.Check(saved, history, returns);
        }

        /// <summary>Loot per Voyage: every sector of every voyage Submarine Tracker recorded over the Income window
        /// (fleet_voyage_loot_view, app 1.8.0 and later), worked out on the Income page (LootMath).</summary>
        public static async Task<List<VoyageLootRow>> ReadLoot(DbConnection conn)
        {
            var rows = new List<VoyageLootRow>();
            await using var cmd = Db.Cmd("SELECT * FROM fleet_voyage_loot_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                rows.Add(new VoyageLootRow(Text(r, "fc_id"), Long(r, "sub_register"), Ms(r, "returned_at"), Int(r, "sector"), TextOrNull(r, "sub_name"),
                    IntOrNull(r, "primary_item"), IntOrNull(r, "primary_count"), IntOrNull(r, "additional_item"), IntOrNull(r, "additional_count"),
                    IntOrNull(r, "leg"), Int(r, "window_days", 7)));
            return rows;
        }

        /// <summary>The Parts page's inputs (the fleet_parts_* views, app 1.8.0 and later), worked out here (PartsPlanner).</summary>
        public static async Task<PartsRead> ReadParts(DbConnection conn)
        {
            var inputs = await ReadPartsInputs(conn);
            return new(PartsPlanner.Plan(inputs), inputs);
        }

        public static async Task<PartsInputs> ReadPartsInputs(DbConnection conn)
        {
            var characters = new List<PartsCharacter>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_parts_characters_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    characters.Add(new PartsCharacter(Long(r, "cid"), Text(r, "name"), Text(r, "world"), Text(r, "account_label"), IntOrNull(r, "sort_index"),
                        TextOrNull(r, "fc_id"), Int(r, "num_sub_slots"), BoolOrNull(r, "has_house") == true));
            var subs = new List<PartsSub>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_parts_subs_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    subs.Add(new PartsSub(Long(r, "cid"), Text(r, "sub_name"), IntOrNull(r, "slot"), Int(r, "rank"),
                        IntOrNull(r, "part1"), IntOrNull(r, "part2"), IntOrNull(r, "part3"), IntOrNull(r, "part4"), BoolOrNull(r, "craft_enabled") == true));
            var rules = new List<PartsRule>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_parts_rules_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    rules.Add(new PartsRule(Long(r, "id"), Int(r, "rank"), DecimalOrNull(r, "cid_override")?.ToString("0", CultureInfo.InvariantCulture),
                        TextOrNull(r, "sub_filter"), TextOrNull(r, "hull"), TextOrNull(r, "stern"), TextOrNull(r, "bow"), TextOrNull(r, "bridge")));
            var routes = new List<PartsFightClubRoute>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_parts_fight_club_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    routes.Add(new PartsFightClubRoute(Long(r, "id"), Text(r, "route"), Double(r, "hours"), TextOrNull(r, "repair_times"), Double(r, "gil_sub_day"),
                        TextOrNull(r, "rec_build_notes"), Double(r, "repair_kit_cost") ?? 0));
            var spares = new List<PartsSpare>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_parts_spares_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    spares.Add(new PartsSpare(LongOrNull(r, "cid"), TextOrNull(r, "fc_id"), BoolOrNull(r, "is_retainer") == true, LongOrNull(r, "retainer_id"),
                        TextOrNull(r, "retainer_name"), Int(r, "sorted_container"), Int(r, "sorted_slot_index"), Int(r, "item_id"), Int(r, "quantity")));
            return new PartsInputs(characters, subs, rules, routes, spares);
        }

        public static async Task<List<SalvageRow>> ReadSalvage(DbConnection conn)
        {
            var list = new List<SalvageRow>();
            await using var cmd = Db.Cmd("SELECT * FROM fleet_salvage_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SalvageRow(Long(r, "cid"), Text(r, "name"), Text(r, "world"), Text(r, "account_label"), Int(r, "sort_index", int.MaxValue),
                    Long(r, "standard"), Long(r, "extravagant"), Long(r, "gil"), Enumerable.Range(22500, 8).Select(id => Long(r, $"q{id}")).ToArray()));
            return FleetOrder.Roster(list, x => x.SortIndex, x => x.Character);
        }

        public static async Task<IncomeRead> ReadIncome(DbConnection conn)
        {
            var characters = new List<IncomeRow>();
            var windowDays = 7;
            var kitCost = 0.0;
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_income_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    characters.Add(new IncomeRow(Long(r, "cid"), Text(r, "name"), Text(r, "world"), Text(r, "account_label"), Int(r, "sort_index", int.MaxValue),
                        Int(r, "subs"), BoolOrNull(r, "enough") == true, Double(r, "span_days") ?? 0, Long(r, "pieces"), Long(r, "from_chest"), Long(r, "first_stored"),
                        Double(r, "salvage_gil") ?? 0, Long(r, "kits_used"), Double(r, "kit_gil") ?? 0, Double(r, "net") ?? 0, Double(r, "per_day") ?? 0,
                        Double(r, "salvage_per_day") ?? 0, Double(r, "kit_per_day") ?? 0));
                    windowDays = Int(r, "window_days", 7);
                    kitCost = Double(r, "kit_cost") ?? 0;
                }
            }
            var ventures = await ReadVentures(conn);
            if (ventures.Rows.Count > 0) windowDays = ventures.WindowDays;
            return new IncomeRead(FleetOrder.Roster(characters, x => x.SortIndex, x => x.Character), ventures.Rows, windowDays, kitCost);
        }

        public static async Task<VenturesRead> ReadVentures(DbConnection conn)
        {
            var ventures = new List<VentureIncomeRow>();
            var windowDays = 7;
            await using var cmd = Db.Cmd("SELECT * FROM fleet_venture_income_view", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                ventures.Add(new VentureIncomeRow(Long(r, "owner_cid"), Text(r, "name"), Text(r, "world"), Text(r, "account_label"), Int(r, "sort_index", int.MaxValue),
                    Int(r, "item_id"), Long(r, "quantity"), IntOrNull(r, "salvage_price"), Int(r, "rewards"), Int(r, "retainers"),
                    Has(r, "hq_quantity") ? Long(r, "hq_quantity") : 0));
                windowDays = Int(r, "window_days", windowDays);
            }
            return new VenturesRead(ventures, windowDays);
        }

        /// <summary>Each character's venture rewards added up, as the Income tab's Retainer Ventures: rewards, retainers,
        /// vendor value (salvage at its fixed price, anything else at price, what a vendor pays) and per day over the window,
        /// and the three items worth most (the larger item number last when two are worth the same).</summary>
        public sealed record VentureSummary(long Cid, string Character, string World, string Account, int SortIndex, int Rewards, int Retainers,
            double Gil, double PerDay, List<(int ItemId, long Quantity, double Gil)> Top);

        public static List<VentureSummary> SummarizeVentures(IEnumerable<VentureIncomeRow> rows, int windowDays, Func<int, double> price)
        {
            return rows.GroupBy(x => x.Cid).Select(g =>
            {
                var first = g.First();
                var items = g.Select(x => (x.ItemId, x.Quantity, Gil: x.Quantity * (x.SalvagePrice ?? price(x.ItemId)))).ToList();
                var gil = items.Sum(x => x.Gil);
                return new VentureSummary(first.Cid, first.Character, first.World, first.Account, first.SortIndex, first.Rewards, first.Retainers,
                    gil, windowDays > 0 ? gil / windowDays : 0, items.OrderByDescending(x => x.Gil).ThenBy(x => x.ItemId).Take(3).ToList());
            }).ToList();
        }

        /// <summary>Every item the fleet holds (not gil), for searching by name in Where is it?.</summary>
        public static async Task<List<int>> ReadHeldItems(DbConnection conn)
        {
            var list = new List<int>();
            await using var cmd = Db.Cmd("SELECT DISTINCT item_id FROM fleet_holdings_view WHERE NOT money AND item_id IS NOT NULL", conn);
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
        /// <summary>The Shopping List's (on the Parts page): read with the Parts page's, while the list is shown.</summary>
        public FleetRead<ShoppingRead> Shopping { get; private set; } = new(null, null, null);
        /// <summary>The Universalis client (its answers are kept for 30 minutes), for the Shopping List's market prices.</summary>
        public Universalis UniversalisClient => universalis;

        /// <summary>Reads the stacks for key (say "characters:1,2") when it isn't what was last read, or that is over a
        /// minute old.</summary>
        public void ReadHoldingsIfDue(string key, string where, string idColumn, IEnumerable<object> ids, bool force = false)
        {
            if (!force && HoldingsKey == key && Holdings.At.HasValue && DateTime.Now - Holdings.At.Value < ListsEvery) return;
            RunInventoryRead(async conn =>
            {
                var before = HoldingsKey == key ? Holdings : new FleetRead<List<HoldingRow>>(null, null, null);
                HoldingsKey = key;
                Holdings = before;
                Holdings = await Read(conn, c => ReadHoldings(c, where, idColumn, ids), before);
            }, text => Holdings = Holdings with { Error = text });
        }

        public void ReadItemTotalsIfDue(bool force = false)
        {
            if (!force && ItemTotals.At.HasValue && DateTime.Now - ItemTotals.At.Value < ListsEvery) return;
            RunInventoryRead(async conn => ItemTotals = await Read(conn, ReadItemTotals, ItemTotals), text => ItemTotals = ItemTotals with { Error = text });
        }

        /// <summary>The Shopping List's database part, when it hasn't been read since the Parts page's last read or is 5 minutes old.</summary>
        public void ReadShoppingIfDue(bool force = false)
        {
            var at = Shopping.At;
            if (!force && at.HasValue && DateTime.Now - at.Value < PartsEvery && !(Parts.At is DateTime partsAt && partsAt > at.Value)) return;
            RunInventoryRead(async conn => Shopping = await Read(conn, ReadShopping, Shopping), text => Shopping = Shopping with { Error = text });
        }

        public static async Task<ShoppingRead> ReadShopping(DbConnection conn)
        {
            var held = new List<HeldRow>();
            await using (var cmd = Db.Cmd("SELECT * FROM fleet_shopping_held_view ORDER BY item_id, who, place", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    held.Add(new HeldRow(Int(r, "item_id"), Text(r, "who"), Text(r, "place"), Long(r, "qty")));
            string? progress = null;
            await using (var cmd = Db.Cmd("SELECT progress FROM fleet_shopping_progress_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                if (await r.ReadAsync()) progress = TextOrNull(r, "progress");
            var live = false;
            await using (var cmd = Db.Cmd("SELECT live_prices FROM fleet_market_settings_view", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                if (await r.ReadAsync()) live = BoolOrNull(r, "live_prices") == true;
            var exclude = new HashSet<string>();
            var worlds = new Dictionary<string, int>();
            if (live)
            {
                await using (var cmd = Db.Cmd("SELECT retainer_name AS name, world FROM fleet_retainers_view", conn))
                await using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync())
                        if (Text(r, "name").Length > 0) exclude.Add(Universalis.RetainerKey(Text(r, "name"), TextOrNull(r, "world")));
                await using (var cmd = Db.Cmd("SELECT world FROM fleet_parts_characters_view", conn))
                await using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync())
                        if (TextOrNull(r, "world") is { Length: > 0 } world) worlds[world] = worlds.GetValueOrDefault(world) + 1;
            }
            return new ShoppingRead(held, progress, exclude, live, worlds.Select(kv => (kv.Key, kv.Value)).ToList());
        }

        // --- In-game edits (2.x "ticking things off" stage): one at a time, in the background; once saved, everything is
        // read again at the next chance, so every page shows the change. ---

        private volatile bool editing;
        /// <summary>True while an edit is being saved.</summary>
        public bool Editing => editing;
        /// <summary>The last edit: what it was, why it failed (null: saved) and when.</summary>
        public (string What, string? Error, DateTime At)? LastEdit { get; private set; }

        /// <summary>Saves an edit (FleetEdits): edit answers null when done, else why not. done is told the same, on a background thread.</summary>
        public void Edit(string what, Func<DbConnection, Task<string?>> edit, Action<string?>? done = null)
        {
            if (editing)
            {
                LastEdit = (what, "Another change is still being saved. Try again in a moment.", DateTime.Now);
                return;
            }
            var cs = connectionString();
            if (cs == null)
            {
                LastEdit = (what, "The database isn't set up yet (Settings tab).", DateTime.Now);
                return;
            }
            editing = true;
            LastEdit = null;
            Task.Run(async () =>
            {
                string? error;
                try
                {
                    await using var conn = await OpenAsync(cs);
                    error = await edit(conn);
                }
                catch (PostgresException ex)
                {
                    error = ex.MessageText;
                }
                catch (Exception ex)
                {
                    error = ReachFailure(ex);
                }
                finally
                {
                    editing = false;
                }
                LastEdit = (what, error, DateTime.Now);
                if (error == null) ReadAgainSoon();
                done?.Invoke(error);
            });
        }

        /// <summary>Everything is read again at the next chance (after an edit).</summary>
        public void ReadAgainSoon()
        {
            subsAsked = suppliesAsked = listsAsked = moneyAsked = partsAsked = marketAsked = DateTime.MinValue;
            if (Shopping.At.HasValue) Shopping = Shopping with { At = DateTime.MinValue };
        }

        // --- The Trends page's reads (2.x Trends stage): what's recorded and whose, every 5 minutes while charts are shown;
        // and each chart, in the background, one at a time, kept for 5 minutes (the page asks again each minute, as its span
        // moves on). While a chart is read again, whoever asked keeps showing the last one it had. ---

        public static readonly TimeSpan TrendsEvery = TimeSpan.FromMinutes(5);
        public FleetRead<TrendsMeta> Trends { get; private set; } = new(null, null, null);
        private readonly object trendLock = new();
        private readonly Dictionary<string, (DateTime At, TrendResult? Result, string? Error)> trendCache = new();
        private readonly List<(string Key, TrendRequest Request)> trendQueue = new();
        private readonly Dictionary<string, string> trendWanted = new();
        private readonly Dictionary<string, (TrendResult? Result, string? Error)> trendShown = new();
        private bool trendWorking;

        public void ReadTrendsIfDue(bool force = false)
        {
            if (!force && Trends.At.HasValue && DateTime.Now - Trends.At.Value < TrendsEvery) return;
            RunInventoryRead(async conn => Trends = await Read(conn, ReadTrendsMeta, Trends), text => Trends = Trends with { Error = text });
        }

        /// <summary>Forgets the charts read, so each is read again (Refresh).</summary>
        public void ForgetTrends()
        {
            lock (trendLock) trendCache.Clear();
            ReadTrendsIfDue(force: true);
        }

        /// <summary>A chart for whoever asks (slot: "trends", "tile:gil" and so on): the one read for this request, else (while
        /// it's read) the last one that slot had; Loading while it's being read.</summary>
        public (TrendResult? Result, string? Error, bool Loading) Trend(string slot, TrendRequest request)
        {
            var key = request.Key;
            lock (trendLock)
            {
                trendWanted[slot] = key;
                if (trendCache.TryGetValue(key, out var hit))
                {
                    trendShown[slot] = (hit.Result, hit.Error);
                    if (DateTime.UtcNow - hit.At < TrendsEvery) return (hit.Result, hit.Error, false);
                }
                if (!trendQueue.Any(q => q.Key == key)) trendQueue.Add((key, request));
                if (!trendWorking) StartTrendWorker();
                var shown = trendShown.TryGetValue(slot, out var last) ? last : (null, null);
                return (shown.Result, shown.Error, true);
            }
        }

        // Reads the charts asked for, one after another on one connection. Called holding trendLock.
        private void StartTrendWorker()
        {
            var cs = connectionString();
            if (cs == null)
            {
                foreach (var (key, _) in trendQueue) Finished(key, null, "The database isn't set up yet (Settings tab).");
                trendQueue.Clear();
                return;
            }
            trendWorking = true;
            Task.Run(async () =>
            {
                try
                {
                    await using var conn = await OpenAsync(cs);
                    while (true)
                    {
                        (string Key, TrendRequest Request) job;
                        lock (trendLock)
                        {
                            if (trendQueue.Count == 0) break;
                            job = trendQueue[0];
                            trendQueue.RemoveAt(0);
                        }
                        TrendResult? result = null;
                        string? error = null;
                        try
                        {
                            result = await ReadSeries(conn, job.Request);
                        }
                        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
                        {
                            error = "The database has no recorded history yet.";
                        }
                        catch (PostgresException ex)
                        {
                            error = ex.MessageText;
                        }
                        lock (trendLock) Finished(job.Key, result, error);
                    }
                }
                catch (Exception ex)
                {
                    lock (trendLock)
                    {
                        foreach (var (key, _) in trendQueue) Finished(key, null, ReachFailure(ex));
                        trendQueue.Clear();
                    }
                }
                finally
                {
                    lock (trendLock) trendWorking = false;
                }
            });
        }

        // A chart has been read: kept (the oldest dropped past 100), and shown to whoever asked for it. Called holding trendLock.
        private void Finished(string key, TrendResult? result, string? error)
        {
            trendCache[key] = (DateTime.UtcNow, result, error);
            if (trendCache.Count > 100) trendCache.Remove(trendCache.OrderBy(kv => kv.Value.At).First().Key);
            foreach (var (slot, wanted) in trendWanted)
                if (wanted == key) trendShown[slot] = (result, error);
        }

        /// <summary>One chart (db/metric-series.js runSeries, in one go).</summary>
        public static async Task<TrendResult> ReadSeries(DbConnection conn, TrendRequest r)
        {
            if (Db.IsFile(conn)) return await FileReadSeries(conn, r);
            await using var cmd = new NpgsqlCommand(TrendsMath.SeriesSql, (NpgsqlConnection)conn);
            var at = (double ms) => DateTime.SpecifyKind(DateTime.UnixEpoch.AddTicks((long)Math.Round(ms * TimeSpan.TicksPerMillisecond)), DateTimeKind.Utc);
            cmd.Parameters.Add(new NpgsqlParameter { Value = at(r.From) });
            cmd.Parameters.Add(new NpgsqlParameter { Value = at(r.To) });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Points - 1 });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Parts.Select(p => p.Metric).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Parts.Select(p => p.SubjectType).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Parts.Select(p => (decimal)p.Weight).ToArray() });
            cmd.Parameters.Add(r.SubjectIds == null
                ? new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Numeric }
                : new NpgsqlParameter { Value = r.SubjectIds.Select(id => decimal.Parse(id, CultureInfo.InvariantCulture)).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Split });
            cmd.Parameters.Add(new NpgsqlParameter { Value = 0 });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Points - 1 });
            var rows = new List<(int, string?, double?)>();
            await using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    rows.Add((Int(reader, "i"), DecimalOrNull(reader, "subject_id")?.ToString(CultureInfo.InvariantCulture), Double(reader, "value")));
            return TrendsMath.Shape(rows, r);
        }

        private static async Task<TrendResult> FileReadSeries(DbConnection conn, TrendRequest r)
        {
            await using var cmd = Db.Cmd(TrendsMath.FileSeriesSql(r.Parts.Count, r.SubjectIds?.Count ?? 0), conn);
            Db.Add(cmd, "from", r.From / 1000.0);
            Db.Add(cmd, "to", r.To / 1000.0);
            Db.Add(cmd, "n", r.Points - 1);
            Db.Add(cmd, "first", 0);
            Db.Add(cmd, "last", r.Points - 1);
            Db.Add(cmd, "split", r.Split);
            for (var k = 0; k < r.Parts.Count; k++)
            {
                Db.Add(cmd, $"m{k}", r.Parts[k].Metric);
                Db.Add(cmd, $"t{k}", r.Parts[k].SubjectType);
                Db.Add(cmd, $"w{k}", (double)r.Parts[k].Weight);
            }
            for (var k = 0; k < (r.SubjectIds?.Count ?? 0); k++) Db.Add(cmd, $"s{k}", r.SubjectIds![k]);
            var rows = new List<(int, string?, double?)>();
            await using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    rows.Add((Int(reader, "i"), DecimalOrNull(reader, "subject_id")?.ToString(CultureInfo.InvariantCulture), Double(reader, "value")));
            return TrendsMath.Shape(rows, r);
        }

        /// <summary>What's recorded and whose (ipc/trends.js get-metric-catalog and get-metric-subjects).</summary>
        public static async Task<TrendsMeta> ReadTrendsMeta(DbConnection conn)
        {
            static double? Ms(DbDataReader r, string col)
            {
                var i = r.GetOrdinal(col);
                return r.IsDBNull(i) ? null : (Db.Time(r, i) - DateTime.UnixEpoch).TotalMilliseconds;
            }
            var catalog = new List<TrendCatalogRow>();
            await using (var cmd = Db.Cmd(Db.IsFile(conn) ? TrendsMath.FileCatalogSql : TrendsMath.CatalogSql, conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    catalog.Add(new TrendCatalogRow(Text(r, "metric"), Text(r, "subject_type"), Int(r, "subjects"), Ms(r, "first_recorded"), Ms(r, "last_recorded")));
            var characters = new List<TrendCharacter>();
            await using (var cmd = Db.Cmd(TrendsMath.CharactersSql, conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    characters.Add(new TrendCharacter(Decimal(r, "cid").ToString(CultureInfo.InvariantCulture), Text(r, "name"), TextOrNull(r, "world"), TextOrNull(r, "account_label"), IntOrNull(r, "sort_index")));
            var fcs = new List<TrendFc>();
            await using (var cmd = Db.Cmd(Db.IsFile(conn) ? TrendsMath.FileFcsSql : TrendsMath.FcsSql, conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    fcs.Add(new TrendFc(Decimal(r, "fc_id").ToString(CultureInfo.InvariantCulture), TextOrNull(r, "fc_name"), TextOrNull(r, "fc_master")));
            return new TrendsMeta(catalog, characters, fcs);
        }

        public void ReadHeldItemsIfDue(bool force = false)
        {
            if (!force && HeldItems.At.HasValue && DateTime.Now - HeldItems.At.Value < ListsEvery) return;
            RunInventoryRead(async conn => HeldItems = await Read(conn, ReadHeldItems, HeldItems), text => HeldItems = HeldItems with { Error = text });
        }

        private void RunInventoryRead(Func<DbConnection, Task> read, Action<string> failed)
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
                    await using var conn = await OpenAsync(cs);
                    await read(conn);
                }
                catch (Exception ex)
                {
                    failed(ReachFailure(ex));
                }
                finally
                {
                    inventoryBusy = false;
                }
            });
        }

        /// <summary>A class's role for its colour: "tank", "healer" or "dps", and whether it's a base class (Gladiator,
        /// Conjurer, ...) rather than the job it becomes; null role for gatherers, crafters or an unknown class. As the
        /// app's (renderer/tables.jsx jobRole).</summary>
        public static (string? Role, bool IsBase) JobRole(string abbreviation) => abbreviation switch
        {
            "PLD" or "WAR" or "DRK" or "GNB" => ("tank", false),
            "GLA" or "MRD" => ("tank", true),
            "WHM" or "SCH" or "AST" or "SGE" => ("healer", false),
            "CNJ" => ("healer", true),
            "MNK" or "DRG" or "NIN" or "SAM" or "RPR" or "VPR" or "BRD" or "MCH" or "DNC" or "BLM" or "SMN" or "RDM" or "PCT" or "BLU" => ("dps", false),
            "PGL" or "LNC" or "ROG" or "ARC" or "THM" or "ACN" => ("dps", true),
            _ => (null, false),
        };

        /// <summary>One item a retainer wears, from the game's files: its slot (EquipSlotCategory), its equip level and item
        /// level, and whether the retainer's class can wear it (null: unknown).</summary>
        public sealed record GearPiece(int SlotCategory, int? LevelEquip, int? LevelItem, bool? CanWear);

        // The slots a retainer's gear target checks, by EquipSlotCategory, as the app's Retainers tab (ipc/retainers.js).
        private static string SlotOf(int category) => category switch
        {
            1 or 13 or 14 => "Weapon", 2 => "Off-hand", 3 => "Head", 4 or 15 or 19 or 20 or 21 or 22 or 23 => "Chest",
            5 => "Gloves", 7 or 18 => "Legs", 8 => "Feet", 9 => "Earrings", 10 => "Necklace", 11 => "Bracelet", 12 => "Ring",
            _ => "Other",
        };

        /// <summary>The classes with something in the off-hand slot (a shield or a gathering tool); only theirs is
        /// checked when empty.</summary>
        public static bool HasOffHand(string abbreviation) => abbreviation is "GLA" or "PLD" or "MIN" or "BTN";

        /// <summary>
        /// Whether a retainer's gear meets its gear target, as the app's Retainers tab: "meets" when every piece is for its
        /// class and at the target (equip level in level mode, item level in ilvl mode) and no slot is empty; "short"
        /// otherwise; null with no target set. The off-hand only counts when empty for the classes that have one.
        /// </summary>
        public static string? GearCheck(string abbreviation, string? targetMode, int? targetValue, IReadOnlyList<GearPiece> pieces)
        {
            if (string.IsNullOrEmpty(targetMode) || targetValue == null) return null;
            foreach (var p in pieces)
            {
                if (p.CanWear == false) return "short";
                var value = targetMode == "level" ? p.LevelEquip : p.LevelItem;
                if (value != null && value != targetValue) return "short";
            }
            var slots = pieces.Select(p => SlotOf(p.SlotCategory)).ToList();
            var needed = new List<string> { "Weapon", "Head", "Chest", "Gloves", "Legs", "Feet", "Earrings", "Necklace", "Bracelet" };
            if (HasOffHand(abbreviation)) needed.Add("Off-hand");
            if (needed.Any(n => !slots.Contains(n)) || slots.Count(x => x == "Ring") < 2) return "short";
            return "meets";
        }

        /// <summary>The clock time something finishes, for after its countdown: "5:12 PM" today, "Thu 5:12 PM" later.</summary>
        public static string FinishClock(long endsAtUnix, DateTime now)
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(endsAtUnix).LocalDateTime;
            var clock = at.ToString("t", CultureInfo.CurrentCulture);
            return at.Date == now.Date ? clock : $"{at.ToString("ddd", CultureInfo.CurrentCulture)} {clock}";
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
        /// The info bar line: "Fleet" when nothing needs you, else what does, for example "Fleet: 4 back · 2 need parts · 1
        /// low · 2 to check" (subs back and waiting; subs ready for new parts; characters low on supplies; anything else
        /// that needs attention).
        /// </summary>
        public static string InfoBarText(int subsBack, IReadOnlyCollection<AlertRow>? alerts, int subsReadyForParts = 0)
        {
            var parts = new List<string>();
            if (subsBack > 0) parts.Add($"{subsBack} back");
            if (subsReadyForParts > 0) parts.Add($"{subsReadyForParts} need parts");
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
