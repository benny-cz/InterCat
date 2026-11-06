using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// A process's sections in icat processes (ADR-092's fourth addendum): its records of its handles to sections and of the
/// views it mapped, unmapped and held at the capture's start and stop - each by its section's number in the capture - are
/// said apart, as no byte moved through a view, and drill down exactly to each record with its source record.
/// </summary>
public sealed class SectionsProcessesTests
{
    [Fact(DisplayName = "R18: a process's section records drill down exactly - the icat evidence command icat processes names lists each one it counts, its handles and its views, a view the stop's rundown names it holding among them though raised elsewhere, of that process and mechanism alone, with its source record")]
    public async Task AProcesssSectionRecordsDrillDownExactly()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\creator.exe" },
            Lifecycle(2, ObservationKind.Create, 500, 2) with { ResourceName = @"C:\Tools\opener.exe" },

            // The creator's: its handle's creation and close, raised in it and naming no owner; its view's mapping and
            // unmapping, naming it; and its view at the stop, which the rundown names it holding, though raised in another process.
            SectionRecord(10, HandleCreated, raisedBy: 400, owner: null, 10),
            SectionRecord(20, 37, raisedBy: 400, owner: 400, 11, view: true),
            SectionRecord(40, 38, raisedBy: 400, owner: 400, 12, view: true),
            SectionRecord(45, HandleClosed, raisedBy: 400, owner: null, 13),
            SectionRecord(50, 40, raisedBy: 500, owner: 400, 14, view: true),

            // The opener's handle and view of the same section, and a view the rundown raised in the creator names another
            // process holding: none is the creator's record.
            SectionRecord(25, HandleCreated, raisedBy: 500, owner: null, 15),
            SectionRecord(30, 37, raisedBy: 500, owner: 500, 16, view: true),
            SectionRecord(35, HandleClosed, raisedBy: 500, owner: null, 17),
            SectionRecord(55, 40, raisedBy: 400, owner: 600, 18, view: true),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Sections +5 records of its section handles and of the views it mapped, unmapped and held at the "
            + @"capture's start and stop, each by its section's number in the capture; none is a byte moved through a view\r?$", output);
        Match command = Regex.Match(output,
            @"(?m)^    icat evidence ""(?<path>[^""]+)"" --owner-process (?<instance>[0-9a-f]{32}) --mechanism shared-section\r?$");
        Assert.True(command.Success, output);
        Assert.Equal(session.Path, command.Groups["path"].Value);

        // The command it names lists the five it counted, and no other: each of that process and the mechanism - its handle's
        // creation and close, its view's mapping and unmapping, and its view at the stop - never the opener's handle and view
        // of the same section or another process's view a rundown raised in it, each with the raw record it was admitted from.
        (code, string page) = await Run("evidence", session.Path, "--owner-process", command.Groups["instance"].Value,
            "--mechanism", "shared-section", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument listed = JsonDocument.Parse(page);
        JsonElement[] records = [.. listed.RootElement.GetProperty("page").GetProperty("records").EnumerateArray()];
        Assert.Equal(JsonValueKind.Null, listed.RootElement.GetProperty("page").GetProperty("nextCursor").ValueKind);
        Assert.Equal([10UL, 11UL, 12UL, 13UL, 14UL], records.Select(static record => record.GetProperty("observationId").GetProperty("rawRecordId")
            .GetProperty("recordOrdinal").GetUInt64()));
        Assert.All(records, record =>
        {
            Assert.Equal(Guid.Parse(command.Groups["instance"].Value), record.GetProperty("owner").GetProperty("instance").GetProperty("value").GetGuid());
            Assert.Equal("SharedSection", record.GetProperty("observation").GetProperty("mechanism").GetString());
            Assert.Equal(record.GetProperty("observation").GetProperty("rawRecordOrdinal").GetUInt64(),
                record.GetProperty("observationId").GetProperty("rawRecordId").GetProperty("recordOrdinal").GetUInt64());
            Assert.StartsWith("seg-", record.GetProperty("segmentName").GetString(), StringComparison.Ordinal);
        });
        Assert.Equal(["Open", "Map", "Unmap", "Close", "Inventory"], records.Select(static record => record.GetProperty("observation").GetProperty("kind").GetString()));

        // The opener's line counts its own three, raised in it, never the creator's view the stop's rundown it raised names.
        (code, output) = await Run("processes", session.Path, "--pid", "500");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Sections +3 records of its section handles and of the views it mapped", output);
    }

    [Fact(DisplayName = "R18: icat processes gives a process with no section record no Sections line and no command to list any")]
    public async Task AProcessWithoutSectionRecordsSaysNone()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\creator.exe" },
            Lifecycle(2, ObservationKind.Create, 500, 2) with { ResourceName = @"C:\Tools\opener.exe" },
            SectionRecord(30, 37, raisedBy: 500, owner: 500, 12, view: true),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.DoesNotContain("Sections", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--mechanism shared-section", output, StringComparison.Ordinal);
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
