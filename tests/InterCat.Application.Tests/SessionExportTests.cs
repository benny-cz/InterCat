using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class SessionExportTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private const string OtherClient = "127.0.0.1:50001";
    private const string OtherServer = "127.0.0.1:9090";
    private static readonly DateTimeOffset Exported = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "R18: a headless export descends by row key and names its rung, interval and scope")]
    public void AHeadlessExportDescendsByRowKey()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessNode client = overview.Nodes.Single(node => node.ProcessId == 100);

        SessionExportResult machine = SessionExport.Build(session.Store, new([], null, false, ExportFormat.Json), Exported);
        Assert.Equal(DetailLevel.Machine, machine.Context.Rung);
        Assert.Equal("Whole session", machine.Context.Scope);
        Assert.Equal(overview.Groups.Count, machine.Rows);
        using (JsonDocument json = JsonDocument.Parse(machine.Content))
        {
            Assert.Equal(WorkspaceExport.Contract, json.RootElement.GetProperty("contract").GetString());
            Assert.Equal("ranking", json.RootElement.GetProperty("kind").GetString());
        }

        SessionExportResult process = SessionExport.Build(session.Store,
            new([client.GroupKey, client.Id.ToString()], null, false, ExportFormat.Csv), Exported);
        Assert.Equal(DetailLevel.ProcessInstance, process.Context.Rung);

        // icat overview --json prints instance IDs dashed; either spelling names the same row.
        Assert.Equal(process.Content, SessionExport.Build(session.Store,
            new([client.GroupKey, client.Id.Value.ToString("D")], null, false, ExportFormat.Csv), Exported).Content);
        Assert.Equal(2, process.Context.Filters.Count);
        Assert.Contains(client.Name, process.Context.Breadcrumb, StringComparison.Ordinal);
        Assert.Equal(process.Rows + 1, process.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // Ranked within an interval, the counts answer only that interval and the scope says so.
        SessionExportResult within = SessionExport.Build(session.Store,
            new([client.GroupKey], new TimeRange(10, 13), false, ExportFormat.Json), Exported);
        Assert.StartsWith("Ranked within", within.Context.Scope, StringComparison.Ordinal);
        Assert.Equal(new TimeRange(10, 13), within.Context.Interval);

        ArgumentException missing = Assert.Throws<ArgumentException>(() =>
            SessionExport.Build(session.Store, new(["executable:nowhere"], null, false, ExportFormat.Json), Exported));
        Assert.Contains("is not a row of the Machine rung", missing.Message, StringComparison.Ordinal);
        Assert.Contains(client.GroupKey, missing.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: an evidence export reads its whole scope in one pass, and one that stops at its limit says so")]
    public void AnEvidenceExportPagesItsWholeScope()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows, rowsPerSegment: 37);

        // One pass under one lease reads exactly the pages' records, in the same order, across every segment.
        var paged = new List<SessionEvidenceRecord>();
        string? cursor = null;
        do
        {
            SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, pageSize: 7, cursor: cursor);
            paged.AddRange(page.Records);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        SessionEvidencePage scope = SessionEvidenceQuery.ReadScope(session.Store, SessionExport.MaximumEvidenceLimit);
        Assert.Null(scope.NextCursor);
        Assert.Equal(paged.Select(record => (record.SegmentName, record.SegmentRow)),
            scope.Records.Select(record => (record.SegmentName, record.SegmentRow)));
        Assert.NotNull(SessionEvidenceQuery.ReadScope(session.Store, rows.Length - 1).NextCursor);

        SessionExportResult all = SessionExport.Build(session.Store, new([], null, true, ExportFormat.Json), Exported);
        Assert.Equal(DetailLevel.Evidence, all.Context.Rung);
        Assert.Equal(rows.Length, all.Rows);
        Assert.True(all.Context.Complete);
        Assert.Contains("Every record of this scope is included.", all.Context.Caveats);
        using (JsonDocument json = JsonDocument.Parse(all.Content))
        {
            Assert.Equal(rows.Length, json.RootElement.GetProperty("records").GetArrayLength());
            Assert.True(json.RootElement.GetProperty("context").GetProperty("complete").GetBoolean());
        }

        SessionExportResult exact = SessionExport.Build(session.Store, new([], null, true, ExportFormat.Json, rows.Length), Exported);
        Assert.True(exact.Context.Complete);

        SessionExportResult partial = SessionExport.Build(session.Store, new([], null, true, ExportFormat.Csv, 5), Exported);
        Assert.Equal(5, partial.Rows);
        Assert.False(partial.Context.Complete);
        Assert.Contains(partial.Context.Caveats, caveat => caveat.Contains("--limit", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R21: an export states what the capture covered over its scope, mechanism by mechanism, as the inspector does")]
    public void AnExportStatesItsScopesCoverage()
    {
        using TemporarySession gapped = Gapped();
        const string Unknown = "Coverage unknown: outside the readings the capture's sources delivered, so a count of none here "
            + "is not proof of inactivity";

        // Over the whole session the capture covered what it collected: the export lists each mechanism's state with the fact
        // behind it, and its caveats end by saying so as the inspector does beneath its time scope.
        SessionExportResult whole = SessionExport.Build(gapped.Store, new([], null, false, ExportFormat.Json), Exported);
        Assert.Equal("Coverage: covered for TCP · no other mechanism collected", whole.Context.Caveats[^1]);
        Assert.Equal(Enum.GetValues<Mechanism>(), whole.Context.Coverage.Select(entry => entry.Mechanism));
        using (JsonDocument json = JsonDocument.Parse(whole.Content))
        {
            JsonElement[] coverage = [.. json.RootElement.GetProperty("context").GetProperty("coverage").EnumerateArray()];
            Assert.Equal(Enum.GetValues<Mechanism>().Length, coverage.Length);
            JsonElement tcp = coverage.Single(entry => entry.GetProperty("mechanism").GetString() == "Tcp");
            Assert.Equal(("Covered", "2 records from its 1 admitted descriptor, and nothing was reported lost"),
                (tcp.GetProperty("state").GetString(), tcp.GetProperty("reason").GetString()));
            JsonElement udp = coverage.Single(entry => entry.GetProperty("mechanism").GetString() == "Udp");
            Assert.Equal(("NotCollected", "no admitted descriptor records it"),
                (udp.GetProperty("state").GetString(), udp.GetProperty("reason").GetString()));
        }

        // Ranked within a range the capture delivered nothing in, every row counts none, and the export says that is no
        // proof of inactivity, for every mechanism.
        SessionExportResult gap = SessionExport.Build(gapped.Store, new([], new TimeRange(25, 35), false, ExportFormat.Json), Exported);
        Assert.NotEqual(0, gap.Rows);
        using (JsonDocument json = JsonDocument.Parse(gap.Content))
        {
            Assert.All(json.RootElement.GetProperty("rows").EnumerateArray(),
                row => Assert.Equal(0, row.GetProperty("observations").GetInt64()));
        }

        Assert.Equal(Unknown, gap.Context.Caveats[^1]);
        Assert.All(gap.Context.Coverage, entry => Assert.Equal(
            (CoverageState.UnknownCoverage, "outside the readings the capture's sources delivered"), (entry.State, entry.Reason)));

        // Its evidence holds every record of the range, which is none: complete, and still said to be no proof of inactivity.
        SessionExportResult evidence = SessionExport.Build(gapped.Store, new([], new TimeRange(25, 35), true, ExportFormat.Json), Exported);
        Assert.Equal((0, true), (evidence.Rows, evidence.Context.Complete));
        Assert.Equal(["Every record of this scope is included.", Unknown], evidence.Context.Caveats.Skip(1));
        Assert.Equal(gap.Context.Coverage, evidence.Context.Coverage);

        // A generation without a ledger has judged nothing, and its export says so.
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        Assert.Equal("Coverage unknown: this generation publishes no coverage ledger, so a count of none here is not proof of "
            + "inactivity", SessionExport.Build(session.Store, new([], null, false, ExportFormat.Json), Exported).Context.Caveats[^1]);
    }

    [Fact(DisplayName = "§6.4: a scope that cannot be read is refused with its reason, a page as a whole scope, never read as the whole session")]
    public void AnUnreadableScopeIsRefused()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows);

        // It names no channel, owner or key, as the whole session's scope does, and says why it cannot be read.
        var unreadable = new EvidenceScope("No readable scope", null, [], null, null,
            "None of the selected processes is in this generation.");
        Assert.Equal(unreadable.Problem,
            Assert.Throws<InvalidOperationException>(() => SessionEvidenceQuery.Read(session.Store, unreadable, null)).Message);
        Assert.Equal(unreadable.Problem,
            Assert.Throws<InvalidOperationException>(() => SessionEvidenceQuery.ReadScope(session.Store, unreadable, 1_000)).Message);
        Assert.Equal(rows.Length, SessionEvidenceQuery.ReadScope(session.Store, unreadable with { Problem = null }, 1_000).Records.Count);
    }

    /// <summary>Two paired connections between two process pairs, 400 records, some beyond one page.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        .. Enumerable.Range(0, 100).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)(1 + (4 * index)))
                .Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, (ulong)(2 + (4 * index)))
                .Between(ServerEnd, ClientEnd)),
            Timed(Transfer(510 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 8, 300, (ulong)(3 + (4 * index)))
                .Between(OtherClient, OtherServer)),
            Timed(Transfer(511 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 400, (ulong)(4 + (4 * index)))
                .Between(OtherServer, OtherClient)),
        }),
    ];

    /// <summary>A capture that delivered readings from 0 to 20 and from 40 to 60, and none between, and lost nothing.</summary>
    private static TemporarySession Gapped()
    {
        var gapped = new TemporarySession();
        Publish(gapped.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between(ClientEnd, ServerEnd)
                with { SessionRelativeTicks = 1_000 },
            Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2).Between(ClientEnd, ServerEnd)
                with { SessionRelativeTicks = 5_000 },
        ], coverage: new CoverageLedgerV1
        {
            Contract = CoverageLedgerV1.ContractName,
            Epochs = [TcpEpoch(1, 0, 20), TcpEpoch(2, 40, 60)],
        });
        return gapped;
    }

    /// <summary>A live epoch between two delivered readings that collected TCP, delivered two records and lost nothing.</summary>
    private static CoverageEpochV1 TcpEpoch(int number, long first, long last) => new()
    {
        Epoch = number,
        Acquisition = CoverageAcquisition.LiveCapture,
        FirstDeliveredNativeTicks = first,
        LastDeliveredNativeTicks = last,
        Collected =
        [
            new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
        ],
        Deliveries = [new CoverageDeliveryV1 { ProviderId = NetworkProvider, EventId = 10, Version = 0, Delivered = 2, Admitted = 2, Omitted = 0 }],
        Losses =
        [
            new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
        ],
    };

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
