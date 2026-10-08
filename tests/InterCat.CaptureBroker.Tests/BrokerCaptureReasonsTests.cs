using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

/// <summary>What a broker capture's start and status say of why it did not start or why it stopped.</summary>
public sealed class BrokerCaptureReasonsTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "R21: a content capture that reached the content its request allows says so, not that its length elapsed")]
    public void AContentLimitIsSaid()
    {
        // The limit is said, not what was kept below it when the next record would have passed it.
        LiveCaptureResult content = Result() with { ContentLimitReached = true, ContentKeptBytes = 15L * 1024 * 1024 };
        Assert.Equal("The content it kept reached the 16 MiB its request allows; the admitted prefix was finalized.",
            BrokerCaptureReasons.Stopped(content, false, 16L * 1024 * 1024, null, userStopRequested: false));

        // A journal that filled first is the limit said; one stopped by its owner says nothing, its length elapsed does.
        Assert.Equal("The configured journal byte limit was reached; the admitted prefix was finalized.",
            BrokerCaptureReasons.Stopped(content with { JournalQuotaReached = true }, false, 16L * 1024 * 1024, null, false));
        Assert.Null(BrokerCaptureReasons.Stopped(Result(), false, null, null, userStopRequested: true));
        Assert.Equal("The configured maximum capture duration elapsed; evidence was finalized.",
            BrokerCaptureReasons.Stopped(Result(), false, null, null, userStopRequested: false));
    }

    [Fact(DisplayName = "R21: a content capture whose content source was not enabled refuses its start, saying why, as no other capture does")]
    public void AContentSourceNotEnabledRefusesTheStart()
    {
        PreparedCapturePlan content = BrokerPrepareCompiler.Prepare(CompileContent(), Quota, BrokerRetentionPolicy.StopAtLimit, Runtime,
            contentProcesses: [new(4_242, Started)]).PreparedPlan!;
        CaptureStartResult refused = Start(new ProviderEnablementResult(WindowsSourceCatalog.WinInetCaptureSourceId, false,
            "process 4242 has already exited, so nothing it could record is the named process's"));
        Assert.Equal("The content source could not be enabled: process 4242 has already exited, so nothing it could record is the "
            + "named process's. The capture stopped before keeping any content.", BrokerCaptureReasons.ContentNotEnabled(content, refused));
        Assert.Equal("The content source could not be enabled: its provider was not enabled. The capture stopped before keeping any "
            + "content.",
            BrokerCaptureReasons.ContentNotEnabled(content, Start()));
        Assert.Null(BrokerCaptureReasons.ContentNotEnabled(content,
            Start(new ProviderEnablementResult(WindowsSourceCatalog.KernelProcessSourceId, false, "lifecycle was not enabled"),
                new ProviderEnablementResult(WindowsSourceCatalog.WinInetCaptureSourceId, true, null))));

        // Its lifecycle enabled is not its content source enabled.
        Assert.Equal("The content source could not be enabled: its provider was not enabled. The capture stopped before keeping any "
            + "content.", BrokerCaptureReasons.ContentNotEnabled(content,
                Start(new ProviderEnablementResult(WindowsSourceCatalog.KernelProcessSourceId, true, null))));

        // A capture that keeps no content starts whatever its sources' degradation, which its coverage then says.
        Assert.Null(BrokerCaptureReasons.ContentNotEnabled(PreparedFocused(), refused));
    }

    private static CaptureStartResult Start(params ProviderEnablementResult[] providers) =>
        new(true, CaptureLifecycle.Recording, [.. providers], null, [], 0);

    private static LiveCaptureResult Result() => new() { Start = Start() };
}
