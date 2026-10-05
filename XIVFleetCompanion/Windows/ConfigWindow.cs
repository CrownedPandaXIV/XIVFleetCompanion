using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace XIVFleetCompanion.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Plugin plugin;

    // Every character AutoRetainer knows on this client, read when the window opens or on Refresh
    // (not every frame). Null until read; empty when AutoRetainer is not ready.
    private sealed record KnownCharacter(ulong Cid, string Name, string World, int Subs);
    private List<KnownCharacter>? knownCharacters;

    // Dalamud's file picker: drawn inside the game like this window, so the game keeps running while
    // it is open (the Windows one froze the game until it was closed).
    private readonly FileDialogManager fileDialog = new();

    // Whether the FCTracker file exists, checked when the path changes and every few seconds after.
    private string checkedFcTrackerPath = "";
    private bool fcTrackerFileFound;
    private DateTime fcTrackerCheckedAt = DateTime.MinValue;

    // Postgres credential form. Filled from the saved credential when the window opens or the
    // connection mode changes; the password box always starts empty (a blank password keeps the
    // saved one), so the password is never read back into the window.
    private string pgHost = "";
    private string pgPort = "5432";
    private string pgDatabase = "";
    private string pgUsername = "";
    private string pgPassword = "";
    private bool pgHasSavedPassword;
    private string pgSaveResult = "";
    private DateTime clearArmedUntil = DateTime.MinValue;

    // "###" keeps the window's ImGui ID fixed while the title shows the version.
    public ConfigWindow(Plugin plugin) : base($"XIV Fleet Companion Settings v{Plugin.VersionText}###XIVFleetCompanionSettings")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(320, 340);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void OnOpen()
    {
        LoadSavedCredential();
        knownCharacters = null;
    }

    // Reads the characters AutoRetainer knows (this runs on the game's thread, like all drawing).
    private void ReadKnownCharacters()
    {
        var autoRetainer = plugin.AutoRetainer;
        knownCharacters = new List<KnownCharacter>();
        if (autoRetainer == null || !autoRetainer.Ready) return;
        foreach (var cid in autoRetainer.GetRegisteredCharacters())
        {
            var data = autoRetainer.GetOfflineCharacterData(cid);
            if (data == null || data.CID == 0) continue;
            knownCharacters.Add(new KnownCharacter(data.CID, data.Name, data.World, data.OfflineSubmarineData.Count));
        }
        knownCharacters.Sort((a, b) => string.Compare($"{a.Name} {a.World}", $"{b.Name} {b.World}", StringComparison.OrdinalIgnoreCase));
    }

    private void SetSync(ulong cid, bool sync)
    {
        CharacterChoice.Set(cid, sync, configuration.SyncOnlyChosen, configuration.ChosenCharacters, configuration.SkippedCharacters);
        configuration.Save();
    }

    private void SetSyncExactly(Func<KnownCharacter, bool> sync)
    {
        if (knownCharacters == null) return;
        CharacterChoice.SetExactly(knownCharacters.Select(c => c.Cid), knownCharacters.Where(sync).Select(c => c.Cid).ToHashSet(),
            configuration.SyncOnlyChosen, configuration.ChosenCharacters, configuration.SkippedCharacters);
        configuration.Save();
    }

    // Which characters are synced: every one except the unticked, or only the ticked (for an account
    // with many characters of which only a few should reach the app).
    private void DrawCharacters()
    {
        if (!ImGui.CollapsingHeader("Characters")) return;
        if (knownCharacters == null) ReadKnownCharacters();

        if (ImGui.RadioButton("Sync every character (untick any to leave out)", !configuration.SyncOnlyChosen))
        {
            configuration.SyncOnlyChosen = false;
            configuration.Save();
        }
        if (ImGui.RadioButton("Only sync the characters I tick", configuration.SyncOnlyChosen))
        {
            configuration.SyncOnlyChosen = true;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("New characters are then left out until ticked.");

        if (ImGui.Button("Refresh list")) ReadKnownCharacters();
        ImGui.SameLine();
        if (ImGui.Button("Tick only those with subs")) SetSyncExactly(c => c.Subs > 0);
        ImGui.SameLine();
        if (ImGui.Button("Tick all")) SetSyncExactly(_ => true);
        ImGui.SameLine();
        if (ImGui.Button("Untick all")) SetSyncExactly(_ => false);

        var list = knownCharacters!;
        if (list.Count == 0)
        {
            ImGui.TextWrapped("AutoRetainer is not running or knows no characters on this client.");
            return;
        }

        var syncing = list.Count(c => configuration.ShouldSync(c.Cid));
        ImGui.Text($"Syncing {syncing} of {list.Count} characters.");
        ImGui.TextDisabled("Left-out characters are not read or sent. Anything they sent before can be removed with the app's Remove button on the Roster.");

        using (var child = ImRaii.Child("##characters", new Vector2(0, Math.Min(list.Count, 10) * ImGui.GetFrameHeightWithSpacing() + 8), true))
        {
            if (child.Success)
            {
                foreach (var c in list)
                {
                    var sync = configuration.ShouldSync(c.Cid);
                    var subs = c.Subs > 0 ? $"  ({c.Subs} sub{(c.Subs == 1 ? "" : "s")})" : "";
                    if (ImGui.Checkbox($"{c.Name} @ {c.World}{subs}##{c.Cid}", ref sync))
                        SetSync(c.Cid, sync);
                }
            }
        }
    }

    // Fills the form from the saved credential for the current connection mode (or the defaults).
    private void LoadSavedCredential()
    {
        var saved = PostgresCredentialStore.Load(configuration.UseRemoteConnection);
        pgHost = saved?.Host ?? "";
        pgPort = saved != null && saved.Port > 0 ? saved.Port.ToString() : "5432";
        pgDatabase = saved?.Database ?? "";
        pgUsername = saved?.Username ?? "";
        pgPassword = "";
        pgHasSavedPassword = !string.IsNullOrEmpty(saved?.Password);
        pgSaveResult = "";
        clearArmedUntil = DateTime.MinValue;
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

        var syncAfterLogout = configuration.SyncAfterLogout;
        if (ImGui.Checkbox("Also sync right after a character logs out", ref syncAfterLogout))
        {
            configuration.SyncAfterLogout = syncAfterLogout;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("AutoRetainer logs a character out when it has finished with it, so the app and the Discord alerts see its subs a few seconds later instead of at the next interval.");

        ImGui.Spacing();
        DrawCharacters();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Connection Mode");

        var useRemote = configuration.UseRemoteConnection;
        if (ImGui.RadioButton("Local", !useRemote))
        {
            configuration.UseRemoteConnection = false;
            configuration.Save();
            LoadSavedCredential();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Remote", useRemote))
        {
            configuration.UseRemoteConnection = true;
            configuration.Save();
            LoadSavedCredential();
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
        if (pgHasSavedPassword)
            ImGui.TextDisabled("A password is saved. Leave the box empty to keep it.");

        if (ImGui.Button("Save Postgres Credentials"))
            pgSaveResult = SaveCredential();

        ImGui.SameLine();

        // Two clicks within a few seconds, so the saved connection is not removed by a stray click.
        var clearArmed = DateTime.UtcNow < clearArmedUntil;
        if (ImGui.Button(clearArmed ? "Click again to clear" : "Clear Saved Credentials"))
        {
            if (clearArmed)
            {
                PostgresCredentialStore.Delete(configuration.UseRemoteConnection);
                LoadSavedCredential();
                pgSaveResult = "Cleared.";
            }
            else
            {
                clearArmedUntil = DateTime.UtcNow.AddSeconds(5);
            }
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

    // Saves the form. A blank password keeps the saved one; every other box must be filled in.
    private string SaveCredential()
    {
        if (string.IsNullOrWhiteSpace(pgHost) || string.IsNullOrWhiteSpace(pgDatabase) || string.IsNullOrWhiteSpace(pgUsername))
            return "Host, database and username must be filled in. Nothing was saved.";
        if (!int.TryParse(pgPort, out var portNum) || portNum < 1 || portNum > 65535)
            return "Port must be a number from 1 to 65535. Nothing was saved.";

        var password = pgPassword;
        if (password.Length == 0)
        {
            var saved = PostgresCredentialStore.Load(configuration.UseRemoteConnection);
            if (saved == null || string.IsNullOrEmpty(saved.Password))
                return "Type the password (none is saved yet). Nothing was saved.";
            password = saved.Password;
        }

        PostgresCredentialStore.Save(configuration.UseRemoteConnection, pgHost.Trim(), portNum, pgDatabase.Trim(), pgUsername.Trim(), password);
        pgPassword = "";
        pgHasSavedPassword = true;
        return "Saved.";
    }

    private void SetFcTrackerPath(string path)
    {
        configuration.FCTrackerConfigPath = path;
        configuration.Save();
    }
}
