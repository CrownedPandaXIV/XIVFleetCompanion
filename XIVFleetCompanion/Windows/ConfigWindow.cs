using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace XIVFleetCompanion.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    // Dalamud's file picker: drawn inside the game like this window, so the game keeps running while
    // it is open (the Windows one froze the game until it was closed).
    private readonly FileDialogManager fileDialog = new();

    // Whether the FCTracker file exists, checked when the path changes and every few seconds after.
    private string checkedFcTrackerPath = "";
    private bool fcTrackerFileFound;
    private DateTime fcTrackerCheckedAt = DateTime.MinValue;

    // Postgres credential save form
    private string pgHost = "";
    private string pgPort = "5432";
    private string pgDatabase = "";
    private string pgUsername = "";
    private string pgPassword = "";
    private string pgSaveResult = "";

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base($"XIV Fleet Companion Settings v{Plugin.VersionText}###With a constant ID")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(320, 340);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        // Can't ref a property, so use a local copy
        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Fleet Sync Enabled", ref enabled))
        {
            configuration.Enabled = enabled;
            configuration.Save();
        }

        var accountLabel = configuration.AccountLabel;
        if (ImGui.InputText("Account Label", ref accountLabel, 50))
        {
            configuration.AccountLabel = accountLabel;
            configuration.Save();
        }

        var syncInterval = configuration.SyncIntervalMinutes;
        if (ImGui.InputInt("Sync Interval (minutes)", ref syncInterval))
        {
            if (syncInterval < 1)
                syncInterval = 1;

            configuration.SyncIntervalMinutes = syncInterval;
            configuration.Save();
        }

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Connection Mode");

        var useRemote = configuration.UseRemoteConnection;
        if (ImGui.RadioButton("Local", !useRemote))
        {
            configuration.UseRemoteConnection = false;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Remote", useRemote))
        {
            configuration.UseRemoteConnection = true;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text($"Postgres Connection ({(configuration.UseRemoteConnection ? "Remote" : "Local")})");

        ImGui.InputText("Host", ref pgHost, 100);
        ImGui.InputText("Port", ref pgPort, 10);
        ImGui.InputText("Database", ref pgDatabase, 100);
        ImGui.InputText("Username", ref pgUsername, 100);
        ImGui.InputText("Password", ref pgPassword, 100, ImGuiInputTextFlags.Password);

        if (ImGui.Button("Save Postgres Credentials"))
        {
            if (int.TryParse(pgPort, out var portNum))
            {
                PostgresCredentialStore.Save(configuration.UseRemoteConnection, pgHost, portNum, pgDatabase, pgUsername, pgPassword);
                pgSaveResult = "Saved.";
            }
            else
            {
                pgSaveResult = "Port must be a number.";
            }
        }

        ImGui.SameLine();

        if (ImGui.Button("Clear Saved Credentials"))
        {
            PostgresCredentialStore.Delete(configuration.UseRemoteConnection);
            pgSaveResult = "Cleared.";
        }

        if (!string.IsNullOrEmpty(pgSaveResult))
        {
            ImGui.TextWrapped(pgSaveResult);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("FCTracker Config Path");
        ImGui.TextWrapped("Points at FCTracker's config file for this XIVLauncher install, used to read FC/housing data.");

        var fcTrackerPath = configuration.FCTrackerConfigPath;
        if (ImGui.InputText("##FCTrackerPath", ref fcTrackerPath, 260))
        {
            SetFcTrackerPath(fcTrackerPath);
        }

        ImGui.SameLine();

        if (ImGui.Button("Browse..."))
        {
            var startFolder = Path.GetDirectoryName(configuration.FCTrackerConfigPath);
            if (string.IsNullOrEmpty(startFolder) || !Directory.Exists(startFolder))
                startFolder = null;
            fileDialog.OpenFileDialog("Select FCTrackerConfig.json", ".json",
                (picked, paths) =>
                {
                    if (picked && paths.Count > 0)
                        SetFcTrackerPath(paths[0]);
                },
                1, startFolder);
        }

        ImGui.SameLine();

        var defaultPath = Plugin.DefaultFcTrackerConfigPath();
        using (ImRaii.Disabled(defaultPath == null || defaultPath == configuration.FCTrackerConfigPath))
        {
            if (ImGui.Button("Use default") && defaultPath != null)
                SetFcTrackerPath(defaultPath);
        }
        if (defaultPath != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(defaultPath);

        if (configuration.FCTrackerConfigPath != checkedFcTrackerPath || DateTime.UtcNow - fcTrackerCheckedAt > TimeSpan.FromSeconds(5))
        {
            checkedFcTrackerPath = configuration.FCTrackerConfigPath;
            fcTrackerFileFound = !string.IsNullOrWhiteSpace(checkedFcTrackerPath) && File.Exists(checkedFcTrackerPath);
            fcTrackerCheckedAt = DateTime.UtcNow;
        }
        using (ImRaii.PushColor(ImGuiCol.Text, fcTrackerFileFound ? new Vector4(0.45f, 0.85f, 0.45f, 1f) : new Vector4(0.95f, 0.70f, 0.35f, 1f)))
        {
            ImGui.TextWrapped(fcTrackerFileFound
                ? "Found."
                : "Not found: Free Company and housing details are not synced until this points at FCTracker's config file.");
        }

        fileDialog.Draw();
    }

    private void SetFcTrackerPath(string path)
    {
        configuration.FCTrackerConfigPath = path;
        configuration.Save();
    }
}
