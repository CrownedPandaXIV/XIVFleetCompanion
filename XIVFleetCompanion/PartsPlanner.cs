using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace XIVFleetCompanion
{
    // ---- What the Parts page reads (the app's fleet_parts_* views) ----

    /// <summary>A character: its Roster place, Free Company (as text), sub slots unlocked, and whether its FC has a house.</summary>
    public sealed record PartsCharacter(long Cid, string Name, string World, string Account, int? SortIndex, string? FcId, int NumSubSlots, bool HasHouse);

    /// <summary>A built sub: its workshop slot (null: unknown), rank, the item numbers of its hull, stern, bow and bridge,
    /// and whether it's ticked Craft? (follows Fight Club's builds from rank 85).</summary>
    public sealed record PartsSub(long Cid, string SubName, int? Slot, int Rank, int? Part1, int? Part2, int? Part3, int? Part4, bool CraftEnabled);

    /// <summary>A Sub Rule: the build from a rank, for some slots (SubFilter) and maybe one character (CidOverride, as text).</summary>
    public sealed record PartsRule(long Id, int Rank, string? CidOverride, string? SubFilter, string? Hull, string? Stern, string? Bow, string? Bridge);

    /// <summary>A Fight Club route with a recommended build ("90: WUSS"), and the repair kit price from Settings.</summary>
    public sealed record PartsFightClubRoute(long Id, string Route, double? Hours, string? RepairTimes, double? GilSubDay, string? RecBuild, double RepairKitCost);

    /// <summary>A stack of spare sub parts: a character's (Cid; IsRetainer with its retainer's number and name) or a Free
    /// Company chest's (FcId).</summary>
    public sealed record PartsSpare(long? Cid, string? FcId, bool IsRetainer, long? RetainerId, string? RetainerName, int Container, int SlotIndex, int ItemId, int Quantity);

    public sealed record PartsInputs(List<PartsCharacter> Characters, List<PartsSub> Subs, List<PartsRule> Rules, List<PartsFightClubRoute> FightClub, List<PartsSpare> Spares);

    /// <summary>
    /// One part a sub needs, as the app's Parts tab: Slot is "Hull", "Stern", "Bow" or "Bridge"; NeededClass "Shark" and so
    /// on. SpareLocation: where a spare is ("Inventory Box 2", "Retainer: Name", "FC Tab 1"), or which sub's old part to
    /// reuse ("Submersible-1's old Bow (once it is upgraded)", RecycledFrom then names the sub); null when it must be crafted.
    /// </summary>
    public sealed record PartNeed(long Cid, string Character, string World, string Account, int? SortIndex, string SubName, int Rank, string Slot,
        string NeededClass, bool NeededModified, string? SpareLocation, string? RecycledFrom, double? DueAt = null)
    {
        /// <summary>Look ahead: a part of a build coming up (due at DueAt, milliseconds; at or before now when it can switch already).</summary>
        public bool Ahead => DueAt != null;
        public bool ToCraft => SpareLocation == null;
        /// <summary>A sub not built yet: its first build, in an open slot (or a new workshop's first sub).</summary>
        public bool IsNew => Rank == 0;
        public string Needed => NeededModified ? $"Modified {NeededClass}" : NeededClass;
    }

    /// <summary>
    /// The Parts page's working out (plugin 2.0 batch 4), the same as the app's (ipc/parts.js computePartsNeeded, with
    /// subs/submarine-builds.js and subs/parts-matching.js): each sub's build against the one it should have at its rank
    /// (Fight Club's best build after repair kits for a sub ticked Craft? from rank 85, else the highest Sub Rule at or below
    /// its rank for its slot and character), a new workshop's first sub, and every unlocked empty slot; then each part
    /// covered by a spare (the character's own, then its retainers', then its FC chest's, which its tracked members share
    /// and never get twice), else by the part another of its subs gives up when upgraded, else crafted.
    /// The app's tests write made-up fleets and its answers (tests/db/parts-fixture.js) that the plugin's tests check
    /// this against. Look ahead: with `upcoming` (each sub's next build due within the days chosen, SubRoutes.Upcoming), also
    /// that build's parts, after what's needed now, soonest first (checked in the routes fixture).
    /// </summary>
    public static class PartsPlanner
    {
        public static readonly string[] Slots = { "Hull", "Stern", "Bow", "Bridge" };

        private static readonly Dictionary<string, string> ClassOfLetter = new()
        {
            ["S"] = "Shark", ["U"] = "Unkiu", ["W"] = "Whale", ["C"] = "Coelacanth", ["Y"] = "Syldra",
        };

        // Item number ranges of each class's four parts (bow, bridge, hull, stern in that order), plain then modified.
        private static readonly (int Base, string Letter)[] Bases =
        {
            (21792, "S"), (21796, "U"), (22526, "W"), (23903, "C"), (24344, "Y"),
            (24348, "S+"), (24352, "U+"), (24356, "W+"), (24360, "C+"), (24364, "Y+"),
        };
        private static readonly string[] SpareSlotOrder = { "Bow", "Bridge", "Hull", "Stern" };

        /// <summary>A fitted part's letter ("S", "U+"), or its number as text for anything else ("null" for none), as the app.</summary>
        public static string PartLetter(int? item)
        {
            if (item is int id)
                foreach (var (b, letter) in Bases)
                    if (id >= b && id <= b + 3) return letter;
            return item?.ToString(CultureInfo.InvariantCulture) ?? "null";
        }

        /// <summary>A part's item number from its class ("Shark"), slot ("Bow") and whether it's modified (ipc/parts.js partItemId); null when not a sub part.</summary>
        public static int? PartItemId(string cls, string slot, bool modified)
        {
            var letter = ClassOfLetter.FirstOrDefault(kv => kv.Value == cls).Key;
            var offset = System.Array.IndexOf(SpareSlotOrder, slot);
            if (letter == null || offset < 0) return null;
            foreach (var (b, l) in Bases)
                if (l == (modified ? letter + "+" : letter)) return b + offset;
            return null;
        }

        /// <summary>A spare's class, slot and whether it's modified; null for anything that isn't a sub part.</summary>
        public static (string Class, string Slot, bool Modified)? SparePart(int item)
        {
            foreach (var (b, letter) in Bases)
                if (item >= b && item <= b + 3)
                    return (ClassOfLetter[letter.TrimEnd('+')], SpareSlotOrder[item - b], letter.EndsWith('+'));
            return null;
        }

        private static (string Class, bool Modified)? Decode(string? letter)
        {
            var text = letter ?? "";
            var modified = text.EndsWith('+');
            return ClassOfLetter.TryGetValue(modified ? text[..^1] : text, out var cls) ? (cls, modified) : null;
        }

        /// <summary>Whether a Sub Rule's slots ("", "All", "Sub2Only", "Sub1,Sub3") take in a slot; anything else: no.</summary>
        public static bool RuleAppliesToSlot(string? subFilter, int slot)
        {
            var filter = (subFilter ?? "").Trim();
            if (filter.Length == 0 || filter.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
            var legacy = Regex.Match(filter, "^Sub([0-9])Only$", RegexOptions.IgnoreCase);
            if (legacy.Success) return int.Parse(legacy.Groups[1].Value, CultureInfo.InvariantCulture) == slot;
            if (filter.Contains(',') || Regex.IsMatch(filter, "^Sub[0-9]$", RegexOptions.IgnoreCase))
                return filter.Split(',').Select(x => Regex.Match(x.Trim(), "^Sub([0-9])$", RegexOptions.IgnoreCase))
                    .Any(m => m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == slot);
            return false;
        }

        /// <summary>A recommended build ("90: WUSS", "95: SSUC++", "100: S+S+U+C+"): its rank and four letters; null otherwise.</summary>
        public static (int Rank, string[] Parts)? ParseRankBuild(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text, @"^([0-9]+)\s*:\s*(.+)$");
            if (!m.Success) return null;
            var rank = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var build = m.Groups[2].Value.Trim();
            var allModified = Regex.Match(build, @"^([SUWCY])([SUWCY])([SUWCY])([SUWCY])\+\+$");
            if (allModified.Success) return (rank, new[] { 1, 2, 3, 4 }.Select(i => allModified.Groups[i].Value + "+").ToArray());
            if (Regex.IsMatch(build, @"^([SUWCY]\+?){4}$"))
            {
                var parts = new List<string>();
                var rest = build;
                for (var i = 0; i < 4; i++)
                {
                    var modified = rest.Length > 1 && rest[1] == '+';
                    parts.Add(modified ? $"{rest[0]}+" : rest[0].ToString());
                    rest = rest[(modified ? 2 : 1)..];
                }
                return (rank, parts.ToArray());
            }
            return null;
        }

        private static int RepairKits(IEnumerable<string> letters) => letters.Sum(letter =>
            letter.EndsWith('+') ? (ClassOfLetter.ContainsKey(letter[..^1]) ? 6 : 0)
            : letter switch { "S" => 1, "U" => 2, "W" => 3, "C" => 4, "Y" => 5, _ => 0 });

        /// <summary>A route's gil a day with a build, after the repair kits it breaks over a repair cycle (as the app).</summary>
        public static double NetGilPerDay(PartsFightClubRoute route, string[] parts)
        {
            var gilPerDay = route.GilSubDay ?? 0;
            var hours = route.Hours is double h && h != 0 ? h : 24;
            var repair = Regex.Match(route.RepairTimes ?? "", "^([0-9]+)");
            var voyages = repair.Success ? double.Parse(repair.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            if (voyages == 0) return gilPerDay;
            var cycleDays = voyages * hours / 24;
            return gilPerDay - RepairKits(parts) * route.RepairKitCost / cycleDays;
        }

        /// <summary>Fight Club's best build a sub at a rank can run (most gil a day after repair kits), from rank 85; null otherwise.</summary>
        public static string[]? BestFightClubBuild(int rank, IEnumerable<PartsFightClubRoute> routes)
        {
            if (rank < 85) return null;
            (double Net, string[] Parts)? best = null;
            foreach (var route in routes)
            {
                if (ParseRankBuild(route.RecBuild) is not { } build || rank < build.Rank) continue;
                var net = NetGilPerDay(route, build.Parts);
                if (best == null || net > best.Value.Net) best = (net, build.Parts);
            }
            return best?.Parts;
        }

        /// <summary>The build a sub should have now: four letters ("" where a rule leaves a slot alone), or null when nothing applies.</summary>
        public static string[]? TargetBuild(int rank, int slot, long cid, bool craftEnabled, IReadOnlyList<PartsRule> rules, IReadOnlyList<PartsFightClubRoute> routes)
        {
            if (craftEnabled && rank >= 85 && BestFightClubBuild(rank, routes) is { } best) return best;
            var cidText = cid.ToString(CultureInfo.InvariantCulture);
            PartsRule? active = null;
            foreach (var r in rules)
            {
                if (rank < r.Rank || !RuleAppliesToSlot(r.SubFilter, slot)) continue;
                if (!string.IsNullOrEmpty(r.CidOverride) && r.CidOverride != cidText) continue;
                if (active == null || r.Rank > active.Rank) active = r;
            }
            if (active == null) return null;
            return new[] { active.Hull, active.Stern, active.Bow, active.Bridge }.Select(x => (x ?? "").Trim()).ToArray();
        }

        private sealed class Stack
        {
            public required string Class;
            public required string Slot;
            public required bool Modified;
            public required string Location;
            public int Quantity;
        }

        private sealed record Need(string SubName, int Rank, string Slot, string Letter, string? Current, double? DueAt = null)
        {
            public bool Ahead => DueAt != null;
            public string Class => Decode(Letter)?.Class ?? (Letter.EndsWith('+') ? Letter[..^1] : Letter);
            public bool Modified => Letter.EndsWith('+');
            public (string Class, bool Modified)? Freed => Current == null ? null : Decode(Current);
        }

        /// <summary>Every part needed, character by character (in the order of their ids), each character's highest rank first.</summary>
        public static List<PartNeed> Plan(PartsInputs input, IReadOnlyDictionary<(long Cid, int Slot), (int Rank, string[] Parts, double DueAt)>? upcoming = null)
        {
            var characters = input.Characters.OrderBy(c => c.Cid).ToList();
            var rules = input.Rules.OrderBy(r => r.Id).ToList();
            var routes = input.FightClub.OrderBy(r => r.Id).ToList();

            // Subs by character and workshop slot (a later one in name order takes a slot two share, as the app's).
            var subs = new Dictionary<long, Dictionary<int, PartsSub>>();
            foreach (var s in input.Subs.Where(s => s.Rank > 0 && s.Slot != null).OrderBy(s => s.Cid).ThenBy(s => s.SubName, StringComparer.Ordinal))
            {
                if (!subs.TryGetValue(s.Cid, out var bySlot)) subs[s.Cid] = bySlot = new Dictionary<int, PartsSub>();
                bySlot[s.Slot!.Value] = s;
            }

            // Each character's spares: its own bags, then its retainers', then its FC chest's (the same stacks for every
            // tracked member, so a part taken by one is gone for the others).
            var pools = characters.ToDictionary(c => c.Cid, _ => new List<Stack>());
            foreach (var s in input.Spares.Where(s => s.Cid != null && s.Quantity > 0)
                         .OrderBy(s => s.Cid).ThenBy(s => s.IsRetainer).ThenBy(s => s.RetainerId ?? 0).ThenBy(s => s.Container).ThenBy(s => s.SlotIndex))
            {
                if (SparePart(s.ItemId) is not { } p || !pools.TryGetValue(s.Cid!.Value, out var pool)) continue;
                var location = s.IsRetainer ? $"Retainer: {(string.IsNullOrEmpty(s.RetainerName) ? "Unknown" : s.RetainerName)}" : $"Inventory Box {s.Container + 1}";
                pool.Add(new Stack { Class = p.Class, Slot = p.Slot, Modified = p.Modified, Location = location, Quantity = s.Quantity });
            }
            var members = characters.Where(c => !string.IsNullOrEmpty(c.FcId) && c.FcId != "0").GroupBy(c => c.FcId!).ToDictionary(g => g.Key, g => g.Select(c => c.Cid).ToList());
            foreach (var s in input.Spares.Where(s => s.Cid == null && s.FcId != null && s.Quantity > 0).OrderBy(s => s.Container).ThenBy(s => s.SlotIndex))
            {
                if (SparePart(s.ItemId) is not { } p || !members.TryGetValue(s.FcId!, out var cids)) continue;
                var stack = new Stack { Class = p.Class, Slot = p.Slot, Modified = p.Modified, Location = $"FC Tab {s.Container - 20000 + 1}", Quantity = s.Quantity };
                foreach (var cid in cids) pools[cid].Add(stack);
            }

            // What each character needs.
            var needs = new List<(PartsCharacter Character, List<Need> Needs)>();
            foreach (var character in characters)
            {
                var bySlot = subs.TryGetValue(character.Cid, out var found) ? found : new Dictionary<int, PartsSub>();
                var list = new List<Need>();
                var ahead = new List<Need>();
                for (var slot = 1; slot <= 4; slot++)
                {
                    if (!bySlot.TryGetValue(slot, out var sub))
                    {
                        // An unlocked slot with no sub yet: its first build, all Shark.
                        if (slot <= character.NumSubSlots)
                            foreach (var key in Slots) list.Add(new Need($"Submersible-{slot}", 0, key, "S", null));
                        continue;
                    }
                    var current = new[] { PartLetter(sub.Part1), PartLetter(sub.Part2), PartLetter(sub.Part3), PartLetter(sub.Part4) };
                    var target = TargetBuild(sub.Rank, slot, character.Cid, sub.CraftEnabled, rules, routes);
                    // What the slot has once today's needs are met: the target build, or what's fitted where it says nothing.
                    var settled = new string[4];
                    for (var i = 0; i < 4; i++)
                    {
                        var letter = target?[i] ?? "";
                        settled[i] = letter.Length > 0 ? letter : current[i];
                        if (letter.Length > 0 && current[i] != letter) list.Add(new Need(sub.SubName, sub.Rank, Slots[i], letter, current[i]));
                    }
                    // Look ahead: its next build, when that's due within the days chosen.
                    if (upcoming != null && upcoming.TryGetValue((character.Cid, slot), out var next))
                        for (var i = 0; i < 4; i++)
                        {
                            var letter = i < next.Parts.Length ? next.Parts[i] ?? "" : "";
                            if (letter.Length > 0 && settled[i] != letter) ahead.Add(new Need(sub.SubName, next.Rank, Slots[i], letter, settled[i], next.DueAt));
                        }
                }
                // A Free Company house but no workshop yet: its first sub's parts.
                if (character.NumSubSlots == 0 && bySlot.Count == 0 && character.HasHouse)
                    foreach (var key in Slots) list.Add(new Need("Submersible-1", 0, key, "S", null));
                needs.Add((character, list.OrderByDescending(n => n.Rank).Concat(ahead.OrderBy(n => n.DueAt).ThenByDescending(n => n.Rank)).ToList()));
            }

            // Spares first: to what's needed now, across every character in turn, then to the builds coming up, soonest
            // first (so a shared FC chest serves the sub that needs it today); then, for each character, the parts its
            // upgrades free up.
            var spares = needs.Select(x => new string?[x.Needs.Count]).ToList();
            var order = needs.SelectMany((x, c) => x.Needs.Select((n, i) => (C: c, I: i, Need: n))).ToList();
            foreach (var (c, i, n) in order.Where(o => !o.Need.Ahead).Concat(order.Where(o => o.Need.Ahead).OrderBy(o => o.Need.DueAt)))
            {
                var stack = pools[needs[c].Character.Cid].FirstOrDefault(s => s.Quantity > 0 && s.Class == n.Class && s.Slot == n.Slot && s.Modified == n.Modified);
                if (stack == null) continue;
                stack.Quantity -= 1;
                spares[c][i] = stack.Location;
            }

            var result = new List<PartNeed>();
            for (var c = 0; c < needs.Count; c++)
            {
                var (character, list) = needs[c];
                var freed = list.Select((n, index) => (Need: n, Index: index, Part: n.Freed)).Where(x => x.Part != null)
                    .Select(x => new FreedPart(x.Part!.Value.Class, x.Need.Slot, x.Part!.Value.Modified, x.Need.SubName) { Ahead = x.Need.Ahead, Index = x.Index }).ToList();
                for (var i = 0; i < list.Count; i++)
                {
                    var n = list[i];
                    string? location = spares[c][i];
                    string? recycled = null;
                    if (location == null)
                    {
                        // A part freed by a build coming up only comes off later: it can only go to a need coming up after it.
                        var index = i;
                        var part = freed.FirstOrDefault(f => !f.Used && f.SubName != n.SubName && (!f.Ahead || (n.Ahead && f.Index < index))
                                                             && f.Class == n.Class && f.Slot == n.Slot && f.Modified == n.Modified);
                        if (part != null)
                        {
                            part.Used = true;
                            location = $"{part.SubName}'s old {n.Slot} (once it is upgraded)";
                            recycled = part.SubName;
                        }
                    }
                    result.Add(new PartNeed(character.Cid, character.Name, character.World, character.Account, character.SortIndex, n.SubName, n.Rank, n.Slot,
                        n.Class, n.Modified, location, recycled, n.DueAt));
                }
            }
            return result;
        }

        private sealed class FreedPart
        {
            public FreedPart(string cls, string slot, bool modified, string subName) { Class = cls; Slot = slot; Modified = modified; SubName = subName; }
            public string Class { get; }
            public string Slot { get; }
            public bool Modified { get; }
            public string SubName { get; }
            public bool Used { get; set; }
            public bool Ahead { get; init; }
            public int Index { get; init; }
        }

        /// <summary>The Summary's "Subs ready for new parts": each sub with parts needed, in Roster order, with them: built
        /// subs whose rank calls for a different build, and new subs to build in an open slot (rank 0; IsNew).</summary>
        public static List<(PartNeed Sub, List<PartNeed> Parts)> SubsReady(IEnumerable<PartNeed> needs) =>
            needs.GroupBy(n => (n.Cid, n.SubName))
                .Select(g => (Sub: g.First(), Parts: g.ToList()))
                .OrderBy(x => x.Sub.SortIndex ?? 1_000_000_000).ThenBy(x => x.Sub.SubName, StringComparer.CurrentCulture).ToList();

        /// <summary>"7 subs are ready for new parts, 1 new sub to build" (built subs and new ones counted apart); "" with none.</summary>
        public static string ReadyText(IReadOnlyCollection<(PartNeed Sub, List<PartNeed> Parts)> ready)
        {
            var built = ready.Count(x => !x.Sub.IsNew);
            var fresh = ready.Count - built;
            var parts = new List<string>();
            if (built > 0) parts.Add($"{built} sub{(built == 1 ? " is" : "s are")} ready for new parts");
            if (fresh > 0) parts.Add($"{fresh} new sub{(fresh == 1 ? "" : "s")} to build");
            return string.Join(", ", parts);
        }

        /// <summary>The Parts page's summary: how many of each part still need crafting, by slot (hull, stern, bow, bridge) then most first.</summary>
        public static List<(string Slot, string Needed, int Count)> ToCraft(IEnumerable<PartNeed> needs) =>
            needs.Where(n => n.ToCraft).GroupBy(n => (n.Slot, n.Needed))
                .Select(g => (g.Key.Slot, g.Key.Needed, Count: g.Count()))
                .OrderBy(x => Array.IndexOf(Slots, x.Slot)).ThenByDescending(x => x.Count).ToList();
    }
}
