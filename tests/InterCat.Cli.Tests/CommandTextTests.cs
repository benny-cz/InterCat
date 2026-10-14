using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// What icat prints for a person, where its tests had read only the JSON a script reads (R5, R18): the profiles a capture
/// can ask for and how a request would compile, packages measured and written, the workspace's help, a compaction and a
/// recovery review - each in words, current, and saying a thing once.
/// </summary>
public sealed class CommandTextTests : IDisposable
{
    /// <summary>Enumeration names a person should never read where a word says it.</summary>
    private static readonly string[] Identifiers =
    [
        "MetadataOnly", "ScopedContent", "OriginalEvidence", "Included", "Omitted", "Blocking", "Unmeasured", "Moderate",
        "WholeMachine", "ProcessFiltered",
    ];

    private readonly string folder = Path.Combine(Path.GetTempPath(), "InterCat.Cli.Tests.Text", Guid.NewGuid().ToString("N"));

    public CommandTextTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "R5: the profiles a capture can ask for, and how one compiles, read in words, and the flight recorder says where it is")]
    public async Task ProfilesReadInWords()
    {
        (InterCatExitCode listed, string catalog, _) = await Run("profiles");
        Assert.Equal(InterCatExitCode.Success, listed);
        Assert.Matches(@"explore\s+metadata only\s+capture preview", catalog);
        Assert.Matches(@"content\s+scoped content\s+request preview only", catalog);
        Assert.All(Identifiers, identifier => Assert.DoesNotContain(identifier, catalog, StringComparison.Ordinal));

        // A rolling capture is the flight recorder, which the catalog says and how to start one, never that it is not built.
        Assert.Matches(@"flight-recorder\s+metadata only\s+not available\s+A rolling capture is the flight recorder", catalog);
        Assert.DoesNotContain("not implemented yet", catalog, StringComparison.Ordinal);
        (_, string flight, string flightSaid) = await Run("profiles", "flight-recorder");
        Assert.Contains("Starts no capture of its own: a rolling capture is the flight recorder.", flight, StringComparison.Ordinal);
        string why = CaptureProfileReason();
        Assert.Contains("Run icat capture --keep-last <seconds>, or choose The last 10 minutes or The last hour in the window's Keep.",
            why, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(flight + flightSaid, why));

        // A request's sources each read what the compiled request does with them, at their measured cost, in words.
        (_, string explore, _) = await Run("profiles", "explore");
        Assert.Matches(@"Requested admission\s+metadata only", explore);
        Assert.Matches(@"Microsoft-Windows-Kernel-Process\s+required\s+(included|omitted|blocks the start)\s+moderate", explore);
        Assert.All(Identifiers, identifier => Assert.DoesNotContain(identifier, explore, StringComparison.Ordinal));

        // A profile's measured cost leads with its class in a word.
        Assert.Matches(@"Measured cost\s+moderate: its sources measured Moderate at most", (await Run("profiles", "rpc-peers")).Output);

        // A focused request names its mechanism as the window does, whether it is taken or refused.
        (_, string focused, _) = await Run("profiles", "focused-transport", "--mechanism", "tcp", "--pid", "4242");
        Assert.Matches(@"Mechanism\s+TCP", focused);
        Assert.Contains("Broader capture needs consent: TCP cannot be retained only for PID 4242", focused, StringComparison.Ordinal);
        (_, string refused, _) = await Run("profiles", "focused-transport", "--mechanism", "http");
        Assert.Contains("HTTP is not available in Focused transport.", refused, StringComparison.Ordinal);

        // Where a provider is registered, each source's row says what the request does with it and how far it reaches, in
        // the words these give; a value this version does not know is named by its number.
        Assert.Equal(["included", "omitted", "blocks the start", "decision 0"],
            [.. Enum.GetValues<InterCat.Capture.Windows.ProfileSourceDecisionState>().Select(ProfilesCommand.DecisionText),
                ProfilesCommand.DecisionText(0)]);
        Assert.Equal(
        [
            "the whole machine, as the profile asks", "the processes asked for", "the whole machine, for context the processes need",
            "the whole machine: the provider cannot filter by process", "scope 0",
        ], [.. Enum.GetValues<ProviderProcessScope>().Select(ProfilesCommand.ScopeText), ProfilesCommand.ScopeText(0)]);

        // A content request for a source no content contract admits says which source keeps content, and how.
        (_, string content, _) = await Run("profiles", "content", "--source", "etw/manifest/Microsoft-Windows-Kernel-Network",
            "--mechanism", "tcp", "--pid", "4242", "--channel", "*", "--max-record-bytes", "4096", "--max-session-bytes", "65536",
            "--retention", "stop-at-limit", "--inspection", "hex-text");
        Assert.Contains("Content is kept only from a source admitted for it - WinINet's HTTP exchanges - through icat record or "
            + "icat capture.", content, StringComparison.Ordinal);
        Assert.Matches(@"Requested admission\s+scoped content", content);
    }

    [Fact(DisplayName = "§11.3: a package's text says what it verified in the window's words, and asks for its review once")]
    public async Task PackagesSayWhatTheyVerified()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));

        (InterCatExitCode redacted, string pseudonymous, _) = await Run(
            "package", demo.ClientSession, "--redacted", "--output", Path.Combine(folder, "redacted"));
        Assert.Equal(InterCatExitCode.Success, redacted);
        Assert.Equal(1, Occurrences(pseudonymous, "Review the package before sharing it."));
        Assert.Contains("Deleting the source afterwards is not a secure erase on an SSD.", pseudonymous, StringComparison.Ordinal);

        (InterCatExitCode original, string copied, _) = await Run(
            "package", demo.ClientSession, "--original", "--output", Path.Combine(folder, "original"));
        Assert.Equal(InterCatExitCode.Success, original);
        Assert.Matches(@"Verified\s+Each file checked against the digest its generation recorded as it was copied, then the package "
            + "reopened and hashed as a recipient would open it.", copied);
        Assert.DoesNotMatch(@"\d+ files reopened", copied);
    }

    [Fact(DisplayName = "R18: icat workspace's help names the investigation's current file, and says what a package carries as it does")]
    public async Task WorkspaceHelpIsCurrent()
    {
        (InterCatExitCode code, string help, _) = await Run("workspace", "--help");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains($"An investigation over separately captured sessions ({InvestigationWorkspace.Contract}, ADR-038)", help,
            StringComparison.Ordinal);
        Assert.Contains("A session as recorded goes unredacted and a redacted package as it is,", help, StringComparison.Ordinal);
        Assert.Contains("and the file's names, notes and translated endpoints as they were written.", help, StringComparison.Ordinal);
        Assert.Contains("with what its capture covered there, in the window's words; a session with no place says why.", help,
            StringComparison.Ordinal);
        Assert.DoesNotContain("It is unredacted.", help, StringComparison.Ordinal);

        // What it makes, aligns and compares is the investigation, as the window calls it; the command keeps its name.
        Assert.Contains("new      makes an empty investigation; a name without an extension gets .icat-workspace.", help,
            StringComparison.Ordinal);
        Assert.Contains("first alignment makes the other session's clock the investigation's time.", help, StringComparison.Ordinal);
        Assert.Contains("compare  places two instants in the investigation's time", help, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace's time", help, StringComparison.Ordinal);

        // Its sessions are sessions, as the window lists them; member is only the file's word for one.
        Assert.Contains("show     resolves each session where it was last found", help, StringComparison.Ordinal);
        Assert.DoesNotContain("member", help, StringComparison.OrdinalIgnoreCase);
        (_, string overview, _) = await Run("--help");
        Assert.Contains("session per capture, never writing to one. show says where each session stands;", overview,
            StringComparison.Ordinal);
        Assert.DoesNotContain("member", overview, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "§20.1: a compaction with nothing to coalesce and a recovery review with nothing to repair each say so in words")]
    public async Task CompactionAndRecoverySayWhatTheyFound()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));

        (InterCatExitCode compacted, string compaction, _) = await Run("compact", demo.ServerSession);
        Assert.Equal(InterCatExitCode.Success, compacted);
        Assert.Contains("NOTHING TO COMPACT", compaction, StringComparison.Ordinal);
        Assert.Matches(@"Publications\s+1, 1 small", compaction);
        Assert.Contains("Nothing to coalesce: no two consecutive publications are small, below both 64,000 rows and 8 MiB.",
            compaction, StringComparison.Ordinal);

        (InterCatExitCode reviewed, string review, _) = await Run("recover", demo.ServerSession);
        Assert.Equal(InterCatExitCode.Success, reviewed);
        Assert.Matches(@"Current pointer\s+verifies; it names generation 2", review);
        Assert.Contains("The current pointer verifies. No repair is needed or performed.", review, StringComparison.Ordinal);
    }

    private static string CaptureProfileReason() =>
        InterCat.Capture.Windows.CaptureProfileCatalog.Find("flight-recorder")!.UnavailableReason!;

    private static int Occurrences(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(said);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString(), said.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
