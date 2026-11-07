using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>A session's cooperative messages, by channel's epoch (`contracts/cooperative-messages-v1.md`).</summary>
internal sealed record MessagesDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required long Generation { get; init; }
    public required string MessageRule { get; init; }
    public required string BindingRule { get; init; }
    public required string EvidencePolicy { get; init; }

    /// <summary>
    /// What the capture covered of the one mechanism this listing reads, the application SDK, over the session (R21): the
    /// ledger says it, never a count of none.
    /// </summary>
    public required IReadOnlyList<MechanismCoverage> Coverage { get; init; }

    /// <summary>The SDK's channel and message records that name no channel or message: a value they are keyed by was not kept.</summary>
    public required long WithoutMessage { get; init; }

    public required IReadOnlyList<MessagesChannelDocument> Channels { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }
}

internal sealed record MessagesChannelDocument
{
    public required Guid Channel { get; init; }
    public required long Epoch { get; init; }

    /// <summary>The name its first recorded opening gives it; null where no opening was recorded.</summary>
    public required string? Name { get; init; }

    /// <summary>What its first opening says it carries messages through, in words; null where no opening was recorded.</summary>
    public required string? Kind { get; init; }

    public required int Openings { get; init; }
    public required int Closings { get; init; }
    public required int Messages { get; init; }
    public required int Consumed { get; init; }
    public required int Unconsumed { get; init; }
    public required int PublicationUnseen { get; init; }
    public required int Conflicting { get; init; }
    public required int Readers { get; init; }
    public required long Consumptions { get; init; }
    public required long PublishedBytes { get; init; }

    /// <summary>Its first messages in sequence order, when messages were asked for; null otherwise.</summary>
    public IReadOnlyList<MessagesMessageDocument>? List { get; init; }
}

internal sealed record MessagesMessageDocument
{
    public required long Sequence { get; init; }
    public required string State { get; init; }
    public required int Publications { get; init; }

    /// <summary>Its first recorded publication; null where none was recorded.</summary>
    public required MessagesRecordDocument? Publication { get; init; }

    /// <summary>The bytes its publisher copied for it, where it said; null where it said it does not know.</summary>
    public required long? CopiedLength { get; init; }

    public required IReadOnlyList<MessagesConsumptionDocument> Consumptions { get; init; }
}

internal sealed record MessagesConsumptionDocument
{
    /// <summary>The reader, as the application numbers it.</summary>
    public required long Consumer { get; init; }

    public required MessagesRecordDocument Record { get; init; }
}

internal sealed record MessagesRecordDocument
{
    public required int ProcessId { get; init; }

    /// <summary>The instance the record binds to, when the evidence policy admits it; null otherwise.</summary>
    public required ProcessInstanceDocument? Process { get; init; }

    /// <summary>Why no instance holds the record under the evidence policy; null when one does.</summary>
    public required string? Unattributed { get; init; }

    public required long? Length { get; init; }

    /// <summary>The raising process's own clock reading at the record, as it stated it; null where it carries none.</summary>
    public required long? SourceTicks { get; init; }

    /// <summary>The record's session time, in seconds, invariantly; null where it has none.</summary>
    public required string? Seconds { get; init; }

    public required uint Stream { get; init; }
    public required uint SourceEpoch { get; init; }
    public required ulong Ordinal { get; init; }
}

/// <summary>
/// Lists a session's cooperative messages (ADR-104): what applications instrumented with InterCat's SDK said of their channels
/// - each message by its channel's epoch and sequence, published once and consumed once by each reader - grouped by channel.
/// What an application says is its own assertion, never an observation of the channel beneath it.
/// </summary>
internal static class MessagesCommand
{
    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? channelOption = command.TakeOption("--channel");
        string? messagesOption = command.TakeOption("--messages");
        string? policyOption = command.TakeOption("--evidence-policy");
        string? sessionPath = command.TakePositional();
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(command.Refusal(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null)
        {
            ConsoleUi.Failure("A session directory is required: icat messages <directory>.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sessionPath);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        Guid? channel = null;
        if (channelOption is not null)
        {
            if (!Guid.TryParse(channelOption, out Guid parsed))
            {
                ConsoleUi.Failure($"--channel expects a channel's identity, a GUID; '{channelOption}' is not one.");
                return InterCatExitCode.InvalidInvocation;
            }

            channel = parsed;
        }

        int listed = channel is null ? 0 : 20;
        if (messagesOption is not null
            && (!int.TryParse(messagesOption, NumberStyles.None, CultureInfo.InvariantCulture, out listed) || listed > 10_000))
        {
            ConsoleUi.Failure($"--messages expects a number of messages per channel up to 10,000; '{messagesOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!EvidencePolicyOption.TryParse(policyOption, out EvidencePolicy policy, out string? policyRefusal))
        {
            ConsoleUi.Failure(policyRefusal!);
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Acquiring the current generation of {full} and verifying every dependency.");
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (store.Current is null)
        {
            return Icat.NoSession();
        }

        MessagesDocument document;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            if (SessionSegments.SourceClock(store.Root, manifest) is not { } clock)
            {
                ConsoleUi.Failure("The generation names no source clock, so no message has a process or a time.");
                return InterCatExitCode.PermissionOrCapabilityFailure;
            }

            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
            ConsoleUi.Progress("Deriving process instances and keying every SDK record by its channel's epoch and sequence.");
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, clock, fields, cancellationToken);
            CooperativeMessageIndex index = CooperativeMessageIndex.Derive(segments, fields, processes, cancellationToken);
            IReadOnlyList<MechanismCoverage> coverage = SessionCoverage.ForMechanisms(
                SessionSegments.CoverageLedger(store.Root, manifest), [Mechanism.ApplicationSdk]);
            document = Describe(full, manifest.Generation, index, coverage, clock, policy, channel, listed);
        }

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            Render(document, channel);
        }

        return store.Recovery.RolledBackToLastKnownGood ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
    }

    private static MessagesDocument Describe(
        string path,
        long generation,
        CooperativeMessageIndex index,
        IReadOnlyList<MechanismCoverage> coverage,
        SourceClockDescriptor clock,
        EvidencePolicy policy,
        Guid? channel,
        int listed) => new()
    {
        Contract = "cooperative-messages-v1",
        Path = path,
        Generation = generation,
        MessageRule = CooperativeMessageIndex.Rule,
        BindingRule = ProcessInstanceIndex.BindingRule,
        EvidencePolicy = policy.ToString(),
        Coverage = coverage,
        WithoutMessage = index.WithoutMessage,
        Channels =
        [
            .. index.Channels.Where(held => channel is not { } only || held.Channel == only).Select(held => new MessagesChannelDocument
            {
                Channel = held.Channel,
                Epoch = held.Epoch,
                Name = held.Name,
                Kind = held.Kind is { } kind ? KindText(kind) : null,
                Openings = held.Openings,
                Closings = held.Closings,
                Messages = held.Messages,
                Consumed = held.Consumed,
                Unconsumed = held.Unconsumed,
                PublicationUnseen = held.PublicationUnseen,
                Conflicting = held.Conflicting,
                Readers = held.Readers,
                Consumptions = held.Consumptions,
                PublishedBytes = held.PublishedBytes,
                List = listed == 0
                    ? null
                    : [.. index.MessagesOf(held).Take(listed).Select(message => new MessagesMessageDocument
                    {
                        Sequence = message.Sequence,
                        State = message.State.ToString(),
                        Publications = message.Publications,
                        Publication = message.Publication is { } publication ? Record(publication, index, clock, policy) : null,
                        CopiedLength = message.CopiedLength,
                        Consumptions =
                        [
                            .. message.Consumptions.Select(consumption => new MessagesConsumptionDocument
                            {
                                Consumer = consumption.Consumer,
                                Record = Record(consumption.Record, index, clock, policy),
                            }),
                        ],
                    })],
            }),
        ],
        Caveats =
        [
            "A message is its channel's identity, epoch and sequence, as the application numbered it: published once and consumed "
            + $"once by each reader ({CooperativeMessageIndex.Rule}, ADR-104). A publication no reader consumed is open as far as "
            + "anything recorded says; a consumption whose publication was not recorded says so; a message published twice, or "
            + "consumed twice by one reader, conflicts, and none of its records is chosen.",
            "What an application says through the SDK is its own assertion: a channel, a message and its length are the "
            + "application's word, never an observation of the channel beneath it.",
            $"A record of the SDK names the process that raised it, which owns it ({ProcessInstanceIndex.BindingRule}, ADR-030).",
        ],
    };

    private static MessagesRecordDocument Record(CooperativeMessageRecord record, CooperativeMessageIndex index, SourceClockDescriptor clock, EvidencePolicy policy)
    {
        bool admitted = record.Process.IsAdmittedUnder(policy);
        return new()
        {
            ProcessId = record.ProcessId,
            Process = admitted ? ProcessInstanceDocument.From(index.Processes.Instances[record.Process.Instance], clock) : null,
            Unattributed = admitted
                ? null
                : record.Process.IsBound ? ProcessBindingReason.NotAdmittedByPolicy.ToString() : record.Process.Reason.ToString(),
            Length = record.Length,
            SourceTicks = record.SourceTicks,
            Seconds = SessionText.Seconds(clock, record.Ticks),
            Stream = record.Address.Stream,
            SourceEpoch = record.Address.Epoch,
            Ordinal = record.Address.Ordinal,
        };
    }

    /// <summary>What a channel's opening says it carries messages through, in words (`contracts/sdk-v1.md` §2).</summary>
    internal static string KindText(long kind) => kind switch
    {
        0 => "another kind",
        1 => "a pipe",
        2 => "a socket",
        3 => "shared memory",
        _ => string.Create(CultureInfo.InvariantCulture, $"kind {kind}"),
    };

    private static void Render(MessagesDocument document, Guid? channel)
    {
        ConsoleUi.Heading("Cooperative messages");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(document.Generation));
        ConsoleUi.Field("Rules", $"{document.MessageRule} over {document.BindingRule}, evidence policy "
            + EvidencePolicyOption.Typed(document.EvidencePolicy));
        int messages = document.Channels.Sum(held => held.Messages);
        ConsoleUi.Field("Messages", string.Create(CultureInfo.CurrentCulture,
            $"{messages:N0} in {CountText.Of(document.Channels.Count, "channel epoch")}")
            + (document.WithoutMessage == 0
                ? string.Empty
                : string.Create(CultureInfo.CurrentCulture, $"; {CountText.Of(document.WithoutMessage, "record")} named none, a value it is keyed by not kept")));

        // Whether a message listed nowhere could have been seen at all is the ledger's to say, not the records' absence (R21).
        ConsoleUi.Note(SessionCoverage.Sentence(document.Coverage[0], "the session"));
        ConsoleUi.Line();
        if (document.Channels.Count == 0)
        {
            ConsoleUi.Note(channel is null
                ? "This session holds no channel or message of the SDK's."
                : "No channel of this identity holds a message or an opening.");
            return;
        }

        ConsoleUi.Table(
            ["Channel", "Epoch", "Name", "Kind", "Messages", "Consumed", "Open", "Unseen publication", "Conflicting", "Readers", "Bytes"],
            [.. document.Channels.Select(held => (IReadOnlyList<string>)
            [
                held.Channel.ToString(),
                ConsoleUi.Count(held.Epoch),
                held.Name ?? "no opening recorded",
                held.Kind ?? "no opening recorded",
                ConsoleUi.Count(held.Messages),
                ConsoleUi.Count(held.Consumed),
                ConsoleUi.Count(held.Unconsumed),
                ConsoleUi.Count(held.PublicationUnseen),
                ConsoleUi.Count(held.Conflicting),
                ConsoleUi.Count(held.Readers),
                ConsoleUi.Count(held.PublishedBytes),
            ])]);
        foreach (MessagesChannelDocument held in document.Channels.Where(held => held.List is { Count: > 0 }))
        {
            ConsoleUi.Line();
            ConsoleUi.Line(string.Create(CultureInfo.CurrentCulture,
                $"  {held.Name ?? held.Channel.ToString()}, epoch {held.Epoch:N0}: the first {held.List!.Count:N0} of {held.Messages:N0} messages"));
            ConsoleUi.Table(
                ["Sequence", "State", "Publisher", "Length", "Read by"],
                [.. held.List.Select(message => (IReadOnlyList<string>)
                [
                    ConsoleUi.Count(message.Sequence),
                    StateText(message.State),
                    message.Publication is { } publication ? ProcessName(publication) : "not recorded",
                    message.Publication?.Length is { } length ? ConsoleUi.Count(length) : "not stated",
                    message.Consumptions.Count == 0
                        ? "no reader"
                        : string.Join(", ", message.Consumptions.Select(consumption => string.Create(CultureInfo.CurrentCulture,
                            $"reader {consumption.Consumer:N0} in {ProcessName(consumption.Record)}"))),
                ])]);
        }
    }

    /// <summary>What became of a message, in words.</summary>
    internal static string StateText(string state) => Enum.Parse<CooperativeMessageState>(state) switch
    {
        CooperativeMessageState.Consumed => "consumed",
        CooperativeMessageState.Unconsumed => "open: no reader consumed it",
        CooperativeMessageState.PublicationUnseen => "consumed, its publication not recorded",
        CooperativeMessageState.Conflicting => "conflicting: published or read twice",
        _ => state,
    };

    private static string ProcessName(MessagesRecordDocument record) => record.Process is { } process
        ? string.Create(CultureInfo.CurrentCulture, $"{process.ImageName ?? "executable not witnessed"} · {process.ProcessId}")
        : string.Create(CultureInfo.CurrentCulture,
            $"PID {record.ProcessId} · {BindingText.Reason(Enum.Parse<ProcessBindingReason>(record.Unattributed!))}");

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat messages <directory> [--channel <id>] [--messages <n>] [--evidence-policy <name>] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  Lists what applications instrumented with InterCat's SDK said of their channels, captured with");
        ConsoleUi.Line("  --profile sdk (contracts/cooperative-messages-v1.md): each channel's epoch, by the name its");
        ConsoleUi.Line("  opening gives it, with its messages - each its channel's epoch and sequence, published once and");
        ConsoleUi.Line("  consumed once by each reader - counted as consumed, open, consumed with no publication recorded,");
        ConsoleUi.Line("  or conflicting. What an application says is its own word. What the capture covered of the SDK is");
        ConsoleUi.Line("  said from its coverage ledger, never from a count of none.");
        ConsoleUi.Line();
        ConsoleUi.Line("  --channel <id>         Only this channel's epochs, with their first messages.");
        ConsoleUi.Line("  --messages <n>         How many messages to list per channel epoch (default 20 with --channel).");
        ConsoleUi.Line("  --evidence-policy      Which bindings name a process: DirectOnly, IncludeCorrelated (default),");
        ConsoleUi.Line("                         IncludeCandidates or AllIncludingConflicting.");
    }
}
