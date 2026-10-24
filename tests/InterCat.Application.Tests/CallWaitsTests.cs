using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Bottleneck views (`contracts/waits-v1.md`, plan M12): each caller's validated call spans by interface and by the process
/// that served them, the server's own time beside them over the linked calls alone, and every call no time is said for
/// counted by why.
/// </summary>
public sealed class CallWaitsTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact(DisplayName = "R3: a wait is a validated call's span, by caller, interface and server, the server's own time measured over linked calls alone")]
    public void AWaitIsAValidatedCallsSpan()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        WaitReading reading = CallWaits.Read(session.Store);
        ProcessInstanceId Of(int pid) => reading.Processes.Values.Single(node => node.ProcessId == pid && node.PidHolder == 1).Id;

        // app.exe's two calls services.exe served, one of which failed; the call no ALPC send carried, by itself; and
        // other.exe's call at the same moment, by itself too - the most time first.
        Assert.Equal(
            [
                (Of(LinkApp), (ProcessInstanceId?)Of(LinkServices), 2L, 1L, 6_000L),
                (Of(LinkOther), (ProcessInstanceId?)null, 1L, 0L, 1_800L),
                (Of(LinkApp), (ProcessInstanceId?)null, 1L, 0L, 1_000L),
            ],
            reading.Waits.Select(wait => (wait.Caller, wait.Server, wait.Calls, wait.Failed, wait.InCalls)));
        CallWait served = reading.Waits[0];
        Assert.Equal((ServiceControlInterface, 2L, 2_000L, 4_000L, 4_000L), (served.Interface!.Value, served.Durations.Count,
            served.Durations.Median, served.Durations.Percentile95, served.Durations.Maximum));
        Assert.Equal((2L, 6_000L, 3_000L), (served.Linked, served.LinkedInCalls, served.ServerRan));
        Assert.Empty(served.Unresolved);
        Assert.Equal([(RpcPeerState.NoSend, 1L)], reading.Waits[2].Unresolved.Select(entry => (entry.Key, entry.Value)));
        Assert.Equal((0L, 0L, 0L), (reading.Waits[2].Linked, reading.Waits[2].ServerRan, reading.Waits[2].ServerNotAdmitted));
        Assert.Empty(reading.NotValidated);
        Assert.Equal(0L, reading.NotAdmitted);
        Assert.Equal([Mechanism.Rpc, Mechanism.Alpc], reading.Coverage.Select(coverage => coverage.Mechanism));

        // The servers callers waited on, each over every caller.
        Assert.Equal([(Of(LinkServices), 1, 2L, 6_000L, 2L, 6_000L, 3_000L)], CallWaits.Servers(reading).Select(server =>
            (server.Server, server.Callers, server.Calls, server.InCalls, server.Linked, server.LinkedInCalls, server.ServerRan)));
    }

    [Fact(DisplayName = "R3: no time is said for a call whose span is not validated, or whose caller the evidence policy does not admit, and each is counted by why")]
    public void WhatNoTimeIsSaidForIsCounted()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = WaitRows();
        Publish(session.Store, rows, fields: fields);
        WaitReading reading = CallWaits.Read(session.Store);

        // A call open when the capture ended, one whose start was not recorded and one with no activity id have no span.
        Assert.Equal(
            [(RpcCallState.OpenAtCaptureEnd, 1L), (RpcCallState.StartNotObserved, 1L), (RpcCallState.NoActivityId, 1L)],
            reading.NotValidated.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value)));
        ProcessInstanceId Of(int pid) => reading.Processes.Values.Single(node => node.ProcessId == pid && node.PidHolder == 1).Id;
        Assert.Equal(7L, reading.Waits.Sum(wait => wait.Calls));

        // The most time first; as much time, the most calls first, then by caller, interface and server.
        Assert.Equal(
            [
                (Of(400), ServiceControlInterface, (ProcessInstanceId?)Of(1_960), 2L, 6_000L),
                (Of(400), WaitInterface, (ProcessInstanceId?)Of(1_960), 1L, 1_500L),
                (Of(WaitBrief), ServiceControlInterface, (ProcessInstanceId?)null, 2L, 1_000L),
                (Of(400), ServiceControlInterface, (ProcessInstanceId?)null, 1L, 1_000L),
                (Of(400), WaitOtherInterface, (ProcessInstanceId?)Of(WaitOtherServer), 1L, 1_000L),
            ],
            reading.Waits.Select(wait => (wait.Caller, wait.Interface!.Value, wait.Server, wait.Calls, wait.InCalls)));

        // A server is counted once per caller, however many interfaces it calls there; the most time first.
        Assert.Equal([(Of(1_960), 1, 3L, 7_500L, 3_300L), (Of(WaitOtherServer), 1, 1L, 1_000L, 200L)],
            CallWaits.Servers(reading).Select(server => (server.Server, server.Callers, server.Calls, server.InCalls, server.ServerRan)));

        // A reused PID's later holder's call binds to it as a candidate: no time is said for it unless candidates count.
        Assert.Equal(1L, reading.NotAdmitted);
        WaitReading candidates = CallWaits.Read(session.Store, EvidencePolicy.IncludeCandidates);
        Assert.Equal(0L, candidates.NotAdmitted);
        Assert.Contains(candidates.Waits, wait => candidates.Processes[wait.Caller].ProcessId == WaitReused && wait.Calls == 1
            && wait.InCalls == 1_000);
    }

    [Fact(DisplayName = "R3: a call served by a process the evidence policy does not admit names no server, and its server's time is said only where admitted")]
    public void AServerNotAdmittedIsNotNamed()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] calls, SourceFieldRowV1[] fields) = LinkedRpcCalls();

        // services.exe exited and its PID was taken by another before the calls it served: its calls bind as candidates.
        Publish(session.Store,
        [
            .. calls,
            Lifecycle(3, ObservationKind.Exit, 1_960, 90),
            Lifecycle(4, ObservationKind.Create, 1_960, 91) with { ResourceName = @"C:\Windows\System32\services.exe" },
        ], fields: fields);
        WaitReading reading = CallWaits.Read(session.Store);
        CallWait none = Assert.Single(reading.Waits);
        Assert.Equal((null, 3L, 2L), (none.Server, none.Calls, none.ServerNotAdmitted));
        Assert.Equal([(RpcPeerState.NoSend, 1L)], none.Unresolved.Select(entry => (entry.Key, entry.Value)));
        Assert.Equal("not linked: 2 served by a process the evidence policy does not admit; 1 no ALPC send on its thread during "
            + "the call", WaitText.Served(reading, none));
        Assert.Empty(CallWaits.Servers(reading));

        // Counting candidates, the server is named, and its time is said.
        WaitReading candidates = CallWaits.Read(session.Store, EvidencePolicy.IncludeCandidates);
        CallWait served = candidates.Waits.Single(wait => wait.Server is not null);
        Assert.Equal((2L, 3_000L), (served.Calls, served.ServerRan));
        Assert.Equal("services.exe · PID 1960 #2", WaitText.Served(candidates, served));
    }

    [Fact(DisplayName = "R18: a wait's server, its share and what no time is said for are said in one set of words, the command line's and the window's")]
    public void WaitsAreSaidInOneSetOfWords()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = WaitRows();
        Publish(session.Store, rows, fields: fields);
        WaitReading reading = CallWaits.Read(session.Store);
        CallWait served = reading.Waits[0];
        CallWait unlinked = reading.Waits.Single(wait => wait.Server is null && reading.Processes[wait.Caller].ProcessId == 400);

        // The server, or why none is named: one reason said alone.
        Assert.Equal("PID 1960", WaitText.Served(reading, served));
        Assert.Equal("not linked: no ALPC send on its thread during the call", WaitText.Served(reading, unlinked));
        Assert.Equal("a process the overview did not name", WaitText.Named(reading, ProcessInstanceId.New()));

        // The server's time beside the caller's, over the linked calls alone, and how many of the calls that is.
        Assert.Equal("3.0 µs of 6.0 µs (50%)", WaitText.ServerRan(served.Linked, served.Calls, served.LinkedInCalls, served.ServerRan, Invariant));
        Assert.Equal("1.0 µs of 4.0 µs (25%), over 1 of the 3 calls", WaitText.ServerRan(1, 3, 4_000, 1_000, Invariant));
        Assert.Equal("-", WaitText.ServerRan(0, 3, 0, 0, Invariant));
        Assert.Equal("0 ns of 0 ns (0%)", WaitText.ServerRan(1, 1, 0, 0, Invariant));

        // What no time is said for, in one sentence.
        Assert.Equal("No time is said for 3 calls whose span is not validated: 1 open at capture end, 1 start not observed and 1 no "
            + "activity id, or 1 call the include-correlated evidence policy does not admit the caller of.", WaitText.NotCounted(reading));
        Assert.Null(WaitText.NotCounted(reading with { NotValidated = new Dictionary<RpcCallState, long>(), NotAdmitted = 0 }));
        Assert.Equal("No time is said for 4 calls whose span is not validated: 3 start not observed and 1 open at capture end.",
            WaitText.NotCounted(reading with
            {
                NotValidated = new Dictionary<RpcCallState, long> { [RpcCallState.OpenAtCaptureEnd] = 1, [RpcCallState.StartNotObserved] = 3 },
                NotAdmitted = 0,
            }));
        Assert.Contains("none is said to have blocked or delayed anything", WaitText.Note, StringComparison.Ordinal);
    }
}
