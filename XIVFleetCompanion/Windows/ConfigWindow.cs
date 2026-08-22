using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Windows.Forms;

namespace XIVFleetCompanion.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    // Postgres credential save form
    private string pgHost = "";
    private string pgPort = "5432";
    private string pgDatabase = "";
    private string pgUsername = "";
    private string pgPassword = "";
    private string pgSaveResult = "";

    // Retention/downsampling save form
    private static readonly string[] TimeUnits = { "Days", "Weeks", "Months" };
    private int retentionValue = 6;
    private int retentionUnitIndex = 2; // Months
    private int downsampleValue = 1;
    private int downsampleUnitIndex = 0; // Days
    private string retentionSaveResult = "";

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("A Wonderful Configuration Window###With a constant ID")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(320, 340);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;

        retentionValue = configuration.RetentionValue;
        retentionUnitIndex = Array.IndexOf(TimeUnits, configuration.RetentionUnit);
        if (retentionUnitIndex < 0) retentionUnitIndex = 2;

        downsampleValue = configuration.DownsampleValue;
        downsampleUnitIndex = Array.IndexOf(TimeUnits, configuration.DownsampleUnit);
        if (downsampleUnitIndex < 0) downsampleUnitIndex = 0;
    }

    public void Dispose() { }

    public override void OnOpen()
    {
        retentionSaveResult = "";
    }

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
            configuration.FCTrackerConfigPath = fcTrackerPath;
            configuration.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button("Browse..."))
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Select FCTrackerConfig.json"
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                configuration.FCTrackerConfigPath = dialog.FileName;
                configuration.Save();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Retention & Downsampling");
        ImGui.TextWrapped("Runs automatically about once every 24 hours while the plugin is enabled and loaded — not on a fixed calendar schedule, so a few hours of drift is normal.");

        ImGui.TextWrapped("Retention Window: how old a snapshot must be before it becomes eligible for compression. Anything older than this gets downsampled.");
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##RetentionValue", ref retentionValue);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.Combo("Retention Window", ref retentionUnitIndex, TimeUnits, TimeUnits.Length);

        ImGui.Spacing();

        ImGui.TextWrapped("Downsample Interval: how coarse compressed data becomes. For example, 1 Days keeps one snapshot per day; 1 Weeks keeps one per week. Note: Months are approximated as 30-day blocks, not calendar months.");
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##DownsampleValue", ref downsampleValue);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.Combo("Downsample Interval", ref downsampleUnitIndex, TimeUnits, TimeUnits.Length);

        if (ImGui.Button("Save Retention Settings"))
        {
            if (retentionValue < 1 || downsampleValue < 1)
            {
                retentionSaveResult = "Both values must be at least 1.";
            }
            else
            {
                configuration.RetentionValue = retentionValue;
                configuration.RetentionUnit = TimeUnits[retentionUnitIndex];
                configuration.DownsampleValue = downsampleValue;
                configuration.DownsampleUnit = TimeUnits[downsampleUnitIndex];
                configuration.Save();
                retentionSaveResult = "Saved.";
            }
        }

        if (!string.IsNullOrEmpty(retentionSaveResult))
        {
            ImGui.TextWrapped(retentionSaveResult);
        }
    }
}
