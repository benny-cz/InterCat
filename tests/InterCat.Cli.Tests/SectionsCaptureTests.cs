using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat capture and icat record take the opt-in sections profile (ADR-090): lifecycle, every view a process maps or unmaps from
/// the kernel's file view flag and each section's handles from the object manager, asked for as Explore is and refused for a
/// focus or content it does not take, its cost stated with the throughput regressions its sources measured.
/// </summary>
public sealed class SectionsCaptureTests
{
    [Fact(DisplayName = "R18: icat capture records sections through the broker as it records Explore, live, and refuses a focus or content it does not take")]
    public void IcatCaptureRecordsSections()
    {
        BrokerPrepareCaptureRequest request = CaptureRequests.Parse("sections", null, false, "90", null, null, true,
            Options([]), out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(("sections", (Mechanism?)null, 0, false, (ContentCaptureRequest?)null),
            (request.ProfileId, request.FocusedMechanism, request.FocusedProcessIds.Count, request.AllowBroaderCapture,
                request.Content));
        Assert.Equal((BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live, 90),
            (request.Retention, request.Publication, request.Quota.MaximumDurationSeconds));

        // It names the catalog's profile, which requires the file view flag and sections' handles beside lifecycle, and the
        // broker prepares it as the catalog says.
        CaptureProfileDescriptor profile = CaptureProfileCatalog.Find(request.ProfileId)!;
        Assert.Equal((CaptureProfileKind.Sections, request.ProfileId), (profile.Kind, profile.Id));
        Assert.Equal([WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.KernelFileViewsSourceId, WindowsSourceCatalog.KernelSectionHandlesSourceId],
            profile.Sources.Where(source => source.Required).Select(source => source.SourceId));
        Assert.Equal(CaptureProfileKind.Sections, RecordCommand.Profile("sections"));
        Assert.Equal(CaptureProfileKind.Sections, RecordCommand.Profile("Sections"));

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
            Assert.Null(CaptureRequests.Parse("sections", mechanism, broader, null, null, null, false, Options(words), out problem));
            Assert.StartsWith(refusal, problem, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat record and icat capture say what sections records and that collecting it slows a workload heavy in views or handles")]
    public async Task TheirHelpSaysWhatSectionsRecords()
    {
        foreach ((string[] asked, string records, string owned, string slows) in new (string[], string, string, string)[]
        {
            (["record", "--help"], "every view a process maps or unmaps from the kernel's file view flag, read by the layout measured for this build",
                "each owned by its process and numbered by its view and section, never an address (ADR-090)",
                "collecting them slows a workload heavy in either, so icat profiles sections states its cost."),
            (["capture", "--help"], "lifecycle, every view a process maps or unmaps from the kernel's file view flag",
                "each owned by its process and numbered by its view and section",
                "it slows a workload heavy in either, so icat profiles sections states its cost."),
        })
        {
            (InterCatExitCode code, string help) = await Run(asked);
            Assert.Equal(InterCatExitCode.Success, code);
            string words = System.Text.RegularExpressions.Regex.Replace(help, @"\s+", " ");
            Assert.Contains("--profile sections records " + records, words, StringComparison.Ordinal);
            Assert.Contains(owned, words, StringComparison.Ordinal);
            Assert.Contains(slows, words, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat profiles sections states the cost measured through the product - Moderate, past the throughput target, nothing dropped, its sources' beside it - the evidence it rests on, and that the file view flag is read by the layout measured for the build, a partial result though it can start exactly")]
    public async Task ItsPreviewStatesItsCost()
    {
        // The file view flag is read by the layout measured for this build, which the preview's diagnostics state (ADR-091),
        // so the preview is a partial result, though every source compiled and the capture can start exactly.
        (InterCatExitCode code, string preview) = await Run("profiles", "sections");
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        string words = System.Text.RegularExpressions.Regex.Replace(preview, @"\s+", " ");
        Assert.Contains("Can start exactly yes", words, StringComparison.Ordinal);
        Assert.Contains($"etw/kernel-flag/VAMap: {MeasuredClassicLayouts.Statement(CapabilityInventoryProbe.DescribeEnvironment().BuildId, 3)}", words, StringComparison.Ordinal);
        Assert.Contains("Measured cost moderate: measured through the product on FX-SECT-001's opener", words, StringComparison.Ordinal);
        Assert.Contains("it added a median 1.9 pp to no capture, its own process 3.4, and the workload ran 17.6% slower, past the 5% "
            + "throughput target, nothing dropped or lost", words, StringComparison.Ordinal);
        Assert.Contains("a view-heavy workload ran 9.5, 5.2, 5.3 and 0.2% lower in throughput", words, StringComparison.Ordinal);
        Assert.Contains("a handle-heavy workload ran 5.3 and 8.3% lower.", words, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Cost evidence +bench/results/sections-impact-\d{8}T\d{6}Z/product-impact\.json\r?$", preview);
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
