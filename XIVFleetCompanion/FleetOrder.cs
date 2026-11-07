using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>One place an item sits, for Where is it? (several stacks in one place added together).</summary>
    public sealed record ItemPlace(string Who, string World, string Account, string Kind, string Where, long Quantity,
        string Owner, string OwnerKey, int SortIndex, int HireOrder, string RetainerName);

    /// <summary>One item and every place it sits, for Where is it?.</summary>
    public sealed record ItemPlaces(int ItemId, string Name, long Total, List<ItemPlace> Places);

    /// <summary>
    /// The app's orders and groupings for the in-game tables (no Dalamud here, so they're checked in the tests): the
    /// Roster's order (its saved order, then name), retainers by their owner's place in it and then hiring order, and
    /// "Group by account" (accounts A to Z, Ungrouped last).
    /// </summary>
    public static class FleetOrder
    {
        private static readonly StringComparer Text = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: false);

        /// <summary>The Roster's order (renderer sortRows): saved place first (none last), then name.</summary>
        public static List<RosterRow> Roster(IEnumerable<RosterRow> rows) =>
            rows.OrderBy(r => r.SortIndex).ThenBy(r => r.Character, Text).ToList();

        /// <summary>The Roster's order for any rows of characters (Salvage, Income, ventures): saved place first (none last),
        /// then name.</summary>
        public static List<T> Roster<T>(IEnumerable<T> rows, Func<T, int> sortIndex, Func<T, string> name) =>
            rows.OrderBy(sortIndex).ThenBy(name, Text).ToList();

        /// <summary>Every search box (2.x, as the app): matches when the text has every word typed, in any order, ignoring
        /// case ("diadochos healing" finds "Diadochos Bottoms of Healing"). Nothing typed matches everything.</summary>
        public static bool MatchesWords(string? text, string? query)
        {
            var haystack = text ?? "";
            return (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The Retainers tab's order (renderer sortByRoster): the owner's place in the Roster (none last), the
        /// owner's name, hiring order (none last), then the retainer's name.</summary>
        public static List<RetainerRow> Retainers(IEnumerable<RetainerRow> rows) =>
            rows.OrderBy(r => r.SortIndex).ThenBy(r => r.Owner, Text).ThenBy(r => r.HireOrder).ThenBy(r => r.Name, Text).ToList();

        /// <summary>The Free Companies tab's order: its first member's place in the Roster (none last), then its name.</summary>
        public static List<FcRow> Fcs(IEnumerable<FcRow> rows) =>
            rows.OrderBy(r => r.SortIndex).ThenBy(r => r.Name, Text).ToList();

        /// <summary>"Group by account": one group per account label, A to Z, with no label ("Ungrouped") last; each
        /// group keeps the rows' order.</summary>
        public static List<(string Label, List<T> Rows)> ByAccount<T>(IEnumerable<T> rows, Func<T, string?> account)
        {
            var groups = new Dictionary<string, List<T>>();
            foreach (var row in rows)
            {
                var label = string.IsNullOrEmpty(account(row)) ? "Ungrouped" : account(row)!;
                if (!groups.TryGetValue(label, out var list)) groups[label] = list = new List<T>();
                list.Add(row);
            }
            return groups.OrderBy(g => g.Key == "Ungrouped" ? 1 : 0).ThenBy(g => g.Key, Text).Select(g => (g.Key, g.Value)).ToList();
        }

        /// <summary>
        /// Where is it? (characters/item-locations.js groupByItem): the stacks of the items found, one entry per item
        /// (high-quality ones with the normal ones) with every place it sits, places in the Roster's order: a
        /// character's own places (biggest first), then its retainers in hiring order, then the FC chest counted on it.
        /// Items A to Z. name: the item's name (from the game's files).
        /// </summary>
        public static List<ItemPlaces> WhereIsIt(IEnumerable<HoldingRow> stacks, Func<int, string> name)
        {
            var items = new Dictionary<int, (long Total, List<ItemPlace> Places)>();
            foreach (var s in stacks)
            {
                if (s.Quantity <= 0 || s.Money) continue;
                if (!items.TryGetValue(s.ItemId, out var item)) items[s.ItemId] = item = (0, new List<ItemPlace>());
                var ownerKey = s.OwnerCid?.ToString(CultureInfo.InvariantCulture) ?? "";
                var i = item.Places.FindIndex(p => p.OwnerKey == ownerKey && p.Who == s.Who && p.World == s.World && p.Where == s.Place);
                if (i >= 0) item.Places[i] = item.Places[i] with { Quantity = item.Places[i].Quantity + s.Quantity };
                else item.Places.Add(new ItemPlace(s.Who, s.World, s.Account, s.Kind, s.Place, s.Quantity, s.OwnerName ?? s.Who, ownerKey,
                    s.SortIndex, s.HireOrder, s.RetainerName ?? ""));
                items[s.ItemId] = (item.Total + s.Quantity, item.Places);
            }
            return items.Select(kv => new ItemPlaces(kv.Key, name(kv.Key), kv.Value.Total, kv.Value.Places.OrderBy(p => p, PlaceOrder).ToList()))
                .OrderBy(x => x.Name, Text).ToList();
        }

        private static readonly Comparer<ItemPlace> PlaceOrder = Comparer<ItemPlace>.Create(ComparePlaces);

        // characters/item-locations.js compareRoster.
        private static int ComparePlaces(ItemPlace a, ItemPlace b)
        {
            var c = a.SortIndex.CompareTo(b.SortIndex);
            if (c != 0) return c;
            c = Text.Compare(a.Owner, b.Owner);
            if (c != 0) return c;
            c = Text.Compare(a.OwnerKey, b.OwnerKey);
            if (c != 0) return c;
            static int Rank(string kind) => kind == "character" ? 0 : kind == "retainer" ? 1 : 2;
            c = Rank(a.Kind).CompareTo(Rank(b.Kind));
            if (c != 0) return c;
            if (a.Kind == "retainer")
            {
                c = a.HireOrder.CompareTo(b.HireOrder);
                if (c != 0) return c;
                c = Text.Compare(a.RetainerName, b.RetainerName);
                if (c != 0) return c;
            }
            c = b.Quantity.CompareTo(a.Quantity);
            if (c != 0) return c;
            c = Text.Compare(a.Who, b.Who);
            return c != 0 ? c : Text.Compare(a.Where, b.Where);
        }

        /// <summary>The Inventory tab's Location for a character's own stack or its retainer's ("Retainer: Ret A", or the
        /// container's name).</summary>
        public static string CharacterLocation(HoldingRow s) =>
            s.Kind == "retainer" ? $"Retainer: {(string.IsNullOrEmpty(s.RetainerName) ? "Unknown" : s.RetainerName)}"
                : string.IsNullOrEmpty(s.ContainerName) ? $"Container {s.Container}" : s.ContainerName;
    }
}
