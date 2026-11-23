using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Which submarine sectors each Free Company has unlocked and explored, for the Sub Calculator's Route Finder. The game
    /// only has it while a character is in the FC's workshop (SubSectorReader reads it there), and a visit can be shorter
    /// than the time between syncs, so readings wait here until the next sync writes them (DetailsWriter.WriteSubSectorsAsync).
    /// A character's first sensible reading this session is kept, then each one that differs. No Dalamud here, so it is
    /// tested on its own.
    /// </summary>
    public static class SubSectors
    {
        /// <summary>The first sector of the first map, open to every Free Company from the start (SubmarineExploration).</summary>
        public const int FirstSector = 1;

        /// <summary>One sector, as read.</summary>
        public sealed record Sector(int Id, bool Unlocked, bool Explored);

        /// <summary>Every sector, read on one character at one time (At: Unix seconds), in the workshop of the Free Company
        /// the game says the character is in (FcId; 0 when it didn't say).</summary>
        public sealed record Reading(ulong Cid, IReadOnlyList<Sector> Sectors, double At, ulong FcId = 0);

        /// <summary>A reading ready to write: with the Free Company the character is in.</summary>
        public sealed record Row(ulong FcId, Reading Reading);

        /// <summary>Whether a reading makes sense, or why not. Before the game has the workshop's data it may answer that
        /// nothing is unlocked, which saved would wipe the FC's sectors: so the first sector must be unlocked, and nothing
        /// explored that isn't unlocked.</summary>
        public static string? Problem(IReadOnlyList<Sector> sectors)
        {
            if (sectors.Count == 0) return "no sectors were read";
            if (!sectors.Any(s => s.Id == FirstSector && s.Unlocked)) return "the first sector reads as locked, so the game doesn't have the workshop's data yet";
            if (sectors.Any(s => s.Explored && !s.Unlocked)) return "a sector reads as explored but not unlocked";
            return null;
        }

        /// <summary>The readings waiting for the next sync. Safe to use from the game's thread and the sync's at once.</summary>
        public sealed class Log
        {
            private readonly object gate = new();
            private readonly Dictionary<ulong, string> last = new();
            private readonly Dictionary<ulong, Reading> pending = new();

            /// <summary>Keeps a sensible reading when it differs from the character's last one (or is its first). Waiting
            /// readings of a character are replaced by the newer. True when kept.</summary>
            public bool Offer(Reading reading)
            {
                if (reading.Cid == 0 || Problem(reading.Sectors) != null) return false;
                var key = Key(reading.Sectors);
                lock (gate)
                {
                    if (last.TryGetValue(reading.Cid, out var was) && was == key) return false;
                    last[reading.Cid] = key;
                    pending[reading.Cid] = reading;
                    return true;
                }
            }

            /// <summary>Everything waiting, and no longer waiting.</summary>
            public List<Reading> Take()
            {
                lock (gate)
                {
                    var all = pending.Values.OrderBy(r => r.At).ToList();
                    pending.Clear();
                    return all;
                }
            }

            /// <summary>Readings a sync couldn't write, back unless a newer one of that character came in since.</summary>
            public void PutBack(IEnumerable<Reading> readings)
            {
                lock (gate)
                    foreach (var r in readings)
                        if (!pending.ContainsKey(r.Cid)) pending[r.Cid] = r;
            }

            public int Count { get { lock (gate) return pending.Count; } }

            private static string Key(IEnumerable<Sector> sectors) =>
                string.Join(",", sectors.OrderBy(s => s.Id).Select(s => $"{s.Id}{(s.Unlocked ? "u" : "")}{(s.Explored ? "e" : "")}"));
        }

        /// <summary>The Free Company a reading is filed under: the workshop's own, as the game said when it was read, else the
        /// one AutoRetainer has for the character (fcOf; 0 for none). 0: not known yet.</summary>
        public static ulong FcFor(ulong readFc, ulong cid, IReadOnlyDictionary<ulong, ulong> fcOf) =>
            readFc != 0 ? readFc : fcOf.TryGetValue(cid, out var fc) ? fc : 0;

        /// <summary>The readings to write: those of characters this setup syncs (fcOf: each one's FC, 0 for none) whose Free
        /// Company is known, the newest per FC.</summary>
        public static List<Row> ToWrite(IEnumerable<Reading> readings, IReadOnlyDictionary<ulong, ulong> fcOf) =>
            readings.Where(r => fcOf.ContainsKey(r.Cid) && FcFor(r.FcId, r.Cid, fcOf) != 0)
                .GroupBy(r => FcFor(r.FcId, r.Cid, fcOf)).Select(g => new Row(g.Key, g.OrderBy(r => r.At).Last())).ToList();

        /// <summary>The readings to keep waiting for a later sync: their character isn't synced in this one (not set up in
        /// AutoRetainer yet, or synced from another PC), or no Free Company is known for it yet. Kept so a reading isn't
        /// lost for the session while its character's unchanged sectors aren't offered again.</summary>
        public static List<Reading> Waiting(IEnumerable<Reading> readings, IReadOnlyDictionary<ulong, ulong> fcOf) =>
            readings.Where(r => !fcOf.ContainsKey(r.Cid) || FcFor(r.FcId, r.Cid, fcOf) == 0).ToList();
    }
}
