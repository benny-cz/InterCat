using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// The baseline's build validation backlog (P27): icat bench may say the fixture corpus ran on a build only where the
/// coverage the product carries holds a tier measured there for every mechanism the corpus measures.
/// </summary>
public sealed class BuildBacklogTests
{
    [Fact(DisplayName = "P27: icat bench calls the fixture corpus run on a build only where every mechanism the corpus measures carries a tier measured on it - 26220 and 26320 - and lists every other build in the matrix as owing it")]
    public async Task TheCorpusIsSaidRunOnlyWhereItsEvidenceIs()
    {
        (InterCatExitCode code, string output, string said) = await Run("bench", "--no-storage-probe", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        using JsonDocument baseline = JsonDocument.Parse(output);
        JsonElement[] backlog = [.. baseline.RootElement.GetProperty("buildBacklog").EnumerateArray()];
        Assert.Equal(SupportedBuilds.All.Select(build => build.BuildNumber), backlog.Select(entry => entry.GetProperty("buildNumber").GetInt32()));

        int[] measured = [.. backlog.Where(entry => entry.GetProperty("corpusMeasured").GetBoolean()).Select(entry => entry.GetProperty("buildNumber").GetInt32())];
        Assert.Equal([26220, 26320], measured);

        // Each build said to be measured carries a tier for every mechanism any build's fixtures measured, from runs on it.
        Mechanism[] corpus = [.. MeasuredCoverage.Entries.Select(entry => entry.Mechanism).Distinct().Order()];
        Assert.InRange(corpus.Length, 11, int.MaxValue);
        foreach (int build in measured)
        {
            Assert.Equal(corpus, MeasuredCoverage.Entries.Where(entry => BuildNumber(entry.Build) == build)
                .Select(entry => entry.Mechanism).Distinct().Order());
        }

        // A measured build's evidence says where to check it; every other build owes the corpus, in words.
        Assert.All(backlog, entry => Assert.Equal(entry.GetProperty("corpusMeasured").GetBoolean(),
            entry.GetProperty("evidence").GetString()!.Contains("Evidence: fixtures/", StringComparison.Ordinal)));
        Assert.All(backlog.Where(entry => !entry.GetProperty("corpusMeasured").GetBoolean()), entry => Assert.StartsWith(
            "The fixture corpus of section 13.4 has not been run on this build.", entry.GetProperty("evidence").GetString(), StringComparison.Ordinal));
    }

    private static int BuildNumber(string build) => int.Parse(build.Split('.')[2], CultureInfo.InvariantCulture);

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
