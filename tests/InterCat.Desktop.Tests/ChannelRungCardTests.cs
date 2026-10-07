using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// A channel's own rung (§6.4, I5): the evidence card counts the channel it opened - what the rung's level line counts and E
/// lists from there - in the words its row used where it was chosen, rather than the process it was opened from.
/// </summary>
public sealed class ChannelRungCardTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "§6.4: on a channel's own rung the card counts the channel E lists, as its row did where it was chosen, not the process it was opened from")]
    public void AChannelsOwnRungCountsTheChannel() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        PublishClient(session);
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        await OpenClient(workspace);

        foreach ((Func<RungRow, bool> isKind, string noun, string heading, int records) in Kinds)
        {
            // Chosen among the process's rows, the card counts its records; opened, its own rung's card counts them alike,
            // while the inspector's process is still the one it was opened from.
            workspace.SelectedRung = workspace.RungRows.Single(isKind);
            await workspace.SelectionBytesReady;
            string counted = workspace.EvidenceSummary;
            Assert.Equal("Selected " + noun, workspace.EvidenceHeading);
            Assert.True(workspace.Descend());
            await workspace.RpcReady;
            await workspace.HttpReady;
            await workspace.SelectionBytesReady;
            Assert.Equal((heading, counted), (workspace.EvidenceHeading, workspace.EvidenceSummary));
            Assert.Equal(client.Id, workspace.SelectedProcess?.Id);

            // E lists exactly those records, and the way back finds the card as it was once the rung has read them again.
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Assert.Equal(records, workspace.RungRows.Count);
            Assert.True(workspace.Ascend());
            await workspace.RpcReady;
            await workspace.HttpReady;
            await workspace.SelectionBytesReady;
            Assert.Equal((heading, counted), (workspace.EvidenceHeading, workspace.EvidenceSummary));
            Assert.True(workspace.Ascend());
            await workspace.RpcReady;
            await workspace.HttpReady;
        }

        // Each in the words its row used: a paired channel's records at its two ends, an RPC channel's call records, which
        // carry no size, and the HTTP exchanges' buffers with what their messages held.
        workspace.SelectedRung = workspace.RungRows.Single(Kinds[0].IsKind);
        Assert.StartsWith("3 observed records at its two ends · ", workspace.EvidenceSummary, StringComparison.Ordinal);
        workspace.SelectedRung = workspace.RungRows.Single(Kinds[1].IsKind);
        Assert.Equal("2 call records · an RPC call carries no size", workspace.EvidenceSummary);
        workspace.SelectedRung = workspace.RungRows.Single(Kinds[2].IsKind);
        Assert.Equal("3 buffer records · 181 B sent, 122 B received in HTTP messages", workspace.EvidenceSummary);
    });

    [Fact(DisplayName = "§6.4: on a channel's own rung a call or a process chosen leaves the card on the channel E lists, and a relationship or both ends chosen are what both read")]
    public void WhatIsChosenOnAChannelsRungIsWhatELists() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        PublishClient(session);
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode server = workspace.Snapshot.Processes.Single(node => node.ProcessId == 200);
        await OpenClient(workspace);

        // A call chosen on its RPC channel's rung: Enter would open the call, but E lists the channel's records, which the
        // card still counts.
        workspace.SelectedRung = workspace.RungRows.Single(Kinds[1].IsKind);
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        workspace.SelectedRung = Assert.Single(workspace.RungRows);
        Assert.Equal(("This RPC channel", "2 call records · an RPC call carries no size"),
            (workspace.EvidenceHeading, workspace.EvidenceSummary));
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(2, workspace.RungRows.Count);
        Assert.True(workspace.Ascend());
        Assert.True(workspace.Ascend());
        await workspace.RpcReady;

        // On the paired channel's rung, the server chosen in the graph leaves the card on the channel, whose records E lists.
        workspace.SelectedRung = workspace.RungRows.Single(Kinds[0].IsKind);
        await workspace.SelectionBytesReady;
        string counted = workspace.EvidenceSummary;
        Assert.True(workspace.Descend());
        workspace.SelectProcess(server.Id);
        await workspace.SelectionBytesReady;
        Assert.Equal(("This channel", counted), (workspace.EvidenceHeading, workspace.EvidenceSummary));
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(3, workspace.RungRows.Count);
        Assert.True(workspace.Ascend());

        // Its relationship chosen is what the card counts and E lists instead.
        workspace.SelectedRelationship = Assert.Single(workspace.Relationships);
        Assert.Equal("Selected relationship", workspace.EvidenceHeading);
        Assert.StartsWith("3 paired TCP observations · ", workspace.EvidenceSummary, StringComparison.Ordinal);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(3, workspace.RungRows.Count);
        Assert.True(workspace.Ascend());

        // Both its ends chosen together are what the card counts and E lists, as Enter would: their own records.
        workspace.SelectedRelationship = null;
        foreach (string node in workspace.GraphDisplay.Nodes.Where(node => node.Process is not null).Select(node => node.Key))
        {
            workspace.ToggleGraphNodeInSelection(node);
        }

        Assert.Equal("Selected processes", workspace.EvidenceHeading);
        Assert.StartsWith("10 own records, ", workspace.EvidenceSummary, StringComparison.Ordinal);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(10, workspace.RungRows.Count);
        Assert.True(workspace.Ascend());

        // Let go, the card returns to the channel; under a brush that holds one of its sends, it counts that send alone, as
        // E then lists it.
        workspace.ClearSelection();
        Assert.Equal(("This channel", counted), (workspace.EvidenceHeading, workspace.EvidenceSummary));
        workspace.SelectInterval(new TimeRange(45, 52));
        await workspace.IntervalReady;
        Assert.StartsWith("1 observed record at its two ends · ", workspace.EvidenceSummary, StringComparison.Ordinal);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Single(workspace.RungRows);
    });

    /// <summary>Each kind of channel a process's rung lists, how it is named chosen and opened, and the records E lists.</summary>
    [Fact(DisplayName = "§6.2: read on the wall clock, an HTTP exchange's row names the time of day it began")]
    public void AnExchangeRowReadsTheWallClock() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        DateTimeOffset noon = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        PublishClient(session, new ClockCalibrationV1
        {
            Contract = ClockCalibrationV1.ContractName,
            CaptureId = TestSessions.Capture.Value,
            ClockId = TestClock.Id.Value,
            WallClock = "test-wall-clock",
            Samples = [new() { NativeTicks = 0, Utc = noon, AcquisitionUncertaintyNanoseconds = 200 }],
        });
        using WorkspaceViewModel workspace = Open(session);
        await OpenClient(workspace);
        workspace.SelectedRung = workspace.RungRows.Single(row => HttpExchangeKeys.IsHttp(row.Key));
        Assert.True(workspace.Descend());
        await workspace.HttpReady;
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
        string exchangeAt = " · exchange 1 · ";
        Assert.StartsWith(SessionClock.Session(TimeZoneInfo.Local).Record(8_000, culture) + exchangeAt,
            workspace.RungRows[0].Detail, StringComparison.Ordinal);

        // Its first record was read 8 µs into the session, which the wall clock read 8 µs past noon.
        workspace.ReadsWallClock = true;
        SessionClock wall = SessionClock.Wall(workspace.Snapshot.WallClock!, TimeZoneInfo.Local, workspace.Snapshot.Extent);
        Assert.StartsWith(wall.Record(8_000, culture) + exchangeAt, workspace.RungRows[0].Detail, StringComparison.Ordinal);
        Assert.Equal("HTTP exchange at " + wall.Record(8_000, culture), workspace.RungRows[0].Source.Label);
    });

    private static readonly (Func<RungRow, bool> IsKind, string Noun, string Heading, int Records)[] Kinds =
    [
        (row => row.Label == "↔ server.exe · PID 200", "channel", "This channel", 3),
        (row => RpcChannelKeys.IsRpc(row.Key), "RPC channel", "This RPC channel", 2),
        (row => HttpExchangeKeys.IsHttp(row.Key), "HTTP exchanges", "These HTTP exchanges", 3),
    ];

    private static async Task OpenClient(WorkspaceViewModel workspace)
    {
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        workspace.SelectedRung = Assert.Single(workspace.RungRows);
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        await workspace.HttpReady;
    }

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>
    /// client.exe sends server.exe 8 bytes twice over one paired channel, calls the service control manager once over RPC,
    /// and makes one HTTP exchange.
    /// </summary>
    private static void PublishClient(TemporarySession session, ClockCalibrationV1? calibration = null)
    {
        Guid activity = new(1, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);
        ObservationRowV1[] http = [Http(80, 2001, 10, 181), Http(81, 2003, 11, 115), Http(82, 2004, 12, 7)];
        ObservationRowV1[] rows =
        [
            Timed(Lifecycle(5, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Lifecycle(10, ObservationKind.Create, 100, 2) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(55, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between(ClientEnd, ServerEnd)),
            Timed(RpcCall(70, ObservationKind.RequestStart, Direction.Outbound, 100, 8, activity, ServiceControlInterface)),
            Timed(RpcCall(72, ObservationKind.RequestEnd, Direction.Outbound, 100, 9, activity, status: 0)),
            .. http,
        ];
        Publish(session.Store, rows, coverage: RpcLedger(alpc: false), calibration: calibration, fields:
        [
            .. rows.Where(row => row is { Mechanism: Mechanism.Rpc, Kind: ObservationKind.RequestStart })
                .Select(start => Field(start, SourceField.RpcProcedureNumber, 7)),
            .. http.SelectMany(row => new[]
            {
                Field(row, SourceField.HttpExchangeId, 1),
                Field(row, SourceField.ContentBufferSequence, 0),
                Field(row, SourceField.ContentBufferFlags, 3),
            }),
        ]);
    }

    /// <summary>A WinINet record of <paramref name="eventId"/>, raised in PID 100, which binds it by that header (ADR-030).</summary>
    private static ObservationRowV1 Http(long ticks, ushort eventId, ulong ordinal, long bytes) =>
        Timed(Transfer(ticks, eventId <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            eventId <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, bytes, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = eventId,
            HeaderProcessId = 100,
            Direction = eventId <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
        });

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
