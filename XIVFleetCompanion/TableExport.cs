using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Copy table and Export to CSV, as the app's (shared/csv.js): rows of cell text as CSV that Excel and Google Sheets
    /// open as is, or as tab-separated lines to paste into a spreadsheet. No Dalamud here, so it is tested on its own.
    /// </summary>
    public static class TableExport
    {
        private static readonly Regex FormulaStart = new("^[=+\\-@]");
        private static readonly Regex PlainNumber = new("^[+-]?[\\d,.]+%?$");
        private static readonly Regex Unsafe = new("[\\\\/:*?\"<>|]+");
        private static readonly Regex Spaces = new("\\s+");

        /// <summary>One cell: quoted when it holds a comma, quote or line break; a cell that starts like a formula
        /// (=, +, -, @) gets a leading apostrophe so a spreadsheet shows it as text, unless it's a plain number.</summary>
        public static string CsvCell(string? value)
        {
            var text = value ?? "";
            if (FormulaStart.IsMatch(text) && !PlainNumber.IsMatch(text)) text = "'" + text;
            return text.IndexOfAny(new[] { '"', ',', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }

        /// <summary>Rows as CSV, with a byte order mark so Excel reads it as UTF-8 (names with accents).</summary>
        public static string Csv(IEnumerable<IReadOnlyList<string>> rows) =>
            "﻿" + string.Join("\r\n", rows.Select(r => string.Join(",", r.Select(CsvCell)))) + "\r\n";

        /// <summary>Rows as tab-separated lines (Copy table): tabs and line breaks in a cell become spaces.</summary>
        public static string Tsv(IEnumerable<IReadOnlyList<string>> rows) =>
            string.Join("\n", rows.Select(r => string.Join("\t", r.Select(c => (c ?? "").Replace('\t', ' ').Replace('\n', ' ')))));

        /// <summary>A file name from a title: "Supplies - Ceruleum from FC Credits 2026-10-07.csv".</summary>
        public static string FileName(string? title, DateTime date)
        {
            var safe = Spaces.Replace(Unsafe.Replace(title ?? "Table", " "), " ").Trim();
            if (safe.Length > 100) safe = safe[..100];
            if (safe.Length == 0) safe = "Table";
            return $"{safe} {date:yyyy-MM-dd}.csv";
        }
    }

    /// <summary>
    /// Reads a table as text instead of drawing it, for Copy table and Export to CSV: while one is running, the window's
    /// text, ticks, boxes and lists (Windows/Ui.cs) add what they would show to the cell they're in, as the app's export
    /// reads what each cell shows (a tick as Yes or No, a box's value, a list's choice). Each piece in a cell is joined to
    /// the one before with a space.
    /// </summary>
    public sealed class TableCapture
    {
        [ThreadStatic] private static TableCapture? current;

        /// <summary>The capture running now, if any.</summary>
        public static TableCapture? Current => current;

        private readonly List<List<string>> rows = new();
        private List<string>? headers;
        private List<StringBuilder>? row;
        private int column = -1;

        /// <summary>Reads what <paramref name="draw"/> shows as rows of cell text.</summary>
        public static List<List<string>> Run(Action draw)
        {
            var before = current;
            var capture = new TableCapture();
            current = capture;
            try
            {
                draw();
            }
            finally
            {
                current = before;
            }
            capture.EndRow();
            return capture.rows;
        }

        /// <summary>What one cell shows (for tables that lay out their own rows).</summary>
        public static string Cell(Action draw)
        {
            var rows = Run(() =>
            {
                current!.NextColumn();
                draw();
            });
            return rows.Count > 0 && rows[0].Count > 0 ? rows[0][0] : "";
        }

        /// <summary>A heading as it reads ("##tick" is an unnamed heading; "A day##kits" reads "A day").</summary>
        public static string Visible(string label)
        {
            var i = label.IndexOf("##", StringComparison.Ordinal);
            return (i < 0 ? label : label[..i]).Trim();
        }

        public void Header(string label) => (headers ??= new List<string>()).Add(Visible(label));

        public void HeadersRow()
        {
            EndRow();
            if (headers != null) rows.Add(headers);
            headers = null;
        }

        public void NextRow()
        {
            EndRow();
            row = new List<StringBuilder>();
            column = -1;
        }

        public void NextColumn() => SetColumn(column + 1);

        public void SetColumn(int index)
        {
            row ??= new List<StringBuilder>();
            while (row.Count <= index) row.Add(new StringBuilder());
            column = index;
        }

        /// <summary>Text the cell shows (nothing outside a cell).</summary>
        public void Add(string? text)
        {
            if (row == null || column < 0 || string.IsNullOrWhiteSpace(text)) return;
            var cell = row[column];
            if (cell.Length > 0) cell.Append(' ');
            cell.Append(Spaces.Replace(text, " ").Trim());
        }

        private static readonly Regex Spaces = new("\\s+");

        private void EndRow()
        {
            if (row != null) rows.Add(row.Select(c => c.ToString()).ToList());
            row = null;
            column = -1;
        }
    }
}
