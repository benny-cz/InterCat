using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat capture and icat record take the opt-in synchronization objects profile (ADR-083): lifecycle and each event's,
/// mutant's and semaphore's handles from the kernel's object manager, asked for as Explore is and refused for a focus or
/// content it does not take, its cost stated with the throughput regression its source measured.
/// </summary>
public sealed class SyncObjectsCaptureTests
{
    [Fact(DisplayName = "R18: icat capture records synchronization objects through the broker as it records Explore, live, and refuses a focus or content it does not take")]
    public void IcatCaptureRecordsSyncObjects()
    {
        BrokerPrepareCaptureRequest request = CaptureRequests.Parse("sync-objects", null, false, "90", null, null, true,
            Options([]), out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(("sync-objects", (Mechanism?)null, 0, false, (ContentCaptureRequest?)null),
            (request.ProfileId, request.FocusedMechanism, request.FocusedProcessIds.Count, request.AllowBroaderCapture,
                request.Content));
        Assert.Equal((BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live, 90),
            (request.Retention, request.Publication, request.Quota.MaximumDurationSeconds));

        // It names the catalog's profile, which requires the object manager's handle group beside lifecycle, and the
        // broker prepares it as the catalog says.
        CaptureProfileDescriptor profile = CaptureProfileCatalog.Find(request.ProfileId)!;
        Assert.Equal((CaptureProfileKind.SyncObjects, request.ProfileId), (profile.Kind, profile.Id));
        Assert.Equal([WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.KernelObjectHandlesSourceId],
            profile.Sources.Where(source => source.Required).Select(source => source.SourceId));
        Assert.Equal(CaptureProfileKind.SyncObjects, RecordCommand.Profile("sync-objects"));

        // A focus, wider capture or content is not its to take, and is refused before Windows is asked for approval.
        foreach ((string? mechanism, string[] words, bool broader, string refusal) in new (string?, string[], bool, string)[]
        {
            ("tcp", [], false, "focused-transport takes --mechanism tcp and optionally --pid <id,...> and --allow-broader; "
                + "explore, rpc-peers, unix-sockets, com-calls and sync-objects take neither."),
            (null, ["--pid", "4242"], false, "focused-transport takes --mechanism tcp"),
            (null, [], true, "focused-transport takes --mechanism tcp"),
            (null, ["--max-record-bytes", "1"], false, "--source, --channel, the byte limits, --inspection and --retention"),
        })
        {
            Assert.Null(CaptureRequests.Parse("sync-objects", mechanism, broader, null, null, null, false, Options(words), out problem));
            Assert.StartsWith(refusal, problem, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat record and icat capture say what synchronization objects records and that collecting it slows a handle-heavy workload")]
    public async Task TheirHelpSaysWhatSyncObjectsRecords()
    {
        foreach ((string[] asked, string slows) in new (string[], string)[]
        {
            (["record", "--help"], "collecting them slows a handle-heavy workload, so icat profiles sync-objects states its cost."),
            (["capture", "--help"], "it slows a handle-heavy workload, so icat profiles sync-objects states its cost."),
        })
        {
            (InterCatExitCode code, string help) = await Run(asked);
            Assert.Equal(InterCatExitCode.Success, code);
            string words = System.Text.RegularExpressions.Regex.Replace(help, @"\s+", " ");
            Assert.Contains("--profile sync-objects records", words, StringComparison.Ordinal);
            Assert.Contains("each event's, mutex's and semaphore's handles", words, StringComparison.Ordinal);
            Assert.Contains(slows, words, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat profiles sync-objects states its sources' cost - the handle group Low in processor time but past the throughput target - and the evidence it rests on")]
    public async Task ItsPreviewStatesItsCost()
    {
        (InterCatExitCode code, string preview) = await Run("profiles", "sync-objects");
        Assert.Equal(InterCatExitCode.Success, code);
        string words = System.Text.RegularExpressions.Regex.Replace(preview, @"\s+", " ");
        Assert.Contains("Measured cost moderate: its sources measured Moderate at most", words, StringComparison.Ordinal);
        Assert.Contains("a handle-heavy workload ran 5.3 and 8.3% lower in throughput, past the 5% target", words, StringComparison.Ordinal);
        Assert.Contains("Nothing has measured the profile whole yet.", words, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Cost evidence +bench/results/sync-objects-impact-handles-\d{8}T\d{6}Z/impact\.json\r?$", preview);
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
