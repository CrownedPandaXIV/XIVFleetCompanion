using AutoRetainerAPI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.EzEventManager;
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

    public Configuration Configuration { get; init; }
    public AutoRetainerApi? AutoRetainer { get; private set; }
    public AllaganToolsConnector? AllaganTools { get; private set; }
    private DateTime lastSyncCheck = DateTime.MinValue;
    private bool syncInProgress = false;
    private DateTime lastRetentionCheck = DateTime.MinValue;
    private bool retentionInProgress = false;

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
        Log.Information($"{PluginInterface.Manifest.Name} loaded — version {PluginInterface.Manifest.AssemblyVersion}.");
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

        // Retention runs on its own, much less frequent, once-daily check —
        // no need to tie it to the sync interval.
        if (!retentionInProgress && now - lastRetentionCheck >= TimeSpan.FromHours(24))
        {
            lastRetentionCheck = now;
            retentionInProgress = true;

            Task.Run(async () =>
            {
                try
                {
                    var result = await PostgresWriter.RunRetentionCleanupAsync(
                        Configuration.RetentionValue, Configuration.RetentionUnit,
                        Configuration.DownsampleValue, Configuration.DownsampleUnit,
                        Configuration.UseRemoteConnection);

                    Log.Information($"Fleet Companion: retention cleanup — {result}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Fleet Companion retention cleanup failed: {ex}");
                }
                finally
                {
                    retentionInProgress = false;
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
        var fcTrackerHousing = FCTrackerConnector.ReadHousingData(Configuration.FCTrackerConfigPath);
        Log.Information($"Fleet Companion: FCTracker path='{Configuration.FCTrackerConfigPath}' parsed {fcTrackerHousing.Count} housing entries.");
        int successCount = 0;
        var metrics = new Dictionary<(string Type, ulong Id, string Metric), (decimal Value, string? Label)>();

        foreach (var cid in cids)
        {
            var data = AutoRetainer.GetOfflineCharacterData(cid);
            if (data == null || data.CID == 0) continue;

            var result = await PostgresWriter.WriteCharacterSnapshotAsync(
                data.CID, data.Name, data.World,
                data.RetainerData.Count, data.OfflineSubmarineData.Count,
                data.Gil, data.Ceruleum, data.RepairKits, Configuration.AccountLabel, data.FCID, data.NumSubSlots, Configuration.UseRemoteConnection);

            if (result == "Success.")
                successCount++;
            else
                Log.Warning($"Fleet Companion: failed to write snapshot for {data.Name}@{data.World} — {result}");

            AddMetric(metrics, "character", data.CID, "gil", data.Gil);
            AddMetric(metrics, "character", data.CID, "ceruleum", data.Ceruleum);
            AddMetric(metrics, "character", data.CID, "repair_kits", data.RepairKits);

            if (AllaganTools != null)
            {
                var personalItems = AllaganTools.GetCharacterItems(data.CID);
                var nonEmpty = personalItems.Where(i => i.Quantity > 0).ToList();

                for (int retainerIndex = 0; retainerIndex < data.RetainerData.Count; retainerIndex++)
                {
                    var retainer = data.RetainerData[retainerIndex];
                    var retainerItems = AllaganTools.GetCharacterItems(retainer.RetainerID);
                    nonEmpty.AddRange(retainerItems.Where(i => i.Quantity > 0));

                    var retainerLookupResult = await PostgresWriter.WriteRetainerLookupAsync(
                        retainer.RetainerID, data.CID, retainer.Name,
                        retainer.Job, retainer.Gil, retainer.HasVenture, retainer.VentureID,
                        retainer.VentureBeginsAt, retainer.VentureEndsAt, retainer.Level, retainerIndex, Configuration.UseRemoteConnection);

                    if (!retainerLookupResult.StartsWith("Success"))
                        Log.Warning($"Fleet Companion: failed to write retainer lookup for {retainer.Name} (owner {data.Name}) — {retainerLookupResult}");
                }

                // FC chest data comes from the FC's own ID, not from a
                // character's personal items.
                var personalAndRetainerItems = nonEmpty;
                List<AllaganToolsConnector.ParsedItem> fcChestItems = new();
                if (data.FCID != 0)
                {
                    var fcItems = AllaganTools.GetCharacterItems(data.FCID);
                    fcChestItems = fcItems
                        .Where(i => i.Quantity > 0 && i.SortedContainer >= 20000 && i.SortedContainer <= 20004)
                        .ToList();
                }

                // Salvage item quantities (bags + retainers for the character, chest for
                // the FC). Only recorded when AllaganTools actually returned items, so a
                // missing cache is never stored as a real drop to zero.
                if (nonEmpty.Count > 0)
                {
                    foreach (var salvageId in SalvageItemIds)
                        AddMetric(metrics, "character", data.CID, $"item_qty:{salvageId}",
                            nonEmpty.Where(i => i.ItemId == salvageId).Sum(i => (long)i.Quantity));
                }

                if (data.FCID != 0 && fcChestItems.Count > 0)
                {
                    foreach (var salvageId in SalvageItemIds)
                        AddMetric(metrics, "fc", data.FCID, $"item_qty:{salvageId}",
                            fcChestItems.Where(i => i.ItemId == salvageId).Sum(i => (long)i.Quantity));
                }

                var invResult = await PostgresWriter.WriteInventorySnapshotAsync(cid, personalAndRetainerItems, Configuration.UseRemoteConnection);

                if (!invResult.StartsWith("Success"))
                    Log.Warning($"Fleet Companion: failed to write inventory for {data.Name}@{data.World} — {invResult}");

                // Always write, even with zero items, so stale rows get cleared.
                // AllaganTools only has fresh FC chest data after the in-game
                // FC chest UI has been opened.
                if (data.FCID != 0)
                {
                    var fcInvResult = await PostgresWriter.WriteFCInventorySnapshotAsync(data.FCID, fcChestItems, Configuration.UseRemoteConnection);

                    if (!fcInvResult.StartsWith("Success"))
                        Log.Warning($"Fleet Companion: failed to write FC chest inventory for {data.Name}@{data.World} — {fcInvResult}");
                }
            }

            // AdditionalSubmarineData holds build/rank (keyed by sub name);
            // OfflineSubmarineData holds voyage return time (as a list,
            // matched by its own Name field). Only subs present in
            // AdditionalSubmarineData are written - a sub with no entry
            // there has no build at all yet (matches Parse Parts Needed's
            // own "no build exists for this slot" case from the old n8n
            // logic), so there's nothing raw to write for it.
            var subRecords = new List<PostgresWriter.SubmarineRecord>();
            foreach (var (subName, vesselData) in data.AdditionalSubmarineData)
            {
                var voyage = data.OfflineSubmarineData.Find(v => v.Name == subName);

                subRecords.Add(new PostgresWriter.SubmarineRecord
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

            var subResult = await PostgresWriter.WriteSubmarineSnapshotAsync(cid, subRecords, Configuration.UseRemoteConnection);

            if (!subResult.StartsWith("Success"))
                Log.Warning($"Fleet Companion: failed to write submarines for {data.Name}@{data.World} — {subResult}");

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

                var housingResult = await PostgresWriter.WriteHousingSnapshotAsync(cid, housing, Configuration.UseRemoteConnection);

                if (!housingResult.StartsWith("Success"))
                    Log.Warning($"Fleet Companion: failed to write housing for {data.Name}@{data.World} — {housingResult}");
            }
        }
        var metricPoints = metrics.Select(kv => new PostgresWriter.MetricPoint
        {
            SubjectType = kv.Key.Type,
            SubjectId = kv.Key.Id,
            Metric = kv.Key.Metric,
            Value = kv.Value.Value,
            Label = kv.Value.Label
        }).ToList();

        var metricResult = await PostgresWriter.WriteMetricsAsync(metricPoints, Configuration.UseRemoteConnection);
        if (!metricResult.StartsWith("Success"))
            Log.Warning($"Fleet Companion: failed to write metric history — {metricResult}");

        Configuration.LastSyncTimestamp = DateTime.Now;
        Configuration.Save();

        Log.Information($"Fleet Companion: synced {successCount}/{cids.Count} characters.");
    }
}
