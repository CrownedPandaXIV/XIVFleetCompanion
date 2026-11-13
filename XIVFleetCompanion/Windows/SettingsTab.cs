using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace XIVFleetCompanion.Windows;

/// <summary>
/// The Settings tab of the main window (until 2.0 its own window, which Dalamud's settings button now opens the main
/// window at).
/// </summary>
public class SettingsTab : IDisposable
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

    public SettingsTab(Plugin plugin)
    {
        configuration = plugin.Configuration;
        this.plugin = plugin;
    }

    public void Dispose() { }

    /// <summary>When the tab is shown: the saved connection is read again and the character list re-read.</summary>
    public void OnShown()
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
    // The shared database's connection (Shared between PCs): Local or Remote, and its saved login.
    public void DrawDatabaseConnection()
    {
        Ui.Text("Connection Mode");

        var useRemote = configuration.UseRemoteConnection;
        if (ImGui.RadioButton("Local", !useRemote))
        {
            configuration.UseRemoteConnection = false;
            configuration.Save();
            Plugin.SaveChoiceForPc(configuration);
            LoadSavedCredential();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Remote", useRemote))
        {
            configuration.UseRemoteConnection = true;
            configuration.Save();
            Plugin.SaveChoiceForPc(configuration);
            LoadSavedCredential();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Ui.Text($"Database Connection ({(configuration.UseRemoteConnection ? "Remote" : "Local")})");

        ImGui.InputText("Host", ref pgHost, 100);
        ImGui.InputText("Port", ref pgPort, 10);
        ImGui.InputText("Database", ref pgDatabase, 100);
        ImGui.InputText("Username", ref pgUsername, 100);
        ImGui.InputText("Password", ref pgPassword, 100, ImGuiInputTextFlags.Password);
        if (pgHasSavedPassword)
            Ui.Disabled("A password is saved. Leave the box empty to keep it.");

        if (ImGui.Button("Save Database Credentials"))
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
            Ui.Wrapped(pgSaveResult);
        }
    }

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
            Ui.Wrapped("AutoRetainer is not running or knows no characters on this client.");
            return;
        }

        var syncing = list.Count(c => configuration.ShouldSync(c.Cid));
        Ui.Text($"Syncing {syncing} of {list.Count} characters.");
        Ui.Disabled("Left-out characters are not read or sent." + (configuration.FleetStore == FleetStoreChoice.ThisPc
            ? " What they sent before stays until they're synced again."
            : " Anything they sent before can be removed with the app's Remove button on the Roster."));

        using (var child = ImRaii.Child("##characters", new Vector2(0, Math.Min(list.Count, 10) * ImGui.GetFrameHeightWithSpacing() + 8), true))
        {
            if (child.Success)
            {
                foreach (var c in list)
                {
                    var sync = configuration.ShouldSync(c.Cid);
                    var subs = c.Subs > 0 ? $"  ({c.Subs} sub{(c.Subs == 1 ? "" : "s")})" : "";
                    if (Ui.Checkbox($"{c.Name} @ {c.World}{subs}##{c.Cid}", ref sync))
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

    /// <summary>shared: the settings shared with the app (drawn by the main window, after the Characters list).</summary>
    public void Draw(Action? shared = null)
    {
        // Can't ref a property, so use a local copy
        var enabled = configuration.Enabled;
        if (Ui.Checkbox("Fleet Sync Enabled", ref enabled))
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
        if (string.IsNullOrWhiteSpace(configuration.AccountLabel))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Status(configuration.Theme, configuration.ColourBlindStatus).Warn))
                Ui.Wrapped(Configuration.NoAccountLabelText);
        }

        var syncInterval = configuration.SyncIntervalMinutes;
        if (Ui.InputInt("Sync Interval (minutes)", ref syncInterval))
        {
            if (syncInterval < 1)
                syncInterval = 1;

            configuration.SyncIntervalMinutes = syncInterval;
            configuration.Save();
        }

        var syncAfterLogout = configuration.SyncAfterLogout;
        if (Ui.Checkbox("Also sync right after a character logs out", ref syncAfterLogout))
        {
            configuration.SyncAfterLogout = syncAfterLogout;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("AutoRetainer logs a character out when it has finished with it, so its subs show a few seconds later instead of at the next interval.");

        var showInInfoBar = configuration.ShowInInfoBar;
        if (Ui.Checkbox("Show Fleet in the game's info bar", ref showInInfoBar))
        {
            configuration.ShowInInfoBar = showInInfoBar;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("\"Fleet\" by the clock at the top right: it says what needs you (subs back, supplies running low, houses...); hover for the list, click to open the Summary. Dalamud's own Server Info Bar settings can hide it too.");

        // The look of this window (Theme.cs): only it changes, never the game or other plugins. The app has the same looks.
        ImGui.Spacing();
        Ui.Text("Look:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var current = Theme.Info(configuration.Theme);
        using (var combo = Ui.Combo("##look", current.Name))
        {
            if (combo)
            {
                foreach (var look in Theme.Looks)
                {
                    if (Ui.Selectable($"{look.Name}##look{look.Key}", look.Choice == configuration.Theme))
                    {
                        configuration.Theme = look.Choice;
                        configuration.Save();
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(look.About);
                    ImGui.SameLine(170 * ImGuiHelpers.GlobalScale);
                    Swatches(look.Choice);
                }
            }
        }
        ImGui.SameLine();
        Swatches(configuration.Theme);
        Ui.Disabled(current.About);
        var colourBlind = configuration.ColourBlindStatus;
        if (Ui.Checkbox("Colour-blind friendly status colors", ref colourBlind))
        {
            configuration.ColourBlindStatus = colourBlind;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Blue for fine, yellow for a look, orange-red for urgent, in place of green, amber and red: told apart with every common kind of colour blindness.");

        ImGui.Spacing();
        DrawCharacters();

        if (shared != null)
        {
            ImGui.Spacing();
            shared();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (configuration.FleetStore == FleetStoreChoice.ThisPc)
        {
            Ui.Text("Fleet data: Just this PC");
            Ui.Wrapped($"Every game on this PC keeps your fleet in {FileDatabase.DefaultPath}, backed up once a day " +
                              "(the Status page has Back up now). There's no database to connect to.");
        }
        else
            DrawDatabaseConnection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Ui.Text("FC Tracker Config Path");
        Ui.Wrapped("Points at FC Tracker's config file for this XIVLauncher install, used to read FC/housing data.");

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
        var status = Theme.Status(configuration.Theme, configuration.ColourBlindStatus);
        using (ImRaii.PushColor(ImGuiCol.Text, fcTrackerFileFound ? status.Good : status.Warn))
        {
            Ui.Wrapped(fcTrackerFileFound
                ? "Found."
                : "Not found: Free Company and housing details are not synced until this points at FC Tracker's config file.");
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

    // A strip of a look's colours.
    private static void Swatches(ThemeChoice theme)
    {
        var size = ImGui.GetTextLineHeight() * 0.8f;
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetTextLineHeight() - size) / 2);
        var colors = Theme.Swatches(theme);
        for (var i = 0; i < colors.Length; i++)
        {
            var min = at + new Vector2(i * (size + 3), 0);
            draw.AddRectFilled(min, min + new Vector2(size, size), ImGui.GetColorU32(colors[i]), 2f);
            draw.AddRect(min, min + new Vector2(size, size), ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.6f)), 2f);
        }
        ImGui.Dummy(new Vector2(colors.Length * (size + 3), ImGui.GetTextLineHeight()));
    }

    private void SetFcTrackerPath(string path)
    {
        configuration.FCTrackerConfigPath = path;
        configuration.Save();
    }
}
