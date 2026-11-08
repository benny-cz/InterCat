using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A session's cooperative messages (`contracts/cooperative-messages-v1.md`, ADR-104) as `icat messages` and InterCat's
/// window read them alike (R18): the derivation over one generation, the clock its times are in, the evidence policy its
/// processes are named under, and what the capture covered of the SDK, said from the ledger (R21).
/// </summary>
public sealed record CooperativeMessageReading(
    Guid SessionId,
    long Generation,
    EvidencePolicy Policy,
    SourceClockDescriptor Clock,
    IReadOnlyList<MechanismCoverage> Coverage,
    CooperativeMessageIndex Index)
{
    /// <summary>How many messages every channel epoch holds together, each of a run counted.</summary>
    public long Messages => Index.Channels.Sum(channel => channel.Messages);
}

/// <summary>
/// Reads a session's cooperative messages, and says them in one set of words for the command line and InterCat's window:
/// a channel by its name and epoch, what it carries messages through, what became of each message, and the process that
/// published or read it. What an application says through the SDK is its own assertion, which the words keep saying.
/// </summary>
public static class CooperativeMessageText
{
    /// <summary>Why a generation's messages cannot be read without its clock.</summary>
    public const string NoClock = "The generation names no source clock, so no message has a process or a time.";

    /// <summary>The note a listing of messages carries: what a message is, and whose word it is.</summary>
    public const string Note = "A message is its channel's epoch and sequence, as the application numbered it: published once and "
        + "consumed once by each reader. What an application says through the SDK is its own word, never an observation of the "
        + "channel beneath it.";

    /// <summary>
    /// Reads <paramref name="store"/>'s current generation: its instances, as every other reading of it takes them, and each
    /// record of the SDK keyed by its channel's epoch and sequence, under one lease; and what the capture covered of the SDK.
    /// </summary>
    public static CooperativeMessageReading Read(
        SessionStore store,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest) ?? throw new InvalidDataException(NoClock);
        var generation = new GenerationSegments(store, manifest, clock);
        ProcessInstanceIndex processes = generation.Processes(cancellationToken);
        CooperativeMessageIndex index = CooperativeMessageIndex.Derive(generation.All, generation.Fields, processes, cancellationToken);
        IReadOnlyList<MechanismCoverage> coverage = SessionCoverage.ForMechanisms(
            SessionSegments.CoverageLedger(store.Root, manifest), [Mechanism.ApplicationSdk]);
        return new(manifest.SessionId, manifest.Generation, policy, clock, coverage, index);
    }

    /// <summary>What a channel's opening says it carries messages through, in words (`contracts/sdk-v1.md` §2).</summary>
    public static string Kind(long kind) => kind switch
    {
        0 => "another kind",
        1 => "a pipe",
        2 => "a socket",
        3 => "shared memory",
        _ => string.Create(CultureInfo.InvariantCulture, $"kind {kind}"),
    };

    /// <summary>What became of a message, in words.</summary>
    public static string State(CooperativeMessageState state) => state switch
    {
        CooperativeMessageState.Consumed => "consumed",
        CooperativeMessageState.Unconsumed => "open: no reader consumed it",
        CooperativeMessageState.PublicationUnseen => "consumed, its publication not recorded",
        CooperativeMessageState.Conflicting => "conflicting: published or read twice",
        _ => state.ToString(),
    };

    /// <summary>A channel epoch as a listing names it: its name, or its identity where no opening was recorded, and its epoch.</summary>
    public static string Channel(CooperativeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return (channel.Name ?? channel.Channel.ToString()) + string.Create(CultureInfo.CurrentCulture, $" · epoch {channel.Epoch:N0}");
    }

    /// <summary>
    /// What a channel epoch holds, as a listing counts it: its messages, how many were consumed, open, read with no
    /// publication recorded or conflicting - each only where any was - its readers and the bytes published.
    /// </summary>
    public static string Counted(CooperativeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var parts = new List<string> { CountText.Of(channel.Messages, "message") };
        if (channel.Consumed > 0) parts.Add(string.Create(CultureInfo.CurrentCulture, $"{channel.Consumed:N0} consumed"));
        if (channel.Unconsumed > 0) parts.Add(string.Create(CultureInfo.CurrentCulture, $"{channel.Unconsumed:N0} open"));
        if (channel.PublicationUnseen > 0) parts.Add(string.Create(CultureInfo.CurrentCulture, $"{channel.PublicationUnseen:N0} with no publication recorded"));
        if (channel.Conflicting > 0) parts.Add(string.Create(CultureInfo.CurrentCulture, $"{channel.Conflicting:N0} conflicting"));
        parts.Add(CountText.Of(channel.Readers, "reader"));
        parts.Add(CountText.Of(channel.PublishedBytes, "byte") + " published");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The process a record of a message names, as a listing says it: its executable and PID where the evidence policy
    /// admits its binding, or its PID and why no instance holds the record.
    /// </summary>
    public static string Process(CooperativeMessageReading reading, CooperativeMessageRecord record)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Process.IsAdmittedUnder(reading.Policy))
        {
            ProcessInstance instance = reading.Index.Processes.Instances[record.Process.Instance];
            return string.Create(CultureInfo.CurrentCulture, $"{instance.ImageName ?? "executable not witnessed"} · {instance.ProcessId}");
        }

        ProcessBindingReason reason = record.Process.IsBound ? ProcessBindingReason.NotAdmittedByPolicy : record.Process.Reason;
        return string.Create(CultureInfo.CurrentCulture, $"PID {record.ProcessId} · {BindingText.Reason(reason)}");
    }

    /// <summary>
    /// One message as a listing says it - or a run of messages said together in batches, every one of which its records say
    /// alike: its sequence or its run's, what became of it, its length as its publication stated it, or its batch's together,
    /// who published it and which readers consumed it in which process.
    /// </summary>
    public static string Message(CooperativeMessageReading reading, CooperativeMessage message)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(message);
        return (message.Count == 1 ? "Sequence " : "Sequences ") + Sequences(message) + " · " + State(message.State)
            + " · " + Length(message)
            + " · published by " + (message.Publication is { } publication ? Process(reading, publication) : "no recorded process")
            + " · " + (message.Consumptions.Count == 0 ? "no reader" : "read by " + Readers(reading, message));
    }

    /// <summary>A message's sequence, or a run's first and last: "41", or "1 to 100".</summary>
    public static string Sequences(CooperativeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Count == 1
            ? message.Sequence.ToString("N0", CultureInfo.CurrentCulture)
            : string.Create(CultureInfo.CurrentCulture, $"{message.Sequence:N0} to {message.Sequence + (message.Count - 1):N0}");
    }

    /// <summary>
    /// What a message's publication says of its length: its own - "4,096 bytes" - or its batch's together, never split among
    /// its messages - "said in a batch of 100 messages, 51,200 bytes together" - or that it says none.
    /// </summary>
    public static string Length(CooperativeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Publication is not { } publication)
        {
            return "length not stated";
        }

        if (publication.Batch is { } batch)
        {
            return "said in a batch of " + CountText.Of(batch, "message") + ", "
                + (publication.Length is { } together ? CountText.Of(together, "byte") + " together" : "their length not stated");
        }

        return publication.Length is { } length ? CountText.Of(length, "byte") : "length not stated";
    }

    /// <summary>
    /// Which readers consumed a message, each in the process its record names and how long after its publication was said
    /// it said so - "reader 1 in client.exe · 400, 10.0 µs after its publication was said" - or that none did.
    /// </summary>
    public static string Readers(CooperativeMessageReading reading, CooperativeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Consumptions.Count == 0
            ? "no reader"
            : string.Join(", ", message.Consumptions.Select(consumption =>
                string.Create(CultureInfo.CurrentCulture, $"reader {consumption.Consumer:N0} in ") + Process(reading, consumption.Record)
                + (consumption.LagNanoseconds is { } lag ? ", " + Gap(lag) + " its publication was said" : string.Empty)));
    }

    /// <summary>
    /// How long after their publications a channel epoch's consumptions were said (ADR-110), in words: at the median, and from
    /// the least to the most, with how many were said before their publication was - which the SDK allows, each record said
    /// after its moment, so it is never the messages' latency.
    /// </summary>
    public static string Lag(CooperativeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Lag is not { } lag)
        {
            return "No consumption follows a publication recorded once, so no lag between them is said.";
        }

        return $"Its consumptions were said {Gap(lag.MedianNanoseconds)} their publications at the median, from "
            + $"{Gap(lag.LeastNanoseconds)} to {Gap(lag.MostNanoseconds)}"
            + (lag.SaidBefore == 0 ? "." : $"; {CountText.Of(lag.SaidBefore, "consumption")} said before its publication was, as a "
                + "reader may: each record is said after its moment, so a lag is never a message's latency.");
    }

    /// <summary>A channel epoch's median lag as a table says it - "10.0 µs after" - or that none is said.</summary>
    public static string MedianLag(CooperativeLag? lag) => lag is { } said ? Gap(said.MedianNanoseconds) : "none said";

    /// <summary>A lag in words: "10.0 µs after", or - said first - "3.0 µs before".</summary>
    private static string Gap(long lag) => lag >= 0
        ? OperationText.Duration(lag, CultureInfo.CurrentCulture) + " after"
        : OperationText.Duration(-lag, CultureInfo.CurrentCulture) + " before";
}
