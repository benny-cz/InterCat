using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat capture and icat record take the opt-in window messages profile (ADR-087): lifecycle and every message one window
/// sends or posts another from Win32k's message pump, asked for as Explore is and refused for a focus or content it does not
/// take, its cost stated with the throughput regression its source measured.
/// </summary>
public sealed class WindowMessagesCaptureTests
{
    [Fact(DisplayName = "R18: icat capture records window messages through the broker as it records Explore, live, and refuses a focus or content it does not take")]
    public void IcatCaptureRecordsWindowMessages()
    {
        BrokerPrepareCaptureRequest request = CaptureRequests.Parse("window-messages", null, false, "90", null, null, true,
            Options([]), out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(("window-messages", (Mechanism?)null, 0, false, (ContentCaptureRequest?)null),
            (request.ProfileId, request.FocusedMechanism, request.FocusedProcessIds.Count, request.AllowBroaderCapture,
                request.Content));
        Assert.Equal((BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live, 90),
            (request.Retention, request.Publication, request.Quota.MaximumDurationSeconds));

        // It names the catalog's profile, which requires Win32k's message pump beside lifecycle, and the broker prepares it
        // as the catalog says.
        CaptureProfileDescriptor profile = CaptureProfileCatalog.Find(request.ProfileId)!;
        Assert.Equal((CaptureProfileKind.WindowMessages, request.ProfileId), (profile.Kind, profile.Id));
        Assert.Equal([WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.Win32kSourceId],
            profile.Sources.Where(source => source.Required).Select(source => source.SourceId));
        Assert.Equal(CaptureProfileKind.WindowMessages, RecordCommand.Profile("window-messages"));
        Assert.Equal(CaptureProfileKind.WindowMessages, RecordCommand.Profile("WindowMessages"));

        // A focus, wider capture or content is not its to take, and is refused before Windows is asked for approval.
        foreach ((string? mechanism, string[] words, bool broader, string refusal) in new (string?, string[], bool, string)[]
        {
            ("tcp", [], false, "focused-transport takes --mechanism tcp and optionally --pid <id,...> and --allow-broader; "
                + "explore, rpc-peers, unix-sockets, com-calls, sync-objects and window-messages take neither."),
            (null, ["--pid", "4242"], false, "focused-transport takes --mechanism tcp"),
            (null, [], true, "focused-transport takes --mechanism tcp"),
            (null, ["--max-record-bytes", "1"], false, "--source, --channel, the byte limits, --inspection and --retention"),
        })
        {
            Assert.Null(CaptureRequests.Parse("window-messages", mechanism, broader, null, null, null, false, Options(words), out problem));
            Assert.StartsWith(refusal, problem, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat record and icat capture say what window messages records and that collecting it slows a message-bound workload")]
    public async Task TheirHelpSaysWhatWindowMessagesRecords()
    {
        foreach ((string[] asked, string records, string slows) in new (string[], string, string)[]
        {
            (["record", "--help"], "every message one window sends or posts another from Win32k's message pump",
                "collecting them slows a message-bound workload, so icat profiles window-messages states its cost."),
            (["capture", "--help"], "lifecycle and every message one window sends or posts another from Win32k",
                "it slows a message-bound workload, so icat profiles window-messages states its cost."),
        })
        {
            (InterCatExitCode code, string help) = await Run(asked);
            Assert.Equal(InterCatExitCode.Success, code);
            string words = System.Text.RegularExpressions.Regex.Replace(help, @"\s+", " ");
            Assert.Contains("--profile window-messages records " + records, words, StringComparison.Ordinal);
            Assert.Contains("each end owned by the process that raised it and numbered by its message", words, StringComparison.Ordinal);
            Assert.Contains(slows, words, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat profiles window-messages states its cost - its sources' Moderate at most, the message pump's collection Low but a message-bound workload past the throughput target, nothing measured whole yet - and the evidence it rests on")]
    public async Task ItsPreviewStatesItsCost()
    {
        (InterCatExitCode code, string preview) = await Run("profiles", "window-messages");
        Assert.Equal(InterCatExitCode.Success, code);
        string words = System.Text.RegularExpressions.Regex.Replace(preview, @"\s+", " ");
        Assert.Contains("Measured cost moderate: its sources measured Moderate at most (Kernel-Process 1.75 CPU pp)", words, StringComparison.Ordinal);
        Assert.Contains("a message-bound workload ran 8.3 and 5.2% lower in throughput, past the 5% target", words, StringComparison.Ordinal);
        Assert.Contains("Nothing has measured the profile whole yet.", words, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Cost evidence +bench/results/window-message-impact-\d{8}T\d{6}Z/impact\.json\r?$", preview);
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
