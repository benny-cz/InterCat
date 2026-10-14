using System.Globalization;
using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat retain as a person runs it (S5): a journal prefix release says what it gives up, measures it until told why, and
/// reports what it published; what the store would refuse - the whole journal, a prefix beside kept content, a release by
/// record number while a pin stands - is said before it is asked and never offered to be performed; and a session read at
/// its last complete generation is measured, but nothing is released from it until its pointer is repaired.
/// </summary>
public sealed class JournalReleaseTests : IDisposable
{
    private const string Refused = "Nothing was published, because the release above is refused.";

    private readonly TemporaryDirectory root = new();

    public void Dispose() => root.Dispose();

    [Fact(DisplayName = "S5: icat retain --release-journal-before-record says what a journal prefix release gives up, measures it until told why, and reports what it published")]
    public async Task AJournalPrefixIsReleasedWhenToldWhy()
    {
        // Three records, a batch each: releasing the records before the first gives up its batch and keeps two.
        using var session = new TemporarySession();
        Publish(session.Store, Rows(3), journalBatchRecords: 1, finished: true);
        session.Store.ReleaseSegmentReaders();
        long generation = Generation(session.Path);

        (InterCatExitCode code, string output, string said) = await Run("retain", session.Path, "--release-journal-before-record", "1");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("JOURNAL PREFIX RELEASE, MEASURED ONLY", output, Ordinal);
        Assert.Matches(@"(?m)^  Journal +journal-0000000001\.icatj, [\d,]+ B, 3 records\r?$", output);
        Assert.Matches(@"(?m)^  Boundaries it allows +1 to 2 records\r?$", output);
        Assert.Matches(@"(?m)^  Released +1 +1\r?$", output);
        Assert.Matches(@"(?m)^  Retained +2 +2\r?$", output);
        Assert.Contains("This was a measurement. Add --confirm and --reason to perform it.", output, Ordinal);
        Assert.Contains("Measuring what releasing records before 1 would give up. Nothing is written yet.", said, Ordinal);
        Assert.Equal(generation, Generation(session.Path));

        // A reason alone measures too, and says what performs it.
        (code, output, said) = await Run("retain", session.Path, "--release-journal-before-record", "1", "--reason", "older than a day");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("This was a measurement: a reason alone performs nothing. Add --confirm to perform it.", output, Ordinal);
        Assert.DoesNotContain("Add --confirm and --reason", output, Ordinal);
        Assert.Equal(generation, Generation(session.Path));

        // Told why, it releases, says what it published, and writes its report where it is asked to.
        string written = Path.Combine(root.Path, "reports", "released.json");
        (code, output, said) = await Run("retain", session.Path, "--release-journal-before-record", "1", "--confirm", "--reason",
            "older than a day", "--output", written);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("Releasing 1 record in 1 batch and publishing the retention generation.", said, Ordinal);
        Assert.Contains($"Retention report written to {written}.", said, Ordinal);
        Assert.Contains("JOURNAL PREFIX RELEASED", output, Ordinal);
        Assert.Contains("PUBLISHED RETENTION GENERATION", output, Ordinal);
        Assert.Matches($@"(?m)^  Generation +{generation + 1}\r?$", output);
        Assert.Matches($@"(?m)^  Retained journal +journal-{generation + 1:D10}\.icatj, [\d,]+ B, 2 records\r?$", output);
        Assert.Matches(@"(?m)^  Given up +[\d,]+ B of admitted evidence\r?$", output);
        Assert.Matches(@"(?m)^  Net change +-?[\d,]+ B \([\d,]+ B written, [\d,]+ B removed\)\r?$", output);
        Assert.Matches(@"(?m)^  Released files +journal-0000000001\.icatj\r?$", output);
        Assert.DoesNotContain("This was a measurement", output, Ordinal);
        Assert.Equal(generation + 1, Generation(session.Path));
        using (JsonDocument report = JsonDocument.Parse(File.ReadAllText(written)))
        {
            JsonElement document = report.RootElement;
            Assert.Equal(("store-v1", "release-journal-prefix", true), (document.GetProperty("contract").GetString(),
                document.GetProperty("action").GetString(), document.GetProperty("performed").GetBoolean()));
            JsonElement preview = document.GetProperty("preview");
            Assert.Equal((3L, 1L, 2L, false, JsonValueKind.Null), (preview.GetProperty("totalRecords").GetInt64(),
                preview.GetProperty("releasedRecords").GetInt64(), preview.GetProperty("retainedRecords").GetInt64(),
                preview.GetProperty("keepsContent").GetBoolean(), preview.GetProperty("heldBy").ValueKind));
            JsonElement result = document.GetProperty("result");
            Assert.Equal((generation + 1, 2L), (result.GetProperty("generation").GetInt64(),
                result.GetProperty("retainedJournalRecords").GetInt64()));
        }

        // The report is never written over without --overwrite.
        (code, _, said) = await Run("retain", session.Path, "--release-journal-before-record", "1", "--output", written);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{written} exists. Pass --overwrite to replace it.", said, Ordinal);
    }

    [Fact(DisplayName = "S5: a journal prefix release that would give up the whole journal is refused, measured or confirmed, and never offered to be performed")]
    public async Task AReleaseOfTheWholeJournalIsRefused()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(3), journalBatchRecords: 1, finished: true);
        session.Store.ReleaseSegmentReaders();
        long generation = Generation(session.Path);

        foreach (string[] confirmed in new string[][] { [], ["--confirm", "--reason", "all of it"] })
        {
            (InterCatExitCode code, string output, string said) =
                await Run(["retain", session.Path, "--release-journal-before-record", "3", .. confirmed]);
            Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
            Assert.Matches(@"(?m)^  Released +3 +3\r?$", output);
            Assert.Contains("This boundary would leave no admitted evidence at all, which is refused rather than published", output,
                Ordinal);
            Assert.Equal(confirmed.Length > 0, output.Contains(Refused, Ordinal));
            Assert.DoesNotContain("Add --confirm", output, Ordinal);
            Assert.DoesNotContain("Releasing", said, Ordinal);
            Assert.Equal(generation, Generation(session.Path));
        }
    }

    [Fact(DisplayName = "S5: a journal prefix release beside content kept with a single journal is refused before it is asked, naming the release that goes first")]
    public async Task ContentKeptBesideTheJournalGoesFirst()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows(3);
        Publish(session.Store, rows, journalBatchRecords: 1, finished: true,
            content: (ContentHeader(), [Content(rows[0], "hello"u8.ToArray())]));
        session.Store.ReleaseSegmentReaders();
        long generation = Generation(session.Path);
        string besideContent = "This session keeps content beside its journal, and rewriting the journal's prefix would leave "
            + "content whose records it no longer holds, so its prefix is released only once a content release has given that "
            + $"content up on its own. icat retain \"{session.Path}\" --release-content measures what that gives up.";

        // Measured, it says so where it would say how to perform it.
        (InterCatExitCode code, string output, string said) =
            await Run("retain", session.Path, "--release-journal-before-record", "1", "--json");
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
        using (JsonDocument answer = JsonDocument.Parse(output))
        {
            Assert.True(answer.RootElement.GetProperty("preview").GetProperty("keepsContent").GetBoolean());
            string[] notes = [.. answer.RootElement.GetProperty("notes").EnumerateArray().Select(note => note.GetString()!)];
            Assert.Equal(besideContent, notes[^1]);
            Assert.DoesNotContain(notes, note => note.Contains("Add --confirm", Ordinal));
        }

        // Confirmed, it is refused before it is attempted, and the command ends as a refusal does.
        (code, output, said) = await Run("retain", session.Path, "--release-journal-before-record", "1", "--confirm", "--reason",
            "free space");
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
        Assert.Contains(besideContent, output, Ordinal);
        Assert.Contains(Refused, output, Ordinal);
        Assert.DoesNotContain("Releasing", said, Ordinal);
        Assert.Equal(generation, Generation(session.Path));

        // Once the content has been released on its own, the prefix goes as the journal alone decides.
        (code, _, said) = await Run("retain", session.Path, "--release-content", "--confirm", "--reason", "payloads held tokens");
        Assert.True(code == InterCatExitCode.Success, said);
        (code, output, said) = await Run("retain", session.Path, "--release-journal-before-record", "1", "--confirm", "--reason",
            "free space");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("JOURNAL PREFIX RELEASED", output, Ordinal);
        Assert.Equal(generation + 2, Generation(session.Path));
    }

    [Fact(DisplayName = "S5: a pin stops every journal prefix release, as the measurement says before it is asked, and pins that cannot be read stop the measurement")]
    public async Task APinStopsAReleaseByRecordNumber()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(3), journalBatchRecords: 1, finished: true);
        session.Store.ReleaseSegmentReaders();
        long generation = Generation(session.Path);
        (InterCatExitCode code, string output, string said) =
            await Run("pin", session.Path, "--from", "20 µs", "--reason", "the incident", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        string shortId;
        using (JsonDocument placed = JsonDocument.Parse(output))
        {
            shortId = placed.RootElement.GetProperty("placed").GetProperty("shortId").GetString()!;
        }

        string from = SessionTimeText.Seconds(20_000, CultureInfo.CurrentCulture);
        string pinned = $"A pin keeps every record read from {from} (the incident). A release by record number cannot say when "
            + "its records were read, so none is made while a pin stands: release the interval before the pin by its session "
            + "time instead, or remove the pin.";

        // Measured, the report names the pin and why it stops the release, and never offers to perform it.
        (code, output, said) = await Run("retain", session.Path, "--release-journal-before-record", "1");
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
        Assert.Matches($@"(?m)^  Pinned +pin {shortId} keeps every record from {from.Replace(".", @"\.", Ordinal)} \(the incident\)\r?$",
            output);
        Assert.Contains(pinned, output, Ordinal);
        Assert.DoesNotContain("Add --confirm", output, Ordinal);

        // Confirmed, it is refused before it is attempted.
        (code, output, said) = await Run("retain", session.Path, "--release-journal-before-record", "1", "--confirm", "--reason",
            "by number", "--json");
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
        using (JsonDocument answer = JsonDocument.Parse(output))
        {
            JsonElement document = answer.RootElement;
            Assert.False(document.GetProperty("performed").GetBoolean());
            Assert.Equal(shortId, document.GetProperty("preview").GetProperty("heldBy").GetProperty("shortId").GetString());
            string[] notes = [.. document.GetProperty("notes").EnumerateArray().Select(note => note.GetString()!)];
            Assert.Equal([pinned, Refused], notes[^2..]);
        }

        Assert.DoesNotContain("Releasing", said, Ordinal);
        Assert.Equal(generation, Generation(session.Path));

        // Pins that cannot be read leave unknown whether one stands, so nothing is measured as releasable.
        File.WriteAllText(Path.Combine(session.Path, RetentionPinsV1.FileName), "{");
        (code, output, said) = await Run("retain", session.Path, "--release-journal-before-record", "1");
        Assert.Equal((InterCatExitCode.CorruptedInput, string.Empty), (code, output));
        Assert.Contains($"This session's pins, in {RetentionPinsV1.FileName}, could not be read", said, Ordinal);
    }

    [Fact(DisplayName = "S5: a session read at its last complete generation is measured, saying nothing is released until its pointer is repaired, and a release is refused before it measures")]
    public async Task NothingIsReleasedFromASessionReadAtItsLastGeneration()
    {
        // Finished sessions whose checkpoint generation's pointer is damaged, so readers read the finished first one: one to
        // release journal records and an interval of, one to release content from.
        ObservationRowV1[] rows = Rows(3);
        using var records = new TemporarySession();
        Publish(records.Store, rows, journalBatchRecords: 1, finished: true);
        using var content = new TemporarySession();
        Publish(content.Store, rows, journalBatchRecords: 1, finished: true, content: (ContentHeader(), [Content(rows[0], "hello"u8.ToArray())]));
        foreach ((TemporarySession session, string[][] releases) in new (TemporarySession, string[][])[]
        {
            (records, [["--release-journal-before-record", "1"], ["--release-before", "25 µs"]]),
            (content, [["--release-content"]]),
        })
        {
            session.Store.ReleaseSegmentReaders();
            (InterCatExitCode published, _, string publishing) = await Run("checkpoint", session.Path);
            Assert.True(published == InterCatExitCode.Success, publishing);
            Assert.Equal(2, Generation(session.Path));
            File.WriteAllText(Path.Combine(session.Path, SessionPointerV1.FileName), "{");
            string[] files = Files(session.Path);
            const string Reason = "'current-generation.json' is missing or unreadable";
            string unrepaired = $"This session is read at generation 1, its last complete one, because the newest did not "
                + $"verify: {Reason}. Nothing can be released from it until its pointer is repaired: icat recover "
                + $"\"{session.Path}\" reviews the pointer and repairs it once you confirm.";
            foreach (string[] release in releases)
            {
                // Measured, it says why nothing can be released yet, where it would say how to perform it.
                (InterCatExitCode code, string output, string said) = await Run(["retain", session.Path, .. release]);
                Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
                Assert.Matches(@"(?m)^  Generation +1\r?$", output);
                Assert.Contains(unrepaired, output, Ordinal);
                Assert.DoesNotContain("Add --confirm", output, Ordinal);

                // Confirmed, it is refused before anything is measured, naming the command that repairs the pointer.
                (code, output, said) = await Run(["retain", session.Path, .. release, "--confirm", "--reason", "too soon"]);
                Assert.Equal((InterCatExitCode.PermissionOrCapabilityFailure, string.Empty), (code, output));
                Assert.Contains($"The newest generation did not verify: {Reason}. This session is read at generation 1, and "
                    + "nothing can be released from it until its pointer is repaired, so nothing was measured or released. "
                    + $"icat recover \"{session.Path}\" reviews the pointer and repairs it once you confirm.", said, Ordinal);
                Assert.DoesNotContain("Measuring", said, Ordinal);
                Assert.Equal(files, Files(session.Path));
            }
        }
    }

    /// <summary>Sends ten microseconds apart in session time, the first ten microseconds in.</summary>
    private static ObservationRowV1[] Rows(int count) =>
    [
        .. Enumerable.Range(1, count).Select(ordinal => Transfer(100 * ordinal, ObservationKind.Send, AccountingSide.SendSide, 64,
            100, (ulong)ordinal).Between("10.0.0.10:50000", "10.0.0.20:8080") with { SessionRelativeTicks = 10_000 * ordinal }),
    ];

    private static long Generation(string session) =>
        SessionStore.OpenExisting(LocalOwnedDirectory.Open(session)).Current!.Generation;

    private static string[] Files(string session) =>
        [.. Directory.EnumerateFiles(session).Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal)];

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
