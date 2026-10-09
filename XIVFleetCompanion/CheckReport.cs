using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace XIVFleetCompanion;

/// <summary>
/// "Check what I can see": what the plugin can read for each character (AutoRetainer, AllaganTools,
/// FCTracker) and when each part was last stored, as plain text that can be copied and shared. The
/// plugin gathers the facts (CheckWindow); this only words them. No Dalamud here, so it is tested on
/// its own (tests/FleetWriterTests).
/// </summary>
public static class CheckReport
{
    // One thing AllaganTools has items for: the character's bags, a retainer or the FC chest.
    // Items is null when AllaganTools has never seen it. StoredAgo is how long ago its items were
    // last stored in the database (null: none stored, or the database was not checked).
    public sealed class Part
    {
        public ulong Id;
        public string Name = "";
        public int? Items;
        public TimeSpan? StoredAgo;
    }

    public sealed class Character
    {
        public ulong Cid;
        public string Name = "";
        public string World = "";
        public int Subs;
        public Part Bags = new() { Name = "Bags" };
        public List<Part> Retainers = new();
        // FcId 0: not in a Free Company (by AutoRetainer).
        public ulong FcId;
        public string? FcName;
        public Part? Chest;
        // From FCTracker: null when it has no entry for this character.
        public FCTrackerConnector.HousingInfo? Housing;
        // When the character's row was last written (null: never, or the database was not checked).
        public TimeSpan? SyncedAgo;
    }

    public sealed class Facts
    {
        public string Version = "";
        public DateTime At;
        public string AccountLabel = "";
        public bool AutoRetainerReady;
        public int Registered;
        public int LeftOut;
        public bool AllaganToolsReady;
        public string FcTrackerPath = "";
        public bool FcTrackerFound;
        // Whether FCTracker is loaded in this game (null: Dalamud could not be asked).
        public bool? FcTrackerRunning;
        // Null when the database was checked; otherwise why not.
        public string? DatabaseProblem;
        public List<Character> Characters = new();
    }

    // FCTracker's housing district ids (the app's housing_key_city_lookup).
    private static readonly Dictionary<int, string> Districts = new()
    {
        [2] = "Lavender Beds", [8] = "Mist", [9] = "Goblet", [70] = "Empyreum", [111] = "Shirogane",
    };

    public static string House(FCTrackerConnector.HousingInfo h)
    {
        if (!h.HasHouse) return "no house";
        var district = h.HouseCity is int city && Districts.TryGetValue(city, out var name) ? name : $"district {h.HouseCity}";
        return $"{district}, Ward {h.HouseWard}, Plot {h.HousePlot}";
    }

    public static string Ago(TimeSpan ago)
    {
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return Plural((int)ago.TotalMinutes, "minute") + " ago";
        if (ago < TimeSpan.FromDays(2)) return Plural((int)ago.TotalHours, "hour") + " ago";
        return Plural((int)ago.TotalDays, "day") + " ago";
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private static string Stored(Part p, bool databaseChecked)
        => !databaseChecked ? "" : p.StoredAgo is TimeSpan ago ? $", stored {Ago(ago)}" : ", nothing stored";

    // What the player can do about one character, in plain sentences (empty when all is well).
    public static List<string> ToFix(Character c, Facts f)
    {
        var fixes = new List<string>();
        if (f.AllaganToolsReady)
        {
            if (c.Bags.Items == null)
                fixes.Add($"Log in as {c.Name} on this PC once, so AllaganTools sees their bags.");
            foreach (var r in c.Retainers.Where(r => r.Items == null))
                fixes.Add($"Open {r.Name} at a summoning bell on this PC.");
            // The FC chest holds the subs' ceruleum, repair kits and salvage: only asked for when the FC has subs.
            if (c.Chest != null && c.Chest.Items == null && FcHasSubs(c.FcId, f))
                fixes.Add($"Open the Free Company chest{(string.IsNullOrEmpty(c.FcName) ? "" : $" of {c.FcName}")} on this PC.");
        }
        if (f.FcTrackerFound && c.FcId != 0 && c.Housing == null)
            fixes.Add($"Log in as {c.Name} with FCTracker running, so it records the Free Company and house.");
        if (f.DatabaseProblem == null && c.SyncedAgo == null)
            fixes.Add("Not in the database yet: it is sent at the next sync.");
        return fixes;
    }

    // Whether any character here in that Free Company has subs.
    public static bool FcHasSubs(ulong fcId, Facts f) => fcId != 0 && f.Characters.Any(x => x.FcId == fcId && x.Subs > 0);

    public static int ToFixCount(Facts f) => f.Characters.Sum(c => ToFix(c, f).Count) + ToolProblems(f).Count;

    // Problems with the other plugins or the database, which affect every character.
    public static List<string> ToolProblems(Facts f)
    {
        var problems = new List<string>();
        if (!f.AutoRetainerReady) problems.Add("AutoRetainer is not running: nothing can be synced.");
        if (!f.AllaganToolsReady) problems.Add("AllaganTools is not running: bags, retainers' items and FC chests are not updated.");
        if (f.DatabaseProblem != null) problems.Add($"Database not checked: {f.DatabaseProblem}");
        return problems;
    }

    // FCTracker is optional (only Free Company house details): running, or installed but not running in this
    // game (its last saved file is still read), or not found.
    public static string FcTrackerLine(Facts f)
    {
        if (f.FcTrackerRunning == true) return $"FCTracker: running ({f.FcTrackerPath})";
        if (f.FcTrackerRunning == false && f.FcTrackerFound)
            return $"FCTracker: not running in this game, optional: its last saved details are used ({f.FcTrackerPath})";
        return f.FcTrackerFound
            ? $"FCTracker: found ({f.FcTrackerPath})"
            : $"FCTracker: not found, optional: only for Free Company house details (Settings → FCTracker Config Path: {f.FcTrackerPath})";
    }

    public static string ToText(Facts f)
    {
        var databaseChecked = f.DatabaseProblem == null;
        var sb = new StringBuilder();
        sb.AppendLine($"XIV Fleet Companion {f.Version}: what this PC can see, {f.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
                      + (string.IsNullOrWhiteSpace(f.AccountLabel) ? "" : $" (account {f.AccountLabel})"));
        sb.AppendLine(f.AutoRetainerReady
            ? $"AutoRetainer: running, {Plural(f.Registered, "character")}" + (f.LeftOut > 0 ? $" ({f.LeftOut} left out in settings)" : "")
            : "AutoRetainer: not running");
        sb.AppendLine($"AllaganTools: {(f.AllaganToolsReady ? "running" : "not running")}");
        sb.AppendLine(FcTrackerLine(f));
        sb.AppendLine($"Database: {(databaseChecked ? "checked" : "not checked, " + f.DatabaseProblem)}");

        var total = ToFixCount(f);
        sb.AppendLine(total == 0 ? "Nothing to fix." : $"{Plural(total, "thing")} to fix (marked !).");
        foreach (var problem in ToolProblems(f))
            sb.AppendLine($"! {problem}");

        foreach (var c in f.Characters)
        {
            sb.AppendLine();
            sb.AppendLine($"{c.Name} @ {c.World}");
            if (databaseChecked)
                sb.AppendLine($"  Last synced: {(c.SyncedAgo is TimeSpan ago ? Ago(ago) : "never")}");
            sb.AppendLine($"  {Plural(c.Subs, "sub")}, {Plural(c.Retainers.Count, "retainer")}");
            if (f.AllaganToolsReady)
            {
                foreach (var part in new[] { c.Bags }.Concat(c.Retainers.Select(r => new Part { Name = $"Retainer {r.Name}", Items = r.Items, StoredAgo = r.StoredAgo })))
                    sb.AppendLine($"  {part.Name}: {(part.Items is int n ? Plural(n, "item") : "not seen")}{Stored(part, databaseChecked)}");
                if (c.Chest != null)
                    sb.AppendLine($"  FC chest{(string.IsNullOrEmpty(c.FcName) ? "" : $" ({c.FcName})")}: {(c.Chest.Items is int n ? Plural(n, "item") : "not seen")}{Stored(c.Chest, databaseChecked)}");
            }
            if (c.FcId == 0)
                sb.AppendLine("  Free Company: none");
            else if (c.Housing != null)
                sb.AppendLine($"  House: {House(c.Housing)}");
            foreach (var fix in ToFix(c, f))
                sb.AppendLine($"  ! {fix}");
        }
        return sb.ToString();
    }
}
