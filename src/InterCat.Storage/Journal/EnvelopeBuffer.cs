using System.Buffers;

namespace InterCat.Storage;

/// <summary>
/// One pooled byte buffer with a single owner. §18.1 requires that every buffer has one owner at a time —
/// callback, then queue, then journal writer, then returned pool — and that ownership is expressed by an
/// explicit disposable lease rather than by convention. A returned lease refuses every read, so a
/// use-after-return is an exception at the point of the mistake rather than corrupted evidence later.
/// </summary>
public sealed class EnvelopeBuffer : IDisposable
{
    private byte[]? rented;
    private int length;

    private EnvelopeBuffer(byte[] rented, int length)
    {
        this.rented = rented;
        this.length = length;
    }

    /// <summary>A lease over zero bytes. It owns nothing, so disposing it is free and always safe.</summary>
    public static EnvelopeBuffer Empty { get; } = new([], 0);

    /// <summary>
    /// True once the lease has been returned. A returned lease can still be asked this. The shared empty
    /// lease never returns, because it never owned anything to return.
    /// </summary>
    public bool IsReturned => rented is null;

    public int Length => rented is null
        ? throw new ObjectDisposedException(nameof(EnvelopeBuffer), ReturnedMessage)
        : length;

    /// <summary>Rents a buffer of exactly this length from the shared pool.</summary>
    public static EnvelopeBuffer Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return length == 0 ? Empty : new(ArrayPool<byte>.Shared.Rent(length), length);
    }

    /// <summary>
    /// Rents a buffer and copies these bytes into it. This is the callback's operation: the source span
    /// points into memory the callback does not own past its return, so it is copied, never kept.
    /// </summary>
    public static EnvelopeBuffer CopyOf(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            return Empty;
        }

        EnvelopeBuffer buffer = Rent(source.Length);
        source.CopyTo(buffer.Span);
        return buffer;
    }

    /// <summary>The writable bytes. Throws once the lease is returned.</summary>
    public Span<byte> Span => rented is null
        ? throw new ObjectDisposedException(nameof(EnvelopeBuffer), ReturnedMessage)
        : rented.AsSpan(0, length);

    /// <summary>The readable bytes. Throws once the lease is returned.</summary>
    public ReadOnlySpan<byte> ReadOnlySpan => Span;

    /// <summary>Copies the bytes out into an independent array, for a caller that outlives the lease.</summary>
    public byte[] ToArray() => Span.ToArray();

    /// <summary>
    /// Returns the buffer to the pool. Disposing twice is safe and is not a second return: a lease that
    /// returned its array twice would hand the same buffer to two owners, which is the failure this type
    /// exists to prevent.
    /// </summary>
    public void Dispose()
    {
        // A lease over nothing owns nothing, so disposing it is a no-op. The shared empty lease must
        // survive it: marking that one returned would make every later empty body unreadable.
        if (length == 0)
        {
            return;
        }

        byte[]? array = Interlocked.Exchange(ref rented, null);
        if (array is null)
        {
            return;
        }

        length = 0;
        ArrayPool<byte>.Shared.Return(array);
    }

    private const string ReturnedMessage =
        "This envelope buffer was returned to the pool. Its bytes belong to whoever rents it next.";
}
