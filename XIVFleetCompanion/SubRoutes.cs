using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XIVFleetCompanion
{
    // 2.x routes stage: each sub's route, gil a day, speed and range, rank-up dates and next build, worked out here the
    // same way as the app's Subs tab (ipc/subs.js loadSubmarines and addRankUps; subs/submarine-builds.js, rank-up.js
    // and voyage-forecast.js). The fleet comes from the database's fleet_route_* views; the sectors, ranks and parts from
    // the game's own files (SubGameData). The app's tests write made-up fleets and its answers (tests/db/routes-fixture.js)
    // that the plugin's tests check this against.

    /// <summary>A built sub, from fleet_route_subs_view: its route's stops (one byte each, 0 unused) and what its dates need.</summary>
    public sealed record RouteSub(long Cid, string SubName, int? Slot, int Rank, int? Part1, int? Part2, int? Part3, int? Part4, byte[]? Points,
        long? ReturnTime, long? CurrentExp, long? NextLevelExp, bool CraftEnabled, string? FcId);

    /// <summary>A Fight Club route, from fleet_route_fight_club_view (every route, with or without a recommended build).</summary>
    public sealed record FightClubRow(long Id, string Route, double? Hours, string? RepairTimes, double? GilSubDay, string? RecBuild, string? UnmodMinRank,
        string? MinRankBuild, double RepairKitCost)
    {
        public PartsFightClubRoute AsParts => new(Id, Route, Hours, RepairTimes, GilSubDay, RecBuild, RepairKitCost);
    }

    /// <summary>A recorded value of a sub slot's rank or experience ("sub_rank:2", "sub_exp:2"), from fleet_sub_history_view.</summary>
    public sealed record SubHistoryPoint(long Cid, string Metric, double At, double Value);

    /// <summary>One sector of a voyage SubmarineTracker recorded, from fleet_voyage_sectors_view (At in milliseconds).</summary>
    public sealed record VoyageSector(string FcId, long Register, string? SubName, double At, int Sector, int? Leg);

    /// <summary>A sector from the game's files (SubmarineExploration): its letter, map, position, experience and survey time.</summary>
    public sealed record SubSector(int Id, string Location, int Map, double X, double Y, double Z, double ExpReward, double SurveyMinutes, bool Starting);

    /// <summary>A rank from the game's files (SubmarineRank): its speed and range before parts, and experience to the next.</summary>
    public sealed record SubRank(int Id, double? Speed, double? Range, double ExpToNext);

    /// <summary>A part's speed and range from the game's files (SubmarinePart), by class (1 Whale ... 10 Syldra+) and slot (0 hull ... 3 bridge).</summary>
    public sealed record SubPartStat(int Class, int Slot, double Speed, double Range);

    /// <summary>The game data the routes need (from the game's files in the plugin; made up in the tests).</summary>
    public sealed record SubGameData(List<SubSector> Sectors, Dictionary<int, string> MapNames, List<SubRank> Ranks, List<SubPartStat> Parts);

    public sealed record RoutesInputs(List<PartsCharacter> Characters, List<RouteSub> Subs, List<PartsRule> Rules, List<FightClubRow> FightClub,
        List<SubHistoryPoint> History, List<VoyageSector> Voyages);

    /// <summary>Fight Club's next more profitable build, when the Sub Rules have nothing ahead.</summary>
    public sealed record FightClubNext(int Rank, string[] Build, string Route, double Net, double? Gain);

    /// <summary>How a date was worked out: "voyages" (voyage by voyage, with the sub's own bonus), "calculated" (from game
    /// data alone), "measured" (from its last week's experience), or null.</summary>
    public sealed record SubBonus(double Factor, int Voyages);

    public sealed record SubNextBuild(int Rank, string Build, string[] Parts, double? At, int? Voyages, FightClubNext? FightClub);

    public sealed record SubRankUp(string? Method, bool OutAtSea, double? PerDay, (double Exp, double Hours)? Voyage, SubBonus? Bonus, bool BuildAhead,
        int MaxRank, int? NextRank, double? NextRankAt, int? Voyages, SubNextBuild? NextBuild);

    /// <summary>A sub with everything the Subs page shows (gil a day rounded to whole gil, as the app; dates in milliseconds).</summary>
    public sealed record SubRoute(long Cid, string SubName, int? Slot, int Rank, string CurrentSetup, string CurrentRoute, (int Id, string Name)? CurrentMap,
        string TargetRoute, double? TargetGilDay, double? CurrentGilDay, double? Speed, double? Range, int? RankProgressPct, bool CraftEnabled,
        long? ReturnTime, SubRankUp RankUp)
    {
        /// <summary>Not on the route Fight Club says is best at its rank (rank 85 and up): the Route Optimization page.</summary>
        public bool Suboptimal => TargetRoute.Length > 0 && TargetRoute != CurrentRoute;

        /// <summary>Target Gil/Day less Current Gil/Day, when both are known.</summary>
        public double? Gain => TargetGilDay is double t && CurrentGilDay is double c ? t - c : null;
    }

    public static class SubRoutes
    {
        private const double Hour = 3600 * 1000;
        private const double Day = 24 * Hour;
        private const double MeasureWindowDays = 7;
        private const double BonusWindowDays = 14;
        private const double MinMeasuredDays = 1;
        private const double FixedVoyageSeconds = 12 * 60 * 60;
        private const int RecentVoyages = 10;
        private const int MinSamples = 2;
        private const double MinRatio = 0.5;
        private const double MaxRatio = 4;
        private const int MaxVoyages = 5000;

        // Letter -> SubmarinePart class: Whale, Unkiu, Shark, Coelacanth, Syldra, then the same modified (as the app's
        // LETTER_TO_PART_CLASS_ID, checked against the game's tooltips there).
        private static readonly Dictionary<string, int> PartClass = new()
        {
            ["W"] = 1, ["U"] = 2, ["S"] = 3, ["C"] = 4, ["Y"] = 5, ["W+"] = 6, ["U+"] = 7, ["S+"] = 8, ["C+"] = 9, ["Y+"] = 10,
        };

        /// <summary>JavaScript's Math.round: halves up (towards positive infinity), not to even.</summary>
        public static double Round(double x) => Math.Floor(x + 0.5);

        // JavaScript's Math.hypot as V8 does it (largest first, then a compensated sum), so voyage lengths, cut down to
        // whole seconds, come out the same to the last second.
        private static double Hypot(double a, double b, double c)
        {
            var values = new[] { Math.Abs(a), Math.Abs(b), Math.Abs(c) };
            var max = values.Max();
            if (max == 0) return 0;
            double sum = 0, compensation = 0;
            foreach (var v in values)
            {
                var n = v / max;
                var summand = n * n - compensation;
                var preliminary = sum + summand;
                compensation = preliminary - sum - summand;
                sum = preliminary;
            }
            return Math.Sqrt(sum) * max;
        }

        // ---- submarine-builds.js ----

        /// <summary>A Fight Club route ("MROJZ") with dashes between its sectors' letters ("M-R-O-J-Z"), two-letter ones kept whole.</summary>
        public static string FormatRoute(string? route, ISet<string> letters)
        {
            if (string.IsNullOrEmpty(route)) return "";
            var tokens = new List<string>();
            var i = 0;
            while (i < route.Length)
            {
                var two = route.Substring(i, Math.Min(2, route.Length - i));
                if (two.Length == 2 && letters.Contains(two)) { tokens.Add(two); i += 2; }
                else { tokens.Add(route.Substring(i, 1)); i += 1; }
            }
            return string.Join("-", tokens);
        }

        private sealed record Option(FightClubRow Row, int Rank, string[] Parts, double Net);

        private static List<Option> Options(IEnumerable<FightClubRow> rows) => rows
            .Select(r => PartsPlanner.ParseRankBuild(r.RecBuild) is { } b ? new Option(r, b.Rank, b.Parts, PartsPlanner.NetGilPerDay(r.AsParts, b.Parts)) : null)
            .OfType<Option>().ToList();

        private static Option? BestOf(List<Option> options, int rank)
        {
            if (rank < 85) return null;
            Option? best = null;
            foreach (var o in options) if (rank >= o.Rank && (best == null || o.Net > best.Net)) best = o;
            return best;
        }

        /// <summary>The first rank from now up to top at which Fight Club's best option needs another build and makes more
        /// gil a day after repair kits than now (the sub's Current Gil/Day, else its best option now).</summary>
        public static FightClubNext? NextFightClubBuild(int rank, int top, string[] currentParts, double? currentNet, List<FightClubRow> rows, ISet<string> letters)
        {
            var options = Options(rows);
            var now = BestOf(options, rank);
            var baseline = currentNet ?? now?.Net;
            var current = string.Join("-", currentParts);
            for (var r = rank; r <= top; r++)
            {
                var option = BestOf(options, r);
                if (option == null || string.Join("-", option.Parts) == current) continue;
                if (baseline is double b && option.Net <= b) continue;
                return new FightClubNext(r, option.Parts, FormatRoute(option.Row.Route, letters), Round(option.Net), baseline is double b2 ? Round(option.Net - b2) : null);
            }
            return null;
        }

        /// <summary>Current Gil/Day: the sub's route and parts against Fight Club's rows for that route (the first whose
        /// build ranks it clears), after repair kits; null when none matches.</summary>
        public static double? CurrentGilPerDay(int rank, string currentRoute, string[] parts, List<FightClubRow> rows)
        {
            if (currentRoute.Length == 0) return null;
            var name = currentRoute.Replace("-", "");
            foreach (var row in rows.Where(r => r.Route == name))
            {
                var unmod = PartsPlanner.ParseRankBuild(row.UnmodMinRank);
                var modded = PartsPlanner.ParseRankBuild(row.MinRankBuild);
                if ((unmod != null && rank >= unmod.Value.Rank) || (modded != null && rank >= modded.Value.Rank))
                    return Round(PartsPlanner.NetGilPerDay(row.AsParts, parts));
            }
            return null;
        }

        /// <summary>A sub's speed or range: its rank's plus each part's; null when the rank or a part isn't known.</summary>
        public static double? Stat(int rank, string[] parts, bool speed, Dictionary<int, SubRank> ranks, Dictionary<(int, int), SubPartStat> partStats)
        {
            if (!ranks.TryGetValue(rank, out var r) || (speed ? r.Speed : r.Range) is not double total) return null;
            for (var i = 0; i < 4; i++)
            {
                if (!PartClass.TryGetValue(parts[i], out var cls) || !partStats.TryGetValue((cls, i), out var p)) return null;
                total += speed ? p.Speed : p.Range;
            }
            return total;
        }

        // ---- rank-up.js ----

        private static double TotalExp(int rank, double exp, Dictionary<int, double> expToNext)
        {
            var total = exp;
            for (var r = 1; r < rank; r++) total += expToNext.TryGetValue(r, out var e) ? e : 0;
            return total;
        }

        private sealed record Point(double At, double Value);

        private static List<Point> TotalExpSeries(List<Point> rankPoints, List<Point> expPoints, Dictionary<int, double> expToNext)
        {
            static Point? Latest(List<Point> points, double at)
            {
                Point? found = null;
                foreach (var p in points) if (p.At <= at && (found == null || p.At >= found.At)) found = p;
                return found;
            }
            var output = new List<Point>();
            foreach (var at in rankPoints.Concat(expPoints).Select(p => p.At).Distinct().OrderBy(x => x))
            {
                var rank = Latest(rankPoints, at);
                var exp = Latest(expPoints, at);
                if (rank != null && exp != null) output.Add(new Point(at, TotalExp((int)rank.Value, exp.Value, expToNext)));
            }
            return output;
        }

        private static (double PerDay, double SpanDays)? MeasuredRate(List<Point> points, double windowStart, double end)
        {
            var sorted = points.Where(p => p.At <= end).OrderBy(p => p.At).ToList();
            var first = 0;
            for (var i = 1; i < sorted.Count; i++) if (sorted[i].Value < sorted[i - 1].Value) first = i;
            var usable = sorted.Skip(first).ToList();
            if (usable.Count == 0) return null;
            var start = usable[0];
            foreach (var p in usable) if (p.At <= windowStart) start = p;
            var from = Math.Max(windowStart, start.At);
            var spanDays = (end - from) / Day;
            if (spanDays < MinMeasuredDays) return null;
            var gained = usable[^1].Value - start.Value;
            return (Math.Max(0, gained) / spanDays, spanDays);
        }

        private static (double Exp, double Hours)? Voyage(List<SubSector?> sectors, SubSector? start, double speed)
        {
            if (sectors.Count == 0 || sectors.Count > 5 || start == null || sectors.Any(s => s == null)) return null;
            var s = Math.Max(1, speed);
            var seconds = FixedVoyageSeconds;
            var previous = start;
            double exp = 0;
            foreach (var sector in sectors)
            {
                seconds += Math.Floor(Hypot(previous.X - sector!.X, previous.Y - sector.Y, previous.Z - sector.Z) * 3990 / (s * 100) * 60);
                seconds += Math.Floor(sector.SurveyMinutes * 7000 / (s * 100) * 60);
                exp += sector.ExpReward;
                previous = sector;
            }
            return (exp, seconds / 3600);
        }

        private static SubSector? StartFor(int sectorId, List<SubSector> startingPoints)
        {
            SubSector? found = null;
            foreach (var p in startingPoints) if (p.Id <= sectorId && (found == null || p.Id > found.Id)) found = p;
            return found;
        }

        private static double AtCollection(double at, double? returnAt, double? hours, double now)
        {
            if (!(returnAt > now)) return at;
            if (at <= returnAt) return returnAt.Value;
            if (!(hours > 0)) return at;
            return returnAt.Value + Math.Ceiling((at - returnAt.Value) / (hours.Value * Hour)) * hours.Value * Hour;
        }

        // null: never at this rate, or the table runs out; Reached: already there; else the date.
        private static (bool Reached, double? At)? RankDate(int rank, double exp, int targetRank, Dictionary<int, double> expToNext,
            (double PerDay, double SpanDays) measured, double? returnAt, double? hours, double now)
        {
            if (rank >= targetRank) return (true, null);
            var needed = -exp;
            for (var r = rank; r < targetRank; r++)
            {
                var step = expToNext.TryGetValue(r, out var e) ? e : 0;
                if (step == 0) return null;
                needed += step;
            }
            if (!(measured.PerDay > 0)) return null;
            return (false, AtCollection(now + needed / measured.PerDay * Day, returnAt, hours, now));
        }

        private static int MaxRank(Dictionary<int, double> expToNext)
        {
            var r = 1;
            while (expToNext.TryGetValue(r, out var e) && e > 0) r++;
            return r;
        }

        private static (int Rank, string[] Build)? NextBuildRank(int rank, int top, Func<int, string[]?> buildAt)
        {
            static string Key(string[]? b) => b == null ? "" : string.Join("-", b);
            var now = Key(buildAt(rank));
            for (var r = rank + 1; r <= top; r++)
            {
                var next = buildAt(r);
                if (next != null && Key(next) != now) return (r, next);
            }
            return null;
        }

        // ---- voyage-forecast.js ----

        private sealed record Collection(double From, double At, double Gained);

        private static List<Collection> Collections(List<Point> points)
        {
            var sorted = points.OrderBy(p => p.At).ToList();
            var output = new List<Collection>();
            var lastAt = sorted.Count > 0 ? sorted[0].At : 0;
            for (var i = 1; i < sorted.Count; i++)
            {
                var gained = sorted[i].Value - sorted[i - 1].Value;
                if (gained < 0) { output = new List<Collection>(); lastAt = sorted[i].At; continue; }
                if (gained > 0) { output.Add(new Collection(lastAt, sorted[i].At, gained)); lastAt = sorted[i].At; }
            }
            return output;
        }

        private static SubBonus? BonusFactor(List<(double Gained, double? Base)> samples)
        {
            var ratios = samples.Where(s => s.Base > 0 && s.Gained > 0).Select(s => s.Gained / s.Base!.Value)
                .Where(r => r >= MinRatio && r <= MaxRatio).ToList();
            ratios = ratios.Skip(Math.Max(0, ratios.Count - RecentVoyages)).ToList();
            if (ratios.Count < MinSamples) return null;
            var sorted = ratios.OrderBy(x => x).ToList();
            var mid = sorted.Count / 2;
            var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
            return new SubBonus(median, ratios.Count);
        }

        private static Dictionary<int, (double At, int Voyages)> Forecast(int rank, double exp, int untilRank, Dictionary<int, double> expToNext,
            Func<int, (double Exp, double Hours)?> voyageAt, double? returnAt, double now)
        {
            var reached = new Dictionary<int, (double At, int Voyages)>();
            var r = rank;
            var into = exp;
            var current = voyageAt(r);
            if (current is not { } c0 || !(c0.Exp > 0) || !(c0.Hours > 0)) return reached;
            var cur = c0;
            var at = returnAt is double ra && ra != 0 ? Math.Max(now, ra) : now + cur.Hours * Hour;
            double Step(int x) => expToNext.TryGetValue(x, out var e) ? e : 0;
            for (var n = 1; n <= MaxVoyages && r < untilRank; n++)
            {
                into += cur.Exp;
                for (var step = Step(r); step > 0 && into >= step && r < untilRank; step = Step(r))
                {
                    into -= step;
                    r += 1;
                    reached[r] = (at, n);
                }
                if (!(Step(r) > 0)) break;
                var next = voyageAt(r);
                if (next is not { } nx || !(nx.Exp > 0) || !(nx.Hours > 0)) break;
                cur = nx;
                at += cur.Hours * Hour;
            }
            return reached;
        }

        /// <summary>Look ahead on the Parts page (ipc/parts.js upcomingBuilds): each sub's next build due within `aheadDays`, by
        /// character and slot; one it can switch to already is due now.</summary>
        public static Dictionary<(long Cid, int Slot), (int Rank, string[] Parts, double DueAt)> Upcoming(IEnumerable<SubRoute> subs, double now, int aheadDays)
        {
            var until = now + aheadDays * Day;
            var output = new Dictionary<(long, int), (int, string[], double)>();
            foreach (var s in subs)
            {
                if (s.RankUp.NextBuild is not { } next || s.Slot is not int slot || slot == 0) continue;
                double? dueAt = next.Rank <= s.Rank ? now : next.At;
                if (dueAt is not double due || due == 0 || due > until) continue;
                output[(s.Cid, slot)] = (next.Rank, next.Parts, due);
            }
            return output;
        }

        // ---- ipc/subs.js loadSubmarines and addRankUps ----

        /// <summary>Every built sub's route answers, as the app's Subs tab, at `now` (milliseconds since 1970).</summary>
        public static List<SubRoute> Plan(RoutesInputs input, SubGameData game, double now)
        {
            var fightClub = input.FightClub.OrderBy(f => f.Id).ToList();
            var rules = input.Rules.OrderBy(r => r.Id).ToList();
            var partsRoutes = fightClub.Select(f => f.AsParts).ToList();
            var kitCost = fightClub.Count > 0 ? fightClub[0].RepairKitCost : 0;

            // Game data, as the app reads its copy: sector letters (a sector with none is left out), maps, ranks, parts.
            var letterOf = new Dictionary<int, string>();
            var mapOf = new Dictionary<int, (int, string)>();
            var sectors = new Dictionary<int, SubSector>();
            var startingPoints = new List<SubSector>();
            foreach (var s in game.Sectors)
            {
                if (s.Id != 0 && s.Location.Length > 0) letterOf[s.Id] = s.Location;
                if (s.Id > 0 && s.Map > 0) mapOf[s.Id] = (s.Map, game.MapNames.TryGetValue(s.Map, out var mapName) ? mapName : "");
                if (s.Starting) startingPoints.Add(s); else sectors[s.Id] = s;
            }
            var letters = new HashSet<string>(letterOf.Values);
            var ranks = new Dictionary<int, SubRank>();
            foreach (var r in game.Ranks) ranks[r.Id] = r;
            var partStats = new Dictionary<(int, int), SubPartStat>();
            foreach (var p in game.Parts) partStats[(p.Class, p.Slot)] = p;
            var topRank = Math.Max(0, ranks.Values.Where(r => r.Speed != null).Select(r => r.Id).DefaultIfEmpty(0).Max());
            var expToNext = ranks.ToDictionary(kv => kv.Key, kv => kv.Value.ExpToNext);
            var top = MaxRank(expToNext);

            var windowStart = now - MeasureWindowDays * Day;
            var historyStart = now - BonusWindowDays * Day;
            var history = input.History.GroupBy(h => (h.Cid, h.Metric)).ToDictionary(g => g.Key, g => g.Select(p => new Point(p.At, p.Value)).ToList());
            // Each voyage's sectors (in the order run), by FC and sub name.
            var voyageRoutes = input.Voyages.Where(v => v.SubName != null && v.At >= historyStart)
                .GroupBy(v => (v.FcId, v.SubName, v.Register, v.At))
                .Select(g => (Key: $"{g.Key.FcId}|{g.Key.SubName}", At: g.Key.At,
                    Sectors: g.OrderBy(v => v.Leg == null ? 1 : 0).ThenBy(v => v.Leg ?? 0).ThenBy(v => v.Sector).Select(v => v.Sector).ToList()))
                .GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Select(v => (v.At, v.Sectors)).ToList());
            double? RouteExp(List<int> ids) => ids.Count > 0 && ids.All(sectors.ContainsKey) ? ids.Sum(id => sectors[id].ExpReward) : null;

            var characters = input.Characters.ToDictionary(c => c.Cid);
            var output = new List<SubRoute>();
            foreach (var sub in input.Subs)
            {
                var parts = new[] { sub.Part1, sub.Part2, sub.Part3, sub.Part4 }.Select(PartsPlanner.PartLetter).ToArray();
                var rank = sub.Rank;
                var stops = (sub.Points ?? Array.Empty<byte>()).Where(b => b > 0).Select(b => (int)b).ToList();
                var currentRoute = string.Join("-", stops.Select(b => letterOf.TryGetValue(b, out var l) ? l : $"?{b}"));
                var best = BestOf(Options(fightClub), rank);
                var targetRoute = best == null ? "" : FormatRoute(best.Row.Route, letters);
                double? targetGil = best == null ? null : Round(best.Net);
                var currentGil = CurrentGilPerDay(rank, currentRoute, parts, fightClub);
                var next = sub.NextLevelExp ?? 0;
                var cur = sub.CurrentExp ?? 0;
                int? progress = next > 0 ? (int)Round((double)cur / next * 100) : null;
                var fcNext = NextFightClubBuild(rank, topRank, parts, currentGil, fightClub, letters);
                var speed = Stat(rank, parts, true, ranks, partStats);
                var range = Stat(rank, parts, false, ranks, partStats);
                (int, string)? map = stops.Count > 0 && mapOf.TryGetValue(stops[0], out var m) ? m : null;

                // ---- addRankUps ----
                var cid = sub.Cid;
                var slotKey = sub.Slot?.ToString(CultureInfo.InvariantCulture) ?? "null";
                var series = TotalExpSeries(history.GetValueOrDefault((cid, $"sub_rank:{slotKey}")) ?? new List<Point>(),
                    history.GetValueOrDefault((cid, $"sub_exp:{slotKey}")) ?? new List<Point>(), expToNext);
                var latestAt = series.Count > 0 ? series[^1].At : 0;
                var measured = series.Count > 0 ? MeasuredRate(series, windowStart, Math.Max(now, latestAt)) : null;

                var routeSectors = stops.Select(id => sectors.TryGetValue(id, out var s) ? s : null).ToList();
                var start = stops.Count > 0 ? StartFor(stops[0], startingPoints) : null;
                var trip = speed is double sp ? Voyage(routeSectors, start, sp) : null;
                double? returnAt = sub.ReturnTime is long rt && rt > 0 ? rt * 1000.0 : null;

                var slot = sub.Slot ?? -1;
                string[]? BuildAt(int r) => PartsPlanner.TargetBuild(r, slot, cid, sub.CraftEnabled, rules, partsRoutes);
                var fromRules = NextBuildRank(rank, top, BuildAt);
                var fightClubNext = fromRules == null && fcNext != null && fcNext.Rank <= top ? fcNext : null;
                (int Rank, string[] Build)? switchTo = fromRules ?? (fightClubNext != null ? (fightClubNext.Rank, fightClubNext.Build) : null);
                var untilRank = Math.Min(top, Math.Max(rank + 1, switchTo?.Rank ?? 0));

                var fcId = sub.FcId ?? (characters.TryGetValue(cid, out var ch) ? ch.FcId : null);
                var recorded = voyageRoutes.GetValueOrDefault($"{fcId ?? "null"}|{sub.SubName}") ?? new List<(double At, List<int> Sectors)>();
                var samples = Collections(series).Select(c =>
                {
                    var these = recorded.Where(v => v.At > c.From && v.At <= c.At).ToList();
                    var bases = these.Select(v => RouteExp(v.Sectors)).ToList();
                    double? b = these.Count > 0 ? (bases.All(x => x != null) ? bases.Sum(x => x!.Value) : null) : trip?.Exp;
                    return (c.Gained, b);
                }).ToList();
                var bonus = trip != null ? BonusFactor(samples) : null;

                string[] LettersAt(int r)
                {
                    if (switchTo == null || r < switchTo.Value.Rank) return parts;
                    var build = (fightClubNext != null ? fightClubNext.Build : BuildAt(r)) ?? Array.Empty<string>();
                    return parts.Select((letter, i) => i < build.Length && !string.IsNullOrEmpty(build[i]) ? build[i] : letter).ToArray();
                }
                (double Exp, double Hours)? VoyageAt(int r)
                {
                    var s = Stat(r, LettersAt(r), true, ranks, partStats);
                    var v = s is double x ? Voyage(routeSectors, start, x) : null;
                    return v is { } vv ? (vv.Exp * (bonus?.Factor ?? 1), vv.Hours) : null;
                }
                var reached = trip != null ? Forecast(rank, cur, untilRank, expToNext, VoyageAt, returnAt, now) : new Dictionary<int, (double At, int Voyages)>();

                string? method = null;
                Func<int, (bool Reached, double? At, int? Voyages)?> dateFor = _ => null;
                if (reached.Count > 0)
                {
                    method = bonus != null ? "voyages" : "calculated";
                    dateFor = r => reached.TryGetValue(r, out var d) ? (false, d.At, d.Voyages) : null;
                }
                else if (measured is { } meas)
                {
                    method = "measured";
                    dateFor = r => RankDate(rank, cur, r, expToNext, meas, returnAt, trip?.Hours, now) is { } d ? (d.Reached, d.At, null) : null;
                }

                var nextDate = rank < top ? dateFor(rank + 1) : null;
                var switchAt = switchTo != null ? dateFor(switchTo.Value.Rank) : null;
                var firstVoyage = trip != null ? VoyageAt(rank) : null;
                var rankUp = new SubRankUp(
                    method,
                    method == "measured" && returnAt > now,
                    method == "measured" ? measured!.Value.PerDay : null,
                    firstVoyage is { } fv && method != "measured" ? (Round(fv.Exp), fv.Hours) : null,
                    method == "voyages" ? bonus : null,
                    method != "measured" && switchTo != null && switchAt != null,
                    top,
                    rank < top ? rank + 1 : null,
                    nextDate?.At is double na && na != 0 ? na : null,
                    nextDate?.Voyages is int nv && nv != 0 ? nv : null,
                    switchTo is { } sw ? new SubNextBuild(sw.Rank, string.Concat(sw.Build.Select(l => string.IsNullOrEmpty(l) ? "·" : l)),
                        sw.Build.Select(l => l ?? "").ToArray(),
                        switchAt?.At is double sa && sa != 0 ? sa : null, switchAt?.Voyages is int sv && sv != 0 ? sv : null, fightClubNext) : null);

                output.Add(new SubRoute(cid, sub.SubName, sub.Slot, rank, string.Join("-", parts), currentRoute, map, targetRoute, targetGil, currentGil,
                    speed, range, progress, sub.CraftEnabled, sub.ReturnTime, rankUp));
            }
            return output;
        }
    }
}
