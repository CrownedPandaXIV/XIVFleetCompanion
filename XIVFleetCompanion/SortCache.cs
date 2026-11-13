using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVFleetCompanion;

/// <summary>
/// A table's rows sorted by a clicked column, kept between frames. The window draws every frame, and sorting a big
/// table by a text column each time costs a large share of a frame (10,000 rows: about 9 ms of the 16.7 ms a frame has
/// at 60 fps), so the rows are only sorted again when they change (the same rows, in the same order, as last time),
/// the column or direction changes, or a second has passed (for a sort that depends on the time). Pages that filter
/// their rows each frame hand over a new list every time, so the rows themselves are compared, not the list.
/// No Dalamud here, so it is tested on its own (tests/FleetWriterTests).
/// </summary>
public sealed class SortCache<T>
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(1);

    private T[] input = Array.Empty<T>();
    private object? sortedBy;
    private DateTime sortedAt;
    private List<T> sorted = new();

    /// <summary>How many times the rows were actually sorted (for the tests).</summary>
    public int Sorts { get; private set; }

    /// <summary>The rows sorted by key (missing values last, whichever the direction). column: the column's own key, so a
    /// different column sorts again.</summary>
    public List<T> Sorted(IReadOnlyList<T> rows, string column, bool ascending, Func<T, object?> key, Func<T, bool>? missing, DateTime now)
    {
        var by = (column, ascending);
        if (Equals(by, sortedBy) && now - sortedAt < KeepFor && now >= sortedAt && Same(rows)) return sorted;
        var byMissing = rows.OrderBy(r => missing != null && missing(r));
        sorted = (ascending ? byMissing.ThenBy(key) : byMissing.ThenByDescending(key)).ToList();
        input = rows.ToArray();
        sortedBy = by;
        sortedAt = now;
        Sorts++;
        return sorted;
    }

    private bool Same(IReadOnlyList<T> rows)
    {
        if (rows.Count != input.Length) return false;
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < input.Length; i++)
        {
            var same = typeof(T).IsValueType ? comparer.Equals(rows[i], input[i]) : ReferenceEquals(rows[i], input[i]);
            if (!same) return false;
        }
        return true;
    }
}
