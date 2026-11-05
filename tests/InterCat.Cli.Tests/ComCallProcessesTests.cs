using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// A process's COM calls in icat processes (ADR-079): its COM call records, of the calls it made and those it served, are
/// said apart from its RPC calls, which carry them, and drill down exactly to each record with its source record.
/// </summary>
public sealed class ComCallProcessesTests
{
    private static readonly Guid Dispatch = new("00020400-0000-0000-c000-000000000046");
    private static readonly Guid Made = new("8d1e1a6e-5a4b-4f43-9d6f-1c2a3b4c5d6e");
    private static readonly Guid Served = new("2b7c9e10-0f3d-4e5a-8b6c-7d8e9f0a1b2c");

    [Fact(DisplayName = "R18: a process's COM call records drill down exactly - the icat evidence command icat processes names lists each one it counts, of the calls it made and served, of that process and mechanism alone, with its source record")]
    public async Task AProcesssComCallRecordsDrillDownExactly()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(2, ObservationKind.Create, 500, 2) with { ResourceName = @"C:\Tools\server.exe" },

            // A call it made through IDispatch, carried by its RPC call of the same activity, and served in the other process.
            RpcCall(10, ObservationKind.RequestStart, Direction.Outbound, 400, 10, Made, Dispatch),
            ComCall(11, ObservationKind.RequestStart, Direction.Outbound, 400, 11, Made, Dispatch),
            ComCall(14, ObservationKind.RequestStart, Direction.Inbound, 500, 12, Made, Dispatch),
            ComCall(15, ObservationKind.RequestEnd, Direction.Inbound, 500, 13, Made, hresult: 0),
            ComCall(16, ObservationKind.RequestEnd, Direction.Outbound, 400, 14, Made, hresult: 0),
            RpcCall(17, ObservationKind.RequestEnd, Direction.Outbound, 400, 15, Made, status: 0),

            // And a call it served, the other process's client end not recorded.
            ComCall(20, ObservationKind.RequestStart, Direction.Inbound, 400, 16, Served, Dispatch),
            ComCall(21, ObservationKind.RequestEnd, Direction.Inbound, 400, 17, Served, hresult: 0),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  RPC +2 call records; icat operations <directory> --pid 400 lists its calls", output);
        Assert.Matches(@"(?m)^  COM calls +4 records of the COM calls it made and served, each annotating the RPC call that carries it, "
            + @"whose other end icat operations names\r?$", output);
        Match command = Regex.Match(output,
            @"(?m)^    icat evidence ""(?<path>[^""]+)"" --owner-process (?<instance>[0-9a-f]{32}) --mechanism com-call\r?$");
        Assert.True(command.Success, output);
        Assert.Equal(session.Path, command.Groups["path"].Value);

        // The command it names lists the four it counted, and no other: each of that process, a COM call's - the start and
        // stop of the call it made and of the one it served, never its RPC call records or the other process's - with the raw
        // record it was admitted from.
        (code, string page) = await Run("evidence", session.Path, "--owner-process", command.Groups["instance"].Value,
            "--mechanism", "com-call", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument listed = JsonDocument.Parse(page);
        JsonElement[] records = [.. listed.RootElement.GetProperty("page").GetProperty("records").EnumerateArray()];
        Assert.Equal(JsonValueKind.Null, listed.RootElement.GetProperty("page").GetProperty("nextCursor").ValueKind);
        Assert.Equal([11UL, 14UL, 16UL, 17UL], records.Select(record => record.GetProperty("observationId").GetProperty("rawRecordId")
            .GetProperty("recordOrdinal").GetUInt64()));
        Assert.All(records, record =>
        {
            Assert.Equal(Guid.Parse(command.Groups["instance"].Value), record.GetProperty("owner").GetProperty("instance").GetProperty("value").GetGuid());
            Assert.Equal("ComCall", record.GetProperty("observation").GetProperty("mechanism").GetString());
            Assert.Equal(record.GetProperty("observation").GetProperty("rawRecordOrdinal").GetUInt64(),
                record.GetProperty("observationId").GetProperty("rawRecordId").GetProperty("recordOrdinal").GetUInt64());
            Assert.StartsWith("seg-", record.GetProperty("segmentName").GetString(), StringComparison.Ordinal);
        });
        Assert.Equal(["Outbound", "Outbound", "Inbound", "Inbound"], records.Select(record => record.GetProperty("observation").GetProperty("direction").GetString()));
    }

    [Fact(DisplayName = "R18: icat processes gives a process with no COM call record no COM calls line and no command to list any")]
    public async Task AProcessWithoutComCallsSaysNone()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\client.exe" },
            RpcCall(10, ObservationKind.RequestStart, Direction.Outbound, 400, 10, Made, Dispatch),
            RpcCall(17, ObservationKind.RequestEnd, Direction.Outbound, 400, 11, Made, status: 0),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.DoesNotContain("COM calls", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--mechanism com-call", output, StringComparison.Ordinal);
    }

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
