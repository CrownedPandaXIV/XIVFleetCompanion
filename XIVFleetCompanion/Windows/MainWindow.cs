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

    // Whether Postgres is configured and AutoRetainer / AllaganTools answer. Checked every few
    // seconds while the window is open, not on every frame (each check reads Windows Credential
    // Manager or calls another plugin).
    private static readonly TimeSpan StatusRefresh = TimeSpan.FromSeconds(3);
    private DateTime statusCheckedAt = DateTime.MinValue;
    private bool statusRemote;
    private bool postgresConfigured;
    private bool autoRetainerReady;
    private bool allaganToolsReady;

    // "###" keeps the window's ImGui ID fixed while the title shows the version.
    public MainWindow(Plugin plugin, string submarineImagePath)
        : base($"XIV Fleet Companion v{Plugin.VersionText}###XIVFleetCompanionMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
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

    private void RefreshStatus()
    {
        var remote = plugin.Configuration.UseRemoteConnection;
        if (remote == statusRemote && DateTime.UtcNow - statusCheckedAt < StatusRefresh)
            return;
        statusRemote = remote;
        statusCheckedAt = DateTime.UtcNow;

        postgresConfigured = PostgresCredentialStore.Load(remote) != null;
        try
        {
            autoRetainerReady = plugin.AutoRetainer != null && plugin.AutoRetainer.Ready;
        }
        catch
        {
            autoRetainerReady = false;
        }
        allaganToolsReady = plugin.AllaganTools?.IsReady() ?? false;
    }

    public override void Draw()
    {
        RefreshStatus();

        ImGui.Text($"Sync interval: {plugin.Configuration.SyncIntervalMinutes} minute(s){(plugin.Configuration.SyncAfterLogout ? ", and right after a character logs out" : "")}.");

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

            var mode = statusRemote ? "Remote" : "Local";
            ImGui.BulletText(postgresConfigured ? $"Postgres ({mode}): Configured" : $"Postgres ({mode}): Not configured");
            ImGui.BulletText(autoRetainerReady ? "AutoRetainer: OK" : "AutoRetainer: Not found / not running");
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
                        // Home world, as the sync and the app use; where the character is now
                        // (AutoRetainer's override, e.g. during data center travel) only when different.
                        var visiting = !string.IsNullOrEmpty(data.WorldOverride) && data.WorldOverride != data.World
                            ? $" (currently on {data.WorldOverride})"
                            : "";
                        autoRetainerTestResult =
                            $"{data.Name}@{data.World}{visiting}\n" +
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

        using (var child = ImRaii.Child("Banner", Vector2.Zero, true))
        {
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
            }
        }
    }
}
