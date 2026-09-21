using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

public sealed class OwnedEtlFileCaptureTests
{
    [Fact(DisplayName = "P14: an ETL capture stops and disposes only its exact owned session")]
    public async Task OwnedSessionIsStoppedAndMeasured()
    {
        string output = NewOutputPath();
        var host = new FakeEtlFileSessionHost { EventsLost = 3, OutputBytesOnStop = 257 };
        EtlFileCapturePlan plan = BuildPlan(output);

        try
        {
            await using var capture = new OwnedEtlFileCapture(plan, host);
            EtlCaptureStartResult start = await capture.StartAsync();
            EtlCaptureStopResult stop = await capture.StopAsync();

            Assert.True(start.Started);
            Assert.Equal(CaptureLifecycle.Closed, stop.State);
            Assert.Equal(257, stop.FileLengthBytes);
            Assert.Equal(3, stop.EventsLost);
            Assert.Equal(plan.Session.Identity.SessionName, Assert.Single(host.StoppedSessions));
            Assert.Equal(plan.Session.Identity.SessionName, Assert.Single(host.DisposedSessions));
        }
        finally
        {
            DeleteArtifact(output);
        }
    }

    [Fact(DisplayName = "R16: unelevated ETL capture creates no session and explains the refusal")]
    public async Task UnelevatedCaptureCreatesNothing()
    {
        var host = new FakeEtlFileSessionHost { IsElevated = false };
        await using var capture = new OwnedEtlFileCapture(BuildPlan(NewOutputPath()), host);

        EtlCaptureStartResult start = await capture.StartAsync();

        Assert.False(start.Started);
        Assert.Contains("elevation", start.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.CreatedSessions);
    }

    [Fact(DisplayName = "IC-009: ETL evidence is never overwritten")]
    public async Task ExistingEvidencePathIsRefusedBeforeSessionCreation()
    {
        string output = NewOutputPath();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "existing evidence");
        var host = new FakeEtlFileSessionHost();

        try
        {
            await using var capture = new OwnedEtlFileCapture(BuildPlan(output), host);
            EtlCaptureStartResult start = await capture.StartAsync();

            Assert.False(start.Started);
            Assert.Contains("overwrite", start.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(host.CreatedSessions);
            Assert.Equal("existing evidence", await File.ReadAllTextAsync(output));
        }
        finally
        {
            DeleteArtifact(output);
        }
    }

    [Fact(DisplayName = "P14: a same-name machine session is never adopted by ETL capture")]
    public async Task SameNameSessionIsRefused()
    {
        EtlFileCapturePlan plan = BuildPlan(NewOutputPath());
        var host = new FakeEtlFileSessionHost("Other-Tool", plan.Session.Identity.SessionName);
        await using var capture = new OwnedEtlFileCapture(plan, host);

        EtlCaptureStartResult start = await capture.StartAsync();

        Assert.False(start.Started);
        Assert.Contains("refuses to adopt", start.FailureReason, StringComparison.Ordinal);
        Assert.Empty(host.CreatedSessions);
        Assert.Empty(host.StoppedSessions);
    }

    [Theory(DisplayName = "P14: failed ETL provider setup cleans up the exact session it created")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedProviderSetupCleansUp(bool throws)
    {
        EtlFileCapturePlan plan = BuildPlan(NewOutputPath());
        var host = new FakeEtlFileSessionHost
        {
            FailProviderEnable = !throws,
            ThrowDuringProviderEnable = throws,
        };
        await using var capture = new OwnedEtlFileCapture(plan, host);

        EtlCaptureStartResult start = await capture.StartAsync();

        Assert.False(start.Started);
        Assert.Equal(plan.Session.Identity.SessionName, Assert.Single(host.StoppedSessions));
        Assert.Equal(plan.Session.Identity.SessionName, Assert.Single(host.DisposedSessions));
    }

    [Fact(DisplayName = "P14: a host returning a foreign handle is not stopped or disposed")]
    public async Task ForeignHandleIsNeverTouched()
    {
        var host = new FakeEtlFileSessionHost { ReturnedSessionName = "Foreign-Session", FailProviderEnable = true };
        await using var capture = new OwnedEtlFileCapture(BuildPlan(NewOutputPath()), host);

        EtlCaptureStartResult start = await capture.StartAsync();

        Assert.False(start.Started);
        Assert.Empty(host.StoppedSessions);
        Assert.Empty(host.DisposedSessions);
        Assert.Contains(capture.Degradations, reason => reason.Contains("Refused to stop or dispose", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "R8: invalid ETL bounds and ambiguous output paths are refused")]
    [InlineData("capture.bin", 64, 30)]
    [InlineData("capture.etl", 0, 30)]
    [InlineData("capture.etl", 64, 0)]
    public async Task InvalidPlanIsRefused(string fileName, int bufferMiB, int durationSeconds)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "etl-test-artifacts", fileName);
        EtlFileCapturePlan plan = BuildPlan(output) with
        {
            BufferSizeMegabytes = bufferMiB,
            Session = BuildPlan(output).Session with { MaximumDuration = TimeSpan.FromSeconds(durationSeconds) },
        };
        var host = new FakeEtlFileSessionHost();
        await using var capture = new OwnedEtlFileCapture(plan, host);

        EtlCaptureStartResult start = await capture.StartAsync();

        Assert.False(start.Started);
        Assert.Empty(host.CreatedSessions);
    }

    private static EtlFileCapturePlan BuildPlan(string outputPath)
    {
        CaptureSessionIdentity identity = CaptureSessionIdentity.Create("etltest", 4242);
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

        return new()
        {
            OutputPath = outputPath,
            Session = new()
            {
                Identity = identity,
                Providers = [request],
                Sources = [],
                MaximumDuration = TimeSpan.FromSeconds(30),
            },
        };
    }

    private static string NewOutputPath() => Path.Combine(
        AppContext.BaseDirectory,
        "etl-test-artifacts",
        $"{Guid.NewGuid():N}.etl");

    private static void DeleteArtifact(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
