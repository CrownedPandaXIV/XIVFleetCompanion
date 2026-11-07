using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace XIVFleetCompanion.Windows;

/// <summary>
/// 2.0 stage 1 (/xivfleet speedtest): times reading what the in-game windows will need from the database
/// (SpeedTest.cs), on a background thread, and watches the game's frames meanwhile to show it causes no stutter.
/// Only reads; nothing is written.
/// </summary>
public class SpeedTestWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private volatile bool running;
    private string? text;
    private string status = "";

    // The game's frame times (seconds), while this window is open: the longest over the last few seconds before
    // a test, and the longest while it runs.
    private readonly Queue<(DateTime At, float Seconds)> recentFrames = new();
    private static readonly TimeSpan FrameWindow = TimeSpan.FromSeconds(3);
    private float longestWhileRunning;
    private float? longestBefore;

    public SpeedTestWindow(Plugin plugin) : base($"XIV Fleet Companion: Speed test v{Plugin.VersionText}###XIVFleetCompanionSpeedTest")
    {
        Size = new Vector2(560, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public void OpenAndRun()
    {
        IsOpen = true;
        Run();
    }

    public void Run()
    {
        if (running) return;
        var remote = plugin.Configuration.UseRemoteConnection;
        var (connectionString, error) = PostgresWriter.BuildConnectionString(remote);
        if (connectionString == null)
        {
            status = error ?? "The database isn't set up yet (open the settings).";
            return;
        }
        var where = $"v{Plugin.VersionText}, {(remote ? "Remote" : "Local")} database";
        // The longest frame seen recently, before the test starts (null when the window has only just opened).
        longestBefore = recentFrames.Count > 0 ? recentFrames.Max(f => f.Seconds) * 1000f : null;
        longestWhileRunning = 0;
        running = true;
        status = "Testing...";
        Task.Run(async () =>
        {
            try
            {
                var results = await SpeedTest.RunAsync(connectionString);
                text = SpeedTest.ToText(results, where, longestWhileRunning * 1000.0, longestBefore);
                var failed = results.Count(r => r.Error != null);
                status = failed == 0 ? "Done." : $"Done; {failed} part{(failed == 1 ? "" : "s")} failed (see below).";
            }
            catch (Exception ex)
            {
                text = null;
                status = $"Could not connect: {ex.Message}";
                Plugin.Log.Warning($"Fleet Companion: speed test failed — {ex}");
            }
            finally
            {
                running = false;
            }
        });
    }

    private void NoteFrame()
    {
        var now = DateTime.UtcNow;
        var seconds = ImGui.GetIO().DeltaTime;
        recentFrames.Enqueue((now, seconds));
        while (recentFrames.Count > 0 && now - recentFrames.Peek().At > FrameWindow)
            recentFrames.Dequeue();
        if (running && seconds > longestWhileRunning)
            longestWhileRunning = seconds;
    }

    // 2.x: drawn as a page of the main window (Tools); also still a window of its own.
    public override void Draw() => DrawPage();

    public void DrawPage()
    {
        NoteFrame();
        ImGui.TextWrapped("For XIV Fleet Companion 2.0: times reading from the database what the in-game Summary, Subs and " +
                          "Supplies windows will need. It runs in the background, so the game shouldn't stutter; the longest " +
                          "game frame is shown to check. Only reads; nothing is changed.");
        using (ImRaii.Disabled(running))
        {
            if (ImGui.Button(running ? "Testing..." : "Test again")) Run();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(text == null || running))
        {
            if (ImGui.Button("Copy results") && text != null)
            {
                ImGui.SetClipboardText(text);
                status = "Copied. Paste it wherever you need it.";
            }
        }
        ImGui.SameLine();
        ImGui.Text(status);

        using var child = ImRaii.Child("##speedresults", Vector2.Zero, true);
        if (!child.Success || text == null || running) return;
        foreach (var line in text.Split('\n'))
            ImGui.TextWrapped(line.TrimEnd('\r'));
    }
}
