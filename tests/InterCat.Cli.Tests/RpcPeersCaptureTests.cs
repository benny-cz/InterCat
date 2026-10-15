using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat capture records RPC peers through the broker (ADR-035): Explore's metadata and the kernel's ALPC messages beside it,
/// which link each RPC call to the process that served it, asked for as Explore is and refused for what it does not take.
/// </summary>
public sealed class RpcPeersCaptureTests
{
    [Fact(DisplayName = "R18: icat capture records RPC peers through the broker as it records Explore, live, and refuses a focus or content it does not take")]
    public void IcatCaptureRecordsRpcPeers()
    {
        BrokerPrepareCaptureRequest request = CaptureRequests.Parse("rpc-peers", null, false, "120", null, null, true,
            Options([]), out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(("rpc-peers", (Mechanism?)null, 0, false, (ContentCaptureRequest?)null),
            (request.ProfileId, request.FocusedMechanism, request.FocusedProcessIds.Count, request.AllowBroaderCapture,
                request.Content));
        Assert.Equal((BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live, 120),
            (request.Retention, request.Publication, request.Quota.MaximumDurationSeconds));

        // It names the catalog's profile by its canonical ID, which admits ALPC beside RPC calls, so the broker prepares it as
        // the catalog says.
        CaptureProfileDescriptor profile = CaptureProfileCatalog.Find(request.ProfileId)!;
        Assert.Equal((CaptureProfileKind.RpcPeers, request.ProfileId), (profile.Kind, profile.Id));
        Assert.Contains(profile.Sources, source => source.SourceId == WindowsSourceCatalog.KernelAlpcSourceId && source.Required);

        // A focus, wider capture or content is not its to take, and is refused before Windows is asked for approval.
        foreach ((string? mechanism, string[] words, bool broader, string refusal) in new (string?, string[], bool, string)[]
        {
            ("tcp", [], false, "focused-transport takes --mechanism tcp and optionally --pid <id,...> and --allow-broader; "
                + "explore and rpc-peers take neither."),
            (null, ["--pid", "4242"], false, "focused-transport takes --mechanism tcp"),
            (null, [], true, "focused-transport takes --mechanism tcp"),
            (null, ["--max-record-bytes", "1"], false, "--source, --channel, the byte limits, --inspection and --retention"),
        })
        {
            Assert.Null(CaptureRequests.Parse("rpc-peers", mechanism, broader, null, null, null, false, Options(words),
                out problem));
            Assert.StartsWith(refusal, problem, StringComparison.Ordinal);
        }
    }

    private static ContentRequestOptions Options(string[] words) => ContentRequestOptions.Take(new CommandLine(words));
}
