using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace XIVFleetCompanion.Windows;

// 2.x Trends stage: the Trends page, as the app's Trends tab (renderer/trends.jsx): any recorded value (gil, ceruleum,
// repair kits, retainers, subs, sub slots, FC points, salvage) for the whole fleet, or characters or Free Companies side
// by side, over 24 hours to all time; drag across the chart to zoom in, hover for the values, or see it as a table. Under
// the chart, each line's value now, its change over the span and a day on average, and its lowest and highest. Also: the
// Summary's small trend tiles, a chart at the top of the Roster, Salvage and Free Companies pages, and each character's
// or Free Company's own trends (the chart button beside its name). Read in the background (FleetReader.Trend), with the
// same chart query as the app; drawn here.
public partial class MainWindow
{
    // Each line keeps its colour (slot) when others are removed, as the app's.
    private static readonly Vector4[] TrendColors =
    {
        Hex(0x3987e5), Hex(0xd95926), Hex(0x199e70), Hex(0xc98500), Hex(0xd55181), Hex(0x008300), Hex(0x9085e9), Hex(0xe66767),
    };
    private static Vector4 Hex(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);

    private string trendScope = "fleet"; // fleet | characters | fcs

    // Values that are gil (gil, salvage value) show in gil's gold, as everywhere else.
    private Vector4? TrendValueColor(string metricKey) => metricKey is "gil" or "salvage_value" ? Palette.Gil : null;
    private readonly List<(string Id, int Slot)> trendSelection = new();
    private (double From, double To)? trendZoom;
    private bool trendTable;
    private string trendFind = "";
    // The character's or Free Company's own trends (the chart button beside a name): kind ("character" or "fc"), id and name.
    private (string Kind, string Id, string Name)? trendDetail;
    private int trendDetailDays = 90;
    private readonly Dictionary<string, float> chartDragFrom = new();
    private readonly Dictionary<string, int> cardDays = new();

    private string TrendMetricKey
    {
        get => plugin.Configuration.TrendsMetric ?? "gil";
        set { plugin.Configuration.TrendsMetric = value; plugin.Configuration.Save(); }
    }

    private string TrendRangeKey
    {
        get => TrendsMath.Ranges.Any(r => r.Key == plugin.Configuration.TrendsRange) ? plugin.Configuration.TrendsRange! : "30d";
        set { plugin.Configuration.TrendsRange = value; plugin.Configuration.Save(); }
    }

    /// <summary>Opens the Trends page on one value: for the whole fleet, or one character or Free Company ("characters" or "fcs").</summary>
    private void OpenTrends(string metricKey, string scope = "fleet", string? subjectId = null, string rangeKey = "30d")
    {
        TrendMetricKey = metricKey;
        TrendRangeKey = rangeKey;
        trendScope = subjectId != null && scope is "characters" or "fcs" ? scope : "fleet";
        trendSelection.Clear();
        if (subjectId != null && trendScope != "fleet") trendSelection.Add((subjectId, 0));
        trendZoom = null;
        trendDetail = null;
        Show(Tab.Trends);
    }

    /// <summary>Opens a character's or Free Company's own trends (kind: "character" or "fc").</summary>
    private void OpenTrendDetail(string kind, string id, string name)
    {
        trendDetail = (kind, id, name);
        Show(Tab.Trends);
    }

    // The small chart button beside a character's or Free Company's name.
    private void TrendButton(string kind, string id, string name)
    {
        ImGui.SameLine(0, ImGui.CalcTextSize(" ").X);
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.SmallButton($"{FontAwesomeIcon.ChartLine.ToIconString()}##trend{kind}{id}")) OpenTrendDetail(kind, id, name);
        }
        Hover($"Trends for {name}");
    }

    // ---- Building a chart's request ----

    // A chart of one value over `ms` up to now (or over the zoom), for the whole fleet or some characters or Free Companies.
    private static (TrendRequest? Request, string? Error) TrendAsk(TrendMetric metric, string scope, List<string>? ids, double from, double to, int points)
    {
        List<TrendPart>? parts;
        if (scope == "fleet") parts = metric.FleetParts;
        else
        {
            parts = metric.PartsFor(scope == "characters" || scope == "character" ? "character" : "fc");
            if (parts == null || ids == null || ids.Count == 0) return (null, null);
        }
        return TrendsMath.Check(from, to, points, parts, scope == "fleet" ? null : ids);
    }

    // ---- Drawing a chart ----

    private static string TrendTime(double ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().ToString("MMM d, yyyy, HH:mm", CultureInfo.CurrentCulture);

    // A time on the chart's axis, as short as the span allows.
    private static string TrendTick(double ms, double span)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime();
        if (span <= 2 * TrendsMath.Day) return t.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (span <= 400 * TrendsMath.Day) return t.ToString("MMM d", CultureInfo.CurrentCulture);
        return t.ToString("MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>Draws a chart: each line as steps (a value holds until the next one), the value axis on the left in short
    /// numbers, times along the bottom; a single line is shaded below. Hover for the values at that time; with zoomable,
    /// drag across to zoom into that span (returned once let go, at least 10 minutes).</summary>
    private (double From, double To)? TrendChart(string id, IReadOnlyList<double> timestamps, IReadOnlyList<(string Name, Vector4 Color, IReadOnlyList<double?> Values)> lines,
        float height, bool dimmed = false, bool zoomable = true)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(240 * scale, ImGui.GetContentRegionAvail().X);
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##chart{id}", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var released = ImGui.IsItemDeactivated();
        var after = ImGui.GetCursorScreenPos();
        if (timestamps.Count < 2 || lines.Count == 0) return null;

        var (low, high) = TrendsMath.Range(lines.SelectMany(l => l.Values));
        var ticks = TrendsMath.NiceTicks(low, high, 4);
        var labelWidth = ticks.Select(t => ImGui.CalcTextSize(TrendsMath.Compact(t)).X).DefaultIfEmpty(0).Max() + 8 * scale;
        var lineHeight = ImGui.GetTextLineHeight();
        var left = start.X + labelWidth;
        var top = start.Y + lineHeight / 2;
        var right = start.X + width - 8 * scale;
        var bottom = start.Y + height - lineHeight - 6 * scale;
        var from = timestamps[0];
        var to = timestamps[^1];
        float X(double t) => (float)(left + (t - from) / (to - from) * (right - left));
        float Y(double v) => (float)(bottom - (v - low) / (high - low) * (bottom - top));
        var draw = ImGui.GetWindowDrawList();
        var alpha = dimmed ? 0.5f : 1f;
        var grid = ImGui.GetColorU32(new Vector4(Dim.X, Dim.Y, Dim.Z, 0.18f));
        var muted = ImGui.GetColorU32(new Vector4(Dim.X, Dim.Y, Dim.Z, 1f));

        // The value axis: round values, with faint lines across.
        foreach (var t in ticks)
        {
            var y = Y(t);
            draw.AddLine(new Vector2(left, y), new Vector2(right, y), grid, 1);
            var text = TrendsMath.Compact(t);
            draw.AddText(new Vector2(left - 6 * scale - ImGui.CalcTextSize(text).X, y - lineHeight / 2), muted, text);
        }
        // The time axis: about one label per 110 pixels.
        var count = Math.Clamp((int)((right - left) / (110 * scale)), 2, 8);
        for (var k = 0; k <= count; k++)
        {
            var t = from + (to - from) * k / count;
            var text = TrendTick(t, to - from);
            var w = ImGui.CalcTextSize(text).X;
            var x = Math.Max(left, Math.Min(X(t) - w / 2, right - w));
            draw.AddText(new Vector2(x, bottom + 4 * scale), muted, text);
        }

        // The lines, as steps; a gap where nothing was recorded yet.
        draw.PushClipRect(new Vector2(left, top - 2), new Vector2(right + 1, bottom + 1), true);
        foreach (var (_, color, values) in lines)
        {
            var c = ImGui.GetColorU32(color with { W = alpha });
            var shade = ImGui.GetColorU32(color with { W = 0.1f * alpha });
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is not double v) continue;
                var x0 = X(timestamps[i]);
                var x1 = i + 1 < values.Count ? X(timestamps[i + 1]) : x0;
                var y0 = Y(v);
                if (lines.Count == 1 && x1 > x0) draw.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, bottom), shade);
                if (x1 > x0) draw.AddLine(new Vector2(x0, y0), new Vector2(x1, y0), c, 2 * scale);
                if (i + 1 < values.Count && values[i + 1] is double next) draw.AddLine(new Vector2(x1, y0), new Vector2(x1, Y(next)), c, 2 * scale);
            }
        }
        draw.PopClipRect();

        var mouse = ImGui.GetIO().MousePos;
        // Dragging across: the span chosen, shaded.
        if (zoomable && active)
        {
            if (!chartDragFrom.ContainsKey(id)) chartDragFrom[id] = Math.Clamp(mouse.X, left, right);
            var a = chartDragFrom[id];
            var b = Math.Clamp(mouse.X, left, right);
            draw.AddRectFilled(new Vector2(Math.Min(a, b), top), new Vector2(Math.Max(a, b), bottom), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.08f)));
        }
        (double, double)? zoomTo = null;
        if (zoomable && released && chartDragFrom.Remove(id, out var dragFrom))
        {
            var dragTo = Math.Clamp(mouse.X, left, right);
            if (Math.Abs(dragTo - dragFrom) >= 8 * scale)
            {
                double T(float x) => from + (x - left) / (right - left) * (to - from);
                var (z0, z1) = (T(Math.Min(dragFrom, dragTo)), T(Math.Max(dragFrom, dragTo)));
                if (z1 - z0 >= 10 * 60 * 1000) zoomTo = (Math.Round(z0), Math.Round(z1));
            }
        }

        // Hovering: a line at that time, a dot on each line, and the values.
        if (hovered && mouse.X >= left && mouse.X <= right)
        {
            var index = 0;
            for (var i = 1; i < timestamps.Count; i++) if (Math.Abs(X(timestamps[i]) - mouse.X) < Math.Abs(X(timestamps[index]) - mouse.X)) index = i;
            var x = X(timestamps[index]);
            draw.AddLine(new Vector2(x, top), new Vector2(x, bottom), muted, 1);
            foreach (var (_, color, values) in lines)
                if (values[index] is double v)
                {
                    draw.AddCircleFilled(new Vector2(x, Y(v)), 5 * scale, ImGui.GetColorU32(ImGuiCol.WindowBg));
                    draw.AddCircleFilled(new Vector2(x, Y(v)), 3.5f * scale, ImGui.GetColorU32(color));
                }
            using (ImRaii.Tooltip())
            {
                ImGui.TextUnformatted(TrendTime(timestamps[index]));
                foreach (var (name, color, values) in lines)
                {
                    Colored(color, "■");
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{TrendsMath.Full(values[index])}  {name}");
                }
                if (zoomable) Colored(Dim, "Drag across the chart to zoom into that span.");
            }
        }
        ImGui.SetCursorScreenPos(after);
        return zoomTo;
    }

    // A tiny step line with no axes, and a dot at its end (the Summary's tiles).
    private static void Sparkline(IReadOnlyList<double?> values, float width, float height, Vector4 color)
    {
        var start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, height));
        var known = values.Select((v, i) => (I: i, V: v)).Where(p => p.V != null).ToList();
        if (known.Count < 2) return;
        var min = known.Min(p => p.V!.Value);
        var max = known.Max(p => p.V!.Value);
        float X(int i) => start.X + 4 + i * (width - 8) / Math.Max(1, values.Count - 1);
        float Y(double v) => max == min ? start.Y + height / 2 : (float)(start.Y + height - 6 - (v - min) * (height - 12) / (max - min));
        var draw = ImGui.GetWindowDrawList();
        var c = ImGui.GetColorU32(color);
        for (var k = 1; k < known.Count; k++)
        {
            var (i0, v0) = known[k - 1];
            var (i1, v1) = known[k];
            draw.AddLine(new Vector2(X(i0), Y(v0!.Value)), new Vector2(X(i1), Y(v0.Value)), c, 2);
            draw.AddLine(new Vector2(X(i1), Y(v0.Value)), new Vector2(X(i1), Y(v1!.Value)), c, 2);
        }
        var (li, lv) = known[^1];
        draw.AddCircleFilled(new Vector2(X(li), Y(lv!.Value)), 5, ImGui.GetColorU32(ImGuiCol.WindowBg));
        draw.AddCircleFilled(new Vector2(X(li), Y(lv.Value)), 3.5f, c);
    }

    // "▲ 12.5%" (or the change itself from nothing), green up, amber down.
    private void ChangeText(TrendStats s)
    {
        if (s.Change is not double change) return;
        var arrow = change > 0 ? "▲" : change < 0 ? "▼" : "▬";
        var text = s.Percent is double pct ? $"{arrow} {Math.Abs(pct):0.0}%" : $"{arrow} {TrendsMath.Full(Math.Abs(change))}";
        Colored(change > 0 ? Green : change < 0 ? Amber : Dim, text);
    }

    private static string Signed(double? n) => n is double v ? (v > 0 ? "+" : "") + TrendsMath.Full(v) : "—";

    // Under a chart: each line's value now, its change over the span (and as a share of the start), a day on average,
    // and its lowest and highest.
    private void TrendStatsTable(string id, IReadOnlyList<double> timestamps, IReadOnlyList<(string Name, Vector4 Color, IReadOnlyList<double?> Values)> lines)
    {
        using var table = ImRaii.Table($"##trendStats{id}", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
        if (!table) return;
        foreach (var label in new[] { lines.Count == 1 ? "" : "Line", "Now", "Change", "Change %", "A day on average", "Lowest", "Highest" }) ImGui.TableSetupColumn(label);
        ImGui.TableHeadersRow();
        foreach (var (name, color, values) in lines)
        {
            var s = TrendsMath.Stats(values, timestamps);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Colored(color, "■");
            ImGui.SameLine();
            ImGui.TextUnformatted(lines.Count == 1 && name.Length == 0 ? "" : name);
            ImGui.TableNextColumn();
            TableCells.Right(TrendsMath.Full(s.Current));
            ImGui.TableNextColumn();
            TableCells.Right(Signed(s.Change), s.Change > 0 ? Green : s.Change < 0 ? Amber : null);
            ImGui.TableNextColumn();
            TableCells.Right(s.Percent is double p ? $"{(p > 0 ? "+" : "")}{p:0.0}%" : "—", s.Change > 0 ? Green : s.Change < 0 ? Amber : null);
            ImGui.TableNextColumn();
            TableCells.Right(Signed(s.PerDay));
            Hover("The change over this span, divided by the days between the first and last recorded values shown.");
            ImGui.TableNextColumn();
            TableCells.Right(TrendsMath.Full(s.Low));
            ImGui.TableNextColumn();
            TableCells.Right(TrendsMath.Full(s.High));
        }
    }

    // ---- The Trends page ----

    private void DrawTrends()
    {
        reader.ReadTrendsIfDue();
        using (ImRaii.Disabled(reader.Busy))
        {
            if (ImGui.Button("Refresh##trends")) reader.ForgetTrends();
        }
        ReadLine(reader.Trends, FleetReader.TrendsEvery);
        if (reader.Trends.Data is not { } meta)
        {
            if (reader.Trends.Error == null) ImGui.TextUnformatted("Reading what's been recorded...");
            return;
        }
        if (trendDetail is { } detail)
        {
            DrawTrendDetail(detail, meta);
            return;
        }

        var metric = TrendsMath.Metric(TrendMetricKey);
        var scale = ImGuiHelpers.GlobalScale;
        // Value, whose, the span and chart or table, as the app's.
        ImGui.SetNextItemWidth(190 * scale);
        using (var combo = ImRaii.Combo("Value##trendMetric", metric.Label))
        {
            if (combo)
                foreach (var m in TrendsMath.Metrics)
                    if (ImGui.Selectable(m.Label, m.Key == metric.Key))
                    {
                        TrendMetricKey = m.Key;
                        var type = trendScope == "characters" ? "character" : trendScope == "fcs" ? "fc" : null;
                        if (type != null && m.PartsFor(type) == null) PickTrendScope("fleet", meta);
                        trendZoom = null;
                    }
        }
        metric = TrendsMath.Metric(TrendMetricKey);
        ImGui.SameLine(0, 16 * scale);
        ImGui.SetNextItemWidth(210 * scale);
        var scopeLabel = trendScope == "characters" ? "Characters (compare)" : trendScope == "fcs" ? "Free Companies (compare)" : "Whole fleet (total)";
        using (var combo = ImRaii.Combo("Show##trendScope", scopeLabel))
        {
            if (combo)
            {
                if (ImGui.Selectable("Whole fleet (total)", trendScope == "fleet")) PickTrendScope("fleet", meta);
                if (metric.Character != null && ImGui.Selectable("Characters (compare)", trendScope == "characters")) PickTrendScope("characters", meta);
                if (metric.Fc != null && ImGui.Selectable("Free Companies (compare)", trendScope == "fcs")) PickTrendScope("fcs", meta);
            }
        }
        foreach (var r in TrendsMath.Ranges)
        {
            ImGui.SameLine(0, r == TrendsMath.Ranges[0] ? 16 * scale : 2 * scale);
            var on = trendZoom == null && TrendRangeKey == r.Key;
            using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive], on))
            {
                if (ImGui.Button($"{r.Label}##trendRange"))
                {
                    TrendRangeKey = r.Key;
                    trendZoom = null;
                }
            }
        }
        ImGui.SameLine(0, 16 * scale);
        if (ImGui.Button(trendTable ? "Show as chart##trendView" : "Show as table##trendView")) trendTable = !trendTable;

        // Whose, in compare: each with its colour, and a list to add more from.
        var items = trendScope == "characters" ? meta.Characters.Select(c => (c.Id, c.Label)).ToList()
            : trendScope == "fcs" ? meta.Fcs.Select(f => (f.Id, f.Label)).ToList() : new List<(string Id, string Label)>();
        string LabelOf(string id) => items.FirstOrDefault(x => x.Id == id).Label ?? id;
        if (trendScope != "fleet") DrawTrendPicker(items);
        if (trendScope == "characters" && metric.Key.StartsWith("salvage", StringComparison.Ordinal))
            Colored(Dim, "Character totals cover bags and retainers. Free Company chests are counted under Free Companies.");

        // The request: the span chosen (All time: from the first time this value was recorded), or the zoom.
        var range = TrendsMath.Ranges.First(r => r.Key == TrendRangeKey);
        var now = NowMs;
        var firstRecorded = TrendsMath.FirstRecorded(metric, meta.Catalog);
        var (from, to) = trendZoom ?? (range.Ms is double ms ? (now - ms, now) : (firstRecorded ?? now - 30 * TrendsMath.Day, now));
        var (request, error) = TrendAsk(metric, trendScope, trendSelection.Select(s => s.Id).ToList(), from, to, trendZoom != null ? 150 : range.Points);
        if (trendZoom is { } z)
        {
            Colored(Dim, $"Showing {TrendTime(z.From)} to {TrendTime(z.To)}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset zoom")) trendZoom = null;
        }
        if (error != null) Colored(Amber, $"Could not chart this: {error}");
        if (request == null)
        {
            if (trendScope != "fleet") ImGui.TextUnformatted("Pick at least one to chart.");
            return;
        }
        var (result, readError, loading) = reader.Trend("trends", request);
        if (readError != null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                ImGui.TextWrapped($"Could not read this chart: {readError}");
        }
        if (result == null)
        {
            if (loading) ImGui.TextUnformatted("Reading...");
            return;
        }
        var lines = trendScope == "fleet"
            ? new List<(string, Vector4, IReadOnlyList<double?>)> { ("Whole fleet", TrendColors[0], result.Series[0].Values) }
            : trendSelection.Select(s => (LabelOf(s.Id), TrendColors[s.Slot % TrendColors.Length],
                (IReadOnlyList<double?>)(result.Series.FirstOrDefault(x => x.SubjectId == s.Id)?.Values ?? Enumerable.Repeat<double?>(null, result.Timestamps.Count).ToList()))).ToList();
        if (!lines.Any(l => l.Item3.Any(v => v != null)))
        {
            ImGui.TextWrapped("No history recorded for this yet in this time span." + (firstRecorded is double first ? $" Recording of this value began {TrendTime(first)}." : ""));
            return;
        }

        if (loading)
        {
            ImGui.SameLine();
            Colored(Dim, "Updating...");
        }
        if (lines.Count == 1)
        {
            var s = TrendsMath.Stats(lines[0].Item3, result.Timestamps);
            ImGui.Spacing();
            Runs(T("Now "), T(TrendsMath.Full(s.Current), TrendValueColor(metric.Key)));
            ImGui.SameLine(0, 24 * scale);
            ImGui.TextUnformatted($"Change over this span {Signed(s.Change)}");
            ImGui.SameLine();
            ChangeText(s);
            ImGui.SameLine(0, 24 * scale);
            ImGui.TextUnformatted($"A day on average {Signed(s.PerDay)}");
        }
        if (trendTable) DrawTrendTable(result, lines);
        else
        {
            var picked = TrendChart("trends", result.Timestamps, lines, 320 * scale, loading);
            if (picked != null) trendZoom = picked;
            Colored(Dim, "Drag across the chart to zoom into that span; hover for the values.");
        }
        if (lines.Count > 1) TrendStatsTable("trends", result.Timestamps, lines);
    }

    private void PickTrendScope(string scope, TrendsMeta meta)
    {
        trendScope = scope;
        trendSelection.Clear();
        var first = scope == "characters" ? meta.Characters.FirstOrDefault()?.Id : scope == "fcs" ? meta.Fcs.FirstOrDefault()?.Id : null;
        if (first != null) trendSelection.Add((first, 0));
        trendZoom = null;
    }

    // Who's charted (each in its colour, with a button to take it off) and a searchable list to add up to 8.
    private void DrawTrendPicker(List<(string Id, string Label)> items)
    {
        foreach (var (id, slot) in trendSelection.ToList())
        {
            Colored(TrendColors[slot % TrendColors.Length], "■");
            ImGui.SameLine();
            ImGui.TextUnformatted(items.FirstOrDefault(x => x.Id == id).Label ?? id);
            ImGui.SameLine();
            if (ImGui.SmallButton($"x##trendRemove{id}")) trendSelection.RemoveAll(s => s.Id == id);
            Hover("Take this off the chart");
            ImGui.SameLine(0, 14 * ImGuiHelpers.GlobalScale);
        }
        ImGui.NewLine();
        if (trendSelection.Count >= TrendsMath.MaxSeries)
        {
            Colored(Dim, $"Up to {TrendsMath.MaxSeries} at a time. Take one off to add another.");
            return;
        }
        var noun = trendScope == "characters" ? "a character" : "a Free Company";
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo("##trendAdd", $"Add {noun}...");
        if (!combo) return;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##trendFind", ref trendFind, 64);
        var chosen = trendSelection.Select(s => s.Id).ToHashSet();
        var shown = 0;
        foreach (var (id, label) in items)
        {
            if (chosen.Contains(id) || !Matches(trendFind, label)) continue;
            if (++shown > 60) break;
            if (ImGui.Selectable($"{label}##trendPick{id}"))
            {
                var used = trendSelection.Select(s => s.Slot).ToHashSet();
                var slot = 0;
                while (used.Contains(slot)) slot++;
                trendSelection.Add((id, slot));
                trendFind = "";
            }
        }
        if (shown == 0) Colored(Dim, "Nothing matches.");
    }

    // The chart as a table: each time and every line's value then.
    private void DrawTrendTable(TrendResult result, List<(string Name, Vector4 Color, IReadOnlyList<double?> Values)> lines)
    {
        var height = 320 * ImGuiHelpers.GlobalScale;
        using var table = ImRaii.Table("##trendTable", lines.Count + 1, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, height));
        if (!table) return;
        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableSetupColumn("Time");
        foreach (var l in lines) ImGui.TableSetupColumn(l.Name);
        ImGui.TableHeadersRow();
        for (var i = 0; i < result.Timestamps.Count; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(TrendTime(result.Timestamps[i]));
            foreach (var l in lines)
            {
                ImGui.TableNextColumn();
                TableCells.Right(TrendsMath.Full(l.Values[i]), l.Values[i] == null ? Dim : null);
            }
        }
    }

    // ---- A compact chart (the cards on other pages and a character's or Free Company's own trends) ----

    /// <summary>One value for the whole fleet, a character ("character") or a Free Company ("fc") over `days`: its value
    /// now and change, the chart, and a button to open it on the Trends page. With ownDays, its own 7 / 30 / 90 days.</summary>
    private void TrendCard(string title, string metricKey, string scope, string? subjectId, int days, float height, bool ownDays)
    {
        var key = $"{metricKey}|{scope}|{subjectId}";
        if (ownDays) days = cardDays.TryGetValue(key, out var chosenDays) ? chosenDays : days;
        var metric = TrendsMath.Metric(metricKey);
        var now = NowMs;
        var (request, _) = TrendAsk(metric, scope, subjectId == null ? null : new List<string> { subjectId }, now - days * TrendsMath.Day, now, 120);
        ImGui.TextUnformatted(title);
        if (request == null)
        {
            Colored(Dim, "Not recorded for this.");
            return;
        }
        var (result, error, loading) = reader.Trend($"card:{key}", request);
        var values = result?.Series.FirstOrDefault()?.Values;
        var stats = values != null ? TrendsMath.Stats(values, result!.Timestamps) : null;
        if (stats?.Current != null)
        {
            ImGui.SameLine(0, 14 * ImGuiHelpers.GlobalScale);
            Tinted(TrendValueColor(metricKey), TrendsMath.Full(stats.Current));
            ImGui.SameLine();
            ChangeText(stats);
        }
        if (ownDays)
            foreach (var d in new[] { 7, 30, 90 })
            {
                ImGui.SameLine(0, d == 7 ? 14 * ImGuiHelpers.GlobalScale : 2);
                using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive], days == d))
                {
                    if (ImGui.SmallButton($"{d} d##card{key}{d}")) cardDays[key] = d;
                }
            }
        ImGui.SameLine(0, 14 * ImGuiHelpers.GlobalScale);
        if (ImGui.SmallButton($"Open in Trends##card{key}"))
            OpenTrends(metricKey, scope == "character" ? "characters" : scope == "fc" ? "fcs" : "fleet", subjectId, days >= 365 ? "1y" : days >= 90 ? "90d" : days >= 30 ? "30d" : "7d");
        if (error != null) Colored(Amber, $"Could not read this chart: {error}");
        else if (result == null) Colored(Dim, "Reading...");
        else if (stats?.Current == null) Colored(Dim, "No history recorded for this yet.");
        else TrendChart($"card{key}", result.Timestamps, new List<(string, Vector4, IReadOnlyList<double?>)> { (title, TrendColors[0], values!) }, height, loading, zoomable: false);
    }

    /// <summary>A chart at the top of a page, folded away until opened (remembered).</summary>
    private void TrendCardSection(string title, string metricKey)
    {
        var config = plugin.Configuration;
        var key = $"page:{metricKey}";
        var open = config.TrendCardsOpen.TryGetValue(key, out var kept) && kept;
        ImGui.SetNextItemOpen(open);
        var nowOpen = ImGui.CollapsingHeader($"{title}##trendCard{metricKey}");
        if (nowOpen != open)
        {
            config.TrendCardsOpen[key] = nowOpen;
            config.Save();
        }
        if (!nowOpen) return;
        reader.ReadTrendsIfDue();
        TrendCard(title, metricKey, "fleet", null, 30, 150 * ImGuiHelpers.GlobalScale, ownDays: true);
        ImGui.Spacing();
    }

    // A character's or Free Company's own trends: each of its values over 30 days, 90 days or a year.
    private void DrawTrendDetail((string Kind, string Id, string Name) detail, TrendsMeta meta)
    {
        if (ImGui.Button("Back to Trends##trendDetail")) trendDetail = null;
        ImGui.SameLine(0, 16 * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted($"{detail.Name}: {(detail.Kind == "character" ? "character" : "Free Company")} trends");
        foreach (var (d, label) in new[] { (30, "30 days"), (90, "90 days"), (365, "1 year") })
        {
            ImGui.SameLine(0, d == 30 ? 16 * ImGuiHelpers.GlobalScale : 2);
            using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive], trendDetailDays == d))
            {
                if (ImGui.Button($"{label}##trendDetailDays")) trendDetailDays = d;
            }
        }
        var cards = detail.Kind == "character"
            ? new[] { ("Gil", "gil"), ("Ceruleum", "ceruleum"), ("Repair kits", "repair_kits"), ("FC points (this character leads the FC)", "fc_points"), ("Salvage value, bags and retainers (gil)", "salvage_value") }
            : new[] { ("FC points", "fc_points"), ("Salvage value in the FC chest (gil)", "salvage_value"), ("Salvage items in the FC chest", "salvage_items") };
        // Only what's recorded for it.
        var recorded = meta.Catalog.Where(r => r.SubjectType == detail.Kind).Select(r => r.Metric).ToHashSet();
        using var table = ImRaii.Table("##trendDetailCards", 2, ImGuiTableFlags.SizingStretchSame);
        if (!table) return;
        foreach (var (title, key) in cards)
        {
            var metric = TrendsMath.Metric(key);
            if (metric.PartsFor(detail.Kind)?.Any(p => recorded.Contains(p.Metric)) != true) continue;
            ImGui.TableNextColumn();
            TrendCard(title, key, detail.Kind, detail.Id, trendDetailDays, 150 * ImGuiHelpers.GlobalScale, ownDays: false);
            ImGui.Spacing();
        }
    }

    // ---- The Summary's tiles ----

    private static readonly string[] TileKeys = { "gil", "ceruleum", "repair_kits", "fc_points", "salvage_value" };

    /// <summary>A small trend line for each fleet-wide value over the last 30 days, with its value now and change, and a
    /// button to open it on the Trends page. The sub values only when the fleet has subs.</summary>
    private void DrawTrendTiles()
    {
        reader.ReadTrendsIfDue();
        var hasSubs = reader.Subs.Data is { Count: > 0 };
        var metrics = TileKeys.Select(TrendsMath.Metric).Where(m => hasSubs || !m.ForSubs).ToList();
        Heading("Trends, last 30 days");
        if (reader.Trends.Data == null)
        {
            Colored(Dim, reader.Trends.Error != null ? $"Trends are unavailable right now: {reader.Trends.Error}" : "Reading...");
            return;
        }
        // The span moves on by the minute, so the charts are read again then, not every frame.
        var now = Math.Floor(NowMs / 60000) * 60000;
        using var table = ImRaii.Table("##trendTiles", metrics.Count, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV);
        if (!table) return;
        foreach (var metric in metrics)
        {
            ImGui.TableNextColumn();
            Colored(Dim, metric.Label);
            var (request, _) = TrendAsk(metric, "fleet", null, now - 30 * TrendsMath.Day, now, 30);
            var (result, _, _) = request != null ? reader.Trend($"tile:{metric.Key}", request) : (null, null, false);
            var values = result?.Series.FirstOrDefault()?.Values;
            var stats = values != null ? TrendsMath.Stats(values, result!.Timestamps) : null;
            Tinted(TrendValueColor(metric.Key), TrendsMath.Full(stats?.Current));
            if (stats?.Change != null) ChangeText(stats);
            else Colored(Dim, result == null ? " " : "No history yet");
            Sparkline(values ?? new List<double?>(), ImGui.GetContentRegionAvail().X, 40 * ImGuiHelpers.GlobalScale, TrendColors[0]);
            if (ImGui.SmallButton($"View trend##tile{metric.Key}")) OpenTrends(metric.Key);
        }
    }
}
