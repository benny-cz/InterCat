using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>`icat demo --discovery`: the generated discovery demo written, said to be generated, and its resources listed.</summary>
public sealed class DiscoveryDemoCommandTests
{
    [Fact(DisplayName = "§14: icat demo --discovery writes the discovery demo into a new folder and says it was generated, and icat resources lists what its processes held")]
    public async Task TheDiscoveryDemoIsWritten()
    {
        string folder = Path.Combine(Path.GetTempPath(), "intercat-discovery-cli-" + Guid.NewGuid().ToString("N"));
        string again = Path.Combine(Path.GetTempPath(), "intercat-discovery-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            (InterCatExitCode code, string text, string said) = await Run("demo", folder, "--discovery");
            Assert.Equal(InterCatExitCode.Success, code);
            Assert.Matches(@"(?m)^  Demo workstation +.+demo-discovery\r?$", text);
            Assert.Contains("InterCat generated this demo", said, StringComparison.Ordinal);

            (code, text, _) = await Run("resources", Path.Combine(folder, "demo-discovery"));
            Assert.Equal(InterCatExitCode.Success, code);
            Assert.Contains("IndexReady", text, StringComparison.Ordinal);
            Assert.Contains("PreviewFrames", text, StringComparison.Ordinal);

            // As a document, its one session.
            (code, text, _) = await Run("demo", again, "--discovery", "--json");
            Assert.Equal(InterCatExitCode.Success, code);
            using JsonDocument document = JsonDocument.Parse(text);
            Assert.Equal(Path.Combine(again, "demo-discovery"), Assert.Single(document.RootElement.GetProperty("sessions").EnumerateArray()).GetString());
        }
        finally
        {
            foreach (string made in new[] { folder, again })
            {
                if (Directory.Exists(made))
                {
                    Directory.Delete(made, recursive: true);
                }
            }
        }
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
