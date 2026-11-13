using System;
using System.Linq;

namespace XIVFleetCompanion;

/// <summary>
/// The words /xivfleet takes: a page to open, check or speedtest. The help text is made from the same list, so
/// it can't leave a page out. No Dalamud here, so it is tested on its own (tests/FleetWriterTests).
/// </summary>
public static class CommandWords
{
    /// <summary>Pages, by their MainWindow.Tab names in lower case, in the order the help lists them.</summary>
    public static readonly string[] Pages =
        { "summary", "subs", "routes", "supplies", "parts", "roster", "retainers", "fcs", "inventory", "salvage", "income", "market", "trends", "pcs", "status", "settings" };

    public const string Check = "check";
    public const string SpeedTest = "speedtest";

    public static string Help =>
        $"Opens the XIV Fleet Companion window. Add {string.Join(", ", Pages[..^1])} or {Pages[^1]} to open that page; " +
        "check shows what the plugin can see for each character; speedtest times reading from the database.";

    /// <summary>The word as one of Pages, Check or SpeedTest (any case), or null when it is none of them.</summary>
    public static string? Match(string word)
    {
        var w = word.Trim();
        if (w.Equals(Check, StringComparison.OrdinalIgnoreCase)) return Check;
        if (w.Equals(SpeedTest, StringComparison.OrdinalIgnoreCase)) return SpeedTest;
        return Pages.FirstOrDefault(p => p.Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What to say in chat for a word that isn't one of them.</summary>
    public static string Unknown(string word) =>
        $"XIV Fleet Companion: \"{word.Trim()}\" isn't a page. Try one of: {string.Join(", ", Pages)}, {Check} or {SpeedTest}.";
}
