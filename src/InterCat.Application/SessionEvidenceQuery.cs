using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The canonical owner of one evidence row under <c>process-binding-v2</c>, resolved when its page was read. An
/// unresolved row names its reason, and a candidate stays a candidate rather than being promoted (I12).
/// </summary>
public sealed record SessionEvidenceOwner(
    ProcessInstanceId? Instance,
    int? ProcessId,
    string? ImageName,
    RelationStrength Strength,
    ProcessBindingReason Reason,
    bool AdmittedUnderPolicy);

/// <summary>One materialized observation-v1 row, with both its stable source identity and its location in this generation.</summary>
public sealed record SessionEvidenceRecord(
    ObservationId ObservationId,
    string SegmentName,
    int SegmentRow,
    ObservationRowV1 Observation,
    SessionEvidenceOwner? Owner = null);

/// <summary>
/// A bounded exact-row page in the canonical row order of <c>segment-v1</c> §4. A cursor names the last row it
/// returned and the query's meaning, not a position in one generation, so a following page continues after that row
/// in whatever generation is current. A changed scope, policy, rule or derivation returns an explicit restart instead.
/// These are admitted normalized rows, not raw payload bytes.
/// </summary>
public sealed record SessionEvidencePage(
    string QueryIdentity,
    Guid SessionId,
    long Generation,
    string? ChannelKey,
    ProcessInstanceId? OwnerProcessScope,
    TimeRange? Interval,
    IReadOnlyList<SessionEvidenceRecord> Records,
    string? NextCursor,
    bool RestartRequired,
    string? RestartReason,
    string Caveat)
{
    /// <summary>Every canonical-owner instance the page is scoped to, sorted; empty when it is not owner-scoped.</summary>
    public IReadOnlyList<ProcessInstanceId> OwnerProcesses { get; init; } = [];

    /// <summary>
    /// The generation the cursor was issued in, when this page continued it in a newer one. Rows published later that
    /// sort before the cursor are not inserted into a list already under way; the first page includes them.
    /// </summary>
    public long? ContinuedFromGeneration { get; init; }
}

public static class SessionEvidenceQuery
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    /// <summary>A group scope names its members; more than a graph can hold is refused, not truncated.</summary>
    public const int MaximumOwnerProcesses = GraphLayout.MaximumNodes;

    private const string CursorVersion = "v2";
    private const int MaximumCursorLength = 256;

    private const string Caveat = "These are exact admitted observation-v1 rows with provider, descriptor, native "
        + "reading and raw-record locator, in native-reading order. They are normalized facts, not a replay of "
        + "original payload bytes. A channel page includes only the admitted paired TCP incarnation; an owner-process "
        + "page includes only rows canonically bound to those processes as owner, not possible peer rows. One-sided "
        + "and ambiguous rows remain available in unscoped evidence. This page does not infer completeness from "
        + "observed rows; consult the session's coverage ledger.";

    public static SessionEvidencePage Read(
        SessionStore store,
        string? channelKey = null,
        TimeRange? interval = null,
        ProcessInstanceId? ownerProcessScope = null,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        int pageSize = DefaultPageSize,
        string? cursor = null,
        IReadOnlyCollection<ProcessInstanceId>? ownerProcesses = null,
        bool resolveOwners = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (pageSize is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (ownerProcessScope is not null && ownerProcesses is not null)
            throw new ArgumentException("Name one owner process or a set of them, not both.", nameof(ownerProcesses));
        ProcessInstanceId[] owners = Owners(channelKey, policy,
            ownerProcesses ?? (ownerProcessScope is { } single ? [single] : null));
        return ReadCore(store, channelKey, interval, owners, policy, pageSize, ParseCursor(cursor), resolveOwners,
            cancellationToken);
    }

    /// <summary>
    /// Every record of one scope in canonical order, up to <paramref name="limit"/>, under a single lease: what an
    /// export needs, where paging would acquire a lease and reopen the segments once per page. The records are exactly
    /// the pages' records concatenated; the page's next cursor is set when the scope holds more than the limit.
    /// </summary>
    public static SessionEvidencePage ReadScope(
        SessionStore store,
        int limit,
        string? channelKey = null,
        TimeRange? interval = null,
        IReadOnlyCollection<ProcessInstanceId>? ownerProcesses = null,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        bool resolveOwners = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return ReadCore(store, channelKey, interval, Owners(channelKey, policy, ownerProcesses), policy, limit, null,
            resolveOwners, cancellationToken);
    }

    private static ProcessInstanceId[] Owners(
        string? channelKey, EvidencePolicy policy, IReadOnlyCollection<ProcessInstanceId>? ownerProcesses)
    {
        if (string.IsNullOrWhiteSpace(channelKey) && channelKey is not null)
            throw new ArgumentException("A channel key cannot be empty.", nameof(channelKey));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        ProcessInstanceId[] owners = ownerProcesses is not null
            ? [.. ownerProcesses.Distinct().OrderBy(id => id.Value.ToString("N"), StringComparer.Ordinal)]
            : [];
        if (ownerProcesses is not null && owners.Length == 0)
            throw new ArgumentException("An owner-process set needs at least one member.", nameof(ownerProcesses));
        if (owners.Length > MaximumOwnerProcesses)
            throw new ArgumentException(
                $"An owner-process set is limited to {MaximumOwnerProcesses:N0} members.", nameof(ownerProcesses));
        if (owners.Any(owner => owner.Value == Guid.Empty))
            throw new ArgumentException("An owner process needs a non-empty instance ID.", nameof(ownerProcesses));
        return owners;
    }

    private static SessionEvidencePage ReadCore(
        SessionStore store,
        string? channelKey,
        TimeRange? interval,
        ProcessInstanceId[] owners,
        EvidencePolicy policy,
        int maximum,
        EvidenceCursor? position,
        bool resolveOwners,
        CancellationToken cancellationToken)
    {
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        string[] names = [.. SessionSegments.Names(manifest)];
        SegmentReaderV1[] segments = [.. names.Select(name => SessionSegments.Open(store.Root, manifest, name))];
        string identity = Identity(manifest.SessionId, segments, channelKey, interval, owners, policy);
        SessionEvidencePage Page(IReadOnlyList<SessionEvidenceRecord> records, string? next, bool restart,
            string? reason, long? continuedFrom) =>
            new(identity, manifest.SessionId, manifest.Generation, channelKey,
                owners.Length == 1 ? owners[0] : null, interval, records, next, restart, reason, Caveat)
            {
                OwnerProcesses = Array.AsReadOnly(owners),
                ContinuedFromGeneration = continuedFrom,
            };

        if (position is { Legacy: true })
            return Page([], null, true, "This cursor was issued by an earlier InterCat version. Restart from the "
                + "first page; no rows were silently shifted.", null);
        if (position is not null && position.Identity != identity)
            return Page([], null, true, "This evidence cursor names another session, query, scope or derivation. "
                + "Restart from the first page; no rows were silently shifted.", null);

        SessionDerivation? derivation = null;
        SourceClockDescriptor clock = default;
        SegmentReaderV1[] fields = [];
        if (channelKey is not null || owners.Length > 0 || (resolveOwners && segments.Length > 0))
        {
            clock = SessionSegments.SourceClock(store.Root, manifest)
                ?? throw new InvalidDataException("This generation has no source clock for process binding.");
            fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            derivation = SessionDerivationCache.For(manifest);
        }

        ProcessInstanceIndex? processes = derivation?.Processes(segments, clock, fields, cancellationToken);
        HashSet<int> selectedOwners = [];
        foreach (ProcessInstanceId owner in owners)
        {
            int index = IndexOf(processes!, owner);
            if (index < 0)
                throw new InvalidOperationException(owners.Length == 1
                    ? "The selected process instance is not in this generation. Return to the overview and select it again."
                    : "A process instance of this group is not in this generation. Return to the overview and select "
                        + "the group again.");
            selectedOwners.Add(index);
        }

        TransportRelationIndex? relations = null;
        int? selectedChannel = null;
        if (channelKey is not null)
        {
            relations = derivation!.Relations(segments, clock, fields, cancellationToken);
            TransportRelation[] matching = [.. relations.Relations.Where(relation =>
                relation.Mechanism == Mechanism.Tcp && relation.StableKey == channelKey
                && SessionOverviewProjector.Admitted(relation.Strength, policy))];
            if (matching.Length != 1)
                throw new InvalidOperationException("This paired TCP channel is not uniquely admitted in the "
                    + "current generation and evidence policy. Return to the overview and select it again.");
            selectedChannel = matching[0].Channel;
        }

        var cursors = new SegmentCursor[segments.Length];
        var queue = new PriorityQueue<int, RowKey>(segments.Length, RowKeyComparer.Instance);
        for (int index = 0; index < segments.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentCursor = new SegmentCursor(segments[index], names[index]);
            int first = position is null ? 0 : segmentCursor.FirstAfter(position.Key);
            cursors[index] = segmentCursor;
            if (segmentCursor.Seek(first))
                queue.Enqueue(index, segmentCursor.Key);
        }

        var records = new List<SessionEvidenceRecord>(Math.Min(maximum, 4_096));
        bool needOwners = processes is not null && (selectedOwners.Count > 0 || resolveOwners);
        RowKey? lastReturned = null;
        RowKey? previous = null;
        bool more = false;
        long visited = 0;
        while (queue.TryDequeue(out int index, out RowKey key))
        {
            if ((++visited & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (previous is { } prior && RowKeyComparer.Instance.Compare(prior, key) == 0)
                throw new InvalidDataException("Two published rows share one canonical row identity. Refusing to "
                    + "page a generation that names a row twice.");
            previous = key;
            SegmentCursor segment = cursors[index];
            int row = segment.Row;
            if (segment.Seek(row + 1))
                queue.Enqueue(index, segment.Key);

            if (interval is { } range
                && (segment.Reader.SignedValue(SegmentColumnId.SessionRelativeTicks, row) is not { } nanoseconds
                    || !range.Contains(nanoseconds / 100))) continue;
            if (selectedChannel is { } channel && segment.ChannelOf(row, relations!) != channel) continue;
            ProcessBinding? binding = needOwners ? segment.OwnerOf(row, processes!) : null;
            if (selectedOwners.Count > 0
                && (!selectedOwners.Contains(binding!.Value.Instance) || !binding.Value.IsAdmittedUnder(policy))) continue;
            if (records.Count == maximum)
            {
                more = true;
                break;
            }

            ObservationRowV1 observation = segment.Reader.Row(row);
            records.Add(new(observation.ObservationIdIn(segment.Reader.CaptureId, segment.Reader.Derivation),
                segment.Name, row, observation, binding is { } owner ? Owner(owner, processes!, policy) : null));
            lastReturned = key;
        }

        long? continuedFrom = position is not null && position.Generation != manifest.Generation ? position.Generation : null;
        return Page(records.AsReadOnly(),
            more && lastReturned is { } last ? Cursor(identity, manifest.Generation, last) : null,
            false, null, continuedFrom);
    }

    private static int IndexOf(ProcessInstanceIndex processes, ProcessInstanceId id)
    {
        for (int index = 0; index < processes.Instances.Count; index++)
        {
            if (processes.Instances[index].Id == id) return index;
        }

        return -1;
    }

    private static SessionEvidenceOwner Owner(ProcessBinding binding, ProcessInstanceIndex processes, EvidencePolicy policy)
    {
        if (!binding.IsBound) return new(null, null, null, binding.Strength, binding.Reason, false);
        ProcessInstance instance = processes.Instances[binding.Instance];
        return new(instance.Id, instance.ProcessId, instance.ImageName, binding.Strength, binding.Reason,
            binding.IsAdmittedUnder(policy));
    }

    private static string Identity(Guid sessionId, IReadOnlyList<SegmentReaderV1> segments, string? channelKey,
        TimeRange? interval, ProcessInstanceId[] owners, EvidencePolicy policy)
    {
        string timeScope = interval is { } range
            ? string.Create(CultureInfo.InvariantCulture, $"{range.StartTicks}:{range.EndTicks}")
            : "all-time";
        string relationRule = channelKey is null ? "unscoped" : TransportRelationIndex.RelationRule;
        string bindingRule = owners.Length == 0 ? "unscoped" : ProcessInstanceIndex.BindingRule;
        string captures = string.Join(",", segments.Select(segment => segment.CaptureId.Value.ToString("N"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        string derivations = string.Join(",", segments.Select(segment => segment.Derivation.Value)
            .Distinct().Order());
        string ownerSet = string.Join(",", owners.Select(owner => owner.Value.ToString("N")));
        string canonical = string.Create(CultureInfo.InvariantCulture,
            $"evidence-page-v2|{sessionId:N}|{captures}|{derivations}|{policy}|{relationRule}|{bindingRule}|"
            + $"{channelKey?.Length ?? 0}:{channelKey}|{ownerSet}|{timeScope}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Cursor(string identity, long generation, RowKey key) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{CursorVersion}.{identity}.{generation}.{key.NativeTicks}.{key.Stream}.{key.Epoch}.{key.Ordinal}."
            + $"{key.FactHigh:x16}{key.FactLow:x16}");

    private static EvidenceCursor? ParseCursor(string? cursor)
    {
        if (cursor is null) return null;
        if (cursor.Length > MaximumCursorLength)
            throw new ArgumentException("The evidence cursor is too long.", nameof(cursor));
        if (cursor.StartsWith("v1.", StringComparison.Ordinal))
            return new(true, string.Empty, 0, default);
        string[] parts = cursor.Split('.');
        if (parts.Length != 8 || parts[0] != CursorVersion || !IsHex(parts[1], 64) || !IsHex(parts[7], 32)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long generation)
            || !long.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long ticks)
            || !uint.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out uint stream)
            || !uint.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out uint epoch)
            || !ulong.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out ulong ordinal))
            throw new ArgumentException("The evidence cursor is malformed; restart from the first page.", nameof(cursor));
        ulong high = ulong.Parse(parts[7].AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        ulong low = ulong.Parse(parts[7].AsSpan(16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return new(false, parts[1], generation, new(ticks, stream, epoch, ordinal, high, low));
    }

    private static bool IsHex(string text, int length) =>
        text.Length == length && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record EvidenceCursor(bool Legacy, string Identity, long Generation, RowKey Key);

    /// <summary>The <c>segment-v1</c> §4 row order: native reading, then the raw locator, then the fact key.</summary>
    private readonly record struct RowKey(long NativeTicks, uint Stream, uint Epoch, ulong Ordinal, ulong FactHigh, ulong FactLow);

    private sealed class RowKeyComparer : IComparer<RowKey>
    {
        public static RowKeyComparer Instance { get; } = new();

        public int Compare(RowKey left, RowKey right)
        {
            int order = left.NativeTicks.CompareTo(right.NativeTicks);
            order = order != 0 ? order : left.Stream.CompareTo(right.Stream);
            order = order != 0 ? order : left.Epoch.CompareTo(right.Epoch);
            order = order != 0 ? order : left.Ordinal.CompareTo(right.Ordinal);
            order = order != 0 ? order : left.FactHigh.CompareTo(right.FactHigh);
            return order != 0 ? order : left.FactLow.CompareTo(right.FactLow);
        }
    }

    /// <summary>
    /// One segment's position in the merge. A segment is sorted by the row key (the reader verified it), so its first
    /// row after a cursor is found by search, and bindings are computed only for a segment the merge reaches.
    /// </summary>
    private sealed class SegmentCursor(SegmentReaderV1 reader, string name)
    {
        private ChannelBinding[]? channels;
        private ProcessBinding[]? owners;

        public SegmentReaderV1 Reader { get; } = reader;

        public string Name { get; } = name;

        public int Row { get; private set; }

        public RowKey Key { get; private set; }

        public bool Seek(int row)
        {
            Row = row;
            if (row >= Reader.RowCount) return false;
            Key = KeyAt(row);
            return true;
        }

        public int FirstAfter(RowKey cursor)
        {
            int low = 0;
            int high = Reader.RowCount;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (RowKeyComparer.Instance.Compare(KeyAt(middle), cursor) <= 0) low = middle + 1;
                else high = middle;
            }

            return low;
        }

        public int ChannelOf(int row, TransportRelationIndex relations) =>
            (channels ??= relations.ChannelsOf(Reader))[row].Channel;

        public ProcessBinding OwnerOf(int row, ProcessInstanceIndex processes) =>
            (owners ??= processes.OwnersOf(Reader))[row];

        private RowKey KeyAt(int row) => new(
            Reader.SignedValue(SegmentColumnId.NativeTicks, row)!.Value,
            (uint)Reader.UnsignedValue(SegmentColumnId.RawStreamId, row)!.Value,
            (uint)Reader.UnsignedValue(SegmentColumnId.RawSourceEpoch, row)!.Value,
            Reader.UnsignedValue(SegmentColumnId.RawRecordOrdinal, row)!.Value,
            Reader.UnsignedValue(SegmentColumnId.FactKeyHigh, row)!.Value,
            Reader.UnsignedValue(SegmentColumnId.FactKeyLow, row)!.Value);
    }
}
