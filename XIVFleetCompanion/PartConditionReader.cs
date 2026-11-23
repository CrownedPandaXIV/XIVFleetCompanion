using FFXIVClientStructs.FFXIV.Client.Game;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Reads each sub's part condition from the game, which has it only while a character is in the Free Company's
    /// workshop. The fitted parts sit in the house's placed-items container, five places per sub in workshop order (the
    /// sub's four parts, then one unused), as AutoRetainer reads them for its repairs. Runs on the game's thread.
    /// </summary>
    internal static unsafe class PartConditionReader
    {
        private const int PlacesPerSub = 5;

        /// <summary>What one look found: the readings (null when not in a workshop), and in plain words what was seen, for
        /// the log once per visit.</summary>
        public sealed record Look(List<PartCondition.Reading>? Readings, string Seen);

        /// <summary>Every fitted part of every sub in this workshop. A place holding anything but a sub part is skipped, so a
        /// change to the game's layout reads nothing rather than something wrong (and Seen says what was there instead).</summary>
        public static Look Read(ulong cid, double now)
        {
            var housing = HousingManager.Instance();
            if (housing == null || housing->WorkshopTerritory == null) return new Look(null, "not in a workshop");
            if (cid == 0) return new Look(null, "in a workshop, but no character is logged in");
            var inventory = InventoryManager.Instance();
            var container = inventory == null ? null : inventory->GetInventoryContainer(InventoryType.HousingInteriorPlacedItems2);
            if (container == null) return new Look(new(), "in the workshop, but the game has no parts container");

            var readings = new List<PartCondition.Reading>();
            var subs = housing->WorkshopTerritory->Submersible.Data;
            var registered = 0;
            var other = new List<string>();
            for (var i = 0; i < subs.Length && i < 4; i++)
            {
                var sub = subs[i];
                if (sub.RegisterTime == 0) continue;
                registered++;
                var name = sub.NameString;
                for (var slot = 0; slot < 4; slot++)
                {
                    var place = i * PlacesPerSub + slot;
                    var part = place < container->Size ? container->GetInventorySlot(place) : null;
                    if (part == null) { other.Add($"{place}: none"); continue; }
                    if (!PartsPlanner.IsSubPart(part->ItemId)) { other.Add($"{place}: item {part->ItemId}"); continue; }
                    readings.Add(new PartCondition.Reading(cid, sub.RegisterTime, name, i + 1, slot, part->ItemId, part->Condition, sub.ReturnTime, now));
                }
            }
            if (registered == 0)
                return new Look(readings, "in the workshop, no sub list yet (the game loads it when the Voyage Control Panel's submarines are opened)");
            var seen = $"in the workshop: {registered} sub(s), {readings.Count} part(s) read"
                + (container->IsLoaded ? "" : " (the game says the parts container isn't loaded)")
                + (other.Count > 0 ? $"; places without a sub part: {string.Join(", ", other.Take(8))}" : "");
            return new Look(readings, seen);
        }
    }
}
