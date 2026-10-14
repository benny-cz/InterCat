using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat recover and icat staging as a person runs them (§20.1, R18): a review changes nothing and says what a confirmation
/// would do, either half of a confirmation alone is refused with the review that supplies the other, a confirmation of what
/// the session no longer holds changes nothing, and no file is removed that nothing proves abandoned.
/// </summary>
public sealed class RecoveryCommandTests
{
    [Fact(DisplayName = "§20.1: icat recover reviews a damaged pointer, refuses what it did not print, and repairs it once confirmed, keeping every file")]
    public async Task RecoverRepairsADamagedPointerOnceConfirmed()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Send(64)]);
        Publish(session.Store, [Send(32)]);
        session.Store.ReleaseSegmentReaders();
        string staged = Abandon(session.Path);
        (_, string cleanupReview, _) = await Run("staging", session.Path);
        string pointer = Path.Combine(session.Path, SessionPointerV1.FileName);
        File.WriteAllText(pointer, "{");
        string[] files = Files(session.Path);

        // Generation 2's files and the staged pair are named by no generation that verifies, and kept, every byte of them.
        string[] unreferenced =
            [.. files.Where(file => file.Contains("0000000002", Ordinal) || file.StartsWith(SessionStore.StagingPrefix, Ordinal))];
        Assert.Equal(6, unreferenced.Length);
        string kept = Regex.Escape(ConsoleUi.Bytes(unreferenced.Sum(file => new FileInfo(Path.Combine(session.Path, file)).Length)));

        // Opening the session says what it found, and names the command that repairs it.
        (InterCatExitCode opened, string opening, _) = await Run("session", session.Path);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, opened);
        Assert.Contains($"Nothing was repaired and nothing was removed. icat recover \"{session.Path}\" reviews the pointer and, "
            + "once you confirm, re-points it to this generation, so the session can be published into again.", opening, Ordinal);
        Assert.Contains($"icat staging \"{session.Path}\" lists each one, and removes, once you confirm, only those whose writer let "
            + "go; a file staged with no ownership marker is never removed.", opening, Ordinal);
        Assert.Matches($@"(?m)^  Unreferenced files +6 files kept, {kept}\r?$", opening);

        // So does whatever it cannot publish until then: here, the staging cleanup reviewed before the pointer was damaged.
        (InterCatExitCode refused, string refusedOutput, string refusal) = await Run(Printed(cleanupReview));
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (refused, refusedOutput));
        Assert.Contains("This session's newest generation does not verify, so it is read at its last complete one, and nothing "
            + "can be published into it or released from it until its pointer is repaired. Nothing was changed. "
            + $"icat recover \"{session.Path}\" reviews the pointer and repairs it once you confirm.", refusal, Ordinal);

        // The review says what readers read, why, what is kept, and the one command that repairs it; it changes nothing.
        (InterCatExitCode reviewed, string review, _) = await Run("recover", session.Path);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, reviewed);
        Assert.Contains("SESSION RECOVERY REVIEW", review, Ordinal);
        Assert.Matches(@"(?m)^  Current pointer +does not verify; readers fall back to generation 1\r?$", review);
        Assert.Matches(@"(?m)^  Rollback reason +'current-generation\.json' is missing or unreadable\r?$", review);
        Assert.Matches($@"(?m)^  Unreferenced files +6 files kept, {kept}\r?$", review);
        Assert.Matches(@"(?m)^  - manifest-0000000002\.json\r?$", review);
        Assert.Contains("Nothing has been changed. Readers read generation 1 in the newest one's place, and nothing can be "
            + "published into this session or released from it until its pointer is repaired. Once you have reviewed the reason "
            + "and the files above, this re-points it to generation 1, keeping the pointer's present bytes and every file:",
            review, Ordinal);
        string[] repair = Printed(review);
        Assert.Equal(["recover", session.Path, "--confirm", "--expect-manifest"], repair[..4]);
        Assert.Equal(files, Files(session.Path));

        // Either half of the confirmation alone is refused with the review that supplies the other, as is a digest the
        // session's verified generation does not have; none of them changes anything.
        (InterCatExitCode unbound, string unboundOutput, string unboundSaid) = await Run("recover", session.Path, "--confirm");
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (unbound, unboundOutput));
        Assert.Contains("--confirm needs the --expect-manifest digest a review printed, which binds the repair to the generation "
            + $"you reviewed: run icat recover \"{session.Path}\" first.", unboundSaid, Ordinal);
        (InterCatExitCode unconfirmed, _, string unconfirmedSaid) = await Run("recover", session.Path, "--expect-manifest", repair[4]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, unconfirmed);
        Assert.Contains($"--expect-manifest applies only with --confirm; icat recover \"{session.Path}\" alone reviews the session "
            + "and changes nothing.", unconfirmedSaid, Ordinal);
        (InterCatExitCode stale, _, string staleSaid) = await Run(
            "recover", session.Path, "--confirm", "--expect-manifest", "sha256:" + new string('0', 64));
        Assert.Equal(InterCatExitCode.InvalidInvocation, stale);
        Assert.Contains("The session's verified generation is no longer the one this confirmation names, so nothing was changed. "
            + $"Run icat recover \"{session.Path}\" again and review what it shows before confirming.", staleSaid, Ordinal);
        Assert.Equal(files, Files(session.Path));
        Assert.Equal("{", File.ReadAllText(pointer));

        // The printed confirmation repairs the pointer: the damaged bytes are kept beside it, and every other file too.
        (InterCatExitCode repaired, string done, _) = await Run(repair);
        Assert.Equal(InterCatExitCode.Success, repaired);
        Assert.Contains("SESSION POINTER REPAIRED", done, Ordinal);
        Assert.Matches(@"(?m)^  Current pointer +repaired; it names generation 1\r?$", done);
        Match backup = Regex.Match(done, @"(?m)^  Pointer backup +(recovery-pointer-[0-9a-f]{32}\.json)\r?$");
        Assert.True(backup.Success, done);
        Assert.Equal("{", File.ReadAllText(Path.Combine(session.Path, backup.Groups[1].Value)));
        Assert.Contains("The current pointer names generation 1, which verifies, so the session can be published into again. The "
            + "pointer's earlier bytes are kept in the pointer backup. The 6 unreferenced files are kept as well: InterCat never "
            + "deletes one, and they stay for you to inspect.", done, Ordinal);
        Assert.Equal(files.Append(backup.Groups[1].Value).Order(StringComparer.Ordinal), Files(session.Path));

        // Reviewed again, the pointer verifies and there is nothing to confirm; the backup is one more file kept.
        (InterCatExitCode verified, string again, _) = await Run("recover", session.Path);
        Assert.Equal(InterCatExitCode.Success, verified);
        Assert.Matches(@"(?m)^  Current pointer +verifies; it names generation 1\r?$", again);
        Assert.Matches(@"(?m)^  Unreferenced files +7 files kept, [\d,]+ B\r?$", again);
        Assert.Matches($@"(?m)^  - {backup.Groups[1].Value}\r?$", again);
        Assert.Contains("The current pointer verifies, so there is nothing to repair. Nothing was changed.", again, Ordinal);
        Assert.DoesNotContain("--confirm", again, Ordinal);

        // What the damaged pointer refused now goes ahead.
        (InterCatExitCode cleaned, _, string cleanedSaid) = await Run(Printed(cleanupReview));
        Assert.True(cleaned == InterCatExitCode.Success, cleanedSaid);
        Assert.False(File.Exists(Path.Combine(session.Path, staged)));
    }

    [Fact(DisplayName = "§20.1: icat recover of a pointer that is gone keeps no bytes it does not have, and says only what it kept")]
    public async Task RecoverSaysWhatItKeptOfAPointerThatIsGone()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Send(64)]);
        Publish(session.Store, [Send(32)]);
        session.Store.ReleaseSegmentReaders();
        File.Delete(Path.Combine(session.Path, SessionPointerV1.FileName));

        (_, string review, _) = await Run("recover", session.Path);
        Assert.Contains("this re-points it to generation 1, keeping every file:", review, Ordinal);
        (InterCatExitCode repaired, string done, string said) = await Run(Printed(review));
        Assert.True(repaired == InterCatExitCode.Success, said);
        Assert.DoesNotContain("Pointer backup", done, Ordinal);
        Assert.Contains("The current pointer names generation 1, which verifies, so the session can be published into again. The 4 "
            + "unreferenced files are kept as well:", done, Ordinal);
        Assert.Empty(Directory.EnumerateFiles(session.Path, "recovery-pointer-*"));

        // A directory is required, and its absence is said in the words a person typed it in.
        (InterCatExitCode nothing, _, string nothingSaid) = await Run("recover");
        Assert.Equal(InterCatExitCode.InvalidInvocation, nothing);
        Assert.Contains("A session directory is required: icat recover <directory> reviews it and changes nothing.", nothingSaid,
            Ordinal);
    }

    [Fact(DisplayName = "§20.1: icat recover repairs a session whose newest generation changed under an intact pointer, in the same words")]
    public async Task RecoverRepairsAGenerationThatChangedUnderItsPointer()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Send(64)]);
        Publish(session.Store, [Send(32)]);
        session.Store.ReleaseSegmentReaders();
        string segment = Path.Combine(session.Path, "seg-0000000002-0000.icats");
        byte[] bytes = File.ReadAllBytes(segment);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(segment, bytes);
        string pointer = File.ReadAllText(Path.Combine(session.Path, SessionPointerV1.FileName));

        (InterCatExitCode reviewed, string review, string progress) = await Run("recover", session.Path);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, reviewed);
        Assert.Matches(@"(?m)^  Current pointer +does not verify; readers fall back to generation 1\r?$", review);
        Assert.Matches(@"(?m)^  Rollback reason +'seg-0000000002-0000\.icats' does not match what generation 2 records \(its SHA-256 "
            + @"begins [0-9a-f]{12}, not [0-9a-f]{12}\)\r?$", review);
        Assert.Contains("keeping the pointer's present bytes and every file:", review, Ordinal);
        Assert.Contains("Verifying both generation pointers and dependencies", progress, Ordinal);

        (InterCatExitCode repaired, string done, string doing) = await Run(Printed(review));
        Assert.True(repaired == InterCatExitCode.Success, doing);
        Assert.Contains("Keeping the pointer's present bytes, then pointing it to verified generation 1.", doing, Ordinal);
        Match backup = Regex.Match(done, @"(?m)^  Pointer backup +(recovery-pointer-[0-9a-f]{32}\.json)\r?$");
        Assert.True(backup.Success, done);
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(session.Path, backup.Groups[1].Value)));
        Assert.Equal(bytes, File.ReadAllBytes(segment));
        Assert.Equal(InterCatExitCode.Success, (await Run("recover", session.Path)).Code);
    }

    [Fact(DisplayName = "§20.1: icat staging lists every file it found and what a cleanup does with it, and removes only the reviewed set")]
    public async Task StagingRemovesOnlyTheReviewedSet()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Send(64)]);
        session.Store.ReleaseSegmentReaders();

        // Nothing staged: the review says so, and offers nothing to confirm.
        (InterCatExitCode clean, string none, _) = await Run("staging", session.Path);
        Assert.Equal(InterCatExitCode.Success, clean);
        Assert.Contains("STAGING REVIEW", none, Ordinal);
        Assert.Contains("Nothing is staged in this session, so there is nothing to remove.", none, Ordinal);
        Assert.DoesNotContain("--confirm", none, Ordinal);
        Assert.DoesNotContain("Files removed", none, Ordinal);

        // A staged file whose writer let go, a marker left without its file, one staged with no ownership marker, and one a
        // writer still holds.
        string abandoned = Abandon(session.Path);
        string marker = $"{SessionStore.StagingPrefix}{Guid.NewGuid():N}{SessionStore.StagingOwnershipSuffix}";
        File.WriteAllText(Path.Combine(session.Path, marker), string.Empty);
        string unmarked = $"{SessionStore.StagingPrefix}{Guid.NewGuid():N}{SessionStore.StagingSuffix}";
        File.WriteAllText(Path.Combine(session.Path, unmarked), "legacy");
        StoreStagingFile held = session.Store.Stage("seg-0000000009-0000.icats", StoreDependencyKind.Segment);
        try
        {
            string[] files = Files(session.Path);
            (InterCatExitCode reviewed, string review, _) = await Run("staging", session.Path);
            Assert.Equal(InterCatExitCode.PartialResultSuccess, reviewed);
            Assert.Matches(@"(?m)^  Abandoned staged files +1\r?$", review);
            Assert.Matches(@"(?m)^  Marker-only files +1\r?$", review);
            Assert.Matches(@"(?m)^  Active writer files +1\r?$", review);
            Assert.Matches(@"(?m)^  Unmarked staged files +1\r?$", review);
            Assert.DoesNotContain("Files removed", review, Ordinal);
            Assert.Matches(@"(?m)^  File +What it is +A cleanup\r?$", review);
            Assert.Matches($@"(?m)^  {abandoned} +staged; its writer let go +removes it\r?$", review);
            Assert.Matches($@"(?m)^  {MarkerOf(abandoned)} +its ownership marker +removes it\r?$", review);
            Assert.Matches($@"(?m)^  {marker} +ownership marker; its staged file is gone +removes it\r?$", review);
            Assert.Matches($@"(?m)^  {held.StagingName} +a writer still holds it +keeps it\r?$", review);
            Assert.Matches($@"(?m)^  {unmarked} +staged; no ownership marker +keeps it\r?$", review);
            Assert.Contains("A writer still holds 1 file: a capture or a publication is at work, and a cleanup never removes what a "
                + "writer holds.", review, Ordinal);
            Assert.Contains("1 file was staged with no ownership marker, so nothing proves its writer let go, and InterCat never "
                + "removes it. Once you know no InterCat process is writing to this session, you can delete it yourself.", review,
                Ordinal);
            Assert.Contains("Nothing has been changed. To remove the 3 files the table marks, and nothing else, run:", review, Ordinal);
            string[] confirm = Printed(review);
            Assert.Equal(["staging", session.Path, "--confirm", "--expect-set"], confirm[..4]);
            Assert.Equal(files, Files(session.Path));

            // Either half of the confirmation alone is refused with the review that supplies the other.
            (InterCatExitCode unbound, string unboundOutput, string unboundSaid) = await Run("staging", session.Path, "--confirm");
            Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (unbound, unboundOutput));
            Assert.Contains("--confirm needs the --expect-set digest a review printed, so a cleanup removes only the files you "
                + $"reviewed: run icat staging \"{session.Path}\" first.", unboundSaid, Ordinal);
            (InterCatExitCode unconfirmed, _, string unconfirmedSaid) = await Run("staging", session.Path, "--expect-set", confirm[4]);
            Assert.Equal(InterCatExitCode.InvalidInvocation, unconfirmed);
            Assert.Contains($"--expect-set applies only with --confirm; icat staging \"{session.Path}\" alone reviews the staged "
                + "files and changes nothing.", unconfirmedSaid, Ordinal);
            (InterCatExitCode nothing, _, string nothingSaid) = await Run("staging");
            Assert.Equal(InterCatExitCode.InvalidInvocation, nothing);
            Assert.Contains("A session directory is required: icat staging <directory> reviews the files staged in it and changes "
                + "nothing.", nothingSaid, Ordinal);

            // A file left behind since the review is never added to it: the confirmation removes nothing, and says so.
            string later = Abandon(session.Path);
            (InterCatExitCode stale, _, string staleSaid) = await Run(confirm);
            Assert.Equal(InterCatExitCode.InvalidInvocation, stale);
            Assert.Contains("The staged files this confirmation names are not the ones the session holds now, so nothing was "
                + "removed: a file left behind since the review is never added to an earlier decision. "
                + $"icat staging \"{session.Path}\" reviews them again.", staleSaid, Ordinal);
            Assert.Equal(files.Concat([later, MarkerOf(later)]).Order(StringComparer.Ordinal), Files(session.Path));

            // Reviewed again, the confirmation removes exactly the files its review listed as removed, and keeps the rest.
            (_, string again, _) = await Run("staging", session.Path);
            (InterCatExitCode cleaned, string result, _) = await Run(Printed(again));
            Assert.Equal(InterCatExitCode.PartialResultSuccess, cleaned);
            Assert.Contains("STAGING CLEANUP", result, Ordinal);
            Assert.Matches(@"(?m)^  Files removed +5\r?$", result);
            Assert.Matches(@"(?m)^  File +What it is +The cleanup\r?$", result);
            Assert.Matches($@"(?m)^  {later} +staged; its writer let go +removed it\r?$", result);
            Assert.Matches($@"(?m)^  {marker} +ownership marker; its staged file is gone +removed it\r?$", result);
            Assert.Matches($@"(?m)^  {held.StagingName} +a writer still holds it +kept it\r?$", result);
            Assert.Matches($@"(?m)^  {unmarked} +staged; no ownership marker +kept it\r?$", result);
            Assert.Contains("Removed 5 files: the reviewed staged files whose writer let go, and their ownership markers.", result,
                Ordinal);
            Assert.DoesNotContain("--confirm", result, Ordinal);
            Assert.Equal(
                files.Except([abandoned, MarkerOf(abandoned), marker], StringComparer.Ordinal).Order(StringComparer.Ordinal),
                Files(session.Path));
        }
        finally
        {
            held.Dispose();
        }

        // Once its writer lets go, the held file is reviewed as removable; the one with no marker never is.
        (InterCatExitCode released, string afterWriter, _) = await Run("staging", session.Path);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, released);
        Assert.Matches($@"(?m)^  {held.StagingName} +staged; its writer let go +removes it\r?$", afterWriter);
        Assert.Matches($@"(?m)^  {unmarked} +staged; no ownership marker +keeps it\r?$", afterWriter);
        Assert.Contains("Nothing has been changed. To remove the 2 files the table marks, and nothing else, run:", afterWriter,
            Ordinal);
    }

    [Fact(DisplayName = "§20.1: icat staging lists twelve files and says how many more --json lists, every one")]
    public async Task StagingListsTwelveFilesAndCountsTheRest()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Send(64)]);
        session.Store.ReleaseSegmentReaders();
        string[] abandoned = [.. Enumerable.Range(0, 7).Select(_ => Abandon(session.Path))];

        (InterCatExitCode reviewed, string review, _) = await Run("staging", session.Path);
        Assert.Equal(InterCatExitCode.Success, reviewed);
        Assert.Equal(12, Regex.Count(review, @"(?m)^  stg-[0-9a-f]{32}\.(tmp|lease) "));
        Assert.Contains("... and 2 more; --json lists every one.", review, Ordinal);
        Assert.Contains("To remove the 14 files the table marks, and nothing else, run:", review, Ordinal);

        (_, string answer, _) = await Run("staging", session.Path, "--json");
        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal(abandoned.Order(StringComparer.Ordinal), document.RootElement.GetProperty("report")
            .GetProperty("abandonedFiles").EnumerateArray().Select(name => name.GetString()!).Order(StringComparer.Ordinal));
    }

    private const StringComparison Ordinal = StringComparison.Ordinal;

    private static ObservationRowV1 Send(long bytes) =>
        Transfer(100, ObservationKind.Send, AccountingSide.SendSide, bytes, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080");

    /// <summary>A staged file and its ownership marker, as a writer that let go of them leaves them.</summary>
    private static string Abandon(string session)
    {
        string staged = $"{SessionStore.StagingPrefix}{Guid.NewGuid():N}{SessionStore.StagingSuffix}";
        File.WriteAllText(Path.Combine(session, staged), "partial");
        File.WriteAllText(Path.Combine(session, MarkerOf(staged)), string.Empty);
        return staged;
    }

    private static string MarkerOf(string staged) =>
        staged[..^SessionStore.StagingSuffix.Length] + SessionStore.StagingOwnershipSuffix;

    private static string[] Files(string session) =>
        [.. Directory.EnumerateFiles(session).Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal)];

    /// <summary>The confirmation a review printed, as the arguments a person runs it with.</summary>
    private static string[] Printed(string review)
    {
        Match command = Regex.Match(review, @"(?m)^    icat (\w+) ""([^""]+)"" --confirm (--expect-[a-z]+) (\S+)\r?$");
        Assert.True(command.Success, review);
        return [command.Groups[1].Value, command.Groups[2].Value, "--confirm", command.Groups[3].Value, command.Groups[4].Value];
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
