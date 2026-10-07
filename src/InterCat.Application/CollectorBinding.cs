using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The process instances a capture's collectors are (`collector-binding-v1`, `contracts/collector-identities-v1.md` §3): the
/// ones whose PID is a collector's and whose lifecycle records carry its creation time, and the collectors no instance is.
/// </summary>
/// <param name="Instances">Each instance a collector is, with the part it played in collecting the capture.</param>
/// <param name="Unfound">The collectors no instance is: one whose creation time was not read, or no lifecycle record carried.</param>
/// <param name="Recorded">Whether the capture names its collectors at all; an import, a package or an older capture does not.</param>
public sealed record CollectorMatch(
    IReadOnlyList<CollectorInstance> Instances,
    IReadOnlyList<CollectorProcessV1> Unfound,
    bool Recorded)
{
    /// <summary>A capture that names no collector: no instance is one, and none is unfound.</summary>
    public static CollectorMatch NotRecorded { get; } = new([], [], false);

    /// <summary>The part <paramref name="instance"/> played in collecting the capture; null when it played none.</summary>
    public CollectorRole? RoleOf(ProcessInstanceId instance) =>
        Instances.FirstOrDefault(collector => collector.Instance == instance)?.Role;
}

/// <summary>One process instance a capture's collector is, and the part it played (`collector-binding-v1`).</summary>
public sealed record CollectorInstance(ProcessInstanceId Instance, CollectorRole Role);

/// <summary>
/// `collector-binding-v1`: a process instance is a collector when its PID is the collector's and the creation time its
/// lifecycle records carry is the one the collector named. A PID alone is reused by the machine and an image name proves
/// nothing, so neither is ever taken for a collector, and a collector whose creation time is unknown is no instance.
/// </summary>
public static class CollectorBinding
{
    public const string Rule = "collector-binding-v1";

    /// <summary>The instances of <paramref name="instances"/> that <paramref name="collectors"/> names, and those it names no instance for.</summary>
    public static CollectorMatch Match(IReadOnlyList<ProcessInstance> instances, CollectorIdentitiesV1? collectors)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (collectors is null)
        {
            return CollectorMatch.NotRecorded;
        }

        var matched = new List<CollectorInstance>();
        var unfound = new List<CollectorProcessV1>();
        foreach (CollectorProcessV1 collector in collectors.Processes)
        {
            long? created = collector.CreatedUtc?.ToFileTime();
            ProcessInstance? instance = created is null ? null : instances.FirstOrDefault(candidate =>
                candidate.ProcessId == collector.ProcessId && candidate.CreateFileTime == created);
            if (instance is null)
            {
                unfound.Add(collector);
            }
            else if (!matched.Exists(entry => entry.Instance == instance.Id))
            {
                // A process named in two roles keeps the first: the broker before its client.
                matched.Add(new(instance.Id, collector.Role));
            }
        }

        return new(matched, unfound, true);
    }
}

/// <summary>
/// How a person reads that a process collected the capture it is in (§19.5): one set of words for the window and the
/// command line (R5).
/// </summary>
public static class CollectorText
{
    /// <summary>The collector in a few words, as a row's caption ends: "InterCat's broker".</summary>
    public static string Label(CollectorRole role) => role switch
    {
        CollectorRole.Broker => "InterCat's broker",
        CollectorRole.Client => "InterCat, which asked to record",
        _ => "InterCat (icat record)",
    };

    /// <summary>The part a collector played, as a field of `icat session` and `icat overview` names it: "Broker".</summary>
    public static string RoleName(CollectorRole role) => role switch
    {
        CollectorRole.Broker => "Broker",
        CollectorRole.Client => "Its client",
        _ => "Recorder",
    };

    /// <summary>The collector in a sentence, as an explanation states it, with the rule that named it.</summary>
    public static string Sentence(CollectorRole role) => role switch
    {
        CollectorRole.Broker => "It is InterCat's broker, which recorded this capture",
        CollectorRole.Client => "It is the InterCat process that asked for this capture, the window or icat capture",
        _ => "It is icat record, which recorded this capture",
    } + $", as the capture's collectors name it by PID and creation time ({CollectorBinding.Rule}): its records are "
        + "InterCat's own activity, counted as any process's are.";

    /// <summary>
    /// Why no process instance is a collector the capture names (`collector-binding-v1`): its creation time was not read,
    /// so no instance can be shown to be it, or no lifecycle record of its PID carries the creation time it names. A PID
    /// alone is never taken for one.
    /// </summary>
    public static string UnfoundReason(CollectorProcessV1 collector)
    {
        ArgumentNullException.ThrowIfNull(collector);
        return collector.CreatedUtc is null
            ? "its creation time was not read, so no process can be shown to be it"
            : string.Create(CultureInfo.InvariantCulture,
                $"no lifecycle record of PID {collector.ProcessId} carries the creation time it names, so no process here is it");
    }

    /// <summary>
    /// A collector the capture names that no process instance is, as an explanation of a process holding its PID says it:
    /// "The capture names InterCat's broker as PID 4120, but its creation time was not read, so no process can be shown to
    /// be it, and none is labelled as it (collector-binding-v1)."
    /// </summary>
    public static string Unfound(CollectorProcessV1 collector)
    {
        ArgumentNullException.ThrowIfNull(collector);
        string named = collector.Role switch
        {
            CollectorRole.Broker => "InterCat's broker",
            CollectorRole.Client => "the InterCat process that asked to record",
            _ => "icat record",
        };
        return string.Create(CultureInfo.InvariantCulture, $"The capture names {named} as PID {collector.ProcessId}, but ")
            + UnfoundReason(collector) + $", and none is labelled as it ({CollectorBinding.Rule}).";
    }

    /// <summary>
    /// A collector as a list of them names it: "intercat-broker.exe · PID 4120 (InterCat's broker)".
    /// </summary>
    public static string Named(ProcessNode process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return process.Collector is { } role ? $"{process.NameWithPid} ({Label(role)})" : process.NameWithPid;
    }

    /// <summary>
    /// What an export of a view that set InterCat's own processes aside says beside its rows (§19.5's view filter): how
    /// many, with how many records over the whole session, and that none of their records was removed. Counts alone, in
    /// invariant digits as an export writes them, so it names no process.
    /// </summary>
    public static string SetAsideCaveat(int processes, long records) =>
        "InterCat's own processes are set aside from these rows: "
        + string.Create(CultureInfo.InvariantCulture, $"{processes:N0} {CountText.Agree(processes, "process", "processes")}, ")
        + string.Create(CultureInfo.InvariantCulture, $"with {records:N0} {CountText.Agree(records, "record", "records")} ")
        + "over the whole session. No record is removed: the evidence still lists theirs.";
}
