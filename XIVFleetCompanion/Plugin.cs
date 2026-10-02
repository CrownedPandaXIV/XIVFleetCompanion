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
    private DateTime lastSyncCheck = DateTime.MinValue;
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
            var ownConfigDir = PluginInterface.ConfigDirectory.FullName;
            var pluginConfigsRoot = Directory.GetParent(ownConfigDir)?.FullName;
            if (pluginConfigsRoot != null)
            {
                var guessedPath = Path.Combine(pluginConfigsRoot, "FCTracker", "FCTrackerConfig.json");
                Configuration.FCTrackerConfigPath = guessedPath;
                Configuration.Save();
            }
        }

        ECommonsMain.Init(PluginInterface, this);
        AutoRetainer = new AutoRetainerApi();
        AllaganTools = new AllaganToolsConnector(PluginInterface);

        Framework.Update += OnFrameworkUpdate;

        var submarineImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "submarine.png");

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this, submarineImagePath);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the XIV Fleet Companion main window."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [XIVFleetCompanion] ===A cool log message from Sample Plugin===
        Log.Information($"{PluginInterface.Manifest.Name} loaded — version {VersionText}.");
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        Framework.Update -= OnFrameworkUpdate;

        AutoRetainer?.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Configuration.Enabled) return;

        var now = DateTime.UtcNow;

        if (!syncInProgress && now - lastSyncCheck >= TimeSpan.FromMinutes(Configuration.SyncIntervalMinutes))
        {
            lastSyncCheck = now;
            syncInProgress = true;

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

    private async Task RunSyncAsync()
    {
        if (AutoRetainer == null || !AutoRetainer.Ready) return;

        var cids = AutoRetainer.GetRegisteredCharacters();
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

        foreach (var cid in cids)
        {
            var data = AutoRetainer.GetOfflineCharacterData(cid);
            if (data == null || data.CID == 0) continue;
            var who = $"{data.Name}@{data.World}";

            snapshots.Add(new FleetWriter.CharacterSnapshot
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
            });

            AddMetric(metrics, "character", data.CID, "gil", data.Gil);
            AddMetric(metrics, "character", data.CID, "ceruleum", data.Ceruleum);
            AddMetric(metrics, "character", data.CID, "repair_kits", data.RepairKits);
            AddMetric(metrics, "character", data.CID, "retainer_count", data.RetainerData.Count);
            AddMetric(metrics, "character", data.CID, "submarine_count", data.OfflineSubmarineData.Count);
            AddMetric(metrics, "character", data.CID, "num_sub_slots", data.NumSubSlots);

            // Retainer details come from AutoRetainer, so they are written whether or not
            // AllaganTools is available.
            var retainers = data.RetainerData.Select((retainer, index) => new FleetWriter.RetainerRecord
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
            }).ToList();
            if (retainers.Count > 0)
                await WriteIfChanged($"retainers:{data.CID}", FleetWriter.Fingerprint(retainers),
                    () => FleetWriter.WriteRetainersAsync(conn, data.CID, retainers), $"retainers for {who}");

            // Inventories are replaced as a whole, so they are only written when AllaganTools
            // actually answered. Otherwise (AllaganTools disabled, updating after a patch, or
            // with nothing cached for this character yet) the stored inventory is left as it
            // was instead of being emptied.
            if (allaganToolsReady)
            {
                var personalItems = AllaganTools!.GetCharacterItems(data.CID);
                var personalAndRetainerItems = personalItems?.Where(i => i.Quantity > 0).ToList();

                // A character always carries something (at least the gear they wear), so
                // an empty answer means AllaganTools has no data for them, not an empty bag.
                var inventoryComplete = personalAndRetainerItems != null && personalAndRetainerItems.Count > 0;
                if (inventoryComplete)
                {
                    foreach (var retainer in data.RetainerData)
                    {
                        var retainerItems = AllaganTools.GetCharacterItems(retainer.RetainerID);
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
                if (data.FCID != 0)
                {
                    fcChestItems = AllaganTools.GetCharacterItems(data.FCID)?
                        .Where(i => i.Quantity > 0 && i.SortedContainer >= 20000 && i.SortedContainer <= 20004)
                        .ToList();
                }

                // Salvage item quantities (bags + retainers for the character, chest for
                // the FC). Only recorded when AllaganTools actually returned items, so a
                // missing cache is never stored as a real drop to zero.
                if (inventoryComplete)
                {
                    foreach (var salvageId in SalvageItemIds)
                        AddMetric(metrics, "character", data.CID, $"item_qty:{salvageId}",
                            personalAndRetainerItems!.Where(i => i.ItemId == salvageId).Sum(i => (long)i.Quantity));
                }

                if (data.FCID != 0 && fcChestItems != null && fcChestItems.Count > 0)
                {
                    foreach (var salvageId in SalvageItemIds)
                        AddMetric(metrics, "fc", data.FCID, $"item_qty:{salvageId}",
                            fcChestItems.Where(i => i.ItemId == salvageId).Sum(i => (long)i.Quantity));
                }

                if (inventoryComplete)
                {
                    var inventory = personalAndRetainerItems!.Select(ToInventoryItem).ToList();
                    await WriteIfChanged($"inventory:{data.CID}", FleetWriter.Fingerprint(inventory),
                        () => FleetWriter.WriteInventoryAsync(conn, data.CID, inventory), $"inventory for {who}");
                }
                else
                {
                    Log.Warning($"Fleet Companion: AllaganTools returned no inventory for {who}; the stored inventory was left as it was.");
                }

                // Written even with zero items, so a chest that was emptied is cleared.
                // AllaganTools only has fresh FC chest data after the in-game
                // FC chest UI has been opened. Skipped when the chest could not be read.
                // Keyed by FC, so characters sharing an FC do not write the same chest twice.
                if (data.FCID != 0 && fcChestItems != null)
                {
                    var chest = fcChestItems.Select(ToInventoryItem).ToList();
                    await WriteIfChanged($"fcchest:{data.FCID}", FleetWriter.Fingerprint(chest),
                        () => FleetWriter.WriteFcInventoryAsync(conn, data.FCID, chest), $"FC chest inventory for {who}");
                }
            }

            // AdditionalSubmarineData holds build/rank (keyed by sub name);
            // OfflineSubmarineData holds voyage return time (as a list,
            // matched by its own Name field). Only subs present in
            // AdditionalSubmarineData are written - a sub with no entry
            // there has no build at all yet (matches Parse Parts Needed's
            // own "no build exists for this slot" case from the old n8n
            // logic), so there's nothing raw to write for it.
            var subRecords = new List<FleetWriter.SubmarineRecord>();
            foreach (var (subName, vesselData) in data.AdditionalSubmarineData)
            {
                var voyage = data.OfflineSubmarineData.Find(v => v.Name == subName);

                subRecords.Add(new FleetWriter.SubmarineRecord
                {
                    SubName = subName,
                    Level = vesselData.Level,
                    Part1 = vesselData.Part1,
                    Part2 = vesselData.Part2,
                    Part3 = vesselData.Part3,
                    Part4 = vesselData.Part4,
                    Points = vesselData.Points ?? Array.Empty<byte>(),
                    ReturnTime = voyage != null ? voyage.ReturnTime : (long?)null,
                    CurrentExp = vesselData.CurrentExp,
                    NextLevelExp = vesselData.NextLevelExp
                });
            }

            await WriteIfChanged($"subs:{data.CID}", FleetWriter.Fingerprint(subRecords),
                () => FleetWriter.WriteSubmarinesAsync(conn, data.CID, subRecords), $"submarines for {who}");

            if (fcTrackerHousing.TryGetValue(cid, out var housing))
            {
                if (housing.FcId != 0)
                {
                    // FC points belong to the FC (labelled with the leader's name) and, when
                    // this character IS the leader, also to this character (labelled with the
                    // FC's name). The leader is matched by name within the same FC.
                    AddMetric(metrics, "fc", housing.FcId, "fc_points", housing.FcPoints,
                        string.IsNullOrWhiteSpace(housing.FcMaster) ? null : housing.FcMaster);

                    if (data.FCID == housing.FcId
                        && string.Equals(data.Name, housing.FcMaster, StringComparison.OrdinalIgnoreCase))
                    {
                        AddMetric(metrics, "character", data.CID, "fc_points", housing.FcPoints,
                            string.IsNullOrWhiteSpace(housing.FcName) ? null : housing.FcName);
                    }
                }

                await WriteIfChanged($"housing:{data.CID}", FleetWriter.Fingerprint(housing),
                    () => FleetWriter.WriteHousingAsync(conn, data.CID, housing), $"housing for {who}");
            }
            else if (data.FCID == 0 && charactersWithoutFc.Contains(cid))
            {
                // FCTracker and AutoRetainer both say this character is in no Free Company (it
                // left): its old FC and house details go.
                await WriteIfChanged($"housing:{data.CID}", "no free company",
                    () => FleetWriter.RemoveHousingAsync(conn, new[] { data.CID }), $"old Free Company details for {who}");
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

        Log.Information($"Fleet Companion: synced {synced}/{cids.Count} characters; {written} changed entries written, {unchanged} unchanged skipped, {failed} failed.");
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
