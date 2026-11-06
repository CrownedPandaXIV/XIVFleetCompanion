using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace XIVFleetCompanion
{
    // 2.x Trends stage, as the app's Trends tab (renderer/trends.jsx, ipc/trends.js, db/metric-series.js): charts of the
    // values the plugin records over time (companion_metric_history: a row when a value changes, and an hourly "still the
    // same" row), for the whole fleet, characters or Free Companies, read with the same chart query. The app's tests write
    // a made-up history and the app's answers (tests/db/trends-fixture.js) that the plugin's tests check this against.

    /// <summary>One recorded value making up a chart: its name ("gil", "item_qty:22500"), whose it is ("character" or
    /// "fc") and what each counts for (salvage: its vendor price).</summary>
    public sealed record TrendPart(string Metric, string SubjectType, double Weight = 1);

    /// <summary>What can be charted: its key and name, the values making it up for a character and for a Free Company
    /// (none: not for those), and whose are added together for the whole fleet.</summary>
    public sealed record TrendMetric(string Key, string Label, List<TrendPart>? Character, List<TrendPart>? Fc, string[] FleetTypes, bool ForSubs = false)
    {
        public List<TrendPart>? PartsFor(string subjectType) => subjectType == "fc" ? Fc : Character;
        public List<TrendPart> FleetParts => FleetTypes.SelectMany(t => PartsFor(t) ?? new List<TrendPart>()).ToList();
    }

    /// <summary>A chart to read: from and to (milliseconds), how many points, the values, and whose (null: the whole fleet in one line).</summary>
    public sealed record TrendRequest(double From, double To, int Points, List<TrendPart> Parts, List<string>? SubjectIds)
    {
        public bool Split => SubjectIds != null;
        public string Key => string.Join("|", From.ToString(CultureInfo.InvariantCulture), To.ToString(CultureInfo.InvariantCulture), Points,
            string.Join(",", Parts.Select(p => $"{p.Metric}:{p.SubjectType}:{p.Weight.ToString(CultureInfo.InvariantCulture)}")), SubjectIds == null ? "-" : string.Join(",", SubjectIds));
    }

    /// <summary>One line: whose (null for the whole fleet) and its value at each point (null before anything was recorded).</summary>
    public sealed record TrendSeries(string? SubjectId, List<double?> Values);

    /// <summary>A chart's points (milliseconds) and its lines.</summary>
    public sealed record TrendResult(List<double> Timestamps, List<TrendSeries> Series);

    /// <summary>A value recorded: for whom ("character" or "fc"), how many have it, and when it was first and last recorded (milliseconds).</summary>
    public sealed record TrendCatalogRow(string Metric, string SubjectType, int Subjects, double? FirstRecorded, double? LastRecorded);

    public sealed record TrendCharacter(string Id, string Name, string? World, string? Account, int? SortIndex)
    {
        public string Label => $"{Name} ({World})";
    }

    public sealed record TrendFc(string Id, string? Name, string? Leader)
    {
        public string Label => Leader != null ? $"{Name} — {Leader}" : Name ?? "";
    }

    /// <summary>What the Trends page needs to start: what's recorded, and the characters and Free Companies.</summary>
    public sealed record TrendsMeta(List<TrendCatalogRow> Catalog, List<TrendCharacter> Characters, List<TrendFc> Fcs);

    /// <summary>A line over its span: the value now and at the start, the change (and as a share of the start), the change
    /// a day on average, and the lowest and highest.</summary>
    public sealed record TrendStats(double? Current, double? Start, double? Change, double? Percent, double? PerDay, double? Low, double? High);

    public sealed record TrendRange(string Key, string Label, double? Ms, int Points);

    public static class TrendsMath
    {
        public const double Day = 86400000;
        public const int MaxSeries = 8;
        public const int MaxPoints = 1000;
        private const int MaxParts = 20;
        private const int MaxSubjects = 500;
        private const double MaxSpanMs = 20.0 * 365 * 24 * 3600 * 1000;
        private static readonly Regex MetricPattern = new("^[a-z_]{1,40}(:[0-9]{1,9})?$");
        private static readonly Regex SubjectIdPattern = new("^[0-9]{1,25}$");

        /// <summary>What salvage sells for (as the app's).</summary>
        public static readonly IReadOnlyDictionary<int, double> SalvagePrices = new Dictionary<int, double>
        {
            [22500] = 8000, [22501] = 9000, [22502] = 10000, [22503] = 13000, [22504] = 27000, [22505] = 28500, [22506] = 30000, [22507] = 34500,
        };

        public static readonly TrendRange[] Ranges =
        {
            new("24h", "24 hours", Day, 144), new("7d", "7 days", 7 * Day, 168), new("30d", "30 days", 30 * Day, 150),
            new("90d", "90 days", 90 * Day, 150), new("1y", "1 year", 365 * Day, 150), new("all", "All time", null, 150),
        };

        /// <summary>What can be charted (buildTrendMetrics).</summary>
        public static readonly IReadOnlyList<TrendMetric> Metrics = BuildMetrics();

        private static List<TrendMetric> BuildMetrics()
        {
            List<TrendPart> One(string metric, string type) => new() { new TrendPart(metric, type) };
            List<TrendPart> Items(Func<int, double> weight, string type) => SalvagePrices.Keys.Select(id => new TrendPart($"item_qty:{id}", type, weight(id))).ToList();
            return new List<TrendMetric>
            {
                new("gil", "Gil", One("gil", "character"), null, new[] { "character" }),
                new("ceruleum", "Ceruleum", One("ceruleum", "character"), null, new[] { "character" }, ForSubs: true),
                new("repair_kits", "Repair kits", One("repair_kits", "character"), null, new[] { "character" }, ForSubs: true),
                new("retainer_count", "Retainers", One("retainer_count", "character"), null, new[] { "character" }),
                new("submarine_count", "Submarines", One("submarine_count", "character"), null, new[] { "character" }, ForSubs: true),
                new("num_sub_slots", "Sub slots", One("num_sub_slots", "character"), null, new[] { "character" }, ForSubs: true),
                new("fc_points", "FC points", One("fc_points", "character"), One("fc_points", "fc"), new[] { "fc" }),
                new("salvage_value", "Salvage value (gil)", Items(id => SalvagePrices[id], "character"), Items(id => SalvagePrices[id], "fc"), new[] { "character", "fc" }, ForSubs: true),
                new("salvage_items", "Salvage items (count)", Items(_ => 1, "character"), Items(_ => 1, "fc"), new[] { "character", "fc" }, ForSubs: true),
            };
        }

        public static TrendMetric Metric(string key) => Metrics.FirstOrDefault(m => m.Key == key) ?? Metrics[0];

        /// <summary>When the first of a chart's values was recorded (for "All time"), from the catalog.</summary>
        public static double? FirstRecorded(TrendMetric metric, List<TrendCatalogRow> catalog)
        {
            var names = new HashSet<string>((metric.Character ?? new()).Concat(metric.Fc ?? new()).Select(p => p.Metric));
            var times = catalog.Where(r => names.Contains(r.Metric) && r.FirstRecorded != null).Select(r => r.FirstRecorded!.Value).ToList();
            return times.Count > 0 ? times.Min() : null;
        }

        /// <summary>Checks a chart request as the app does (validateSeriesRequest), then rounds a span of half an hour or more
        /// to whole minutes (the end rounded up), as ipc/trends.js does. Returns the request to read, or why not.</summary>
        public static (TrendRequest? Request, string? Error) Check(double from, double to, int points, List<TrendPart> parts, List<string>? subjectIds)
        {
            if (double.IsNaN(from) || double.IsNaN(to)) return (null, "Invalid time range.");
            if (from >= to) return (null, "The start of the range must be before the end.");
            if (to - from > MaxSpanMs) return (null, "The time range is too long.");
            if (points < 2 || points > MaxPoints) return (null, $"points must be a whole number from 2 to {MaxPoints}.");
            if (parts.Count < 1 || parts.Count > MaxParts) return (null, $"parts must list 1 to {MaxParts} values.");
            if (parts.Any(p => !MetricPattern.IsMatch(p.Metric))) return (null, "Invalid metric name.");
            if (parts.Any(p => p.SubjectType is not ("character" or "fc"))) return (null, "Invalid subject type.");
            if (parts.Any(p => double.IsNaN(p.Weight) || double.IsInfinity(p.Weight))) return (null, "Invalid weight.");
            if (subjectIds != null)
            {
                if (subjectIds.Count < 1 || subjectIds.Count > MaxSubjects) return (null, $"subjectIds must list 1 to {MaxSubjects} ids.");
                if (!subjectIds.All(id => SubjectIdPattern.IsMatch(id))) return (null, "Invalid subject id.");
            }
            if (to - from >= 30 * 60 * 1000)
            {
                from = Math.Floor(from / 60000) * 60000;
                to = Math.Ceiling(to / 60000) * 60000;
            }
            return (new TrendRequest(from, to, points, parts, subjectIds?.ToList()), null);
        }

        /// <summary>The chart query (db/metric-series.js SERIES_SQL): the chart instants, the subjects that carry each value
        /// (from companion_metric_latest), and one index lookup per subject per instant for the newest row at or before it.</summary>
        public const string SeriesSql = @"
  WITH buckets AS (
    SELECT i, $1::timestamptz + ($2::timestamptz - $1::timestamptz) * i / $3::int AS ts
    FROM generate_series($9::int, $10::int) AS i
  ),
  parts AS (
    SELECT * FROM unnest($4::text[], $5::text[], $6::numeric[]) AS x(metric, subject_type, weight)
  ),
  subjects AS (
    SELECT DISTINCT l.subject_type, l.subject_id, pt.metric, pt.weight
    FROM parts pt
    JOIN companion_metric_latest l ON l.metric = pt.metric AND l.subject_type = pt.subject_type
    WHERE $7::numeric[] IS NULL OR l.subject_id = ANY($7::numeric[])
  )
  SELECT b.i,
         CASE WHEN $8::boolean THEN s.subject_id END AS subject_id,
         sum(v.value * s.weight) AS value
  FROM buckets b
  CROSS JOIN subjects s
  CROSS JOIN LATERAL (
    SELECT h.value
    FROM companion_metric_history h
    WHERE h.subject_type = s.subject_type
      AND h.subject_id = s.subject_id
      AND h.metric = s.metric
      AND h.recorded_at <= b.ts
    ORDER BY h.recorded_at DESC
    LIMIT 1
  ) v
  GROUP BY b.i, CASE WHEN $8::boolean THEN s.subject_id END
  ORDER BY 2, b.i";

        /// <summary>What's recorded (db/metric-series.js CATALOG_SQL).</summary>
        public const string CatalogSql = @"
  SELECT m.metric, m.subject_type, m.subjects, m.last_recorded,
         (SELECT min(h.recorded_at) FROM companion_metric_history h WHERE h.metric = m.metric) AS first_recorded
  FROM (
    SELECT metric, subject_type, count(*)::int AS subjects, max(recorded_at) AS last_recorded
    FROM companion_metric_latest
    GROUP BY metric, subject_type
  ) m
  ORDER BY m.metric, m.subject_type";

        /// <summary>The characters and Free Companies a chart can show (ipc/trends.js get-metric-subjects).</summary>
        public const string CharactersSql = @"
  SELECT c.cid, c.name, c.world, c.account_label, so.sort_index
  FROM companion_character_current c
  LEFT JOIN character_sort_order so ON so.cid = c.cid
  ORDER BY so.sort_index NULLS LAST, c.name";

        public const string FcsSql = @"
  SELECT DISTINCT ON (h.fc_id) h.fc_id, h.fc_name, h.fc_master
  FROM companion_character_housing h
  WHERE h.fc_id IS NOT NULL
  ORDER BY h.fc_id, h.updated_at DESC";

        /// <summary>The query's rows (point, whose, value) as the chart's points and lines (shapeSeriesResult): a point with
        /// nothing recorded yet is null, so a line starts where its history does.</summary>
        public static TrendResult Shape(IEnumerable<(int I, string? SubjectId, double? Value)> rows, TrendRequest r)
        {
            var span = r.To - r.From;
            var timestamps = Enumerable.Range(0, r.Points).Select(i => Math.Floor(r.From + span * i / (r.Points - 1) + 0.5)).ToList();
            var order = r.Split ? r.SubjectIds!.Cast<string?>().ToList() : new List<string?> { null };
            var byKey = new Dictionary<string, List<double?>>();
            foreach (var id in order) byKey[id ?? ""] = Enumerable.Repeat<double?>(null, r.Points).ToList();
            foreach (var (i, subjectId, value) in rows)
            {
                var key = r.Split ? subjectId ?? "" : "";
                if (!byKey.TryGetValue(key, out var values) || i < 0 || i >= r.Points) continue;
                values[i] = value;
            }
            return new TrendResult(timestamps, order.Select(id => new TrendSeries(id, byKey[id ?? ""])).ToList());
        }

        /// <summary>A line over its span: now, at the start, the change (and its share of the start), the change a day on
        /// average (between the first and last points with a value), and the lowest and highest.</summary>
        public static TrendStats Stats(IReadOnlyList<double?> values, IReadOnlyList<double> timestamps)
        {
            int first = -1, last = -1;
            for (var i = 0; i < values.Count; i++)
                if (values[i] != null) { if (first < 0) first = i; last = i; }
            if (first < 0) return new TrendStats(null, null, null, null, null, null, null);
            var current = values[last]!.Value;
            var start = values[first]!.Value;
            var change = current - start;
            var known = values.Where(v => v != null).Select(v => v!.Value).ToList();
            var days = (timestamps[last] - timestamps[first]) / Day;
            return new TrendStats(current, start, change, start != 0 ? change / Math.Abs(start) * 100 : null, days > 0 ? change / days : null, known.Min(), known.Max());
        }

        /// <summary>A whole number with thousands separators ("—" for none), as the app's trendFullNumber.</summary>
        public static string Full(double? n) => n is double v ? Math.Floor(v + 0.5).ToString("#,0", CultureInfo.InvariantCulture) : "—";

        /// <summary>A short number for the chart's axis: 1.25M, 980K, 12.5 (trendCompactNumber).</summary>
        public static string Compact(double n)
        {
            static string Trim(double x) => (Math.Floor(x * 100 + 0.5) / 100).ToString("0.##", CultureInfo.InvariantCulture);
            var abs = Math.Abs(n);
            if (abs >= 1e9) return $"{Trim(n / 1e9)}B";
            if (abs >= 1e6) return $"{Trim(n / 1e6)}M";
            if (abs >= 1e3) return $"{Trim(n / 1e3)}K";
            return Trim(n);
        }

        /// <summary>Evenly spaced round values for an axis from low to high (about `count` of them: steps of 1, 2 or 5 times a power of ten).</summary>
        public static List<double> NiceTicks(double low, double high, int count = 5)
        {
            if (!(high > low)) return new List<double> { low };
            var raw = (high - low) / Math.Max(1, count);
            var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var step = new[] { 1.0, 2, 2.5, 5, 10 }.Select(m => m * power).First(s => s >= raw);
            var ticks = new List<double>();
            for (var v = Math.Ceiling(low / step) * step; v <= high + step * 1e-9; v += step) ticks.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
            return ticks;
        }

        /// <summary>The chart's value range: its lowest and highest with a little room (8%) above and below, as the app's.</summary>
        public static (double Low, double High) Range(IEnumerable<double?> values)
        {
            var known = values.Where(v => v != null).Select(v => v!.Value).ToList();
            if (known.Count == 0) return (0, 1);
            double min = known.Min(), max = known.Max();
            if (min == max) return (min - 1, max + 1);
            var pad = (max - min) * 0.08;
            return (min - pad, max + pad);
        }
    }
}
