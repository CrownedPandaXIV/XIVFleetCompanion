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
/// "Check what I can see" (/xivfleet check): goes through every character the plugin would sync and
/// shows what AutoRetainer, AllaganTools and FCTracker have for it, and when each part was last stored.
/// Nothing is written: the database is only read. The result can be copied to share.
/// </summary>
public class CheckWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private bool running;
    private string? text;
    private string status = "";

    public CheckWindow(Plugin plugin) : base($"XIV Fleet Companion: Check what I can see v{Plugin.VersionText}###XIVFleetCompanionCheck")
    {
        Size = new Vector2(560, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public void OpenAndRun()
    {
        IsOpen = true;
        Run();
    }

    private void Run()
    {
        if (running) return;
        running = true;
        status = "Checking...";
        Task.Run(async () =>
        {
            try
            {
                var facts = await Gather();
                text = CheckReport.ToText(facts);
                var count = CheckReport.ToFixCount(facts);
                status = count == 0 ? "Nothing to fix." : $"{count} thing{(count == 1 ? "" : "s")} to fix (marked !).";
            }
            catch (Exception ex)
            {
                status = $"The check failed: {ex.Message}";
                Plugin.Log.Warning($"Fleet Companion: check failed — {ex}");
            }
            finally
            {
                running = false;
            }
        });
    }

    // Reads AutoRetainer and AllaganTools on the game's thread (as the sync does), then FCTracker's
    // file and the database in the background.
    private async Task<CheckReport.Facts> Gather()
    {
        var configuration = plugin.Configuration;
        var facts = await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var f = new CheckReport.Facts
            {
                Version = Plugin.VersionText,
                At = DateTime.Now,
                AccountLabel = configuration.AccountLabel,
                FcTrackerPath = configuration.FCTrackerConfigPath,
            };
            var autoRetainer = plugin.AutoRetainer;
            f.AutoRetainerReady = autoRetainer != null && autoRetainer.Ready;
            f.AllaganToolsReady = plugin.AllaganTools?.IsReady() ?? false;
            if (!f.AutoRetainerReady) return f;

            var cids = autoRetainer!.GetRegisteredCharacters();
            f.Registered = cids.Count;
            foreach (var cid in cids)
            {
                if (!configuration.ShouldSync(cid))
                {
                    f.LeftOut++;
                    continue;
                }
                var data = autoRetainer.GetOfflineCharacterData(cid);
                if (data == null || data.CID == 0) continue;

                var c = new CheckReport.Character
                {
                    Cid = data.CID,
                    Name = data.Name,
                    World = data.World,
                    Subs = data.OfflineSubmarineData.Count,
                    FcId = data.FCID,
                };
                c.Bags = new CheckReport.Part { Id = data.CID, Name = "Bags", Items = ItemCount(data.CID) };
                foreach (var retainer in data.RetainerData)
                    c.Retainers.Add(new CheckReport.Part { Id = retainer.RetainerID, Name = retainer.Name, Items = ItemCount(retainer.RetainerID) });
                if (data.FCID != 0)
                    c.Chest = new CheckReport.Part { Id = data.FCID, Name = "FC chest", Items = ChestCount(data.FCID) };
                f.Characters.Add(c);
            }
            return f;
        });

        // FCTracker: which characters it has Free Company and house details for.
        var housing = FCTrackerConnector.ReadHousingData(configuration.FCTrackerConfigPath);
        facts.FcTrackerFound = System.IO.File.Exists(configuration.FCTrackerConfigPath);
        foreach (var c in facts.Characters)
        {
            if (housing.TryGetValue(c.Cid, out var h)) c.Housing = h;
            if (c.Housing != null && !string.IsNullOrEmpty(c.Housing.FcName)) c.FcName = c.Housing.FcName;
        }

        // The database, read only: when each part was last stored.
        try
        {
            await using var conn = await PostgresWriter.OpenConnectionAsync(configuration.UseRemoteConnection);
            if (conn == null)
            {
                facts.DatabaseProblem = "no saved connection (Settings → Postgres).";
            }
            else
            {
                var ages = await FleetWriter.ReadStoredAgesAsync(conn,
                    facts.Characters.Select(c => c.Cid).ToList(),
                    facts.Characters.Where(c => c.FcId != 0).Select(c => c.FcId).Distinct().ToList());
                foreach (var c in facts.Characters)
                {
                    c.SyncedAgo = ages.Synced.TryGetValue(c.Cid, out var synced) ? synced : null;
                    foreach (var part in c.Retainers.Append(c.Bags))
                        part.StoredAgo = ages.Inventory.TryGetValue((c.Cid, part.Id), out var stored) ? stored : null;
                    if (c.Chest != null)
                        c.Chest.StoredAgo = ages.Chest.TryGetValue(c.FcId, out var chest) ? chest : null;
                }
            }
        }
        catch (Exception ex)
        {
            facts.DatabaseProblem = ex.Message;
        }
        return facts;
    }

    // How many items AllaganTools has for a character or retainer (empty slots left out), or null
    // when it has never seen it (it then answers with nothing at all, not even empty slots).
    private int? ItemCount(ulong id)
    {
        var raw = plugin.AllaganTools?.GetCharacterItems(id);
        return raw == null || raw.Count == 0 ? null : raw.Count(i => i.Quantity > 0);
    }

    private int? ChestCount(ulong fcId)
    {
        var raw = plugin.AllaganTools?.GetCharacterItems(fcId);
        return raw == null || raw.Count == 0 ? null : raw.Count(i => i.Quantity > 0 && i.SortedContainer >= 20000 && i.SortedContainer <= 20004);
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Goes through every character this plugin syncs and shows what it can see: AutoRetainer, the bags, " +
                          "each retainer and the FC chest (from AllaganTools), the house (from FCTracker) and when each was last " +
                          "stored. Nothing is written.");
        using (ImRaii.Disabled(running))
        {
            if (ImGui.Button(running ? "Checking..." : "Check again")) Run();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(text == null))
        {
            if (ImGui.Button("Copy results") && text != null)
            {
                ImGui.SetClipboardText(text);
                status = "Copied. Paste it wherever you need it.";
            }
        }
        ImGui.SameLine();
        ImGui.Text(status);

        using var child = ImRaii.Child("##checkresults", Vector2.Zero, true);
        if (!child.Success || text == null) return;
        var amber = new Vector4(0.95f, 0.70f, 0.35f, 1f);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.TrimStart().StartsWith("!"))
            {
                using (ImRaii.PushColor(ImGuiCol.Text, amber))
                    ImGui.TextWrapped(trimmed);
            }
            else
            {
                ImGui.TextWrapped(trimmed);
            }
        }
    }
}
