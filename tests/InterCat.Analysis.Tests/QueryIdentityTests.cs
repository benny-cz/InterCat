using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// `query-identity-v1` (§10.5): the canonical form of a metric specification and the identity hashed from it. The
/// golden corpus is the contract - a UI and the CLI must produce these bytes (R18) - so a change to it is a deliberate
/// act, taken with a new canonicalization version or an ADR, never a test update.
/// </summary>
public sealed class QueryIdentityTests
{
    private static readonly ProcessInstanceId Focus = new(Guid.Parse("7f0c5b3d-914a-42e8-b0d6-1c2fa3845e19"));
    private static readonly ProcessInstanceId Counterpart = new(Guid.Parse("2b8e4f6a-1c3d-4e5f-9a7b-8c9d0e1f2a3b"));
    private static readonly SnapshotEntry[] Snapshot =
    [
        new(new CaptureId(Guid.Parse("9f1c4e0a-7b2d-4f11-a3c6-e58d90b7a412")), 84, "sha256:" + string.Concat(Enumerable.Repeat("0123456789abcdef", 4))),
    ];

    [Fact(DisplayName = "I16: every golden specification canonicalizes to exactly its recorded bytes and identity")]
    public void TheGoldenCorpusHolds()
    {
        string produced = string.Concat(Corpus().Select(entry =>
        {
            QueryIdentity identity = AnalysisSpecification.IdentityOf(entry.Request, Snapshot, NormalizerContractVersion.V1);
            return string.Create(CultureInfo.InvariantCulture, $"{entry.Name}\t{identity.Token}\t{identity.CanonicalSpecification}\n");
        }));
        string path = GoldenPath();
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, produced, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Equal(File.ReadAllText(path, Encoding.UTF8).ReplaceLineEndings("\n"), produced);

        // The hash is SHA-256 over the canonical bytes exactly, which a reader can check with any implementation.
        foreach (string line in produced.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\t');
            Assert.Equal(
                "v1:sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fields[2]))),
                fields[1]);
        }
    }

    [Fact(DisplayName = "R18: two spellings of one request, and a policy that changes nothing, are one identity")]
    public void SemanticallyIdenticalRequestsShareAnIdentity()
    {
        // A metric's fixed domain, side and implied layer are written out before hashing, so leaving them implicit
        // and writing them out are one request.
        Assert.Equal(
            Identity(Request(Metric.RequestedIoBytes, accountingSide: AccountingSide.SendSide)),
            Identity(Request(Metric.RequestedIoBytes, ByteDomain.RequestedIo, AccountingSide.SendSide)));
        Assert.Equal(
            Identity(Request(Metric.ApplicationPayloadBytes, accountingSide: AccountingSide.SendSide)),
            Identity(Request(Metric.ApplicationPayloadBytes, ByteDomain.ApplicationPayload, AccountingSide.SendSide) with
            {
                Layer = ObservationLayer.Application,
            }));

        // An evidence policy decides nothing when no record is bound to a process, so it is not part of the query.
        Assert.Equal(
            Identity(Request(Metric.Observations) with { Grouping = LaneGrouping.Mechanism, EvidencePolicy = EvidencePolicy.DirectOnly }),
            Identity(Request(Metric.Observations) with { Grouping = LaneGrouping.Mechanism }));
        Assert.NotEqual(
            Identity(Request(Metric.Observations) with { Grouping = LaneGrouping.InstanceOnly, EvidencePolicy = EvidencePolicy.DirectOnly }),
            Identity(Request(Metric.Observations) with { Grouping = LaneGrouping.InstanceOnly }));

        // Requested rows cut one aggregation: the hash is shared, and the identity keeps the cut beside it.
        QueryIdentity top5 = AnalysisSpecification.IdentityOf(Request(Metric.Observations) with { Grouping = LaneGrouping.InstanceOnly, RequestedRows = 5 }, Snapshot, NormalizerContractVersion.V1);
        QueryIdentity top20 = AnalysisSpecification.IdentityOf(Request(Metric.Observations) with { Grouping = LaneGrouping.InstanceOnly, RequestedRows = 20 }, Snapshot, NormalizerContractVersion.V1);
        Assert.Equal(top5.Hash, top20.Hash);
        Assert.NotEqual(top5, top20);
    }

    [Fact(DisplayName = "I16: any meaningful difference - member, snapshot, focus role or peer - is a different identity")]
    public void MeaningfulDifferencesAreDifferentIdentities()
    {
        MetricRequest sent = Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide);
        string[] identities =
        [
            Identity(sent),
            Identity(sent with { AccountingSide = AccountingSide.ReceiveSide }),
            Identity(sent with { Mechanism = Mechanism.Tcp }),
            Identity(sent with { Interval = new TimeRange(0, 10) }),
            Identity(sent with { Interval = new TimeRange(0, 11) }),
            Identity(sent with { Sender = Focus }),
            Identity(sent with { Participant = Focus }),
            Identity(sent with { Participant = Focus, Peer = Counterpart }),
            Identity(sent with { Participant = Counterpart, Peer = Focus }),
            Identity(sent, [Snapshot[0] with { Generation = 85 }]),
            Identity(sent, [Snapshot[0] with { ManifestDigest = "sha256:" + new string('f', 64) }]),
        ];
        Assert.Equal(identities.Length, identities.Distinct(StringComparer.Ordinal).Count());

        // The snapshot vector is sorted, so the order a caller lists captures in is not part of the query.
        SnapshotEntry other = new(new CaptureId(Guid.Parse("00000000-0000-4000-8000-000000000001")), 84, Snapshot[0].ManifestDigest);
        Assert.Equal(Identity(sent, [Snapshot[0], other]), Identity(sent, [other, Snapshot[0]]));
        Assert.Throws<ArgumentException>(() => Identity(sent with { Owner = Focus, AccountingSide = AccountingSide.ReceiveSide }));
    }

    [Fact(DisplayName = "I16: every answer names the identity of the specification and snapshot it answers")]
    public void AnswersNameTheirIdentity()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult published = Publish(session.Store,
        [
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 100, owner: 1_000),
            Transfer(200, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, owner: 2_000),
        ]);
        MetricRequest request = Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide);

        MetricResult answered = SessionMetrics.Evaluate(session.Store, request);
        QueryIdentity identified = SessionMetrics.Identify(session.Store, request)!;
        Assert.Equal(identified, answered.Identity);
        Assert.Contains(
            $"\"snapshotVector\":[{{\"captureId\":\"{Capture}\",\"generation\":{published.Manifest.Generation},\"manifest\":\"{published.Manifest.Digest}\"}}]",
            identified.CanonicalSpecification,
            StringComparison.Ordinal);

        // A result that is unavailable still answers one exact question over one exact snapshot.
        MetricResult unavailable = SessionMetrics.Evaluate(session.Store, request with { AccountingSide = AccountingSide.CanonicalOwner });
        Assert.Equal(MetricUnavailableReason.NoTransferAssociations, unavailable.Unavailable);
        Assert.NotNull(unavailable.Identity);
        Assert.NotEqual(answered.Identity!.Hash, unavailable.Identity!.Hash);

        // A later generation is another snapshot, so the same request is another query.
        Publish(session.Store, [Transfer(300, ObservationKind.Send, AccountingSide.SendSide, 7, owner: 1_000, ordinal: 3)]);
        Assert.NotEqual(identified.Hash, SessionMetrics.Identify(session.Store, request)!.Hash);
    }

    /// <summary>The golden corpus: one entry per rule of the canonical form, named for what it shows.</summary>
    private static IEnumerable<(string Name, MetricRequest Request)> Corpus() =>
    [
        ("observations-whole-capture", Request(Metric.Observations)),
        ("bytes-sent-in-an-interval", Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
        {
            Interval = new TimeRange(1_200_000_000, 1_800_000_000),
        }),
        ("requested-io-with-its-domain-implied", Request(Metric.RequestedIoBytes, accountingSide: AccountingSide.SendSide)),
        ("application-payload-with-its-layer-implied", Request(Metric.ApplicationPayloadBytes, accountingSide: AccountingSide.ReceiveSide)),
        ("endpoint-activity-with-its-side-implied", Request(Metric.EndpointActivityBytes, ByteDomain.TransportObserved)),
        ("rate-of-received-bytes", Request(Metric.Rate, ByteDomain.TransportObserved, AccountingSide.ReceiveSide) with
        {
            RateNumerator = Metric.BytesReceived,
            Interval = new TimeRange(0, 10_000_000),
        }),
        ("observations-by-mechanism", Request(Metric.Observations) with { Grouping = LaneGrouping.Mechanism, Layer = ObservationLayer.Transport }),
        ("sent-bytes-by-process-top-20", Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
        {
            Grouping = LaneGrouping.InstanceOnly,
            RequestedRows = 20,
        }),
        ("observations-by-executable-with-candidates", Request(Metric.Observations) with
        {
            Grouping = LaneGrouping.Executable,
            EvidencePolicy = EvidencePolicy.IncludeCandidates,
        }),
        ("owner-observations", Request(Metric.Observations) with { Owner = Focus }),
        ("participant-conversation-by-peer", Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
        {
            Participant = Focus,
            Grouping = LaneGrouping.Peer,
            Mechanism = Mechanism.Tcp,
        }),
        ("sender-to-one-peer-measured-at-receivers", Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.ReceiveSide) with
        {
            Sender = Focus,
            Peer = Counterpart,
        }),
    ];

    private static string Identity(MetricRequest request, SnapshotEntry[]? snapshot = null) =>
        AnalysisSpecification.IdentityOf(request, snapshot ?? Snapshot, NormalizerContractVersion.V1).Token;

    private static MetricRequest Request(
        Metric metric,
        ByteDomain? byteDomain = null,
        AccountingSide? accountingSide = null) => new()
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = metric,
            ByteDomain = byteDomain,
            AccountingSide = accountingSide,
        };

    private static string GoldenPath() =>
        Path.Combine(RepositoryRoot(), "fixtures", "FX-QUERY-001", "golden", "canonical-corpus.tsv");

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
