using System.Collections.Generic;

namespace XIVFleetCompanion;

/// <summary>
/// Which characters are synced. Either every character AutoRetainer knows except the ones unticked
/// (the default, so a new character is synced), or only the ones ticked (so a new character is not).
/// Each way keeps its own list, so switching back and forth does not lose either. No Dalamud here, so
/// it is tested on its own (tests/FleetWriterTests).
/// </summary>
public static class CharacterChoice
{
    public static bool ShouldSync(ulong cid, bool onlyChosen, ISet<ulong> chosen, ISet<ulong> skipped)
        => onlyChosen ? chosen.Contains(cid) : !skipped.Contains(cid);

    /// <summary>Ticks or unticks one character in whichever list the current way uses.</summary>
    public static void Set(ulong cid, bool sync, bool onlyChosen, ISet<ulong> chosen, ISet<ulong> skipped)
    {
        if (onlyChosen)
        {
            if (sync) chosen.Add(cid); else chosen.Remove(cid);
        }
        else
        {
            if (sync) skipped.Remove(cid); else skipped.Add(cid);
        }
    }

    /// <summary>
    /// The logins a setup records (see FleetWriter.RecordLoginsAsync): only of characters it syncs. A character
    /// unticked here never claims to be this setup's, so it can't be taken from a setup that does sync it.
    /// </summary>
    public static List<KeyValuePair<ulong, System.DateTime>> LoginsToRecord(IEnumerable<KeyValuePair<ulong, System.DateTime>> logins, System.Func<ulong, bool> shouldSync)
    {
        var list = new List<KeyValuePair<ulong, System.DateTime>>();
        foreach (var login in logins)
            if (shouldSync(login.Key)) list.Add(login);
        return list;
    }

    /// <summary>Ticks exactly the given characters out of all of them (e.g. only those with subs).</summary>
    public static void SetExactly(IEnumerable<ulong> all, ISet<ulong> toSync, bool onlyChosen, ISet<ulong> chosen, ISet<ulong> skipped)
    {
        foreach (var cid in all)
            Set(cid, toSync.Contains(cid), onlyChosen, chosen, skipped);
    }
}
