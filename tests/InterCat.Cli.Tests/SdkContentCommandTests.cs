using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// A content request of what an application says through InterCat's SDK, in the command line's words (ADR-107's addendum):
/// icat profiles previews one, read from no machine, and icat record takes its mechanism for --profile content alone.
/// </summary>
public sealed class SdkContentCommandTests
{
    [Fact(DisplayName = "§15: icat profiles and icat record take a content request of an application's messages said through the SDK - --mechanism sdk with its source - and no other profile takes that mechanism")]
    public async Task AContentRequestNamesTheSdk()
    {
        // A preview, read from no machine: the request admitted for the SDK's source, and what it keeps said before anything is.
        (InterCatExitCode code, string text, string said) = await Run("profiles", "content", "--source", "etw/eventsource/InterCat-Sdk",
            "--mechanism", "sdk", "--pid", "1234", "--channel", "*", "--max-record-bytes", "4096", "--max-session-bytes", "65536",
            "--retention", "stop-at-limit", "--inspection", "hex-text");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Mechanism +Application SDK\r?$", text);
        Assert.Contains("so a message it encrypts on the wire is kept as its plaintext", text, StringComparison.Ordinal);

        // icat record reads the mechanism of a content request - refused here for what the request lacks, before any capture
        // starts - and refuses it of any other profile.
        string directory = Path.Combine(Path.GetTempPath(), "icat-sdk-content-" + Guid.NewGuid().ToString("N"));
        (code, _, said) = await Run("record", directory, "--profile", "content", "--mechanism", "sdk");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--profile content needs --source", said, StringComparison.Ordinal);
        (code, _, said) = await Run("record", directory, "--profile", "focused-transport", "--mechanism", "sdk");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("http or sdk the messages a content request keeps", said, StringComparison.Ordinal);
        Assert.False(Directory.Exists(directory));
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
