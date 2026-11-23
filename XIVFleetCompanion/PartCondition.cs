using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Sub part condition, for the Sub Calculator's repair costs. The game only has it while a character is in the Free
    /// Company's workshop (PartConditionReader reads it there, every couple of seconds), and a visit can be shorter than
    /// the time between syncs, so readings wait here until the next sync writes them (DetailsWriter.WritePartConditionAsync).
    /// Only a change is kept: a part's first reading this session, then each time its item or condition differs, so a
    /// voyage shows as a drop and a repair as a rise. No Dalamud here, so it is tested on its own.
    /// </summary>
    public static class PartCondition
    {
        /// <summary>The game's condition for a whole part (100%).</summary>
        public const int Full = 30000;

        /// <summary>One part of one sub, as read. SubIndex: the sub's place in the workshop (1 to 4); PartSlot: 0 hull, 1 stern,
        /// 2 bow, 3 bridge; ReturnAt and At in Unix seconds (ReturnAt 0: not sent out); FcId: the Free Company whose workshop
        /// it was read in, as the game said (0 when it didn't).</summary>
        public sealed record Reading(ulong Cid, uint Register, string SubName, int SubIndex, int PartSlot, uint ItemId, int Condition, long ReturnAt, double At, ulong FcId = 0);

        /// <summary>A reading ready to write: with the Free Company the character is in.</summary>
        public sealed record Row(ulong FcId, Reading Reading);

        /// <summary>The changes waiting for the next sync. Safe to use from the game's thread and the sync's at once.</summary>
        public sealed class Log
        {
            /// <summary>At most this many wait; beyond it the oldest go (a database that's been unreachable for weeks).</summary>
            public const int MaxPending = 5000;

            private readonly object gate = new();
            private readonly Dictionary<(ulong Cid, uint Register, int Slot), (uint Item, int Condition)> last = new();
            private readonly List<Reading> pending = new();

            /// <summary>Keeps the readings that differ from the part's last one. Returns how many were kept.</summary>
            public int Offer(IEnumerable<Reading> readings)
            {
                var kept = 0;
                lock (gate)
                {
                    foreach (var r in readings)
                    {
                        if (r.ItemId == 0 || r.Register == 0 || r.Condition < 0) continue;
                        var key = (r.Cid, r.Register, r.PartSlot);
                        if (last.TryGetValue(key, out var was) && was == (r.ItemId, r.Condition)) continue;
                        last[key] = (r.ItemId, r.Condition);
                        pending.Add(r);
                        kept++;
                    }
                    Trim();
                }
                return kept;
            }

            /// <summary>Everything waiting, oldest first, and no longer waiting.</summary>
            public List<Reading> Take()
            {
                lock (gate)
                {
                    var all = pending.ToList();
                    pending.Clear();
                    return all;
                }
            }

            /// <summary>Readings a sync couldn't write, back in front of any taken since.</summary>
            public void PutBack(IReadOnlyCollection<Reading> readings)
            {
                lock (gate)
                {
                    pending.InsertRange(0, readings);
                    Trim();
                }
            }

            public int Count { get { lock (gate) return pending.Count; } }

            private void Trim()
            {
                if (pending.Count > MaxPending) pending.RemoveRange(0, pending.Count - MaxPending);
            }
        }

        /// <summary>The readings to write: those of characters this setup syncs (fcOf: each one's FC, 0 for none) whose Free
        /// Company is known: the workshop's own as the game said, else AutoRetainer's (SubSectors.FcFor).</summary>
        public static List<Row> ToWrite(IEnumerable<Reading> readings, IReadOnlyDictionary<ulong, ulong> fcOf) =>
            readings.Where(r => fcOf.ContainsKey(r.Cid) && SubSectors.FcFor(r.FcId, r.Cid, fcOf) != 0)
                .Select(r => new Row(SubSectors.FcFor(r.FcId, r.Cid, fcOf), r)).ToList();

        /// <summary>The readings to keep waiting for a later sync: their character isn't synced in this one, or no Free
        /// Company is known for it yet (as SubSectors.Waiting).</summary>
        public static List<Reading> Waiting(IEnumerable<Reading> readings, IReadOnlyDictionary<ulong, ulong> fcOf) =>
            readings.Where(r => !fcOf.ContainsKey(r.Cid) || SubSectors.FcFor(r.FcId, r.Cid, fcOf) == 0).ToList();
    }
}
