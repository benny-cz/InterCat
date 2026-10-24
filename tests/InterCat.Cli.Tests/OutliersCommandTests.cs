using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat outliers (`contracts/outliers-v1.md`, plan M12's optional outlier detection): an interval's rows judged against
/// the windows of its baseline, each as long as the interval, with the baseline, its windows and how often chance alone
/// would put a row outside their range; what is not judged and why; and what it refuses.
/// </summary>
public sealed class OutliersCommandTests
{
    [Fact(DisplayName = "R18: icat outliers lists the rows above or below every window of their baseline, with the baseline, its windows and how often chance alone would")]
    public async Task IcatOutliersListsTheRowsOutsideTheirWindows()
    {
        using var session = Session();
        (InterCatExitCode code, string text, string said) = await Run("outliers", session.Path, "--interval", "1000:1050");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("OUTLIERS", text, Ordinal);
        Assert.Matches(@"(?m)^  Baseline +\[0\.000, 0\.100\) ms, 0\.100 ms long, as 20 windows of 5\.0 µs, end to end\r?$", text);
        Assert.Matches(@"(?m)^  Rows +5 rows judged · 2 above every window of their baseline and 1 below every one\r?$", text);
        Assert.Equal(
            [
                "PID 300 and PID 400 | TCP | 50 | 10 to 12, median 10 | 38 more than the most | above every window of its baseline",
                "PID 500 and PID 600 | TCP | 0 | 10 to 12, median 10 | 10 fewer than the fewest | below every window of its baseline",
                "PID 700 and PID 800 | TCP | 4 | 0 in each | 4 more than the most | above every window of its baseline",
            ],
            Rows(text, "OUTLIERS", "Relationship"));
        string flowed = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("chance alone would put a row above every one of them at most 1 time in 21", flowed, Ordinal);
        Assert.Contains("1 row not judged: The capture collected UDP", flowed, Ordinal);
        Assert.Contains(OutlierText.Note, flowed, Ordinal);

        // A baseline named is judged as the one taken without it.
        (code, string named, said) = await Run("outliers", session.Path, "--interval", "1000:1050", "--baseline", "0:1000");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(Rows(text, "OUTLIERS", "Relationship"), Rows(named, "OUTLIERS", "Relationship"));

        // --limit lists as many as asked, saying how many more there are.
        (code, text, said) = await Run("outliers", session.Path, "--interval", "1000:1050", "--limit", "1");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Single(Rows(text, "OUTLIERS", "Relationship"));
        Assert.Contains("2 more, fewer records outside their windows' range; --limit 3 lists every one.", text, Ordinal);

        // The JSON: the baseline and its windows, what was judged and found, and each row listed with its windows' records.
        (code, text, said) = await Run("outliers", session.Path, "--interval", "1000:1050", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("outliers-v1", 20, 21, 6, 5), (root.GetProperty("contract").GetString(), root.GetProperty("windows").GetArrayLength(),
            root.GetProperty("chance").GetInt32(), root.GetProperty("rows").GetInt32(), root.GetProperty("judged").GetInt32()));
        Assert.Equal((2, 1, 3, 3), (root.GetProperty("above").GetInt32(), root.GetProperty("below").GetInt32(),
            root.GetProperty("outliers").GetInt32(), root.GetProperty("listed").GetArrayLength()));
        Assert.Equal((950L, 1_000L), (root.GetProperty("windows")[19].GetProperty("startTicks").GetInt64(),
            root.GetProperty("windows")[19].GetProperty("endTicks").GetInt64()));
        JsonElement notJudged = Assert.Single(root.GetProperty("notJudged").EnumerateArray());
        Assert.Equal(1, notJudged.GetProperty("rows").GetInt32());
        Assert.StartsWith("The capture collected UDP", notJudged.GetProperty("reason").GetString(), Ordinal);
        JsonElement burst = root.GetProperty("listed")[0];
        Assert.Equal((1, "Above", 300, 400, 50L, 202L), (burst.GetProperty("rank").GetInt32(), burst.GetProperty("side").GetString(),
            burst.GetProperty("source").GetProperty("processId").GetInt32(), burst.GetProperty("target").GetProperty("processId").GetInt32(),
            burst.GetProperty("comparedRecords").GetInt64(), burst.GetProperty("baselineRecords").GetInt64()));
        Assert.Equal((10L, 10L, 12L, 38L), (burst.GetProperty("fewest").GetInt64(), burst.GetProperty("median").GetInt64(),
            burst.GetProperty("most").GetInt64(), burst.GetProperty("beyond").GetInt64()));
        Assert.Equal([12L, .. Enumerable.Repeat(10L, 19)], burst.GetProperty("windowRecords").EnumerateArray().Select(window => window.GetInt64()));
        Assert.Equal(("Processes", "Tcp", "Unobserved"), (burst.GetProperty("kind").GetString(), burst.GetProperty("mechanism").GetString(),
            burst.GetProperty("connected").GetString()));
        Assert.Equal(("Below", 2), (root.GetProperty("listed")[1].GetProperty("side").GetString(), root.GetProperty("listed")[1].GetProperty("rank").GetInt32()));
        (code, text, said) = await Run("outliers", session.Path, "--interval", "1000:1050", "--json", "--limit", "1");
        Assert.True(code == InterCatExitCode.Success, said);
        root = JsonDocument.Parse(text).RootElement;
        Assert.Equal((3, 1), (root.GetProperty("outliers").GetInt32(), root.GetProperty("listed").GetArrayLength()));

        // Within every window's range, nothing is listed and it says so; by executable, a process of no witnessed executable
        // is in no row, and what is unmatched is said apart.
        (code, text, said) = await Run("outliers", session.Path, "--interval", "950:1000");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Rows +5 rows judged · none above or below every window\r?$", text);
        Assert.Contains("No row is above or below every window of its baseline.", text, Ordinal);
        (code, text, said) = await Run("outliers", session.Path, "--interval", "1000:1050", "--group-by", "executable");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Rows +no row judged\r?$", text);
        flowed = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("a row per executable, each one's processes together by the path their image was witnessed at", flowed, Ordinal);
        Assert.Contains("No relationship between executables, and no executable's connections to another host, has a record in "
            + "either interval, so none is judged.", flowed, Ordinal);
        Assert.Contains("Processes whose executable was not witnessed are in no row", flowed, Ordinal);
    }

    [Fact(DisplayName = "R21: icat outliers says why it judges nothing, and refuses a baseline it cannot judge by in words")]
    public async Task IcatOutliersRefusesWhatItCannotJudge()
    {
        using var session = Session();
        foreach ((string[] args, string refusal) in new (string[], string)[]
        {
            (["--interval", "1000:1050", "--baseline", "0:1001"], ComparisonOutliers.Overlapping),
            (["--interval", "1000:1050", "--baseline", "0:900"], "The baseline holds only 18 windows as long as the compared interval, "
                + "and at least 19 are needed"),
            (["--interval", "0:50"], "Nothing the session keeps is before --interval, so it has no baseline unless --baseline names one."),
            (["--baseline", "0:1000"], "--interval is required: start:end"),
            (["--interval", "1000:1050", "--baseline", "10:5"], "--baseline names no interval: start:end"),
            (["--interval", "1000:1050", "--group-by", "host"], "--group-by judges process or executable rows; 'host' is neither."),
            (["--interval", "1000:1050", "--limit", "0"], "--limit expects a number of rows of 1 or more; '0' is not one."),
            (["--interval", "1000:1050", "--evidence-policy", "2"], "--evidence-policy expects one of: DirectOnly, IncludeCorrelated, "
                + "IncludeCandidates, AllIncludingConflicting. '2' is not one."),
        })
        {
            (InterCatExitCode refused, string output, string why) = await Run(["outliers", session.Path, .. args]);
            Assert.Equal(InterCatExitCode.InvalidInvocation, refused);
            Assert.Equal(string.Empty, output);
            Assert.Contains(refusal, Regex.Replace(why, @"\s+", " "), Ordinal);
        }

        (InterCatExitCode missing, _, string none) = await Run("outliers", "--interval", "1000:1050");
        Assert.Equal(InterCatExitCode.InvalidInvocation, missing);
        Assert.Contains("A session directory is required: icat outliers <directory>.", none, Ordinal);
        (InterCatExitCode help, string usage, _) = await Run("outliers", "--help");
        Assert.Equal(InterCatExitCode.Success, help);
        Assert.Contains("icat outliers <session-directory> --interval <start:end> [--baseline <start:end>]", usage, Ordinal);

        // A session with no relationship judges nothing, and says so.
        using var empty = new TemporarySession();
        Publish(empty.Store, [Lifecycle(1, ObservationKind.Inventory, 100, 1) with { SessionRelativeTicks = 100 }],
            coverage: TransportLedger(tcp: true, udp: true));
        empty.Store.ReleaseSegmentReaders();
        (InterCatExitCode code, string text, string said) = await Run("outliers", empty.Path, "--interval", "1000:1050");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("No relationship the overview draws, and no process's connections to another host, has a record in either "
            + "interval, so none is judged.", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.DoesNotContain("Were the compared interval like", text, Ordinal);

        // A session whose only relationship its two intervals did not collect alike judges none, and says why.
        using var otherwise = new TemporarySession();
        Publish(otherwise.Store, [.. OutlierRows().Where(row => row.Mechanism == Mechanism.Udp)], coverage: OutlierCoverage());
        otherwise.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("outliers", otherwise.Path, "--interval", "1000:1050");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Rows +no row judged\r?$", text);
        string flowed = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("No row is judged: none was covered and collected alike over both intervals.", flowed, Ordinal);
        Assert.Contains("1 row not judged: The capture collected UDP", flowed, Ordinal);
    }

    private static TemporarySession Session()
    {
        var temporary = new TemporarySession();
        Publish(temporary.Store, OutlierRows(), coverage: OutlierCoverage());
        temporary.Store.ReleaseSegmentReaders();
        return temporary;
    }

    /// <summary>The rows of the table beneath <paramref name="heading"/> whose header starts with <paramref name="first"/>, each cell joined by " | ".</summary>
    private static string[] Rows(string text, string heading, string first)
    {
        string[] lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        int start = Array.FindIndex(lines, line => line.StartsWith(heading, Ordinal));
        Assert.True(start >= 0, $"No heading {heading}.");
        int header = Array.FindIndex(lines, start, line => line.StartsWith("  " + first + " ", Ordinal));
        Assert.True(header >= 0, $"No table under {heading}.");
        return [.. lines.Skip(header + 1).TakeWhile(line => line.StartsWith("  ", Ordinal) && Regex.IsMatch(line, @"\S\s{2,}\S"))
            .Select(line => string.Join(" | ", Regex.Split(line.Trim(), @"\s{2,}")))];
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
