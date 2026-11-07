using System.Diagnostics;

namespace InterCat.Sdk;

/// <summary>What a channel carries its messages through, as the application says (`contracts/sdk-v1.md` §2).</summary>
public enum InterCatChannelKind
{
    /// <summary>A channel of a kind the SDK names none for.</summary>
    Other = 0,

    /// <summary>A named or anonymous pipe.</summary>
    Pipe = 1,

    /// <summary>A socket.</summary>
    Socket = 2,

    /// <summary>Shared memory, such as a ring buffer in a section two processes map.</summary>
    SharedMemory = 3,
}

/// <summary>
/// One process's use of a channel at one epoch (`contracts/sdk-v1.md` §2): every process publishing to it or consuming
/// from it opens it under the identity they share, and says each message's publication and each reader's consumption at the
/// message's sequence, as logical boundaries - never each load or store of the memory beneath. A new epoch is a new
/// channel incarnation: after the sequence wraps, or the channel is made again. Nothing is raised while no session listens.
/// </summary>
public sealed class InterCatChannel : IDisposable
{
    /// <summary>The most messages one batch says (ADR-106).</summary>
    public const int MaximumBatch = 65_536;

    private int closed;

    private InterCatChannel(Guid id, string name, InterCatChannelKind kind, long epoch)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Epoch = epoch;
    }

    /// <summary>The identity every process using the channel names it by.</summary>
    public Guid Id { get; }

    /// <summary>The channel's name, as the application gives it.</summary>
    public string Name { get; }

    /// <summary>What the channel carries its messages through.</summary>
    public InterCatChannelKind Kind { get; }

    /// <summary>The channel's incarnation, from 1: a sequence is unique within one epoch alone.</summary>
    public long Epoch { get; }

    /// <summary>Opens the process's use of channel <paramref name="id"/> at <paramref name="epoch"/>, saying so.</summary>
    public static InterCatChannel Open(Guid id, string name, InterCatChannelKind kind, long epoch = 1)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(epoch, 1);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        InterCatSdkEventSource log = InterCatSdkEventSource.Log;
        if (log.IsEnabled())
        {
            log.ChannelOpened(Environment.ProcessId, id, epoch, (int)kind, name);
        }

        return new(id, name, kind, epoch);
    }

    /// <summary>
    /// Says the message at <paramref name="sequence"/> was published - committed where its readers can take it - with its
    /// logical length and the bytes copied for it where the publisher knows them (-1 where not).
    /// </summary>
    public void Published(long sequence, long logicalLength, long copiedLength = -1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(logicalLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(copiedLength, -1);
        InterCatSdkEventSource log = InterCatSdkEventSource.Log;
        if (log.IsEnabled())
        {
            log.MessagePublished(Environment.ProcessId, Id, Epoch, sequence, logicalLength, copiedLength, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Says reader <paramref name="consumer"/> consumed the message at <paramref name="sequence"/>, of its logical length:
    /// each reader of one message says so once, and the message was still published once.
    /// </summary>
    public void Consumed(long sequence, long logicalLength, long consumer = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(logicalLength);
        InterCatSdkEventSource log = InterCatSdkEventSource.Log;
        if (log.IsEnabled())
        {
            log.MessageConsumed(Environment.ProcessId, Id, Epoch, sequence, consumer, logicalLength, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Says the <paramref name="count"/> consecutive messages from <paramref name="firstSequence"/> on were published together
    /// - a batch, each committed where its readers can take it and still its own sequence, published once - with their
    /// logical lengths and the bytes copied for them together, where the publisher knows them (-1 where not). One call
    /// says what <paramref name="count"/> calls of <see cref="Published"/> would, for the cost of one (ADR-106); no message's
    /// own length is said.
    /// </summary>
    public void PublishedBatch(long firstSequence, int count, long logicalLength, long copiedLength = -1)
    {
        Batch(firstSequence, count, logicalLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(copiedLength, -1);
        InterCatSdkEventSource log = InterCatSdkEventSource.Log;
        if (log.IsEnabled())
        {
            log.MessagesPublished(Environment.ProcessId, Id, Epoch, firstSequence, count, logicalLength, copiedLength, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Says reader <paramref name="consumer"/> consumed the <paramref name="count"/> consecutive messages from
    /// <paramref name="firstSequence"/> on together - a batch, one consumption of each - with their logical lengths together.
    /// </summary>
    public void ConsumedBatch(long firstSequence, int count, long logicalLength, long consumer = 0)
    {
        Batch(firstSequence, count, logicalLength);
        InterCatSdkEventSource log = InterCatSdkEventSource.Log;
        if (log.IsEnabled())
        {
            log.MessagesConsumed(Environment.ProcessId, Id, Epoch, firstSequence, count, consumer, logicalLength, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>Closes the process's use of the channel's epoch, saying so once.</summary>
    public void Dispose()
    {
        InterCatSdkEventSource log = InterCatSdkEventSource.Log;
        if (Interlocked.Exchange(ref closed, 1) == 0 && log.IsEnabled())
        {
            log.ChannelClosed(Environment.ProcessId, Id, Epoch);
        }
    }

    /// <summary>
    /// Refuses a batch no SDK says: one of no messages or more than <see cref="MaximumBatch"/>, a negative length, or a run
    /// past the last sequence a long holds.
    /// </summary>
    private static void Batch(long firstSequence, int count, long logicalLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumBatch);
        ArgumentOutOfRangeException.ThrowIfNegative(logicalLength);

        // The run's last sequence, reckoned past what a long holds, so the check stands on its own whatever the count.
        if ((Int128)firstSequence + (count - 1) > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(firstSequence), firstSequence, "The batch's run would pass the last sequence a long holds.");
        }
    }
}
