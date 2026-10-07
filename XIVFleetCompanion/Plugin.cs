using AutoRetainerAPI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.EzEventManager;
using Npgsql;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using XIVFleetCompanion.Windows;

namespace XIVFleetCompanion;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/xivfleet";

    // The plugin's version (from the project file), shown in window titles and the log.
    internal static string VersionText => PluginInterface.Manifest.AssemblyVersion?.ToString(3) ?? "unknown";

    public Configuration Configuration { get; init; }
    public AutoRetainerApi? AutoRetainer { get; private set; }
    public AllaganToolsConnector? AllaganTools { get; private set; }
    private readonly SyncSchedule schedule = new();
    private bool syncInProgress = false;

    // What was last written per character / FC, so unchanged data is not written again every
    // sync. Forgotten once an hour, so everything is rewritten at least hourly.
    private readonly ChangeTracker changes = new(TimeSpan.FromHours(1), DateTime.UtcNow);
    private bool warnedNoCurrentTable = false;

    public readonly WindowSystem WindowSystem = new("XIVFleetCompanion");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private CheckWindow CheckWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        if (string.IsNullOrWhiteSpace(Configuration.FCTrackerConfigPath))
        {
            var guessedPath = DefaultFcTrackerConfigPath();
            if (guessedPath != null)
            {
                Configuration.FCTrackerConfigPath = guessedPath;
                Configuration.Save();
            }
        }

        ECommonsMain.Init(PluginInterface, this);
        AutoRetainer = new AutoRetainerApi();
        AllaganTools = new AllaganToolsConnector(PluginInterface);

        Framework.Update += OnFrameworkUpdate;
        ClientState.Logout += OnLogout;

        var submarineImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "submarine.png");

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this, submarineImagePath);
        CheckWindow = new CheckWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(CheckWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the XIV Fleet Companion main window. /xivfleet check shows what the plugin can see for each character."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        // The settings and main window buttons in the plugin installer.
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information($"{PluginInterface.Manifest.Name} loaded — version {VersionText}.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        CheckWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        Framework.Update -= OnFrameworkUpdate;
        ClientState.Logout -= OnLogout;

        AutoRetainer?.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("check", StringComparison.OrdinalIgnoreCase))
            OpenCheck();
        else
            MainWindow.Toggle();
    }

    public void OpenCheck() => CheckWindow.OpenAndRun();

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    // AutoRetainer logs a character out once it has finished with it: sync that news soon instead of
    // waiting for the next interval (see SyncSchedule).
    private void OnLogout(int type, int code)
    {
        if (Configuration.Enabled && Configuration.SyncAfterLogout)
            schedule.RequestSoon(DateTime.UtcNow);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Configuration.Enabled) return;

        var now = DateTime.UtcNow;
        var reason = schedule.ShouldStart(now, TimeSpan.FromMinutes(Configuration.SyncIntervalMinutes), syncInProgress);
        if (reason != null)
        {
            schedule.Started(now);
            syncInProgress = true;
            if (reason == "after logout") Log.Information("Fleet Companion: syncing after a character logged out.");

            Task.Run(async () =>
            {
                try
                {
                    await RunSyncAsync();
                }
                catch (Exception ex)
                {
                    Log.Error($"Fleet Companion sync failed: {ex}");
                }
                finally
                {
                    syncInProgress = false;
                }
            });
        }
    }

    // Item ids whose quantities are recorded as history for the salvage charts
    // (same items the app's Salvage view prices).
    private static readonly uint[] SalvageItemIds = { 22500, 22501, 22502, 22503, 22504, 22505, 22506, 22507 };

    // Ceruleum tanks and Magitek repair materials across bags and retainers, so the app can tell supplies
    // used on voyages (the total goes down) from supplies moved to a retainer (it does not).
    private static readonly uint[] SupplyItemIds = { 10155, 10373 };
    private bool warnedNoItemsSeenColumn = false;

    private static void AddMetric(
        Dictionary<(string Type, ulong Id, string Metric), (decimal Value, string? Label)> metrics,
        string subjectType, ulong subjectId, string metric, decimal value, string? label = null)
        => metrics[(subjectType, subjectId, metric)] = (value, label);

    // Where FCTracker keeps its config in this XIVLauncher install: next to this plugin's own config
    // folder, under pluginConfigs\FCTracker. Null if that folder cannot be worked out.
    internal static string? DefaultFcTrackerConfigPath()
    {
        var pluginConfigsRoot = Directory.GetParent(PluginInterface.ConfigDirectory.FullName)?.FullName;
        return pluginConfigsRoot == null ? null : Path.Combine(pluginConfigsRoot, "FCTracker", "FCTrackerConfig.json");
    }

    // What one character's sync needs from AutoRetainer, copied on the game's thread.
    private sealed class CharacterRead
    {
        public FleetWriter.CharacterSnapshot Snapshot = new();
        public List<FleetWriter.RetainerRecord> Retainers = new();
        public List<FleetWriter.SubmarineRecord> Subs = new();
    }

    // AutoRetainer changes its data on the game's thread, so it is copied there (this runs through
    // Framework.RunOnFrameworkThread); the rest of the sync then works on the copy in the background.
    // Null when AutoRetainer is not ready. Registered is how many characters AutoRetainer lists;
    // Left out is how many of those are not chosen for syncing in settings (they are not read at all).
    private (List<CharacterRead> Characters, int Registered, int LeftOut)? ReadAutoRetainer()
    {
        if (AutoRetainer == null || !AutoRetainer.Ready) return null;

        var cids = AutoRetainer.GetRegisteredCharacters();
        var characters = new List<CharacterRead>();
        var leftOut = 0;
        foreach (var cid in cids)
        {
            if (!Configuration.ShouldSync(cid))
            {
                leftOut++;
                continue;
            }

            var data = AutoRetainer.GetOfflineCharacterData(cid);
            if (data == null || data.CID == 0) continue;

            var read = new CharacterRead
            {
                Snapshot = new FleetWriter.CharacterSnapshot
                {
                    Cid = data.CID,
                    Name = data.Name,
                    World = data.World,
                    RetainerCount = data.RetainerData.Count,
                    SubmarineCount = data.OfflineSubmarineData.Count,
                    Gil = data.Gil,
                    Ceruleum = data.Ceruleum,
                    RepairKits = data.RepairKits,
                    AccountLabel = Configuration.AccountLabel,
                    FcId = data.FCID,
                    NumSubSlots = data.NumSubSlots,
                },
                Retainers = data.RetainerData.Select((retainer, index) => new FleetWriter.RetainerRecord
                {
                    RetainerId = retainer.RetainerID,
                    Name = retainer.Name,
                    Job = retainer.Job,
                    Gil = retainer.Gil,
                    HasVenture = retainer.HasVenture,
                    VentureId = retainer.VentureID,
                    VentureBeginsAt = retainer.VentureBeginsAt,
                    VentureEndsAt = retainer.VentureEndsAt,
                    Level = retainer.Level,
                    HireOrderIndex = index,
                }).ToList(),
            };

            // AdditionalSubmarineData holds build/rank (keyed by sub name); OfflineSubmarineData is
            // the character's list of subs in workshop order, with voyage return times. A sub is
            // written when it is in the list and has build data (one with no build yet has nothing
            // to write); its slot is its place in the list. Build data under a name that is no longer
            // in the list (a renamed sub) is left out, so it does not linger as an extra sub.
            var registeredSubs = data.OfflineSubmarineData.Select(v => v.Name).ToList();
            foreach (var (subName, slot) in FleetWriter.PlanSubmarines(registeredSubs, data.AdditionalSubmarineData.Keys))
            {
                var vesselData = data.AdditionalSubmarineData[subName];
                var voyage = data.OfflineSubmarineData.Find(v => v.Name == subName);

                read.Subs.Add(new FleetWriter.SubmarineRecord
                {
                    SubName = subName,
                    Level = vesselData.Level,
                    Part1 = vesselData.Part1,
                    Part2 = vesselData.Part2,
                    Part3 = vesselData.Part3,
                    Part4 = vesselData.Part4,
                    Points = (byte[]?)vesselData.Points?.Clone() ?? Array.Empty<byte>(),
                    ReturnTime = voyage != null ? voyage.ReturnTime : (long?)null,
                    CurrentExp = vesselData.CurrentExp,
                    NextLevelExp = vesselData.NextLevelExp,
                    Slot = slot,
                });
            }

            characters.Add(read);
        }
        return (characters, cids.Count, leftOut);
    }

    private async Task RunSyncAsync()
    {
        var fromAutoRetainer = await Framework.RunOnFrameworkThread(() => ReadAutoRetainer());
        if (fromAutoRetainer == null) return;
        var (characters, registeredCount, leftOutCount) = fromAutoRetainer.Value;

        var charactersWithoutFc = new HashSet<ulong>();
        var fcTrackerHousing = FCTrackerConnector.ReadHousingData(Configuration.FCTrackerConfigPath, charactersWithoutFc);
        Log.Information($"Fleet Companion: FCTracker path='{Configuration.FCTrackerConfigPath}' parsed {fcTrackerHousing.Count} housing entries.");

        // Asked once per sync. When AllaganTools is not running, inventories and FC chests
        // keep their last stored contents (see below).
        var allaganToolsReady = AllaganTools != null && AllaganTools.IsReady();
        if (!allaganToolsReady)
            Log.Warning("Fleet Companion: AllaganTools is not available; inventories and FC chests were not updated this sync.");

        // One connection for the whole sync. A sync that cannot connect is reported by the
        // caller and simply tried again next time.
        await using var openConnection = await PostgresWriter.OpenConnectionAsync(Configuration.UseRemoteConnection);
        if (openConnection == null)
        {
            Log.Warning("Fleet Companion: no saved Postgres credential; nothing was synced.");
            return;
        }
        NpgsqlConnection conn = openConnection;

        changes.BeginSync(DateTime.UtcNow);
        if (!detailsTablesReady && DateTime.UtcNow >= detailsTablesAskAgainAt) detailsTablesReady = true;
        var written = 0;
        var unchanged = 0;
        var failed = 0;

        // Writes only when the data differs from what this plugin last wrote for the same key.
        // A database error (for example a constraint) is logged and that entry is retried next
        // sync; a lost connection ends the sync.
        async Task WriteIfChanged(string key, string fingerprint, Func<Task> write, string what)
        {
            if (changes.IsUnchanged(key, fingerprint))
            {
                unchanged++;
                return;
            }
            try
            {
                await write();
                changes.Remember(key, fingerprint);
                written++;
            }
            catch (PostgresException ex)
            {
                changes.Forget(key);
                failed++;
                Log.Warning($"Fleet Companion: failed to write {what} — {ex.Message}");
            }
        }

        // What this PC could not see this sync, in plain sentences, for the app's PCs panel.
        var problems = new List<string>();
        if (!allaganToolsReady) problems.Add("AllaganTools is not running: bags, retainers' items and FC chests are not updated.");

        var snapshots = new List<FleetWriter.CharacterSnapshot>();
        var metrics = new Dictionary<(string Type, ulong Id, string Metric), (decimal Value, string? Label)>();

        foreach (var read in characters)
        {
            var character = read.Snapshot;
            var cid = character.Cid;
            var who = $"{character.Name}@{character.World}";

            snapshots.Add(character);

            AddMetric(metrics, "character", cid, "gil", character.Gil);
            AddMetric(metrics, "character", cid, "ceruleum", character.Ceruleum);
            AddMetric(metrics, "character", cid, "repair_kits", character.RepairKits);
            AddMetric(metrics, "character", cid, "retainer_count", character.RetainerCount);
            AddMetric(metrics, "character", cid, "submarine_count", character.SubmarineCount);
            AddMetric(metrics, "character", cid, "num_sub_slots", character.NumSubSlots);

            // Retainer details come from AutoRetainer, so they are written whether or not
            // AllaganTools is available.
            var retainers = read.Retainers;
            if (retainers.Count > 0)
                await WriteIfChanged($"retainers:{cid}", FleetWriter.Fingerprint(retainers),
                    () => FleetWriter.WriteRetainersAsync(conn, cid, retainers), $"retainers for {who}");

            // Items come from AllaganTools per source: the character's own bags and each retainer.
            // AllaganTools answers with nothing at all (not even empty slots) for a source it has
            // never seen, such as a retainer never opened at a bell on this PC; that source keeps its
            // last stored items instead of being cleared, and the log says which retainer to open.
            if (allaganToolsReady)
            {
                var sources = new List<FleetWriter.InventorySource>
                {
                    new() { Id = cid, Name = who, SeenItems = SeenItems(cid) },
                };
                foreach (var retainer in retainers)
                    sources.Add(new() { Id = retainer.RetainerId, Name = retainer.Name, SeenItems = SeenItems(retainer.RetainerId) });

                var plan = FleetWriter.PlanInventory(cid, sources, retainerListKnown: retainers.Count > 0);
                if (!plan.BagsSeen)
                    problems.Add($"AllaganTools has not seen {who}'s bags: log in as {character.Name} once.");
                foreach (var unseen in plan.Unseen)
                    problems.Add($"Open retainer {unseen.Name} ({who}) at a summoning bell.");
                if (!plan.BagsSeen)
                    WarnOnce($"bags:{cid}", $"Fleet Companion: AllaganTools has not seen {who}'s bags on this PC (log in as {who} once with AllaganTools running); their last stored items are kept.");
                foreach (var unseen in plan.Unseen)
                    WarnOnce($"retainer:{unseen.Id}", $"Fleet Companion: AllaganTools has not seen retainer {unseen.Name}'s items ({who}); open {unseen.Name} at a summoning bell on this PC. Its last stored items are kept.");

                if (plan.Read.Count > 0)
                {
                    await WriteIfChanged($"inventory:{cid}", FleetWriter.Fingerprint(plan),
                        () => FleetWriter.WriteInventoryAsync(conn, cid, plan), $"inventory for {who}");

                    // When AllaganTools saw each retainer (sql/007), so the app can tell an empty retainer
                    // from one never opened at a bell.
                    var seenRetainers = plan.Read.Where(id => id != cid).ToList();
                    try
                    {
                        if (!warnedNoItemsSeenColumn && !await FleetWriter.MarkRetainersSeenAsync(conn, seenRetainers))
                        {
                            warnedNoItemsSeenColumn = true;
                            Log.Information("Fleet Companion: run sql/007_retainer_items_seen.sql once so the app can tell empty retainers from unseen ones.");
                        }
                    }
                    catch (PostgresException ex)
                    {
                        Log.Warning($"Fleet Companion: could not record when {who}'s retainers were seen — {ex.Message}");
                    }

                    // Salvage and supply quantities for the charts, income and supplies, from what is now
                    // stored (fresh items, plus the last stored items of any retainer not seen), so a
                    // retainer AllaganTools missed is never recorded as a drop to zero. inventory_sources
                    // goes up when a source's items are stored for the first time, which the app does not
                    // count as income.
                    try
                    {
                        var totals = await FleetWriter.ReadItemTotalsAsync(conn, cid, SalvageItemIds.Concat(SupplyItemIds).ToArray());
                        foreach (var itemId in SalvageItemIds.Concat(SupplyItemIds))
                            AddMetric(metrics, "character", cid, $"item_qty:{itemId}", totals[itemId]);
                        AddMetric(metrics, "character", cid, "inventory_sources", await FleetWriter.ReadStoredSourceCountAsync(conn, cid));
                    }
                    catch (PostgresException ex)
                    {
                        Log.Warning($"Fleet Companion: could not read stored salvage counts for {who} — {ex.Message}");
                    }
                }

                // The retainers' market listings (sql/008), for each retainer AllaganTools saw.
                var seenSources = sources.Where(src => src.Id != cid && src.SeenItems != null).ToList();
                if (seenSources.Count > 0 && detailsTablesReady)
                {
                    var listings = seenSources.SelectMany(src => src.SeenItems!
                        .Where(i => i.SortedContainer == RetainerMarketContainer)
                        .Select(i => new DetailsWriter.Listing
                        {
                            RetainerId = src.Id, Slot = i.SortedSlotIndex, ItemId = i.ItemId, Quantity = i.Quantity,
                            Hq = i.Hq, UnitPrice = i.MarketPrice,
                        })).ToList();
                    var seenIds = seenSources.Select(src => src.Id).ToList();
                    await WriteIfChanged($"listings:{cid}", string.Join(",", seenIds.OrderBy(id => id)) + "#" + DetailsWriter.Fingerprint(listings), async () =>
                    {
                        if (!await DetailsWriter.WriteListingsAsync(conn, cid, seenIds, listings)) DetailsTablesMissing();
                    }, $"market listings for {who}");
                }

                // FC chest data comes from the FC's own id. A chest AllaganTools has never seen (not
                // opened on this PC) answers with nothing and is left as stored; a seen chest is
                // written even with zero items, so a chest that was emptied is cleared. Keyed by FC,
                // so characters sharing an FC do not write the same chest twice.
                if (character.FcId != 0)
                {
                    var chestSeen = SeenItems(character.FcId);
                    if (chestSeen == null)
                    {
                        problems.Add($"Open the Free Company chest of {who}'s FC.");
                        WarnOnce($"chest:{character.FcId}", $"Fleet Companion: AllaganTools has not seen the Free Company chest of {who}'s FC; open the chest on this PC. Its last stored items are kept.");
                    }
                    else
                    {
                        var chest = chestSeen.Where(i => i.SortedContainer >= 20000 && i.SortedContainer <= 20004).ToList();
                        foreach (var salvageId in SalvageItemIds)
                            AddMetric(metrics, "fc", character.FcId, $"item_qty:{salvageId}",
                                chest.Where(i => i.ItemId == salvageId).Sum(i => (long)i.Quantity));
                        await WriteIfChanged($"fcchest:{character.FcId}", FleetWriter.Fingerprint(chest),
                            () => FleetWriter.WriteFcInventoryAsync(conn, character.FcId, chest), $"FC chest inventory for {who}");
                    }
                }
            }

            var subRecords = read.Subs;

            // Each sub's rank and experience, by workshop slot, so the app can see how fast it ranks up.
            foreach (var sub in subRecords.Where(s => s.Slot != null))
            {
                AddMetric(metrics, "character", cid, $"sub_rank:{sub.Slot}", sub.Level, sub.SubName);
                AddMetric(metrics, "character", cid, $"sub_exp:{sub.Slot}", sub.CurrentExp, sub.SubName);
            }
            await WriteIfChanged($"subs:{cid}", FleetWriter.Fingerprint(subRecords),
                () => FleetWriter.WriteSubmarinesAsync(conn, cid, subRecords), $"submarines for {who}");

            if (fcTrackerHousing.TryGetValue(cid, out var housing))
            {
                if (housing.FcId != 0)
                {
                    // FC points belong to the FC (labelled with the leader's name) and, when
                    // this character IS the leader, also to this character (labelled with the
                    // FC's name). The leader is matched by name within the same FC.
                    AddMetric(metrics, "fc", housing.FcId, "fc_points", housing.FcPoints,
                        string.IsNullOrWhiteSpace(housing.FcMaster) ? null : housing.FcMaster);

                    if (character.FcId == housing.FcId
                        && string.Equals(character.Name, housing.FcMaster, StringComparison.OrdinalIgnoreCase))
                    {
                        AddMetric(metrics, "character", cid, "fc_points", housing.FcPoints,
                            string.IsNullOrWhiteSpace(housing.FcName) ? null : housing.FcName);
                    }
                }

                await WriteIfChanged($"housing:{cid}", FleetWriter.Fingerprint(housing),
                    () => FleetWriter.WriteHousingAsync(conn, cid, housing), $"housing for {who}");
            }
            else if (character.FcId == 0 && charactersWithoutFc.Contains(cid))
            {
                // FCTracker and AutoRetainer both say this character is in no Free Company (it
                // left): its old FC and house details go.
                await WriteIfChanged($"housing:{cid}", "no free company",
                    () => FleetWriter.RemoveHousingAsync(conn, new[] { cid }), $"old Free Company details for {who}");
            }
        }

        // Every character's current row, in one statement. The table comes from
        // sql/004_character_current.sql; until that has been run, characters are not saved (one
        // warning per plugin session).
        var synced = 0;
        try
        {
            synced = await FleetWriter.WriteCurrentCharactersAsync(conn, snapshots);

            // With every character's Free Company now current, chests of FCs none of them is in go.
            var removedSlots = await FleetWriter.RemoveOrphanFcChestsAsync(conn);
            if (removedSlots > 0)
                Log.Information($"Fleet Companion: removed {removedSlots} chest slots of Free Companies no tracked character is in.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            if (!warnedNoCurrentTable)
                Log.Warning("Fleet Companion: companion_character_current does not exist; run sql/004_character_current.sql once. Characters are not saved until then.");
            warnedNoCurrentTable = true;
        }
        catch (PostgresException ex)
        {
            Log.Warning($"Fleet Companion: failed to write current character rows — {ex.Message}");
        }

        if (detailsTablesReady)
            await WriteDetailsAsync(conn, characters, leftOutCount, allaganToolsReady, fcTrackerHousing.Count > 0 || File.Exists(Configuration.FCTrackerConfigPath), problems);

        var metricPoints = metrics.Select(kv => new PostgresWriter.MetricPoint
        {
            SubjectType = kv.Key.Type,
            SubjectId = kv.Key.Id,
            Metric = kv.Key.Metric,
            Value = kv.Value.Value,
            Label = kv.Value.Label
        }).ToList();

        var metricResult = await PostgresWriter.WriteMetricsAsync(conn, metricPoints);
        if (!metricResult.StartsWith("Success"))
            Log.Warning($"Fleet Companion: failed to write metric history — {metricResult}");

        // "Last sync" means the characters were actually written.
        if (synced > 0)
        {
            Configuration.LastSyncTimestamp = DateTime.Now;
            Configuration.Save();
        }

        var leftOutText = leftOutCount > 0 ? $" ({leftOutCount} left out in settings)" : "";
        Log.Information($"Fleet Companion: synced {synced}/{registeredCount - leftOutCount} characters{leftOutText}; {written} changed entries written, {unchanged} unchanged skipped, {failed} failed.");
    }

    // The items AllaganTools has for one character, retainer or FC (empty slots left out), or null when
    // it has never seen that source: it then answers with nothing at all, not even empty slots.
    private List<FleetWriter.InventoryItem>? SeenItems(ulong sourceId)
    {
        var raw = AllaganTools?.GetCharacterItems(sourceId);
        if (raw == null || raw.Count == 0) return null;
        return raw.Where(i => i.Quantity > 0).Select(ToInventoryItem).ToList();
    }

    // A warning about something only the player can fix (open a retainer at a bell) is logged once per
    // plugin session, not every sync.
    private readonly HashSet<string> warned = new();
    private void WarnOnce(string key, string message)
    {
        lock (warned)
        {
            if (!warned.Add(key)) return;
        }
        Log.Warning(message);
    }

    private static FleetWriter.InventoryItem ToInventoryItem(AllaganToolsConnector.ParsedItem item) => new()
    {
        RetainerId = item.RetainerId,
        SortedContainer = item.SortedContainer,
        SortedSlotIndex = item.SortedSlotIndex,
        ItemId = item.ItemId,
        Quantity = item.Quantity,
        GearSetIds = item.GearSetIds,
        MarketPrice = item.MarketPrice,
        Hq = item.Hq,
    };

    // AllaganTools' container for the items a retainer has up for sale.
    private const uint RetainerMarketContainer = 12002;

    // The sql/008 tables (PCs, voyage loot, venture rewards, market listings). Until it has been run, they
    // are skipped after one log line; asked again every 30 minutes in case it has been run since.
    private bool detailsTablesReady = true;
    private DateTime detailsTablesAskAgainAt = DateTime.MinValue;

    private bool loggedDetailsTablesMissing = false;

    private void DetailsTablesMissing()
    {
        if (!loggedDetailsTablesMissing)
            Log.Information("Fleet Companion: run sql/008_pcs_loot_ventures_listings.sql once so the app can show PCs, loot per voyage, venture income and market listings.");
        loggedDetailsTablesMissing = true;
        detailsTablesReady = false;
        detailsTablesAskAgainAt = DateTime.UtcNow.AddMinutes(30);
    }

    // The other plugins' config folders sit next to this plugin's own.
    private static string? OtherPluginFolder(string name)
    {
        var root = Directory.GetParent(PluginInterface.ConfigDirectory.FullName)?.FullName;
        return root == null ? null : Path.Combine(root, name);
    }

    // AutoRetainer's statistics files already read, by when they last changed, so unchanged ones are not
    // read again.
    private readonly Dictionary<string, DateTime> ventureFilesRead = new();

    // This PC's status, voyage loot from SubmarineTracker and venture rewards from AutoRetainer (sql/008).
    private async Task WriteDetailsAsync(NpgsqlConnection conn, List<CharacterRead> characters, int leftOut,
        bool allaganToolsReady, bool fcTrackerFound, List<string> problems)
    {
        var cids = characters.Select(c => c.Snapshot.Cid).ToList();
        var fcIds = characters.Select(c => c.Snapshot.FcId).Where(f => f != 0).Distinct().ToList();

        // Voyage loot, newer than what is stored.
        var stFolder = OtherPluginFolder("SubmarineTracker");
        var stPath = stFolder == null ? null : Path.Combine(stFolder, SubmarineTrackerReader.FileName);
        var stFound = stPath != null && File.Exists(stPath);
        if (stFound && fcIds.Count > 0)
        {
            try
            {
                var newest = await DetailsWriter.ReadNewestVoyagesAsync(conn, fcIds);
                if (newest == null) { DetailsTablesMissing(); return; }
                var loot = SubmarineTrackerReader.ReadLoot(stPath!, newest, fcIds);
                if (!await DetailsWriter.WriteVoyageLootAsync(conn, loot)) { DetailsTablesMissing(); return; }
                if (loot.Count > 0) Log.Information($"Fleet Companion: stored {loot.Count} voyage sectors from SubmarineTracker.");
            }
            catch (Exception ex) when (ex is not PostgresException)
            {
                WarnOnce("submarinetracker", $"Fleet Companion: could not read SubmarineTracker's loot ({stPath}) — {ex.Message}");
            }
        }
        else if (!stFound && fcIds.Count > 0)
        {
            problems.Add("SubmarineTracker was not found: loot per voyage is not recorded.");
        }

        // Venture rewards, from the statistics files of the characters synced here.
        var arFolder = OtherPluginFolder("AutoRetainer");
        var statFiles = arFolder != null && Directory.Exists(arFolder)
            ? Directory.GetFiles(arFolder, VentureStatsReader.Pattern) : Array.Empty<string>();
        var wanted = new HashSet<ulong>(cids);
        var changedFiles = new List<(string Path, ulong Cid, string Retainer, DateTime Changed)>();
        foreach (var path in statFiles)
        {
            if (VentureStatsReader.ParseFileName(Path.GetFileName(path)) is not { } parsed || !wanted.Contains(parsed.Cid)) continue;
            var changed = File.GetLastWriteTimeUtc(path);
            if (ventureFilesRead.TryGetValue(path, out var read) && read == changed) continue;
            changedFiles.Add((path, parsed.Cid, parsed.Retainer, changed));
        }
        if (changedFiles.Count > 0)
        {
            var newest = await DetailsWriter.ReadNewestVenturesAsync(conn, cids);
            if (newest == null) { DetailsTablesMissing(); return; }
            foreach (var file in changedFiles)
            {
                try
                {
                    var since = newest.TryGetValue((file.Cid, file.Retainer), out var s) ? s : 0;
                    var rows = VentureStatsReader.ReadFile(file.Path, file.Cid, file.Retainer, since);
                    if (!await DetailsWriter.WriteVenturesAsync(conn, rows)) { DetailsTablesMissing(); return; }
                    ventureFilesRead[file.Path] = file.Changed;
                }
                catch (Exception ex) when (ex is not PostgresException)
                {
                    WarnOnce($"ventures:{file.Path}", $"Fleet Companion: could not read AutoRetainer's venture statistics ({file.Path}) — {ex.Message}");
                }
            }
        }

        if (!fcTrackerFound) problems.Add("FCTracker's file was not found: Free Company and house details are not updated.");
        var status = new DetailsWriter.PcStatus
        {
            PcName = Environment.MachineName,
            AccountLabel = Configuration.AccountLabel ?? "",
            PluginVersion = VersionText,
            AutoRetainerReady = true,
            AllaganToolsReady = allaganToolsReady,
            FcTrackerFound = fcTrackerFound,
            SubmarineTrackerFound = stFound,
            VentureStatsFound = statFiles.Length > 0,
            CharactersSynced = characters.Count,
            CharactersLeftOut = leftOut,
            Problems = problems,
        };
        if (!await DetailsWriter.WritePcStatusAsync(conn, status)) DetailsTablesMissing();
    }
}
