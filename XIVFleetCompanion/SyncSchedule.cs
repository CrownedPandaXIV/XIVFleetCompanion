using System;

namespace XIVFleetCompanion;

/// <summary>
/// When to start a sync: every sync interval, and soon after a character logs out (AutoRetainer logs a
/// character out once it has finished with it, so its subs and retainers are then up to date), at
/// most once every <see cref="MinGap"/>. A request that comes in while a sync is running waits for it
/// to finish. No Dalamud here, so it is tested on its own (tests/FleetWriterTests).
/// </summary>
public sealed class SyncSchedule
{
    /// <summary>How long after a logout the sync starts, so the game and AutoRetainer have finished it.</summary>
    public static readonly TimeSpan AfterLogoutDelay = TimeSpan.FromSeconds(5);

    /// <summary>The least time between two syncs, so quick relogs do not start one each.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);

    private DateTime lastStart = DateTime.MinValue;
    private DateTime? requestedFor;

    public bool Requested => requestedFor.HasValue;

    /// <summary>A character logged out: sync soon. An earlier request still waiting keeps its time.</summary>
    public void RequestSoon(DateTime now) => requestedFor ??= now + AfterLogoutDelay;

    /// <summary>Why a sync should start now ("after logout" or "interval"), or null for not now.</summary>
    public string? ShouldStart(DateTime now, TimeSpan interval, bool busy)
    {
        if (busy) return null;
        if (requestedFor.HasValue && now >= requestedFor.Value && now - lastStart >= MinGap) return "after logout";
        if (now - lastStart >= interval) return "interval";
        return null;
    }

    /// <summary>A sync started; it covers any request made before now.</summary>
    public void Started(DateTime now)
    {
        lastStart = now;
        requestedFor = null;
    }
}
