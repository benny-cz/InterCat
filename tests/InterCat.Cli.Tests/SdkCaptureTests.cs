using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat capture and icat record take the opt-in sdk profile (ADR-103): lifecycle and what applications instrumented with
/// InterCat's SDK say of themselves, asked for as Explore is and refused for a focus or content it does not take, its cost
/// stated as not yet measured.
/// </summary>
public sealed class SdkCaptureTests
{
    [Fact(DisplayName = "R18: icat capture records the cooperative SDK through the broker as it records Explore, live, and refuses a focus or content it does not take")]
    public void IcatCaptureRecordsTheSdk()
    {
        BrokerPrepareCaptureRequest request = CaptureRequests.Parse("sdk", null, false, "90", null, null, true,
            Options([]), out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(("sdk", (Mechanism?)null, 0, false, (ContentCaptureRequest?)null),
            (request.ProfileId, request.FocusedMechanism, request.FocusedProcessIds.Count, request.AllowBroaderCapture,
                request.Content));
        Assert.Equal((BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live, 90),
            (request.Retention, request.Publication, request.Quota.MaximumDurationSeconds));

        // It names the catalog's profile, which requires the SDK's source beside lifecycle, and the broker prepares it as the
        // catalog says.
        CaptureProfileDescriptor profile = CaptureProfileCatalog.Find(request.ProfileId)!;
        Assert.Equal((CaptureProfileKind.CooperativeSdk, request.ProfileId), (profile.Kind, profile.Id));
        Assert.Equal([WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.CooperativeSdkSourceId],
            profile.Sources.Where(source => source.Required).Select(source => source.SourceId));
        Assert.Equal(CaptureProfileKind.CooperativeSdk, RecordCommand.Profile("sdk"));
        Assert.Equal(CaptureProfileKind.CooperativeSdk, RecordCommand.Profile("SDK"));
        Assert.Contains("sdk", CaptureRequests.Profiles);
        Assert.Contains("sdk", RecordCommand.Profiles);

        // A focus, wider capture or content is not its to take, and is refused before Windows is asked for approval.
        foreach ((string? mechanism, string[] words, bool broader, string refusal) in new (string?, string[], bool, string)[]
        {
            ("tcp", [], false, "focused-transport takes --mechanism tcp and optionally --pid <id,...> and --allow-broader; "
                + "explore, rpc-peers, unix-sockets, com-calls, sync-objects, window-messages, sections and sdk take neither."),
            (null, ["--pid", "4242"], false, "focused-transport takes --mechanism tcp"),
            (null, [], true, "focused-transport takes --mechanism tcp"),
            (null, ["--max-record-bytes", "1"], false, "--source, --channel, the byte limits, --inspection and --retention"),
        })
        {
            Assert.Null(CaptureRequests.Parse("sdk", mechanism, broader, null, null, null, false, Options(words), out problem));
            Assert.StartsWith(refusal, problem, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat record and icat capture say what the sdk profile records, that what an application says is its own word, and that its cost is not yet measured")]
    public async Task TheirHelpSaysWhatTheSdkRecords()
    {
        foreach ((string[] asked, string records, string owned) in new (string[], string, string)[]
        {
            (["record", "--help"], "what applications instrumented with InterCat's SDK say of themselves - their schema and clock",
                "each owned by the process it names (ADR-103); what one says is its own word, and the profile's cost is not yet measured."),
            (["capture", "--help"], "lifecycle and what applications instrumented with InterCat's SDK say of themselves",
                "each owned by the process it names; what one says is its own word, and the profile's cost is not yet measured."),
        })
        {
            (InterCatExitCode code, string help) = await Run(asked);
            Assert.Equal(InterCatExitCode.Success, code);
            string words = System.Text.RegularExpressions.Regex.Replace(help, @"\s+", " ");
            Assert.Contains("--profile sdk records " + records, words, StringComparison.Ordinal);
            Assert.Contains(owned, words, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat profiles sdk compiles the SDK's source from the manifest it carries, includes it beside lifecycle, and states the source's measured cost with its evidence, the profile's own still unmeasured")]
    public async Task ItsPreviewIncludesTheSdk()
    {
        (InterCatExitCode code, string preview) = await Run("profiles", "sdk");
        Assert.True(code is InterCatExitCode.Success or InterCatExitCode.PartialResultSuccess, preview);
        string words = System.Text.RegularExpressions.Regex.Replace(preview, @"\s+", " ");
        Assert.Contains("Requested profile sdk", words, StringComparison.Ordinal);
        Assert.Contains("Can start exactly yes", words, StringComparison.Ordinal);

        // The source's cost is what keeping its content cost, every one of its events recorded (ADR-107's addendum): low.
        Assert.Matches(@"etw/eventsource/InterCat-Sdk\s+required\s+included\s+low", preview);
        Assert.Contains("etw/eventsource/InterCat-Sdk: overhead evidence bench/results/sdk-content-impact-20261107T203409Z/impact.json",
            words, StringComparison.Ordinal);
        Assert.DoesNotContain("Measured cost", preview, StringComparison.Ordinal);
    }

    private static ContentRequestOptions Options(string[] words) => ContentRequestOptions.Take(new CommandLine(words));

    private static async Task<(InterCatExitCode Code, string Output)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        using var answered = new StringWriter();
        Console.SetOut(answered);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString());
        }
        finally
        {
            Console.SetOut(output);
        }
    }
}
