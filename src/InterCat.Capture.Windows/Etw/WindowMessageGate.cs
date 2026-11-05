namespace InterCat.Capture.Windows;

/// <summary>
/// One message pump record as the window message gate reads it (ADR-087): its event - a posted message's queueing or
/// retrieval, a sent message's start or stop at the sender, or its retrieval's start or stop at the receiver - the queue
/// message's kernel pointer, which joins a message's ends within its life, and when it was raised, in the session's ticks.
/// </summary>
public readonly record struct WindowMessageRecord(int EventId, ulong QueueMessage, long Raised);

/// <summary>Where the gate first met a message.</summary>
public enum MessageFirstSeen
{
    /// <summary>A posted message's queueing, raised in the process that posted it.</summary>
    Queued = 1,

    /// <summary>A sent message's start, raised in the process that sent it.</summary>
    Sent = 2,

    /// <summary>
    /// An end at a pointer no followed message of its kind held: of a message begun before the capture, or one the gate
    /// stopped following. Its later ends at the pointer are its own until another message begins there.
    /// </summary>
    Unstarted = 3,

    /// <summary>
    /// A record raised before one the session delivered ahead of it. The gate numbers by the order a session was measured
    /// delivering in (ADR-086's third addendum), which this record is not, so it is numbered alone and never joined.
    /// </summary>
    OutOfOrder = 4,
}

/// <summary>A message the gate numbered: its number in the capture, never its pointer, and where the gate first met it.</summary>
public readonly record struct WindowMessageIncarnation(long Number, MessageFirstSeen FirstSeen);

/// <summary>
/// Numbers each window message whose ends Win32k's message pump records (ADR-087): a sent message's start and stop at the
/// sender and its retrieval's start and stop at the receiver, a posted message's queueing and its retrieval. Its ends are
/// joined only by the queue message's kernel pointer, which the kernel reuses once the message is retrieved, so a message is
/// followed at its pointer from its start - a send's start or a post's queueing - to its end - the sender's stop or the
/// post's retrieval - and each start begins another, whatever the pointer held before ended, seen or not (R22). An end at a
/// pointer no followed message of its kind holds is of a message begun before the capture: it begins one there, numbered
/// apart, which its later ends join, as a session delivers a message's ends in the order they were raised and a message
/// begins only with a start (ADR-086's third addendum). That order is measured, not promised, so a record raised before
/// one delivered ahead of it is numbered alone and counted. Records are read as they arrive, and the gate holds none back.
/// It reads them on the capture's callback, so it allocates nothing per record (P12): it follows at most its bound of
/// messages, the oldest it follows given up first, each counted, and both its tables are sized once.
/// </summary>
public sealed class WindowMessageGate
{
    /// <summary>A posted message's queueing, raised in the poster.</summary>
    public const int QueuePost = 59;

    /// <summary>A sent message's start, raised in the sender and naming the process and thread that receive it.</summary>
    public const int SendStart = 60;

    /// <summary>A posted message's retrieval, raised in the receiver: the message's end.</summary>
    public const int RetrievePost = 61;

    /// <summary>A sent message's retrieval's start, raised in the receiver.</summary>
    public const int RetrieveSendStart = 62;

    /// <summary>A sent message's stop, raised in the sender: the message's end.</summary>
    public const int SendStop = 70;

    /// <summary>A sent message's retrieval's stop, raised in the receiver.</summary>
    public const int RetrieveSendStop = 83;

    /// <summary>The message pump's events that are a message's ends, the ones the gate reads.</summary>
    public static readonly IReadOnlyList<int> Ends = [QueuePost, SendStart, RetrievePost, RetrieveSendStart, SendStop, RetrieveSendStop];

    /// <summary>How many messages the gate follows at most: beyond them the oldest it follows is given up.</summary>
    public const int DefaultCapacity = 65_536;

    // Each followed message by its pointer.
    private readonly Dictionary<ulong, Followed> followed;

    // Each followed message's beginning, oldest first, in a ring the size of the bound: the slot a new beginning takes is the
    // oldest's, whose message, if the gate still follows it, is given up.
    private readonly (ulong Pointer, long Number)[] begun;
    private readonly int capacity;
    private int next;
    private int filled;
    private long numbered;
    private long latest = long.MinValue;

    /// <param name="capacity">How many messages the gate follows at most.</param>
    public WindowMessageGate(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
        followed = new(capacity);
        begun = new (ulong, long)[capacity];
    }

    /// <summary>How many messages the gate follows now: never more than its bound.</summary>
    public int Following => followed.Count;

    /// <summary>How many records the gate read raised before one delivered ahead of them, each numbered alone.</summary>
    public long DeliveredOutOfOrder { get; private set; }

    /// <summary>How many messages the gate gave up following, the oldest, to follow a newer one within its bound.</summary>
    public long GivenUp { get; private set; }

    /// <summary>Reads one record as it arrives: the message it is an end of.</summary>
    public WindowMessageIncarnation Read(in WindowMessageRecord record)
    {
        bool start = record.EventId is SendStart or QueuePost;
        bool ofASend = record.EventId switch
        {
            SendStart or RetrieveSendStart or RetrieveSendStop or SendStop => true,
            QueuePost or RetrievePost => false,
            _ => throw new ArgumentOutOfRangeException(nameof(record), record.EventId, "The gate reads a message's ends: a send's start, retrieval and stop, a post's queueing and retrieval."),
        };

        // The order the gate numbers by is the order raised; a record raised before one delivered ahead of it is not in it.
        if (record.Raised < latest)
        {
            DeliveredOutOfOrder++;
            return new(++numbered, MessageFirstSeen.OutOfOrder);
        }

        latest = record.Raised;
        bool last = record.EventId is SendStop or RetrievePost;
        if (!start && followed.TryGetValue(record.QueueMessage, out Followed found) && found.OfASend == ofASend)
        {
            // An end of the message its pointer has; the sender's stop or the post's retrieval ends it.
            if (last)
            {
                followed.Remove(record.QueueMessage);
            }

            return found.Incarnation;
        }

        // A start begins a message at its pointer; an end where no message of its kind is followed is of one begun before the
        // capture, or given up, which its later ends join - unless it is the message's last, which ends it at once.
        WindowMessageIncarnation incarnation = new(++numbered,
            start ? (ofASend ? MessageFirstSeen.Sent : MessageFirstSeen.Queued) : MessageFirstSeen.Unstarted);
        if (last)
        {
            followed.Remove(record.QueueMessage);
        }
        else
        {
            Begin(record.QueueMessage, new(incarnation, ofASend));
        }

        return incarnation;
    }

    private void Begin(ulong pointer, Followed message)
    {
        if (filled == capacity)
        {
            // The oldest beginning's message is given up if the gate still follows it, unless it is at this very pointer,
            // where the new message ends it as any start does.
            (ulong oldest, long number) = begun[next];
            if (oldest != pointer && followed.TryGetValue(oldest, out Followed held) && held.Incarnation.Number == number)
            {
                followed.Remove(oldest);
                GivenUp++;
            }
        }
        else
        {
            filled++;
        }

        begun[next] = (pointer, message.Incarnation.Number);
        next = next + 1 == capacity ? 0 : next + 1;
        followed[pointer] = message;
    }

    /// <summary>A followed message: its incarnation, and whether it is a sent message or a posted one.</summary>
    private readonly record struct Followed(WindowMessageIncarnation Incarnation, bool OfASend);
}
