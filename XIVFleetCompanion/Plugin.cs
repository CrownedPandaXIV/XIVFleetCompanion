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

    private async Task RunSyncAsync()
    {
        if (AutoRetainer == null || !AutoRetainer.Ready) return;

        var cids = AutoRetainer.GetRegisteredCharacters();
        var fcTrackerHousing = FCTrackerConnector.ReadHousingData(Configuration.FCTrackerConfigPath);
        Log.Information($"Fleet Companion: FCTracker path='{Configuration.FCTrackerConfigPath}' parsed {fcTrackerHousing.Count} housing entries.");
        int successCount = 0;

        foreach (var cid in cids)
        {
            var data = AutoRetainer.GetOfflineCharacterData(cid);
            if (data == null || data.CID == 0) continue;

            var result = await PostgresWriter.WriteCharacterSnapshotAsync(
                data.CID, data.Name, data.CurrentWorld,
                data.RetainerData.Count, data.OfflineSubmarineData.Count,
                data.Gil, data.Ceruleum, data.RepairKits, Configuration.AccountLabel, data.FCID, data.NumSubSlots, Configuration.UseRemoteConnection);

            if (result == "Success.")
                successCount++;
            else
                Log.Warning($"Fleet Companion: failed to write snapshot for {data.Name}@{data.CurrentWorld} — {result}");

            if (AllaganTools != null)
            {
                var personalItems = AllaganTools.GetCharacterItems(data.CID);
                var nonEmpty = personalItems.Where(i => i.Quantity > 0).ToList();

                foreach (var retainer in data.RetainerData)
                {
                    var retainerItems = AllaganTools.GetCharacterItems(retainer.RetainerID);
                    nonEmpty.AddRange(retainerItems.Where(i => i.Quantity > 0));

                    var retainerLookupResult = await PostgresWriter.WriteRetainerLookupAsync(
                        retainer.RetainerID, data.CID, retainer.Name, Configuration.UseRemoteConnection);

                    if (!retainerLookupResult.StartsWith("Success"))
                        Log.Warning($"Fleet Companion: failed to write retainer lookup for {retainer.Name} (owner {data.Name}) — {retainerLookupResult}");
                }

                // FC chest data must be queried directly via the FC's own
                // ID (GetCharacterItems(data.FCID)), NOT filtered out of a
                // character's personal item list - confirmed as the real
                // bug tonight: personal-item queries only incidentally
                // included FC-range containers when AllaganTools happened
                // to have stale/cached data mixed in, which is why this was
                // unreliable (sometimes real chest data, sometimes a stray
                // FreeCompanyCurrency row, usually nothing at all). A
                // dedicated FC-scoped query is the correct source, matching
                // what an earlier probe in this same file (since removed)
                // had already confirmed worked. Personal inventory never
                // includes FC-range containers now, so no filtering needed
                // there anymore either.
                var personalAndRetainerItems = nonEmpty;
                List<AllaganToolsConnector.ParsedItem> fcChestItems = new();
                if (data.FCID != 0)
                {
                    var fcItems = AllaganTools.GetCharacterItems(data.FCID);
                    fcChestItems = fcItems
                        .Where(i => i.Quantity > 0 && i.SortedContainer >= 20000 && i.SortedContainer <= 20004)
                        .ToList();
                }

                var invResult = await PostgresWriter.WriteInventorySnapshotAsync(cid, personalAndRetainerItems, Configuration.UseRemoteConnection);

                if (!invResult.StartsWith("Success"))
                    Log.Warning($"Fleet Companion: failed to write inventory for {data.Name}@{data.CurrentWorld} — {invResult}");

                // Always write (even with zero items) so the delete-then-
                // reinsert inside WriteFCInventorySnapshotAsync actually
                // clears stale/wrong rows every sync - previously this only
                // ran when fcChestItems.Count > 0, so a sync where
                // AllaganTools reported no FC chest data at all (confirmed
                // common - it appears to only have fresh FC data cached
                // after the in-game FC chest UI has actually been opened)
                // left whatever was written last time sitting there forever,
                // uncleaned.
                if (data.FCID != 0)
                {
                    var fcInvResult = await PostgresWriter.WriteFCInventorySnapshotAsync(data.FCID, fcChestItems, Configuration.UseRemoteConnection);

                    if (!fcInvResult.StartsWith("Success"))
                        Log.Warning($"Fleet Companion: failed to write FC chest inventory for {data.Name}@{data.CurrentWorld} — {fcInvResult}");
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
                Log.Warning($"Fleet Companion: failed to write submarines for {data.Name}@{data.CurrentWorld} — {subResult}");

            if (fcTrackerHousing.TryGetValue(cid, out var housing))
            {
                var housingResult = await PostgresWriter.WriteHousingSnapshotAsync(cid, housing, Configuration.UseRemoteConnection);

                if (!housingResult.StartsWith("Success"))
                    Log.Warning($"Fleet Companion: failed to write housing for {data.Name}@{data.CurrentWorld} — {housingResult}");
            }
        }
        Configuration.LastSyncTimestamp = DateTime.Now;
        Configuration.Save();

        Log.Information($"Fleet Companion: synced {successCount}/{cids.Count} characters.");
    }
}
