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

    private sealed record LvSub((long Cid, string SubName) Key, int Rank, double Exp, int Target, string[] Parts, long Cid);
    private sealed record LvResult(bool Every, List<(LvSub Sub, Levelling.Plan Plan)> Plans);
    private string lvKey = "";
    private LvResult? lvResult;
    private Task<LvResult?>? lvWork;
    private string lvWorkKey = "";

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

    private string? LvFcOf(long cid) => reader.Parts.Data?.Inputs?.Characters?.FirstOrDefault(c => c.Cid == cid)?.FcId;

    private void DrawLevelling()
    {
        reader.ReadIfDue(withLists: true); // your subs, their next builds, Free Companies, and each FC's sectors
        var g = PlannerGameData();
        if (g == null) { Ui.Wrapped("The Levelling Plan needs the game's sub data, which couldn't be read."); return; }
        var data = SubCalculatorData.Shipped();
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
            ImGui.SetNextItemWidth(240 * scale);
            var f0 = lvFc == null ? null : fcs.FirstOrDefault(f => f.FcId == lvFc);
            using (var combo = ImRaii.Combo("##lvFc", f0 == null ? (fcs.Count == 0 ? "No Free Company yet" : "Choose…") : f0.Name + (f0.ReadAt == null ? " (not read yet)" : "")))
            {
                if (combo)
                    foreach (var f in fcs)
                        if (ImGui.Selectable($"{f.Name}{(f.ReadAt == null ? " (not read yet)" : "")}##lvFc{f.FcId}", f.FcId == lvFc)) lvFc = f.FcId;
            }
            ImGui.SameLine();
            Colored(Dim, f0?.ReadAt is double at ? $"only sectors {f0.Name} has discovered (read at the workshop {RfWhen(at)})" : "not read yet: every sector is allowed");
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
                asked.Add(new LvSub((s.Cid, s.SubName), s.Rank, s.CurrentExp, lvTargets.TryGetValue((s.Cid, s.SubName), out var t) ? t : LvDefaultTarget(s, g.MaxRank), letters, s.Cid));
            }
        }
        var fcRead = lvFc == null ? null : fcs.FirstOrDefault(f => f.FcId == lvFc);
        var kitCost = reader.Credits.Data?.RepairKitCost ?? 0;
        var key = $"{lvEveryFc}|{string.Join(";", asked.Select(a => $"{a.Key}|{a.Rank}|{a.Exp}|{a.Target}|{string.Join(",", a.Parts)}"))}|{lvMaxHours}|{lvSuggest}|{lvStandardOnly}|{lvFc}|{fcRead?.ReadAt}|{kitCost}";
        if (lvWork is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully) lvResult = done.Result;
            else Plugin.Log.Warning(done.Exception, "Levelling Plan: the working out failed");
            lvKey = lvWorkKey;
            lvWork = null;
        }
        if (key != lvKey && lvWork == null)
        {
            var (every, hours, suggest, standard) = (lvEveryFc, lvMaxHours, lvSuggest, lvStandardOnly);
            var read = fcRead?.ReadAt != null ? fcRead.Sectors : null;
            var inputs = reader.Parts.Data?.Inputs;
            var list = asked.ToList();
            lvWorkKey = key;
            lvWork = Task.Run<LvResult?>(() =>
            {
                var expToNext = g.Ranks.ToDictionary(kv => kv.Key, kv => kv.Value.ExpToNext);
                Levelling.Searches For(IReadOnlyDictionary<int, (bool Unlocked, bool Explored)>? r) =>
                    new(g.Sectors, g.Starts, g.Maps.Select(m => m.Id), g.Ranks, g.Parts, g.Game.Parts, data, id => ListingVendor((int)id), kitCost, r, hours);
                var search = For(read);
                var all = read != null ? For(null) : null;
                var plans = new List<(LvSub, Levelling.Plan)>();
                foreach (var s in list)
                {
                    var owned = suggest && inputs != null && s.Cid != 0 ? BuildFinder.Owned(inputs, s.Cid) : new HashSet<string>();
                    var plan = Levelling.LevellingPlan(s.Rank, s.Exp, s.Target, expToNext, s.Parts, search.BestAt, search.Score, suggest, standard, owned);
                    if (read != null) Levelling.MarkBetter(plan, all, read);
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
            }
            FcLine();

            // 2. The plan.
            ImGui.Spacing();
            HeadingRuns(("The plan", null), ($"   best experience at each rank, sent out again as soon as it's back{(working ? " · working out…" : "")}", Dim));
            Choices();
            if (lvRank >= g.MaxRank) { Ui.Wrapped("This sub is at the top rank."); return; }
            if (result?.Plans.FirstOrDefault() is not { } one) return;
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
            HeadingRuns(("Every sub in a Free Company", null), ($"   each to its next build's rank (else the top rank){(working ? " · working out…" : "")}", Dim));
            FcLine();
            Choices();
            if (fcSubs.Count == 0) { Ui.Wrapped("No subs in this Free Company."); return; }
            using var table = ImRaii.Table("##lvFleet", 6, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.SizingStretchProp);
            if (table)
            {
                foreach (var h in new[] { "Sub", "Level to", "Route now", "Voyages", "Reaches it", "" }) ImGui.TableSetupColumn(h);
                ImGui.TableHeadersRow();
                foreach (var s in fcSubs)
                {
                    var k = (s.Cid, s.SubName);
                    var p = result?.Plans.FirstOrDefault(x => x.Sub.Key == k).Plan;
                    var top = s.Rank >= g.MaxRank;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    Ui.Text(s.SubName); ImGui.SameLine(); Colored(Dim, s.Character);
                    ImGui.TableNextColumn();
                    if (top) Colored(Dim, "top rank");
                    else
                    {
                        var t = lvTargets.TryGetValue(k, out var tt) ? tt : LvDefaultTarget(s, g.MaxRank);
                        Ui.Text($"{s.Rank} →"); ImGui.SameLine();
                        ImGui.SetNextItemWidth(70 * scale);
                        using var combo = ImRaii.Combo($"##lvT{s.Cid}{s.SubName}", t.ToString(CultureInfo.CurrentCulture), ImGuiComboFlags.HeightLarge);
                        if (combo) for (var rr = s.Rank + 1; rr <= g.MaxRank; rr++) if (ImGui.Selectable($"{rr}##lvFT{s.Cid}{s.SubName}{rr}", rr == t)) lvTargets[k] = rr;
                    }
                    ImGui.TableNextColumn();
                    if (p?.Stages.FirstOrDefault() is { } first) LvRoute(g, first.Route); else Colored(Dim, "—");
                    ImGui.TableNextColumn();
                    TableCells.Right(p == null ? "—" : PlannerN(p.Voyages));
                    ImGui.TableNextColumn();
                    if (p == null) TableCells.Right("—");
                    else if (p.Reached) TableCells.Right($"about {LvDays(p.Hours)} · {LvDate(LvStart(s).AddHours(p.Hours))}");
                    else TableCells.Right($"stops at rank {p.StuckAt}", Amber);
                    ImGui.TableNextColumn();
                    if (!top && Ui.SmallButton($"Stages##lvOpen{s.Cid}{s.SubName}"))
                    {
                        var letters = s.Build.Split('-');
                        if (letters.Length == 4 && letters.All(l => SubRoutes.PartClass.ContainsKey(l))) for (var i = 0; i < 4; i++) lvParts[i] = letters[i];
                        lvChosen = k;
                        lvRank = s.Rank;
                        lvExp = s.CurrentExp;
                        lvTarget = lvTargets.TryGetValue(k, out var tt2) ? tt2 : null;
                        lvEveryFc = false;
                    }
                }
            }
        }
        ImGui.Spacing();
        Colored(Dim, "Experience with the bonus each sub is sure of (Pepper's guide); routes as the Route Finder ranks them for experience (up to 5 stops, any map); " +
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

    // A row per stage (its ranks and time, route, figures), with each part swap before the stage it starts.
    private void LvStages(PlannerGame g, Levelling.Plan plan)
    {
        var scale = ImGuiHelpers.GlobalScale;
        string L(int id) => g.Sectors.TryGetValue(id, out var s) ? s.Location : "?";
        using var table = ImRaii.Table("##lvStages", 3, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;
        ImGui.TableSetupColumn("##lvR", ImGuiTableColumnFlags.WidthFixed, 130 * scale);
        ImGui.TableSetupColumn("##lvM", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##lvB", ImGuiTableColumnFlags.WidthFixed, 120 * scale);
        for (var i = 0; i < plan.Stages.Count; i++)
        {
            var st = plan.Stages[i];
            foreach (var sw in plan.Swaps.Where(x => x.AtRank == st.FromRank))
            {
                ImGui.TableNextRow();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Amber with { W = 0.12f }));
                ImGui.TableNextColumn();
                Colored(Amber, $"At rank {sw.AtRank}");
                ImGui.TableNextColumn();
                Colored(Amber, $"{LvSlots[sw.Slot]} → {PlannerPartName(sw.To)}");
                ImGui.SameLine();
                Colored(Dim, sw.Owned ? "(you have one)" : "(to get)");
                ImGui.SameLine();
                Ui.Text($"{PlannerN(sw.Gain * 100)}% more experience an hour, {LvDays(sw.SavedHours)} sooner in all");
                ImGui.TableNextColumn();
            }
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Ui.Text($"{st.FromRank} → {st.ToRank}");
            Colored(Dim, $"{LvDays(st.Hours)} · {st.Voyages} voyage{(st.Voyages == 1 ? "" : "s")}");
            ImGui.TableNextColumn();
            LvRoute(g, st.Route);
            var before = i > 0 ? plan.Stages[i - 1] : null;
            if (before != null)
                foreach (var id in st.Route.Where(id => !before.Route.Contains(id) && g.Sectors.TryGetValue(id, out var s) && s.RankReq > before.FromRank))
                { ImGui.SameLine(0, 8 * scale); Tag($"{L(id)} from rank {g.Sectors[id].RankReq}", Blue); }
            if (plan.Swaps.Count > 0) foreach (var p in st.Parts) { ImGui.SameLine(0, 4 * scale); Tag(p, Dim); }
            Runs(($"{PlannerHours(st.Hours / st.Voyages)}", null), (" a voyage   ", Dim), ($"{PlannerN(st.ExpPerVoyage)}", null), (" exp a voyage   ", Dim),
                ($"{PlannerN(st.ExpPerHour)}", null), (" exp an hour", Dim));
            if (st.Better is { } b)
                Colored(softColor, $"{string.Join(", ", b.Missing.Select(L))} {(b.Missing.Count == 1 ? "isn't" : "aren't")} discovered yet: with {(b.Missing.Count == 1 ? "it" : "them")}, " +
                                   $"{string.Join("-", b.Route.Select(L))} gives {PlannerN((b.ExpPerHour / st.ExpPerHour - 1) * 100)}% more an hour.");
            ImGui.TableNextColumn();
            if (Ui.SmallButton($"Open in Planner##lvOpen{i}"))
            {
                plannerChosen = null;
                plannerRank = st.FromRank;
                for (var s = 0; s < 4; s++) plannerParts[s] = st.Parts[s];
                plannerRoute.Clear();
                plannerRoute.AddRange(st.Route);
                if (st.Route.Count > 0 && g.Sectors.TryGetValue(st.Route[0], out var first)) plannerMap = first.Map;
                shown = Tab.Planner;
            }
            Hover("Open this stage's build and route in Sub Calculator → Planner.");
        }
    }
}
