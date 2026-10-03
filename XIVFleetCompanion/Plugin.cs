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

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the XIV Fleet Companion main window."
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

        CommandManager.RemoveHandler(CommandName);

        Framework.Update -= OnFrameworkUpdate;
        ClientState.Logout -= OnLogout;

        AutoRetainer?.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }

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
    // Null when AutoRetainer is not ready. Registered is how many characters AutoRetainer lists.
    private (List<CharacterRead> Characters, int Registered)? ReadAutoRetainer()
    {
        if (AutoRetainer == null || !AutoRetainer.Ready) return null;

        var cids = AutoRetainer.GetRegisteredCharacters();
        var characters = new List<CharacterRead>();
        foreach (var cid in cids)
        {
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
        return (characters, cids.Count);
    }

    private async Task RunSyncAsync()
    {
        var fromAutoRetainer = await Framework.RunOnFrameworkThread(() => ReadAutoRetainer());
        if (fromAutoRetainer == null) return;
        var (characters, registeredCount) = fromAutoRetainer.Value;

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

            // Inventories are replaced as a whole, so they are only written when AllaganTools
            // actually answered. Otherwise (AllaganTools disabled, updating after a patch, or
            // with nothing cached for this character yet) the stored inventory is left as it
            // was instead of being emptied.
            if (allaganToolsReady)
            {
                var personalItems = AllaganTools!.GetCharacterItems(cid);
                var personalAndRetainerItems = personalItems?.Where(i => i.Quantity > 0).ToList();

                // A character always carries something (at least the gear they wear), so
                // an empty answer means AllaganTools has no data for them, not an empty bag.
                var inventoryComplete = personalAndRetainerItems != null && personalAndRetainerItems.Count > 0;
                if (inventoryComplete)
                {
                    foreach (var retainer in retainers)
                    {
                        var retainerItems = AllaganTools.GetCharacterItems(retainer.RetainerId);
                        if (retainerItems == null)
                        {
                            inventoryComplete = false;
                            break;
                        }
                        personalAndRetainerItems!.AddRange(retainerItems.Where(i => i.Quantity > 0));
                    }
                }

                // FC chest data comes from the FC's own ID, not from a
                // character's personal items. Null when it could not be read.
                List<AllaganToolsConnector.ParsedItem>? fcChestItems = null;
                if (character.FcId != 0)
                {
                    fcChestItems = AllaganTools.GetCharacterItems(character.FcId)?
                        .Where(i => i.Quantity > 0 && i.SortedContainer >= 20000 && i.SortedContainer <= 20004)
                        .ToList();
                }

                // Salvage item quantities (bags + retainers for the character, chest for
                // the FC). Only recorded when AllaganTools actually returned items, so a
                // missing cache is never stored as a real drop to zero.
                if (inventoryComplete)
                {
                    foreach (var salvageId in SalvageItemIds)
                        AddMetric(metrics, "character", cid, $"item_qty:{salvageId}",
                            personalAndRetainerItems!.Where(i => i.ItemId == salvageId).Sum(i => (long)i.Quantity));
                }

                if (character.FcId != 0 && fcChestItems != null && fcChestItems.Count > 0)
                {
                    foreach (var salvageId in SalvageItemIds)
                        AddMetric(metrics, "fc", character.FcId, $"item_qty:{salvageId}",
                            fcChestItems.Where(i => i.ItemId == salvageId).Sum(i => (long)i.Quantity));
                }

                if (inventoryComplete)
                {
                    var inventory = personalAndRetainerItems!.Select(ToInventoryItem).ToList();
                    await WriteIfChanged($"inventory:{cid}", FleetWriter.Fingerprint(inventory),
                        () => FleetWriter.WriteInventoryAsync(conn, cid, inventory), $"inventory for {who}");
                }
                else
                {
                    Log.Warning($"Fleet Companion: AllaganTools returned no inventory for {who}; the stored inventory was left as it was.");
                }

                // Written even with zero items, so a chest that was emptied is cleared.
                // AllaganTools only has fresh FC chest data after the in-game
                // FC chest UI has been opened. Skipped when the chest could not be read.
                // Keyed by FC, so characters sharing an FC do not write the same chest twice.
                if (character.FcId != 0 && fcChestItems != null)
                {
                    var chest = fcChestItems.Select(ToInventoryItem).ToList();
                    await WriteIfChanged($"fcchest:{character.FcId}", FleetWriter.Fingerprint(chest),
                        () => FleetWriter.WriteFcInventoryAsync(conn, character.FcId, chest), $"FC chest inventory for {who}");
                }
            }

            var subRecords = read.Subs;
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

        Log.Information($"Fleet Companion: synced {synced}/{registeredCount} characters; {written} changed entries written, {unchanged} unchanged skipped, {failed} failed.");
    }

    private static FleetWriter.InventoryItem ToInventoryItem(AllaganToolsConnector.ParsedItem item) => new()
    {
        RetainerId = item.RetainerId,
        SortedContainer = item.SortedContainer,
        SortedSlotIndex = item.SortedSlotIndex,
        ItemId = item.ItemId,
        Quantity = item.Quantity,
        GearSetIds = item.GearSetIds,
    };
}
