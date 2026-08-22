using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using System.Linq;

namespace XIVFleetCompanion.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string submarineImagePath;
    private readonly Plugin plugin;

    // Status window state
    private string connectionTestResult = "";
    private bool connectionTestRunning = false;
    private string autoRetainerTestResult = "";
    private string allaganToolsTestResult = "";

    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin, string submarineImagePath)
        : base("My Amazing Window##With a hidden ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.submarineImagePath = submarineImagePath;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Text($"Fleet sync is currently {(plugin.Configuration.Enabled ? "Enabled" : "Disabled")}.");
        ImGui.Text($"Sync interval: {plugin.Configuration.SyncIntervalMinutes} minute(s).");
        ImGui.Text(plugin.Configuration.LastSyncTimestamp.HasValue
            ? $"Last sync: {plugin.Configuration.LastSyncTimestamp.Value:g}"
            : "Last sync: never");

        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }

        ImGui.Spacing();

        // --- Fleet Companion Status ---
        using (ImRaii.PushId("FleetStatus"))
        {
            ImGui.Text("Fleet Companion Status");
            ImGui.Spacing();

            // Enabled toggle (mirrors Configuration.Enabled)
            var enabled = plugin.Configuration.Enabled;
            if (ImGui.Checkbox("Enabled", ref enabled))
            {
                plugin.Configuration.Enabled = enabled;
                plugin.Configuration.Save();
            }

            ImGui.Text(plugin.Configuration.LastSyncTimestamp.HasValue
                ? $"Last sync: {plugin.Configuration.LastSyncTimestamp.Value:g}"
                : "Last sync: never");

            ImGui.Spacing();
            ImGui.Text("Source connectivity:");

            var cred = PostgresCredentialStore.Load(plugin.Configuration.UseRemoteConnection);
            ImGui.BulletText(cred != null
                ? $"Postgres ({(plugin.Configuration.UseRemoteConnection ? "Remote" : "Local")}): Configured"
                : $"Postgres ({(plugin.Configuration.UseRemoteConnection ? "Remote" : "Local")}): Not configured");
            bool autoRetainerReady = false;
            try
            {
                autoRetainerReady = plugin.AutoRetainer != null && plugin.AutoRetainer.Ready;
            }
            catch
            {
                autoRetainerReady = false;
            }

            ImGui.BulletText(autoRetainerReady ? "AutoRetainer: OK" : "AutoRetainer: Not found / not running");
            bool allaganToolsReady = plugin.AllaganTools?.IsReady() ?? false;
            ImGui.BulletText(allaganToolsReady ? "AllaganTools: OK" : "AllaganTools: Not found / not running");

            if (ImGui.Button("Read My Character Data"))
            {
                try
                {
                    var cid = Plugin.PlayerState.ContentId;
                    var data = plugin.AutoRetainer?.GetOfflineCharacterData(cid);

                    if (data == null || data.CID == 0)
                    {
                        autoRetainerTestResult = "No data found for this character (CID may not be registered yet).";
                    }
                    else
                    {
                        autoRetainerTestResult =
                            $"{data.Name}@{data.CurrentWorld}\n" +
                            $"Retainers: {data.RetainerData.Count}\n" +
                            $"Submarines: {data.OfflineSubmarineData.Count}\n" +
                            $"Gil: {data.Gil:N0}\n" +
                            $"Ceruleum: {data.Ceruleum}\n" +
                            $"Repair Kits: {data.RepairKits}";
                    }
                }
                catch (Exception ex)
                {
                    autoRetainerTestResult = $"Error: {ex.Message}";
                }
            }

            ImGui.TextWrapped(autoRetainerTestResult);

            if (ImGui.Button("Read My Inventory (AllaganTools)"))
            {
                try
                {
                    var cid = Plugin.PlayerState.ContentId;
                    var items = plugin.AllaganTools?.GetCharacterItems(cid) ?? new List<AllaganToolsConnector.ParsedItem>();

                    if (items.Count == 0)
                    {
                        allaganToolsTestResult = "No items found (or AllaganTools not available).";
                    }
                    else
                    {
                        var nonEmpty = items.Where(i => i.Quantity > 0).ToList();

                        var sample = nonEmpty.Take(5)
                            .Select(i => $"ItemId {i.ItemId} x{i.Quantity} (Container {i.Container})");

                        allaganToolsTestResult =
                            $"Total slots: {items.Count} (non-empty: {nonEmpty.Count})\n" +
                            string.Join("\n", sample);
                    }
                }
                catch (Exception ex)
                {
                    allaganToolsTestResult = $"Error: {ex.Message}";
                }
            }

            ImGui.TextWrapped(allaganToolsTestResult);

            ImGui.Spacing();

            using (ImRaii.Disabled(connectionTestRunning))
            {
                if (ImGui.Button("Test Postgres Connection"))
                {
                    connectionTestRunning = true;
                    connectionTestResult = "Testing...";

                    Task.Run(async () =>
                    {
                        var result = await PostgresConnectionTester.TestConnectionAsync(plugin.Configuration.UseRemoteConnection);
                        connectionTestResult = result;
                        connectionTestRunning = false;
                    });
                }
            }

            ImGui.TextWrapped(connectionTestResult);
        }

        // Normally a BeginChild() would have to be followed by an unconditional EndChild(),
        // ImRaii takes care of this after the scope ends.
        // This works for all ImGui functions that require specific handling, examples are BeginTable() or Indent().
        using (var child = ImRaii.Child("SomeChildWithAScrollbar", Vector2.Zero, true))
        {
            // Check if this child is drawing
            if (child.Success)
            {
                ImGui.Text("XIV Fleet Companion");
                var submarineImage = Plugin.TextureProvider.GetFromFile(submarineImagePath).GetWrapOrDefault();
                if (submarineImage != null)
                {
                    using (ImRaii.PushIndent(55f))
                    {
                        ImGui.Image(submarineImage.Handle, submarineImage.Size);
                    }
                }
                else
                {
                    ImGui.Text("Image not found.");
                }

                ImGuiHelpers.ScaledDummy(20.0f);

                // Example for other services that Dalamud provides.
                // PlayerState provides a wrapper filled with information about the player character.

                var playerState = Plugin.PlayerState;
                if (!playerState.IsLoaded)
                {
                    ImGui.Text("Our local player is currently not logged in.");
                    return;
                }
                
                if (!playerState.ClassJob.IsValid)
                {
                    ImGui.Text("Our current job is currently not valid.");
                    return;
                }
                
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"Current job:");
                
                // Scaling hardcoded pixel values is important, as otherwise users with HUD scales above or below 100%
                // won't be able to see everything.
                ImGui.SameLine(120 * ImGuiHelpers.GlobalScale);
                
                // Get the icon id from a known offset + the class jobs id
                var jobIconId = 62100 + playerState.ClassJob.RowId;
                var iconTexture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(jobIconId)).GetWrapOrEmpty();
                ImGui.Image(iconTexture.Handle, new Vector2(28, 28) * ImGuiHelpers.GlobalScale);
                
                ImGui.SameLine();
                
                // If you want to see the Macro representation of this SeString use `.ToMacroString()`
                // More info about SeStrings: https://dalamud.dev/plugin-development/sestring/
                ImGui.Text(playerState.ClassJob.Value.Abbreviation.ToString());
                
                ImGui.SameLine();
                ImGui.Text($" [Level {playerState.Level}]");
                
                // Example for querying Lumina, getting the name of our current area.
                var territoryId = Plugin.ClientState.TerritoryType;
                if (Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territoryRow))
                {
                    ImGui.Text($"Current location:");
                    ImGui.SameLine(120 * ImGuiHelpers.GlobalScale);
                    ImGui.Text(territoryRow.PlaceName.Value.Name.ToString());
                }
                else
                {
                    ImGui.Text("Invalid territory.");
                }
            }
        }
    }
}
