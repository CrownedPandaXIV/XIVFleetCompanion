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

// Sub Calculator → Levelling Plan, as the app's page: a sub's way from its rank to a target rank (Levelling). Stages:
// the route best for experience an hour at each rank, only through sectors the Free Company has discovered, with how
// many voyages and days each takes. Two choices: one sub in detail, or every sub in a Free Company one line each; keep
// the build, or suggest part swaps on the way (Standard parts only if wanted). Worked out away from the game's frame.
public partial class MainWindow
{
    private static readonly string[] LvSlots = { "Hull", "Stern", "Bow", "Bridge" };
    private bool lvEveryFc;
    private (long Cid, string SubName)? lvChosen;
    private int lvRank = 1;
    private double lvExp;
    private readonly string[] lvParts = { "S", "S", "S", "S" };
    private bool lvEditBuild;
    private int? lvTarget;
    private readonly Dictionary<(long, string), int> lvTargets = new();
    private double? lvMaxHours = 24;
    private bool lvSuggest;
    private bool lvStandardOnly;
    private string? lvFc;
    private bool lvEverySector;

    private sealed record LvSub((long Cid, string SubName) Key, int Rank, double Exp, int Target, string[] Parts, long Cid);
    private sealed record LvResult(bool Every, List<(LvSub Sub, Levelling.Plan Plan)> Plans);
    private string lvKey = "";
    private LvResult? lvResult;
    private string? lvError; // the last working out's failure, shown instead of an old plan
    private Task<LvResult?>? lvWork;
    private string lvWorkKey = "";
    private System.Threading.CancellationTokenSource? lvCancel;

    private static string LvDays(double hours)
    {
        var d = hours / 24;
        return d < 1 ? $"{Math.Max(1, Math.Round(hours)):0}h" : d < 10 ? $"{Math.Round(d * 2) / 2:0.#} days" : $"{Math.Round(d):0} days";
    }

    private static string LvDate(DateTimeOffset at) => at.LocalDateTime.ToString("d MMM", CultureInfo.CurrentCulture);

    // When a sub's first voyage of the plan leaves: now, or when it's back if it's out.
    private static DateTimeOffset LvStart(SubRow? s)
    {
        var now = DateTimeOffset.UtcNow;
        return s != null && s.ReturnTime > now.ToUnixTimeSeconds() ? DateTimeOffset.FromUnixTimeSeconds(s.ReturnTime) : now;
    }

    // A sub's default target: its next build's rank (Subs page), else the top rank.
    private int LvDefaultTarget(SubRow s, int top) => Math.Min(top, Math.Max(s.Rank + 1, RouteOf(s.Cid, s.SubName)?.RankUp.NextBuild?.Rank ?? top));
    // A sub's own target while it's still above its rank (it may have ranked past it since), else the default.
    private int LvTargetOf(SubRow s, int top) => lvTargets.TryGetValue((s.Cid, s.SubName), out var t) && t > s.Rank ? t : LvDefaultTarget(s, top);

    private string? LvFcOf(long cid) => reader.Parts.Data?.Inputs?.Characters?.FirstOrDefault(c => c.Cid == cid)?.FcId;

    private void DrawLevelling()
    {
        reader.ReadIfDue(withLists: true); // your subs, their next builds, Free Companies, and each FC's sectors
        var g = PlannerGameData();
        if (g == null) { Ui.Wrapped("The Levelling Plan needs the game's sub data, which couldn't be read."); return; }
        var data = SubCalculatorData.Current();
        var scale = ImGuiHelpers.GlobalScale;
        var fcs = reader.SubSectors.Data ?? new List<FcSectors>();
        lvFc ??= fcs.FirstOrDefault(f => f.ReadAt != null)?.FcId ?? fcs.FirstOrDefault()?.FcId;
        lvRank = Math.Clamp(lvRank, 1, g.MaxRank);
        var subs = reader.Subs.Data ?? new List<SubRow>();
        var chosenSub = lvChosen is { } ch ? subs.FirstOrDefault(s => s.Cid == ch.Cid && s.SubName == ch.SubName) : null;

        FieldLabel("Plan for");
        RfSegment("lvMode", new[] { "One sub", "Every sub in a Free Company" }, lvEveryFc ? 1 : 0, i => lvEveryFc = i == 1);

        // The Free Company picker and whether it has been read.
        void FcLine()
        {
            FieldLabel("Sectors explored by");
            if (FcPicker("lv", fcs, lvFc) is { } picked) lvFc = picked;
            var f0 = lvFc == null ? null : fcs.FirstOrDefault(f => f.FcId == lvFc);
            ImGui.SameLine(0, 12 * scale);
            ImGui.Checkbox("Use every sector##lvEvery", ref lvEverySector);
            Hover("Plan as if every sector were discovered: how long it takes once the Free Company has found them.");
            ImGui.SameLine(0, 12 * scale);
            Colored(Dim, lvEverySector ? "every sector, as if all were discovered"
                : f0?.ReadAt is double at ? $"only sectors {f0.Name} has discovered (read at the workshop {RfWhen(at)})" : "not read yet: every sector is allowed");
        }
        // The longest voyage, and keep the build or suggest changes.
        void Choices()
        {
            FieldLabel("Longest voyage");
            RfSegment("lvHours", RfHours.Select(x => x.Label).ToArray(), Array.FindIndex(RfHours, x => x.Hours == lvMaxHours), i => lvMaxHours = RfHours[i].Hours);
            Flow(ImGui.CalcTextSize("Parts  Keep this build  Suggest changes on the way").X + 60 * scale);
            FieldLabel("Parts");
            RfSegment("lvParts", new[] { "Keep this build", "Suggest changes on the way" }, lvSuggest ? 1 : 0, i => lvSuggest = i == 1);
            if (lvSuggest) { Flow(CheckWidth("Standard parts only")); ImGui.Checkbox("Standard parts only##lvStd", ref lvStandardOnly); }
        }

        // What to work out: one sub, or every sub in the FC (below the top rank).
        var asked = new List<LvSub>();
        var fcSubs = lvFc == null ? new List<SubRow>() : subs.Where(s => LvFcOf(s.Cid) == lvFc).ToList();
        if (!lvEveryFc)
        {
            var target = lvTarget ?? (chosenSub != null && chosenSub.Rank == lvRank ? LvDefaultTarget(chosenSub, g.MaxRank) : g.MaxRank);
            target = Math.Clamp(target, Math.Min(g.MaxRank, lvRank + 1), g.MaxRank);
            if (lvRank < g.MaxRank) asked.Add(new LvSub(lvChosen ?? (0, ""), lvRank, lvExp, target, lvParts.ToArray(), lvChosen?.Cid ?? 0));
        }
        else
        {
            foreach (var s in fcSubs.Where(s => s.Rank < g.MaxRank))
            {
                var letters = s.Build.Split('-');
                if (letters.Length != 4 || !letters.All(l => SubRoutes.PartClass.ContainsKey(l))) continue;
                asked.Add(new LvSub((s.Cid, s.SubName), s.Rank, s.CurrentExp, LvTargetOf(s, g.MaxRank), letters, s.Cid));
            }
        }
        var fcRead = lvFc == null ? null : fcs.FirstOrDefault(f => f.FcId == lvFc);
        var kitCost = reader.Credits.Data?.RepairKitCost ?? 0;
        var key = $"{lvEveryFc}|{string.Join(";", asked.Select(a => $"{a.Key}|{a.Rank}|{a.Exp}|{a.Target}|{string.Join(",", a.Parts)}"))}|{lvMaxHours}|{lvSuggest}|{lvStandardOnly}|{lvFc}|{lvEverySector}|{fcRead?.ReadAt}|{kitCost}";
        if (lvWork is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully) { lvResult = done.Result; lvError = null; }
            else if (!done.IsCanceled && done.Exception?.InnerException is not OperationCanceledException)
            {
                Plugin.Log.Warning(done.Exception, "Levelling Plan: the working out failed");
                lvResult = null;
                lvError = FailureText(done);
            }
            if (!done.IsCanceled) lvKey = lvWorkKey;
            lvWork = null;
        }
        // A newer choice stops the plan still being worked out, instead of waiting for it.
        // (Going back to the choice already shown stops it too: its answer would replace the one asked for.)
        if (lvWork != null && lvWorkKey != key) { lvCancel?.Cancel(); lvWork = null; }
        if (key != lvKey && lvWork == null)
        {
            var (every, hours, suggest, standard) = (lvEveryFc, lvMaxHours, lvSuggest, lvStandardOnly);
            // Use every sector: as if every sector were discovered (the full-map plan), whatever the FC has read.
            var read = !lvEverySector && fcRead?.ReadAt != null ? fcRead.Sectors : null;
            var inputs = reader.Parts.Data?.Inputs;
            var list = asked.ToList();
            lvWorkKey = key;
            lvCancel = new System.Threading.CancellationTokenSource();
            var cancel = lvCancel.Token;
            lvWork = LowPriority.Run<LvResult?>(() =>
            {
                var expToNext = g.Ranks.ToDictionary(kv => kv.Key, kv => kv.Value.ExpToNext);
                var partRank = Levelling.PartRanks(g.Game.Parts);
                Levelling.Searches For(IReadOnlyDictionary<int, (bool Unlocked, bool Explored)>? r) =>
                    new(g.Sectors, g.Starts, g.Maps.Select(m => m.Id), g.Ranks, g.Parts, g.Game.Parts, data, id => ListingVendor((int)id), kitCost, r, hours, cancel);
                var search = For(read);
                var all = read != null ? For(null) : null;
                var plans = new List<(LvSub, Levelling.Plan)>();
                foreach (var s in list)
                {
                    var owned = suggest && inputs != null && s.Cid != 0 ? BuildFinder.Owned(inputs, s.Cid) : new HashSet<string>();
                    var plan = Levelling.LevellingPlan(s.Rank, s.Exp, s.Target, expToNext, s.Parts, search.BestAt, search.Score, suggest, standard, owned, partRank);
                    if (read != null) Levelling.MarkBetter(plan, all, read, search);
                    plans.Add((s, plan));
                }
                return new LvResult(every, plans);
            });
        }
        var working = key != lvKey;
        var result = lvResult is { } r0 && r0.Every == lvEveryFc ? r0 : null;

        if (!lvEveryFc)
        {
            // 1. Your sub.
            HeadingRuns(("Your sub", null));
            SubPicker("lv", lvChosen, s =>
            {
                var letters = s.Build.Split('-');
                if (letters.Length == 4 && letters.All(l => SubRoutes.PartClass.ContainsKey(l))) for (var i = 0; i < 4; i++) lvParts[i] = letters[i];
                lvChosen = (s.Cid, s.SubName);
                lvRank = s.Rank;
                lvExp = s.CurrentExp;
                lvTarget = null;
                if (LvFcOf(s.Cid) is { } fc) lvFc = fc;
            });
            if (lvChosen != null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear sub##lvClearSub")) { lvChosen = null; lvTarget = null; }
                Hover("Stop starting from this sub; the build stays as it is.");
            }
            ImGui.SameLine(0, 14 * scale);
            Tag($"Rank {lvRank}", softColor);
            foreach (var p in lvParts) { ImGui.SameLine(0, 4 * scale); Tag(p, softColor); }
            ImGui.SameLine(0, 12 * scale);
            if (ImGui.SmallButton(lvEditBuild ? "Done##lvEdit" : "Change parts or rank##lvEdit")) lvEditBuild = !lvEditBuild;
            if (lvEditBuild)
            {
                ImGui.SetNextItemWidth(110 * scale);
                var rank = lvRank;
                if (ImGui.InputInt("Rank##lvRank", ref rank)) { lvRank = Math.Clamp(rank, 1, g.MaxRank); lvExp = 0; lvTarget = null; }
                for (var i = 0; i < 4; i++)
                {
                    ImGui.SameLine(0, 12 * scale);
                    ImGui.SetNextItemWidth(170 * scale);
                    using var combo = ImRaii.Combo($"{PlannerSlots[i]}##lvPart{i}", PlannerPartName(lvParts[i]));
                    if (combo) foreach (var l in PlannerLetters) if (ImGui.Selectable(PlannerPartName(l), lvParts[i] == l)) lvParts[i] = l;
                }
            }
            if (lvRank < g.MaxRank)
            {
                var next = chosenSub != null && chosenSub.Rank == lvRank ? RouteOf(chosenSub.Cid, chosenSub.SubName)?.RankUp.NextBuild?.Rank : null;
                var goal = asked.Count > 0 ? asked[0].Target : g.MaxRank;
                string Label(int rr) => $"Rank {rr}{(rr == next ? " (its next build)" : rr == g.MaxRank ? " (the top rank)" : "")}";
                FieldLabel("Level to");
                ImGui.SetNextItemWidth(220 * scale);
                using (var combo = ImRaii.Combo("##lvTarget", Label(goal), ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                        for (var rr = lvRank + 1; rr <= g.MaxRank; rr++)
                            if (ImGui.Selectable($"{Label(rr)}##lvT{rr}", rr == goal)) lvTarget = rr;
                }
                if (lvTarget != null) { ImGui.SameLine(); if (ImGui.SmallButton("Reset target##lvResetTarget")) lvTarget = null; }
            }
            FcLine();

            // 2. The plan.
            ImGui.Spacing();
            HeadingRuns(("The plan", null), ("   best experience at each rank, sent out again as soon as it's back", Dim));
            Choices();
            if (lvRank >= g.MaxRank) { Ui.Wrapped("This sub is at the top rank."); return; }
            Working(working, "Working out the plan…");
            if (!working) WorkError(lvError, "the plan");
            if (result?.Plans.FirstOrDefault() is not { } one) return;
            using var dim = WorkingDim(working);
            var plan = one.Plan;
            ImGui.Spacing();
            LvSummary(one.Sub, plan, LvStart(chosenSub));
            if (!plan.Reached)
                Colored(Amber, plan.StuckAt is int stuck ? $"From rank {stuck}, no route fits this sub's range{(lvMaxHours is double hh ? $" in {hh:0}h" : "")}. Try a longer voyage or other parts." : "The plan stops here.");
            if (lvSuggest && plan.Swaps.Count == 0) Colored(Dim, $"No part swap{(lvStandardOnly ? " to a Standard part" : "")} makes this plan shorter.");
            LvStages(g, plan);
        }
        else
        {
            HeadingRuns(("Every sub in a Free Company", null), ("   each to its next build's rank (else the top rank)", Dim));
            FcLine();
            if (lvTargets.Count > 0) { ImGui.SameLine(); if (ImGui.SmallButton("Reset targets##lvResetTargets")) lvTargets.Clear(); }
            Choices();
            if (fcSubs.Count == 0) { Ui.Wrapped("No subs in this Free Company."); return; }
            Working(working, "Working out the plans…");
            if (!working) WorkError(lvError, "the plans");
            using var dim = WorkingDim(working);
            lvFleetCtx = (g, result);
            LvFleetTable.DrawControls();
            LvFleetTable.Draw(fcSubs.Select(sub => new LvFleetRow(sub, (sub.Cid, sub.SubName), sub.Rank >= g.MaxRank,
                LvTargetOf(sub, g.MaxRank))).ToList(), fitRows: true);
        }
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            Ui.Wrapped("Experience with the bonus each sub is sure of (Pepper's guide); routes as the Route Finder ranks them for experience (up to 5 stops, any map); " +
                       "rank-up experience from the game data. A sub out now starts when it's back. Part swaps are tried at the start of each stage, one part at a time.");
    }

    private void LvRoute(PlannerGame g, List<int> route)
    {
        for (var j = 0; j < route.Count; j++)
        {
            if (j > 0) ImGui.SameLine(0, 3 * ImGuiHelpers.GlobalScale);
            Tag(g.Sectors.TryGetValue(route[j], out var s) ? s.Location : "?", softColor);
            Hover(s?.Name ?? "");
        }
    }

    // The four boxes, then the stages as a strip of days.
    private void LvSummary(LvSub sub, Levelling.Plan plan, DateTimeOffset start)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using (var boxes = ImRaii.Table("##lvBoxes", 4, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.PadOuterX))
        {
            if (boxes)
            {
                ImGui.TableNextColumn();
                BoxTitle("Ranks", $"{sub.Rank} → {sub.Target}", null);
                Colored(Dim, $"{PlannerN(sub.Target - sub.Rank)} rank{(sub.Target - sub.Rank == 1 ? "" : "s")}, {PlannerN(plan.ExpNeeded)} experience");
                ImGui.TableNextColumn();
                BoxTitle("Voyages", PlannerN(plan.Voyages), null);
                Colored(Dim, $"{plan.Stages.Count} stage{(plan.Stages.Count == 1 ? "" : "s")}{(plan.Swaps.Count > 0 ? $", {plan.Swaps.Count} part swap{(plan.Swaps.Count == 1 ? "" : "s")}" : "")}");
                ImGui.TableNextColumn();
                BoxTitle("Time", $"about {LvDays(plan.Hours)}", null);
                Colored(Dim, $"rank {sub.Target} around {LvDate(start.AddHours(plan.Hours))}");
                if (plan.Swaps.Count > 0) Colored(Dim, $"{LvDays(plan.KeepHours - plan.Hours)} sooner than keeping the build");
                ImGui.TableNextColumn();
                BoxTitle("Ceruleum · kits", $"{PlannerN(plan.Tanks)} · {PlannerN(plan.Kits)}", null);
                Colored(Dim, "tanks · repair kits on the way");
            }
        }
        if (plan.Hours <= 0 || TableCapture.Current != null) return;
        // The strip: a piece per stage, as long as its days.
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetTextLineHeight() + 6 * scale;
        var colors = new[] { Blue, Blue with { W = 0.65f }, Green, Green with { W = 0.65f } };
        var x = at.X;
        for (var i = 0; i < plan.Stages.Count; i++)
        {
            var st = plan.Stages[i];
            var w = (float)(width * st.Hours / plan.Hours);
            draw.AddRectFilled(new Vector2(x, at.Y), new Vector2(x + w - 2 * scale, at.Y + height), ImGui.GetColorU32(colors[i % 4]), 3 * scale);
            var label = $"{st.FromRank}–{st.ToRank}";
            var size = ImGui.CalcTextSize(label);
            if (size.X < w - 6 * scale) draw.AddText(new Vector2(x + (w - size.X) / 2, at.Y + 3 * scale), ImGui.GetColorU32(new Vector4(0.06f, 0.07f, 0.1f, 1f)), label);
            x += w;
        }
        ImGui.Dummy(new Vector2(width, height));
        Colored(Dim, "start");
        var end = $"day {Math.Round(plan.Hours / 24):0}";
        ImGui.SameLine(width - ImGui.CalcTextSize(end).X);
        Colored(Dim, end);
    }

    // Every sub in a Free Company as the shared table: each sub's target, the route it starts on, voyages and when it gets
    // there, and Stages to see its plan. As the app's LvFleet.
    private sealed record LvFleetRow(SubRow S, (long, string) Key, bool Top, int Target);
    private (PlannerGame G, LvResult? Result) lvFleetCtx;
    private FleetTable<LvFleetRow>? lvFleetTable;
    private Levelling.Plan? LvPlanOf(LvFleetRow r) => lvFleetCtx.Result?.Plans.FirstOrDefault(x => x.Sub.Key == r.Key).Plan;

    private FleetTable<LvFleetRow> LvFleetTable => lvFleetTable ??= new FleetTable<LvFleetRow>("levellingFleet", plugin.Configuration, new List<FleetColumn<LvFleetRow>>
    {
        new("sub", "Sub", r => { Ui.Text(r.S.SubName); ImGui.SameLine(); Colored(Dim, r.S.Character); }, r => r.S.SubName, Locked: true),
        new("target", "Level to", r =>
        {
            if (r.Top) { Colored(Dim, "top rank"); return; }
            Ui.Text($"{r.S.Rank} →"); ImGui.SameLine();
            ImGui.SetNextItemWidth(70 * ImGuiHelpers.GlobalScale);
            using var combo = ImRaii.Combo($"##lvT{r.S.Cid}{r.S.SubName}", r.Target.ToString(CultureInfo.CurrentCulture), ImGuiComboFlags.HeightLarge);
            if (combo) for (var rr = r.S.Rank + 1; rr <= lvFleetCtx.G.MaxRank; rr++) if (ImGui.Selectable($"{rr}##lvFT{r.S.Cid}{r.S.SubName}{rr}", rr == r.Target)) lvTargets[r.Key] = rr;
        }, r => r.Target),
        new("route", "Route now", r => { if (LvPlanOf(r)?.Stages.FirstOrDefault() is { } first) LvRoute(lvFleetCtx.G, first.Route); else Colored(Dim, "—"); }),
        new("voyages", "Voyages", r => TableCells.Right(LvPlanOf(r) is { } p ? PlannerN(p.Voyages) : "—"), r => LvPlanOf(r)?.Voyages ?? int.MaxValue),
        new("reaches", "Reaches it", r =>
        {
            var p = LvPlanOf(r);
            if (p == null) TableCells.Right("—");
            else if (p.Reached) TableCells.Right($"about {LvDays(p.Hours)} · {LvDate(LvStart(r.S).AddHours(p.Hours))}");
            else TableCells.Right($"stops at rank {p.StuckAt}", Amber);
        }, r => LvPlanOf(r) is { Reached: true } p ? p.Hours : double.MaxValue),
        new("stages", "Stages", r =>
        {
            if (r.Top || !Ui.SmallButton($"Stages##lvOpen{r.S.Cid}{r.S.SubName}")) return;
            var letters = r.S.Build.Split('-');
            if (letters.Length == 4 && letters.All(l => SubRoutes.PartClass.ContainsKey(l))) for (var i = 0; i < 4; i++) lvParts[i] = letters[i];
            lvChosen = r.Key;
            lvRank = r.S.Rank;
            lvExp = r.S.CurrentExp;
            lvTarget = lvTargets.TryGetValue(r.Key, out var tt2) && tt2 > r.S.Rank ? tt2 : null;
            lvEveryFc = false;
        }),
    }) { Title = "Levelling Plan, every sub" };

    // The stages as the shared table (columns to size, move, sort and choose, remembered), each part swap as an amber line
    // before the first stage that uses it while the table is in plan order. As the app's LvStages.
    private sealed record LvRow(Levelling.Stage St, int Index, List<int> Opened, string RouteLetters, IReadOnlyList<string> SwapLines);
    private PlannerGame? lvGame;
    private FleetTable<LvRow>? lvStageTable;
    // The rows of the last plan shown, made again only when the plan or the game data changes (not every frame).
    private (Levelling.Plan Plan, PlannerGame Game, List<LvRow> Rows)? lvRowsCache;

    private void LvStages(PlannerGame g, Levelling.Plan plan)
    {
        lvGame = g;
        var rows = lvRowsCache is { } c && ReferenceEquals(c.Plan, plan) && ReferenceEquals(c.Game, g) ? c.Rows : LvStageRows(g, plan);
        lvRowsCache = (plan, g, rows);
        LvStageTable.Before = r => r.SwapLines;
        LvStageTable.BeforeColor = Amber;
        LvStageTable.DrawControls();
        LvStageTable.Draw(rows, fitRows: true);
        if (LvStageTable.Sorted && plan.Swaps.Count > 0) Colored(Dim, "The part swaps show between the stages in plan order: Reset Sort goes back to it.");
    }

    private static List<LvRow> LvStageRows(PlannerGame g, Levelling.Plan plan)
    {
        string L(int id) => g.Sectors.TryGetValue(id, out var s) ? s.Location : "?";
        var swapsAt = Levelling.SwapsByStage(plan);
        return plan.Stages.Select((st, i) =>
        {
            var before = i > 0 ? plan.Stages[i - 1] : null;
            var opened = before == null ? new List<int>() : st.Route.Where(id => !before.Route.Contains(id) && g.Sectors.TryGetValue(id, out var s) && s.RankReq > before.FromRank).ToList();
            var lines = swapsAt[i].Select(x => $"At rank {x.ShowRank}: {LvSlots[x.Swap.Slot]} → {PlannerPartName(x.Swap.To)} {(x.Swap.Owned ? "(you have one)" : "(to get)")} → " +
                                               (x.Swap.Gain is double gain ? $"{PlannerN(gain * 100)}% more experience an hour, {LvDays(x.Swap.SavedHours ?? 0)} sooner in all"
                                                   : $"no route fits with the parts it has, {LvDays(plan.Hours)} to the target with this")).ToList();
            return new LvRow(st, i, opened, string.Concat(st.Route.Select(L)), lines);
        }).ToList();
    }

    private FleetTable<LvRow> LvStageTable => lvStageTable ??= new FleetTable<LvRow>("levellingStages", plugin.Configuration, new List<FleetColumn<LvRow>>
    {
        new("ranks", "Ranks", r => Ui.Text($"{r.St.FromRank} → {r.St.ToRank}"), r => r.St.FromRank, Locked: true),
        new("days", "Time", r => TableCells.Right(LvDays(r.St.Hours)), r => r.St.Hours),
        new("voyages", "Voyages", r => RightNumber(r.St.Voyages), r => r.St.Voyages),
        new("route", "Route", r =>
        {
            if (lvGame == null) return;
            LvRoute(lvGame, r.St.Route);
            foreach (var id in r.Opened)
            {
                ImGui.SameLine(0, 8 * ImGuiHelpers.GlobalScale);
                Tag($"{(lvGame.Sectors.TryGetValue(id, out var s) ? s.Location : "?")} from rank {s?.RankReq}", Blue);
            }
        }, r => r.RouteLetters),
        new("parts", "Parts", r =>
        {
            for (var j = 0; j < r.St.Parts.Length; j++)
            {
                if (j > 0) ImGui.SameLine(0, 4 * ImGuiHelpers.GlobalScale);
                OutlineTag(r.St.Parts[j], Blue);
                Hover($"{LvSlots[j]}: {PlannerPartName(r.St.Parts[j])}");
            }
        }, r => string.Join("", r.St.Parts)),
        new("voyage", "Voyage", r => TableCells.Right(PlannerHours(r.St.Hours / r.St.Voyages)), r => r.St.Hours / r.St.Voyages),
        new("expVoyage", "Exp a voyage", r => TableCells.Right(PlannerN(r.St.ExpPerVoyage)), r => r.St.ExpPerVoyage),
        new("expHour", "Exp an hour", r => TableCells.Right(PlannerN(r.St.ExpPerHour)), r => r.St.ExpPerHour),
        new("tanks", "Ceruleum", r => TableCells.Right(PlannerN(r.St.Tanks)), r => r.St.Tanks, Hidden: true),
        new("kits", "Repair kits", r => TableCells.Right(PlannerN(r.St.Kits)), r => r.St.Kits, Hidden: true),
        new("better", "Not discovered yet", r =>
        {
            if (r.St.Better is not { } b || lvGame == null) { Colored(Dim, "—"); return; }
            string L(int id) => lvGame.Sectors.TryGetValue(id, out var s) ? s.Location : "?";
            using (ImRaii.PushColor(ImGuiCol.Text, softColor))
                Ui.Wrapped($"{string.Join(", ", b.Missing.Select(L))} {(b.Missing.Count == 1 ? "isn't" : "aren't")} discovered yet: with {(b.Missing.Count == 1 ? "it" : "them")}, " +
                           $"{string.Join("-", b.Route.Select(L))} gives {PlannerN(b.Gain * 100)}% more an hour{(b.AtRank > r.St.FromRank ? $" from rank {b.AtRank}" : "")}.");
        }),
        new("planner", "Voyage Planner", r =>
        {
            if (Ui.SmallButton($"Open in Voyage Planner##lvOpen{r.Index}"))
            {
                plannerChosen = null;
                plannerRank = r.St.FromRank;
                for (var s = 0; s < 4; s++) plannerParts[s] = r.St.Parts[s];
                plannerRoute.Clear();
                plannerRoute.AddRange(r.St.Route);
                if (r.St.Route.Count > 0 && lvGame != null && lvGame.Sectors.TryGetValue(r.St.Route[0], out var first)) plannerMap = first.Map;
                shown = Tab.Planner;
            }
            Hover("Open this stage's build and route in Sub Calculator → Voyage Planner.");
        }),
    }) { Title = "Levelling Plan" };
}
