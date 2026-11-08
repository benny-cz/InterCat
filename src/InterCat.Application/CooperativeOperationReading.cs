using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A session's cooperative operations (`contracts/cooperative-operations-v1.md`, ADR-109) as `icat messages --operations`
/// and InterCat's window read them alike (R18): the derivation over one generation, the clock its times are in, the evidence
/// policy its processes are named under, and what the capture covered of the SDK, said from the ledger (R21).
/// </summary>
public sealed record CooperativeOperationReading(
    Guid SessionId,
    long Generation,
    EvidencePolicy Policy,
    SourceClockDescriptor Clock,
    IReadOnlyList<MechanismCoverage> Coverage,
    CooperativeOperationIndex Index)
{
    /// <summary>How many operations completed with a status other than 0: a failure in the application's own code.</summary>
    public long Failed => Index.Operations.LongCount(operation => operation.State == CooperativeOperationState.Completed
        && operation.Status is not (null or 0));

    /// <summary>How many operations are in <paramref name="state"/>.</summary>
    public long Count(CooperativeOperationState state) => Index.Operations.LongCount(operation => operation.State == state);
}

/// <summary>
/// Reads a session's cooperative operations, and says them in one set of words for the command line and InterCat's window:
/// an operation by its name, the process that started it and what became of it, under the operation it was started under.
/// What an application says through the SDK is its own assertion, which the words keep saying.
/// </summary>
public static class CooperativeOperationText
{
    /// <summary>The note a listing of operations carries: what an operation is, and whose word it is.</summary>
    public const string Note = "An operation is its identity, as the application made it: started once and completed once in the "
        + "process that holds it, under the operation its start names. What an application says through the SDK is its own word, "
        + "never an observation of the work beneath it.";

    /// <summary>
    /// Reads <paramref name="store"/>'s current generation: its instances, as every other reading of it takes them, and each
    /// operation record of the SDK keyed by its operation, under one lease; and what the capture covered of the SDK.
    /// </summary>
    public static CooperativeOperationReading Read(
        SessionStore store,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException(CooperativeMessageText.NoClock);
        var generation = new GenerationSegments(store, manifest, clock);
        ProcessInstanceIndex processes = generation.Processes(cancellationToken);
        CooperativeOperationIndex index = CooperativeOperationIndex.Derive(generation.All, generation.Fields, processes, cancellationToken);
        IReadOnlyList<MechanismCoverage> coverage = SessionCoverage.ForMechanisms(
            SessionSegments.CoverageLedger(store.Root, manifest), [Mechanism.ApplicationSdk]);
        return new(manifest.SessionId, manifest.Generation, policy, clock, coverage, index);
    }

    /// <summary>An operation's name as a listing says it: the name its start gives it, or why it has none.</summary>
    public static string Name(CooperativeOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.Name is { Length: > 0 } name ? name
            : operation.Start is null ? "an operation whose start was not recorded"
            : "an operation its start gave no name";
    }

    /// <summary>
    /// The process an operation is the application's in, as a listing says it: its first start's, or - its start unseen - its
    /// first completion's; its executable and PID where the evidence policy admits the binding, or its PID and why not.
    /// </summary>
    public static string Process(CooperativeOperationReading reading, CooperativeOperation operation)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(operation);
        CooperativeOperationRecord record = operation.Start ?? operation.Completion
            ?? throw new ArgumentException("An operation holds a start or a completion.", nameof(operation));
        if (record.Process.IsAdmittedUnder(reading.Policy))
        {
            ProcessInstance instance = reading.Index.Processes.Instances[record.Process.Instance];
            return string.Create(CultureInfo.CurrentCulture, $"{instance.ImageName ?? "executable not witnessed"} · {instance.ProcessId}");
        }

        ProcessBindingReason reason = record.Process.IsBound ? ProcessBindingReason.NotAdmittedByPolicy : record.Process.Reason;
        return string.Create(CultureInfo.CurrentCulture, $"PID {record.ProcessId} · {BindingText.Reason(reason)}");
    }

    /// <summary>
    /// What became of an operation, in words: how it completed and how long it ran, that it is open, that its start went
    /// unseen, or how its records conflict.
    /// </summary>
    public static string Outcome(CooperativeOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CultureInfo culture = CultureInfo.CurrentCulture;
        string ran = operation.DurationNanoseconds is { } duration ? " in " + OperationText.Duration(duration, culture) : string.Empty;
        return operation.State switch
        {
            CooperativeOperationState.Completed => operation.Status switch
            {
                0 => "succeeded" + ran,
                { } status => string.Create(culture, $"failed with status {status}{ran}"),
                null => "completed, its status not kept" + ran,
            },
            CooperativeOperationState.Open => "open: no completion recorded",
            CooperativeOperationState.StartUnseen => operation.Status switch
            {
                0 => "succeeded, its start not recorded",
                { } status => string.Create(culture, $"failed with status {status}, its start not recorded"),
                null => "completed, its start and status not recorded",
            },
            _ => operation.Starts > 1 || operation.Completions > 1
                ? string.Create(culture, $"conflicting: started {operation.Starts:N0} and completed {operation.Completions:N0} times, none of its records chosen")
                : "conflicting: started and completed in two processes, none of its records chosen",
        };
    }

    /// <summary>One operation as a line of the tree says it: its name, its process and what became of it.</summary>
    public static string Line(CooperativeOperationReading reading, CooperativeOperation operation) =>
        $"{Name(operation)} · {Process(reading, operation)} · {Outcome(operation)}";

    /// <summary>The heading a parent the generation never recorded is listed under.</summary>
    public static string Unrecorded(Guid parent) =>
        $"Under operation {parent}, which this capture did not record: another capture's, or one begun before it";

    /// <summary>The heading operations no root reaches are listed under.</summary>
    public const string Circular = "Under parents naming each other round, which no start can: each identity is made before it is passed on";
}
