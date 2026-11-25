using Dalamud.Bindings.ImGui;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Windows;

// Settings → Connections in use (ConnectionsInUse.cs, as the app's Settings → Database): the shared database's connection
// slots taken, by the app, the plugins and anything else, and from which PC. And Settings → Sub Calculator Data: which
// copy of its data is in use, and Check now (SubDataUpdate).
public partial class MainWindow
{
    private ConnectionsInUse.Summary? connections;
    private string? connectionsError;
    private volatile bool connectionsReading;
    private bool connectionsAsked;

    private void ReadConnections()
    {
        if (connectionsReading) return;
        connectionsReading = true;
        connectionsAsked = true;
        Task.Run(async () =>
        {
            try
            {
                await using var conn = await plugin.OpenFleetAsync();
                connections = conn == null ? null : await ConnectionsInUse.Read(conn);
                connectionsError = null;
            }
            catch (Exception ex) { connectionsError = ex.Message; }
            finally { connectionsReading = false; }
        });
    }

    private void DrawConnectionsInUse()
    {
        if (plugin.OnThisPc || plugin.Configuration.FleetStore != FleetStoreChoice.SharedDatabase) return;
        if (!ImGui.CollapsingHeader("Connections in use")) { connectionsAsked = false; return; }
        if (!connectionsAsked) ReadConnections();
        Colored(Dim, "Each PC's app and plugin takes some of the database's connection slots. When they run out, the plugins can't sync. " +
                     "Other: anything else connected, such as pgAdmin.");
        if (connectionsError != null) Colored(Amber, $"Couldn't read the connections: {connectionsError}");
        else if (connections == null) Ui.Text(connectionsReading ? "Checking..." : "Not connected to the database.");
        else
        {
            var c = connections;
            var parts = string.Join(", ", new[]
            {
                c.App > 0 ? $"{c.App} app" : null,
                c.Plugins > 0 ? $"{c.Plugins} plugin{(c.Plugins == 1 ? "" : "s")}" : null,
                c.Other > 0 ? $"{c.Other} other" : null,
            }.Where(p => p != null));
            var line = $"{c.InUse} of {c.Limit} in use ({parts})" +
                       (c.Warn ? ". Nearly full: close what you aren't using, or restart the app on a PC holding many." : ".");
            if (c.Warn) Colored(Amber, line); else Ui.Text(line);
            if (ImGui.BeginTable("##connectionsByPc", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
            {
                foreach (var h in new[] { "PC", "App", "Plugins", "Other", "Total" }) ImGui.TableSetupColumn(h);
                ImGui.TableHeadersRow();
                foreach (var pc in c.Pcs)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); Ui.Text(pc.Label);
                    foreach (var n in new[] { pc.App, pc.Plugins, pc.Other, pc.Total })
                    {
                        ImGui.TableNextColumn();
                        Ui.Text(n.ToString(CultureInfo.CurrentCulture));
                    }
                }
                ImGui.EndTable();
            }
        }
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(connectionsReading))
            if (ImGui.Button(connectionsReading ? "Checking...##connections" : "Check again##connections")) ReadConnections();
    }

    // A quiet line when live prices fell back this time (Income's loot and ventures, the Summary's ventures): none came
    // (vendor prices shown) or some are from an earlier check. The reason is on hover. As the app's LivePricesNote.
    private void LivePricesNote()
    {
        if (reader.Market.Data is not { Settings.LivePrices: true, Live: { } live }) return;
        if (live.Error is { } err)
        {
            Colored(Dim, "Live prices didn't arrive this time, vendor prices shown.");
            Hover(err);
        }
        else if (live.Stale is { } old)
        {
            Colored(Dim, $"Some live prices are from {old.Since.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}.");
            Hover(old.Problem);
        }
    }

    private void DrawSubCalculatorData()
    {
        if (!ImGui.CollapsingHeader("Sub Calculator Data")) return;
        Colored(Dim, "The loot and breakpoints the Sub Calculator works from (XIVStats and Pepper's ship guide). " +
                     "A newer copy is checked for once a day and used when there is one.");
        var made = SubCalculatorData.Current().About.Made;
        Ui.Text($"From {(made is { } m ? m.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "an unknown date")} " +
                $"({(SubCalculatorData.Downloaded() ? "downloaded" : "shipped with this version")}).");
        var update = plugin.SubData;
        if (update.LastCheck is { } at)
        {
            var found = update.LastResult switch
            {
                { Error: { } err } => $": couldn't get the newer copy ({err})",
                { Adopted: true } => ": a newer copy was found",
                { } => ": nothing newer",
                null => "",
            };
            Colored(Dim, $"Last checked {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}{found}.");
        }
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(update.Checking))
            if (ImGui.Button(update.Checking ? "Checking...##subData" : "Check now##subData")) update.CheckSoon(DateTime.UtcNow);
    }
}
