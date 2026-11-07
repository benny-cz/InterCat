using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>`icat capabilities --catalog` and `--find`: the capability catalog, read from no machine, and a search of it (§4.1).</summary>
public sealed class CapabilityCatalogCommandTests
{
    [Fact(DisplayName = "§4.1: icat capabilities --catalog explains every mechanism, read from no machine - its sources, every build's measured tier and why - and --find keeps those whose words hold the text")]
    public async Task TheCatalogIsPrintedAndSearched()
    {
        // A mailslot, which no source records: explained, with the tier its fixture measured.
        (InterCatExitCode code, string text, _) = await Run("capabilities", "--catalog");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^Mailslot\r?$", text);
        Assert.Matches(@"(?m)^  Measured +unsupported on 10\.0\.26220\.0-x64 by FX-MAIL-001, \d{4}-\d{2}-\d{2}\r?$", text);
        Assert.Matches(@"(?m)^  Sources +none in the catalog\r?$", text);
        Assert.Matches(@"(?m)^  Why +No driverless source records one: .+\(ADR-085\)\.\r?$", text);

        // A search keeps three, an anonymous pipe's source said to be requested by no profile.
        (code, text, _) = await Run("capabilities", "--find", "pipe");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("3 of 22 mechanisms hold \"pipe\".", text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Requested by no profile +etw/manifest/Microsoft-Windows-Kernel-File\r?$", text);
        Assert.Matches(@"(?m)^Anonymous pipe\r?$", text);
        Assert.DoesNotMatch(@"(?m)^Mailslot\r?$", text);

        // As a document naming its contract, a search that keeps none.
        (code, text, _) = await Run("capabilities", "--find", "no such words", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using (JsonDocument document = JsonDocument.Parse(text))
        {
            Assert.Equal(("capability-catalog-v1", 22, 0), (document.RootElement.GetProperty("contract").GetString(),
                document.RootElement.GetProperty("mechanisms").GetInt32(), document.RootElement.GetProperty("entries").GetArrayLength()));
        }

        // The catalog is printed, never written as a machine's report.
        (code, _, string said) = await Run("capabilities", "--catalog", "--output", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("read from no machine", said, StringComparison.Ordinal);
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
