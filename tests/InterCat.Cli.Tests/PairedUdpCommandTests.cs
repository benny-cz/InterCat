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
/// The command line reads a paired UDP flow as the window does (§6.3, R18): icat overview counts it among the
/// relationships and channels, icat channels lists it as UDP with how it was paired and what the capture covered of each
/// transport, and icat evidence opens its records by its key.
/// </summary>
public sealed class PairedUdpCommandTests
{
    [Fact(DisplayName = "R18: icat overview, channels and evidence read a paired UDP flow as the window does")]
    public async Task TheCommandLineReadsAPairedUdpFlow()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\resolver.exe", SessionRelativeTicks = 110 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 4)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
            Datagram(30, ObservationKind.Send, AccountingSide.SendSide, 40, 100, 5).Between("127.0.0.1:61000", "127.0.0.1:53"),
            Datagram(31, ObservationKind.Receive, AccountingSide.ReceiveSide, 40, 200, 6).Between("127.0.0.1:61000", "127.0.0.1:53"),
            Datagram(32, ObservationKind.Send, AccountingSide.SendSide, 120, 200, 7).Between("127.0.0.1:53", "127.0.0.1:61000"),
            Datagram(33, ObservationKind.Receive, AccountingSide.ReceiveSide, 120, 100, 8).Between("127.0.0.1:53", "127.0.0.1:61000"),
        ]);
        Channel flow = SessionChannelQuery.Read(session.Store).Channels.Single(channel => channel.Mechanism == Mechanism.Udp);
        session.Store.ReleaseSegmentReaders();

        // The overview counts the flow among its relationships and paired channels.
        (InterCatExitCode code, string text, string said) = await Run("overview", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Relationships +2\r?$", text);
        Assert.Matches(@"(?m)^  Paired channels +2\r?$", text);
        Assert.Contains("Graph edges show paired TCP connection incarnations and UDP datagram flows", Regex.Replace(text, @"\s+", " "),
            Ordinal);

        // The channels name it UDP, say how it was paired - a flow no record opens or closes - and what the capture covered
        // of each transport.
        (code, text, said) = await Run("channels", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"  {flow.Key} · UDP · {flow.Name} · 4 observed records{Environment.NewLine}    "
            + ChannelPairingText.Explain(flow) + Environment.NewLine, text, Ordinal);
        Assert.Contains("A datagram flow, which no record opens or closes.", ChannelPairingText.Explain(flow), Ordinal);
        Assert.Contains("Admitted paired TCP and UDP channels", text, OrdinalIgnoreCase);
        Assert.Contains("TCP's coverage over the session is unknown", text, Ordinal);
        Assert.Contains("UDP's coverage over the session is unknown", text, Ordinal);

        // Its key opens its four datagrams at both ends.
        (code, text, said) = await Run("evidence", session.Path, "--channel", flow.Key);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Rows on page +4\r?$", text);
        Assert.Matches($@"(?m)^  Paired channel +{Regex.Escape(flow.Key)}\r?$", text);
    }

    private static ObservationRowV1 Datagram(long ticks, ObservationKind kind, AccountingSide side, long bytes, int owner, ulong ordinal) =>
        Transfer(ticks, kind, side, bytes, owner, ordinal) with
        {
            Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = ticks * 10,
        };

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
