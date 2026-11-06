using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat capture and icat record take the opt-in COM calls profile (ADR-079): lifecycle, RPC calls and the COM calls they
/// carry from COM-Perf, asked for as Explore is and refused for a focus or content it does not take, its cost stated with
/// the throughput regression its source measured.
/// </summary>
public sealed class ComCallsCaptureTests
{
    [Fact(DisplayName = "R18: icat capture records COM calls through the broker as it records Explore, live, and refuses a focus or content it does not take")]
    public void IcatCaptureRecordsComCalls()
    {
        BrokerPrepareCaptureRequest request = CaptureRequests.Parse("com-calls", null, false, "90", null, null, true,
            Options([]), out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(("com-calls", (Mechanism?)null, 0, false, (ContentCaptureRequest?)null),
            (request.ProfileId, request.FocusedMechanism, request.FocusedProcessIds.Count, request.AllowBroaderCapture,
                request.Content));
        Assert.Equal((BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live, 90),
            (request.Retention, request.Publication, request.Quota.MaximumDurationSeconds));

        // It names the catalog's profile, which requires COM-Perf and RPC beside lifecycle, so a COM call's RPC call can be
        // served through it, and the broker prepares it as the catalog says.
        CaptureProfileDescriptor profile = CaptureProfileCatalog.Find(request.ProfileId)!;
        Assert.Equal((CaptureProfileKind.ComCalls, request.ProfileId), (profile.Kind, profile.Id));
        Assert.Equal(
            [WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.RpcSourceId, WindowsSourceCatalog.ComPerfSourceId],
            profile.Sources.Where(source => source.Required).Select(source => source.SourceId));
        Assert.Equal(CaptureProfileKind.ComCalls, RecordCommand.Profile("com-calls"));

        // A focus, wider capture or content is not its to take, and is refused before Windows is asked for approval.
        foreach ((string? mechanism, string[] words, bool broader, string refusal) in new (string?, string[], bool, string)[]
        {
            ("tcp", [], false, "focused-transport takes --mechanism tcp and optionally --pid <id,...> and --allow-broader; "
                + "explore, rpc-peers, unix-sockets, com-calls, sync-objects, window-messages and sections take neither."),
            (null, ["--pid", "4242"], false, "focused-transport takes --mechanism tcp"),
            (null, [], true, "focused-transport takes --mechanism tcp"),
            (null, ["--max-record-bytes", "1"], false, "--source, --channel, the byte limits, --inspection and --retention"),
        })
        {
            Assert.Null(CaptureRequests.Parse("com-calls", mechanism, broader, null, null, null, false, Options(words), out problem));
            Assert.StartsWith(refusal, problem, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat record and icat capture say what COM calls records and that collecting COM-Perf slows a COM-heavy workload")]
    public async Task TheirHelpSaysWhatComCallsRecords()
    {
        foreach (string[] asked in (string[][])[["record", "--help"], ["capture", "--help"]])
        {
            (InterCatExitCode code, string help) = await Run(asked);
            Assert.Equal(InterCatExitCode.Success, code);
            string words = System.Text.RegularExpressions.Regex.Replace(help, @"\s+", " ");
            Assert.Contains("--profile com-calls records", words, StringComparison.Ordinal);
            Assert.Contains("collecting COM-Perf slows a COM-heavy workload, so icat profiles com-calls states its cost.", words,
                StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat profiles com-calls states the cost measured through the product - the throughput regression unpaced, within the target in bursts, nothing dropped - and the evidence it rests on")]
    public async Task ItsPreviewStatesItsCost()
    {
        (InterCatExitCode code, string preview) = await Run("profiles", "com-calls");
        Assert.Equal(InterCatExitCode.Success, code);
        string words = System.Text.RegularExpressions.Regex.Replace(preview, @"\s+", " ");
        Assert.Contains("Measured cost moderate: its sources measured Moderate at most", words, StringComparison.Ordinal);
        Assert.Contains("the workload ran 35.2% slower, past the 5% throughput target, nothing dropped or lost", words, StringComparison.Ordinal);
        Assert.Contains("In bursts of 64 calls 40 ms apart it added 2.6 pp and the workload ran 0.9% slower, nothing dropped or lost",
            words, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Cost evidence +bench/results/com-calls-impact-\d{8}T\d{6}Z/product-impact\.json\r?$", preview);
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
