using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>The fixture and build a window message evaluation is for.</summary>
public sealed record WindowMessageCoverageSettings
{
    public required string FixtureId { get; init; }
    public required string BuildId { get; init; }
    public required bool BuildIsSupported { get; init; }
    public bool Reproduced { get; init; }
}

/// <summary>
/// One of a capture's window message records as a session holds it (ADR-087): which of a message's ends it is, by its kind
/// and direction - a send's start or stop at the sender, its retrieval's start or stop at the receiver, a post's queueing or
/// its retrieval - the process it binds to since `process-binding-v7`, the message's number the gate wrote over its pointer,
/// and the process a send's start names at its other end.
/// </summary>
public sealed record WindowMessageObservation
{
    /// <summary>The record's place in the capture, which orders its records.</summary>
    public required long Ordinal { get; init; }

    public required ObservationKind Kind { get; init; }

    public required Direction Direction { get; init; }

    /// <summary>The process the record binds to; null for one that binds to none.</summary>
    public int? ProcessId { get; init; }

    /// <summary>The message's number in the capture, never its pointer; null where the record carries none.</summary>
    public long? MessageNumber { get; init; }

    /// <summary>The process a send's start names at its other end; null for every other end, or a start that named none.</summary>
    public int? PeerProcessId { get; init; }
}

/// <summary>What one of a window message's records is, by its kind and direction (ADR-087's addendum).</summary>
public enum WindowMessageEnd
{
    /// <summary>A record of no end a message has.</summary>
    Unknown = 0,

    /// <summary>A sent message's start, raised in the sender and naming the process that receives it.</summary>
    SendStart = 1,

    /// <summary>A sent message's stop, raised in the sender as its send returns.</summary>
    SendStop = 2,

    /// <summary>The receiver's retrieval of a sent message, its start.</summary>
    RetrieveSendStart = 3,

    /// <summary>The receiver's retrieval of a sent message, its end.</summary>
    RetrieveSendStop = 4,

    /// <summary>A posted message's queueing, raised in the poster.</summary>
    QueuePost = 5,

    /// <summary>A posted message's retrieval, raised in the receiver.</summary>
    RetrievePost = 6,
}

/// <summary>
/// One truth message and what the capture showed of it: its kind - sent, posted, or a window's close, posted after its
/// messages - the window it went to by the truth's name for it, the number of its rank among the capture's messages of its
/// kind from the sender to the receiver, whether that number has every end each in its own process, and the process at its
/// other end; or why not.
/// </summary>
public sealed record WindowMessageTruthCoverage(
    string Kind,
    string? Window,
    long? CallId,
    long? MessageNumber,
    bool EveryEnd,
    int? OtherEndProcessId,
    string? Gap);

/// <summary>The evaluation result: the section 14.2 counters plus the evidence behind every one of them.</summary>
public sealed record WindowMessageCoverageResult
{
    public required MechanismMeasurement Measurement { get; init; }
    public required TierAssessment Assessment { get; init; }
    public required IReadOnlyList<WindowMessageTruthCoverage> Messages { get; init; }

    /// <summary>The truth messages whose number had every end, each raised in its own process.</summary>
    public required int MessagesWithEveryEnd { get; init; }

    /// <summary>The capture's messages from the sender to the receiver beyond as many as the truth logged of their kind.</summary>
    public required int MessagesBeyondTruth { get; init; }

    /// <summary>The window message records of processes the truth does not name: a machine-wide capture sees them; they are scope.</summary>
    public required int ObservationsOutsideScope { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Compares an independent truth log of window messages - each message a sender sent or posted a receiver's window, and the
/// close it posted each window after its messages - with a capture's window message records (ADR-086, ADR-087), and computes
/// the section 14.2 counters (ADR-088). No record names which message it is - Win32k carries no message number, parameter or
/// size - so a truth message is the number of its rank among the capture's messages of its kind from the sender to the
/// receiver: a sent one among the numbers whose start the sender raised naming the receiver, a posted one or a close among
/// those the sender queued that no other process alone retrieved, each in the order of its first record, as the sender sends
/// and posts them one after another, the windows one after another too. Where the capture holds as many of a kind as the
/// truth logged, each truth message is the number of its rank; where it holds more or fewer, no rank says which is which, and
/// none of that kind is matched. A message is observed with every end where its number has a send's start and stop raised in
/// the sender and its retrieval's start and stop in the receiver, or a post's queueing in the sender and its retrieval in the
/// receiver. Its peer is the process at its other end - the receiver a send's start names, and the process its retrieval
/// binds to - false unless it is the receiver the truth logged. No record names the window or carries a size, so no message
/// is bound to a resource instance, no window is discovered and the byte criterion is not measured; the mechanism claims
/// operations, so it reaches experimental evidence at most (P27). It is pure and portable: the same evaluation runs in a test
/// on committed evidence (R19, R18).
/// </summary>
public static class WindowMessageCoverageEvaluator
{
    /// <summary>The status a sender's truth logs a posted message with; a sent one's names its message, WM_COPYDATA.</summary>
    public const string PostedStatus = "posted";

    /// <summary>The status a truth logs each of the receiver's windows with, whose close the sender posts after its messages.</summary>
    public const string WindowStatus = "window";

    /// <summary>Which of a message's ends a record is, by its kind and direction.</summary>
    public static WindowMessageEnd EndOf(ObservationKind kind, Direction direction) => (kind, direction) switch
    {
        (ObservationKind.RequestStart, Direction.Outbound) => WindowMessageEnd.SendStart,
        (ObservationKind.RequestEnd, Direction.Outbound) => WindowMessageEnd.SendStop,
        (ObservationKind.RequestStart, Direction.Inbound) => WindowMessageEnd.RetrieveSendStart,
        (ObservationKind.RequestEnd, Direction.Inbound) => WindowMessageEnd.RetrieveSendStop,
        (ObservationKind.Send, Direction.Outbound) => WindowMessageEnd.QueuePost,
        (ObservationKind.Receive, Direction.Inbound) => WindowMessageEnd.RetrievePost,
        _ => WindowMessageEnd.Unknown,
    };

    public static WindowMessageCoverageResult Evaluate(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<WindowMessageObservation> observations,
        WindowMessageCoverageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(settings);

        // The sender, whose log has each message it sent and posted, and the receiver, whose window received them.
        int[] senders = [.. truth.Where(static record => record.Kind == TruthEventKind.MessageSent).Select(static record => record.ProcessId).Distinct()];
        int[] receivers = [.. truth.Where(static record => record.Kind == TruthEventKind.MessageReceived).Select(static record => record.ProcessId).Distinct()];
        int? sender = senders.Length == 1 ? senders[0] : null;
        int? receiver = receivers.Length == 1 ? receivers[0] : null;

        // The truth's messages in the sender's order: its sends, and its posts with the close it posted each window after
        // them, in the one order it logged them - every window's posts and its close before the next window's.
        TruthRecord[] sent = [.. truth.Where(record => record.Kind == TruthEventKind.MessageSent && record.ProcessId == sender
            && !string.Equals(record.Status, PostedStatus, StringComparison.Ordinal)).OrderBy(static record => record.Sequence)];
        TruthRecord[] posted = [.. truth.Where(record => record.ProcessId == sender
            && ((record.Kind == TruthEventKind.MessageSent && string.Equals(record.Status, PostedStatus, StringComparison.Ordinal))
                || (record.Kind == TruthEventKind.HandleClosed && string.Equals(record.Status, WindowStatus, StringComparison.Ordinal))))
            .OrderBy(static record => record.Sequence)];

        // The windows the sender's messages went to, by the truth's name for each, in the order it first messaged them.
        string?[] windows = [.. sent.Concat(posted).OrderBy(static record => record.Sequence).Select(static record => record.ResourceName).Distinct()];

        // Every number's records in the capture's order, whichever process each binds to: a message the sender made to another
        // process's window shows there. Records of processes the truth does not name are scope.
        int outsideScope = observations.Count(observation => observation.ProcessId is not { } process || (process != sender && process != receiver));
        var byNumber = observations.Where(static observation => observation.MessageNumber is not null)
            .OrderBy(static observation => observation.Ordinal)
            .GroupBy(static observation => observation.MessageNumber!.Value)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        // A sent message of the sender's to the receiver is a number whose start the sender raised naming the receiver; a
        // posted one a number the sender queued and the receiver retrieved, or that no other process retrieved - a post only
        // another process retrieved went to its window. Each kind in the order of its first record.
        long[] sends = [.. byNumber.Where(pair => pair.Value.Any(record => EndOf(record.Kind, record.Direction) == WindowMessageEnd.SendStart
            && record.ProcessId == sender && record.PeerProcessId == receiver)).OrderBy(static pair => pair.Value[0].Ordinal).Select(static pair => pair.Key)];
        long[] posts = [.. byNumber.Where(pair => Has(pair.Value, WindowMessageEnd.QueuePost, sender)
                && (Has(pair.Value, WindowMessageEnd.RetrievePost, receiver)
                    || !pair.Value.Any(record => EndOf(record.Kind, record.Direction) == WindowMessageEnd.RetrievePost && record.ProcessId != receiver)))
            .OrderBy(static pair => pair.Value[0].Ordinal).Select(static pair => pair.Key)];

        var messages = new List<WindowMessageTruthCoverage>(sent.Length + posted.Length);
        int attributed = 0;
        int falsePeers = 0;
        foreach ((TruthRecord[] records, long[] numbers, bool isSent) in new[] { (sent, sends, true), (posted, posts, false) })
        {
            bool ranked = sender is not null && receiver is not null && records.Length == numbers.Length;
            for (int rank = 0; rank < records.Length; rank++)
            {
                TruthRecord record = records[rank];
                string kind = isSent ? "sent" : record.Kind == TruthEventKind.HandleClosed ? "close" : PostedStatus;
                if (!ranked)
                {
                    messages.Add(new(kind, record.ResourceName, record.CallId, null, false, null, sender is null || receiver is null
                        ? "The truth names no one sender and one receiver."
                        : $"The capture holds {numbers.Length} {(isSent ? "sent" : "posted")} messages from the sender to the receiver "
                            + $"where the truth logged {records.Length}, so no rank says which is which."));
                    continue;
                }

                long number = numbers[rank];
                WindowMessageObservation[] ends = byNumber[number];

                // Its other end: the receiver a send's start names, and every process its retrieval binds to - a send's or a
                // post's. One that is not the receiver is a false attribution, and so is a number whose ends name two.
                int[] others =
                [
                    .. ends.Where(end => EndOf(end.Kind, end.Direction) == WindowMessageEnd.SendStart).Select(static end => end.PeerProcessId)
                        .OfType<int>(),
                    .. ends.Where(end => EndOf(end.Kind, end.Direction) is WindowMessageEnd.RetrieveSendStart or WindowMessageEnd.RetrieveSendStop
                        or WindowMessageEnd.RetrievePost).Select(static end => end.ProcessId).OfType<int>(),
                ];
                int[] named = [.. others.Distinct().Order()];
                int? otherEnd = named.Length == 1 ? named[0] : null;
                bool falsely = named.Any(process => process != receiver);
                attributed += named.Length > 0 ? 1 : 0;
                falsePeers += falsely ? 1 : 0;
                bool every = isSent
                    ? Has(ends, WindowMessageEnd.SendStart, sender) && Has(ends, WindowMessageEnd.SendStop, sender)
                        && Has(ends, WindowMessageEnd.RetrieveSendStart, receiver) && Has(ends, WindowMessageEnd.RetrieveSendStop, receiver)
                    : Has(ends, WindowMessageEnd.QueuePost, sender) && Has(ends, WindowMessageEnd.RetrievePost, receiver);
                messages.Add(new(kind, record.ResourceName, record.CallId, number, every && !falsely, otherEnd, every && !falsely ? null
                    : falsely ? "Its ends name a process at its other end the truth says did not receive it."
                    : isSent ? "Its number lacks a send's start or stop in the sender, or its retrieval's start or stop in the receiver."
                    : "Its number lacks its queueing in the sender or its retrieval in the receiver."));
            }
        }

        int observed = messages.Count(static message => message.MessageNumber is not null);
        int withEveryEnd = messages.Count(static message => message.EveryEnd);
        int beyond = Math.Max(0, sends.Length - sent.Length) + Math.Max(0, posts.Length - posted.Length);
        var measurement = new MechanismMeasurement
        {
            FixtureId = settings.FixtureId,
            BuildId = settings.BuildId,
            Reproduced = settings.Reproduced,
            MeasuredOnSupportedBuild = settings.BuildIsSupported,
            TruthOperations = messages.Count,
            TruthOperationsWithAdmittedObservation = observed,

            // A message names no window - its handle is not admitted - so no message is bound to a resource instance.
            TruthOperationsBoundToResourceInstance = 0,
            PeerAttributions = attributed,
            FalsePeerAttributions = falsePeers,

            // No window message record carries a size, so no message is byte eligible: not measured, never a measured zero
            // (R3, P1).
            ByteEligibleOperations = 0,
            ByteMeasuredOperations = 0,

            // None of the receiver's windows has a record of its own, so none is discovered and no lifetime can be shown for
            // one; a window's two ends are its memberships, each resolved where a message to it was observed at that end.
            TruthResources = windows.Length,
            DiscoveredResourcesWithLifetime = 0,
            ExpectedMemberships = 2L * windows.Length,
            ResolvedMemberships = windows.Sum(window =>
                (messages.Any(message => message.Window == window && message.MessageNumber is not null) ? 1 : 0)
                + (messages.Any(message => message.Window == window && message.MessageNumber is not null && message.OtherEndProcessId == receiver) ? 1 : 0)),
            ClaimsBytesOrOperations = true,
        };

        var notes = new List<string>
        {
            $"{observed} of {messages.Count} truth messages were the number of their rank among the capture's messages of their "
            + $"kind from the sender to the receiver, and {withEveryEnd} of them had every end, each raised in its own process.",
            "No record names which message it is, the window it went to or its size: a message is bound to no resource instance, "
            + "and the byte criterion is not measured (R3, P1).",
            $"The truth's messages went to {windows.Length} of the receiver's windows, which no record names: none is discovered, "
            + "and each one's two ends are memberships resolved where a message to it was observed at that end.",
            $"{outsideScope} window message records were of processes the truth does not name: a machine-wide capture's scope, not error.",
        };
        if (beyond > 0)
        {
            notes.Add($"{beyond} of the capture's messages from the sender to the receiver were beyond as many as the truth logged of their kind.");
        }

        return new()
        {
            Measurement = measurement,
            Assessment = CoverageTierCalculator.Assess(measurement),
            Messages = messages,
            MessagesWithEveryEnd = withEveryEnd,
            MessagesBeyondTruth = beyond,
            ObservationsOutsideScope = outsideScope,
            Notes = notes,
        };
    }

    // Whether one of a number's records is the end given, raised in the process given.
    private static bool Has(WindowMessageObservation[] records, WindowMessageEnd end, int? process) =>
        records.Any(record => EndOf(record.Kind, record.Direction) == end && record.ProcessId == process);
}
