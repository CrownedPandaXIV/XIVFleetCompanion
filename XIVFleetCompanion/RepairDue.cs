using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>A part's reading as saved (companion_sub_part_condition): times in Unix seconds, ReturnAt 0 when not sent out.</summary>
    public sealed record PartReading(long Cid, string SubName, int Slot, double ReadAt, double ReturnAt, int ItemId, int Condition);

    /// <summary>When a sub's part needs repairing: how many more voyages the part closest to 0% can go on (on the current
    /// route, after the one it is on now), which part, its condition as last read (%), and when it was read.</summary>
    public sealed record SubRepair(int Voyages, int Slot, double Condition, double ReadAt);

    /// <summary>
    /// When a sub's parts need repairing (the Subs page's Repair in), as the app's subs/repair-due.js: from each part's condition
    /// as last read in the workshop and how much a voyage on its current route wears it (SubCalculator.SectorDamage). A part
    /// can be sent out while its condition is above 0. Whether the game takes a voyage's wear when the sub is sent or when it
    /// comes back is told from the fleet's own drops (one read while the sub was out: when sent); with none, the voyage out
    /// is counted as not yet worn (the earlier repair date).
    /// </summary>
    public static class RepairDue
    {
        /// <summary>Each drop from the reading before it (oldest first): true when read while the sub was out.</summary>
        public static List<bool> DropsOf(IReadOnlyList<PartReading> readings)
        {
            var output = new List<bool>();
            for (var i = 1; i < readings.Count; i++)
            {
                var r = readings[i];
                if (r.ItemId == readings[i - 1].ItemId && r.Condition < readings[i - 1].Condition) output.Add(r.ReturnAt > r.ReadAt);
            }
            return output;
        }

        /// <summary>Wear taken when sent (true), on return (false), or not known yet (null): what most drops say.</summary>
        public static bool? WearAtSend(IEnumerable<IReadOnlyList<PartReading>> parts)
        {
            int sent = 0, back = 0;
            foreach (var readings in parts)
                foreach (var outAtSea in DropsOf(readings)) { if (outAtSea) sent++; else back++; }
            if (sent == 0 && back == 0) return null;
            return sent > back;
        }

        /// <summary>How many more voyages a part can go on after the one it is on now, or null when it can't be told (no
        /// reading, or another part fitted now). returnTime: the sub's now (0 or past: in port).</summary>
        public static int? VoyagesLeft(PartReading? last, int itemId, double lost, double returnTime, bool? atSend)
        {
            if (last == null || last.ItemId != itemId || !(lost > 0)) return null;
            var outWhenRead = last.ReturnAt > last.ReadAt;
            var sentSince = returnTime > 0 && returnTime > last.ReturnAt + 60 && returnTime > last.ReadAt;
            var unworn = atSend == true ? (sentSince ? 1 : 0) : (outWhenRead ? 1 : 0) + (sentSince ? 1 : 0);
            var after = last.Condition - unworn * lost;
            return after > 0 ? (int)Math.Ceiling(after / lost) : 0;
        }

        /// <summary>A sub: the part that needs repairing first. parts: per slot 0-3 (readings oldest first, the part fitted
        /// now, its rank), null where not known; route: its sectors' rank needed.</summary>
        public static SubRepair? Of(IReadOnlyList<(IReadOnlyList<PartReading> Readings, int ItemId, int Rank)?> parts, IReadOnlyList<int> route, double returnTime, bool? atSend)
        {
            if (route.Count == 0) return null;
            SubRepair? due = null;
            for (var slot = 0; slot < parts.Count; slot++)
            {
                if (parts[slot] is not { } p || p.Readings.Count == 0) continue;
                var lost = route.Sum(rankReq => SubCalculator.SectorDamage(rankReq, p.Rank));
                var last = p.Readings[^1];
                if (VoyagesLeft(last, p.ItemId, lost, returnTime, atSend) is not int voyages) continue;
                if (due == null || voyages < due.Voyages)
                    due = new SubRepair(voyages, slot, 100.0 * last.Condition / SubCalculator.FullCondition, last.ReadAt);
            }
            return due;
        }
    }
}
