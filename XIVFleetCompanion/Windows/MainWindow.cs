using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>The main window's tabs (2.0: one window for everything).</summary>
    public enum Tab { Summary, Subs, Supplies, Status, Settings }

    private readonly SettingsTab settings;
    private readonly FleetReader reader;
    private Tab shown = Tab.Summary;
    private Tab? select; // a tab to switch to on the next frame (from a command or Dalamud's settings button)
    private string subsFilter = "";
    private bool subsSoonestFirst;

    // "###" keeps the window's ImGui ID fixed while the title shows the version.
    public MainWindow(Plugin plugin, string submarineImagePath)
        : base($"XIV Fleet Companion v{Plugin.VersionText}###XIVFleetCompanionMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 370),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Size = new Vector2(720, 520);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.submarineImagePath = submarineImagePath;
        this.plugin = plugin;
        settings = new SettingsTab(plugin);
        reader = new FleetReader(() => PostgresWriter.BuildConnectionString(plugin.Configuration.UseRemoteConnection).connectionString);
    }

    public void Dispose() => settings.Dispose();

    /// <summary>Opens the window at a tab.</summary>
    public void OpenAt(Tab tab)
    {
        IsOpen = true;
        select = tab;
        BringToFront();
    }

    public override void OnOpen()
    {
        if (shown == Tab.Settings) settings.OnShown();
        reader.ReadIfDue(everything: true);
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##fleettabs");
        if (!tabs) return;
        TabItem(Tab.Summary, "Summary", DrawSummary);
        TabItem(Tab.Subs, "Subs", DrawSubs);
        TabItem(Tab.Supplies, "Supplies", DrawSupplies);
        TabItem(Tab.Status, "Status", DrawStatus);
        TabItem(Tab.Settings, "Settings", settings.Draw);
        select = null;
    }

    private void TabItem(Tab tab, string label, System.Action draw)
    {
        var flags = select == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (!ImGui.BeginTabItem(label, flags)) return;
        try
        {
            if (shown != tab && tab == Tab.Settings) settings.OnShown();
            shown = tab;
            using var child = ImRaii.Child($"##tab{tab}", Vector2.Zero, false);
            if (child.Success) draw();
        }
        finally
        {
            ImGui.EndTabItem();
        }
    }

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

    private void DrawStatus()
    {
        RefreshStatus();

        ImGui.Text($"Sync interval: {plugin.Configuration.SyncIntervalMinutes} minute(s){(plugin.Configuration.SyncAfterLogout ? ", and right after a character logs out" : "")}.");

        if (ImGui.Button("Check what I can see"))
        {
            plugin.OpenCheck();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Goes through every character and shows what AutoRetainer, Allagan Tools and FC Tracker have for it, without writing anything (also /xivfleet check).");
        ImGui.SameLine();
        if (ImGui.Button("Speed test (2.0)"))
        {
            plugin.OpenSpeedTest();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Times reading from the database what the 2.0 in-game windows will need, without changing anything (also /xivfleet speedtest).");

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

            // Characters this setup's AutoRetainer knows that last logged in on another setup (another account).
            var elsewhere = plugin.SyncedElsewhere;
            if (elsewhere > 0)
                ImGui.TextWrapped($"{elsewhere} character{(elsewhere == 1 ? "" : "s")} last logged in on another setup, so that setup syncs {(elsewhere == 1 ? "it" : "them")} instead.");

            // Why syncing isn't getting through right now (the database PC is off, a wrong password...).
            var problem = plugin.SyncProblem;
            if (problem != null)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.3f, 1f)))
                    ImGui.TextWrapped($"Not syncing ({plugin.SyncProblemAt:t}): {problem}");
            }

            if (string.IsNullOrWhiteSpace(plugin.Configuration.AccountLabel))
            {
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.3f, 1f)))
                    ImGui.TextWrapped(Configuration.NoAccountLabelText);
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

    // --- 2.0: the fleet, from the database's ready-made views (FleetReader) ---

    private static readonly Vector4 Amber = new(1f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 Red = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Green = new(0.55f, 0.85f, 0.55f, 1f);
    private static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);

    private static void Colored(Vector4 color, string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(text);
    }

    private static void Heading(string text)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(text);
        ImGui.Separator();
    }

    // When it was read, and why the last read failed (the last good numbers stay shown).
    private void ReadLine<T>(FleetRead<T> read, TimeSpan every) where T : class
    {
        if (read.Error != null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                ImGui.TextWrapped(read.Error);
        }
        if (read.At.HasValue)
            Colored(Dim, $"Updated {FleetReader.Ago(read.At.Value, DateTime.Now)} (every {(every.TotalMinutes >= 1 ? $"{every.TotalMinutes:0} minutes" : $"{every.TotalSeconds:0} seconds")}).");
    }

    private void RefreshButton()
    {
        using (ImRaii.Disabled(reader.Busy))
        {
            if (ImGui.Button(reader.Busy ? "Reading..." : "Refresh")) reader.ReadIfDue(everything: true);
        }
    }

    private static string Days(double? days, double? perDay) =>
        perDay == null ? "—" : days == null ? "never" : days.Value < 10 ? days.Value.ToString("0.0") : days.Value.ToString("0");

    private static Vector4? DaysColor(double? days) => days == null ? null : days < 3 ? Red : days < 7 ? Amber : null;

    private static string PerDay(double? perDay) => perDay == null ? "—" : perDay.Value < 10 ? perDay.Value.ToString("0.0") : perDay.Value.ToString("0");

    private void DrawSummary()
    {
        reader.ReadIfDue();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        RefreshButton();

        Heading("Subs");
        var subs = reader.Subs.Data;
        if (subs == null && reader.Subs.Error == null) ImGui.TextUnformatted("Reading...");
        if (subs != null)
        {
            var back = subs.Count(x => x.ReturnTime > 0 && x.ReturnTime <= now);
            var outNow = subs.Where(x => x.ReturnTime > now).ToList();
            var inPort = subs.Count(x => x.ReturnTime <= 0);
            if (back > 0) Colored(Green, $"{back} back and waiting to be collected");
            else ImGui.TextUnformatted("None back and waiting");
            ImGui.TextUnformatted(outNow.Count == 0 ? "None out" :
                $"{outNow.Count} out; the next is back in {FleetReader.Countdown(outNow.Min(x => x.ReturnTime) - now)}");
            if (inPort > 0) ImGui.TextUnformatted($"{inPort} in port (not sent out)");
        }
        ReadLine(reader.Subs, FleetReader.SubsEvery);

        Heading("Retainers");
        var summary = reader.Summary.Data;
        if (summary == null && reader.Summary.Error == null) ImGui.TextUnformatted("Reading...");
        if (summary != null)
        {
            if (summary.VenturesDone > 0) Colored(Green, $"{summary.VenturesDone} venture{(summary.VenturesDone == 1 ? "" : "s")} done");
            else ImGui.TextUnformatted("No ventures done yet");
            if (summary.RetainersIdle > 0) Colored(Amber, $"{summary.RetainersIdle} retainer{(summary.RetainersIdle == 1 ? "" : "s")} without a venture");
            ImGui.TextUnformatted($"{summary.Retainers} retainer{(summary.Retainers == 1 ? "" : "s")} with a class");
        }

        Heading("Supplies");
        if (summary != null)
        {
            if (summary.LowCeruleum > 0) Colored(Amber, $"{summary.LowCeruleum} character{(summary.LowCeruleum == 1 ? "" : "s")} low on ceruleum");
            if (summary.LowRepairKits > 0) Colored(Amber, $"{summary.LowRepairKits} character{(summary.LowRepairKits == 1 ? "" : "s")} low on repair kits");
            if (summary.LowCeruleum == 0 && summary.LowRepairKits == 0) ImGui.TextUnformatted("Nobody is below the low-supply warnings (set in the app: Settings → General → Warnings)");
            if (summary.SoonestRunsOutDays is double soonest)
            {
                var text = $"The first to run out does in {Days(soonest, 1)} days (Supplies tab)";
                if (DaysColor(soonest) is Vector4 color) Colored(color, text); else ImGui.TextUnformatted(text);
            }
        }
        ReadLine(reader.Summary, FleetReader.SuppliesEvery);
    }

    private void DrawSubs()
    {
        reader.ReadIfDue();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        RefreshButton();
        ImGui.SameLine();
        ImGui.Checkbox("Soonest back first", ref subsSoonestFirst);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.InputText("Filter", ref subsFilter, 64);
        ReadLine(reader.Subs, FleetReader.SubsEvery);

        var subs = reader.Subs.Data;
        if (subs == null) return;
        IEnumerable<SubRow> rows = subs;
        if (!string.IsNullOrWhiteSpace(subsFilter))
            rows = rows.Where(x => x.Character.Contains(subsFilter, StringComparison.OrdinalIgnoreCase)
                                || x.SubName.Contains(subsFilter, StringComparison.OrdinalIgnoreCase)
                                || x.Account.Contains(subsFilter, StringComparison.OrdinalIgnoreCase));
        // Soonest first: back and waiting, then the soonest back, in port last.
        if (subsSoonestFirst) rows = rows.OrderBy(x => x.ReturnTime <= 0 ? long.MaxValue : x.ReturnTime);
        var list = rows.ToList();

        using var table = ImRaii.Table("##subs", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("Sub");
        ImGui.TableSetupColumn("Rank");
        ImGui.TableSetupColumn("Build");
        ImGui.TableSetupColumn("Status");
        ImGui.TableHeadersRow();
        string? lastCharacter = null;
        foreach (var sub in list)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var who = $"{sub.Character} ({sub.World})";
            // Grouped by character: its name once, on its first sub.
            if (subsSoonestFirst || who != lastCharacter) ImGui.TextUnformatted(who);
            if (ImGui.IsItemHovered() && sub.Account.Length > 0) ImGui.SetTooltip($"Account: {sub.Account}");
            lastCharacter = who;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sub.SubName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sub.Rank.ToString());
            if (ImGui.IsItemHovered() && sub.NextLevelExp > 0)
                ImGui.SetTooltip($"{100.0 * sub.CurrentExp / sub.NextLevelExp:0}% of the way to rank {sub.Rank + 1}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sub.Build);
            ImGui.TableNextColumn();
            var status = FleetReader.SubStatus(sub.ReturnTime, now);
            if (status == "Back") Colored(Green, status);
            else if (status == "In port") Colored(Dim, status);
            else ImGui.TextUnformatted(status);
        }
    }

    private void DrawSupplies()
    {
        reader.ReadIfDue();
        RefreshButton();
        var supplies = reader.Supplies.Data;
        if (supplies != null && supplies.Count > 0)
        {
            ImGui.SameLine();
            Colored(Dim, $"Use per day over the last {supplies[0].WindowDays} days; to buy: enough for {supplies[0].TargetDays} days (both set in the app's Supplies tab).");
        }
        ReadLine(reader.Supplies, FleetReader.SuppliesEvery);
        if (supplies == null) return;

        using var table = ImRaii.Table("##supplies", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("Ceruleum");
        ImGui.TableSetupColumn("A day");
        ImGui.TableSetupColumn("Days left");
        ImGui.TableSetupColumn("Repair kits");
        ImGui.TableSetupColumn("A day##kits");
        ImGui.TableSetupColumn("Days left##kits");
        ImGui.TableSetupColumn("To buy");
        ImGui.TableHeadersRow();
        foreach (var c in supplies)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{c.Character} ({c.World})");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Account: {(c.Account.Length > 0 ? c.Account : "none")}{(c.LastSynced.HasValue ? $"\nLast synced {c.LastSynced.Value:g}" : "")}");
            SupplyCells(c.Ceruleum, c.CeruleumPerDay, c.CeruleumDaysLeft);
            SupplyCells(c.RepairKits, c.RepairKitsPerDay, c.RepairKitsDaysLeft);
            ImGui.TableNextColumn();
            var buy = new List<string>();
            if (c.CeruleumRestock > 0) buy.Add($"{c.CeruleumRestock:N0} ceruleum");
            if (c.RepairKitsRestock > 0) buy.Add($"{c.RepairKitsRestock:N0} kits");
            ImGui.TextUnformatted(buy.Count > 0 ? string.Join(", ", buy) : "—");
        }
    }

    private static void SupplyCells(int have, double? perDay, double? daysLeft)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(have.ToString("N0"));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(PerDay(perDay));
        if (perDay == null && ImGui.IsItemHovered()) ImGui.SetTooltip("Not enough history yet (needs a day).");
        ImGui.TableNextColumn();
        var days = Days(daysLeft, perDay);
        if (DaysColor(daysLeft) is Vector4 color) Colored(color, days); else ImGui.TextUnformatted(days);
    }
}
