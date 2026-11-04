using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// A process's Unix-domain sockets in icat processes (ADR-076): their bytes are what the socket's transport carried, so
/// they are its sent and received as a TCP or UDP record's are; their records are said apart, with no other end, as
/// AF_UNIX names none (ADR-075).
/// </summary>
public sealed class UnixSocketProcessesTests
{
    [Fact(DisplayName = "R21: icat processes counts a process's Unix-domain socket bytes as its sent and received, says its records apart, and names no other end for them")]
    public async Task AProcesssUnixSocketsAreItsTraffic()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\server.exe" },
            Unix(10, ObservationKind.Open, AccountingSide.EndpointActivity, null) with
            {
                ByteDomain = null,
                AccountingSide = null,
                MeasurementUnit = null,
            },
            Unix(20, ObservationKind.Send, AccountingSide.SendSide, 120),
            Unix(30, ObservationKind.Receive, AccountingSide.ReceiveSide, 96),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(output, @"(?m)^  400 +server\.exe +.+ +4 +.+ +120 B +96 B\r?$"), output);
        Assert.Matches(@"(?m)^  Unix sockets +3 records of its AF_UNIX sockets, their bytes in its sent and received; AF_UNIX names "
            + @"no socket's other end, so theirs is unresolved\r?$", output);

        // Their other end is unresolved, as relations-v1 leaves every mechanism no relation rule covers, never a peer guessed.
        Assert.Matches(@"(?m)^  unresolved: no relation rule covers this mechanism yet +120 B +96 B\r?$", output);
        Assert.DoesNotContain("It made no TCP or UDP send or receive record", output, StringComparison.Ordinal);
    }

    /// <summary>A Unix-domain socket row of PID 400, as Winsock AFD's gate admits one: no endpoint, its bytes the transport's.</summary>
    private static ObservationRowV1 Unix(long ticks, ObservationKind kind, AccountingSide side, long? bytes) =>
        Transfer(ticks, kind, side, bytes, 400) with
        {
            FactKey = FactKey.Create("unix-socket-operation"),
            Mechanism = Mechanism.UnixDomainSocket,
            Direction = kind switch
            {
                ObservationKind.Send => Direction.Outbound,
                ObservationKind.Receive => Direction.Inbound,
                _ => Direction.DirectionNotApplicable,
            },
            ByteAvailability = bytes is null ? FieldAvailability.NotApplicable : FieldAvailability.Present,
        };

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
