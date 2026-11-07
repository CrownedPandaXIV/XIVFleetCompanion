using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace XIVFleetCompanion.Windows;

// 2.x routes stage, as the app's Subs and Route Optimization tabs: each sub's route, gil a day after repair kits (its
// own route against Fight Club, and Fight Club's best at its rank), speed and range, when it ranks up and changes build.
// Worked out here (SubRoutes) from the database's fleet_route_* views and the game's own files, read with Parts every
// 5 minutes.
public partial class MainWindow
{
    private string routesFilter = "";
    private FleetTable<SubRoute>? routesTable;
    private RoutesRead? routeIndexFor;
    private Dictionary<(long, string), SubRoute> routeIndex = new();
    private Dictionary<long, (double? Expected, int Unmatched)> expectedIndex = new();

    private static double NowMs => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

    // Looked up by character and sub name, made again only when a new read is in.
    private void IndexRoutes()
    {
        var read = reader.Routes.Data;
        if (ReferenceEquals(read, routeIndexFor)) return;
        routeIndexFor = read;
        routeIndex = read?.Subs.GroupBy(s => (s.Cid, s.SubName)).ToDictionary(g => g.Key, g => g.First()) ?? new();
        // Income's expected: the character's subs' Current Gil/Day added up (none when none matches Fight Club).
        expectedIndex = read?.Subs.GroupBy(s => s.Cid).ToDictionary(g => g.Key, g =>
        {
            var matched = g.Where(s => s.CurrentGilDay != null).ToList();
            return (matched.Count > 0 ? matched.Sum(s => s.CurrentGilDay!.Value) : (double?)null, g.Count() - matched.Count);
        }) ?? new();
    }

    private SubRoute? RouteOf(long cid, string subName)
    {
        IndexRoutes();
        return routeIndex.TryGetValue((cid, subName), out var r) ? r : null;
    }

    private (double? Expected, int Unmatched)? ExpectedOf(long cid)
    {
        IndexRoutes();
        return expectedIndex.TryGetValue(cid, out var e) ? e : null;
    }

    private List<SubRoute> Suboptimal()
    {
        IndexRoutes();
        return routeIndexFor?.Subs.Where(s => s.Suboptimal)
            .OrderBy(s => s.Gain == null ? 1 : 0).ThenByDescending(s => s.Gain ?? 0).ToList() ?? new List<SubRoute>();
    }

    /// <summary>"in 2d 5h", "in 7h", "in 40 min" until a time (milliseconds); "now" once it's past, as the app.</summary>
    public static string TimeUntil(double at, double now)
    {
        var minutes = SubRoutes.Round((at - now) / 60000);
        if (minutes <= 0) return "now";
        if (minutes < 60) return $"in {minutes:0} min";
        var hours = Math.Floor(minutes / 60);
        if (hours < 24) return $"in {hours:0}h";
        var days = Math.Floor(hours / 24);
        return hours % 24 != 0 ? $"in {days:0}d {hours % 24:0}h" : $"in {days:0}d";
    }

    private static string LocalDate(double at) => DateTime.UnixEpoch.AddMilliseconds(at).ToLocalTime().ToString("ddd MMM d, HH:mm", CultureInfo.CurrentCulture);

    // How a date was worked out, for the hover, as the app's.
    private static string RankUpBasis(SubRankUp r)
    {
        const string ahead = "Assumes it carries on as now: moving to higher-experience routes as they unlock, or a faster build, would make it sooner.";
        const string route = "Assumes it stays on its current route: moving to a higher-experience route would make it sooner.";
        var later = r.NextBuild != null && r.BuildAhead ? " Voyages get shorter as it ranks up and once it switches to its next build." : " Voyages get shorter as it ranks up.";
        return r.Method switch
        {
            "voyages" when r.Voyage is { } v && r.Bonus is { } b =>
                $"Worked out voyage by voyage: {Number((long)v.Exp)} experience a voyage on its route, including its bonus (×{b.Factor:0.00} from its last {b.Voyages} voyages), {v.Hours:0.0}h each.{later} {route}",
            "measured" => $"From its experience over the last week: {Number((long)SubRoutes.Round(r.PerDay ?? 0))} a day. {ahead}"
                          + (r.OutAtSea ? " Experience comes in when a voyage is collected, so this is when it is back." : ""),
            "calculated" when r.Voyage is { } v =>
                $"Calculated from its route: {Number((long)v.Exp)} experience per {v.Hours:0.0}h voyage, without bonus experience.{later} {route} Its own bonus is used once a few of its voyages are recorded.",
            _ => "",
        };
    }

    // "≈ in 2d 5h" (≈ when calculated from game data alone), the date and how on hover.
    private void RankUpWhen(SubRankUp r, double? at, int? voyages, string label, Vector4? color = null)
    {
        if (at is not double when)
        {
            Colored(Dim, r.Method == "measured" ? "No progress" : "—");
            Hover(r.Method == "measured" ? "No experience gained over the last week." : "No route or game data to work it out from.");
            return;
        }
        var text = (r.Method == "calculated" ? "≈ " : "") + TimeUntil(when, NowMs);
        if (color is Vector4 c) Colored(c, text); else ImGui.TextUnformatted(text);
        Hover($"{label} about {LocalDate(when)}{(voyages is int n ? $" ({n} voyage{(n == 1 ? "" : "s")})" : "")}. {RankUpBasis(r)}");
    }

    private void NextRankCell(SubRoute? s)
    {
        if (s == null) { Colored(Dim, "—"); return; }
        var r = s.RankUp;
        if (r.NextRank == null) { Colored(Dim, "Max rank"); Hover($"Rank {r.MaxRank} is the highest."); return; }
        RankUpWhen(r, r.NextRankAt, r.Voyages, $"Rank {r.NextRank}");
    }

    private void NextBuildCell(SubRoute? s)
    {
        var b = s?.RankUp.NextBuild;
        if (s == null || b == null)
        {
            Colored(Dim, "—");
            Hover("No build change ahead in Sub Rules, and nothing in Fight Club makes more after repair kits.");
            return;
        }
        // Due now in green (it can switch already).
        var now = b.Rank <= s.Rank;
        if (now) Colored(Green, $"{b.Build} at {b.Rank}"); else ImGui.TextUnformatted($"{b.Build} at {b.Rank}");
        if (b.FightClub is { } f)
            Hover($"From Fight Club (your Sub Rules have no change ahead): on {f.Route}, {Gil(f.Net)} gil a day after repair kits" + (f.Gain is double g ? $", {Gil(g)} more than now." : "."));
        ImGui.SameLine(0, 0);
        if (now) Colored(Dim, " · now");
        else if (b.At != null)
        {
            Colored(Dim, " · ");
            ImGui.SameLine(0, 0);
            RankUpWhen(s.RankUp, b.At, b.Voyages, $"Rank {b.Rank}", Dim);
        }
    }

    // "2 · O-J": the map's number (softer, its name on hover) before the route's letters, which every map reuses.
    private void RouteCell(string route, (int Id, string Name)? map)
    {
        if (route.Length == 0) { Colored(Dim, "—"); return; }
        if (map is { } m)
        {
            Colored(Dim, $"{m.Id} · ");
            Hover(m.Name.Length > 0 ? m.Name : $"Map {m.Id}");
            ImGui.SameLine(0, 0);
        }
        ImGui.TextUnformatted(route);
    }

    private void GilDayCell(double? gil, string why)
    {
        if (gil is double g) GilCell((long)SubRoutes.Round(g));
        else { Colored(Dim, "—"); Hover(why); }
    }

    // "3 hours", "40 minutes", "2.5 days": how far a Next Rank date was off, as the app.
    private static string OffText(double ms)
    {
        var minutes = SubRoutes.Round(Math.Abs(ms) / 60000);
        if (minutes < 60) return $"{minutes:0} minute{(minutes == 1 ? "" : "s")}";
        var hours = minutes / 60;
        if (hours < 48)
        {
            var shown = hours < 10 ? SubRoutes.Round(hours * 10) / 10 : SubRoutes.Round(hours);
            return $"{shown.ToString("0.#", CultureInfo.CurrentCulture)} hour{(shown == 1 ? "" : "s")}";
        }
        return $"{(SubRoutes.Round(hours / 24 * 10) / 10).ToString("0.#", CultureInfo.CurrentCulture)} days";
    }

    // The Subs page's forecast line, as the app's: how close the Next Rank dates (worked out a day or more before) came
    // to the real rank-ups, each recent one on hover. The app saves the dates every hour while it's open.
    private void ForecastLine()
    {
        if (reader.Forecasts.Data is not { } a) return;
        Colored(Dim, "Next Rank forecasts: " + (a.Count > 0 && a.AverageOff is double off
            ? $"within {OffText(off)} on average over the last {a.Count} rank-up{(a.Count == 1 ? "" : "s")}" : "no rank-ups to check yet"));
        string When(double ms) => DateTime.UnixEpoch.AddMilliseconds(ms).ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture);
        var lines = a.Checks.Select(c => $"{c.SubName}{(c.Character.Length > 0 ? $" ({c.Character})" : "")} rank {c.Rank}: said {When(c.PredictedAt)}, ranked up {When(c.At)}, " +
            $"{(Math.Abs(c.Off) < 60000 ? "on time" : $"{OffText(c.Off)} {(c.Off > 0 ? "late" : "early")}")}; worked out {OffText(c.At - c.MadeAt)} before").ToList();
        Hover((WithApp ? "The app saves each sub's Next Rank date every hour while it's open." : "The plugin saves each sub's Next Rank date every hour while a game shows the fleet.") + " The average counts dates worked out a day or more before the rank-up, " +
              "early or late; a rank-up counts from when its voyage came back." + (lines.Count > 0 ? "\n\nRecent rank-ups:\n" + string.Join("\n", lines) : ""));
    }

    private const string NoCurrentGil = "Its route isn't in Fight Club, or its rank is below the route's builds.";
    private string NoTargetGil => "Fight Club recommends routes from rank 85 (import its data in Settings" + (WithApp ? ", here or in the app)." : ").");

    private FleetTable<SubRoute> RoutesTable => routesTable ??= new FleetTable<SubRoute>("routeOpt", plugin.Configuration, new List<FleetColumn<SubRoute>>
    {
        new("character", "Character", s => CharacterCell(CharacterOf(s.Cid).Name, CharacterOf(s.Cid).World), s => CharacterOf(s.Cid).Name, Locked: true),
        new("sub", "Sub", s => ImGui.TextUnformatted(s.SubName), s => s.SubName),
        new("rank", "Rank", s => RightNumber(s.Rank), s => s.Rank),
        new("currentRoute", "Current Route", s => RouteCell(s.CurrentRoute, s.CurrentMap), s => s.CurrentRoute),
        new("currentGil", "Current Gil/Day", s => GilDayCell(s.CurrentGilDay, NoCurrentGil), s => s.CurrentGilDay ?? double.MinValue, s => s.CurrentGilDay == null),
        new("targetRoute", "Target Route", s => ImGui.TextUnformatted(s.TargetRoute), s => s.TargetRoute),
        new("targetGil", "Target Gil/Day", s => GilDayCell(s.TargetGilDay, NoTargetGil), s => s.TargetGilDay ?? double.MinValue, s => s.TargetGilDay == null),
        new("gain", "Gil/Day Gain", s =>
        {
            if (s.Gain is double g) TableCells.Right((g >= 0 ? "+" : "−") + Gil(Math.Abs(g)), g > 0 ? Green : g < 0 ? Red : (Vector4?)null);
            else { Colored(Dim, "—"); Hover("Its current route isn't in Fight Club, so the gain can't be worked out: it should still switch."); }
        }, s => s.Gain ?? double.MinValue, s => s.Gain == null),
    });

    // A character's name and world, for the Route Optimization page (from the Parts read's characters).
    private (string Name, string World) CharacterOf(long cid)
    {
        if (characterNames.TryGetValue(cid, out var known)) return known;
        var c = reader.Subs.Data?.FirstOrDefault(x => x.Cid == cid);
        var found = c != null ? (c.Character, c.World) : (cid.ToString(CultureInfo.InvariantCulture), "");
        if (c != null) characterNames[cid] = found;
        return found;
    }
    private readonly Dictionary<long, (string, string)> characterNames = new();

    private void DrawRoutes()
    {
        reader.ReadIfDue();
        RefreshButton();
        FilterBox(ref routesFilter, "routes");
        ReadLine(reader.Routes, FleetReader.PartsEvery);
        if (reader.Routes.Data == null) return;
        var subs = Suboptimal();
        Heading(subs.Count == 0 ? "Every sub from rank 85 is on its Target Route." : $"{subs.Count} sub{(subs.Count == 1 ? " isn't" : "s aren't")} on the best route for {(subs.Count == 1 ? "its" : "their")} rank");
        var gain = subs.Where(s => s.Gain > 0).Sum(s => s.Gain!.Value);
        if (gain > 0) GilLine("Switching them all would make about", gain, " more gil a day.");
        Colored(Dim, "Subs from rank 85 whose route isn't Fight Club's best at their rank (most gil a day after repair kits). Biggest gain first; " +
                     "a sub on a route Fight Club doesn't list has no gain worked out but should still switch.");
        var rows = subs.Where(s => Matches(routesFilter, CharacterOf(s.Cid).Name, CharacterOf(s.Cid).World, s.SubName, s.CurrentRoute, s.TargetRoute)).ToList();
        if (rows.Count == 0) { if (subs.Count > 0) ImGui.TextUnformatted("Nothing matches."); return; }
        RoutesTable.DrawControls();
        RoutesTable.Draw(rows);
    }
}
