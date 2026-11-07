using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Npgsql;
using NpgsqlTypes;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Import Fight Club Data from the game (Settings): reads Fight Club's route spreadsheet (.xlsx) as the app does
    /// (ipc/fight-club.js: the same columns, merged cells filled in, numbers typed as text read as numbers, problem rows
    /// flagged), then replaces the stored routes with the rows ticked, all or nothing. Works on the PostgreSQL database and
    /// on the one-file SQLite one. The app's tests read some awkward spreadsheets and import them
    /// (tests/db/fight-club-fixture.js); the plugin's tests read the same files and compare.
    ///
    /// An .xlsx file is a zip of XML files; this reads the few it needs (no spreadsheet library), the way the app's
    /// spreadsheet library does: the sheet named "Fight Club Comparisons" (else the first), from the size the sheet says it
    /// has, a merged cell's value copied over every cell it covers.
    /// </summary>
    public static class FightClubImport
    {
        public sealed record Column(string Header, string DbColumn, bool Numeric, bool Required = false);

        /// <summary>Each column the spreadsheet may have and where it's stored (only Route is required).</summary>
        public static readonly Column[] Columns =
        {
            new("OJ 24h %", "oj_24h_pct", true), new("Gil/Sub/Day", "gil_sub_day", true), new("Route", "route", false, true),
            new("Hours", "hours", true), new("Rec Build (NOTES)", "rec_build_notes", false), new("Unmod Min Rank", "unmod_min_rank", false),
            new("Min Rank Build", "min_rank_build", false), new("Gil/Sub", "gil_sub", true), new("Gil/FC", "gil_fc", true),
            new("Gil/Hr", "gil_hr", true), new("Gil/Day", "gil_day", true), new("Gil/Week", "gil_week", true), new("Gil/Month", "gil_month", true),
            new("Gil/Year", "gil_year", true), new("Avg EXP", "avg_exp", true), new("Avg EXP/H", "avg_exp_hr", true),
            new("Repair Times", "repair_times", false), new("Notes", "notes", false), new("FC Points", "fc_points", true),
            new("GC iLvl/Day", "gc_ilvl_day", true), new("~QVs/Day", "qvs_day", true), new("Fuel/Day", "fuel_day", true),
        };

        /// <summary>One row of the sheet: its row number as the sheet shows it, each column's value (a double, a string or
        /// null) and what's wrong with it.</summary>
        public sealed record Row(int RowNumber, Dictionary<string, object?> Data, List<string> Issues)
        {
            public bool HasIssue => Issues.Count > 0;
        }

        /// <summary>What was read: the rows, or why not (Stage "read": not a spreadsheet; "structural": no Route column).</summary>
        public sealed record ReadResult(bool Success, string? Stage, string? Error, List<string> ActualHeaders, List<string> MissingOptional, List<Row> Rows);

        public const string SheetName = "Fight Club Comparisons";

        public static ReadResult ReadFile(string path)
        {
            if (Path.GetExtension(path).Equals(".xls", StringComparison.OrdinalIgnoreCase))
                return Failed("read", "This is an old Excel (.xls) file. Open it in Excel or Google Sheets and save it as .xlsx, or import it in the app.");
            try
            {
                return Read(File.ReadAllBytes(path));
            }
            catch (IOException ex)
            {
                return Failed("read", ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Failed("read", ex.Message);
            }
        }

        private static ReadResult Failed(string stage, string error, List<string>? headers = null) =>
            new(false, stage, error, headers ?? new List<string>(), new List<string>(), new List<Row>());

        public static ReadResult Read(byte[] xlsx)
        {
            List<List<object?>> grid;
            List<(int R1, int C1, int R2, int C2)> merges;
            try
            {
                (grid, merges) = ReadSheet(xlsx);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                return Failed("read", $"This isn't a spreadsheet the plugin can read (.xlsx): {ex.Message}");
            }

            // A merged cell's value is only in its top-left cell: copied over every cell it covers (the app does this
            // with the merge's place on the sheet, also where the sheet doesn't start at A1).
            foreach (var (r1, c1, r2, c2) in merges)
            {
                var value = r1 < grid.Count && c1 < grid[r1].Count ? grid[r1][c1] : null;
                if (value == null) continue;
                for (var r = r1; r <= r2; r++)
                {
                    if (r >= grid.Count) continue;
                    for (var c = c1; c <= c2; c++)
                    {
                        while (grid[r].Count <= c) grid[r].Add(null);
                        grid[r][c] = value;
                    }
                }
            }

            var headerRow = grid.Count > 0 ? grid[0] : new List<object?>();
            var actualHeaders = headerRow.Where(h => h != null).Select(JsString).ToList();
            var missingRequired = Columns.Where(c => c.Required && !actualHeaders.Contains(c.Header)).ToList();
            var missingOptional = Columns.Where(c => !c.Required && !actualHeaders.Contains(c.Header)).Select(c => c.Header).ToList();
            if (missingRequired.Count > 0)
                return Failed("structural", $"Missing required column(s): {string.Join(", ", missingRequired.Select(c => c.Header))}", actualHeaders);

            // Headers can move between exports: each column is found by its header (the last, if one is there twice).
            var index = new Dictionary<string, int>();
            for (var i = 0; i < headerRow.Count; i++)
                if (headerRow[i] != null) index[JsString(headerRow[i])] = i;

            var rows = new List<Row>();
            for (var i = 1; i < grid.Count; i++)
            {
                var cells = grid[i];
                if (!cells.Any(c => c != null && !(c is string s && s.Length == 0))) continue;
                var data = new Dictionary<string, object?>();
                var issues = new List<string>();
                foreach (var col in Columns)
                {
                    var raw = index.TryGetValue(col.Header, out var at) && at < cells.Count ? cells[at] : null;
                    if (col.Numeric)
                    {
                        if (raw == null || raw is string { Length: 0 }) data[col.DbColumn] = null;
                        else if (raw is double d) data[col.DbColumn] = d;
                        else
                        {
                            // A number typed as text ("118,854", "12.5%"), or a stray note.
                            var n = JsNumber(Regex.Replace(JsString(raw).Replace(",", ""), "%$", ""));
                            if (double.IsNaN(n))
                            {
                                issues.Add($"\"{col.Header}\" contains a non-numeric value: \"{JsString(raw)}\"");
                                data[col.DbColumn] = null;
                            }
                            else data[col.DbColumn] = n;
                        }
                    }
                    else data[col.DbColumn] = raw == null || raw is string { Length: 0 } ? null : JsTrim(JsString(raw));
                }
                if (data["route"] is not string { Length: > 0 }) issues.Add("Route is blank on this row.");
                // The row number as the sheet shows it (counted from where the sheet starts).
                rows.Add(new Row(i + 1, data, issues));
            }
            return new ReadResult(true, null, null, actualHeaders, missingOptional, rows);
        }

        // ---- The .xlsx itself ----

        private static XDocument Xml(ZipArchive zip, string path)
        {
            var entry = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{path} is missing");
            using var stream = entry.Open();
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        private static IEnumerable<XElement> Named(XContainer x, string name) => x.Descendants().Where(e => e.Name.LocalName == name);
        private static XElement? Child(XElement x, string name) => x.Elements().FirstOrDefault(e => e.Name.LocalName == name);

        // A shared or inline string: its text, the runs of rich text joined (not the phonetic guide).
        private static string StringItem(XElement si)
        {
            if (Child(si, "t") is { } t) return Unescape(t.Value);
            return Unescape(string.Concat(si.Elements().Where(e => e.Name.LocalName == "r").Select(r => Child(r, "t")?.Value ?? "")));
        }

        // "_x000D_" and the like stand for one character.
        private static string Unescape(string text) =>
            Regex.Replace(text, "_x([0-9a-fA-F]{4})_", m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

        // "AB12" -> (11, 27): row and column from 0.
        private static (int Row, int Col) Cell(string reference)
        {
            var m = Regex.Match(reference, "^\\$?([A-Za-z]+)\\$?([0-9]+)$");
            if (!m.Success) throw new FormatException($"Unknown cell \"{reference}\"");
            var col = 0;
            foreach (var ch in m.Groups[1].Value.ToUpperInvariant()) col = col * 26 + (ch - 'A' + 1);
            return (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1, col - 1);
        }

        private static (int R1, int C1, int R2, int C2) Range(string reference)
        {
            var parts = reference.Split(':');
            var a = Cell(parts[0]);
            var b = parts.Length > 1 ? Cell(parts[1]) : a;
            return (Math.Min(a.Row, b.Row), Math.Min(a.Col, b.Col), Math.Max(a.Row, b.Row), Math.Max(a.Col, b.Col));
        }

        /// <summary>The route sheet as rows of values (a double, a string, a bool or null), from where the sheet says it
        /// starts (its size, else its cells), and its merged cells where they are on the sheet.</summary>
        public static (List<List<object?>> Grid, List<(int R1, int C1, int R2, int C2)> Merges) ReadSheet(byte[] xlsx)
        {
            using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
            var workbook = Xml(zip, "xl/workbook.xml");
            var sheets = Named(workbook, "sheet").Select(s => (Name: (string?)s.Attribute("name") ?? "",
                Id: s.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value)).ToList();
            if (sheets.Count == 0) throw new InvalidDataException("the workbook has no sheets");
            var chosen = sheets.Any(s => s.Name == SheetName) ? sheets.First(s => s.Name == SheetName) : sheets[0];
            var rels = Xml(zip, "xl/_rels/workbook.xml.rels");
            var target = Named(rels, "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == chosen.Id)?.Attribute("Target")?.Value
                ?? throw new InvalidDataException($"sheet \"{chosen.Name}\" is missing");
            var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

            var shared = new List<string>();
            if (zip.GetEntry("xl/sharedStrings.xml") != null)
                shared = Named(Xml(zip, "xl/sharedStrings.xml"), "si").Select(StringItem).ToList();

            var sheet = Xml(zip, sheetPath);
            var cells = new List<(int Row, int Col, object Value)>();
            int? lastRow = null;
            foreach (var row in Named(sheet, "row"))
            {
                var r = (string?)row.Attribute("r") is { } rText ? int.Parse(rText, CultureInfo.InvariantCulture) - 1 : (lastRow ?? -1) + 1;
                lastRow = r;
                var col = -1;
                foreach (var c in row.Elements().Where(e => e.Name.LocalName == "c"))
                {
                    col = (string?)c.Attribute("r") is { } at ? Cell(at).Col : col + 1;
                    var type = (string?)c.Attribute("t") ?? "n";
                    var v = Child(c, "v")?.Value;
                    object? value = type switch
                    {
                        "s" => v == null ? null : shared[int.Parse(v, CultureInfo.InvariantCulture)],
                        "inlineStr" => Child(c, "is") is { } inline ? StringItem(inline) : null,
                        "str" => v == null ? null : Unescape(v),
                        "b" => v == null ? null : v.Trim() is "1" or "true" or "TRUE",
                        "e" => null,
                        "d" => v,
                        _ => v == null || v.Length == 0 ? null : double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture),
                    };
                    if (value != null) cells.Add((r, col, value));
                }
            }

            (int R1, int C1, int R2, int C2)? size = null;
            if (Named(sheet, "dimension").FirstOrDefault()?.Attribute("ref")?.Value is { Length: > 0 } dim)
                size = Range(dim);
            else if (cells.Count > 0)
                size = (cells.Min(c => c.Row), cells.Min(c => c.Col), cells.Max(c => c.Row), cells.Max(c => c.Col));
            var grid = new List<List<object?>>();
            if (size is { } s)
            {
                for (var r = s.R1; r <= s.R2; r++) grid.Add(Enumerable.Repeat<object?>(null, s.C2 - s.C1 + 1).ToList());
                foreach (var (r, c, value) in cells)
                    if (r >= s.R1 && r <= s.R2 && c >= s.C1 && c <= s.C2) grid[r - s.R1][c - s.C1] = value;
            }
            var merges = Named(sheet, "mergeCell").Select(m => (string?)m.Attribute("ref")).Where(r => !string.IsNullOrEmpty(r)).Select(r => Range(r!)).ToList();
            return (grid, merges);
        }

        // ---- JavaScript's ways with values, as the app's import uses them ----

        /// <summary>A value as JavaScript's String() writes it.</summary>
        public static string JsString(object? value) => value switch
        {
            null => "null",
            string s => s,
            bool b => b ? "true" : "false",
            double d => JsNumberText(d),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };

        /// <summary>A number as JavaScript writes it: 85, 0.30000000000000004, 1.5e-7, 1e+21.</summary>
        public static string JsNumberText(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsPositiveInfinity(d)) return "Infinity";
            if (double.IsNegativeInfinity(d)) return "-Infinity";
            if (d == 0) return "0";
            // The fewest digits that still read back as the same number.
            var shortest = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
            var m = Regex.Match(shortest, "^([0-9]+)(?:\\.([0-9]+))?(?:E([+-]?[0-9]+))?$");
            if (!m.Success) return shortest;
            var digits = (m.Groups[1].Value + m.Groups[2].Value).TrimStart('0');
            var exp = (m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0) + m.Groups[1].Value.Length;
            // Leading zeros of a number below 1 ("0.0015" -> digits "15", the point 2 places further left).
            var lead = (m.Groups[1].Value + m.Groups[2].Value).Length - (m.Groups[1].Value + m.Groups[2].Value).TrimStart('0').Length;
            exp -= lead;
            digits = digits.TrimEnd('0');
            if (digits.Length == 0) return "0";
            var k = digits.Length;
            var n = exp; // the number is 0.digits × 10^n
            string text;
            if (k <= n && n <= 21) text = digits + new string('0', n - k);
            else if (0 < n && n <= 21) text = digits[..n] + "." + digits[n..];
            else if (-6 < n && n <= 0) text = "0." + new string('0', -n) + digits;
            else
            {
                var e = n - 1;
                text = digits[..1] + (k > 1 ? "." + digits[1..] : "") + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
            }
            return d < 0 ? "-" + text : text;
        }

        // JavaScript's white space and line ends (what trim() and Number() skip).
        private static bool JsSpace(char c) => c is (char)0x09 or (char)0x0A or (char)0x0B or (char)0x0C or (char)0x0D or (char)0x20 or (char)0xA0
            or (char)0x1680 or (char)0x2028 or (char)0x2029 or (char)0x202F or (char)0x205F or (char)0x3000 or (char)0xFEFF || (c >= (char)0x2000 && c <= (char)0x200A);

        /// <summary>JavaScript's trim().</summary>
        public static string JsTrim(string s)
        {
            var a = 0;
            var b = s.Length;
            while (a < b && JsSpace(s[a])) a++;
            while (b > a && JsSpace(s[b - 1])) b--;
            return s[a..b];
        }

        /// <summary>JavaScript's Number() of a text: "" and spaces are 0, "0x1A" is 26, "1e3" is 1000, anything else NaN.</summary>
        public static double JsNumber(string text)
        {
            var s = JsTrim(text);
            if (s.Length == 0) return 0;
            switch (s)
            {
                case "Infinity" or "+Infinity": return double.PositiveInfinity;
                case "-Infinity": return double.NegativeInfinity;
            }
            if (s.Length > 2 && s[0] == '0' && (s[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B'))
            {
                var radix = s[1] is 'x' or 'X' ? 16 : s[1] is 'o' or 'O' ? 8 : 2;
                double v = 0;
                foreach (var ch in s[2..])
                {
                    var digit = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : 99;
                    if (digit >= radix) return double.NaN;
                    v = v * radix + digit;
                }
                return v;
            }
            if (!Regex.IsMatch(s, "^[+-]?([0-9]+\\.?[0-9]*|\\.[0-9]+)([eE][+-]?[0-9]+)?$")) return double.NaN;
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // ---- Storing it ----

        /// <summary>Replaces the stored routes with these rows (all or nothing; a failure leaves the old ones), with the
        /// patch label they're from (blank: none). PostgreSQL or SQLite. How many were stored.</summary>
        public static async Task<int> Commit(DbConnection conn, IReadOnlyList<Row> rows, string? patchLabel)
        {
            var pg = conn is NpgsqlConnection;
            var label = string.IsNullOrEmpty(patchLabel) ? null : patchLabel;
            var names = Columns.Select(c => c.DbColumn).ToList();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                // SQLite numbers the routes from 1 again once the table is empty, as RESTART IDENTITY does here.
                clear.CommandText = pg ? "TRUNCATE TABLE fight_club_routes RESTART IDENTITY" : "DELETE FROM fight_club_routes";
                await clear.ExecuteNonQueryAsync();
            }
            foreach (var row in rows)
            {
                await using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                var values = new List<string>();
                for (var i = 0; i < Columns.Length; i++)
                {
                    var col = Columns[i];
                    var value = row.Data.GetValueOrDefault(col.DbColumn);
                    var p = insert.CreateParameter();
                    p.ParameterName = $"p{i}";
                    if (pg)
                    {
                        // Numbers go as JavaScript writes them, as the app sends them, so PostgreSQL stores the same digits.
                        ((NpgsqlParameter)p).NpgsqlDbType = NpgsqlDbType.Text;
                        p.Value = value switch { null => DBNull.Value, double d => JsNumberText(d), var v => JsString(v) };
                        values.Add(col.Numeric ? $"@p{i}::numeric" : $"@p{i}");
                    }
                    else
                    {
                        p.Value = value ?? DBNull.Value;
                        values.Add($"@p{i}");
                    }
                    insert.Parameters.Add(p);
                }
                var lp = insert.CreateParameter();
                lp.ParameterName = "label";
                if (pg) ((NpgsqlParameter)lp).NpgsqlDbType = NpgsqlDbType.Text;
                lp.Value = (object?)label ?? DBNull.Value;
                insert.Parameters.Add(lp);
                insert.CommandText = pg
                    ? $"INSERT INTO fight_club_routes ({string.Join(", ", names)}, patch_label) VALUES ({string.Join(", ", values)}, @label)"
                    : $"INSERT INTO fight_club_routes ({string.Join(", ", names)}, patch_label, imported_at) VALUES ({string.Join(", ", values)}, @label, (julianday('now') - 2440587.5) * 86400)";
                await insert.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return rows.Count;
        }
    }
}
