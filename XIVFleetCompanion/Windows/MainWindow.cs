using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
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

public partial class MainWindow : Window, IDisposable
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

    /// <summary>The main window's pages, chosen in its sidebar (2.x: one window for everything).</summary>
    public enum Tab { Summary, Subs, Supplies, Roster, Retainers, Fcs, Inventory, Salvage, Income, Pcs, Check, SpeedTest, Status, Settings }

    private readonly SettingsTab settings;
    private readonly FleetReader reader;
    private Tab shown = Tab.Summary;
    private string subsFilter = "";
    private int themeColors; // how many colors the look pushed this frame (PreDraw), popped in PostDraw

    // "###" keeps the window's ImGui ID fixed while the title shows the version.
    public MainWindow(Plugin plugin, string submarineImagePath)
        : base($"XIV Fleet Companion v{Plugin.VersionText}###XIVFleetCompanionMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Size = new Vector2(860, 560);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.submarineImagePath = submarineImagePath;
        this.plugin = plugin;
        settings = new SettingsTab(plugin);
        reader = plugin.Fleet;
    }

    public void Dispose() => settings.Dispose();

    /// <summary>Opens the window at a page.</summary>
    public void OpenAt(Tab tab)
    {
        IsOpen = true;
        Show(tab);
        BringToFront();
    }

    private void Show(Tab tab)
    {
        if (tab == Tab.Settings && shown != Tab.Settings) settings.OnShown();
        shown = tab;
    }

    public override void OnOpen()
    {
        if (shown == Tab.Settings) settings.OnShown();
        reader.ReadIfDue(everything: true);
    }

    // The look (Settings → Look) applies to this window only.
    public override void PreDraw() => themeColors = Theme.Push(plugin.Configuration.Theme);
    public override void PostDraw()
    {
        Theme.Pop(themeColors);
        themeColors = 0;
    }

    private StatusColors Palette => Theme.Status(plugin.Configuration.Theme, plugin.Configuration.ColourBlindStatus);

    public override void Draw()
    {
        var folded = plugin.Configuration.SidebarFolded;
        var width = (folded ? 46f : 210f) * ImGuiHelpers.GlobalScale;
        var sidebarBg = Theme.SidebarBg(plugin.Configuration.Theme);
        using (ImRaii.PushColor(ImGuiCol.ChildBg, sidebarBg ?? Vector4.Zero, sidebarBg.HasValue))
        using (var side = ImRaii.Child("##sidebar", new Vector2(width, 0), true))
        {
            if (side.Success) DrawSidebar(folded);
        }
        ImGui.SameLine();
        using var page = ImRaii.Child("##page", Vector2.Zero, false);
        if (!page.Success) return;
        switch (shown)
        {
            case Tab.Summary: DrawSummary(); break;
            case Tab.Subs: DrawSubs(); break;
            case Tab.Supplies: DrawSupplies(); break;
            case Tab.Roster: DrawRoster(); break;
            case Tab.Retainers: DrawRetainers(); break;
            case Tab.Fcs: DrawFcs(); break;
            case Tab.Inventory: DrawInventory(); break;
            case Tab.Salvage: DrawSalvage(); break;
            case Tab.Income: DrawIncome(); break;
            case Tab.Pcs: DrawPcs(); break;
            case Tab.Check: plugin.CheckPage.DrawPage(); break;
            case Tab.SpeedTest: plugin.SpeedTestPage.DrawPage(); break;
            case Tab.Status: DrawStatus(); break;
            case Tab.Settings: settings.Draw(); break;
        }
    }

    // --- The sidebar: the submarine, the pages in groups with what needs attention beside them, and this PC's sync. ---

    private static readonly string[] Groups = { "Fleet", "Tools", "System" };

    private void DrawSidebar(bool folded)
    {
        var p = Palette;
        var config = plugin.Configuration;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        reader.ReadIfDue();

        // Fold or unfold, top right.
        var foldIcon = folded ? FontAwesomeIcon.AngleDoubleRight : FontAwesomeIcon.AngleDoubleLeft;
        if (!folded) ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() + ImGui.GetCursorPosX());
        if (ImGuiComponents.IconButton("##fold", foldIcon))
        {
            config.SidebarFolded = !folded;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(folded ? "Show the sidebar" : "Fold the sidebar down to its icons");

        // The submarine.
        var image = Plugin.TextureProvider.GetFromFile(submarineImagePath).GetWrapOrDefault();
        if (image != null)
        {
            var side = (folded ? 30f : 110f) * ImGuiHelpers.GlobalScale;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - side) / 2));
            ImGui.Image(image.Handle, new Vector2(side, side));
        }
        ImGui.Spacing();

        // What each page's line says beside it.
        var back = FleetReader.SubsBack(reader.Subs.Data, now);
        var alerts = reader.Alerts.Data;
        var low = alerts?.Where(a => a.Kind is "low_ceruleum" or "low_repair_kits").Select(a => a.Subject).Distinct().Count() ?? 0;
        var attention = back + (alerts?.Count ?? 0);
        var statusProblem = plugin.SyncProblem != null || (plugin.AutoRetainer != null && !SafeReady());
        var notSynced = alerts?.Count(a => a.Kind == "not_synced") ?? 0;
        var venturesDone = reader.RetainerCounts.Data?.VenturesDone ?? 0;
        var housesAtRisk = alerts?.Count(a => a.Kind == "house") ?? 0;
        var setupsToCheck = alerts?.Count(a => a.Kind == "autoretainer") ?? 0;

        foreach (var group in Groups)
        {
            var open = true;
            if (!folded)
            {
                ImGui.Spacing();
                using (ImRaii.PushColor(ImGuiCol.Text, p.Dim))
                    open = ImGui.TreeNodeEx($"{group.ToUpperInvariant()}##group{group}", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
            }
            else
            {
                ImGui.Separator();
            }
            if (!open) continue;
            switch (group)
            {
                case "Fleet":
                    Entry(Tab.Summary, FontAwesomeIcon.ClipboardList, "Summary", folded, attention > 0 ? attention.ToString() : null, attention > 0 ? p.Warn : p.Dim,
                        attention > 0 ? $"{attention} thing{(attention == 1 ? "" : "s")} need{(attention == 1 ? "s" : "")} attention" : "Nothing needs you");
                    Entry(Tab.Subs, FontAwesomeIcon.Ship, "Subs", folded, back > 0 ? $"{back} back" : null, p.Good, back > 0 ? $"{back} back and waiting" : null);
                    Entry(Tab.Supplies, FontAwesomeIcon.GasPump, "Supplies", folded, low > 0 ? $"{low} low" : null, p.Warn, low > 0 ? $"{low} character{(low == 1 ? "" : "s")} low" : null);
                    Entry(Tab.Roster, FontAwesomeIcon.Users, "Roster", folded, notSynced > 0 ? $"{notSynced} not synced" : null, p.Warn,
                        notSynced > 0 ? $"{notSynced} character{(notSynced == 1 ? "" : "s")} no longer synced" : null);
                    Entry(Tab.Retainers, FontAwesomeIcon.UserTie, "Retainers", folded, venturesDone > 0 ? $"{venturesDone} done" : null, p.Good,
                        venturesDone > 0 ? $"{venturesDone} venture{(venturesDone == 1 ? "" : "s")} done" : null);
                    Entry(Tab.Fcs, FontAwesomeIcon.Home, "Free Companies", folded, housesAtRisk > 0 ? $"{housesAtRisk} house{(housesAtRisk == 1 ? "" : "s")}" : null, p.Warn,
                        housesAtRisk > 0 ? $"{housesAtRisk} house{(housesAtRisk == 1 ? "" : "s")} not entered for a while" : null);
                    Entry(Tab.Inventory, FontAwesomeIcon.BoxOpen, "Inventory", folded, null, p.Dim, "What the fleet holds, and Where is it?");
                    Entry(Tab.Salvage, FontAwesomeIcon.Recycle, "Salvage", folded, null, p.Dim, "The salvage each character holds");
                    Entry(Tab.Income, FontAwesomeIcon.Coins, "Income", folded, null, p.Dim, "Real income from the subs, and venture rewards");
                    break;
                case "Tools":
                    Entry(Tab.Check, FontAwesomeIcon.Search, "Check what I can see", folded, null, p.Dim, null);
                    Entry(Tab.SpeedTest, FontAwesomeIcon.Stopwatch, "Speed test", folded, null, p.Dim, null);
                    break;
                case "System":
                    Entry(Tab.Pcs, FontAwesomeIcon.Desktop, "PCs", folded, setupsToCheck > 0 ? setupsToCheck.ToString() : null, p.Warn,
                        setupsToCheck > 0 ? $"{setupsToCheck} setup{(setupsToCheck == 1 ? "" : "s")} without AutoRetainer running" : null);
                    Entry(Tab.Status, FontAwesomeIcon.Plug, "Status", folded, statusProblem ? "!" : null, p.Warn, statusProblem ? "Something needs a look" : null);
                    Entry(Tab.Settings, FontAwesomeIcon.Cog, "Settings", folded, null, p.Dim, null);
                    break;
            }
        }

        // This PC's sync, at the foot.
        var line = plugin.SyncProblem != null ? "Not syncing" :
            config.LastSyncTimestamp.HasValue ? $"Synced {FleetReader.Ago(config.LastSyncTimestamp.Value, DateTime.Now)}" : "Not synced yet";
        var lineColor = plugin.SyncProblem != null ? p.Warn : p.Dim;
        var height = ImGui.GetTextLineHeightWithSpacing();
        var y = ImGui.GetWindowHeight() - height - ImGui.GetStyle().WindowPadding.Y;
        if (y > ImGui.GetCursorPosY()) ImGui.SetCursorPosY(y);
        if (folded)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            using (ImRaii.PushColor(ImGuiCol.Text, plugin.SyncProblem != null ? p.Warn : config.Enabled ? p.Good : p.Dim))
                ImGui.TextUnformatted(FontAwesomeIcon.Sync.ToIconString());
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.Text, lineColor))
                ImGui.TextUnformatted(config.Enabled ? line : "Syncing is off");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(plugin.SyncProblem != null ? $"Not syncing ({plugin.SyncProblemAt:t}): {plugin.SyncProblem}" :
                config.Enabled ? $"This PC syncs every {config.SyncIntervalMinutes} minute(s). {line}." : "Turn syncing on in Settings.");
    }

    private bool SafeReady()
    {
        try { return plugin.AutoRetainer!.Ready; }
        catch { return false; }
    }

    // One page in the sidebar: its icon and name, and what needs attention beside it (badge). Folded: the icon only,
    // with the name and the badge when hovered.
    private void Entry(Tab tab, FontAwesomeIcon icon, string label, bool folded, string? badge, Vector4 badgeColor, string? hover)
    {
        var start = ImGui.GetCursorPos();
        var rowHeight = ImGui.GetTextLineHeight() * 1.5f;
        if (ImGui.Selectable($"##page{tab}", shown == tab, ImGuiSelectableFlags.None, new Vector2(0, rowHeight))) Show(tab);
        var hovered = ImGui.IsItemHovered();
        var after = ImGui.GetCursorPos();
        var pad = (rowHeight - ImGui.GetTextLineHeight()) / 2;
        ImGui.SetCursorPos(new Vector2(start.X + 6 * ImGuiHelpers.GlobalScale, start.Y + pad));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, badgeColor, folded && badge != null))
            ImGui.TextUnformatted(icon.ToIconString());
        if (!folded)
        {
            ImGui.SameLine(start.X + 30 * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(label);
            if (badge != null)
            {
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, badgeColor))
                    ImGui.TextUnformatted($"· {badge}");
            }
        }
        ImGui.SetCursorPos(after);
        if (hovered && (folded || hover != null))
            ImGui.SetTooltip(folded ? (hover != null ? $"{label}: {hover}" : label) : hover!);
    }

    // The credits, on the Status page.
    private void DrawCredits()
    {
        ImGui.Spacing();
        ImGui.Separator();
        using (ImRaii.PushColor(ImGuiCol.Text, Palette.Dim))
            ImGui.TextWrapped($"XIV Fleet Companion v{Plugin.VersionText} by CrownedPandaXIV. Thanks to kawaii, NightmareXIV and the Puni.sh team (AutoRetainer), " +
                              "Critical_Impact (Allagan Tools), erdelf (FC Tracker) and Infi (Submarine Tracker) for their plugins.");
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
                using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                    ImGui.TextWrapped($"Not syncing ({plugin.SyncProblemAt:t}): {problem}");
            }

            if (string.IsNullOrWhiteSpace(plugin.Configuration.AccountLabel))
            {
                using (ImRaii.PushColor(ImGuiCol.Text, Amber))
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

        DrawCredits();
    }

    // --- 2.0: the fleet, from the database's ready-made views (FleetReader) ---

    // The status colors of the chosen look (Theme.cs).
    private Vector4 Amber => Palette.Warn;
    private Vector4 Red => Palette.Danger;
    private Vector4 Green => Palette.Good;
    private Vector4 Dim => Palette.Dim;

    // Text in a color that says what it is (gil, an address, an FC's name), or plain when the look has none.
    private static void Tinted(Vector4? color, string text)
    {
        if (color is Vector4 c) Colored(c, text); else ImGui.TextUnformatted(text);
    }

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

    // "Label: 1,234 gil" with the amount in gil's gold (plain in a look without one).
    private void GilLine(string label, double gil, string after = "")
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(0, ImGui.CalcTextSize(" ").X);
        Tinted(Palette.Gil, $"{Number((long)Math.Round(gil))} gil");
        if (after.Length > 0)
        {
            ImGui.SameLine(0, 0);
            ImGui.TextUnformatted(after);
        }
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

    private Vector4? DaysColor(double? days) => days == null ? null : days < 3 ? Red : days < 7 ? Amber : null;

    private static string PerDay(double? perDay) => perDay == null ? "—" : perDay.Value < 10 ? perDay.Value.ToString("0.0") : perDay.Value.ToString("0");

    private void DrawSummary()
    {
        reader.ReadIfDue();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        RefreshButton();

        // What needs you, worst first: subs back and waiting (counted live), then the database's alerts.
        Heading("Needs attention");
        var back = FleetReader.SubsBack(reader.Subs.Data, now);
        var alerts = reader.Alerts.Data;
        if (back > 0) Colored(Green, $"{back} sub{(back == 1 ? " is" : "s are")} back and waiting to be collected (Subs tab)");
        if (alerts != null)
        {
            foreach (var alert in alerts)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, alert.Level == "danger" ? Red : Amber))
                    ImGui.TextWrapped($"{alert.Subject}: {alert.Detail}");
            }
            if (back == 0 && alerts.Count == 0) ImGui.TextUnformatted("Nothing needs you right now.");
        }
        else if (reader.Alerts.Error == null) ImGui.TextUnformatted("Reading...");
        if (reader.Alerts.Error != null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                ImGui.TextWrapped(reader.Alerts.Error);
        }

        Heading("Subs");
        var subs = reader.Subs.Data;
        if (subs == null && reader.Subs.Error == null) ImGui.TextUnformatted("Reading...");
        if (subs != null)
        {
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
        var counts = reader.RetainerCounts.Data;
        if (counts == null && reader.RetainerCounts.Error == null) ImGui.TextUnformatted("Reading...");
        if (counts != null)
        {
            if (counts.VenturesDone > 0) Colored(Green, $"{counts.VenturesDone} venture{(counts.VenturesDone == 1 ? "" : "s")} done");
            else ImGui.TextUnformatted("No ventures done yet");
            if (counts.Idle > 0) Colored(Amber, $"{counts.Idle} retainer{(counts.Idle == 1 ? "" : "s")} without a venture");
            ImGui.TextUnformatted($"{counts.Retainers} retainer{(counts.Retainers == 1 ? "" : "s")} with a class");
        }
        ReadLine(reader.RetainerCounts, FleetReader.SubsEvery);
        // As the app's Venture Income tile: the rewards over the Income window, at what a vendor pays.
        var ventures = reader.Ventures.Data;
        if (ventures != null && ventures.Rows.Count == 0) ImGui.TextUnformatted("Venture income: no rewards recorded in the Income window (AutoRetainer: \"Record statistics\")");
        else if (ventures != null)
        {
            var perDay = FleetReader.SummarizeVentures(ventures.Rows, ventures.WindowDays, VendorPrice).Sum(v => v.PerDay);
            ImGui.TextUnformatted($"Venture income: {Gil(perDay)} gil a day");
            Colored(Dim, $"Last {ventures.WindowDays} days, at what a vendor pays (Income page)");
        }
        if (reader.Ventures.Error != null) Colored(Amber, reader.Ventures.Error);

        var summary = reader.Summary.Data;
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

        // As the app's Summary: the fleet-wide salvage total (the Salvage page has each character's).
        Heading("Salvage");
        var salvage = reader.Salvage.Data;
        if (salvage != null) GilLine("Fleet-Wide Salvage Total:", salvage.Sum(x => x.Gil));
        else if (reader.Salvage.Error == null) ImGui.TextUnformatted("Reading...");
        ReadLine(reader.Salvage, FleetReader.SuppliesEvery);
    }

    private FleetTable<SubRow>? subsTable;
    private FleetTable<SupplyRow>? suppliesTable;

    private FleetTable<SubRow> SubsTable => subsTable ??= new FleetTable<SubRow>("subs", plugin.Configuration, new List<FleetColumn<SubRow>>
    {
        // The character once, on its first sub (while the table isn't sorted by a column).
        new("character", "Character", sub =>
        {
            if (SubsTable.SameAsAbove) return;
            CharacterCell(sub.Character, sub.World);
            if (sub.Account.Length > 0) Hover($"Account: {sub.Account}");
        }, sub => sub.Character),
        new("account", "Account", sub => ImGui.TextUnformatted(sub.Account.Length > 0 ? sub.Account : "—"), sub => sub.Account, Hidden: true),
        new("sub", "Sub", sub => ImGui.TextUnformatted(sub.SubName), sub => sub.SubName, Locked: true),
        // The rank, with a thin bar for how far to the next.
        new("rank", "Rank", sub =>
        {
            if (sub.NextLevelExp > 0)
            {
                TableCells.Bar((double)sub.CurrentExp / sub.NextLevelExp, 36 * ImGuiHelpers.GlobalScale);
                Hover($"{100.0 * sub.CurrentExp / sub.NextLevelExp:0}% of the way to rank {sub.Rank + 1}");
                ImGui.SameLine();
            }
            RightNumber(sub.Rank);
            if (sub.NextLevelExp > 0) Hover($"{100.0 * sub.CurrentExp / sub.NextLevelExp:0}% of the way to rank {sub.Rank + 1}");
        }, sub => sub.Rank),
        new("build", "Build", sub => BuildCell(sub.Build), sub => sub.Build),
        // Status: back and waiting first, then the soonest back; in port last either way.
        new("status", "Status", sub =>
        {
            var status = FleetReader.SubStatus(sub.ReturnTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (status == "Back") Colored(Green, status);
            else if (status == "In port") Colored(Dim, status);
            else ImGui.TextUnformatted(status);
        }, sub => sub.ReturnTime, sub => sub.ReturnTime <= 0),
    }, sub => sub.Account, "subs", runKey: sub => $"{sub.Character}@{sub.World}");

    // "W-S-U-C" with the dashes softer, so the four parts stand out.
    private void BuildCell(string build)
    {
        var parts = build.Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0, 0);
                Colored(Dim, "-");
                ImGui.SameLine(0, 0);
            }
            ImGui.TextUnformatted(parts[i]);
        }
    }

    private void DrawSubs()
    {
        reader.ReadIfDue();
        RefreshButton();
        ImGui.SameLine();
        SubsTable.DrawControls();
        FilterBox(ref subsFilter, "subs");
        ReadLine(reader.Subs, FleetReader.SubsEvery);
        var subs = reader.Subs.Data;
        if (subs == null) return;
        // In the app's order: account, the Roster's character order, then slot.
        SubsTable.Draw(subs.Where(x => Matches(subsFilter, x.Character, x.SubName, x.Account, x.Build)).ToList());
    }

    // Days left: "never" (none used) counts as the most; not enough history has none and goes last.
    private static double DaysKey(double? days) => days ?? double.MaxValue;

    private FleetTable<SupplyRow> SuppliesTable => suppliesTable ??= new FleetTable<SupplyRow>("supplies", plugin.Configuration, new List<FleetColumn<SupplyRow>>
    {
        new("character", "Character", c =>
        {
            CharacterCell(c.Character, c.World);
            Hover($"Account: {(c.Account.Length > 0 ? c.Account : "none")}{(c.LastSynced.HasValue ? $"\nLast synced {c.LastSynced.Value:g}" : "")}");
        }, c => c.Character, Locked: true),
        new("account", "Account", c => ImGui.TextUnformatted(c.Account.Length > 0 ? c.Account : "—"), c => c.Account, Hidden: true),
        new("ceruleum", "Ceruleum", c => RightNumber(c.Ceruleum, DaysColor(c.CeruleumDaysLeft) ?? (c.CeruleumPerDay != null ? Green : (Vector4?)null)), c => c.Ceruleum),
        new("ceruleumPerDay", "A day##ceruleum", c => PerDayCell(c.CeruleumPerDay), c => c.CeruleumPerDay ?? 0, c => c.CeruleumPerDay == null),
        new("ceruleumDays", "Days left##ceruleum", c => DaysCell(c.CeruleumDaysLeft, c.CeruleumPerDay), c => DaysKey(c.CeruleumDaysLeft), c => c.CeruleumPerDay == null),
        new("kits", "Repair kits", c => RightNumber(c.RepairKits, DaysColor(c.RepairKitsDaysLeft) ?? (c.RepairKitsPerDay != null ? Green : (Vector4?)null)), c => c.RepairKits),
        new("kitsPerDay", "A day##kits", c => PerDayCell(c.RepairKitsPerDay), c => c.RepairKitsPerDay ?? 0, c => c.RepairKitsPerDay == null),
        new("kitsDays", "Days left##kits", c => DaysCell(c.RepairKitsDaysLeft, c.RepairKitsPerDay), c => DaysKey(c.RepairKitsDaysLeft), c => c.RepairKitsPerDay == null),
        new("buy", "To buy", c =>
        {
            var buy = new List<string>();
            if (c.CeruleumRestock > 0) buy.Add($"{c.CeruleumRestock:N0} ceruleum");
            if (c.RepairKitsRestock > 0) buy.Add($"{c.RepairKitsRestock:N0} kits");
            if (buy.Count > 0) Colored(Amber, string.Join(", ", buy)); else Colored(Green, "Nothing");
        }, c => (long)c.CeruleumRestock * 1_000_000 + c.RepairKitsRestock),
    }, c => c.Account, "characters");

    private void DrawSupplies()
    {
        reader.ReadIfDue();
        RefreshButton();
        ImGui.SameLine();
        SuppliesTable.DrawControls();
        var supplies = reader.Supplies.Data;
        if (supplies != null && supplies.Count > 0)
        {
            ImGui.SameLine();
            Colored(Dim, $"Use per day over the last {supplies[0].WindowDays} days; to buy: enough for {supplies[0].TargetDays} days (both set in the app's Supplies tab).");
        }
        ReadLine(reader.Supplies, FleetReader.SuppliesEvery);
        if (supplies == null) return;
        SuppliesTable.Draw(supplies);
    }

    private void PerDayCell(double? perDay)
    {
        TableCells.Right(PerDay(perDay), perDay == null ? Dim : (Vector4?)null);
        if (perDay == null) Hover("Not enough history yet (needs a day).");
    }

    private void DaysCell(double? daysLeft, double? perDay)
    {
        // Red under 3 days, amber under 7, green otherwise (and when none are used); dim without enough history.
        var days = Days(daysLeft, perDay);
        TableCells.Right(days, perDay == null ? Dim : DaysColor(daysLeft) ?? Green);
    }
}
