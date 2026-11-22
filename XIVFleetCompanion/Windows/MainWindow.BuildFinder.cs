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

// Sub Calculator → Build Finder, as the app's page: pick sectors and what you want from each (a loot tier, optimal
// retrieval, a double dip, items), and see every build that gets it, each at the lowest rank it works (BuildFinder).
// Three parts: what you want (a card per sector), the options, and the builds (what they need as chips, the counts,
// then the list banded by rank). Colours: a part you own green, one to get amber; "might not" (only the lower of
// XIVStats' and Pepper's figures met) amber; out of range red. Worked out away from the game's frame.
public partial class MainWindow
{
    private static readonly (string Key, string Label)[] FinderSorts =
        { ("rank", "Lowest rank"), ("speed", "Fastest voyage"), ("parts", "Fewest parts still needed"), ("range", "Most range to spare") };
    private static readonly string[] FinderSlots = { "hull", "stern", "bow", "bridge" };
    private const int FinderShown = 200;

    private int? finderMap;
    private int finderAdding;
    private readonly List<BuildFinder.Goal> finderGoals = new();
    private string finderSort = "rank";
    private bool finderSureOnly;
    private (long Cid, string SubName)? finderFor;

    private sealed record FinderResult(BuildFinder.Needs Need, List<SubSector> Route, List<SubSector> Sectors, int MinRank, int? RangeNeeded, int Total, int Sure,
        int OutOfRange, Dictionary<int, int> ByRank, List<BuildFinder.Row> Rows);
    private string finderKey = "";
    private FinderResult? finderResult;
    private Task<FinderResult?>? finderWork;
    private string finderWorkKey = "";

    // ---- Drawing helpers: a tag (text on a soft patch of its colour), a box around a group, a row that wraps ----

    // Text on a soft, rounded patch of its own colour (an export gets the text).
    private static void Tag(string text, Vector4 color)
    {
        if (TableCapture.Current != null) { Ui.Text(text); return; }
        var scale = ImGuiHelpers.GlobalScale;
        var pad = new Vector2(5, 1) * scale;
        var size = ImGui.CalcTextSize(text);
        var at = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(at, at + size + pad * 2, ImGui.GetColorU32(color with { W = 0.16f }), 3 * scale);
        draw.AddText(at + pad, ImGui.GetColorU32(color), text);
        ImGui.Dummy(size + pad * 2);
    }

    // Draws content in a box with padding: a fill (null: none) and a border; fullWidth: as wide as the space left.
    private void Boxed(Action content, Vector4? fill, bool fullWidth, float padX = 10, float padY = 7)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var pad = new Vector2(padX, padY) * scale;
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var right = start.X + ImGui.GetContentRegionAvail().X;
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + pad);
        ImGui.BeginGroup();
        content();
        ImGui.EndGroup();
        var end = ImGui.GetItemRectMax() + pad;
        if (fullWidth) end.X = Math.Max(end.X, right);
        draw.ChannelsSetCurrent(0);
        if (fill is Vector4 f) draw.AddRectFilled(start, end, ImGui.GetColorU32(f), 6 * scale);
        draw.AddRect(start, end, ImGui.GetColorU32(ImGuiCol.Border), 6 * scale);
        draw.ChannelsMerge();
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(end - start);
    }

    // The next piece goes on the same line when it fits (width), else on the next.
    private static void Flow(float width)
    {
        var lineEnd = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X * 2 + width <= lineEnd) ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X * 2);
    }

    // A label in front of its box, the two kept together: "Map [The Northern Empty ▾]".
    private float FieldWidth(string label, float box) => ImGui.CalcTextSize(label).X + ImGui.GetStyle().ItemSpacing.X + box * ImGuiHelpers.GlobalScale;
    private void FieldLabel(string label)
    {
        Colored(Dim, label);
        ImGui.SameLine();
    }

    private float CheckWidth(string label) => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;

    private void DrawBuildFinder()
    {
        reader.ReadIfDue(withLists: true); // your subs, their Free Companies, and the parts you have (Parts read)
        var g = PlannerGameData();
        if (g == null) { Ui.Wrapped("The Build Finder needs the game's sub data, which couldn't be read."); return; }
        var data = SubCalculatorData.Shipped();
        finderMap ??= g.Maps.Count > 0 ? g.Maps[^1].Id : null;
        var scale = ImGuiHelpers.GlobalScale;

        // 1. What you want: the map, add a sector, then a card for each sector with its goals.
        HeadingRuns(("What you want", null), ($"   {finderGoals.Count} of 5 sectors", Dim));
        FieldLabel("Map");
        ImGui.SetNextItemWidth(220 * scale);
        using (var combo = ImRaii.Combo("##finderMap", g.Maps.FirstOrDefault(m => m.Id == finderMap).Name ?? ""))
        {
            if (combo)
                foreach (var m in g.Maps)
                    if (ImGui.Selectable(m.Name, m.Id == finderMap)) { finderMap = m.Id; finderGoals.Clear(); }
        }
        var onMap = g.Sectors.Values.Where(s => s.Map == finderMap && finderGoals.All(x => x.SectorId != s.Id)).OrderBy(s => s.Id).ToList();
        if (!onMap.Any(s => s.Id == finderAdding)) finderAdding = onMap.FirstOrDefault()?.Id ?? 0;
        string SectorLabel(SubSector s) => $"{s.Location}: {s.Name} (rank {s.RankReq}){(data.Find(s.Id) == null ? ", no loot data yet" : "")}";
        using (ImRaii.Disabled(finderGoals.Count >= 5 || onMap.Count == 0))
        {
            Flow(FieldWidth("Add a sector", 300) + ImGui.CalcTextSize("Add").X + 30 * scale);
            FieldLabel("Add a sector");
            ImGui.SetNextItemWidth(300 * scale);
            using (var combo = ImRaii.Combo("##finderAdd", onMap.FirstOrDefault(s => s.Id == finderAdding) is { } a ? SectorLabel(a) : ""))
            {
                if (combo)
                    foreach (var s in onMap)
                        if (ImGui.Selectable($"{SectorLabel(s)}##finderAdd{s.Id}", s.Id == finderAdding)) finderAdding = s.Id;
            }
            ImGui.SameLine();
            if (ImGui.Button("Add##finderAddButton") && finderAdding > 0 && finderGoals.Count < 5)
                finderGoals.Add(new BuildFinder.Goal(finderAdding, 0, false, false, new List<uint>()));
        }
        if (finderGoals.Count == 0) Colored(Dim, "Add up to 5 sectors, then say what you want from each.");
        for (var i = 0; i < finderGoals.Count; i++)
        {
            ImGui.Spacing();
            var index = i;
            var removed = false;
            Boxed(() => removed = FinderSectorCard(index, g, data), null, true);
            if (removed) break;
        }

        // 2. The options.
        ImGui.Spacing();
        ImGui.Spacing();
        FieldLabel("Sort by");
        ImGui.SetNextItemWidth(200 * scale);
        using (var combo = ImRaii.Combo("##finderSort", FinderSorts.First(x => x.Key == finderSort).Label))
        {
            if (combo)
                foreach (var (sortKey, label) in FinderSorts)
                    if (ImGui.Selectable(label, finderSort == sortKey)) finderSort = sortKey;
        }
        Flow(CheckWidth("Only builds sure to reach it"));
        ImGui.Checkbox("Only builds sure to reach it##finderSure", ref finderSureOnly);
        Hover("Leave out builds that reach the goals only by the lower of XIVStats' and Pepper's figures.");
        Flow(ImGui.CalcTextSize("Parts you own  Choose one of your subs… ▾  Whole fleet").X + 40 * scale);
        FieldLabel("Parts you own");
        SubPicker("finder", finderFor, s => finderFor = (s.Cid, s.SubName));
        ImGui.SameLine();
        if (finderFor != null) { if (ImGui.SmallButton("Whole fleet##finderFleet")) finderFor = null; }
        else Colored(Dim, "whole fleet");

        if (finderGoals.Count == 0) { FinderFoot(); return; }

        // Worked out in the background when anything changes; the last answer stays shown meanwhile.
        var inputs = reader.Parts.Data?.Inputs;
        var key = $"{string.Join(";", finderGoals.Select(x => $"{x.SectorId}:{x.Tier}:{x.Optimal}:{x.DoubleDip}:{string.Join(",", x.Items)}"))}|{finderSort}|{finderSureOnly}|{finderFor}|{inputs?.GetHashCode()}";
        if (finderWork is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully) finderResult = done.Result;
            else Plugin.Log.Warning(done.Exception, "Build Finder: the search failed");
            finderKey = finderWorkKey; // not tried again until something changes
            finderWork = null;
        }
        if (key != finderKey && finderWork == null)
        {
            var goals = finderGoals.ToList();
            var (sort, sureOnly, forCid) = (finderSort, finderSureOnly, finderFor?.Cid);
            finderWorkKey = key;
            finderWork = Task.Run(() => FindBuilds(g, data, goals, sort, sureOnly, inputs, forCid));
        }
        var result = finderResult;
        var working = key != finderKey;

        // 3. The builds.
        HeadingRuns(("Builds that get it", null), (working ? "   working out…" : "", Dim));
        if (result == null) { FinderFoot(); return; }
        FinderNeeds(result);
        if (result.Need.Missing.Count > 0) Colored(Amber, $"No loot data yet for {string.Join(", ", result.Need.Missing)}: its goals are left out.");
        ImGui.Spacing();
        var tally = new List<(string, Vector4?)> { ($"{PlannerN(result.Total)}", null), ($" build{(result.Total == 1 ? "" : "s")}   ", Dim), ($"{PlannerN(result.Sure)}", Green), (" sure   ", Dim) };
        if (result.Total > result.Sure) { tally.Add(($"{PlannerN(result.Total - result.Sure)}", Amber)); tally.Add((" might not   ", Dim)); }
        tally.Add(($"{PlannerN(result.OutOfRange)}", result.OutOfRange > 0 ? Red : null));
        tally.Add((" out of range", Dim));
        Runs(tally.ToArray());
        // The legend.
        Tag("C", Green); ImGui.SameLine(); Colored(Dim, "you own it");
        ImGui.SameLine(0, 16 * scale); Tag("C+", Amber); ImGui.SameLine(); Colored(Dim, "still to get");
        Flow(ImGui.CalcTextSize("might not  only the lower of the two sources' figures is met").X + 20 * scale);
        Tag("might not", Amber); ImGui.SameLine(); Colored(Dim, "only the lower of the two sources' figures is met");
        ImGui.Spacing();

        if (result.Rows.Count == 0)
        {
            Ui.Wrapped($"No build reaches all of that at any rank{(finderSureOnly ? " for sure (untick \"Only builds sure to reach it\" to see the near ones)" : "")}.");
        }
        else
        {
            // Banded by rank ("Rank 144 · 312 builds") while sorted by lowest rank; the Rank column only otherwise.
            var banded = finderSort == "rank";
            FinderTable.Band = banded
                ? r => r.RangeShort > 0 ? $"Out of range · {PlannerN(result.OutOfRange)} builds" : $"Rank {r.Rank} · {PlannerN(result.ByRank.GetValueOrDefault(r.Rank))} build{(result.ByRank.GetValueOrDefault(r.Rank) == 1 ? "" : "s")}"
                : null;
            FinderTable.LeaveOut = banded ? k => k == "rank" : null;
            finderNeed = result.Need;
            FinderTable.DrawControls();
            FinderTable.Draw(result.Rows, fitRows: true);
            if (result.Total > result.Rows.Count)
                Colored(Dim, $"Showing the first {result.Rows.Count} of {PlannerN(result.Total)}, by {FinderSorts.First(x => x.Key == finderSort).Label.ToLowerInvariant()}.");
        }
        FinderFoot();
    }

    // A sector's card: its letter and name, the rank it needs, Remove; then its goals. True when it was removed.
    private bool FinderSectorCard(int i, PlannerGame g, SubCalculatorData.Data data)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var goal = finderGoals[i];
        var s = g.Sectors.GetValueOrDefault(goal.SectorId);
        var d = data.Find(goal.SectorId);
        Tag(s?.Location ?? "?", headingColor ?? softColor);
        ImGui.SameLine();
        Ui.Text(s?.Name ?? $"Sector {goal.SectorId}");
        ImGui.SameLine();
        Colored(Dim, $"rank {s?.RankReq}");
        ImGui.SameLine(0, 16 * scale);
        if (ImGui.SmallButton($"Remove##finderRemove{goal.SectorId}")) { finderGoals.RemoveAt(i); return true; }
        if (d == null) { Colored(Dim, "No XIVStats data for this sector yet, so it can't set a goal."); return false; }

        FieldLabel("Loot tier");
        ImGui.SetNextItemWidth(80 * scale);
        using (var combo = ImRaii.Combo($"##finderTier{goal.SectorId}", goal.Tier == 0 ? "Any" : $"T{goal.Tier}"))
        {
            if (combo)
                foreach (var t in new[] { 0, 2, 3 })
                    if (ImGui.Selectable(t == 0 ? "Any" : $"T{t}", goal.Tier == t)) finderGoals[i] = goal = goal with { Tier = t };
        }
        Flow(CheckWidth("Optimal retrieval"));
        var optimal = goal.Optimal;
        if (ImGui.Checkbox($"Optimal retrieval##finderOpt{goal.SectorId}", ref optimal)) finderGoals[i] = goal = goal with { Optimal = optimal };
        Flow(CheckWidth("Double dip"));
        var dip = goal.DoubleDip;
        if (ImGui.Checkbox($"Double dip##finderDip{goal.SectorId}", ref dip)) finderGoals[i] = goal = goal with { DoubleDip = dip };
        Flow(FieldWidth("Items", 240));
        FieldLabel("Items");
        ImGui.SetNextItemWidth(240 * scale);
        var items = d.Pools.SelectMany(p => p.Value.Loot.Select(l => l.ItemId)).Distinct()
            .Select(id => (Id: id, Name: ItemName((int)id, false), Tier: BuildFinder.ItemTier(d, id) ?? 1))
            .OrderBy(x => x.Tier).ThenBy(x => x.Name, StringComparer.CurrentCulture).ToList();
        using (var combo = ImRaii.Combo($"##finderItems{goal.SectorId}", "Add an item…"))
        {
            if (combo)
                foreach (var it in items.Where(x => !goal.Items.Contains(x.Id)))
                    if (ImGui.Selectable($"{it.Name} (T{it.Tier})##finderItem{goal.SectorId}_{it.Id}"))
                        finderGoals[i] = goal = goal with { Items = goal.Items.Append(it.Id).ToList() };
        }
        foreach (var id in goal.Items.ToList())
        {
            var name = $"{ItemName((int)id, false)} T{BuildFinder.ItemTier(d, id)}";
            Flow(ImGui.CalcTextSize(name).X + 40 * scale);
            Tag(name, softColor);
            ImGui.SameLine(0, 2 * scale);
            if (ImGui.SmallButton($"×##finderDrop{goal.SectorId}_{id}")) finderGoals[i] = goal = goal with { Items = goal.Items.Where(x => x != id).ToList() };
            Hover("Remove");
        }
        return false;
    }

    // What the goals need, as boxes: each stat asked for (amber where the sources differ), the rank and the range.
    private void FinderNeeds(FinderResult result)
    {
        var chips = new List<(string Label, string Value, string Why, bool Warn)>();
        void Need(string label, SubCalculator.Breakpoint? bp, List<string> why)
        {
            if (bp == null) return;
            var differ = bp.Low != bp.High;
            chips.Add((label, PlannerRange(bp), $"for {string.Join(", ", why)}{(differ ? " · sources differ" : "")}", differ));
        }
        Need("SURVEILLANCE", result.Need.Surveillance, result.Need.WhySurveillance);
        Need("RETRIEVAL", result.Need.Retrieval, result.Need.WhyRetrieval);
        Need("FAVOR", result.Need.Favor, result.Need.WhyFavor);
        if (chips.Count == 0) chips.Add(("STATS", "any", "no goal needs a stat", false));
        chips.Add(("RANK", $"{result.MinRank}+", $"for {string.Join(", ", result.Sectors.Select(s => s.Location))}", false));
        chips.Add(("RANGE", PlannerN(result.RangeNeeded), $"route {string.Join("-", result.Route.Select(s => s.Location))}", false));
        for (var i = 0; i < chips.Count; i++)
        {
            var (label, value, why, warn) = chips[i];
            if (i > 0) Flow(Math.Max(ImGui.CalcTextSize(why).X, ImGui.CalcTextSize(label).X) + 24 * ImGuiHelpers.GlobalScale);
            Boxed(() =>
            {
                Colored(Dim, label);
                Tinted(warn ? Amber : null, value);
                Colored(Dim, why);
            }, warn ? Amber with { W = 0.12f } : null, false, 10, 5);
        }
    }

    // The search itself (away from the frame): what the goals need, the route in its least-range order, every build.
    private static FinderResult? FindBuilds(PlannerGame g, SubCalculatorData.Data data, List<BuildFinder.Goal> goals, string sort, bool sureOnly, PartsInputs? inputs, long? forCid)
    {
        var sectors = goals.Select(x => g.Sectors.GetValueOrDefault(x.SectorId)).OfType<SubSector>().Take(5).ToList();
        if (sectors.Count == 0) return null;
        var need = BuildFinder.NeedsFor(goals, id => data.Find(id), id => g.Sectors.GetValueOrDefault(id)?.Location ?? id.ToString(CultureInfo.InvariantCulture));
        var start = SubRoutes.StartFor(sectors[0].Id, g.Starts);
        var route = sectors.Count > 1 ? SubCalculator.BestOrder(sectors, start, 100) : sectors;
        var minRank = Math.Max(1, sectors.Max(s => s.RankReq));
        var owned = inputs != null ? BuildFinder.Owned(inputs, forCid) : null;
        var rows = BuildFinder.SortBuilds(BuildFinder.FindBuilds(need, g.Ranks, g.Parts, minRank, g.MaxRank, route, start, owned, sureOnly), sort);
        var byRank = rows.Where(r => r.RangeShort <= 0).GroupBy(r => r.Rank).ToDictionary(x => x.Key, x => x.Count());
        return new FinderResult(need, route, sectors, minRank, SubCalculator.RangeUsed(route, start), rows.Count, rows.Count(r => r.Sure), rows.Count(r => r.RangeShort > 0),
            byRank, rows.Take(FinderShown).ToList());
    }

    private void FinderFoot()
    {
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            Ui.Wrapped("Breakpoints: XIVStats, checked against Pepper's FFXIV Ship Guide. Every rank and part combination (10,000 builds a rank) is checked. " +
                       "Parts you own: fitted on a sub, or spare in a bag, a retainer or the FC chest.");
    }

    // A build from the Build Finder in the Planner, with the route.
    private void FinderOpen(BuildFinder.Row r)
    {
        if (finderResult == null) return;
        plannerChosen = null;
        plannerRank = r.Rank;
        for (var i = 0; i < 4; i++) plannerParts[i] = r.Parts[i];
        plannerRoute.Clear();
        plannerRoute.AddRange(finderResult.Route.Select(s => s.Id));
        if (finderResult.Route.Count > 0) plannerMap = finderResult.Route[0].Map;
        shown = Tab.Planner;
    }

    // ---- The list ----

    private BuildFinder.Needs? finderNeed;

    // Pieces right-aligned in a cell, each in its own colour.
    private static void RightRuns(params (string Text, Vector4? Color)[] runs)
    {
        if (TableCapture.Current != null) { Ui.Text(string.Concat(runs.Select(r => r.Text))); return; }
        var width = runs.Sum(r => ImGui.CalcTextSize(r.Text).X);
        var free = ImGui.GetContentRegionAvail().X - width;
        if (free > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + free);
        Runs(runs);
    }

    // A stat: one the goals ask for in full with what it has to spare (green; amber at exactly 0), the rest greyed.
    private void FinderStat(double value, double? spare, bool asked)
    {
        if (!asked || spare is not double sp) { TableCells.Right(PlannerN(value), Dim); return; }
        RightRuns((PlannerN(value), null), ($" +{PlannerN(sp)}", sp == 0 ? Amber : Green));
    }

    private string PartName(string letter) =>
        (letter.EndsWith('+') ? "Modified " : "") + PlannerClassNames[letter[..1]];

    private FleetTable<BuildFinder.Row>? finderTable;
    private FleetTable<BuildFinder.Row> FinderTable => finderTable ??= new FleetTable<BuildFinder.Row>("buildFinder", plugin.Configuration, new List<FleetColumn<BuildFinder.Row>>
    {
        new("rank", "Rank", r => RightNumber(r.Rank), r => r.Rank),
        new("build", "Build", r =>
        {
            var missing = r.MissingParts?.Select(m => m.Slot).ToHashSet();
            for (var slot = 0; slot < 4; slot++)
            {
                if (slot > 0) ImGui.SameLine(0, 4 * ImGuiHelpers.GlobalScale);
                var get = missing != null && missing.Contains(slot);
                Tag(r.Parts[slot], missing == null ? softColor : get ? Amber : Green);
                Hover($"{char.ToUpperInvariant(FinderSlots[slot][0])}{FinderSlots[slot][1..]}: {PartName(r.Parts[slot])}{(missing == null ? "" : get ? ", still to get" : ", you own it")}");
            }
            if (!r.Sure)
            {
                ImGui.SameLine(0, 8 * ImGuiHelpers.GlobalScale);
                Tag("might not", Amber);
                Hover($"Reaches the lower of XIVStats' and Pepper's figures, not the higher. {(r.SureRank is int sr ? $"Sure from rank {sr}." : "Never sure.")}");
            }
        }, r => string.Concat(r.Parts), Locked: true),
        new("surveillance", "Surveillance", r => FinderStat(r.Stats.Surveillance, r.SpareSurveillance, finderNeed?.Surveillance != null), r => r.Stats.Surveillance),
        new("retrieval", "Retrieval", r => FinderStat(r.Stats.Retrieval, r.SpareRetrieval, finderNeed?.Retrieval != null), r => r.Stats.Retrieval),
        new("favor", "Favor", r => FinderStat(r.Stats.Favor, r.SpareFavor, finderNeed?.Favor != null), r => r.Stats.Favor),
        new("speed", "Speed", r => TableCells.Right(PlannerN(r.Stats.Speed), Dim), r => r.Stats.Speed),
        new("range", "Range", r =>
        {
            if (r.RangeShort > 0)
            {
                Tag($"needs {PlannerN(r.RangeShort)} more range", Red);
                ImGui.SameLine();
                Colored(Dim, r.InRangeFrom is int f ? $"in range from rank {f}" : "not in range at any rank");
            }
            else if (r.RangeNeeded is int n) RightRuns((PlannerN(r.Stats.Range), null), ($" +{PlannerN(r.Stats.Range - n)}", Green));
            else TableCells.Right(PlannerN(r.Stats.Range));
        }, r => r.Stats.Range - (r.RangeNeeded ?? 0)),
        new("voyage", "Voyage", r => TableCells.Right(r.Hours is double h ? PlannerHours(h) : "—"), r => r.Hours ?? double.MaxValue),
        new("parts", "To get", r =>
        {
            if (r.MissingParts == null) Colored(Dim, "—");
            else if (r.MissingParts.Count == 0) Colored(Green, "All owned");
            else
            {
                Ui.Text($"{r.MissingParts.Count} part{(r.MissingParts.Count == 1 ? "" : "s")}");
                Hover(string.Join(", ", r.MissingParts.Select(p => $"{PartName(p.Letter)} {FinderSlots[p.Slot]}")));
            }
        }, r => r.MissingParts?.Count ?? -1),
        new("planner", "Planner", r =>
        {
            if (Ui.SmallButton($"Open in Planner##finder{string.Concat(r.Parts)}")) FinderOpen(r);
            Hover("Open this build and the route in Sub Calculator → Planner.");
        }),
    });
}
