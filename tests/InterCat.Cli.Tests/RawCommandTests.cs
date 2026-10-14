using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat raw on a session's records, as a person runs it (§3.7, R18): a record's facts are the window's, in its words -
/// its provider named, its codes in words, its lengths counted - its bytes hidden until asked, a redacted package's record
/// called a synthetic entry, and one whose original is no longer retained said so.
/// </summary>
public sealed class RawCommandTests : IDisposable
{
    private readonly TemporarySession session = new();

    public RawCommandTests()
    {
        // A send whose envelope kept three bytes of its body, a receipt that kept one, a send that kept none and one that kept
        // more than a preview shows, each in a journal batch of its own.
        ObservationRowV1[] rows =
        [
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 10_000 },
            Transfer(110, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 2).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { SessionRelativeTicks = 11_000 },
            Transfer(120, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 12_000 },
            Transfer(130, ObservationKind.Send, AccountingSide.SendSide, 300, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 13_000 },
        ];
        Publish(session.Store, rows, journalBatchRecords: 1, bodyForRow: row => row.RawRecordOrdinal switch
        {
            1 => Body([0xCA, 0xFE, 0x01]),
            2 => Body([0x7F]),
            4 => Body([.. Enumerable.Repeat((byte)0xAB, 300)]),
            _ => BodyV1.None,
        });
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose() => session.Dispose();

    [Fact(DisplayName = "R18: icat raw says a record's facts as the window does, in words - its provider named, its lengths counted - and its bytes only when asked")]
    public async Task ARecordReadsAsTheWindowSaysIt()
    {
        SessionEvidencePage page = Page(session.Store);
        (InterCatExitCode code, string output, string said) = await Run(Locator(session.Path, page, 1));
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("ORIGINAL ADMITTED JOURNAL RECORD", output, StringComparison.Ordinal);

        // Each fact the window lists is listed under its label, in the window's words.
        SessionRawRecordDetail detail = Read(session.Store, page, 1);
        foreach ((string label, string value) in RawRecordText.Facts(detail))
        {
            Assert.Matches($@"(?m)^  {Regex.Escape(label)} +{Regex.Escape(value)}\r?$", output);
        }

        Assert.Matches($@"(?m)^  Provider +Microsoft-Windows-Kernel-Network \({NetworkProvider:N}\) · event \d+ · version \d+ · opcode \d+ · task \d+\r?$", output);
        Assert.Matches(@"(?m)^  Clock +[0-9a-f]{32} · encoding QPC \(performance counter\) · native reading \d+\r?$", output);
        Assert.Matches(@"(?m)^  Body +approved metadata, retained · original 3 bytes · retained 3 bytes\r?$", output);
        Assert.Matches(@"(?m)^  Normalized fact +fact [0-9a-f]{32} · observation-v1\r?$", output);
        Assert.Matches(@"(?m)^  Content +None\. The kernel's network events carry endpoints and a transfer's size", output);
        foreach (string identifier in new[] { "Qpc", "ApprovedMetadata", "NoBody", "{ " })
        {
            Assert.DoesNotContain(identifier, output, StringComparison.Ordinal);
        }

        // Its bytes stay hidden until asked, then show as inert hexadecimal.
        Assert.Contains("Body bytes are hidden. Add --reveal-bytes to print at most 256 inert hex bytes.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("CAFE01", output, StringComparison.Ordinal);
        (code, output, said) = await Run([.. Locator(session.Path, page, 1), "--reveal-bytes"]);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Retained body preview · inert hexadecimal:\r?$", output);
        Assert.Matches(@"(?m)^  0000: CAFE01\r?$", output);

        // A preview shows at most 256 of the bytes kept, and says that more exist.
        (_, output, _) = await Run([.. Locator(session.Path, page, 4), "--reveal-bytes"]);
        Assert.Equal(16, Regex.Count(output, @"(?m)^  [0-9A-F]{4}: (AB){16}\r?$"));
        Assert.Contains(RawRecordText.PreviewStopped, output, StringComparison.Ordinal);

        // A length of one is one byte; a body that kept nothing says so, and its disposition why.
        (_, output, _) = await Run(Locator(session.Path, page, 2));
        Assert.Matches(@"(?m)^  Body +approved metadata, retained · original 1 byte · retained 1 byte\r?$", output);
        (_, output, _) = await Run(Locator(session.Path, page, 3));
        Assert.Matches(@"(?m)^  Body +none, no body · original 0 bytes · retained 0 bytes\r?$", output);
        Assert.Contains(RawRecordText.NoBody, output, StringComparison.Ordinal);

        // A script reads the same record, and that it is no package's synthetic entry.
        (code, output, _) = await Run([.. Locator(session.Path, page, 1), "--json"]);
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument answer = JsonDocument.Parse(output);
        Assert.Equal("raw-record-view-v1", answer.RootElement.GetProperty("contract").GetString());
        JsonElement record = answer.RootElement.GetProperty("record");
        Assert.True(record.GetProperty("available").GetBoolean());
        Assert.False(record.GetProperty("synthetic").GetBoolean());
        Assert.Equal(3, record.GetProperty("retainedBodyLength").GetInt32());
    }

    [Fact(DisplayName = "§11.3: icat raw calls a redacted package's record a synthetic entry, never an original, with the window's disclosure")]
    public async Task APackagesRecordIsSynthetic()
    {
        string destination = Path.Combine(Path.GetTempPath(), "InterCat.Cli.Tests.Raw", Guid.NewGuid().ToString("N"));
        RedactedSessionPackage.Create(session.Store, destination, Committed);
        try
        {
            SessionStore package = SessionStore.OpenExisting(LocalOwnedDirectory.Open(destination));
            SessionEvidencePage page = Page(package);
            (InterCatExitCode code, string output, string said) = await Run(Locator(destination, page, page.Records[0].Observation.RawRecordOrdinal));
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains("SYNTHETIC RECORD OF A REDACTED PACKAGE", output, StringComparison.Ordinal);
            Assert.DoesNotContain("ORIGINAL", output, StringComparison.Ordinal);
            Assert.Contains(RawRecordText.SyntheticDisclosure + " (redacted-session-v1)", output, StringComparison.Ordinal);
            Assert.DoesNotContain(RawRecordText.NoBody, output, StringComparison.Ordinal);
            (_, output, _) = await Run([.. Locator(destination, page, page.Records[0].Observation.RawRecordOrdinal), "--json"]);
            using JsonDocument answer = JsonDocument.Parse(output);
            Assert.True(answer.RootElement.GetProperty("record").GetProperty("synthetic").GetBoolean());
            package.ReleaseSegmentReaders();
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact(DisplayName = "R21: a record whose original journal was released is said to be unavailable, its raw identity still listed, and the answer is partial")]
    public async Task ARecordReleasedIsSaidUnavailable()
    {
        // Retention releases the journal's first batch, which held the first record; its normalized fact survives.
        (InterCatExitCode code, _, string said) = await Run("retain", session.Path, "--release-journal-before-record", "1",
            "--confirm", "--reason", "the first record is not needed");
        Assert.True(code == InterCatExitCode.Success, said);
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        SessionEvidencePage page = Page(store);
        store.ReleaseSegmentReaders();
        (code, string output, said) = await Run(Locator(session.Path, page, 1));
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Matches(@"(?m)^  Raw record +capture [0-9a-f]{32} · stream \d+ · source epoch \d+ · ordinal 1\r?$", output);
        Assert.DoesNotMatch(@"(?m)^  Provider ", output);
        Assert.Contains("The original record is outside the retained journal boundary.", said, StringComparison.Ordinal);

        // The record after the boundary is still its original.
        (code, output, said) = await Run(Locator(session.Path, page, 2));
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Body +approved metadata, retained · original 1 byte · retained 1 byte\r?$", output);
    }

    private static BodyV1 Body(byte[] bytes) => new()
    {
        Classification = BodyClassificationV1.ApprovedMetadata,
        Disposition = BodyDispositionV1.Retained,
        OriginalLength = bytes.Length,
        Bytes = EnvelopeBuffer.CopyOf(bytes),
    };

    private static SessionEvidencePage Page(SessionStore store) => SessionEvidenceQuery.Read(store, resolveOwners: true);

    private static SessionRawRecordDetail Read(SessionStore store, SessionEvidencePage page, ulong ordinal)
    {
        SessionEvidenceRecord record = page.Records.Single(candidate => candidate.Observation.RawRecordOrdinal == ordinal);
        SessionRawRecordDetail detail = SessionRawRecordQuery.ReadAt(store, page.SessionId, page.Generation, record.SegmentName,
            record.SegmentRow);
        store.ReleaseSegmentReaders();
        return detail;
    }

    /// <summary>icat raw's locator for the record of <paramref name="ordinal"/>, as icat evidence prints it.</summary>
    private static string[] Locator(string path, SessionEvidencePage page, ulong ordinal)
    {
        SessionEvidenceRecord record = page.Records.Single(candidate => candidate.Observation.RawRecordOrdinal == ordinal);
        return
        [
            "raw", path, "--session-id", page.SessionId.ToString(), "--generation",
            page.Generation.ToString(CultureInfo.InvariantCulture), "--segment", record.SegmentName, "--row",
            record.SegmentRow.ToString(CultureInfo.InvariantCulture),
        ];
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
