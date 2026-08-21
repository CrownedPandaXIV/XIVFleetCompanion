using AutoRetainerAPI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.EzEventManager;
using System;
using System.IO;
using XIVFleetCompanion.Windows;
using System.Threading.Tasks;
using System.Linq;

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

    private const string CommandName = "/pmycommand";

    public Configuration Configuration { get; init; }
    public AutoRetainerApi? AutoRetainer { get; private set; }
    public AllaganToolsConnector? AllaganTools { get; private set; }
    private DateTime lastSyncCheck = DateTime.MinValue;
    private bool syncInProgress = false;

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

        // You might normally want to embed resources and load them from the manifest stream
        var goatImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "goat.png");

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this, goatImagePath);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "A useful message to display in /xlhelp"
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
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
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
        if (syncInProgress) return;

        var now = DateTime.UtcNow;
        if (now - lastSyncCheck < TimeSpan.FromMinutes(Configuration.SyncIntervalMinutes)) return;

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

    private async Task RunSyncAsync()
    {
        if (AutoRetainer == null || !AutoRetainer.Ready) return;

        var cids = AutoRetainer.GetRegisteredCharacters();
        var fcTrackerHousing = FCTrackerConnector.ReadHousingData(Configuration.FCTrackerConfigPath);
        int successCount = 0;

        foreach (var cid in cids)
        {
            var data = AutoRetainer.GetOfflineCharacterData(cid);
            if (data == null || data.CID == 0) continue;

            var result = await PostgresWriter.WriteCharacterSnapshotAsync(
                data.CID, data.Name, data.CurrentWorld,
                data.RetainerData.Count, data.OfflineSubmarineData.Count,
                data.Gil, data.Ceruleum, data.RepairKits, Configuration.AccountLabel, Configuration.UseRemoteConnection);

            if (result == "Success.")
                successCount++;
            else
                Log.Warning($"Fleet Companion: failed to write snapshot for {data.Name}@{data.CurrentWorld} — {result}");

            if (AllaganTools != null)
            {
                var items = AllaganTools.GetCharacterItems(cid);
                var nonEmpty = items.Where(i => i.Quantity > 0).ToList();

                foreach (var retainer in data.RetainerData)
                {
                    var retainerItems = AllaganTools.GetCharacterItems(retainer.RetainerID);
                    nonEmpty.AddRange(retainerItems.Where(i => i.Quantity > 0));
                }

                var invResult = await PostgresWriter.WriteInventorySnapshotAsync(cid, nonEmpty, Configuration.UseRemoteConnection);

                if (!invResult.StartsWith("Success"))
                    Log.Warning($"Fleet Companion: failed to write inventory for {data.Name}@{data.CurrentWorld} — {invResult}");
            }

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
