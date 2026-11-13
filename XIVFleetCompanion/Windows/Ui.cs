using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace XIVFleetCompanion.Windows;

/// <summary>
/// The window's text, ticks, boxes and tables. Each draws as ImGui does, except while a table is being read for Copy
/// table or Export to CSV (TableCapture): then it adds what it would show to its cell and draws nothing.
/// </summary>
internal static class Ui
{
    private static TableCapture? Capture => TableCapture.Current;

    public static void Text(string text)
    {
        if (Capture is { } c) c.Add(text);
        else ImGui.TextUnformatted(text);
    }

    public static void Wrapped(string text)
    {
        if (Capture is { } c) c.Add(text);
        else ImGui.TextWrapped(text);
    }

    public static void Disabled(string text)
    {
        if (Capture is { } c) c.Add(text);
        else ImGui.TextDisabled(text);
    }

    public static void Bullet(string text)
    {
        if (Capture is { } c) c.Add(text);
        else ImGui.BulletText(text);
    }

    public static bool Checkbox(string label, ref bool value)
    {
        if (Capture is { } c)
        {
            c.Add(value ? "Yes" : "No");
            return false;
        }
        return ImGui.Checkbox(label, ref value);
    }

    public static bool Selectable(string label, bool selected = false, ImGuiSelectableFlags flags = ImGuiSelectableFlags.None, Vector2 size = default)
    {
        if (Capture is { } c)
        {
            c.Add(TableCapture.Visible(label));
            return false;
        }
        return ImGui.Selectable(label, selected, flags, size);
    }

    public static bool InputInt(string label, ref int value, int step = 1, int stepFast = 100)
    {
        if (Capture is { } c)
        {
            c.Add(value.ToString(CultureInfo.InvariantCulture));
            return false;
        }
        return ImGui.InputInt(label, ref value, step, stepFast);
    }

    public static ImRaii.IEndObject Combo(string label, string preview, ImGuiComboFlags flags = ImGuiComboFlags.None)
    {
        if (Capture is { } c)
        {
            c.Add(preview);
            return Ended.Closed;
        }
        return ImRaii.Combo(label, preview, flags);
    }

    public static ImRaii.IEndObject Table(string id, int columns, ImGuiTableFlags flags = ImGuiTableFlags.None, Vector2 outerSize = default)
    {
        if (Capture != null) return Ended.Open;
        return ImRaii.Table(id, columns, flags, outerSize);
    }

    public static void SetupColumn(string label, ImGuiTableColumnFlags flags = ImGuiTableColumnFlags.None, float width = 0)
    {
        if (Capture is { } c) c.Header(label);
        else ImGui.TableSetupColumn(label, flags, width);
    }

    public static void HeadersRow()
    {
        if (Capture is { } c) c.HeadersRow();
        else ImGui.TableHeadersRow();
    }

    public static void NextRow()
    {
        if (Capture is { } c) c.NextRow();
        else ImGui.TableNextRow();
    }

    public static bool NextColumn()
    {
        if (Capture is { } c)
        {
            c.NextColumn();
            return true;
        }
        return ImGui.TableNextColumn();
    }

    public static bool SetColumnIndex(int index)
    {
        if (Capture is { } c)
        {
            c.SetColumn(index);
            return true;
        }
        return ImGui.TableSetColumnIndex(index);
    }

    public static void ScrollFreeze(int columns, int rows)
    {
        if (Capture == null) ImGui.TableSetupScrollFreeze(columns, rows);
    }

    public static void SetBgColor(ImGuiTableBgTarget target, uint color, int column = -1)
    {
        if (Capture == null) ImGui.TableSetBgColor(target, color, column);
    }

    // A table that isn't drawn while it's read: open (its rows are read) or closed (a list shows only its choice).
    private sealed class Ended : ImRaii.IEndObject
    {
        public static readonly Ended Open = new(true);
        public static readonly Ended Closed = new(false);
        private Ended(bool success) => Success = success;
        public bool Success { get; }
        public void Dispose() { }
    }
}

/// <summary>
/// Every table's Export button (beside Columns, or above a table of its own): Copy table puts it on the clipboard to
/// paste into a spreadsheet; Export to CSV asks where to save it (Documents to start with) and writes the same file
/// the app does. The table is read as text off the screen, every row of it, in the order shown.
/// </summary>
internal static class TableExportUi
{
    private static readonly FileDialogManager Dialog = new();
    private static string? note;
    private static DateTime noteUntil;

    /// <summary>The page shown (its name starts an export's file name).</summary>
    public static string Page { get; set; } = "";

    /// <summary>Draws the Export button and its menu. rows: the table read as text (called only when asked).</summary>
    public static void Button(string id, string title, Func<List<List<string>>> rows)
    {
        if (TableCapture.Current != null) return;
        if (ImGui.Button($"Export##export{id}")) ImGui.OpenPopup($"export##{id}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy this table, or save it as a CSV file for Excel or Google Sheets.");
        using var popup = ImRaii.Popup($"export##{id}");
        if (!popup) return;
        var name = string.Join(" - ", new[] { Page, title }.Where(s => s.Length > 0).Distinct());
        if (ImGui.Selectable("Copy table"))
        {
            var read = Read(rows);
            if (read.Count == 0) Note("The table is empty.");
            else
            {
                ImGui.SetClipboardText(TableExport.Tsv(read));
                Note("Table copied: paste it into a spreadsheet.");
            }
        }
        if (ImGui.Selectable("Export to CSV"))
        {
            var read = Read(rows);
            if (read.Count == 0) Note("The table is empty.");
            else
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                Dialog.SaveFileDialog("Export table to CSV", ".csv", TableExport.FileName(name, DateTime.Now), ".csv", (ok, path) =>
                {
                    if (!ok) return;
                    try
                    {
                        File.WriteAllText(path, TableExport.Csv(read), new System.Text.UTF8Encoding(false));
                        Note($"Saved {path}");
                    }
                    catch (Exception e)
                    {
                        Note($"Export failed: {e.Message}");
                    }
                }, folder.Length > 0 ? folder : null);
            }
        }
    }

    /// <summary>Draws the save dialog and the last note (call once a frame).</summary>
    public static void Draw()
    {
        Dialog.Draw();
        if (note != null && DateTime.Now < noteUntil)
        {
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().WorkPos + new Vector2(ImGui.GetMainViewport().WorkSize.X / 2, 40), ImGuiCond.Always, new Vector2(0.5f, 0));
            ImGui.SetNextWindowBgAlpha(0.9f);
            using var tip = ImRaii.Tooltip();
            ImGui.TextUnformatted(note);
        }
    }

    private static void Note(string text)
    {
        note = text;
        noteUntil = DateTime.Now.AddSeconds(4);
    }

    // Read off the screen: in a child window too small to show anything, so nothing flickers while it's read.
    private static List<List<string>> Read(Func<List<List<string>>> rows)
    {
        using var child = ImRaii.Child("##tableExportRead", new Vector2(1, 1), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav);
        ImGui.SetCursorPos(new Vector2(0, 100000));
        return rows();
    }
}
