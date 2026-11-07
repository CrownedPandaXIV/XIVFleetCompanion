using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion;

/// <summary>
/// Saves each sub's Next Rank date once an hour, as the app does while it's open (its ipc/subs.js saveForecasts), so the
/// forecast line on Route Optimization can later compare it with the real rank-up. Whichever copy gets there first in an
/// hour saves it; dates older than 30 days go. The plugin does this on the fleet file (Just this PC), where there's no
/// app to do it.
/// </summary>
public static class ForecastWriter
{
    public const int KeepDays = 30;

    /// <summary>One sub's Next Rank date: when (Unix milliseconds) it reaches `Rank`.</summary>
    public sealed record Forecast(long Cid, int Slot, int Rank, double PredictedAtMs, string SubName);

    /// <summary>The subs with a Next Rank date to save (a slot, a next rank and its date), from Route Optimization's plan.</summary>
    public static List<Forecast> Due(IEnumerable<SubRoute> subs) => subs
        .Where(s => s.Slot is > 0 && s.RankUp.NextRank != null && s.RankUp.NextRankAt != null)
        .Select(s => new Forecast(s.Cid, s.Slot!.Value, s.RankUp.NextRank!.Value, s.RankUp.NextRankAt!.Value, s.SubName))
        .ToList();

    /// <summary>
    /// Saves them under this hour, unless this hour's are already saved; drops those older than 30 days. How many were
    /// saved (0: already done this hour, or none due).
    /// </summary>
    public static async Task<int> SaveAsync(DbConnection conn, IReadOnlyList<Forecast> due)
    {
        if (due.Count == 0) return 0;
        var file = Db.IsFile(conn);
        // This hour, as each database keeps times.
        var hour = file ? "(CAST(" + Db.FileNow + " / 3600 AS INTEGER) * 3600.0)" : "date_trunc('hour', now())";
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = Db.Command(conn, $"SELECT count(*) FROM companion_rankup_forecast WHERE made_at = {hour}", tx))
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0)
            {
                await tx.RollbackAsync();
                return 0;
            }
        var saved = 0;
        foreach (var f in due)
        {
            object at = file ? f.PredictedAtMs / 1000.0 : DateTime.UnixEpoch.AddMilliseconds(f.PredictedAtMs);
            saved += await Db.Exec(conn, $@"INSERT INTO companion_rankup_forecast (cid, slot, rank, made_at, predicted_at, sub_name)
                VALUES (@cid, @slot, @rank, {hour}, @at, @name) ON CONFLICT DO NOTHING", tx,
                ("cid", f.Cid), ("slot", f.Slot), ("rank", f.Rank), ("at", at), ("name", f.SubName));
        }
        await Db.Exec(conn, file
            ? $"DELETE FROM companion_rankup_forecast WHERE made_at < {Db.FileNow} - {KeepDays} * 86400.0"
            : $"DELETE FROM companion_rankup_forecast WHERE made_at < now() - interval '{KeepDays} days'", tx);
        await tx.CommitAsync();
        return saved;
    }
}
