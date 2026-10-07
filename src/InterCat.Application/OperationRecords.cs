namespace InterCat.Application;

/// <summary>
/// The records an operation scope names - an RPC channel's, call's or relationship's, or a process's HTTP exchanges or one
/// exchange - by the segment holding each, its rows there ascending. A page or a timeline focus of the operation then opens
/// only the segments holding them and tests a row against its own segment's rows alone (P25), however many records the
/// operation has.
/// </summary>
internal sealed class OperationRecords
{
    private readonly Dictionary<string, int[]> bySegment;

    private OperationRecords(Dictionary<string, int[]> bySegment, long count)
    {
        this.bySegment = bySegment;
        Count = count;
    }

    /// <summary>How many records the operation names, each once.</summary>
    public long Count { get; }

    /// <summary>Whether a segment holds any of the records.</summary>
    public bool Holds(string segment) => bySegment.ContainsKey(segment);

    /// <summary>The operation's rows of one segment, ascending; none for a segment holding none.</summary>
    public ReadOnlyMemory<int> RowsIn(string segment) => bySegment.TryGetValue(segment, out int[]? rows) ? rows : default;

    /// <summary>The records by segment, each once, however often <paramref name="records"/> names one.</summary>
    public static OperationRecords Of(IEnumerable<(string Segment, int Row)> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var grouped = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach ((string segment, int row) in records)
        {
            if (!grouped.TryGetValue(segment, out List<int>? rows))
            {
                grouped[segment] = rows = [];
            }

            rows.Add(row);
        }

        long count = 0;
        var bySegment = new Dictionary<string, int[]>(grouped.Count, StringComparer.Ordinal);
        foreach ((string segment, List<int> rows) in grouped)
        {
            int[] sorted = [.. rows.Order().Distinct()];
            bySegment[segment] = sorted;
            count += sorted.Length;
        }

        return new(bySegment, count);
    }
}
