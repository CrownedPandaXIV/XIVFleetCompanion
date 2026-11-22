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
// Amber: only the lower of XIVStats' and Pepper's figures is met. Worked out away from the game's frame.
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

    private sealed record FinderResult(BuildFinder.Needs Need, List<SubSector> Route, int MinRank, int? RangeNeeded, int Total, int Sure, int OutOfRange, List<BuildFinder.Row> Rows);
    private string finderKey = "";
    private FinderResult? finderResult;
    private Task<FinderResult?>? finderWork;
    private string finderWorkKey = "";

    private void DrawBuildFinder()
    {
        reader.ReadIfDue(withLists: true); // your subs, their Free Companies, and the parts you have (Parts read)
        var g = PlannerGameData();
        if (g == null) { Ui.Wrapped("The Build Finder needs the game's sub data, which couldn't be read."); return; }
        var data = SubCalculatorData.Shipped();
        finderMap ??= g.Maps.Count > 0 ? g.Maps[^1].Id : null;
        var scale = ImGuiHelpers.GlobalScale;

        // What you want.
        Heading($"What you want ({finderGoals.Count} of 5 sectors)");
        ImGui.SetNextItemWidth(220 * scale);
        using (var combo = ImRaii.Combo("Map##finderMap", g.Maps.FirstOrDefault(m => m.Id == finderMap).Name ?? ""))
        {
            if (combo)
                foreach (var m in g.Maps)
                    if (ImGui.Selectable(m.Name, m.Id == finderMap)) { finderMap = m.Id; finderGoals.Clear(); }
        }
        ImGui.SameLine();
        var onMap = g.Sectors.Values.Where(s => s.Map == finderMap && finderGoals.All(x => x.SectorId != s.Id)).OrderBy(s => s.Id).ToList();
        if (!onMap.Any(s => s.Id == finderAdding)) finderAdding = onMap.FirstOrDefault()?.Id ?? 0;
        string SectorLabel(SubSector s) => $"{s.Location}: {s.Name} (rank {s.RankReq}){(data.Find(s.Id) == null ? ", no loot data yet" : "")}";
        using (ImRaii.Disabled(finderGoals.Count >= 5 || onMap.Count == 0))
        {
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
            var goal = finderGoals[i];
            var s = g.Sectors.GetValueOrDefault(goal.SectorId);
            var d = data.Find(goal.SectorId);
            ImGui.Separator();
            Ui.Text(s != null ? $"{s.Location}: {s.Name}" : $"Sector {goal.SectorId}");
            ImGui.SameLine();
            Colored(Dim, $"(rank {s?.RankReq})");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##finderRemove{goal.SectorId}")) { finderGoals.RemoveAt(i); break; }
            if (d == null) { Colored(Dim, "No XIVStats data for this sector yet, so it can't set a goal."); continue; }
            ImGui.SetNextItemWidth(80 * scale);
            using (var combo = ImRaii.Combo($"Loot tier##finderTier{goal.SectorId}", goal.Tier == 0 ? "Any" : $"T{goal.Tier}"))
            {
                if (combo)
                    foreach (var t in new[] { 0, 2, 3 })
                        if (ImGui.Selectable(t == 0 ? "Any" : $"T{t}", goal.Tier == t)) finderGoals[i] = goal = goal with { Tier = t };
            }
            ImGui.SameLine();
            var optimal = goal.Optimal;
            if (ImGui.Checkbox($"Optimal retrieval##finderOpt{goal.SectorId}", ref optimal)) finderGoals[i] = goal = goal with { Optimal = optimal };
            ImGui.SameLine();
            var dip = goal.DoubleDip;
            if (ImGui.Checkbox($"Double dip##finderDip{goal.SectorId}", ref dip)) finderGoals[i] = goal = goal with { DoubleDip = dip };
            ImGui.SameLine();
            ImGui.SetNextItemWidth(240 * scale);
            var items = d.Pools.SelectMany(p => p.Value.Loot.Select(l => l.ItemId)).Distinct()
                .Select(id => (Id: id, Name: ItemName((int)id, false), Tier: BuildFinder.ItemTier(d, id) ?? 1))
                .OrderBy(x => x.Tier).ThenBy(x => x.Name, StringComparer.CurrentCulture).ToList();
            using (var combo = ImRaii.Combo($"Items##finderItems{goal.SectorId}", "Add an item…"))
            {
                if (combo)
                    foreach (var it in items.Where(x => !goal.Items.Contains(x.Id)))
                        if (ImGui.Selectable($"{it.Name} (T{it.Tier})##finderItem{goal.SectorId}_{it.Id}"))
                            finderGoals[i] = goal = goal with { Items = goal.Items.Append(it.Id).ToList() };
            }
            foreach (var id in goal.Items.ToList())
            {
                ImGui.SameLine();
                Ui.Text($"{ItemName((int)id, false)} (T{BuildFinder.ItemTier(d, id)})");
                ImGui.SameLine();
                if (ImGui.SmallButton($"×##finderDrop{goal.SectorId}_{id}")) finderGoals[i] = goal = goal with { Items = goal.Items.Where(x => x != id).ToList() };
            }
        }

        // Options.
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.SetNextItemWidth(200 * scale);
        using (var combo = ImRaii.Combo("Sort by##finderSort", FinderSorts.First(x => x.Key == finderSort).Label))
        {
            if (combo)
                foreach (var (sortKey, label) in FinderSorts)
                    if (ImGui.Selectable(label, finderSort == sortKey)) finderSort = sortKey;
        }
        ImGui.SameLine();
        ImGui.Checkbox("Only builds sure to reach it##finderSure", ref finderSureOnly);
        Hover("Leave out builds that reach the goals only by the lower of XIVStats' and Pepper's figures.");
        ImGui.SameLine();
        Ui.Text("Parts you own:");
        ImGui.SameLine();
        SubPicker("finder", finderFor, s => finderFor = (s.Cid, s.SubName));
        ImGui.SameLine();
        if (finderFor != null) { if (ImGui.SmallButton("Whole fleet##finderFleet")) finderFor = null; }
        else Colored(Dim, "whole fleet");

        if (finderGoals.Count == 0) { FinderFoot(data); return; }

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
        if (result == null) { Colored(Dim, "Working out…"); return; }

        // What it needs.
        ImGui.Spacing();
        var needs = new List<(string, Vector4?)> { ("Needs ", Dim) };
        void NeedRun(string name, SubCalculator.Breakpoint? bp)
        {
            if (bp == null) return;
            needs.Add(($"{name} {PlannerRange(bp)}{(bp.Low != bp.High ? " (sources differ)" : "")}", null));
            needs.Add(("  ·  ", Dim));
        }
        NeedRun("surveillance", result.Need.Surveillance);
        NeedRun("retrieval", result.Need.Retrieval);
        NeedRun("favor", result.Need.Favor);
        if (result.Need.Surveillance == null && result.Need.Retrieval == null && result.Need.Favor == null) needs.Add(("no stat (any build will do)  ·  ", null));
        needs.Add(($"rank {result.MinRank}+ for these sectors  ·  range {PlannerN(result.RangeNeeded)} (best order: {string.Join("-", result.Route.Select(x => x.Location))})", null));
        Runs(needs.ToArray());
        Hover(string.Join(", ", result.Need.WhySurveillance.Concat(result.Need.WhyRetrieval).Concat(result.Need.WhyFavor)));
        if (result.Need.Missing.Count > 0) Colored(Amber, $"No loot data yet for {string.Join(", ", result.Need.Missing)}: its goals are left out.");
        Colored(Dim, (working ? "Working out… " : "") +
                     $"{PlannerN(result.Total)} build{(result.Total == 1 ? "" : "s")} reach it ({PlannerN(result.Sure)} sure, {PlannerN(result.OutOfRange)} out of range)." +
                     (result.Total > result.Rows.Count ? $" Showing the first {result.Rows.Count}." : "") +
                     " Each at the lowest rank it works; amber where only the lower of XIVStats' and Pepper's figures is met.");
        if (result.Rows.Count == 0)
        {
            Ui.Wrapped($"No build reaches all of that at any rank{(finderSureOnly ? " for sure (untick \"Only builds sure to reach it\" to see the near ones)" : "")}.");
        }
        else
        {
            FinderTable.DrawControls();
            FinderTable.Draw(result.Rows);
        }
        FinderFoot(data);
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
        return new FinderResult(need, route, minRank, SubCalculator.RangeUsed(route, start), rows.Count, rows.Count(r => r.Sure), rows.Count(r => r.RangeShort > 0),
            rows.Take(FinderShown).ToList());
    }

    private void FinderFoot(SubCalculatorData.Data data)
    {
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            Ui.Wrapped("Breakpoints: XIVStats, checked against Pepper's FFXIV Ship Guide. Every rank and part combination (10,000 builds a rank) is checked. " +
                       "Parts you own: fitted on a sub, or spare in a bag, a retainer or the FC chest.");
    }

    // A stat with what it has to spare over the goals (none asked: just the stat).
    private static void FinderStat(double value, double? spare) =>
        TableCells.Right(spare is double sp ? $"{PlannerN(value)} +{PlannerN(sp)}" : PlannerN(value));

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

    private FleetTable<BuildFinder.Row>? finderTable;
    private FleetTable<BuildFinder.Row> FinderTable => finderTable ??= new FleetTable<BuildFinder.Row>("buildFinder", plugin.Configuration, new List<FleetColumn<BuildFinder.Row>>
    {
        new("rank", "Rank", r =>
        {
            if (r.Sure) RightNumber(r.Rank);
            else
            {
                TableCells.Right(r.Rank.ToString(CultureInfo.CurrentCulture), Amber);
                Colored(Dim, r.SureRank is int sr ? $"sure from rank {sr}" : "never sure");
            }
            Hover(r.Sure ? "Sure to reach the goals at this rank" : "Reaches them by the lower of the two sources' figures only");
        }, r => r.Rank, Locked: true),
        new("build", "Build", r => Ui.Text(string.Concat(r.Parts)), r => string.Concat(r.Parts)),
        new("surveillance", "Surveillance", r => FinderStat(r.Stats.Surveillance, r.SpareSurveillance), r => r.Stats.Surveillance),
        new("retrieval", "Retrieval", r => FinderStat(r.Stats.Retrieval, r.SpareRetrieval), r => r.Stats.Retrieval),
        new("favor", "Favor", r => FinderStat(r.Stats.Favor, r.SpareFavor), r => r.Stats.Favor),
        new("speed", "Speed", r => TableCells.Right(PlannerN(r.Stats.Speed)), r => r.Stats.Speed),
        new("range", "Range", r =>
        {
            if (r.RangeShort > 0)
            {
                Colored(Red, $"{PlannerN(r.Stats.Range)}: needs {PlannerN(r.RangeShort)} more for these sectors");
                Colored(Dim, r.InRangeFrom is int f ? $"in range from rank {f}" : "not in range at any rank");
            }
            else TableCells.Right(r.RangeNeeded is int n ? $"{PlannerN(r.Stats.Range)} +{PlannerN(r.Stats.Range - n)}" : PlannerN(r.Stats.Range));
        }, r => r.Stats.Range - (r.RangeNeeded ?? 0)),
        new("voyage", "Voyage", r => TableCells.Right(r.Hours is double h ? PlannerHours(h) : "—"), r => r.Hours ?? double.MaxValue),
        new("parts", "Parts to get", r =>
        {
            if (r.MissingParts == null) Colored(Dim, "—");
            else if (r.MissingParts.Count == 0) Colored(Green, "All owned");
            else Ui.Text(string.Join(", ", r.MissingParts.Select(p => $"{p.Letter} {FinderSlots[p.Slot]}")));
        }, r => r.MissingParts?.Count ?? -1),
        new("planner", "Planner", r =>
        {
            if (Ui.SmallButton($"Open in Planner##finder{string.Concat(r.Parts)}")) FinderOpen(r);
            Hover("Open this build and the route in Sub Calculator → Planner.");
        }),
    });
}
