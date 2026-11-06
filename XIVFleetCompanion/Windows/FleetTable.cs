using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XIVFleetCompanion.Windows;

/// <summary>
/// One column of an in-game table: its key (saved in the settings), heading, how a row's cell is drawn, and what to
/// sort by (none: not sortable). Missing: rows with nothing to sort by, which go last either way. Locked: always shown.
/// Hidden: not shown until added with the Columns button.
/// </summary>
public sealed record FleetColumn<T>(string Key, string Label, Action<T> Draw, Func<T, IComparable>? SortKey = null,
    Func<T, bool>? Missing = null, bool Locked = false, bool Hidden = false);

/// <summary>
/// 2.x's tables, as the app's: click a heading to sort by it (again: the other way; a third time: back to the table's
/// own order), Reset Sort to go back at once, the Columns button to choose and order the columns (saved), and, where a
/// table has accounts, Group by account while it isn't sorted by a column. Rows come in the table's own order (the
/// Roster's for characters). Long tables only draw the rows on screen.
/// </summary>
public sealed class FleetTable<T>
{
    // Each column keeps the width it is given (fitted to its contents at first), rather than all of them stretching to
    // fill the window: so the last column's right edge can be dragged too, the room left over sits after it, and a
    // table wider than the window scrolls sideways.
    private const ImGuiTableFlags Flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX
        | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate;

    private readonly string id;
    private readonly Configuration config;
    private readonly IReadOnlyList<FleetColumn<T>> columns;
    private readonly Func<T, string?>? account;
    private readonly string countNoun;
    private readonly Func<IReadOnlyList<T>, string?>? groupNote;
    private readonly Func<T, string>? runKey;
    private bool resetSort;
    private bool sorted;

    /// <param name="id">The table's name in the settings (its columns and grouping are saved under it).</param>
    /// <param name="account">A row's account label, for Group by account (null: the table doesn't group).</param>
    /// <param name="countNoun">What a row is ("characters"), for each account's band: "Account 2 · 40 characters".</param>
    /// <param name="groupNote">Anything else the band says about the account's rows (null: nothing).</param>
    /// <param name="runKey">Which rows belong together (a character's subs): while the table isn't sorted by a column, a
    /// row with the same key as the one above has <see cref="SameAsAbove"/> set, so a column can leave out what it repeats.</param>
    public FleetTable(string id, Configuration config, IReadOnlyList<FleetColumn<T>> columns, Func<T, string?>? account = null,
        string countNoun = "", Func<IReadOnlyList<T>, string?>? groupNote = null, Func<T, string>? runKey = null)
    {
        this.runKey = runKey;
        this.id = id;
        this.config = config;
        this.columns = columns;
        this.account = account;
        this.countNoun = countNoun;
        this.groupNote = groupNote;
    }

    /// <summary>Whether a column sorts the table (as of the last frame drawn).</summary>
    public bool Sorted => sorted;

    /// <summary>While a row is drawn: whether it belongs with the row above (the same run key, the table not sorted).</summary>
    public bool SameAsAbove { get; private set; }

    private bool GroupByAccount
    {
        get => account != null && (!config.TableGroupByAccount.TryGetValue(id, out var on) || on);
        set { config.TableGroupByAccount[id] = value; config.Save(); }
    }

    /// <summary>The columns shown, in order: the saved choice, or the table's own (locked ones always).</summary>
    public List<FleetColumn<T>> Shown()
    {
        var list = new List<FleetColumn<T>>();
        if (config.TableColumns.TryGetValue(id, out var keys))
            list.AddRange(keys.Select(k => columns.FirstOrDefault(c => c.Key == k)).OfType<FleetColumn<T>>().Distinct());
        else
            list.AddRange(columns.Where(c => !c.Hidden));
        foreach (var locked in columns.Where(c => c.Locked && !list.Contains(c)).Reverse()) list.Insert(0, locked);
        return list;
    }

    private void Save(List<FleetColumn<T>> shown)
    {
        config.TableColumns[id] = shown.Select(c => c.Key).ToList();
        config.Save();
    }

    /// <summary>The table's buttons, on the current line: Group by account (while not sorted) or Reset Sort (while
    /// sorted), then Columns.</summary>
    public void DrawControls()
    {
        if (sorted)
        {
            if (ImGui.Button($"Reset Sort##{id}")) resetSort = true;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Back to the table's own order (the Roster's for characters).");
            ImGui.SameLine();
        }
        else if (account != null)
        {
            var group = GroupByAccount;
            if (ImGui.Checkbox($"Group by account##{id}", ref group)) GroupByAccount = group;
            ImGui.SameLine();
        }
        if (ImGui.Button($"Columns##{id}")) ImGui.OpenPopup($"columns##{id}");
        DrawColumnsPopup();
    }

    private void DrawColumnsPopup()
    {
        using var popup = ImRaii.Popup($"columns##{id}");
        if (!popup) return;
        var shown = Shown();
        List<FleetColumn<T>>? changed = null;
        ImGui.TextDisabled("Shown");
        for (var i = 0; i < shown.Count; i++)
        {
            var c = shown[i];
            using var rowId = ImRaii.PushId(c.Key);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Name(c.Label));
            ImGui.SameLine(180 * ImGuiHelpers.GlobalScale);
            using (ImRaii.Disabled(i == 0))
            {
                if (ImGuiComponents.IconButton("##up", FontAwesomeIcon.ArrowUp)) changed = Swap(shown, i, i - 1);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Move earlier");
            ImGui.SameLine();
            using (ImRaii.Disabled(i == shown.Count - 1))
            {
                if (ImGuiComponents.IconButton("##down", FontAwesomeIcon.ArrowDown)) changed = Swap(shown, i, i + 1);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Move later");
            ImGui.SameLine();
            using (ImRaii.Disabled(c.Locked))
            {
                if (ImGuiComponents.IconButton("##remove", FontAwesomeIcon.Times)) changed = shown.Where(x => x != c).ToList();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(c.Locked ? "This column always shows" : "Hide this column");
        }
        ImGui.Spacing();
        ImGui.TextDisabled("Add a column");
        var hidden = columns.Where(c => !shown.Contains(c)).ToList();
        if (hidden.Count == 0) ImGui.TextUnformatted("Every column is shown.");
        foreach (var c in hidden)
        {
            if (ImGui.Selectable($"+ {Name(c.Label)}##add{c.Key}", false, ImGuiSelectableFlags.DontClosePopups)) changed = shown.Append(c).ToList();
        }
        ImGui.Spacing();
        using (ImRaii.Disabled(!config.TableColumns.ContainsKey(id)))
        {
            if (ImGui.Button("Reset columns"))
            {
                config.TableColumns.Remove(id);
                config.Save();
            }
        }
        if (changed != null) Save(changed);
    }

    // A heading as the Columns list shows it: "A day##kits" (two headings alike, told apart) reads "A day (kits)".
    private static string Name(string label)
    {
        var i = label.IndexOf("##", StringComparison.Ordinal);
        return i < 0 ? label : $"{label[..i]} ({label[(i + 2)..]})";
    }

    private static List<FleetColumn<T>> Swap(List<FleetColumn<T>> shown, int a, int b)
    {
        var list = shown.ToList();
        (list[a], list[b]) = (list[b], list[a]);
        return list;
    }

    // One line of the table: a row, or an account's heading.
    private readonly record struct Line(T Row, string? Heading);

    /// <summary>Draws the table: rows in its own order, sorted by a clicked column or grouped by account.</summary>
    public void Draw(IReadOnlyList<T> rows, Action<T>? afterRow = null)
    {
        var shown = Shown();
        if (shown.Count == 0) return;
        // A table of its own for each choice of columns, so ImGui's saved widths always fit the columns shown ("|w":
        // widths saved before columns kept their own width were shares of the window, which don't carry over).
        // A little more room in each row, so long tables read easily.
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(ImGui.GetStyle().CellPadding.X + 2, ImGui.GetStyle().CellPadding.Y + 2) );
        using var table = ImRaii.Table($"##{id}|{string.Join(",", shown.Select(c => c.Key))}|w", shown.Count, Flags);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(1, 1);
        foreach (var c in shown) ImGui.TableSetupColumn(c.Label, c.SortKey == null ? ImGuiTableColumnFlags.NoSort : ImGuiTableColumnFlags.None);
        if (resetSort)
        {
            for (var i = 0; i < shown.Count; i++) ImGuiP.TableSetColumnSortDirection(i, ImGuiSortDirection.None, false);
            resetSort = false;
        }
        ImGui.TableHeadersRow();

        FleetColumn<T>? by = null;
        var ascending = true;
        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsCount > 0 && specs.Specs.ColumnIndex < shown.Count && specs.Specs.SortDirection != ImGuiSortDirection.None)
        {
            by = shown[specs.Specs.ColumnIndex];
            ascending = specs.Specs.SortDirection != ImGuiSortDirection.Descending;
        }
        sorted = by?.SortKey != null;

        List<Line> lines;
        if (by?.SortKey != null)
        {
            var byMissing = rows.OrderBy(r => by.Missing != null && by.Missing(r));
            lines = (ascending ? byMissing.ThenBy(by.SortKey) : byMissing.ThenByDescending(by.SortKey)).Select(r => new Line(r, null)).ToList();
        }
        else if (account != null && GroupByAccount)
        {
            lines = new List<Line>();
            foreach (var (label, groupRows) in FleetOrder.ByAccount(rows, account))
            {
                // The account's band: its name, how many rows, and anything the table adds ("synced on ...").
                var band = label;
                if (countNoun.Length > 0) band += $" · {groupRows.Count} {countNoun}";
                if (groupNote?.Invoke(groupRows) is { Length: > 0 } note) band += $" · {note}";
                lines.Add(new Line(default!, band));
                lines.AddRange(groupRows.Select(r => new Line(r, null)));
            }
        }
        else
        {
            lines = rows.Select(r => new Line(r, null)).ToList();
        }

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(lines.Count, -1f);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var line = lines[i];
                ImGui.TableNextRow();
                if (line.Heading != null)
                {
                    // A band in the look's accent across the whole row.
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.Header));
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.CheckMark)))
                        ImGui.TextUnformatted(line.Heading);
                    continue;
                }
                using var rowId = ImRaii.PushId(i);
                SameAsAbove = runKey != null && !sorted && i > 0 && lines[i - 1].Heading == null && runKey(lines[i - 1].Row) == runKey(line.Row);
                foreach (var c in shown)
                {
                    ImGui.TableNextColumn();
                    c.Draw(line.Row);
                }
                afterRow?.Invoke(line.Row);
            }
        }
        clipper.Destroy();
        SameAsAbove = false;
    }
}

/// <summary>Cells shared by the in-game tables: numbers on the right, and dots for counts.</summary>
public static class TableCells
{
    /// <summary>Text pushed to the right of its cell, so numbers line up (color: null for the usual).</summary>
    public static void Right(string text, Vector4? color = null)
    {
        var width = ImGui.CalcTextSize(text).X;
        var room = ImGui.GetContentRegionAvail().X;
        if (room > width) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room - width);
        if (color is Vector4 c)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, c)) ImGui.TextUnformatted(text);
        }
        else ImGui.TextUnformatted(text);
    }

    /// <summary>A thin bar, filled to fraction (0 to 1), centred on the line, in the look's accent on a frame-coloured
    /// track; then the cell goes on beside it.</summary>
    public static void Bar(double fraction, float width)
    {
        var height = ImGui.GetTextLineHeight();
        var thickness = Math.Max(3f, height * 0.22f);
        var start = ImGui.GetCursorScreenPos();
        var top = start.Y + (height - thickness) / 2;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(new Vector2(start.X, top), new Vector2(start.X + width, top + thickness), ImGui.GetColorU32(ImGuiCol.FrameBgActive), thickness / 2);
        var filled = (float)Math.Clamp(fraction, 0, 1) * width;
        if (filled > 0) draw.AddRectFilled(new Vector2(start.X, top), new Vector2(start.X + filled, top + thickness), ImGui.GetColorU32(ImGuiCol.CheckMark), thickness / 2);
        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>Filled and empty dots, filled out of total (for example subs out of the sub slots unlocked).</summary>
    public static void Pips(int filled, int total, Vector4 on, Vector4 off)
    {
        var height = ImGui.GetTextLineHeight();
        var radius = height * 0.27f;
        var step = radius * 2.7f;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        for (var i = 0; i < total; i++)
        {
            var center = new Vector2(start.X + radius + i * step, start.Y + height / 2);
            if (i < filled) draw.AddCircleFilled(center, radius, ImGui.GetColorU32(on));
            else draw.AddCircle(center, radius, ImGui.GetColorU32(off), 12, 1.5f);
        }
        ImGui.Dummy(new Vector2(Math.Max(1, total) * step, height));
    }
}
