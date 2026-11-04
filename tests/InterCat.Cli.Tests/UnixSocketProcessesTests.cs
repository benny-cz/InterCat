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

    [Fact(DisplayName = "R18: a process's Unix-domain socket records drill down exactly - the icat evidence command icat processes names lists each one it counts, of that process and mechanism alone, with its bytes and its source record")]
    public async Task AProcesssUnixSocketRecordsDrillDownExactly()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\server.exe" },
            Lifecycle(2, ObservationKind.Create, 500, 2) with { ResourceName = @"C:\Tools\other.exe" },
            Unix(10, ObservationKind.Open, AccountingSide.EndpointActivity, null) with
            {
                ByteDomain = null,
                AccountingSide = null,
                MeasurementUnit = null,
            },
            Unix(20, ObservationKind.Send, AccountingSide.SendSide, 120),
            Unix(30, ObservationKind.Receive, AccountingSide.ReceiveSide, 96),

            // The same process's TCP send, and another process's AF_UNIX send: neither is one of its Unix-domain socket records.
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 50, 400),
            Unix(50, ObservationKind.Send, AccountingSide.SendSide, 70, owner: 500),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Unix sockets +3 records of its AF_UNIX sockets", output);
        System.Text.RegularExpressions.Match command = System.Text.RegularExpressions.Regex.Match(output,
            @"(?m)^    icat evidence ""(?<path>[^""]+)"" --owner-process (?<instance>[0-9a-f]{32}) --mechanism unix-domain-socket\r?$");
        Assert.True(command.Success, output);
        Assert.Equal(session.Path, command.Groups["path"].Value);

        // The command it names lists the three it counted, and no other: each of that process, a Unix-domain socket's, with
        // the bytes its sent and received count beside its TCP send's 50, and the raw record it was admitted from.
        (code, string page) = await Run("evidence", session.Path, "--owner-process", command.Groups["instance"].Value,
            "--mechanism", "unix-domain-socket", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using System.Text.Json.JsonDocument listed = System.Text.Json.JsonDocument.Parse(page);
        System.Text.Json.JsonElement[] records = [.. listed.RootElement.GetProperty("page").GetProperty("records").EnumerateArray()];
        Assert.Equal(System.Text.Json.JsonValueKind.Null, listed.RootElement.GetProperty("page").GetProperty("nextCursor").ValueKind);
        Assert.Equal([10UL, 20UL, 30UL], records.Select(record => record.GetProperty("observationId").GetProperty("rawRecordId")
            .GetProperty("recordOrdinal").GetUInt64()));
        Assert.All(records, record =>
        {
            Assert.Equal(Guid.Parse(command.Groups["instance"].Value), record.GetProperty("owner").GetProperty("instance").GetProperty("value").GetGuid());
            Assert.Equal("UnixDomainSocket", record.GetProperty("observation").GetProperty("mechanism").GetString());
            Assert.Equal(record.GetProperty("observation").GetProperty("rawRecordOrdinal").GetUInt64(),
                record.GetProperty("observationId").GetProperty("rawRecordId").GetProperty("recordOrdinal").GetUInt64());
            Assert.StartsWith("seg-", record.GetProperty("segmentName").GetString(), StringComparison.Ordinal);
        });
        long Bytes(string kind) => records.Where(record => record.GetProperty("observation").GetProperty("kind").GetString() == kind)
            .Sum(record => record.GetProperty("observation").GetProperty("byteValue").GetInt64());
        Assert.Equal((120L, 96L), (Bytes("Send"), Bytes("Receive")));
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(output, @"(?m)^  400 +server\.exe +.+ +170 B +96 B\r?$"), output);
    }

    /// <summary>A Unix-domain socket row, as Winsock AFD's gate admits one: no endpoint, its bytes the transport's.</summary>
    private static ObservationRowV1 Unix(long ticks, ObservationKind kind, AccountingSide side, long? bytes, int owner = 400) =>
        Transfer(ticks, kind, side, bytes, owner) with
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
