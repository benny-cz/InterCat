using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class HttpExchangeIndex
{
    /// <summary>
    /// What an interval release owes the exchanges of <paramref name="segments"/> (ADR-043, `http-exchanges-v1` §2): each
    /// stays whole, so an exchange open across the boundary keeps its first buffer, which keys it, its parts and its
    /// duration; and a use of a number opened only because it repeated a buffer of the use before it keeps that use, whose
    /// going would otherwise join it to an earlier one. The grouping reads the buffers' numbers, parts, places and order
    /// alone, so their processes' instances are not read here.
    /// </summary>
    internal static ReleaseDependencies ReleaseDependenciesOf(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        Func<ulong, bool> released,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        Dictionary<RecordAddress, Place> places = ReadPlaces(fieldSegments, cancellationToken);
        var buffers = new List<Buffer>();
        for (int position = 0; position < segments.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Collect(segments[position], position, places, buffers);
        }

        var dependencies = new ReleaseDependencies(released);
        foreach (IGrouping<(int ProcessId, long Number), Buffer> number in buffers.GroupBy(buffer => (buffer.ProcessId, buffer.Number)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RowAddress[]? before = null;
            foreach ((List<Buffer> use, bool repeated) in Uses(number))
            {
                RowAddress[] rows = [.. use.Select(buffer => new RowAddress(
                    buffer.Address.Stream, buffer.Address.Epoch, buffer.Address.Ordinal, buffer.Address.FactKey))];
                dependencies.Together(rows);
                if (repeated && before is not null)
                {
                    dependencies.Add(rows, before);
                }

                before = rows;
            }
        }

        return dependencies;
    }
}
