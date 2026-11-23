using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Windows;

// Sub Calculator → Compare Builds, as the app's page: up to three builds side by side (SubCompare). Same route: every build
// flies the one route set here, so the numbers compare the builds alone. Best route each: each build gets its own best
// route on a map (RouteFinder, through the sectors its Free Company has discovered), so the numbers compare build and
// route together. The best figure in each row is green. Builds come from your subs, typed in, or Add to compare on the
// Build Finder and Planner. Worked out away from the game's frame; gil at vendor prices.
public partial class MainWindow
{
    private sealed class CmpBuild
    {
        public string Label = "";
        public string Who = "";
        public int Rank;
        public string[] Parts = { "W+", "C+", "W+", "C+" };
        public string? FcId;
        public bool Changed;
        public string Key => $"{Rank}|{string.Join(",", Parts)}";
    }

    private sealed record CmpColumn(SubStats? Stats, List<int> Route, SubCompare.Check Check, bool Flies, SubCalculator.Plan? Plan, Dictionary<string, double?> Values);
    private sealed record CmpResult(string Mode, List<CmpColumn> Columns, Dictionary<string, List<int>> Marks);

    private readonly List<CmpBuild> cmpBuilds = new();
    private readonly List<int> cmpRoute = new();
    private int? cmpMap;
    private int cmpAdding;
    private bool cmpBest;
    private int? cmpEditing;
    private string cmpRankBy = "exp";
    private double? cmpMaxHours = 24;
    private readonly List<int> cmpMust = new();
    private string? cmpFc;
    private string cmpKey = "";
    private CmpResult? cmpResult;
    private Task<CmpResult?>? cmpWork;
    private string cmpWorkKey = "";

    private static readonly (string Key, string Label, bool Gil, string? Hover, int Digits)[] CmpVoyageRows =
    {
        ("hours", "Voyage", false, "Shorter is better: it is sent out again sooner.", 0),
        ("expPerVoyage", "Experience a voyage", false, "With the bonus it is sure of", 0),
        ("expPerDay", "Experience a day", false, "Sent out again as soon as it is back", 0),
        ("gilPerVoyage", "Gil a voyage", true, null, 0),
        ("gilPerDay", "Gil a day", true, null, 0),
        ("kitsPerDay", "Repair kits a day", false, "Fewer is better", 1),
        ("netPerDay", "Gil a day after repairs", true, "Gil a day less the repair kits, at the Supplies page's kit price", 0),
    };

    // "Add to compare" on the Build Finder and Planner: adds the build (the route too, when Compare has none yet), or,
    // once it's in, opens Compare.
    private void CmpAddButton(string id, string label, string who, int rank, IReadOnlyList<string> parts, IReadOnlyList<int>? route, string? fcId = null)
    {
        var key = $"{rank}|{string.Join(",", parts)}";
        if (cmpBuilds.Any(b => b.Key == key))
        {
            if (Ui.SmallButton($"In Compare ({cmpBuilds.Count}) ›##cmpIn{id}")) shown = Tab.Compare;
            Hover("This build is in Compare Builds: open it.");
            return;
        }
        var full = cmpBuilds.Count >= SubCompare.MaxBuilds;
        using (ImRaii.Disabled(full))
        {
            if (Ui.SmallButton($"Add to compare##cmpAdd{id}"))
            {
                cmpBuilds.Add(new CmpBuild { Label = label, Who = who, Rank = rank, Parts = parts.ToArray(), FcId = fcId });
                if (cmpRoute.Count == 0 && route is { Count: > 0 }) { cmpRoute.AddRange(route.Take(5)); cmpMap = null; }
            }
        }
        Hover(full ? "Compare Builds holds 3 builds: take one out there first." : "Add this build to Sub Calculator → Compare Builds, to see it beside others.");
    }

    private void DrawCompare()
    {
        reader.ReadIfDue(withLists: true); // your subs, their Free Companies, and each FC's sectors
        var g = PlannerGameData();
        if (g == null) { Ui.Wrapped("Compare Builds needs the game's sub data, which couldn't be read."); return; }
        var data = SubCalculatorData.Shipped();
        var scale = ImGuiHelpers.GlobalScale;
        // The map: the route's, else the one picked, else the last.
        if (cmpRoute.Count > 0 && g.Sectors.TryGetValue(cmpRoute[0], out var routeFirst)) cmpMap = routeFirst.Map;
        cmpMap ??= g.Maps.Count > 0 ? g.Maps[^1].Id : null;
        var fcs = reader.SubSectors.Data ?? new List<FcSectors>();
        cmpFc ??= cmpBuilds.FirstOrDefault(b => b.FcId != null)?.FcId ?? fcs.FirstOrDefault(f => f.ReadAt != null)?.FcId;
        var full = cmpBuilds.Count >= SubCompare.MaxBuilds;

        // 1. The builds.
        HeadingRuns(("Builds", null), ($"   {cmpBuilds.Count} of {SubCompare.MaxBuilds}", Dim));
        if (cmpBuilds.Count > 0) { ImGui.SameLine(0, 12 * scale); if (ImGui.SmallButton("Remove all builds##cmpRemoveAll")) { cmpBuilds.Clear(); cmpEditing = null; } }
        if (!full)
        {
            SubPicker("cmp", null, s =>
            {
                var letters = s.Build.Split('-');
                if (letters.Length != 4 || !letters.All(l => SubRoutes.PartClass.ContainsKey(l))) return;
                var b = new CmpBuild { Label = s.SubName, Who = string.Join(" · ", new[] { s.Character, s.World }.Where(x => x.Length > 0)), Rank = s.Rank, Parts = letters,
                    FcId = reader.Parts.Data?.Inputs?.Characters?.FirstOrDefault(c => c.Cid == s.Cid)?.FcId };
                if (cmpBuilds.Count < SubCompare.MaxBuilds && cmpBuilds.All(x => x.Key != b.Key)) cmpBuilds.Add(b);
            });
            ImGui.SameLine();
        }
        using (ImRaii.Disabled(full))
        {
            if (ImGui.Button("Type one in##cmpType"))
            {
                var n = cmpBuilds.Count + 1;
                while (cmpBuilds.Any(b => b.Label == $"Build {n}")) n++;
                var b = new CmpBuild { Label = $"Build {n}", Rank = g.MaxRank };
                while (cmpBuilds.Any(x => x.Key == b.Key) && b.Rank > 1) b.Rank--;
                cmpBuilds.Add(b);
                cmpEditing = cmpBuilds.Count - 1;
            }
        }
        ImGui.SameLine();
        Colored(Dim, full ? "Take one out to add another." : "or use Add to compare on the Build Finder or Voyage Planner.");

        // 2. The route.
        ImGui.Spacing();
        HeadingRuns(("Route", null));
        RfSegment("cmpMode", new[] { "Same route", "Best route each" }, cmpBest ? 1 : 0, i => cmpBest = i == 1);
        ImGui.SameLine(0, 12 * scale);
        Colored(Dim, cmpBest ? "Each build gets its own best route on the map: the numbers compare build and route together."
            : "Every build flies this route: the numbers compare the builds alone.");
        FieldLabel("Map");
        ImGui.SetNextItemWidth(220 * scale);
        using (var combo = ImRaii.Combo("##cmpMap", g.Maps.FirstOrDefault(m => m.Id == cmpMap).Name ?? ""))
        {
            if (combo)
                foreach (var m in g.Maps)
                    if (ImGui.Selectable(m.Name, m.Id == cmpMap)) { cmpMap = m.Id; cmpRoute.Clear(); cmpMust.Clear(); }
        }
        var onMap = g.Sectors.Values.Where(s => s.Map == cmpMap).OrderBy(s => s.Id).ToList();
        if (!cmpBest)
        {
            var free = onMap.Where(s => !cmpRoute.Contains(s.Id)).ToList();
            if (!free.Any(s => s.Id == cmpAdding)) cmpAdding = free.FirstOrDefault()?.Id ?? 0;
            ImGui.SameLine(0, 14 * scale);
            FieldLabel("Add a sector");
            using (ImRaii.Disabled(cmpRoute.Count >= 5 || free.Count == 0))
            {
                ImGui.SetNextItemWidth(260 * scale);
                using (var combo = ImRaii.Combo("##cmpAdd", free.FirstOrDefault(s => s.Id == cmpAdding) is { } a ? $"{a.Location}: {a.Name} (rank {a.RankReq})" : ""))
                {
                    if (combo)
                        foreach (var s in free)
                            if (ImGui.Selectable($"{s.Location}: {s.Name} (rank {s.RankReq})##cmpAdd{s.Id}", s.Id == cmpAdding)) cmpAdding = s.Id;
                }
                ImGui.SameLine();
                if (ImGui.Button("Add##cmpAddButton") && cmpAdding > 0 && cmpRoute.Count < 5) cmpRoute.Add(cmpAdding);
            }
            if (cmpRoute.Count == 0) Colored(Dim, "Add up to 5 sectors.");
            for (var i = 0; i < cmpRoute.Count; i++)
            {
                if (i > 0) ImGui.SameLine(0, 10 * scale);
                var s = g.Sectors.GetValueOrDefault(cmpRoute[i]);
                Colored(Dim, $"{i + 1}");
                ImGui.SameLine(0, 4 * scale);
                Ui.Text(s != null ? $"{s.Location}: {s.Name}" : $"Sector {cmpRoute[i]}");
                ImGui.SameLine(0, 4 * scale);
                if (ImGui.SmallButton($"×##cmpRemove{cmpRoute[i]}")) { cmpRoute.RemoveAt(i); break; }
                Hover("Remove");
            }
            if (cmpRoute.Count > 0) { ImGui.SameLine(0, 12 * scale); if (ImGui.SmallButton("Clear route##cmpClearRoute")) cmpRoute.Clear(); }
            // Best order: the fastest the build with the most range can fly (time doesn't depend on the build).
            if (cmpRoute.Count > 1 && cmpBuilds.Count > 0)
            {
                ImGui.SameLine(0, 12 * scale);
                var most = cmpBuilds.Select(b => SubRoutes.BuildStats(b.Rank, b.Parts, g.Ranks, g.Parts)).Where(x => x != null).OrderByDescending(x => x!.Range).FirstOrDefault();
                if (ImGui.SmallButton("Best order##cmpBest") && most != null)
                {
                    var sectors = cmpRoute.Select(id => g.Sectors.GetValueOrDefault(id)).OfType<SubSector>().ToList();
                    var best = SubCalculator.BestOrder(sectors, SubRoutes.StartFor(sectors[0].Id, g.Starts), most.Speed, most.Range);
                    cmpRoute.Clear();
                    cmpRoute.AddRange(best.Select(x => x.Id));
                }
                Hover("Put the sectors in the fastest order the build with the most range can fly.");
            }
        }
        else
        {
            ImGui.SameLine(0, 14 * scale);
            FieldLabel("Best for");
            RfSegment("cmpRank", new[] { "Experience", "Gil" }, cmpRankBy == "gil" ? 1 : 0, i => cmpRankBy = i == 1 ? "gil" : "exp");
            Flow(ImGui.CalcTextSize("Longest voyage  12h  24h  36h  48h  Any").X + 70 * scale);
            FieldLabel("Longest voyage");
            RfSegment("cmpHours", RfHours.Select(x => x.Label).ToArray(), Array.FindIndex(RfHours, x => x.Hours == cmpMaxHours), i => cmpMaxHours = RfHours[i].Hours);
            // The Free Company whose explored sectors the routes go through.
            FieldLabel("Sectors explored by");
            if (FcPicker("cmp", fcs, cmpFc) is { } picked) cmpFc = picked;
            var fc = cmpFc == null ? null : fcs.FirstOrDefault(f => f.FcId == cmpFc);
            ImGui.SameLine();
            Colored(Dim, fc?.ReadAt is double at ? $"read at the workshop {RfWhen(at)}" : "not read yet: every sector is allowed");
            Colored(Dim, "Must go to:");
            foreach (var s in onMap)
            {
                ImGui.SameLine(0, 4 * scale);
                var must = cmpMust.Contains(s.Id);
                using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), must))
                    if (ImGui.SmallButton($"{s.Location}##cmpMust{s.Id}"))
                    {
                        if (must) cmpMust.Remove(s.Id);
                        else if (cmpMust.Count < 5) cmpMust.Add(s.Id);
                    }
                Hover($"{s.Location}: {s.Name}, rank {s.RankReq}");
            }
            if (cmpMust.Count > 0) { ImGui.SameLine(0, 8 * scale); if (ImGui.SmallButton("Clear##cmpMustClear")) cmpMust.Clear(); }
        }

        // 3. Side by side, worked out in the background when anything changes.
        ImGui.Spacing();
        var kitCost = reader.Credits.Data?.RepairKitCost ?? 0;
        var fcRead = cmpFc == null ? null : fcs.FirstOrDefault(f => f.FcId == cmpFc);
        var mode = cmpBest ? "best" : "same";
        var key = $"{mode}|{string.Join(";", cmpBuilds.Select(b => b.Key))}|{string.Join(",", cmpRoute)}|{cmpMap}|{cmpRankBy}|{cmpMaxHours}|{string.Join(",", cmpMust)}|{cmpFc}|{fcRead?.ReadAt}|{kitCost}";
        if (cmpWork is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully) cmpResult = done.Result;
            else Plugin.Log.Warning(done.Exception, "Compare: the working out failed");
            cmpKey = cmpWorkKey;
            cmpWork = null;
        }
        if (key != cmpKey && cmpWork == null)
        {
            var (builds, route, map, rankBy, hours, must) = (cmpBuilds.Select(b => (b.Rank, Parts: b.Parts.ToArray())).ToList(), cmpRoute.ToList(), cmpMap, cmpRankBy, cmpMaxHours, cmpMust.ToList());
            var read = fcRead?.ReadAt != null ? fcRead.Sectors : null;
            cmpWorkKey = key;
            cmpWork = LowPriority.Run<CmpResult?>(() => CmpWorkOut(g, data, mode, builds, route, map, rankBy, hours, must, read, kitCost));
        }
        var working = key != cmpKey;
        HeadingRuns(("Side by side", null), ("   green: the best of these builds", Dim));
        if (cmpBuilds.Count == 0) { Ui.Wrapped("Add a build to start: one of your subs, one typed in, or Add to compare on the Build Finder or Voyage Planner."); return; }
        Working(working, "Working out the comparison…");
        // Only an answer for these builds in this mode.
        var result = cmpResult is { } r0 && r0.Mode == mode && r0.Columns.Count == cmpBuilds.Count ? r0 : null;
        using (WorkingDim(working)) CmpTable(g, result, mode);
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            Ui.Wrapped("Numbers as the Voyage Planner works them out, gil at vendor prices. Best route each: as the Route Finder ranks them (up to 5 stops), only through " +
                       "sectors the Free Company has discovered. A build that can't fly the route shows why instead of numbers.");
    }

    private static CmpResult CmpWorkOut(PlannerGame g, SubCalculatorData.Data data, string mode, List<(int Rank, string[] Parts)> builds, List<int> sameRoute, int? mapId,
        string rankBy, double? maxHours, List<int> must, Dictionary<int, (bool Unlocked, bool Explored)>? read, double kitCost)
    {
        var openers = g.Sectors.Keys.ToDictionary(id => id, id => data.Find(id)?.UnlockedFrom ?? 0);
        var states = RouteFinder.SectorStates(g.Sectors.Keys, read, openers);
        var onMap = g.Sectors.Values.Where(s => s.Map == mapId).OrderBy(s => s.Id).ToList();
        double Price(uint id) => ListingVendor((int)id);
        var columns = new List<CmpColumn>();
        foreach (var (rank, parts) in builds)
        {
            var stats = SubRoutes.BuildStats(rank, parts, g.Ranks, g.Parts);
            var route = new List<int>();
            if (stats != null && mode == "same") route = sameRoute.Where(g.Sectors.ContainsKey).Take(5).ToList();
            else if (stats != null && onMap.Count > 0)
            {
                var allowed = RouteFinder.UsableSectors(onMap, rank, states, new List<int>());
                var values = onMap.ToDictionary(s => s.Id, s => RouteFinder.SectorValue(s, data.Find(s.Id), stats, Price));
                var routes = must.All(id => allowed.Any(a => a.Id == id))
                    ? RouteFinder.FindRoutes(allowed, SubRoutes.StartFor(onMap[0].Id, g.Starts), stats, 5, maxHours, must, values, rankBy == "gil" ? "gil" : "exp", null,
                        RouteFinder.OpensOf(openers), states, false)
                    : new List<RouteFinder.Route>();
                if (routes.Count > 0) route = routes[0].Sectors;
            }
            var sectors = route.Select(id => g.Sectors[id]).ToList();
            var check = SubCompare.RouteCheck(sectors, sectors.Count > 0 ? SubRoutes.StartFor(sectors[0].Id, g.Starts) : null, stats, rank);
            var flies = route.Count > 0 && check.Short == 0 && check.TooHigh.Count == 0;
            var plan = route.Count > 0 && stats != null
                ? SubCalculator.PlanFor(route, stats, parts.Select(l => SubRoutes.PartClass[l]).ToList(), g.Sectors, g.Starts, g.Game.Parts, data, Price, kitCost) : null;
            columns.Add(new CmpColumn(stats, route, check, flies, plan, SubCompare.RowValues(stats, plan, flies)));
        }
        return new CmpResult(mode, columns, SubCompare.BestMarks(columns.Select(c => c.Values).ToList()));
    }

    private void CmpTable(PlannerGame g, CmpResult? result, string mode)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var n = cmpBuilds.Count;
        using var table = ImRaii.Table("##cmpTable", n + 1, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;
        ImGui.TableSetupColumn("##cmpLabel", ImGuiTableColumnFlags.WidthFixed, 190 * scale);
        for (var i = 0; i < n; i++) ImGui.TableSetupColumn($"##cmpBuild{i}", ImGuiTableColumnFlags.WidthStretch, 1);
        string L(int id) => g.Sectors.TryGetValue(id, out var s) ? s.Location : "?";
        bool Best(string k, int i) => result != null && result.Marks.TryGetValue(k, out var m) && m.Contains(i);
        void BestFill(string k, int i) { if (Best(k, i)) ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(Green with { W = 0.10f })); }
        void Group(string text)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Spacing();
            Colored(Dim, text.ToUpperInvariant());
        }
        void Label(string text, string? hover = null)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Colored(softColor, text);
            if (hover != null) Hover(hover);
        }
        void Number(string k, int i, string text, bool gil = false)
        {
            BestFill(k, i);
            TableCells.Right(text, Best(k, i) ? Green : gil ? (Palette.Gil ?? Amber) : null);
        }

        // The builds' headings: name, whose, rank and parts; Change opens them; × takes one out.
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        int? remove = null;
        for (var i = 0; i < n; i++)
        {
            var b = cmpBuilds[i];
            ImGui.TableNextColumn();
            using var id = ImRaii.PushId($"cmpHead{i}");
            Ui.Text(b.Label);
            if (b.Changed) { ImGui.SameLine(); Colored(Dim, "· changed"); }
            ImGui.SameLine();
            if (ImGui.SmallButton("×")) remove = i;
            Hover("Take this build out");
            if (b.Who.Length > 0) Colored(Dim, b.Who);
            Tag($"Rank {b.Rank}", softColor);
            foreach (var p in b.Parts) { ImGui.SameLine(0, 4 * scale); Tag(p, softColor); }
            PartRankNote(PlannerGameData(), b.Rank, b.Parts);
            if (ImGui.SmallButton(cmpEditing == i ? "Done" : "Change")) cmpEditing = cmpEditing == i ? null : i;
            if (cmpEditing == i)
            {
                var g2 = PlannerGameData();
                ImGui.SetNextItemWidth(110 * scale);
                var rank = b.Rank;
                if (ImGui.InputInt("Rank", ref rank) && g2 != null) { b.Rank = Math.Clamp(rank, 1, g2.MaxRank); b.Changed = b.Who.Length > 0 && !b.Who.StartsWith("from the "); }
                for (var s = 0; s < 4; s++)
                {
                    ImGui.SetNextItemWidth(170 * scale);
                    using var combo = ImRaii.Combo($"{PlannerSlots[s]}##part{s}", PlannerPartName(b.Parts[s]));
                    if (combo)
                        foreach (var l in PlannerLetters)
                            if (ImGui.Selectable(PlannerPartName(l), b.Parts[s] == l)) { b.Parts[s] = l; b.Changed = b.Who.Length > 0 && !b.Who.StartsWith("from the "); }
                }
            }
        }
        if (remove is int r) { cmpBuilds.RemoveAt(r); cmpEditing = null; return; }

        Group("Stats");
        foreach (var (k, label) in new[] { ("surveillance", "Surveillance"), ("retrieval", "Retrieval"), ("speed", "Speed"), ("range", "Range"), ("favor", "Favor") })
        {
            Label(label);
            for (var i = 0; i < n; i++)
            {
                ImGui.TableNextColumn();
                var v = result?.Columns[i].Values[k];
                Number(k, i, v is double d ? PlannerN(d) : "—");
            }
        }

        Group("Route");
        Label(mode == "same" ? "Can it fly it?" : "Its best route");
        for (var i = 0; i < n; i++)
        {
            ImGui.TableNextColumn();
            if (result?.Columns[i] is not { } c) { Colored(Dim, "…"); continue; }
            if (mode == "best")
                for (var j = 0; j < c.Route.Count; j++)
                {
                    if (j > 0) ImGui.SameLine(0, 3 * scale);
                    Tag(L(c.Route[j]), cmpMust.Contains(c.Route[j]) ? (headingColor ?? softColor) : softColor);
                    Hover(g.Sectors.GetValueOrDefault(c.Route[j])?.Name ?? "");
                }
            var problem = c.Stats == null ? "This rank or a part isn't in the game data"
                : c.Route.Count == 0 ? (mode == "best" ? "No route fits" : "No route yet")
                : c.Check.TooHigh.Count > 0 ? $"Rank too low for {string.Join(", ", c.Check.TooHigh.Select(L))}"
                : c.Check.Short > 0 ? $"Out of range: needs {PlannerN(c.Check.Short)} more" : null;
            if (mode == "best" && c.Route.Count > 0 && problem != null) ImGui.SameLine(0, 6 * scale);
            if (problem != null) Colored(c.Route.Count > 0 ? Red : Dim, problem);
            else if (mode == "same") Colored(Green, "Yes");
        }
        Label("Range used", "Range the route uses, of the build's range");
        for (var i = 0; i < n; i++)
        {
            ImGui.TableNextColumn();
            if (result?.Columns[i] is not { Stats: { } st, Check.Used: int used } c) { TableCells.Right("—", Dim); continue; }
            BestFill("spare", i);
            TableCells.Right(c.Check.Short > 0 ? $"{PlannerN(used)} / {PlannerN(st.Range)}" : $"{PlannerN(used)} / {PlannerN(st.Range)}  +{PlannerN(st.Range - used)}",
                c.Check.Short > 0 ? Red : Best("spare", i) ? Green : null);
        }

        Group("Voyage · gil at vendor prices");
        foreach (var (k, label, gil, hover, digits) in CmpVoyageRows)
        {
            Label(label, hover);
            for (var i = 0; i < n; i++)
            {
                ImGui.TableNextColumn();
                var v = result?.Columns[i].Values[k];
                Number(k, i, v is double d ? (k == "hours" ? PlannerHours(d) : PlannerN(d, digits)) : "—", gil && v != null);
            }
        }

        // Same route: a row per stop. Best route each: the stops differ, so one row with each build's own tiers.
        Group("Each stop");
        if (mode == "same")
        {
            foreach (var id in cmpRoute)
            {
                Label($"{L(id)}  {g.Sectors.GetValueOrDefault(id)?.Name}");
                for (var i = 0; i < n; i++)
                {
                    ImGui.TableNextColumn();
                    var sp = result?.Columns[i].Plan?.Sectors.FirstOrDefault(x => x.Sector.Id == id);
                    if (sp?.Result is not { } res) { Colored(Dim, sp == null ? "—" : "no loot data"); continue; }
                    PlannerPill(res.TierSure, $"T{res.Tier}");
                    ImGui.SameLine(0, 6 * scale);
                    PlannerPill(res.Retrieval == "optimal" ? res.RetrievalSure : res.Retrieval == "normal" ? "maybe" : "no", char.ToUpperInvariant(res.Retrieval[0]) + res.Retrieval[1..]);
                    if (res.DoubleDip != "no") { ImGui.SameLine(0, 6 * scale); PlannerPill(res.DoubleDip, "Double dip"); }
                }
            }
        }
        else
        {
            Label("Loot tier per stop");
            for (var i = 0; i < n; i++)
            {
                ImGui.TableNextColumn();
                var sectors = result?.Columns[i].Plan?.Sectors;
                if (sectors == null || sectors.Count == 0) { Colored(Dim, "—"); continue; }
                for (var j = 0; j < sectors.Count; j++)
                {
                    if (j > 0) ImGui.SameLine(0, 3 * scale);
                    var res = sectors[j].Result;
                    Tag(res == null ? "?" : $"T{res.Tier}", res == null || res.Tier == 1 ? Dim : res.TierSure == "yes" ? Green : Amber);
                    Hover(res == null ? "No loot data yet" : $"{sectors[j].Sector.Location}: tier {res.Tier}{(res.TierSure == "maybe" ? ", might not: the sources differ" : "")}");
                }
            }
        }

        // Open in Planner.
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        for (var i = 0; i < n; i++)
        {
            ImGui.TableNextColumn();
            var b = cmpBuilds[i];
            var route = result?.Columns[i].Route is { Count: > 0 } rt ? rt : cmpRoute;
            if (Ui.SmallButton($"Open in Voyage Planner##cmpOpen{i}"))
            {
                plannerChosen = null;
                plannerRank = b.Rank;
                for (var s = 0; s < 4; s++) plannerParts[s] = b.Parts[s];
                plannerRoute.Clear();
                plannerRoute.AddRange(route.Take(5));
                if (route.Count > 0 && g.Sectors.TryGetValue(route[0], out var first)) plannerMap = first.Map;
                shown = Tab.Planner;
            }
            Hover("Open this build and its route in Sub Calculator → Voyage Planner.");
        }
    }
}
