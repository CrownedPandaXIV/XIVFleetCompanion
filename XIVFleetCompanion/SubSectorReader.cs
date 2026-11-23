using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Reads which submarine sectors the Free Company has unlocked and explored, from the game, while a character is in its
    /// workshop (the game's own checks, as AutoRetainer uses them at the Voyage Control Panel). Runs on the game's thread.
    /// </summary>
    internal static unsafe class SubSectorReader
    {
        /// <summary>What one look found: the reading (null when there's nothing to read, or it doesn't make sense), and in
        /// plain words what was seen, for the log once per visit.</summary>
        public sealed record Look(SubSectors.Reading? Reading, string Seen);

        public static Look Read(ulong cid, double now, IEnumerable<int> sectorIds)
        {
            var housing = HousingManager.Instance();
            if (housing == null || housing->WorkshopTerritory == null) return new Look(null, "not in a workshop");
            if (cid == 0) return new Look(null, "in a workshop, but no character is logged in");
            var subs = housing->WorkshopTerritory->Submersible.Data;
            var registered = 0;
            for (var i = 0; i < subs.Length && i < 4; i++) if (subs[i].RegisterTime != 0) registered++;
            if (registered == 0)
                return new Look(null, "in the workshop, no sub list yet (the game loads it when the Voyage Control Panel's submarines are opened)");
            var sectors = sectorIds.Where(id => id > 0 && id <= byte.MaxValue).Select(id =>
                new SubSectors.Sector(id, HousingManager.IsSubmarineExplorationUnlocked((byte)id), HousingManager.IsSubmarineExplorationExplored((byte)id))).ToList();
            var problem = SubSectors.Problem(sectors);
            if (problem != null) return new Look(null, $"in the workshop, sectors not read: {problem}");
            // The Free Company the game says the character is in: the workshop's, whatever AutoRetainer has yet.
            var proxy = InfoProxyFreeCompany.Instance();
            return new Look(new SubSectors.Reading(cid, sectors, now, proxy == null ? 0UL : proxy->Id),
                $"in the workshop: {sectors.Count(s => s.Explored)} sector(s) explored, {sectors.Count(s => s.Unlocked && !s.Explored)} unlocked to explore next");
        }
    }
}
