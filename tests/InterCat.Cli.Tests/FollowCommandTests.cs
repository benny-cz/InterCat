using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Capture.Journal;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat follow as a person runs it (§3.1 step 6, R18): finishing a capture a viewer left unfinished, from the ticket beside
/// its session, says what the window's card says - what the broker kept, what finishing derived, what can never be
/// recovered - counting a chunk and a record in the singular where it is one; and a follow it cannot run says why.
/// </summary>
public sealed class FollowCommandTests : IDisposable
{
    private readonly TemporaryDirectory root = new();

    public void Dispose() => root.Dispose();

    [Fact(DisplayName = "§3.1: icat follow <session> finishes a capture a viewer left unfinished, in the card's words, and its ticket goes")]
    public async Task AnUnfinishedCaptureIsFinished()
    {
        (string evidencePath, SessionManifestV1 source) = await Recorded("capture");

        // The viewer followed one chunk and stopped: its ticket stays, released, beside its session.
        string session = Followed(evidencePath, source, "explore-1", chunks: 1);
        Ticket(source, evidencePath, session, settled: false);
        (InterCatExitCode code, string output, string said) = await Run("follow", session);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("RECORDING DERIVED", output, StringComparison.Ordinal);
        Match chunks = Regex.Match(output, @"(?m)^  Chunks +(\d+) of (\d+) mirrored\r?$");
        Assert.True(chunks.Success && chunks.Groups[1].Value == chunks.Groups[2].Value, output);
        Assert.Matches(@"(?m)^  Records +4 derived\r?$", output);
        Assert.Matches(@"(?m)^  State +finished: the capture stopped, and its coverage ledger is mirrored\r?$", output);

        // Each step it took is said as the card says it while it finishes.
        Assert.Contains($"Finishing {session} from the evidence at {evidencePath}.", said, StringComparison.Ordinal);
        Assert.Matches(@$"(?m)^… {chunks.Groups[1].Value} of {chunks.Groups[1].Value} published chunks derived, 4 records so far\.\r?$", said);
        Assert.DoesNotContain("(s)", said + output, StringComparison.Ordinal);
        Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));

        // Nothing is left beside it to finish, and a folder that is not there is said to be so.
        (code, _, said) = await Run("follow", session);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"Nothing unfinished is recorded beside {session}: its capture's follow completed", said, StringComparison.Ordinal);
        string nowhere = Path.Combine(root.Path, "nowhere");
        (code, _, said) = await Run("follow", nowhere);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"There is no session at {nowhere}, and no unfinished capture recorded beside it.", said, StringComparison.Ordinal);

        // A ticket left for a session that already holds the whole capture goes, and nothing is said to be missing.
        Ticket(source, evidencePath, session, settled: true);
        (code, output, said) = await Run("follow", session);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal($"… {session} already holds everything its capture published; its ticket is removed.", said.TrimEnd());
        Assert.Empty(output);
        Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));

        // A ticket a follow still holds is that follow's to finish. Only Windows enforces the hold between two handles in
        // one process, so elsewhere the finish would not be told.
        if (OperatingSystem.IsWindows())
        {
            string held = Path.Combine(root.Path, "explore-held");
            using (LiveFollowTicket.For(source.SessionId, evidencePath, held, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30)).Hold())
            {
                (code, _, said) = await Run("follow", held);
                Assert.Equal(InterCatExitCode.InvalidInvocation, code);
                Assert.Contains($"The capture beside {held} is being followed or finished elsewhere right now.", said, StringComparison.Ordinal);
            }
        }
    }

    [Fact(DisplayName = "§3.1: a capture that ended before it was finalized is derived as far as it published, and icat follow says the session is partial for good")]
    public async Task AnUnfinalizedCaptureIsSavedAsItWasPublished()
    {
        (string evidencePath, SessionManifestV1 recorded) = await Recorded("capture");
        SessionManifestV1 source = EvidenceRecordings.RewindToUnfinalized(evidencePath);
        Assert.Equal(recorded.SessionId, source.SessionId);

        // Within its owner's lease and the broker's time to finalize, the capture may still be recording: what it
        // published is derived, the ticket stays, and the follow says when to run it again.
        string session = Path.Combine(root.Path, "explore-1");
        LiveFollowTicket recording = Ticket(source, evidencePath, session, settled: false);
        (InterCatExitCode code, string output, string said) = await Run("follow", session);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Contains("The capture may still be recording or stopping until ", said, StringComparison.Ordinal);
        Assert.Contains("FOLLOWING STOPPED", output, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  State +the capture has not finished here; run this again to continue\r?$", output);
        Assert.True(File.Exists(LiveFollowTicket.PathFor(session)));

        // Once it settled, the session holds everything it ever will, and is said to be partial: the ticket goes.
        _ = Ticket(source, evidencePath, session, settled: true);
        (code, output, said) = await Run("follow", session);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal($"… {session} already holds everything its capture published; its ticket is removed. The capture ended "
            + "before it was finalized, so records after its last publication were never kept.", said.TrimEnd());
        Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));

        // A session that followed none of it is derived from what it published, and said to be partial for good: running
        // again would derive nothing, so the follow does not ask for it.
        string fresh = Path.Combine(root.Path, "explore-2");
        _ = Ticket(source, evidencePath, fresh, settled: true);
        (code, output, said) = await Run("follow", fresh);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("PARTIAL RECORDING DERIVED", output, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  State +ended before it was finalized, so records after its last publication were never kept; "
            + @"the session holds every chunk it published\r?$", output);
        Assert.DoesNotContain("run this again", output + said, StringComparison.Ordinal);
        Assert.DoesNotContain("FOLLOWING STOPPED", output, StringComparison.Ordinal);
        Assert.False(File.Exists(LiveFollowTicket.PathFor(fresh)));
        (code, _, _) = await Run("follow", fresh);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);

        // A script reads the same finish: the session is complete for good, and not finished.
        string scripted = Path.Combine(root.Path, "explore-3");
        _ = Ticket(source, evidencePath, scripted, settled: true);
        (code, output, said) = await Run("follow", scripted, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        using JsonDocument answer = JsonDocument.Parse(output);
        Assert.Equal("follow-v1", answer.RootElement.GetProperty("contract").GetString());
        Assert.False(answer.RootElement.GetProperty("finished").GetBoolean());
        Assert.Equal(answer.RootElement.GetProperty("evidenceChunks").GetInt32(), answer.RootElement.GetProperty("derivedChunks").GetInt32());
        Assert.Equal(recording.EvidenceDirectory, answer.RootElement.GetProperty("evidencePath").GetString());
    }

    [Fact(DisplayName = "§3.1: evidence that can no longer be read leaves the session as it was followed, and icat follow says what it keeps, or that it kept nothing")]
    public async Task EvidenceThatCannotBeReadKeepsWhatWasFollowed()
    {
        (string evidencePath, SessionManifestV1 source) = await Recorded("capture");
        string session = Followed(evidencePath, source, "explore-1", chunks: 1);
        Ticket(source, evidencePath, session, settled: true);
        string empty = Path.Combine(root.Path, "explore-2");
        Ticket(source, evidencePath, empty, settled: true);

        // Damaged evidence is named with its reason, after what the session keeps of it - one chunk, in the singular.
        File.WriteAllText(Path.Combine(evidencePath, SessionPointerV1.FileName), "{");
        File.Delete(Path.Combine(evidencePath, SessionPointerV1.PreviousFileName));
        (InterCatExitCode code, string output, string said) = await Run("follow", session);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Empty(output);
        Assert.StartsWith($"! The capture beside {session} can no longer be finished: its evidence at {evidencePath} can no "
            + "longer be read. The session keeps the 1 published chunk followed before. Reason: ", said, StringComparison.Ordinal);
        Assert.DoesNotContain("(s)", said, StringComparison.Ordinal);

        // A session that holds nothing of it keeps nothing, which is said, and the evidence is the damaged input.
        (code, output, said) = await Run("follow", empty);
        Assert.Equal(InterCatExitCode.CorruptedInput, code);
        Assert.Empty(output);
        Assert.StartsWith($"x The capture beside {empty} can no longer be finished: its evidence at {evidencePath} can no longer "
            + "be read, and nothing had been saved to the session. Reason: ", said, StringComparison.Ordinal);
        Assert.DoesNotContain("keeps the", said, StringComparison.Ordinal);
        Assert.True(File.Exists(LiveFollowTicket.PathFor(empty)));

        // Evidence that is gone gives no reason; once its capture settled, a session that holds none of it is left nothing
        // to finish, and its ticket goes rather than claim a session that is not there holds everything.
        Directory.Delete(evidencePath, recursive: true);
        (code, _, said) = await Run("follow", session);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Equal($"! The capture beside {session} can no longer be finished: its evidence at {evidencePath} can no longer be "
            + "read. The session keeps the 1 published chunk followed before.", said.TrimEnd());
        (code, _, said) = await Run("follow", empty);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal($"… The capture beside {empty} left nothing to finish: no chunk of it remains to derive, and none was saved "
            + "to the session. Its ticket is removed.", said.TrimEnd());
        Assert.False(File.Exists(LiveFollowTicket.PathFor(empty)));
        Assert.False(Directory.Exists(empty));
    }

    [Fact(DisplayName = "R18: icat follow refuses a follow it cannot run, saying why, and stops waiting for a recording that never published")]
    public async Task AFollowItCannotRunIsRefused()
    {
        (string evidencePath, _) = await Recorded("capture");
        string session = Path.Combine(root.Path, "explore-1");

        // Both directories are named, the evidence is there, and the session goes elsewhere.
        (InterCatExitCode code, string output, string said) = await Run("follow", evidencePath, "--once");
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Contains("An evidence directory and a session directory are required", said, StringComparison.Ordinal);
        string none = Path.Combine(root.Path, "none");
        (code, _, said) = await Run("follow", none, session);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"No evidence directory at {none}.", said, StringComparison.Ordinal);
        (code, _, said) = await Run("follow", evidencePath, evidencePath);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("The session is derived into a directory of its own, not into the evidence directory.", said, StringComparison.Ordinal);
        (code, _, said) = await Run("follow", evidencePath, session, "--poll", "0");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--poll expects whole seconds from 1 to ", said, StringComparison.Ordinal);
        Assert.False(Directory.Exists(session));

        // A recording that has published nothing has nothing to derive at once, and a follow waiting for it stops when
        // asked, keeping nothing, as a cancelled command does; neither makes the session's folder.
        string unpublished = Path.Combine(root.Path, "unpublished");
        Directory.CreateDirectory(unpublished);
        (code, output, said) = await Run("follow", unpublished, session, "--once");
        Assert.Equal((InterCatExitCode.PartialResultSuccess, string.Empty), (code, output));
        Assert.Contains("The recording has published nothing yet, so there is nothing to derive.", said, StringComparison.Ordinal);
        using var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        (code, output, said) = await Run(soon.Token, "follow", unpublished, session);
        Assert.Equal((InterCatExitCode.Cancelled, string.Empty), (code, output));
        Assert.Contains("Waiting for the recording's first publication. Ctrl+C stops.", said, StringComparison.Ordinal);
        Assert.Contains("Stopped before the recording published anything.", said, StringComparison.Ordinal);
        Assert.False(Directory.Exists(session));
    }

    [Fact(DisplayName = "S1: icat follow names, in each generation it mirrors after its first, a checkpoint of the generation before it")]
    public async Task AFollowNamesACheckpointOfTheGenerationBefore()
    {
        // A capture still recording, as far as it published: three chunks.
        string evidencePath = Path.Combine(root.Path, "capture");
        Directory.CreateDirectory(evidencePath);
        _ = await EvidenceRecordings.RecordEvidence(evidencePath, ordinals: [1, 2, 3, 4, 5, 6, 7, 8], bursts: [1, 2, 4, 6]);
        _ = EvidenceRecordings.RewindToUnfinalized(evidencePath);
        string session = Path.Combine(root.Path, "explore");
        (InterCatExitCode code, _, string said) = await Run("follow", evidencePath, session, "--once");
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);

        // The newest generation names a checkpoint, named for it, of every segment the generation before it named.
        SessionStore derived = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
        SessionManifestV1 current = derived.Current!;
        Assert.True(current.Generation >= 2, $"The follow published {current.Generation} generation(s).");
        StoreDependency named = Assert.IsType<StoreDependency>(InterCat.Analysis.DerivationCheckpoint.NamedBy(current));
        Assert.Equal(InterCat.Analysis.DerivationCheckpoint.FileNameFor(current.Generation), named.Name);
        Assert.Equal(SessionSegments.Names(current).Count - 1, InterCat.Analysis.DerivationCheckpoint.Read(
            SessionSegments.ReadVerified(derived.Root, named, InterCat.Analysis.DerivationCheckpoint.MaximumBytes),
            current.SessionId, SessionSegments.SourceClock(derived.Root, current)!.Value).Segments.Count);
    }

    [Fact(DisplayName = "§20.1: icat follow whose compaction cannot be published finishes all the same, says why, and keeps the session whole")]
    public async Task AFollowWhoseCompactionFailsFinishesWhole()
    {
        (string evidencePath, SessionManifestV1 source) = await Recorded("capture");
        int chunks = LiveSessionFollower.Progress(source).Chunks;
        string session = Followed(evidencePath, source, "explore", chunks: 1);

        // Something already stands where the compaction that ends the follow would publish its generation, as a file left
        // there by another program would: the compaction cannot be published, and every chunk was mirrored before it.
        Directory.CreateDirectory(Path.Combine(session, SessionManifestV1.FileNameFor(chunks + 1)));
        (InterCatExitCode code, string output, string said) = await Run("follow", evidencePath, session, "--once", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        using (JsonDocument document = JsonDocument.Parse(output))
        {
            JsonElement root = document.RootElement;
            Assert.Equal((true, chunks, 0), (root.GetProperty("finished").GetBoolean(),
                root.GetProperty("derivedChunks").GetInt32(), root.GetProperty("compactions").GetInt32()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("compactionFailure").GetString()));
        }

        Assert.Equal(4, JournalRederivation.Verify(SessionStore.OpenExisting(LocalOwnedDirectory.Open(session))).ObservationRows);

        // A follow that compacted says nothing of it.
        (string otherEvidence, _) = await Recorded("capture-2");
        (code, output, said) = await Run("follow", otherEvidence, Path.Combine(root.Path, "explore-2"), "--once", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        using (JsonDocument document = JsonDocument.Parse(output))
        {
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("compactionFailure").ValueKind);
        }

        Assert.DoesNotContain("A compaction was not published", said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§20.1: icat follow says in its summary that a compaction was not published, in the words icat record uses")]
    public async Task AFollowSaysItsCompactionWasNotPublished()
    {
        (string evidencePath, SessionManifestV1 source) = await Recorded("capture");
        int chunks = LiveSessionFollower.Progress(source).Chunks;
        string session = Followed(evidencePath, source, "explore", chunks: 1);
        Directory.CreateDirectory(Path.Combine(session, SessionManifestV1.FileNameFor(chunks + 1)));
        (InterCatExitCode code, _, string said) = await Run("follow", evidencePath, session, "--once");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("A compaction was not published; the session is whole, with more segments than it needs, and icat "
            + "compact coalesces them. It failed: ", said, StringComparison.Ordinal);

        // A capture a viewer left unfinished is finished the same way, naming the session alone.
        (string otherEvidence, SessionManifestV1 other) = await Recorded("capture-2");
        string unfinished = Followed(otherEvidence, other, "explore-2", chunks: 1);
        _ = Ticket(other, otherEvidence, unfinished, settled: true);
        Directory.CreateDirectory(Path.Combine(unfinished, SessionManifestV1.FileNameFor(LiveSessionFollower.Progress(other).Chunks + 1)));
        (code, string output, said) = await Run("follow", unfinished, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal((true, 0), (document.RootElement.GetProperty("finished").GetBoolean(),
            document.RootElement.GetProperty("compactions").GetInt32()));
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("compactionFailure").GetString()));
        Assert.False(File.Exists(LiveFollowTicket.PathFor(unfinished)));
    }

    /// <summary>A finalized evidence recording of four records, as the broker publishes it, in a folder of its own.</summary>
    private async Task<(string Path, SessionManifestV1 Source)> Recorded(string name)
    {
        string path = Path.Combine(root.Path, name);
        Directory.CreateDirectory(path);
        _ = await EvidenceRecordings.RecordEvidence(path, ordinals: [1, 2, 3, 4]);
        return (path, SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)).Current!);
    }

    /// <summary>A session that followed the evidence's first <paramref name="chunks"/> chunks before its viewer stopped.</summary>
    private string Followed(string evidencePath, SessionManifestV1 source, string name, int chunks)
    {
        string session = Path.Combine(root.Path, name);
        Directory.CreateDirectory(session);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
        SessionStore followed = SessionStore.Open(LocalOwnedDirectory.Open(session), source.SessionId, source.SourceIdentity);
        FollowStep step = LiveSessionFollower.Open(evidence, followed).CatchUp(maximumChunks: chunks);
        Assert.Equal(chunks, step.DerivedChunks);
        Assert.True(step.EvidenceChunks > chunks, "The evidence must hold a chunk the session does not.");
        return session;
    }

    /// <summary>
    /// The ticket a stopped follow leaves beside <paramref name="session"/>, released; <paramref name="settled"/> puts the
    /// capture's owner lease long past, so a capture that is not finalized never will be.
    /// </summary>
    private static LiveFollowTicket Ticket(SessionManifestV1 source, string evidencePath, string session, bool settled)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LiveFollowTicket ticket = LiveFollowTicket.For(source.SessionId, evidencePath, session,
            settled ? now.AddMinutes(-10) : now, settled ? now.AddMinutes(-5) : now.AddSeconds(30));
        ticket.Hold().Dispose();
        return ticket;
    }

    private static Task<(InterCatExitCode Code, string Output, string Error)> Run(params string[] args) =>
        Run(CancellationToken.None, args);

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Run(CancellationToken cancellationToken,
        params string[] args)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(said);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, cancellationToken);
            return (code, answered.ToString(), said.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
