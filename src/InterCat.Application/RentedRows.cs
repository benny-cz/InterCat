using System.Buffers;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A per-row buffer rented for one segment's pass and given back after it. A query the window runs on every interaction
/// then allocates no array the size of a segment (R11), and nothing the size of the session stays resident (S2). The
/// buffer can be longer than the segment: only its first <see cref="SegmentReaderV1.RowCount"/> slots are the segment's,
/// so a loop runs to the row count, never to the buffer's length.
/// </summary>
internal readonly struct RentedRows<T> : IDisposable
    where T : struct
{
    private RentedRows(T[] buffer, int rows)
    {
        Buffer = buffer;
        Rows = rows;
    }

    /// <summary>The buffer, indexed by row.</summary>
    public T[] Buffer { get; }

    /// <summary>The segment's rows: the slots of <see cref="Buffer"/> that are its.</summary>
    public int Rows { get; }

    /// <summary>The segment's slots, for a method that fills one per row.</summary>
    public Span<T> Span => Buffer.AsSpan(0, Rows);

    public static RentedRows<T> For(SegmentReaderV1 segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new(ArrayPool<T>.Shared.Rent(segment.RowCount), segment.RowCount);
    }

    public void Dispose()
    {
        if (Buffer is not null)
        {
            ArrayPool<T>.Shared.Return(Buffer);
        }
    }
}
