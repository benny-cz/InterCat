using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One of a process's one-sided connections as its rung shows it (§7.1, `contracts/relations-v1.md` §6a): its two
/// endpoints, its records and the transport bytes they measured, within an interval when one is given. Nothing is said of
/// who is at the other end: no record of the capture holds it.
/// </summary>
public sealed record ConnectionSummary(
    string Key,
    Mechanism Mechanism,
    string LocalEndpoint,
    string RemoteEndpoint,
    long Records,
    long Sends,
    long SentBytes,
    long UnmeasuredSends,
    long Receives,
    long ReceivedBytes,
    long UnmeasuredReceives,
    bool OpenWitnessed,
    bool CloseWitnessed)
{
    /// <summary>The transfers of either direction whose record stated no size.</summary>
    public long Unmeasured => UnmeasuredSends + UnmeasuredReceives;

    /// <summary>
    /// Its transport bytes as a metric ranks them: what its sends or receives measured, of how many, and how many stated no
    /// size; null for any other metric, which a connection holds nothing of.
    /// </summary>
    public RankedValue? RankedBy(RankingMetric metric) => metric switch
    {
        RankingMetric.BytesSent => new(metric, Sends > UnmeasuredSends ? SentBytes : null, Sends - UnmeasuredSends, UnmeasuredSends),
        RankingMetric.BytesReceived => new(metric, Receives > UnmeasuredReceives ? ReceivedBytes : null, Receives - UnmeasuredReceives,
            UnmeasuredReceives),
        _ => null,
    };
    /// <summary>The connection in words: "TCP to 142.250.186.36:443".</summary>
    public string Name => (Mechanism == Mechanism.Udp ? "UDP to " : "TCP to ") + RemoteEndpoint;

    /// <summary>
    /// What its records measured, in one line, each direction in the words an interval's row says it in (R5, R21): the
    /// bytes its measured transfers carried, with how many stated no size; only how many stated none, where none measured
    /// one; or that it recorded none that way. None of them is a zero its records did not measure.
    /// </summary>
    public string Transfers(IFormatProvider? culture = null)
    {
        IFormatProvider format = culture ?? CultureInfo.CurrentCulture;
        return Direction(SentBytes, Sends, UnmeasuredSends, "sent", "send") + ", "
            + Direction(ReceivedBytes, Receives, UnmeasuredReceives, "received", "receive");

        string Direction(long bytes, long transfers, long unmeasured, string verb, string noun) =>
            transfers > unmeasured
                ? string.Create(format, $"{bytes:N0} B {verb}") + (unmeasured > 0 ? $" ({Unsized(unmeasured, noun)})" : string.Empty)
                : unmeasured > 0 ? Unsized(unmeasured, noun)
                : $"no {noun} recorded";

        string Unsized(long count, string noun) =>
            string.Create(format, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")} unmeasured");
    }

    /// <summary>Whether the capture saw it open and close, in words.</summary>
    public string Lifetime => Mechanism == Mechanism.Udp ? "a datagram flow" : LifetimeWords(OpenWitnessed, CloseWitnessed);

    /// <summary>
    /// A TCP connection's lifetime as the capture saw it, in words: whether a connect or an accept opened it in the
    /// capture, and a disconnect closed it there. A connection the capture did not see open was open before the capture
    /// began, and one it did not see close was still open when the capture ended.
    /// </summary>
    public static string LifetimeWords(bool openWitnessed, bool closeWitnessed) => (openWitnessed, closeWitnessed) switch
    {
        (true, true) => "opened and closed in the capture",
        (true, false) => "opened in the capture, still open at its end",
        (false, true) => "open before the capture, closed in it",
        _ => "open before the capture and after it",
    };
}

/// <summary>A process instance's one-sided connections in one generation, the most records first.</summary>
public sealed record ConnectionList(Guid SessionId, long Generation, IReadOnlyList<ConnectionSummary> Connections);

/// <summary>
/// A one-sided connection with the process that held it and its lifetime in session time: the span from its first record
/// to its last, which is where the capture saw it, not necessarily where it began or ended.
/// </summary>
public sealed record HeldConnection(
    ConnectionSummary Summary,
    ProcessInstance Holder,
    long FirstNanoseconds,
    long LastNanoseconds);

/// <summary>Every one-sided connection of one generation, whoever held it.</summary>
public sealed record SessionConnectionIndex(Guid SessionId, long Generation, IReadOnlyList<HeldConnection> Connections)
{
    /// <summary>The digest of the one generation's manifest the connections were read from (I16).</summary>
    public string? ManifestDigest { get; init; }

    /// <summary>
    /// What its capture covered of TCP and UDP, the mechanisms a connection is one of, over the generation the connections
    /// were read from (R21): a capture that lost or never collected a connection's records holds no mirror of it.
    /// </summary>
    public IReadOnlyList<MechanismCoverage> Coverage { get; init; } = [];
}

/// <summary>
/// Reads a process's one-sided connections for its rung: the TCP connections and UDP flows it held whose other end no
/// record of the capture holds, most often another host's. Each read leases the current generation and reads the
/// generation's relations once, shared by every read of it.
/// </summary>
public static class SessionConnections
{
    /// <summary>
    /// The one-sided connections of <paramref name="instance"/>, with the records each holds and the transport bytes they
    /// measured - within <paramref name="interval"/> (workspace ticks) when one is given, where a connection with no record
    /// in it is not listed.
    /// </summary>
    public static ConnectionList OneSided(
        SessionStore store,
        ProcessInstanceId instance,
        TimeRange? interval = null,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        (Guid session, long generation, _, _, _, IReadOnlyList<(TransportConnection Connection, ConnectionSummary Summary)> held) =
            Read(store, connection => connection.Holder.Id == instance, interval, policy, ledger: false, cancellationToken);
        return new(session, generation, [.. held.Select(pair => pair.Summary)]);
    }

    /// <summary>
    /// Every one-sided connection of the session, whoever held it, with its holder and its lifetime in session time - what
    /// an investigation compares with another capture's connections (`contracts/workspace-v15.md` §6).
    /// </summary>
    public static SessionConnectionIndex All(
        SessionStore store,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        (Guid session, long generation, string digest, SourceClockDescriptor? clock, CoverageLedgerV1? ledger,
            IReadOnlyList<(TransportConnection Connection, ConnectionSummary Summary)> held) =
            Read(store, _ => true, null, policy, ledger: true, cancellationToken);
        return new(session, generation,
        [
            .. held.Select(pair => new HeldConnection(
                pair.Summary,
                pair.Connection.Holder,
                SessionTime(clock!.Value, pair.Connection.FirstNativeTicks),
                SessionTime(clock.Value, pair.Connection.LastNativeTicks))),
        ])
        {
            ManifestDigest = digest,
            Coverage = SessionCoverage.ForMechanisms(ledger, [Mechanism.Tcp, Mechanism.Udp]),
        };
    }

    private static long SessionTime(SourceClockDescriptor clock, long nativeTicks) =>
        SourceClockMath.ConvertToSession(clock, new NativeTimestamp(clock.Id, clock.Encoding, nativeTicks)).SessionTime?.Nanoseconds
            ?? throw new InvalidDataException($"A connection's reading {nativeTicks} is not on its capture's clock.");

    private static (Guid Session, long Generation, string Digest, SourceClockDescriptor? Clock, CoverageLedgerV1? Ledger,
        IReadOnlyList<(TransportConnection Connection, ConnectionSummary Summary)> Held) Read(
        SessionStore store,
        Func<TransportConnection, bool> selected,
        TimeRange? interval,
        EvidencePolicy policy,
        bool ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;

        // Read under the same lease, the ledger is the generation's whose connections these are (I16).
        CoverageLedgerV1? coverage = ledger ? SessionSegments.CoverageLedger(store.Root, manifest) : null;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        if (segments.Length == 0 || SessionSegments.SourceClock(store.Root, manifest) is not { } clock)
        {
            return (manifest.SessionId, manifest.Generation, manifest.Digest, null, coverage, []);
        }

        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        TransportRelationIndex relations = SessionDerivationCache.For(manifest).Relations(store.Root, segments, clock, fields, cancellationToken);
        TransportConnection[] held = [.. relations.OneSided.Where(connection =>
            selected(connection) && SessionOverviewProjector.Admitted(connection.Strength, policy))];
        if (held.Length == 0)
        {
            return (manifest.SessionId, manifest.Generation, manifest.Digest, clock, coverage, []);
        }

        // One pass over the segments counts every connection's records and bytes at once, by the channel each row names.
        var byChannel = new Dictionary<int, int>(held.Length);
        for (int index = 0; index < held.Length; index++)
        {
            byChannel[held[index].Channel] = index;
        }

        long[] records = new long[held.Length], sends = new long[held.Length], sent = new long[held.Length],
            unsized = new long[held.Length], receives = new long[held.Length], received = new long[held.Length],
            unsizedReceives = new long[held.Length];
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackedChannels channels = SegmentBindings.ChannelsOf(segment, relations);
            SegmentColumnSlice kinds = default, bytes = default, times = default;
            bool opened = false;
            for (int row = 0; row < segment.RowCount; row++)
            {
                if (!channels[row].IsKnown || !byChannel.TryGetValue(channels[row].Channel, out int at))
                {
                    continue;
                }

                if (!opened)
                {
                    kinds = segment.Slice(SegmentColumnId.ObservationKind);
                    bytes = segment.Slice(SegmentColumnId.ByteValue);
                    times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
                    opened = true;
                }

                if (interval is { } range && (times.SignedAt(row) is not { } nanoseconds || !range.Contains(nanoseconds / 100)))
                {
                    continue;
                }

                records[at]++;
                var kind = (ObservationKind)kinds.UnsignedAt(row)!.Value;
                if (kind is not (ObservationKind.Send or ObservationKind.Receive))
                {
                    continue;
                }

                long? value = bytes.SignedAt(row);
                if (kind == ObservationKind.Send)
                {
                    sends[at]++;
                    if (value is { } size) sent[at] += size;
                    else unsized[at]++;
                }
                else
                {
                    receives[at]++;
                    if (value is { } size) received[at] += size;
                    else unsizedReceives[at]++;
                }
            }
        }

        return (manifest.SessionId, manifest.Generation, manifest.Digest, clock, coverage,
        [
            .. Enumerable.Range(0, held.Length)
                .Where(index => interval is null || records[index] > 0)
                .Select(index => (held[index], new ConnectionSummary(
                    held[index].StableKey,
                    held[index].Mechanism,
                    held[index].LocalEndpoint,
                    held[index].RemoteEndpoint,
                    records[index],
                    sends[index],
                    sent[index],
                    unsized[index],
                    receives[index],
                    received[index],
                    unsizedReceives[index],
                    held[index].OpenWitnessed,
                    held[index].CloseWitnessed)))
                .OrderByDescending(pair => pair.Item2.Records)
                .ThenBy(pair => pair.Item2.Key, StringComparer.Ordinal),
        ]);
    }
}
