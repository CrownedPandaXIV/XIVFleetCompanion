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
    private const ImGuiTableFlags Flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
        | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate;

    private readonly string id;
    private readonly Configuration config;
    private readonly IReadOnlyList<FleetColumn<T>> columns;
    private readonly Func<T, string?>? account;
    private bool resetSort;
    private bool sorted;

    /// <param name="id">The table's name in the settings (its columns and grouping are saved under it).</param>
    /// <param name="account">A row's account label, for Group by account (null: the table doesn't group).</param>
    public FleetTable(string id, Configuration config, IReadOnlyList<FleetColumn<T>> columns, Func<T, string?>? account = null)
    {
        this.id = id;
        this.config = config;
        this.columns = columns;
        this.account = account;
    }

    /// <summary>Whether a column sorts the table (as of the last frame drawn).</summary>
    public bool Sorted => sorted;

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
        // A table of its own for each choice of columns, so ImGui's saved widths always fit the columns shown.
        using var table = ImRaii.Table($"##{id}|{string.Join(",", shown.Select(c => c.Key))}", shown.Count, Flags);
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
                lines.Add(new Line(default!, label));
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
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableHeaderBg));
                    ImGui.TextUnformatted(line.Heading);
                    continue;
                }
                using var rowId = ImRaii.PushId(i);
                foreach (var c in shown)
                {
                    ImGui.TableNextColumn();
                    c.Draw(line.Row);
                }
                afterRow?.Invoke(line.Row);
            }
        }
        clipper.Destroy();
    }
}
