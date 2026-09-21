using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

public sealed class OwnedCaptureSessionTests
{
    private static OwnedSessionPlan BuildPlan(int queueCapacity = 1_024)
    {
        CaptureSessionIdentity identity = CaptureSessionIdentity.Create("test", 4242);
        var request = new ProviderEnablementRequest
        {
            SourceId = "etw/manifest/Sample",
            ProviderName = "Sample",
            ProviderGuid = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88"),
            Level = 4,
            MatchAnyKeyword = 0x10,
            EventIdsToEnable = [10],
            RequestCaptureState = true,
        };

        var source = new SourceAdmissionPlan
        {
            SourceId = request.SourceId,
            ProviderGuid = request.ProviderGuid,
            SourceIndex = 0,
            Events =
            [
                new()
                {
                    SourceIndex = 0,
                    ProviderGuid = request.ProviderGuid,
                    EventId = 10,
                    Version = 0,
                    Name = "sent",
                    Mechanism = Mechanism.Tcp,
                    Kind = ObservationKind.Send,
                    Direction = Direction.Outbound,
                    MinimumBodyLength = 8,
                    Slots = [],
                    FieldReport = [],
                },
            ],
            Diagnostics = [],
        };

        return new()
        {
            Identity = identity,
            Providers = [request],
            Sources = [source],
            QueueCapacityRecords = queueCapacity,
        };
    }

    [Fact(DisplayName = "R8: a capture moves Idle to Recording to Closed and reports its own counters")]
    public async Task LifecycleReachesRecordingAndCloses()
    {
        var host = new FakeEtwSessionHost();
        host.Scripted.Add(new AdmittedEvent { SourceIndex = 0, EventId = 10, TimestampUtcTicks = 1_000 });
        OwnedSessionPlan plan = BuildPlan();
        await using var session = new OwnedCaptureSession(plan, host);

        Assert.Equal(CaptureLifecycle.Idle, session.State);
        CaptureStartResult start = await session.StartAsync(CancellationToken.None);

        Assert.True(start.Started);
        Assert.Equal(CaptureLifecycle.Recording, session.State);
        host.Last!.WaitUntilScriptedRecordsDelivered();

        CaptureStopResult stop = await session.StopAsync(CancellationToken.None);

        Assert.Equal(CaptureLifecycle.Closed, session.State);
        Assert.Equal(1, stop.Health.AdmittedRecords);
        Assert.Equal(1, stop.Health.ObservedRecords);
        Assert.Equal(1_000, stop.FirstRecordUtcTicks);
        Assert.Equal(plan.Identity.SessionName, Assert.Single(host.StoppedSessions));
    }

    [Fact(DisplayName = "P14: a capture never adopts or stops a session it did not create")]
    public async Task ExistingSessionIsNeverAdopted()
    {
        OwnedSessionPlan plan = BuildPlan();
        var host = new FakeEtwSessionHost("Some-Other-Tool-Session", plan.Identity.SessionName);
        await using var session = new OwnedCaptureSession(plan, host);

        CaptureStartResult start = await session.StartAsync(CancellationToken.None);

        Assert.False(start.Started);
        Assert.Contains("refuses to adopt", start.FailureReason, StringComparison.Ordinal);
        Assert.Empty(host.CreatedSessions);
        Assert.Empty(host.StoppedSessions);
    }

    [Fact(DisplayName = "P14: a failed start stops only the session that attempt created")]
    public async Task FailedStartCleansUpOnlyItsOwnSession()
    {
        OwnedSessionPlan plan = BuildPlan();
        var host = new FakeEtwSessionHost("Some-Other-Tool-Session") { FailProviderEnable = true };
        await using var session = new OwnedCaptureSession(plan, host);

        CaptureStartResult start = await session.StartAsync(CancellationToken.None);

        Assert.False(start.Started);
        Assert.Equal(plan.Identity.SessionName, Assert.Single(host.CreatedSessions));
        Assert.Equal(plan.Identity.SessionName, Assert.Single(host.StoppedSessions));
        Assert.DoesNotContain("Some-Other-Tool-Session", host.StoppedSessions);
        Assert.Equal(CaptureLifecycle.Closed, session.State);
        Assert.Contains(session.Degradations, reason => reason.Contains("access is denied", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R16: a capture attempt without elevation creates nothing and says why")]
    public async Task UnelevatedStartCreatesNothing()
    {
        var host = new FakeEtwSessionHost { IsElevated = false };
        await using var session = new OwnedCaptureSession(BuildPlan(), host);

        CaptureStartResult start = await session.StartAsync(CancellationToken.None);

        Assert.False(start.Started);
        Assert.Contains("elevation", start.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.CreatedSessions);
    }

    [Fact(DisplayName = "R8: a full bounded queue drops records into a counter instead of blocking acquisition")]
    public async Task QueueOverflowIsCountedNotBlocked()
    {
        var host = new FakeEtwSessionHost();
        for (int index = 0; index < 20; index++)
        {
            host.Scripted.Add(new AdmittedEvent { SourceIndex = 0, EventId = 10, TimestampUtcTicks = 1_000 + index });
        }

        await using var session = new OwnedCaptureSession(BuildPlan(queueCapacity: 4), host);

        _ = await session.StartAsync(CancellationToken.None);
        host.Last!.WaitUntilScriptedRecordsDelivered();
        CaptureStopResult stop = await session.StopAsync(CancellationToken.None);

        Assert.Equal(4, stop.Health.AdmittedRecords);
        Assert.Equal(16, stop.Health.ApplicationDrops);
        Assert.Equal(20, stop.Health.ObservedRecords);
        Assert.False(stop.Health.IsLossFree);
    }

    [Fact(DisplayName = "R21: a refused state rundown degrades the capture instead of hiding the gap")]
    public async Task RefusedRundownIsRecordedAsDegradation()
    {
        var host = new FakeEtwSessionHost { FailCaptureState = true };
        await using var session = new OwnedCaptureSession(BuildPlan(), host);

        CaptureStartResult start = await session.StartAsync(CancellationToken.None);

        Assert.True(start.Started);
        Assert.Equal(1, host.Last!.CaptureStateRequests);
        Assert.Contains(session.Degradations, reason => reason.Contains("rundown", StringComparison.Ordinal));
        _ = await session.StopAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "R8: source-reported loss is read into its own counter, never merged with drops")]
    public async Task SourceLossStaysSeparateFromApplicationDrops()
    {
        var host = new FakeEtwSessionHost();
        await using var session = new OwnedCaptureSession(BuildPlan(), host);
        _ = await session.StartAsync(CancellationToken.None);
        host.Last!.ProviderLoss = 7;
        host.Last!.ConsumerLoss = 3;

        CaptureHealthSnapshot health = session.ReadHealth();

        Assert.Equal(7, health.ProviderReportedEventLoss);
        Assert.Equal(3, health.ConsumerReportedBufferLoss);
        Assert.Equal(0, health.ApplicationDrops);
        _ = await session.StopAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "R8: a session name carries a unique suffix and an ownership token per attempt")]
    public void EachAttemptGetsItsOwnIdentity()
    {
        CaptureSessionIdentity first = CaptureSessionIdentity.Create("m0tcp", 100);
        CaptureSessionIdentity second = CaptureSessionIdentity.Create("m0tcp", 100);

        Assert.NotEqual(first.SessionName, second.SessionName);
        Assert.NotEqual(first.OwnershipToken, second.OwnershipToken);
        Assert.StartsWith("InterCat-m0tcp-100-", first.SessionName, StringComparison.Ordinal);
        Assert.True(first.Owns(first.SessionName));
        Assert.False(first.Owns(second.SessionName));
        Assert.False(first.Owns("InterCat-m0tcp-100-lookalike"));
    }
}
