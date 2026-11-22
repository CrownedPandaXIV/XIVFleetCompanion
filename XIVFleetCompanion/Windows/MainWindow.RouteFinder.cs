using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Windows;

// Sub Calculator → Route Finder, as the app's page, in two tabs. Best routes: a build on one map and the routes it can
// fly, ranked by experience, gil or an item, only through sectors its Free Company has discovered (RouteFinder). Unlocks:
// what the FC can explore next for sure, what could open by chance, and the way to the next map. Colours: explored green,
// to explore amber, could open blue, not yet grey. Worked out away from the game's frame.
public partial class MainWindow
{
    private static readonly (double? Hours, string Label)[] RfHours = { (12, "12h"), (24, "24h"), (36, "36h"), (48, "48h"), (null, "Any") };
    private static readonly (string Key, string Label)[] RfRankBy = { ("exp", "Experience"), ("gil", "Gil"), ("item", "An item") };
    private const string RfOdds = "New sectors open by chance, about 20–30% a voyage to the sector before (Pepper's guess); stats don't change the odds.";
    private const int RfShown = 100;

    private bool rfUnlocksTab;
    private bool rfTabPicked; // set when a button switches the tab, so the tab bar follows once
    private (long Cid, string SubName)? rfChosen;
    private int rfRank = 100;
    private readonly string[] rfParts = { "W+", "C+", "W+", "C+" };
    private bool rfEditBuild;
    private int? rfMap;
    private string rfRankBy = "exp";
    private uint rfItem;
    private double? rfMaxHours = 24;
    private int rfMaxStops = 5;
    private readonly List<int> rfMust = new();
    private readonly List<int> rfLeaveOut = new();
    private bool rfExploreFirst;
    private string? rfFc;

    private sealed record RfResult(SubStats? Stats, Dictionary<int, string?> States, Dictionary<int, RouteFinder.Value> Values, List<RouteFinder.Route> Routes, int Total, int NewOnes);
    private string rfKey = "";
    private RfResult? rfResult;
    private Task<RfResult?>? rfWork;
    private string rfWorkKey = "";

    private Vector4 Blue => Palette.Address ?? new Vector4(0.48f, 0.66f, 0.92f, 1f);

    private Vector4 RfColor(string? state) => state switch
    {
        "explored" => Green,
        "discovered" => Amber,
        "chance" => Blue,
        "locked" => Dim,
        _ => softColor,
    };

    private static string RfStateText(string? state) => state switch
    {
        "explored" => "explored",
        "discovered" => "explore next",
        "chance" => "could open by chance",
        "locked" => "not yet",
        _ => "",
    };

    // What opens what (XIVStats), for every sector.
    private Dictionary<int, int> RfOpeners(PlannerGame g, SubCalculatorData.Data data) =>
        g.Sectors.Keys.ToDictionary(id => id, id => data.Find(id)?.UnlockedFrom ?? 0);

    private FcSectors? RfFcRead() => rfFc == null ? null : reader.SubSectors.Data?.FirstOrDefault(f => f.FcId == rfFc);

    private static string RfWhen(double ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime.ToString("d MMM, t", CultureInfo.CurrentCulture);

    private void DrawRouteFinder()
    {
        reader.ReadIfDue(withLists: true); // your subs, their Free Companies, and each FC's sectors (read with the routes)
        var g = PlannerGameData();
        if (g == null) { Ui.Wrapped("The Route Finder needs the game's sub data, which couldn't be read."); return; }
        var data = SubCalculatorData.Shipped();
        rfMap ??= g.Maps.Count > 0 ? g.Maps[0].Id : null;
        rfRank = Math.Clamp(rfRank, 1, g.MaxRank);
        var fcs = reader.SubSectors.Data ?? new List<FcSectors>();
        rfFc ??= fcs.FirstOrDefault(f => f.ReadAt != null)?.FcId ?? fcs.FirstOrDefault()?.FcId;

        using (var bar = ImRaii.TabBar("##rfTabs"))
        {
            if (bar)
            {
                using (var t = ImRaii.TabItem("Best routes##rfRoutes", rfTabPicked && !rfUnlocksTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                    if (t) { if (!rfTabPicked) rfUnlocksTab = false; }
                using (var t = ImRaii.TabItem("Unlocks##rfUnlocks", rfTabPicked && rfUnlocksTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                    if (t) { if (!rfTabPicked) rfUnlocksTab = true; }
            }
        }
        rfTabPicked = false;
        ImGui.Spacing();
        if (rfUnlocksTab) RfUnlocks(g, data, fcs);
        else RfBestRoutes(g, data, fcs);
        ImGui.Spacing();
        Colored(Dim, "Routes: every set of up to 5 sectors, each in the fastest order the sub's range allows. Loot and breakpoints: XIVStats, checked " +
                     "against Pepper's FFXIV Ship Guide; what opens what: XIVStats; unlock odds: Pepper's guide.");
    }

    // The Free Company picker and whether it has been read.
    private void RfFcLine(List<FcSectors> fcs, string label)
    {
        var scale = ImGuiHelpers.GlobalScale;
        FieldLabel(label);
        ImGui.SetNextItemWidth(240 * scale);
        var fc = RfFcRead();
        using (var combo = ImRaii.Combo("##rfFc" + label, fc == null ? (fcs.Count == 0 ? "No Free Company yet" : "Choose…") : fc.Name + (fc.ReadAt == null ? " (not read yet)" : "")))
        {
            if (combo)
                foreach (var f in fcs)
                    if (ImGui.Selectable($"{f.Name}{(f.ReadAt == null ? " (not read yet)" : "")}##rfFc{f.FcId}", f.FcId == rfFc)) rfFc = f.FcId;
        }
        ImGui.SameLine();
        if (fc?.ReadAt is double at) Colored(Dim, $"read at the workshop {RfWhen(at)}");
        else Colored(Dim, "not read yet: every sector is allowed");
        if (fc?.ReadAt == null)
            Hover("The plugin reads it in the workshop once the Voyage Control Panel's submarines are opened (AutoRetainer does at each collection).");
    }

    // ---- Best routes ----

    private void RfBestRoutes(PlannerGame g, SubCalculatorData.Data data, List<FcSectors> fcs)
    {
        var scale = ImGuiHelpers.GlobalScale;

        // 1. Your sub.
        HeadingRuns(("Your sub", null));
        SubPicker("rf", rfChosen, s =>
        {
            rfChosen = (s.Cid, s.SubName);
            var letters = s.Build.Split('-');
            if (letters.Length == 4 && letters.All(l => SubRoutes.PartClass.ContainsKey(l))) for (var i = 0; i < 4; i++) rfParts[i] = letters[i];
            rfRank = s.Rank;
            if (reader.Parts.Data?.Inputs.Characters.FirstOrDefault(c => c.Cid == s.Cid)?.FcId is { } fc) rfFc = fc;
            if (RouteOf(s.Cid, s.SubName) is { } r && r.Sectors.Length > 0 && g.Sectors.TryGetValue(r.Sectors[0], out var first) && first.Map != rfMap)
            { rfMap = first.Map; rfMust.Clear(); rfLeaveOut.Clear(); }
        });
        ImGui.SameLine(0, 14 * scale);
        Tag($"Rank {rfRank}", softColor);
        foreach (var p in rfParts) { ImGui.SameLine(0, 4 * scale); Tag(p, softColor); }
        ImGui.SameLine(0, 12 * scale);
        if (ImGui.SmallButton(rfEditBuild ? "Done##rfEdit" : "Change parts or rank##rfEdit")) rfEditBuild = !rfEditBuild;
        if (rfEditBuild)
        {
            ImGui.SetNextItemWidth(110 * scale);
            var rank = rfRank;
            if (ImGui.InputInt("Rank##rfRank", ref rank)) rfRank = Math.Clamp(rank, 1, g.MaxRank);
            for (var i = 0; i < 4; i++)
            {
                ImGui.SameLine(0, 12 * scale);
                ImGui.SetNextItemWidth(170 * scale);
                using var combo = ImRaii.Combo($"{PlannerSlots[i]}##rfPart{i}", PlannerPartName(rfParts[i]));
                if (combo) foreach (var l in PlannerLetters) if (ImGui.Selectable(PlannerPartName(l), rfParts[i] == l)) rfParts[i] = l;
            }
        }
        var stats = SubRoutes.BuildStats(rfRank, rfParts, g.Ranks, g.Parts);
        foreach (var (label, value) in new[] { ("SURVEILLANCE", stats?.Surveillance), ("RETRIEVAL", stats?.Retrieval), ("SPEED", stats?.Speed), ("RANGE", stats?.Range), ("FAVOR", stats?.Favor) })
        {
            if (label != "SURVEILLANCE") ImGui.SameLine(0, 8 * scale);
            Boxed(() => { Colored(Dim, label); Ui.Text(value is double v ? PlannerN(v) : "—"); }, null, false, 10, 5);
        }

        // 2. Where it goes.
        ImGui.Spacing();
        HeadingRuns(("Where it goes", null));
        FieldLabel("Map");
        ImGui.SetNextItemWidth(220 * scale);
        using (var combo = ImRaii.Combo("##rfMap", g.Maps.FirstOrDefault(m => m.Id == rfMap).Name ?? ""))
        {
            if (combo)
                foreach (var m in g.Maps)
                    if (ImGui.Selectable(m.Name, m.Id == rfMap)) { rfMap = m.Id; rfMust.Clear(); rfLeaveOut.Clear(); rfItem = 0; }
        }
        Flow(ImGui.CalcTextSize("Best for  Experience  Gil  An item").X + 50 * scale);
        FieldLabel("Best for");
        RfSegment("rank", RfRankBy.Select(x => x.Label).ToArray(), Array.FindIndex(RfRankBy, x => x.Key == rfRankBy), i => rfRankBy = RfRankBy[i].Key);
        var onMap = g.Sectors.Values.Where(s => s.Map == rfMap).OrderBy(s => s.Id).ToList();
        if (rfRankBy == "item")
        {
            var items = onMap.Select(s => data.Find(s.Id)).OfType<SubCalculatorData.Sector>().SelectMany(d => d.Pools.SelectMany(p => p.Value.Loot.Select(l => l.ItemId)))
                .Distinct().Select(id => (Id: id, Name: ItemName((int)id, false))).OrderBy(x => x.Name, StringComparer.CurrentCulture).ToList();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220 * scale);
            using var combo = ImRaii.Combo("##rfItem", rfItem == 0 ? "Choose an item…" : ItemName((int)rfItem, false));
            if (combo) foreach (var it in items) if (ImGui.Selectable($"{it.Name}##rfItem{it.Id}", it.Id == rfItem)) rfItem = it.Id;
        }
        Flow(ImGui.CalcTextSize("Longest voyage  12h  24h  36h  48h  Any").X + 70 * scale);
        FieldLabel("Longest voyage");
        RfSegment("hours", RfHours.Select(x => x.Label).ToArray(), Array.FindIndex(RfHours, x => x.Hours == rfMaxHours), i => rfMaxHours = RfHours[i].Hours);
        Flow(FieldWidth("Stops", 110));
        FieldLabel("Stops");
        ImGui.SetNextItemWidth(110 * scale);
        using (var combo = ImRaii.Combo("##rfStops", $"Up to {rfMaxStops}"))
        {
            if (combo) for (var n = 1; n <= 5; n++) if (ImGui.Selectable($"Up to {n}", rfMaxStops == n)) rfMaxStops = n;
        }
        RfFcLine(fcs, "Sectors explored by");

        // Worked out in the background when anything changes; the last answer stays shown meanwhile.
        var fcRead = RfFcRead();
        var key = $"{rfRank}|{string.Join(",", rfParts)}|{rfMap}|{rfRankBy}|{rfItem}|{rfMaxHours}|{rfMaxStops}|{string.Join(",", rfMust)}|{string.Join(",", rfLeaveOut)}|{rfExploreFirst}|{rfFc}|{fcRead?.ReadAt}";
        if (rfWork is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully) rfResult = done.Result;
            else Plugin.Log.Warning(done.Exception, "Route Finder: the search failed");
            rfKey = rfWorkKey;
            rfWork = null;
        }
        if (key != rfKey && rfWork == null)
        {
            var (map, rank, parts, rankBy, item, hours, stops, must, leaveOut, explore) =
                (rfMap, rfRank, rfParts.ToArray(), rfRankBy, rfItem, rfMaxHours, rfMaxStops, rfMust.ToList(), rfLeaveOut.ToList(), rfExploreFirst);
            var read = fcRead?.ReadAt != null ? fcRead.Sectors : null;
            rfWorkKey = key;
            rfWork = Task.Run(() => RfFind(g, data, map, rank, parts, rankBy, item, hours, stops, must, leaveOut, explore, read));
        }
        var result = rfResult;
        var working = key != rfKey;

        // The legend, then the map's sectors as tiles: click for a must, again to leave it out.
        Tag("A", Green); ImGui.SameLine(); Colored(Dim, "explored");
        ImGui.SameLine(0, 14 * scale); Tag("B", Amber); ImGui.SameLine(); Colored(Dim, "explore next");
        ImGui.SameLine(0, 14 * scale); Tag("C", Blue); ImGui.SameLine(); Colored(Dim, "could open by chance");
        Flow(ImGui.CalcTextSize("D not yet   E must be in the route").X + 40 * scale);
        Tag("D", Dim); ImGui.SameLine(); Colored(Dim, "not yet");
        ImGui.SameLine(0, 14 * scale); Tag("E", headingColor ?? softColor); ImGui.SameLine(); Colored(Dim, "must be in the route (click a sector; again to leave it out)");
        RfTiles(onMap, result?.States ?? new());

        // What's next on this map, and new sectors first.
        var states = result?.States ?? new();
        var discovered = onMap.Where(s => states.GetValueOrDefault(s.Id) == "discovered").ToList();
        var chance = onMap.Where(s => states.GetValueOrDefault(s.Id) == "chance").ToList();
        if (discovered.Count > 0 || chance.Count > 0)
        {
            ImGui.Spacing();
            var openers = RfOpeners(g, data);
            if (discovered.Count > 0) { Colored(Amber, string.Join(", ", discovered.Select(s => s.Location))); ImGui.SameLine(); Ui.Text("can be explored next."); }
            if (chance.Count > 0)
            {
                if (discovered.Count > 0) ImGui.SameLine(0, 14 * scale);
                Colored(Blue, string.Join(", ", chance.Select(s => $"{s.Location} opens from {(g.Sectors.TryGetValue(openers.GetValueOrDefault(s.Id), out var f) ? f.Location : "?")}")));
                ImGui.SameLine(); Ui.Text("by chance.");
                Hover(RfOdds);
            }
            ImGui.Checkbox("Put routes that explore something new first##rfExplore", ref rfExploreFirst);
        }

        // 3. The routes.
        ImGui.Spacing();
        var order = rfExploreFirst ? "new sectors first, then " : "";
        order += rfRankBy == "gil" ? "most gil a day first" : rfRankBy == "item" && rfItem != 0 ? "most of the item a day first" : "best experience an hour first";
        HeadingRuns(("Routes", null), ($"   {order}{(working ? " · working out…" : "")}", Dim));
        if (result == null) return;
        if (result.Stats == null) { Ui.Wrapped("This rank or one of these parts isn't in the game's data."); return; }
        var tally = new List<(string, Vector4?)> { ($"{PlannerN(result.Total)}", null), ($" route{(result.Total == 1 ? "" : "s")} fit{(rfMaxHours is double h ? $" in {h:0}h" : "")} and in range   ", Dim) };
        if (rfMust.Count > 0) { tally.Add(("with ", Dim)); tally.Add((string.Join(", ", rfMust.Select(id => g.Sectors[id].Location)) + "   ", null)); }
        if (result.NewOnes > 0) { tally.Add(($"{PlannerN(result.NewOnes)}", Amber)); tally.Add((" explore or could open something new   ", Dim)); }
        tally.Add(("gil at vendor prices", Dim));
        Runs(tally.ToArray());
        if (result.Routes.Count == 0)
        {
            Ui.Wrapped($"No route fits{(rfMaxHours is double hh ? $" in {hh:0}h" : "")} with this sub's range{(rfMust.Count > 0 ? " and these musts" : "")}. Try a longer voyage or fewer musts.");
            return;
        }
        rfDraw = (g, result, rfMust.ToList());
        RfTable.LeaveOut = rfRankBy == "item" ? null : k => k == "item";
        RfTable.DrawControls();
        RfTable.Draw(result.Routes.Take(RfShown).ToList(), fitRows: true);
        if (result.Total > RfShown) Colored(Dim, $"Showing the best {RfShown} of {PlannerN(result.Total)}.");
        Tag("T3", Green); ImGui.SameLine(); Tag("T2", Amber); ImGui.SameLine(); Colored(Dim, "loot tier this build reaches at each stop (amber: the sources differ, so it might not)");
        Flow(ImGui.CalcTextSize("+11 range to spare").X + 10 * scale);
        Colored(Green, "+11"); ImGui.SameLine(); Colored(Dim, "range to spare");
    }

    // Buttons side by side, the chosen one lit.
    private void RfSegment(string id, string[] labels, int chosen, Action<int> pick)
    {
        for (var i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 2 * ImGuiHelpers.GlobalScale);
            using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), i == chosen))
                if (ImGui.SmallButton($"{labels[i]}##rf{id}{i}")) pick(i);
        }
    }

    // The map's sectors as tiles: the letter as a tag, the name, and what the FC has done there (or must / left out).
    private void RfTiles(List<SubSector> onMap, Dictionary<int, string?> states)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = 168 * scale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (width + spacing)));
        var draw = ImGui.GetWindowDrawList();
        for (var i = 0; i < onMap.Count; i++)
        {
            var s = onMap[i];
            if (i % perRow != 0) ImGui.SameLine();
            var state = states.GetValueOrDefault(s.Id);
            var rankOk = s.RankReq <= rfRank;
            var usable = rankOk && state is null or "explored" or "discovered";
            var must = rfMust.Contains(s.Id);
            var outOf = rfLeaveOut.Contains(s.Id);
            var status = must ? "must be in the route" : outOf ? "left out" : !rankOk ? $"needs rank {s.RankReq}" : state != null ? RfStateText(state) : $"rank {s.RankReq}";
            var start = ImGui.GetCursorScreenPos();
            var size = new Vector2(width, ImGui.GetTextLineHeight() * 2 + 10 * scale);
            using (ImRaii.PushId($"rfTile{s.Id}"))
            {
                if (ImGui.InvisibleButton("##tile", size) && usable)
                {
                    if (must) { rfMust.Remove(s.Id); rfLeaveOut.Add(s.Id); }
                    else if (outOf) rfLeaveOut.Remove(s.Id);
                    else if (rfMust.Count < rfMaxStops) rfMust.Add(s.Id);
                }
                Hover($"{s.Location}: {s.Name}, rank {s.RankReq}");
            }
            var border = must ? (headingColor ?? softColor) : state == "discovered" ? Amber : ImGui.GetStyle().Colors[(int)ImGuiCol.Border];
            draw.AddRect(start, start + size, ImGui.GetColorU32(usable ? border : border with { W = border.W * 0.4f }), 6 * scale, ImDrawFlags.None, must ? 2 * scale : 1);
            var alpha = usable ? 1f : 0.45f;
            var color = RfColor(state);
            var tagSize = ImGui.CalcTextSize(s.Location) + new Vector2(10, 2) * scale;
            var at = start + new Vector2(7, 5) * scale;
            draw.AddRectFilled(at, at + tagSize, ImGui.GetColorU32(color with { W = 0.16f * alpha }), 3 * scale);
            draw.AddText(at + new Vector2(5, 1) * scale, ImGui.GetColorU32(color with { W = alpha }), s.Location);
            var name = s.Name;
            var nameRoom = width - tagSize.X - 20 * scale;
            while (name.Length > 1 && ImGui.CalcTextSize(name + "…").X > nameRoom) name = name[..^1];
            if (name != s.Name) name += "…";
            var text = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            draw.AddText(at + new Vector2(tagSize.X + 6 * scale, 1 * scale), ImGui.GetColorU32(text with { W = outOf ? 0.5f : alpha }), name);
            var statusColor = must ? (headingColor ?? softColor) : state == "discovered" ? Amber : Dim;
            draw.AddText(at + new Vector2(0, ImGui.GetTextLineHeight() + 2 * scale), ImGui.GetColorU32(statusColor with { W = alpha }), status);
        }
    }

    private static RfResult? RfFind(PlannerGame g, SubCalculatorData.Data data, int? mapId, int rank, string[] parts, string rankBy, uint item, double? maxHours,
        int maxStops, List<int> must, List<int> leaveOut, bool exploreFirst, Dictionary<int, (bool Unlocked, bool Explored)>? read)
    {
        var stats = SubRoutes.BuildStats(rank, parts, g.Ranks, g.Parts);
        var openers = g.Sectors.Keys.ToDictionary(id => id, id => data.Find(id)?.UnlockedFrom ?? 0);
        var states = RouteFinder.SectorStates(g.Sectors.Keys, read, openers);
        var onMap = g.Sectors.Values.Where(s => s.Map == mapId).OrderBy(s => s.Id).ToList();
        if (stats == null || onMap.Count == 0) return new RfResult(stats, states, new(), new(), 0, 0);
        var allowed = RouteFinder.UsableSectors(onMap, rank, states, leaveOut);
        var values = onMap.ToDictionary(s => s.Id, s => RouteFinder.SectorValue(s, data.Find(s.Id), stats, id => ListingVendor((int)id)));
        var mustOk = must.All(id => allowed.Any(a => a.Id == id));
        var routes = mustOk ? RouteFinder.FindRoutes(allowed, SubRoutes.StartFor(onMap[0].Id, g.Starts), stats, maxStops, maxHours, must, values,
            rankBy == "item" && item != 0 ? "item" : rankBy == "gil" ? "gil" : "exp", rankBy == "item" && item != 0 ? item : null, RouteFinder.OpensOf(openers), states, exploreFirst)
            : new List<RouteFinder.Route>();
        return new RfResult(stats, states, values, routes, routes.Count, routes.Count(r => r.Explores + r.Chances > 0));
    }

    // ---- The routes table ----

    private (PlannerGame G, RfResult Result, List<int> Must)? rfDraw;

    private void RfBar(double value, double max)
    {
        if (TableCapture.Current != null) return;
        ImGui.SameLine();
        var scale = ImGuiHelpers.GlobalScale;
        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, Blue))
            ImGui.ProgressBar(max > 0 ? (float)Math.Clamp(value / max, 0.02, 1) : 0, new Vector2(70 * scale, 6 * scale), "");
    }

    private double RfMax(Func<RouteFinder.Route, double> of) => rfDraw is { } d && d.Result.Routes.Count > 0 ? d.Result.Routes.Take(RfShown).Max(of) : 0;

    private FleetTable<RouteFinder.Route>? rfTable;
    private FleetTable<RouteFinder.Route> RfTable => rfTable ??= new FleetTable<RouteFinder.Route>("routeFinder", plugin.Configuration, new List<FleetColumn<RouteFinder.Route>>
    {
        new("route", "Route", r =>
        {
            if (rfDraw is not { } d) return;
            var scale = ImGuiHelpers.GlobalScale;
            for (var i = 0; i < r.Sectors.Count; i++)
            {
                if (i > 0) ImGui.SameLine(0, 3 * scale);
                var s = d.G.Sectors[r.Sectors[i]];
                Tag(s.Location, d.Must.Contains(s.Id) ? (headingColor ?? softColor) : d.Result.States.GetValueOrDefault(s.Id) == "discovered" ? Amber : softColor);
                Hover(s.Name);
            }
            if (r.Explores > 0)
            {
                ImGui.SameLine(0, 8 * scale);
                Tag($"explores {string.Join(", ", r.Sectors.Where(id => d.Result.States.GetValueOrDefault(id) == "discovered").Select(id => d.G.Sectors[id].Location))}", Amber);
                Hover("Sectors discovered but not explored yet: this voyage explores them.");
            }
            if (r.Chances > 0)
            {
                ImGui.SameLine(0, 8 * scale);
                Tag($"{r.Chances} chance{(r.Chances == 1 ? "" : "s")} at new sectors", Blue);
                Hover(RfOdds);
            }
            if (ReferenceEquals(r, d.Result.Routes[0])) { ImGui.SameLine(0, 8 * scale); Tag("best", Green); }
        }, r => r.Sectors.Count, Locked: true),
        new("voyage", "Voyage", r => { Ui.Text(PlannerHours(r.Hours)); RfBar(r.Hours, RfMax(x => x.Hours)); }, r => r.Hours),
        new("exp", "Exp / hour", r => { Ui.Text(PlannerN(r.ExpPerHour)); RfBar(r.ExpPerHour, RfMax(x => x.ExpPerHour)); }, r => r.ExpPerHour),
        new("gil", "Gil / day", r => { Colored(Palette.Gil ?? Amber, PlannerN(r.GilPerDay)); RfBar(r.GilPerDay, RfMax(x => x.GilPerDay)); }, r => r.GilPerDay),
        new("item", "Item / day", r => { if (r.ItemPerDay is double v) { Ui.Text(v.ToString("0.00", CultureInfo.CurrentCulture)); RfBar(v, RfMax(x => x.ItemPerDay ?? 0)); } else Colored(Dim, "—"); }, r => r.ItemPerDay ?? 0),
        new("tiers", "Loot tier per stop", r =>
        {
            if (rfDraw is not { } d) return;
            for (var i = 0; i < r.Sectors.Count; i++)
            {
                if (i > 0) ImGui.SameLine(0, 3 * ImGuiHelpers.GlobalScale);
                var v = d.Result.Values[r.Sectors[i]];
                Tag(v.Tier is int t ? $"T{t}" : "?", v.Tier is null or 1 ? Dim : v.TierSure == "yes" ? Green : Amber);
                Hover(v.Tier is int tt ? $"Tier {tt}{(v.TierSure == "maybe" ? ", might not: the sources differ" : "")}" : "No loot data yet");
            }
        }, r => r.Sectors.Sum(id => rfDraw?.Result.Values[id].Tier ?? 0)),
        new("range", "Range", r =>
        {
            var spare = (rfDraw?.Result.Stats?.Range ?? 0) - r.Range;
            Colored(Green, $"+{PlannerN(spare)}");
            Hover($"{PlannerN(spare)} more range than this route uses ({PlannerN(r.Range)})");
        }, r => -r.Range),
        new("planner", "Planner", r =>
        {
            if (Ui.SmallButton($"Open in Planner##rf{string.Join("-", r.Sectors)}"))
            {
                plannerChosen = null;
                plannerRank = rfRank;
                for (var i = 0; i < 4; i++) plannerParts[i] = rfParts[i];
                plannerRoute.Clear();
                plannerRoute.AddRange(r.Sectors);
                plannerMap = rfMap;
                shown = Tab.Planner;
            }
            Hover("Open this build and route in Sub Calculator → Planner.");
        }),
    });

    // ---- Unlocks ----

    private void RfUnlocks(PlannerGame g, SubCalculatorData.Data data, List<FcSectors> fcs)
    {
        var scale = ImGuiHelpers.GlobalScale;
        RfFcLine(fcs, "Free Company");
        Colored(Dim, RfOdds);
        var fc = RfFcRead();
        if (fc?.ReadAt == null) return;
        var openers = RfOpeners(g, data);
        var states = RouteFinder.SectorStates(g.Sectors.Keys, fc.Sectors, openers);
        var u = RouteFinder.UnlocksOf(g.Sectors.Values.OrderBy(s => s.Id).ToList(), g.Maps, states, openers);
        string MapName(int id) => g.Maps.FirstOrDefault(m => m.Id == id).Name ?? $"Map {id}";
        SubSector? S(int id) => g.Sectors.TryGetValue(id, out var s) ? s : null;

        // Sure next.
        ImGui.Spacing();
        Boxed(() =>
        {
            HeadingRuns(("SURE NEXT", Amber), ($"   {u.Sure.Count} sector{(u.Sure.Count == 1 ? "" : "s")}", Dim));
            Colored(Dim, "Discovered, not explored yet: any voyage there explores it.");
            if (u.Sure.Count == 0) Colored(Dim, "Nothing waiting.");
            foreach (var id in u.Sure)
            {
                if (S(id) is not { } s) continue;
                Tag(s.Location, Amber); ImGui.SameLine(); Ui.Text(s.Name); ImGui.SameLine(); Colored(Dim, $"{MapName(s.Map)} · rank {s.RankReq}");
                ImGui.SameLine();
                if (Ui.SmallButton($"Find a route##rfSure{id}")) RfFindThrough(s, false);
            }
        }, null, true);

        // Could open.
        ImGui.Spacing();
        Boxed(() =>
        {
            HeadingRuns(("COULD OPEN", Blue), ($"   {u.Chance.Count} sector{(u.Chance.Count == 1 ? "" : "s")}", Dim));
            Colored(Dim, "Go to the sector before it; each voyage there is a roll.");
            if (u.Chance.Count == 0) Colored(Dim, "Nothing yet.");
            foreach (var (id, from) in u.Chance)
            {
                if (S(id) is not { } s) continue;
                var f = S(from);
                Tag(s.Location, Blue); ImGui.SameLine(); Ui.Text(s.Name); ImGui.SameLine();
                Colored(Dim, $"{MapName(s.Map)} · opens from {(f == null ? "?" : $"{f.Location}, {f.Name}")} · rank {s.RankReq}");
                if (f != null) { ImGui.SameLine(); if (Ui.SmallButton($"Routes via {f.Location}##rfChance{id}")) RfFindThrough(f, true); }
            }
        }, null, true);

        // The next map.
        ImGui.Spacing();
        Boxed(() =>
        {
            HeadingRuns(("NEXT MAP", null), ($"   {u.Next?.Name ?? "all open"}", Dim));
            if (u.Next is not { } next) { Colored(Dim, "Every map is open."); return; }
            var opener = next.Steps.Count >= 2 ? S(next.Steps[^2].Id) : null;
            Colored(Dim, $"{next.Name} opens from {(opener == null ? "a sector not open yet" : $"{opener.Location}, {opener.Name}, on {MapName(opener.Map)}")}.");
            for (var i = 0; i < next.Steps.Count; i++)
            {
                var step = next.Steps[i];
                var s = S(step.Id);
                if (s == null) continue;
                var last = i == next.Steps.Count - 1;
                var text = step.How == "explore" ? $"Explore {s.Name}" : step.How == "go" ? $"Go to {s.Name}"
                    : last ? $"{next.Name} opens from {S(next.Steps[i - 1].Id)?.Location}" : $"{s.Name} opens from {S(next.Steps[i - 1].Id)?.Location}";
                Tag(s.Location, step.How == "explore" ? Amber : step.How == "go" ? Green : Dim);
                ImGui.SameLine(); Ui.Text(text); ImGui.SameLine();
                Tag(step.How == "chance" ? "by chance" : "sure", step.How == "chance" ? Blue : Amber);
            }
            var rolls = next.Steps.Count(x => x.How == "chance");
            Colored(Dim, $"{rolls} sector{(rolls == 1 ? "" : "s")} to open by chance on the way.");
        }, null, true);

        // Your maps.
        ImGui.Spacing();
        HeadingRuns(("Your maps", null));
        Tag("A", Green); ImGui.SameLine(); Colored(Dim, "explored");
        ImGui.SameLine(0, 14 * scale); Tag("B", Amber); ImGui.SameLine(); Colored(Dim, "discovered, explore next");
        ImGui.SameLine(0, 14 * scale); Tag("C", Blue); ImGui.SameLine(); Colored(Dim, "could open by chance");
        ImGui.SameLine(0, 14 * scale); Tag("D", Dim); ImGui.SameLine(); Colored(Dim, "not yet");
        foreach (var m in u.Maps)
        {
            ImGui.Spacing();
            var list = g.Sectors.Values.Where(s => s.Map == m.Id).OrderBy(s => s.Id).ToList();
            var lo = list.Min(s => s.RankReq);
            var hi = list.Max(s => s.RankReq);
            HeadingRuns((m.Name, null), ($"   {(lo == hi ? $"rank {lo}" : $"ranks {lo}–{hi}")}   ", Dim));
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.PlotHistogram, Green))
                ImGui.ProgressBar(m.Total > 0 ? (float)m.Explored / m.Total : 0, new Vector2(140 * scale, 7 * scale), "");
            ImGui.SameLine();
            Colored(Dim, m.Open ? $"{m.Explored} of {m.Total} explored{(m.Discovered > 0 ? $" · {m.Discovered} to explore" : "")}" : "not open yet");
            if (!m.Open)
            {
                var opener = list.Count > 0 ? S(openers.GetValueOrDefault(list[0].Id)) : null;
                Colored(Dim, opener == null ? "Opens from a sector on the map before, by chance." : $"Opens from {opener.Location}, {opener.Name}, on {MapName(opener.Map)}, by chance.");
                continue;
            }
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var st = states.GetValueOrDefault(s.Id);
                if (i > 0) Flow(ImGui.CalcTextSize($"{s.Location}  {s.Name}").X + 24 * scale);
                Tag(s.Location, RfColor(st));
                ImGui.SameLine(0, 4 * scale);
                Colored(st == "locked" ? Dim : softColor, s.Name);
                Hover($"{s.Location}: {s.Name}, {RfStateText(st)}, rank {s.RankReq}");
            }
        }
    }

    // ---- Fight Club targets the FC can't run yet (Subs and Route Optimization) ----

    private string fcCheckKey = "";
    private Dictionary<(long Cid, string SubName), RouteFinder.FightClubCheck> fcChecks = new();

    // A sub's Fight Club target checked against its FC's sectors (null: nothing missing, or not read), worked out again
    // only when the routes or the readings change.
    private RouteFinder.FightClubCheck? FightClubBlocked(long cid, string subName)
    {
        var routes = reader.Routes.Data;
        var fcs = reader.SubSectors.Data;
        var chars = reader.Parts.Data?.Inputs.Characters;
        var g = PlannerGameData();
        if (routes?.FightClub == null || fcs == null || chars == null || g == null) return null;
        var key = $"{routes.At}|{reader.SubSectors.At}";
        if (key != fcCheckKey)
        {
            fcCheckKey = key;
            var maps = g.Sectors.Values.GroupBy(s => s.Map).ToDictionary(x => x.Key, x => x.Where(s => s.Location.Length > 0).GroupBy(s => s.Location).ToDictionary(y => y.Key, y => y.First().Id));
            var reads = fcs.Where(f => f.ReadAt != null).ToDictionary(f => f.FcId, f => f.Sectors);
            var fcOf = chars.GroupBy(c => c.Cid).ToDictionary(x => x.Key, x => x.First().FcId);
            fcChecks = new();
            foreach (var r in routes.Subs)
            {
                if (r.TargetRoute.Length == 0 || fcOf.GetValueOrDefault(r.Cid) is not { } fc || !reads.TryGetValue(fc, out var read)) continue;
                var check = RouteFinder.CheckFightClub(routes.FightClub, r.Rank, maps, read);
                if (check.Missing.Count > 0) fcChecks[(r.Cid, r.SubName)] = check;
            }
        }
        return fcChecks.GetValueOrDefault((cid, subName));
    }

    // After a target route: "needs R" in red, and the best route the FC can run now underneath.
    private void FightClubBlockedCell(long cid, string subName)
    {
        if (FightClubBlocked(cid, subName) is not { } check || PlannerGameData() is not { } g) return;
        var openers = g.Sectors.Keys.ToDictionary(id => id, id => SubCalculatorData.Shipped().Find(id)?.UnlockedFrom ?? 0);
        string L(int id) => g.Sectors.TryGetValue(id, out var s) ? s.Location : id.ToString(CultureInfo.InvariantCulture);
        var letters = new HashSet<string>(g.Sectors.Values.Select(s => s.Location).Where(l => l.Length > 0));
        var alt = check.AltRoute == null ? "" : $" Best your FC can run now: {SubRoutes.FormatRoute(check.AltRoute, letters)}, {PlannerN(check.AltNet ?? 0)} gil a day.";
        ImGui.SameLine(0, 6 * ImGuiHelpers.GlobalScale);
        Tag($"needs {string.Join(", ", check.Missing.Select(L))}", Red);
        Hover(string.Join("; ", check.Missing.Select(id => $"{L(id)} isn't discovered yet{(openers.GetValueOrDefault(id) is int f && f != 0 ? $" (it opens from {L(f)}, by chance)" : "")}")) + "." + alt);
        if (check.AltRoute != null)
        {
            var net = check.AltNet ?? 0;
            Colored(Dim, $"now: {SubRoutes.FormatRoute(check.AltRoute, letters)} · {(net >= 1e6 ? $"{net / 1e6:0.0}M" : net >= 1e3 ? $"{Math.Round(net / 1e3):0}k" : $"{Math.Round(net):0}")}/day");
            Hover(alt.Trim());
        }
    }

    // From the Unlocks tab: Best routes on that sector's map, through it.
    private void RfFindThrough(SubSector s, bool exploreFirst)
    {
        rfMap = s.Map;
        rfLeaveOut.Clear();
        rfMust.Clear();
        rfMust.Add(s.Id);
        if (exploreFirst) rfExploreFirst = true;
        rfUnlocksTab = false;
        rfTabPicked = true;
    }
}
