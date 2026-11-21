using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Ceruleum from FC credits (the Supplies page): how many ceruleum tanks each Free Company's company credits can buy
    /// (from the OIC Quartermaster at a Grand Company), and how many days of the fleet's ceruleum use those tanks cover,
    /// per Free Company, per account and for the whole fleet. The same working out as the app's subs/fc-credits.js.
    /// </summary>
    public static class FcCredits
    {
        public const int DefaultTankCost = 100;
        public const int MaxTankCost = 100000;
        public const string TankCostKey = "fc_credit_tank_cost";
        public const int CeruleumTank = 10155;

        /// <summary>A Free Company as the Free Companies page reads it.</summary>
        public sealed record Fc(string FcId, string Name, string Account, long Points);

        /// <summary>A character on the Supplies page: its FC, account, ceruleum used a day (null: not known yet) and held.</summary>
        public sealed record Member(string? FcId, string Account, double? PerDay, long Have);

        /// <summary>Days: how long the tanks last; null when the use isn't known yet, +infinity when none is used.</summary>
        public sealed record FcLine(string FcId, string Name, string Account, long Credits, long Tanks, long Chest, double? PerDay, double? Days);

        public sealed record Total(string Label, int FcCount, long Credits, long Tanks, long Chest, long Held, double? PerDay, double? Days);

        public sealed record Plan(int TankCost, List<FcLine> Fcs, List<Total> Accounts, Total Fleet);

        /// <summary>The tank price saved in the settings, else the default (as the app reads it).</summary>
        public static int TankCostOf(double? saved) =>
            saved is double v && !double.IsNaN(v) && v >= 1 && v <= MaxTankCost ? (int)Math.Round(v, MidpointRounding.AwayFromZero) : DefaultTankCost;

        public static double? DaysCovered(long tanks, double? perDay) =>
            perDay is not double use ? null : use <= 0 ? double.PositiveInfinity : tanks / use;

        public static Plan Work(IEnumerable<Fc> fcs, IReadOnlyList<Member> characters, IReadOnlyDictionary<string, long> chests, int tankCost)
        {
            var cost = tankCost > 0 ? tankCost : DefaultTankCost;
            static string Label(string? account) => string.IsNullOrEmpty(account) ? "Ungrouped" : account;
            // Use per day added up, or null when none of them is known (a single unknown one is left out).
            static double? AddUse(IEnumerable<Member> rows)
            {
                var known = rows.Where(r => r.PerDay != null).ToList();
                return known.Count > 0 ? known.Sum(r => r.PerDay!.Value) : null;
            }

            var fcLines = fcs.Select(fc =>
            {
                var credits = Math.Max(0, fc.Points);
                var tanks = credits / cost;
                var perDay = AddUse(characters.Where(c => c.FcId != null && c.FcId == fc.FcId));
                return new FcLine(fc.FcId, fc.Name, Label(fc.Account), credits, tanks, chests.TryGetValue(fc.FcId, out var n) ? n : 0, perDay, DaysCovered(tanks, perDay));
            }).ToList();

            Total Summarise(string label, List<FcLine> fcList, List<Member> people)
            {
                var tanks = fcList.Sum(f => f.Tanks);
                var perDay = AddUse(people);
                return new Total(label, fcList.Count, fcList.Sum(f => f.Credits), tanks, fcList.Sum(f => f.Chest), people.Sum(c => c.Have), perDay, DaysCovered(tanks, perDay));
            }

            var labels = fcLines.Select(f => f.Account).Concat(characters.Select(c => Label(c.Account))).Distinct()
                .OrderBy(l => l, StringComparer.CurrentCulture).ToList();
            var accounts = labels.Select(l => Summarise(l, fcLines.Where(f => f.Account == l).ToList(), characters.Where(c => Label(c.Account) == l).ToList()))
                .Where(a => a.FcCount > 0).ToList();
            return new Plan(cost, fcLines, accounts, Summarise("Fleet", fcLines, characters.ToList()));
        }

        /// <summary>How long it lasts, as the app says it: "6.2 days", "Under a day", "No use", "Not enough history".</summary>
        public static string DaysText(double? days) => days switch
        {
            null => "Not enough history",
            double d when double.IsPositiveInfinity(d) => "No use",
            double d when d < 1 => "Under a day",
            double d => $"{(d < 10 ? d.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) : Math.Round(d).ToString("0", System.Globalization.CultureInfo.CurrentCulture))} days",
        };
    }
}
