using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// A process's synchronization handles in icat processes (ADR-084): its records of events', mutants' and semaphores' handles
/// - each it made, was given or closed, and each it still held at the stop - are said apart, as no signal, wait or byte, and
/// drill down exactly to each record with its source record.
/// </summary>
public sealed class SyncObjectsProcessesTests
{
    [Fact(DisplayName = "R18: a process's synchronization records drill down exactly - the icat evidence command icat processes names lists each one it counts, of that process and mechanism alone, with its source record")]
    public async Task AProcesssSynchronizationRecordsDrillDownExactly()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\creator.exe" },
            Lifecycle(2, ObservationKind.Create, 500, 2) with { ResourceName = @"C:\Tools\opener.exe" },

            // Its handle's creation and close, raised in it; a handle the other process duplicated into it, whose record names
            // it; and a handle it still held at the stop, which the rundown raised elsewhere names its.
            Handle(10, HandleCreated, raisedBy: 400, owner: null, 10),
            Handle(20, HandleDuplicated, raisedBy: 500, owner: 400, 11),
            Handle(30, HandleClosed, raisedBy: 400, owner: null, 12),
            Handle(40, HandleAtStop, raisedBy: 4, owner: 400, 13),

            // The other process's own handle, and the stop's type rundown, which names no process: neither is its record.
            Handle(15, HandleCreated, raisedBy: 500, owner: null, 14),
            Handle(45, TypeAtStop, raisedBy: 4, owner: null, 15),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Synchronization +4 records of its handles to events, mutants and semaphores, each by its object's number "
            + @"in the capture; none is a signal, a wait or a byte\r?$", output);
        Match command = Regex.Match(output,
            @"(?m)^    icat evidence ""(?<path>[^""]+)"" --owner-process (?<instance>[0-9a-f]{32}) --mechanism synchronization\r?$");
        Assert.True(command.Success, output);
        Assert.Equal(session.Path, command.Groups["path"].Value);

        // The command it names lists the four it counted, and no other: each of that process and the mechanism - its creation,
        // the handle duplicated into it, its close and its handle at the stop, never the other process's handle or the type
        // rundown - with the raw record it was admitted from.
        (code, string page) = await Run("evidence", session.Path, "--owner-process", command.Groups["instance"].Value,
            "--mechanism", "synchronization", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument listed = JsonDocument.Parse(page);
        JsonElement[] records = [.. listed.RootElement.GetProperty("page").GetProperty("records").EnumerateArray()];
        Assert.Equal(JsonValueKind.Null, listed.RootElement.GetProperty("page").GetProperty("nextCursor").ValueKind);
        Assert.Equal([10UL, 11UL, 12UL, 13UL], records.Select(record => record.GetProperty("observationId").GetProperty("rawRecordId")
            .GetProperty("recordOrdinal").GetUInt64()));
        Assert.All(records, record =>
        {
            Assert.Equal(Guid.Parse(command.Groups["instance"].Value), record.GetProperty("owner").GetProperty("instance").GetProperty("value").GetGuid());
            Assert.Equal("Synchronization", record.GetProperty("observation").GetProperty("mechanism").GetString());
            Assert.Equal(record.GetProperty("observation").GetProperty("rawRecordOrdinal").GetUInt64(),
                record.GetProperty("observationId").GetProperty("rawRecordId").GetProperty("recordOrdinal").GetUInt64());
            Assert.StartsWith("seg-", record.GetProperty("segmentName").GetString(), StringComparison.Ordinal);
        });
        Assert.Equal(["Open", "Open", "Close", "Inventory"], records.Select(record => record.GetProperty("observation").GetProperty("kind").GetString()));
    }

    [Fact(DisplayName = "R18: icat processes gives a process with no synchronization record no Synchronization line and no command to list any")]
    public async Task AProcessWithoutSynchronizationRecordsSaysNone()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\creator.exe" },
            Lifecycle(2, ObservationKind.Create, 500, 2) with { ResourceName = @"C:\Tools\opener.exe" },
            Handle(15, HandleCreated, raisedBy: 500, owner: null, 14),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.DoesNotContain("Synchronization", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--mechanism synchronization", output, StringComparison.Ordinal);
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
