using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

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

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("A Wonderful Configuration Window###With a constant ID")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(320, 340);
        SizeCondition = ImGuiCond.Always;

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
        ImGui.Text("Postgres Connection");

        ImGui.InputText("Host", ref pgHost, 100);
        ImGui.InputText("Port", ref pgPort, 10);
        ImGui.InputText("Database", ref pgDatabase, 100);
        ImGui.InputText("Username", ref pgUsername, 100);
        ImGui.InputText("Password", ref pgPassword, 100, ImGuiInputTextFlags.Password);

        if (ImGui.Button("Save Postgres Credentials"))
        {
            if (int.TryParse(pgPort, out var portNum))
            {
                PostgresCredentialStore.Save(pgHost, portNum, pgDatabase, pgUsername, pgPassword);
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
            PostgresCredentialStore.Delete();
            pgSaveResult = "Cleared.";
        }

        if (!string.IsNullOrEmpty(pgSaveResult))
        {
            ImGui.TextWrapped(pgSaveResult);
        }
    }
}
