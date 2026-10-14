using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat overview says why a session's graph draws no relationship in the words the window says beneath its empty graph
/// (§6.8, R18), and its JSON carries the census those words are made from.
/// </summary>
public sealed class OverviewCensusTests
{
    [Fact(DisplayName = "R18: icat overview says why the graph draws no relationship, as the window does beneath it, and its JSON carries the census")]
    public async Task IcatOverviewSaysWhyTheGraphDrawsNone()
    {
        using var remote = new TemporarySession();
        Publish(remote.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 2)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 200 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 900, 300, 3)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 210 },
        ]);
        remote.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("overview", remote.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Paired TCP edges +0\r?$", text);
        Assert.Contains("No TCP connection was seen with a process of this session at each of its two ends. All 2 of its TCP "
            + "records belong to 1 connection to 1 other host. A process's connections are listed on its rung.",
            Regex.Replace(text, @"\s+", " "), Ordinal);

        (code, text, said) = await Run("overview", remote.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement tcp = JsonDocument.Parse(text).RootElement.GetProperty("bundle").GetProperty("census").GetProperty("tcp");
        Assert.Equal((2, 1, 1, 0), (tcp.GetProperty("toOtherHosts").GetInt64(), tcp.GetProperty("otherHostConnections").GetInt32(),
            tcp.GetProperty("otherHosts").GetInt32(), tcp.GetProperty("relationships").GetInt32()));

        // A session whose graph draws a relationship says nothing of why it would draw none.
        using var paired = new TemporarySession();
        Publish(paired.Store,
        [
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
        ]);
        paired.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("overview", paired.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Paired TCP edges +1\r?$", text);
        Assert.DoesNotContain("No TCP connection was seen", text, Ordinal);
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
