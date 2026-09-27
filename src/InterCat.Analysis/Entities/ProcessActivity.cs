using System.Buffers;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// How many records each process instance was observed making, by mechanism: every record whose own owner binds to the
/// instance (`contracts/entities-v1.md` §4). A lifecycle record binds directly to the instance it creates, ends or
/// confirms, and any other record by its reading, as strongly as the instance's place among its PID's instances allows.
/// The ranked table orders processes by it: what each process did, where a relation counts only what it exchanged with
/// a local peer (revision 166).
/// </summary>
/// <remarks>
/// Its state is one entry per instance and mechanism, and one per PID: the earliest and latest reading of any record
/// naming the PID, and how many of them bind to no instance. A later generation extends it as the relations are
/// extended (IC-015): when every PID's readings bind alike under the new process index, the counts move to the
/// instances they now bind to, and only the added segments are read.
/// </remarks>
public sealed partial class ProcessActivityIndex
{
    /// <summary>The rule identity a result names when it ranks by these counts.</summary>
    public const string CountRule = "process-activity-v1";

    private readonly ProcessInstanceIndex processes;

    /// <summary>By position in <see cref="processes"/>: the records bound to the instance, per mechanism.</summary>
    private readonly Dictionary<int, InstanceActivity> instances;

    /// <summary>Every PID a record names: the span of those records' readings, and how many bind to no instance.</summary>
    private readonly Dictionary<int, PidSpan> pids;

    /// <summary>The published files counted; null when one had no published identity.</summary>
    private readonly HashSet<StoreDependency>? read;

    private ProcessActivityIndex(
        ProcessInstanceIndex processes,
        Dictionary<int, InstanceActivity> instances,
        Dictionary<int, PidSpan> pids,
        long withoutOwner,
        HashSet<StoreDependency>? read)
    {
        this.processes = processes;
        this.instances = instances;
        this.pids = pids;
        RecordsWithoutOwner = withoutOwner;
        this.read = read;
    }

    /// <summary>Records that name no owner under the binding rule: no instance can hold them (§4.1).</summary>
    public long RecordsWithoutOwner { get; private set; }

    /// <summary>Records naming a PID at a reading no instance of it held: before its first evidence, between, or after.</summary>
    public long RecordsNotBound => pids.Values.Sum(span => span.Unbound);

    /// <summary>The process index the positions here are positions in.</summary>
    internal ProcessInstanceIndex Processes => processes;

    /// <summary>
    /// The records of the instance at <paramref name="instance"/> that <paramref name="policy"/> admits: its lifecycle
    /// records always, the rest when the policy admits how strongly the instance binds them.
    /// </summary>
    public long RecordsOf(int instance, EvidencePolicy policy) =>
        MechanismsOf(instance, policy).Sum(entry => entry.Records);

    /// <summary>What <see cref="RecordsOf"/> counts, by mechanism, largest first and then by mechanism code.</summary>
    public IReadOnlyList<(Mechanism Mechanism, long Records)> MechanismsOf(int instance, EvidencePolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instance);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(instance, processes.Instances.Count);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (!instances.TryGetValue(instance, out InstanceActivity? activity))
        {
            return [];
        }

        bool boundAdmitted = new ProcessBinding(instance, StrengthOf(instance), ProcessBindingReason.Bound).IsAdmittedUnder(policy);
        var totals = new Dictionary<Mechanism, long>();
        foreach ((Mechanism mechanism, long records) in activity.Direct)
        {
            totals[mechanism] = totals.GetValueOrDefault(mechanism) + records;
        }

        if (boundAdmitted)
        {
            foreach ((Mechanism mechanism, long records) in activity.Bound)
            {
                totals[mechanism] = totals.GetValueOrDefault(mechanism) + records;
            }
        }

        return [.. totals.Where(entry => entry.Value > 0)
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key)
            .Select(entry => (entry.Key, entry.Value))];
    }

    /// <summary>Counts the records of <paramref name="segments"/>, the segments <paramref name="processes"/> was derived from.</summary>
    public static ProcessActivityIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(processes);
        var activity = new ProcessActivityIndex(processes, [], [], 0, PublishedAs(segments));
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity.Count(segment);
        }

        return activity;
    }

    /// <summary>
    /// The counts of <paramref name="segments"/>, extended from these rather than counted again, when the segments hold
    /// every one counted here and every PID's readings bind alike under <paramref name="processes"/>. The result is
    /// exactly <see cref="Derive"/>'s over the same segments. Null, for a full count, when a counted segment is missing
    /// or a reading already counted would bind otherwise.
    /// </summary>
    public ProcessActivityIndex? Extend(
        IReadOnlyList<SegmentReaderV1> segments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(processes);
        if (read is null || PublishedAs(segments) is not { } offered || !read.IsSubsetOf(offered))
        {
            return null;
        }

        // A record binds by its PID and reading, so where every PID's readings bind alike, every record counted here binds
        // to the instance the map gives, as strongly; a lifecycle record to the instance whose lifetime it bounds.
        var mapped = new Dictionary<int, int>();
        foreach ((int processId, PidSpan span) in pids)
        {
            if (span.First <= span.Last && !this.processes.BindsAlike(processes, processId, span.First, span.Last, mapped))
            {
                return null;
            }
        }

        var moved = new Dictionary<int, InstanceActivity>(instances.Count);
        foreach ((int position, InstanceActivity activity) in instances)
        {
            if (!mapped.TryGetValue(position, out int to) || moved.ContainsKey(to))
            {
                return null;
            }

            moved[to] = activity.Copy();
        }

        var extended = new ProcessActivityIndex(
            processes,
            moved,
            pids.ToDictionary(entry => entry.Key, entry => entry.Value.Copy()),
            RecordsWithoutOwner,
            [.. read]);
        foreach (SegmentReaderV1 segment in segments.Where(segment => !read.Contains(segment.Published!)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            extended.Count(segment);
            _ = extended.read!.Add(segment.Published!);
        }

        return extended;
    }

    /// <summary>How strongly the instance binds a record that is not one of its lifecycle records (identity-v1).</summary>
    private RelationStrength StrengthOf(int instance) =>
        processes.Instances[instance].LifecycleEpoch == 1 ? RelationStrength.Correlated : RelationStrength.Candidate;

    private void Count(SegmentReaderV1 segment)
    {
        ProcessBinding[] bindings = ArrayPool<ProcessBinding>.Shared.Rent(segment.RowCount);
        try
        {
            ProcessRoles.OwnersOf(processes, segment, bindings);
            var owners = new RecordOwnerColumns(segment);
            SegmentColumnSlice ticks = segment.Slice(SegmentColumnId.NativeTicks);
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            for (int row = 0; row < segment.RowCount; row++)
            {
                var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;
                if (owners.At(row, mechanism) is not { } processId)
                {
                    RecordsWithoutOwner++;
                    continue;
                }

                long reading = ticks.SignedAt(row)!.Value;
                if (!pids.TryGetValue(processId, out PidSpan? span))
                {
                    span = new();
                    pids[processId] = span;
                }

                span.First = Math.Min(span.First, reading);
                span.Last = Math.Max(span.Last, reading);
                ProcessBinding binding = bindings[row];
                if (!binding.IsBound)
                {
                    span.Unbound++;
                    continue;
                }

                if (!instances.TryGetValue(binding.Instance, out InstanceActivity? activity))
                {
                    activity = new();
                    instances[binding.Instance] = activity;
                }

                Dictionary<Mechanism, long> tally = binding.Strength == RelationStrength.Direct ? activity.Direct : activity.Bound;
                tally[mechanism] = tally.GetValueOrDefault(mechanism) + 1;
            }
        }
        finally
        {
            ArrayPool<ProcessBinding>.Shared.Return(bindings);
        }
    }

    private static HashSet<StoreDependency>? PublishedAs(IReadOnlyList<SegmentReaderV1> segments)
    {
        var published = new HashSet<StoreDependency>(segments.Count);
        foreach (SegmentReaderV1 segment in segments)
        {
            if (segment.Published is not { } file)
            {
                return null;
            }

            _ = published.Add(file);
        }

        return published;
    }

    /// <summary>One instance's records, lifecycle records apart from the rest, per mechanism.</summary>
    private sealed class InstanceActivity
    {
        public Dictionary<Mechanism, long> Direct { get; private init; } = [];

        public Dictionary<Mechanism, long> Bound { get; private init; } = [];

        public InstanceActivity Copy() => new() { Direct = new(Direct), Bound = new(Bound) };
    }

    /// <summary>The earliest and latest reading of any record naming one PID, and how many of them bind to nothing.</summary>
    private sealed class PidSpan
    {
        public long First { get; set; } = long.MaxValue;

        public long Last { get; set; } = long.MinValue;

        public long Unbound { get; set; }

        public PidSpan Copy() => new() { First = First, Last = Last, Unbound = Unbound };
    }
}
