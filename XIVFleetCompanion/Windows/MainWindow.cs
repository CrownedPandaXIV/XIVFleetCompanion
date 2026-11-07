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
    public enum Tab { Summary, Subs, Supplies, Parts, Roster, Retainers, Fcs, Inventory, Salvage, Income, Market, Pcs, Check, SpeedTest, Status, Settings, Routes, Trends }

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
    public override void PreDraw()
    {
        themeColors = Theme.Push(plugin.Configuration.Theme);
        // Headings and box titles in the look's colours (they're drawn by static helpers).
        var palette = Palette;
        headingColor = palette.Heading;
        softColor = palette.Dim;
    }
    private static Vector4? headingColor;
    private static Vector4 softColor = new(0.62f, 0.64f, 0.68f, 1f);
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
            case Tab.Routes: DrawRoutes(); break;
            case Tab.Supplies: DrawSupplies(); break;
            case Tab.Parts: DrawParts(); break;
            case Tab.Roster: DrawRoster(); break;
            case Tab.Retainers: DrawRetainers(); break;
            case Tab.Fcs: DrawFcs(); break;
            case Tab.Inventory: DrawInventory(); break;
            case Tab.Salvage: DrawSalvage(); break;
            case Tab.Income: DrawIncome(); break;
            case Tab.Market: DrawMarket(); break;
            case Tab.Trends: DrawTrends(); break;
            case Tab.Pcs: DrawPcs(); break;
            case Tab.Check: plugin.CheckPage.DrawPage(); break;
            case Tab.SpeedTest: plugin.SpeedTestPage.DrawPage(); break;
            case Tab.Status: DrawStatus(); break;
            case Tab.Settings:
                settings.Draw(() =>
                {
                    DrawSharedSettings();
                    ImGui.Spacing();
                    DrawFightClubImport();
                });
                break;
        }
    }

    // --- The sidebar: the submarine, the pages in groups with what needs attention beside them, and this PC's sync. ---

    // The sidebar's groups (the app's sidebar has the same).
    private static readonly string[] Groups = { "Overview", "Submarines", "Characters", "Gil", "Tools", "System" };

    /// <summary>Whether a page is shown for what the player runs (the app's shared/play-style.js hiddenViews).</summary>
    public static bool PageUsed(Tab tab, bool subs, bool retainers) => tab switch
    {
        Tab.Salvage or Tab.Subs or Tab.Supplies or Tab.Parts or Tab.Routes => subs,
        Tab.Retainers => retainers,
        _ => true,
    };

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
        var readyList = reader.Parts.Data is { } partsRead ? PartsPlanner.SubsReady(partsRead.Needs) : new List<(PartNeed Sub, List<PartNeed> Parts)>();
        var readyForParts = readyList.Count;
        attention += readyForParts;

        // What do you run? (Settings): pages for what isn't run are left out, as in the app.
        var uses = reader.Settings.Data;
        var subsOn = uses?.UsesSubs ?? true;
        var retainersOn = uses?.UsesRetainers ?? true;
        if (!PageUsed(shown, subsOn, retainersOn)) shown = Tab.Summary;

        foreach (var group in Groups)
        {
            // Retainers only: no Submarines group at all.
            if (group == "Submarines" && !subsOn) continue;
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
                case "Overview":
                    Entry(Tab.Summary, FontAwesomeIcon.ClipboardList, "Summary", folded, attention > 0 ? attention.ToString() : null, attention > 0 ? p.Warn : p.Dim,
                        attention > 0 ? $"{attention} thing{(attention == 1 ? "" : "s")} need{(attention == 1 ? "s" : "")} attention" : "Nothing needs you");
                    Entry(Tab.Trends, FontAwesomeIcon.ChartLine, "Trends", folded, null, p.Dim, "Gil, supplies, FC points and salvage over time");
                    break;
                case "Submarines":
                    if (subsOn) Entry(Tab.Subs, FontAwesomeIcon.Ship, "Subs", folded, back > 0 ? $"{back} back" : null, p.Good, back > 0 ? $"{back} back and waiting" : null);
                    var toSwitch = subsOn && reader.Routes.Data != null ? Suboptimal().Count : 0;
                    if (subsOn) Entry(Tab.Routes, FontAwesomeIcon.Route, "Route Optimization", folded, toSwitch > 0 ? $"{toSwitch} to switch" : null, p.Warn,
                        toSwitch > 0 ? $"{toSwitch} sub{(toSwitch == 1 ? " isn't" : "s aren't")} on the best route for {(toSwitch == 1 ? "its" : "their")} rank" : "Subs not on Fight Club's best route for their rank");
                    if (subsOn) Entry(Tab.Parts, FontAwesomeIcon.Wrench, "Parts", folded, readyForParts > 0 ? $"{readyForParts} ready" : null, p.Good,
                        readyForParts > 0 ? PartsPlanner.ReadyText(readyList) : "What each sub's next build needs");
                    if (subsOn) Entry(Tab.Supplies, FontAwesomeIcon.GasPump, "Supplies", folded, low > 0 ? $"{low} low" : null, p.Warn, low > 0 ? $"{low} character{(low == 1 ? "" : "s")} low" : null);
                    if (subsOn) Entry(Tab.Salvage, FontAwesomeIcon.Recycle, "Salvage", folded, null, p.Dim, "The salvage each character holds");
                    break;
                case "Characters":
                    Entry(Tab.Roster, FontAwesomeIcon.Users, "Roster", folded, notSynced > 0 ? $"{notSynced} not synced" : null, p.Warn,
                        notSynced > 0 ? $"{notSynced} character{(notSynced == 1 ? "" : "s")} no longer synced" : null);
                    if (retainersOn) Entry(Tab.Retainers, FontAwesomeIcon.UserTie, "Retainers", folded, venturesDone > 0 ? $"{venturesDone} done" : null, p.Good,
                        venturesDone > 0 ? $"{venturesDone} venture{(venturesDone == 1 ? "" : "s")} done" : null);
                    Entry(Tab.Fcs, FontAwesomeIcon.Home, "Free Companies", folded, housesAtRisk > 0 ? $"{housesAtRisk} house{(housesAtRisk == 1 ? "" : "s")}" : null, p.Warn,
                        housesAtRisk > 0 ? $"{housesAtRisk} house{(housesAtRisk == 1 ? "" : "s")} not entered for a while" : null);
                    Entry(Tab.Inventory, FontAwesomeIcon.BoxOpen, "Inventory", folded, null, p.Dim, "What the fleet holds, and Where is it?");
                    break;
                case "Gil":
                    Entry(Tab.Income, FontAwesomeIcon.Coins, "Income", folded, null, p.Dim, "Real income from the subs, and venture rewards");
                    var undercutNow = reader.Market.Data is { Live: { Error: null } } marketNow ? marketCache.Undercut(marketNow) : 0;
                    Entry(Tab.Market, FontAwesomeIcon.Store, "Gil & Market", folded, undercutNow > 0 ? $"{undercutNow} undercut" : null, p.Warn,
                        undercutNow > 0 ? $"{undercutNow} listing{(undercutNow == 1 ? " is" : "s are")} undercut on its world" : "Gil per account, and listings unsold for a while");
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
        // A look with a glow (Princess): the page shown glows, and its edge is lit.
        if (shown == tab && Theme.Glow(plugin.Configuration.Theme) is { } glow)
        {
            var draw = ImGui.GetWindowDrawList();
            var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            for (var i = 1; i <= 4; i++)
                draw.AddRect(min - new Vector2(i, i), max + new Vector2(i, i), ImGui.GetColorU32(glow with { W = 0.22f / i }), 4f * ImGuiHelpers.GlobalScale, ImDrawFlags.None, 2f);
            draw.AddRectFilled(min, new Vector2(min.X + 3 * ImGuiHelpers.GlobalScale, max.Y), ImGui.GetColorU32(glow));
        }
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
        Tinted(headingColor, text);
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

    // A line made of pieces, each in its own colour (null: plain): gil amounts in gil's gold among other text.
    private static void Runs(params (string Text, Vector4? Color)[] runs)
    {
        for (var i = 0; i < runs.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 0);
            Tinted(runs[i].Color, runs[i].Text);
        }
    }
    private static (string, Vector4?) T(string text, Vector4? color = null) => (text, color);
    // "1,234 gil" (or with another ending) in gil's gold.
    private (string, Vector4?) GilRun(double gil, string ending = " gil") => ($"{Number((long)Math.Round(gil))}{ending}", Palette.Gil);
    // A heading made of pieces (a gil amount in gold).
    private static void HeadingRuns(params (string Text, Vector4? Color)[] runs)
    {
        ImGui.Spacing();
        Runs(runs.Select(r => (r.Text, r.Color ?? headingColor)).ToArray());
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

    private Vector4? DaysColor(double? days) => days == null ? null : days < 3 ? Red : days < 7 ? Amber : null;

    private static string PerDay(double? perDay) => perDay == null ? "—" : perDay.Value < 10 ? perDay.Value.ToString("0.0") : perDay.Value.ToString("0");

    private void DrawSummary()
    {
        reader.ReadIfDue();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        RefreshButton();
        // When each part was read, on one line (each on hover), and anything that couldn't be read.
        ImGui.SameLine();
        SummaryReadLine();

        // What needs you, worst first: subs back and waiting (counted live), then the database's alerts.
        Heading("Needs attention");
        var back = FleetReader.SubsBack(reader.Subs.Data, now);
        var alerts = reader.Alerts.Data;
        if (back > 0) Colored(Green, $"{back} sub{(back == 1 ? " is" : "s are")} back and waiting to be collected (Subs page)");
        var ready = reader.Parts.Data is { } partsNow ? PartsPlanner.SubsReady(partsNow.Needs) : new List<(PartNeed Sub, List<PartNeed> Parts)>();
        if (ready.Count > 0) Colored(Green, $"{PartsPlanner.ReadyText(ready)} (Parts page)");
        if (alerts != null)
        {
            foreach (var alert in alerts)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, alert.Level == "danger" ? Red : Amber))
                    ImGui.TextWrapped($"{alert.Subject}: {alert.Detail}");
            }
            if (back == 0 && ready.Count == 0 && alerts.Count == 0) Colored(Green, "Nothing needs you right now.");
        }
        else if (reader.Alerts.Error == null) Colored(Dim, "Reading...");

        // Subs, Retainers and Supplies side by side: the main number large, the rest beneath it.
        // Only what the player runs (Settings → What do you run?), as the app's Summary.
        var subsOn = reader.Settings.Data?.UsesSubs ?? true;
        var retainersOn = reader.Settings.Data?.UsesRetainers ?? true;
        ImGui.Spacing();
        using (var boxes = ImRaii.Table("##summaryBoxes", (subsOn ? 2 : 0) + (retainersOn ? 1 : 0), ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.PadOuterX))
        {
            if (boxes)
            {
                if (subsOn)
                {
                    ImGui.TableNextColumn();
                    SummarySubsBox(back, now);
                }
                if (retainersOn)
                {
                    ImGui.TableNextColumn();
                    SummaryRetainersBox();
                }
                if (subsOn)
                {
                    ImGui.TableNextColumn();
                    SummarySuppliesBox();
                }
            }
        }
        if (!subsOn)
        {
            DrawTrendTiles();
            return;
        }

        // As the app's Summary: the subs ready for new parts, one part to a line.
        var partsData = reader.Parts.Data;
        var craft = partsData?.Needs.Count(n => n.ToCraft) ?? 0;
        HeadingRuns(T("Subs ready for new parts"), T(craft > 0 ? $"  ·  {craft} part{(craft == 1 ? "" : "s")} to craft" : "", Amber));
        if (partsData == null) Colored(Dim, reader.Parts.Error == null ? "Reading..." : "");
        else if (ready.Count == 0) ImGui.TextUnformatted("No sub has reached the rank of its next build, and no new sub to build.");
        else
        {
            using var table = ImRaii.Table("##summaryParts", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
            if (table)
            {
                foreach (var h in new[] { "Character", "Sub", "Rank", "New parts" }) ImGui.TableSetupColumn(h);
                ImGui.TableHeadersRow();
                foreach (var (sub, subParts) in ready)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    CharacterCell(sub.Character, sub.World);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(sub.SubName);
                    ImGui.TableNextColumn();
                    if (sub.IsNew) Colored(Dim, "new"); else RightNumber(sub.Rank);
                    ImGui.TableNextColumn();
                    // The class, then the slot ("Shark Hull"), and where it comes from, softer.
                    foreach (var x in subParts)
                    {
                        ImGui.TextUnformatted($"{x.Needed} {x.Slot}");
                        ImGui.SameLine();
                        var from = x.ToCraft ? "to craft" : $"spare in {x.SpareLocation}";
                        Colored(x.ToCraft ? Amber : Dim, $"({from})");
                    }
                }
            }
        }

        // As the app's Summary: a small trend line for each fleet-wide value (Trends page).
        DrawTrendTiles();
    }

    // A box's title (softer), then its main figure large.
    private static void BoxTitle(string title, string figure, Vector4? color)
    {
        Colored(softColor, title);
        ImGui.SetWindowFontScale(1.35f);
        Tinted(color, figure);
        ImGui.SetWindowFontScale(1f);
    }

    private void SummarySubsBox(int back, long now)
    {
        var subs = reader.Subs.Data;
        if (subs == null) { BoxTitle("Subs", "—", null); if (reader.Subs.Error != null) Colored(Amber, "Couldn't read the subs."); return; }
        BoxTitle("Subs", back > 0 ? $"{back} back" : "None back", back > 0 ? Green : null);
        var outNow = subs.Where(x => x.ReturnTime > now).ToList();
        var inPort = subs.Count(x => x.ReturnTime <= 0);
        ImGui.TextUnformatted(outNow.Count == 0 ? "None out" : $"{outNow.Count} out, the next back in {FleetReader.Countdown(outNow.Min(x => x.ReturnTime) - now)}");
        if (inPort > 0) Colored(Amber, $"{inPort} in port (not sent out)");
        // As the app's Route Optimizations Possible tile.
        if (reader.Routes.Data != null)
        {
            var switchable = Suboptimal();
            if (switchable.Count == 0) Colored(Dim, "Every sub from rank 85 is on its Target Route");
            else
            {
                var more = switchable.Sum(x => x.Gain > 0 ? x.Gain!.Value : 0);
                Runs(T($"{switchable.Count} route{(switchable.Count == 1 ? "" : "s")} to switch", Amber), T(more > 0 ? ", about " : ""), more > 0 ? GilRun(more) : T(""), T(more > 0 ? " more a day" : ""));
            }
        }
        // As the app's Summary: the fleet-wide salvage total (the Salvage page has each character's).
        if (reader.Salvage.Data is { } salvage) Runs(T("Salvage held: "), GilRun(salvage.Sum(x => x.Gil)));
    }

    private void SummaryRetainersBox()
    {
        var counts = reader.RetainerCounts.Data;
        if (counts == null) { BoxTitle("Retainers", "—", null); return; }
        BoxTitle("Retainers", counts.VenturesDone > 0 ? $"{counts.VenturesDone} venture{(counts.VenturesDone == 1 ? "" : "s")} done" : "No ventures done",
            counts.VenturesDone > 0 ? Green : null);
        ImGui.TextUnformatted($"{counts.Retainers} retainer{(counts.Retainers == 1 ? "" : "s")} with a class");
        if (counts.Idle > 0) Colored(Amber, $"{counts.Idle} without a venture");
        // As the app's Venture Income tile: the rewards over the Income window, at what a vendor pays.
        var ventures = reader.Ventures.Data;
        if (ventures != null && ventures.Rows.Count == 0) Colored(Dim, "No venture rewards recorded yet (AutoRetainer: \"Record statistics\")");
        else if (ventures != null)
        {
            var perDay = marketCache.VentureSummaries(ventures.Rows, ventures.WindowDays, VendorPrice).Sum(v => v.PerDay);
            Runs(T("Ventures: "), GilRun(perDay), T(" a day"));
            Hover($"The last {ventures.WindowDays} days of venture rewards, at what a vendor pays (Income page).");
            // With live prices, the same rewards at the cheapest listing on each character's world, data center and region.
            if (VentureMarketPerDay(ventures.Rows, ventures.WindowDays) is { } market)
                Runs(new[] { T("Market: ", Dim) }.Concat(Universalis.Scopes.SelectMany((sc, i) => new[] { T(i > 0 ? ", " : "", Dim), GilRun(market.Values.Sum(v => v[sc]), ""),
                    T($" ({(sc == "world" ? "World" : sc == "dc" ? "DC" : "Region")})", Dim) })).ToArray());
        }
        // As the app's Listings Unsold for a While tile.
        if (reader.Market.Data is { } marketRead)
        {
            var unsold = marketRead.Listings.Where(l => l.Stale).ToList();
            var undercut = marketCache.Undercut(marketRead);
            Vector4? tone = unsold.Count > 0 || undercut > 0 ? Amber : null;
            Runs(T($"{Number(unsold.Count)} unsold for {marketRead.Settings.StaleDays}+ days (", tone), GilRun(unsold.Sum(l => l.Value)), T(")", tone),
                T(undercut > 0 ? $" · {undercut} undercut" : "", Amber));
        }
    }

    private void SummarySuppliesBox()
    {
        var summary = reader.Summary.Data;
        if (summary == null) { BoxTitle("Supplies", "—", null); return; }
        var low = summary.LowCeruleum + summary.LowRepairKits;
        BoxTitle("Supplies", low == 0 ? "Nobody low" : $"{low} low", low == 0 ? Green : Amber);
        if (summary.LowCeruleum > 0) Colored(Amber, $"{summary.LowCeruleum} character{(summary.LowCeruleum == 1 ? "" : "s")} low on ceruleum");
        if (summary.LowRepairKits > 0) Colored(Amber, $"{summary.LowRepairKits} character{(summary.LowRepairKits == 1 ? "" : "s")} low on repair kits");
        if (summary.SoonestRunsOutDays is double soonest)
            Tinted(DaysColor(soonest), $"The first runs out in {Days(soonest, 1)} days");
        Colored(Dim, "Warnings are set in the app (Settings → General → Warnings).");
    }

    // "Updated 19s ago" for the Summary's reads together (the oldest), each on hover; anything that couldn't be read, in amber.
    private void SummaryReadLine()
    {
        var reads = new (string Name, DateTime? At, string? Error)[]
        {
            ("Subs", reader.Subs.At, reader.Subs.Error), ("Needs attention", reader.Alerts.At, reader.Alerts.Error),
            ("Retainers", reader.RetainerCounts.At, reader.RetainerCounts.Error), ("Venture income", reader.Ventures.At, reader.Ventures.Error),
            ("Supplies", reader.Summary.At, reader.Summary.Error), ("Parts", reader.Parts.At, reader.Parts.Error),
            ("Salvage", reader.Salvage.At, reader.Salvage.Error), ("Market", reader.Market.At, reader.Market.Error),
        };
        var at = reads.Where(r => r.At.HasValue).Select(r => r.At!.Value).DefaultIfEmpty(DateTime.Now).Min();
        Colored(Dim, reads.Any(r => r.At.HasValue) ? $"Updated {FleetReader.Ago(at, DateTime.Now)}" : "Reading...");
        Hover(string.Join("\n", reads.Select(r => $"{r.Name}: {(r.At.HasValue ? FleetReader.Ago(r.At.Value, DateTime.Now) : "not read yet")}")) +
              "\n\nSubs and what needs attention every 30 seconds; the rest every 5 minutes.");
        foreach (var (name, _, error) in reads.Where(r => r.Error != null))
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                ImGui.TextWrapped($"{name}: {error}");
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
        // A thin bar for how far to the next rank, the rank right after it (both on the left, so stretching the column
        // doesn't pull them apart; the bar's fixed width lines the numbers up).
        new("rank", "Rank", sub =>
        {
            if (sub.NextLevelExp > 0)
            {
                TableCells.Bar((double)sub.CurrentExp / sub.NextLevelExp, 36 * ImGuiHelpers.GlobalScale);
                Hover($"{100.0 * sub.CurrentExp / sub.NextLevelExp:0}% of the way to rank {sub.Rank + 1}");
            }
            else ImGui.Dummy(new Vector2(36 * ImGuiHelpers.GlobalScale, ImGui.GetTextLineHeight()));
            ImGui.SameLine();
            ImGui.TextUnformatted(sub.Rank.ToString(System.Globalization.CultureInfo.CurrentCulture));
            if (sub.NextLevelExp > 0) Hover($"{100.0 * sub.CurrentExp / sub.NextLevelExp:0}% of the way to rank {sub.Rank + 1}");
        }, sub => sub.Rank),
        new("build", "Build", sub => BuildCell(sub.Build), sub => sub.Build),
        // Craft?: follows Fight Club's builds from rank 85 (ticked) or its Sub Rules; changed here or in the app.
        new("craft", "Craft?", sub => CraftCell(sub.Cid, sub.SubName), sub => CraftOf(sub.Cid, sub.SubName) ? 1 : 0, New: true),
        // Status: back and waiting first, then the soonest back; in port last either way.
        new("status", "Status", sub =>
        {
            var status = FleetReader.SubStatus(sub.ReturnTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (status == "Back") Colored(Green, status);
            else if (status == "In port") Colored(Dim, status);
            else ImGui.TextUnformatted(status);
        }, sub => sub.ReturnTime, sub => sub.ReturnTime <= 0),
        // Routes stage: when it ranks up and changes build, its route and gil a day (Fight Club's, after repair kits).
        new("nextRank", "Next Rank", sub => NextRankCell(RouteOf(sub.Cid, sub.SubName)), sub => RouteOf(sub.Cid, sub.SubName)?.RankUp.NextRankAt ?? double.MaxValue,
            sub => RouteOf(sub.Cid, sub.SubName)?.RankUp.NextRankAt == null),
        new("nextBuild", "Next Build", sub => NextBuildCell(RouteOf(sub.Cid, sub.SubName)), sub => RouteOf(sub.Cid, sub.SubName)?.RankUp.NextBuild?.At ?? double.MaxValue,
            sub => RouteOf(sub.Cid, sub.SubName)?.RankUp.NextBuild?.At == null),
        new("speed", "Speed", sub => { if (RouteOf(sub.Cid, sub.SubName)?.Speed is double v) RightNumber((long)v); else Colored(Dim, "—"); },
            sub => RouteOf(sub.Cid, sub.SubName)?.Speed ?? -1, Hidden: true),
        new("range", "Range", sub => { if (RouteOf(sub.Cid, sub.SubName)?.Range is double v) RightNumber((long)v); else Colored(Dim, "—"); },
            sub => RouteOf(sub.Cid, sub.SubName)?.Range ?? -1, Hidden: true),
        new("route", "Current Route", sub => { if (RouteOf(sub.Cid, sub.SubName) is { } r) RouteCell(r.CurrentRoute, r.CurrentMap); else Colored(Dim, "—"); },
            sub => RouteOf(sub.Cid, sub.SubName)?.CurrentRoute ?? ""),
        new("gilDay", "Current Gil/Day", sub => GilDayCell(RouteOf(sub.Cid, sub.SubName)?.CurrentGilDay, NoCurrentGil),
            sub => RouteOf(sub.Cid, sub.SubName)?.CurrentGilDay ?? double.MinValue, sub => RouteOf(sub.Cid, sub.SubName)?.CurrentGilDay == null),
        // The best route for its rank, in amber when it isn't on it.
        new("targetRoute", "Target Route", sub =>
        {
            var r = RouteOf(sub.Cid, sub.SubName);
            if (r == null || r.TargetRoute.Length == 0) { Colored(Dim, "—"); return; }
            if (r.Suboptimal) { Colored(Amber, r.TargetRoute); Hover("Not on this route now: see Route Optimization."); }
            else Colored(Green, r.TargetRoute);
        }, sub => RouteOf(sub.Cid, sub.SubName)?.TargetRoute ?? ""),
        new("targetGilDay", "Target Gil/Day", sub => GilDayCell(RouteOf(sub.Cid, sub.SubName)?.TargetGilDay, NoTargetGil),
            sub => RouteOf(sub.Cid, sub.SubName)?.TargetGilDay ?? double.MinValue, sub => RouteOf(sub.Cid, sub.SubName)?.TargetGilDay == null),
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
        EditLine();
        ForecastLine();
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
