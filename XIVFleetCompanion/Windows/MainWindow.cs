using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string submarineImagePath;
    private readonly Plugin plugin;

    // Status window state
    private string connectionTestResult = "";
    private bool connectionTestRunning = false;

    // Whether Postgres is configured and AutoRetainer / AllaganTools answer. Checked every few
    // seconds while the window is open, not on every frame (each check reads Windows Credential
    // Manager or calls another plugin).
    private static readonly TimeSpan StatusRefresh = TimeSpan.FromSeconds(3);
    private DateTime statusCheckedAt = DateTime.MinValue;
    private bool statusRemote;
    private bool postgresConfigured;
    private bool autoRetainerReady;
    private bool allaganToolsReady;
    // Optional plugins: null when Dalamud could not be asked.
    private bool? fcTrackerRunning;
    private bool? subTrackerRunning;

    // "###" keeps the window's ImGui ID fixed while the title shows the version.
    public MainWindow(Plugin plugin, string submarineImagePath)
        : base($"XIV Fleet Companion v{Plugin.VersionText}###XIVFleetCompanionMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 370),
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
        fcTrackerRunning = Plugin.IsPluginLoaded("FCTracker");
        subTrackerRunning = Plugin.IsPluginLoaded("SubmarineTracker");
    }

    // An optional plugin's line: "running", "not running (optional)", or "can't tell" when Dalamud could not be asked.
    private static string OptionalLine(string name, bool? running) =>
        $"{name}: " + (running == true ? "running" : running == false ? "not running (optional)" : "can't tell");

    public override void Draw()
    {
        RefreshStatus();

        ImGui.Text($"Sync interval: {plugin.Configuration.SyncIntervalMinutes} minute(s){(plugin.Configuration.SyncAfterLogout ? ", and right after a character logs out" : "")}.");

        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }
        ImGui.SameLine();
        if (ImGui.Button("Check what I can see"))
        {
            plugin.OpenCheck();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Goes through every character and shows what AutoRetainer, Allagan Tools and FC Tracker have for it, without writing anything (also /xivfleet check).");

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

            // Why syncing isn't getting through right now (the database PC is off, a wrong password...).
            var problem = plugin.SyncProblem;
            if (problem != null)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.3f, 1f)))
                    ImGui.TextWrapped($"Not syncing ({plugin.SyncProblemAt:t}): {problem}");
            }

            ImGui.Spacing();
            ImGui.Text("Connections:");

            var mode = statusRemote ? "Remote" : "Local";
            ImGui.BulletText(postgresConfigured ? $"Database ({mode}): set up" : $"Database ({mode}): not set up yet (open the settings)");
            ImGui.BulletText(autoRetainerReady ? "AutoRetainer: running" : "AutoRetainer: not running");
            ImGui.BulletText(allaganToolsReady ? "Allagan Tools: running" : "Allagan Tools: not running");
            ImGui.BulletText(OptionalLine("FC Tracker", fcTrackerRunning));
            ImGui.BulletText(OptionalLine("Submarine Tracker", subTrackerRunning));

            ImGui.Spacing();

            using (ImRaii.Disabled(connectionTestRunning))
            {
                if (ImGui.Button("Test Database Connection"))
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
