using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>One materialized observation-v1 row, with both its stable source identity and its location in this generation.</summary>
public sealed record SessionEvidenceRecord(
    ObservationId ObservationId,
    string SegmentName,
    int SegmentRow,
    ObservationRowV1 Observation);

/// <summary>
/// A bounded exact-row page. A cursor is tied to the manifest digest and query semantics; on a new generation the
/// caller gets an explicit restart, never a shifted page. These are admitted normalized rows, not raw payload bytes.
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
    string Caveat);

public static class SessionEvidenceQuery
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    private const string Caveat = "These are exact admitted observation-v1 rows with provider, descriptor, native "
        + "reading and raw-record locator. They are normalized facts, not a replay of original payload bytes. "
        + "A channel page includes only the admitted paired TCP incarnation; an owner-process page includes only "
        + "rows canonically bound to that process as owner, not possible peer rows. One-sided and ambiguous rows "
        + "remain available in unscoped evidence. This page does not infer completeness from observed rows; consult the "
        + "session's coverage ledger.";

    public static SessionEvidencePage Read(
        SessionStore store,
        string? channelKey = null,
        TimeRange? interval = null,
        ProcessInstanceId? ownerProcessScope = null,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        int pageSize = DefaultPageSize,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(channelKey) && channelKey is not null)
            throw new ArgumentException("A channel key cannot be empty.", nameof(channelKey));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (pageSize is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        string identity = Identity(manifest, channelKey, interval, ownerProcessScope, policy);
        (string suppliedIdentity, int startSegment, int startRow) = ParseCursor(cursor);
        if (cursor is not null && suppliedIdentity != identity)
        {
            return new(identity, manifest.SessionId, manifest.Generation, channelKey, ownerProcessScope, interval, [], null, true,
                "This evidence cursor names another generation or query. Restart from the first page; no rows "
                + "were silently shifted.", Caveat);
        }

        string[] names = [.. SessionSegments.Names(manifest)];
        SegmentReaderV1[] segments = [.. names.Select(name => SessionSegments.Open(store.Root, manifest, name))];
        if (cursor is not null && (startSegment >= segments.Length || startRow >= segments[startSegment].RowCount))
            throw new ArgumentException("The cursor points outside this generation's observation rows.", nameof(cursor));

        int? selectedChannel = null;
        int? selectedOwner = null;
        TransportRelationIndex? relations = null;
        ProcessInstanceIndex? processes = null;
        if (channelKey is not null || ownerProcessScope is not null)
        {
            SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
                ?? throw new InvalidDataException("This generation has no source clock for process binding.");
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest)
                .Select(name => SessionSegments.Open(store.Root, manifest, name))];
            processes = ProcessInstanceIndex.Derive(segments, clock, fields, cancellationToken);
            if (ownerProcessScope is { } ownerScope)
            {
                selectedOwner = processes.Instances
                    .Select((instance, index) => (instance, index))
                    .Where(item => item.instance.Id == ownerScope)
                    .Select(item => (int?)item.index).SingleOrDefault();
                if (selectedOwner is null)
                    throw new InvalidOperationException("The selected process instance is not in this generation. "
                        + "Return to the overview and select it again.");
            }
        }
        if (channelKey is not null)
        {
            relations = TransportRelationIndex.Derive(segments, processes!, cancellationToken);
            TransportRelation[] matching = [.. relations.Relations.Where(relation =>
                relation.Mechanism == Mechanism.Tcp && relation.StableKey == channelKey
                && SessionOverviewProjector.Admitted(relation.Strength, policy))];
            if (matching.Length != 1)
                throw new InvalidOperationException("This paired TCP channel is not uniquely admitted in the "
                    + "current generation and evidence policy. Return to the overview and select it again.");
            selectedChannel = matching[0].Channel;
        }

        var records = new List<SessionEvidenceRecord>(pageSize);
        for (int segmentIndex = startSegment; segmentIndex < segments.Length; segmentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = segments[segmentIndex];
            ChannelBinding[]? bindings = relations?.ChannelsOf(segment);
            ProcessBinding[]? owners = selectedOwner is not null ? processes!.OwnersOf(segment) : null;
            SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
            int first = segmentIndex == startSegment ? startRow : 0;
            for (int row = first; row < segment.RowCount; row++)
            {
                if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (selectedChannel is { } channel && bindings![row].Channel != channel) continue;
                if (selectedOwner is { } owner
                    && (owners![row].Instance != owner || !owners[row].IsAdmittedUnder(policy))) continue;
                if (interval is { } range
                    && (times.SignedAt(row) is not { } nanoseconds
                        || !range.Contains(nanoseconds / 100))) continue;
                if (records.Count == pageSize)
                {
                    return new(identity, manifest.SessionId, manifest.Generation, channelKey, ownerProcessScope, interval, records.AsReadOnly(),
                        Cursor(identity, segmentIndex, row), false, null, Caveat);
                }

                ObservationRowV1 observation = segment.Row(row);
                records.Add(new(observation.ObservationIdIn(segment.CaptureId, segment.Derivation),
                    names[segmentIndex], row, observation));
            }
        }

        return new(identity, manifest.SessionId, manifest.Generation, channelKey, ownerProcessScope, interval,
            records.AsReadOnly(), null, false, null, Caveat);
    }

    private static string Identity(SessionManifestV1 manifest, string? channelKey, TimeRange? interval,
        ProcessInstanceId? ownerProcessScope,
        EvidencePolicy policy)
    {
        string timeScope = interval is { } range
            ? string.Create(CultureInfo.InvariantCulture, $"{range.StartTicks}:{range.EndTicks}")
            : "all-time";
        string relationRule = channelKey is null ? "unscoped" : TransportRelationIndex.RelationRule;
        string bindingRule = ownerProcessScope is null ? "unscoped" : ProcessInstanceIndex.BindingRule;
        string canonical = string.Create(CultureInfo.InvariantCulture,
            $"evidence-page-v1|{manifest.SessionId:N}|{manifest.Generation}|{manifest.Digest}|{policy}|"
            + $"{relationRule}|{bindingRule}|{channelKey?.Length ?? 0}:{channelKey}|{ownerProcessScope}|{timeScope}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Cursor(string identity, int segment, int row) =>
        string.Create(CultureInfo.InvariantCulture, $"v1.{identity}.{segment}.{row}");

    private static (string Identity, int Segment, int Row) ParseCursor(string? cursor)
    {
        if (cursor is null) return (string.Empty, 0, 0);
        if (cursor.Length > 128) throw new ArgumentException("The evidence cursor is too long.", nameof(cursor));
        string[] parts = cursor.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || parts[1].Length != 64
            || !parts[1].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int segment)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int row))
            throw new ArgumentException("The evidence cursor is malformed; restart from the first page.", nameof(cursor));
        return (parts[1], segment, row);
    }
}
