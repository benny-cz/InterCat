using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat capture and icat record take the opt-in Unix sockets profile (ADR-074): lifecycle and each AF_UNIX socket's calls
/// from Winsock AFD, asked for as Explore is and refused for a focus or content it does not take.
/// </summary>
public sealed class UnixSocketsCaptureTests
{
    [Fact(DisplayName = "R18: icat capture records Unix sockets through the broker as it records Explore, live, and refuses a focus or content it does not take")]
    public void IcatCaptureRecordsUnixSockets()
    {
        BrokerPrepareCaptureRequest request = CaptureRequests.Parse("unix-sockets", null, false, "90", null, null, true,
            Options([]), out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(("unix-sockets", (Mechanism?)null, 0, false, (ContentCaptureRequest?)null),
            (request.ProfileId, request.FocusedMechanism, request.FocusedProcessIds.Count, request.AllowBroaderCapture,
                request.Content));
        Assert.Equal((BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live, 90),
            (request.Retention, request.Publication, request.Quota.MaximumDurationSeconds));

        // It names the catalog's profile by its canonical ID, which requires Winsock AFD beside lifecycle, so the broker
        // prepares it as the catalog says.
        CaptureProfileDescriptor profile = CaptureProfileCatalog.Find(request.ProfileId)!;
        Assert.Equal((CaptureProfileKind.UnixSockets, request.ProfileId), (profile.Kind, profile.Id));
        Assert.Contains(profile.Sources, source => source.SourceId == WindowsSourceCatalog.WinsockAfdSourceId && source.Required);

        // A focus, wider capture or content is not its to take, and is refused before Windows is asked for approval.
        foreach ((string? mechanism, string[] words, bool broader, string refusal) in new (string?, string[], bool, string)[]
        {
            ("tcp", [], false, "focused-transport takes --mechanism tcp and optionally --pid <id,...> and --allow-broader; "
                + "explore, rpc-peers and unix-sockets take neither."),
            (null, ["--pid", "4242"], false, "focused-transport takes --mechanism tcp"),
            (null, [], true, "focused-transport takes --mechanism tcp"),
            (null, ["--max-record-bytes", "1"], false, "--source, --channel, the byte limits, --inspection and --retention"),
        })
        {
            Assert.Null(CaptureRequests.Parse("unix-sockets", mechanism, broader, null, null, null, false, Options(words),
                out problem));
            Assert.StartsWith(refusal, problem, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat record takes every profile its help names, each by the catalog's id, and refuses any other by the same list")]
    public async Task IcatRecordTakesEveryProfileItsHelpNames()
    {
        Assert.Equal(["explore", "focused-transport", "rpc-peers", "unix-sockets", "content", "content-fixture"], RecordCommand.Profiles);
        foreach (string profile in RecordCommand.Profiles)
        {
            CaptureProfileDescriptor named = CaptureProfileCatalog.Find(profile)!;
            Assert.Equal(named.Kind, RecordCommand.Profile(profile));
            Assert.Equal(named.Kind, RecordCommand.Profile(profile.ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal)));
        }

        Assert.Null(RecordCommand.Profile("timing"));
        (InterCatExitCode code, string help) = await Run("--help");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("icat record <new-session-dir> [--profile explore|focused-transport|rpc-peers|unix-sockets|content|content-fixture]",
            help, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: icat record and icat capture say what Unix sockets records and that AFD collects every socket call of the machine")]
    public async Task TheirHelpSaysWhatUnixSocketsRecords()
    {
        foreach (string[] asked in (string[][])[["record", "--help"], ["capture", "--help"]])
        {
            (InterCatExitCode code, string help) = await Run(asked);
            Assert.Equal(InterCatExitCode.Success, code);
            string words = System.Text.RegularExpressions.Regex.Replace(help, @"\s+", " ");
            Assert.Contains("--profile unix-sockets records", words, StringComparison.Ordinal);
            Assert.Contains("AFD collects every socket call of the machine, so icat profiles unix-sockets states its cost.", words,
                StringComparison.Ordinal);
        }
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
