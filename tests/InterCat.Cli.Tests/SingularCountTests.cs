using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// A count of one as icat says it (R5): "1 record", never "1 records" or "1 chunk(s)" - in a session's extent and rows, a
/// metric's answer, a package's rows, a compaction, a recording's chunks, a re-derivation, a journal release's boundaries,
/// an investigation's copies and candidates, and a capture profile's processes.
/// </summary>
public sealed class SingularCountTests : IDisposable
{
    /// <summary>A count of one said in the plural - "1 records", never "1 process" - or a plural hedged.</summary>
    private static readonly Regex OneInThePlural = new(@"(?<![\d,.\w-])1 (?!(of|across|was|has|does|is)\b)[a-z][a-z-]*[^s\W]s\b|\(s\)");

    private readonly TemporaryDirectory root = new();

    public void Dispose() => root.Dispose();

    [Fact(DisplayName = "R5: a session of one record says it in the singular: its extent, its rows, a metric's answer and its packages")]
    public async Task ASessionOfOneRecord()
    {
        string session = Path.Combine(root.Path, "one");
        Directory.CreateDirectory(session);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(session), Guid.NewGuid(), "singular");
        ObservationRowV1 row = Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)
            .Between("10.0.0.10:50000", "10.0.0.20:8080") with { SessionRelativeTicks = 10_000 };
        Publish(store, [row], fields: [Field(row, SourceField.ProcessStartSequence, 5)]);
        store.ReleaseSegmentReaders();

        (_, string output, _) = await Run("session", session);
        Assert.Matches(@"(?m)^  Durable extent +[\d,.]+ \S+, 1 record\r?$", output);
        Assert.Matches(@"(?m)^  The segment's 1 observation, in its own order:\r?$", output);
        Clean(output);

        (_, output, _) = await Run("metric", session, "--metric", "observations");
        Assert.Matches(@"(?m)^  Value +1 observation\r?$", output);
        Assert.Matches(@"(?m)^  Read +1 observation in 1 segment\r?$", output);
        Clean(output);

        (_, output, _) = await Run("package", session, "--redacted", "--output", Path.Combine(root.Path, "shared"));
        Assert.Matches(@"(?m)^  Rows +1 · 1 source field, \d+ redacted\r?$", output);
        Assert.Matches(@"(?m)^  Pseudonyms +\d+ names? · \d+ process (and|or) thread IDs? · \d+ address(es)? · \d+ ports? · "
            + @"\d+ identifiers? · \d+ providers?\r?$", output);
        Clean(output);
        (_, output, _) = await Run("package", session, "--original", "--output", Path.Combine(root.Path, "copy"), "--check");
        Assert.Matches(@"(?m)^  Rows +1 · 1 source field\r?$", output);
        Assert.Matches(@"(?m)^  Files +\d+ files, .+; 1 raw journal file of admitted records\r?$", output);
        Clean(output);
    }

    [Fact(DisplayName = "R5: a compaction counts what it coalesces, a single source field in the singular")]
    public async Task ACompactionCountsInTheSingular()
    {
        string session = Path.Combine(root.Path, "two");
        Directory.CreateDirectory(session);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(session), Guid.NewGuid(), "singular");
        ObservationRowV1 first = Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)
            .Between("10.0.0.10:50000", "10.0.0.20:8080") with { SessionRelativeTicks = 10_000 };
        Publish(store, [first], fields: [Field(first, SourceField.ProcessStartSequence, 5)]);
        Publish(store, [first, Transfer(110, ObservationKind.Send, AccountingSide.SendSide, 32, 100, 2)
            .Between("10.0.0.10:50000", "10.0.0.20:8080") with { SessionRelativeTicks = 11_000 }]);
        store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output, string said) = await Run("compact", session, "--check");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Would coalesce +2 publications: 3 rows and 1 source field, in \d+ files\r?$", output);
        Clean(output);
    }

    [Fact(DisplayName = "R5: a recording of two chunks of one record each says them in the singular: its newest chunk, its segments, the one boundary it allows")]
    public async Task ARecordingOfOneRecordChunks()
    {
        // One record a chunk: a burst of one record cannot be split between two chunks.
        string evidence = Path.Combine(root.Path, "evidence");
        Directory.CreateDirectory(evidence);
        _ = await EvidenceRecordings.RecordEvidence(evidence, ordinals: [1, 2], bursts: [1]);
        string session = Path.Combine(root.Path, "followed");
        (InterCatExitCode code, _, string said) = await Run("follow", evidence, session);
        Assert.True(code == InterCatExitCode.Success, said);

        (_, string output, _) = await Run("session", session);
        Assert.Matches(@"(?m)^  Journal +2 chunks of one recording, .+ in all\r?$", output);
        Assert.Matches(@"(?m)^  Newest chunk +\S+: [\d,.]+ \S+, 1 record\r?$", output);
        Clean(output);

        // A re-derivation names its segments as a compaction does.
        (code, output, said) = await Run("rederive", session);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Observation segments +1\r?$", output);
        Assert.Matches(@"(?m)^  Source-field segments +1\r?$", output);
        Clean(output);

        // Releasing the first chunk is the one boundary its journal allows, a single record, said once.
        (_, output, _) = await Run("retain", session, "--release-journal-before-record", "1");
        Assert.Matches(@"(?m)^  Boundaries it allows +1 record\r?$", output);
        Assert.DoesNotContain("1 to 1", output, StringComparison.Ordinal);
        Clean(output);
    }

    [Fact(DisplayName = "R5: a journal release that allows one boundary names it once, never as a range from it to itself")]
    public async Task AJournalReleaseNamesItsOneBoundary()
    {
        // Three records in batches of two: only releasing the first batch leaves admitted evidence behind.
        string session = Path.Combine(root.Path, "batches");
        Directory.CreateDirectory(session);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(session), Guid.NewGuid(), "singular");
        Publish(store, [.. Enumerable.Range(1, 3).Select(ordinal => Transfer(100 + ordinal, ObservationKind.Send, AccountingSide.SendSide, 64,
            100, (ulong)ordinal).Between("10.0.0.10:50000", "10.0.0.20:8080") with { SessionRelativeTicks = 10_000 * ordinal })],
            journalBatchRecords: 2);
        store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output, string said) = await Run("retain", session, "--release-journal-before-record", "1");
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Matches(@"(?m)^  Boundaries it allows +2 records\r?$", output);
        Assert.Contains("No batch of this journal ends before that boundary, so nothing would be released. The only boundary it "
            + "allows is 2 records.", output + said, StringComparison.Ordinal);
        Assert.DoesNotContain("2 to 2", output + said, StringComparison.Ordinal);
        Clean(output);
    }

    [Fact(DisplayName = "R5: an investigation of one session copies it in the singular, and refuses a candidate it does not have saying there are none")]
    public async Task AnInvestigationOfOneSession()
    {
        string session = Path.Combine(root.Path, "one");
        Directory.CreateDirectory(session);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(session), Guid.NewGuid(), "singular");
        Publish(store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)
            .Between("10.0.0.10:50000", "10.0.0.20:8080") with { SessionRelativeTicks = 10_000 }]);
        store.ReleaseSegmentReaders();
        string workspace = Path.Combine(root.Path, "case" + InterCat.Application.InvestigationWorkspace.Extension);
        Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "new", workspace)).Code);
        Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "add", workspace, session)).Code);

        (_, string output, _) = await Run("workspace", "package", workspace, "--output", Path.Combine(root.Path, "case.zip"), "--check");
        Assert.Matches(@"(?m)^  Copies +1 of 1 session\r?$", output);
        Clean(output);

        (InterCatExitCode code, output, string said) = await Run("workspace", "join", workspace, "1", "--accept");
        Assert.NotEqual(InterCatExitCode.Success, code);
        Assert.Empty(output);
        Assert.Contains("There are 0 candidates now, so there is no candidate 1; icat workspace correlate lists them.", said,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R5: a capture profile names the process it is asked to keep, or the processes, and counts what it compiled")]
    public async Task AProfileNamesItsProcesses()
    {
        (_, string output, string said) = await Run("profiles", "focused-transport", "--mechanism", "tcp", "--pid", "4242");
        string all = output + said;
        Assert.Contains(" only for PID 4242 while preserving required context", all, StringComparison.Ordinal);
        Assert.Matches(@"Compiled \d+ source plans? and \d+ provider requests?\.", all);
        Clean(all);

        (_, output, said) = await Run("profiles", "focused-transport", "--mechanism", "tcp", "--pid", "4242", "--pid", "4343");
        Assert.Contains(" only for PIDs 4242, 4343 while preserving required context", output + said, StringComparison.Ordinal);
        Clean(output + said);
    }

    /// <summary>Asserts that no count of one is said in the plural, and no plural is hedged.</summary>
    private static void Clean(string text)
    {
        Match found = OneInThePlural.Match(text);
        Assert.False(found.Success, $"'{found.Value}' in:\n{text}");
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
