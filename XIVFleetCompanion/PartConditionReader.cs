using FFXIVClientStructs.FFXIV.Client.Game;
using System.Collections.Generic;

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

        /// <summary>Every fitted part of every sub in this workshop, or null when not in a workshop (or it isn't loaded yet).
        /// A place holding anything but a sub part is skipped, so a change to the game's layout reads nothing rather than
        /// something wrong.</summary>
        public static List<PartCondition.Reading>? Read(ulong cid, double now)
        {
            var housing = HousingManager.Instance();
            if (housing == null || housing->WorkshopTerritory == null || cid == 0) return null;
            var inventory = InventoryManager.Instance();
            if (inventory == null) return null;
            var container = inventory->GetInventoryContainer(InventoryType.HousingInteriorPlacedItems2);
            if (container == null || !container->IsLoaded) return null;

            var readings = new List<PartCondition.Reading>();
            var subs = housing->WorkshopTerritory->Submersible.Data;
            for (var i = 0; i < subs.Length && i < 4; i++)
            {
                var sub = subs[i];
                if (sub.RegisterTime == 0) continue;
                var name = sub.NameString;
                for (var slot = 0; slot < 4; slot++)
                {
                    var part = container->GetInventorySlot(i * PlacesPerSub + slot);
                    if (part == null || !PartsPlanner.IsSubPart(part->ItemId)) continue;
                    readings.Add(new PartCondition.Reading(cid, sub.RegisterTime, name, i + 1, slot, part->ItemId, part->Condition, sub.ReturnTime, now));
                }
            }
            return readings;
        }
    }
}
