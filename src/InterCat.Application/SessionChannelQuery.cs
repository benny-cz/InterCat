using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>A complete count and a bounded page of admitted paired TCP incarnations in a leased generation.</summary>
public sealed record SessionChannelPage(
    string QueryIdentity,
    Guid SessionId,
    long Generation,
    ProcessInstanceId? ProcessScope,
    int TotalChannels,
    IReadOnlyList<Channel> Channels,
    string? NextCursor,
    bool RestartRequired,
    string? RestartReason,
    string Caveat);

public static class SessionChannelQuery
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    private const string Caveat = "Only paired TCP incarnations whose two process instances are admitted under "
        + "this evidence policy appear here. One-sided and ambiguous transport records remain in unscoped "
        + "evidence, not a guessed channel. Counts are observed records, not byte totals, logical operations "
        + "or proof of complete capture coverage.";

    public static SessionChannelPage Read(
        SessionStore store,
        ProcessInstanceId? processScope = null,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        int pageSize = DefaultPageSize,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (pageSize is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        string identity = Identity(manifest, processScope, policy);
        (string suppliedIdentity, int offset) = ParseCursor(cursor);
        if (cursor is not null && suppliedIdentity != identity)
        {
            return new(identity, manifest.SessionId, manifest.Generation, processScope, 0, [], null, true,
                "This channel cursor names another generation or scope. Restart from the first page; no "
                + "channel was silently shifted.", Caveat);
        }

        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest)
            .Select(name => SessionSegments.Open(store.Root, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest)
            .Select(name => SessionSegments.Open(store.Root, manifest, name))];
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation has no source clock for channel binding.");
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, clock, fields, cancellationToken);
        if (processScope is { } scope && !processes.Instances.Any(instance => instance.Id == scope))
            throw new InvalidOperationException("The selected process instance is not in this generation. "
                + "Return to the overview and select it again.");
        TransportRelationIndex relations = TransportRelationIndex.Derive(segments, processes, cancellationToken);
        TransportRelation[] admitted = [.. relations.Relations.Where(relation =>
                relation.Mechanism == Mechanism.Tcp
                && SessionOverviewProjector.Admitted(relation.Strength, policy)
                && (processScope is null || relation.First.Id == processScope || relation.Second.Id == processScope))
            .OrderBy(relation => relation.StableKey, StringComparer.Ordinal)];
        if (admitted.Select(relation => relation.StableKey).Distinct(StringComparer.Ordinal).Count() != admitted.Length)
            throw new InvalidDataException("Two admitted TCP incarnations have the same raw-fact channel key.");
        if (cursor is not null && offset >= admitted.Length)
            throw new ArgumentException("The cursor points outside this generation's channel set.", nameof(cursor));

        int count = Math.Min(pageSize, admitted.Length - offset);
        Channel[] channels = [.. admitted.Skip(offset).Take(count).Select(SessionOverviewProjector.ProjectChannel)];
        int next = offset + count;
        return new(identity, manifest.SessionId, manifest.Generation, processScope, admitted.Length, Array.AsReadOnly(channels),
            next < admitted.Length ? Cursor(identity, next) : null, false, null, Caveat);
    }

    private static string Identity(SessionManifestV1 manifest, ProcessInstanceId? scope, EvidencePolicy policy)
    {
        string canonical = string.Create(CultureInfo.InvariantCulture,
            $"channel-page-v1|{manifest.SessionId:N}|{manifest.Generation}|{manifest.Digest}|"
            + $"{TransportRelationIndex.RelationRule}|{policy}|{scope}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Cursor(string identity, int offset) =>
        string.Create(CultureInfo.InvariantCulture, $"v1.{identity}.{offset}");

    private static (string Identity, int Offset) ParseCursor(string? cursor)
    {
        if (cursor is null) return (string.Empty, 0);
        if (cursor.Length > 128) throw new ArgumentException("The channel cursor is too long.", nameof(cursor));
        string[] parts = cursor.Split('.');
        if (parts.Length != 3 || parts[0] != "v1" || parts[1].Length != 64
            || !parts[1].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
            throw new ArgumentException("The channel cursor is malformed; restart from the first page.", nameof(cursor));
        return (parts[1], offset);
    }
}
