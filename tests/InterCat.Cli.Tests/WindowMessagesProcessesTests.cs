using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// A process's window messages in icat processes (ADR-088): its records of the messages it sent, posted and retrieved - each
/// end raised in it, by its message's number in the capture - are said apart, as naming no window, message or size, and
/// drill down exactly to each record with its source record.
/// </summary>
public sealed class WindowMessagesProcessesTests
{
    [Fact(DisplayName = "R18: a process's window message records drill down exactly - the icat evidence command icat processes names lists each one it counts, of that process and mechanism alone, with its source record")]
    public async Task AProcesssWindowMessageRecordsDrillDownExactly()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\sender.exe" },
            Lifecycle(2, ObservationKind.Create, 500, 2) with { ResourceName = @"C:\Tools\receiver.exe" },

            // The sender's ends: a send's start, which names the receiver, and its stop, and a post's queueing.
            WindowMessage(10, 60, raisedBy: 400, 10),
            WindowMessage(20, 70, raisedBy: 400, 11),
            WindowMessage(30, 59, raisedBy: 400, 12),

            // The receiver's ends of the same messages - the sent one's retrieval, its start and stop, and the post's - and a
            // stranger's post: none is the sender's record.
            WindowMessage(12, 62, raisedBy: 500, 13),
            WindowMessage(18, 83, raisedBy: 500, 14),
            WindowMessage(35, 61, raisedBy: 500, 15),
            WindowMessage(40, 59, raisedBy: 600, 16),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Window messages +3 records of the window messages it sent, posted and retrieved, each end by its "
            + @"message's number in the capture; none names the window, the message or a size\r?$", output);
        Match command = Regex.Match(output,
            @"(?m)^    icat evidence ""(?<path>[^""]+)"" --owner-process (?<instance>[0-9a-f]{32}) --mechanism window-message\r?$");
        Assert.True(command.Success, output);
        Assert.Equal(session.Path, command.Groups["path"].Value);

        // The command it names lists the three it counted, and no other: each of that process and the mechanism - its send's
        // start and stop and its post's queueing, never the receiver's ends of the same messages or the stranger's post - with
        // the raw record it was admitted from.
        (code, string page) = await Run("evidence", session.Path, "--owner-process", command.Groups["instance"].Value,
            "--mechanism", "window-message", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument listed = JsonDocument.Parse(page);
        JsonElement[] records = [.. listed.RootElement.GetProperty("page").GetProperty("records").EnumerateArray()];
        Assert.Equal(JsonValueKind.Null, listed.RootElement.GetProperty("page").GetProperty("nextCursor").ValueKind);
        Assert.Equal([10UL, 11UL, 12UL], records.Select(record => record.GetProperty("observationId").GetProperty("rawRecordId")
            .GetProperty("recordOrdinal").GetUInt64()));
        Assert.All(records, record =>
        {
            Assert.Equal(Guid.Parse(command.Groups["instance"].Value), record.GetProperty("owner").GetProperty("instance").GetProperty("value").GetGuid());
            Assert.Equal("WindowMessage", record.GetProperty("observation").GetProperty("mechanism").GetString());
            Assert.Equal(record.GetProperty("observation").GetProperty("rawRecordOrdinal").GetUInt64(),
                record.GetProperty("observationId").GetProperty("rawRecordId").GetProperty("recordOrdinal").GetUInt64());
            Assert.StartsWith("seg-", record.GetProperty("segmentName").GetString(), StringComparison.Ordinal);
        });
        Assert.Equal(["RequestStart", "RequestEnd", "Send"], records.Select(record => record.GetProperty("observation").GetProperty("kind").GetString()));

        // The receiver's line counts its own three ends, raised in it, never the sender a send's start was raised in.
        (code, output) = await Run("processes", session.Path, "--pid", "500");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Window messages +3 records of the window messages it sent, posted and retrieved", output);
    }

    [Fact(DisplayName = "R18: icat processes gives a process with no window message record no Window messages line and no command to list any")]
    public async Task AProcessWithoutWindowMessageRecordsSaysNone()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\sender.exe" },
            Lifecycle(2, ObservationKind.Create, 500, 2) with { ResourceName = @"C:\Tools\receiver.exe" },
            WindowMessage(30, 59, raisedBy: 500, 12),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.DoesNotContain("Window messages", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--mechanism window-message", output, StringComparison.Ordinal);
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
