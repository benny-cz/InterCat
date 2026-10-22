using System.Text.Json;
using InterCat.Application;
using InterCat.Analysis.Tests;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat rederive as a person runs it (R20, R18): a check replays a recording's journal and says what it verified while
/// publishing nothing; what cannot be replayed is refused before anything says it began; and a session read at its last
/// complete generation is refused before it is replayed, since nothing can be published into it until it is repaired.
/// </summary>
public sealed class RederiveCommandTests : IDisposable
{
    private readonly TemporaryDirectory root = new();

    public void Dispose() => root.Dispose();

    [Fact(DisplayName = "R20: icat rederive --check replays a recording's journal and says what it verified, publishing nothing")]
    public async Task ACheckSaysWhatItVerifiedAndPublishesNothing()
    {
        string session = await Followed();
        long generation = Generation(session);

        (InterCatExitCode code, string report, string said) = await Run("rederive", session, "--check");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("RE-DERIVATION CHECK PASSED", report, StringComparison.Ordinal);
        Assert.Matches($@"(?m)^  Generation +{generation}\r?$", report);
        Assert.Matches(@"(?m)^  Admitted journal +journal-0000000002\.icatj \(the last of 2 chunks of one recording\)\r?$", report);
        Assert.Matches(@"(?m)^  Replayed records +4\r?$", report);
        Assert.Matches(@"(?m)^  Observations +4\r?$", report);
        Assert.Contains("Read-only replay verified the saved plan, every chunk's digest and frames, that the chunks continue "
            + "one capture in order, and every derived row. It did not test a future segment write or commit.", report,
            StringComparison.Ordinal);
        Assert.Contains($"Checking generation {generation}'s retained plan and every committed record of its 2 retained journal "
            + "chunks, in order, without publishing any file or generation.", said, StringComparison.Ordinal);
        Assert.Equal(generation, Generation(session));

        // Its report, written where asked and never over a file already there.
        string written = Path.Combine(root.Path, "check.json");
        (code, string json, _) = await Run("rederive", session, "--check", "--output", written);
        Assert.Equal(InterCatExitCode.Success, code);
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(written)))
        {
            Assert.Equal("rederive-check-v1", document.RootElement.GetProperty("contract").GetString());
            Assert.Equal(4, document.RootElement.GetProperty("replayedRecords").GetInt64());
        }

        Assert.Contains("RE-DERIVATION CHECK PASSED", json, StringComparison.Ordinal);
        (code, _, said) = await Run("rederive", session, "--check", "--output", written);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{written} exists. Pass --overwrite to replace the report.", said, StringComparison.Ordinal);
        Assert.Equal(generation, Generation(session));
    }

    [Fact(DisplayName = "R20: what icat rederive cannot replay is refused before it says it began, in words that name no step there is not")]
    public async Task WhatCannotBeReplayedIsRefusedFirst()
    {
        // A session an earlier version published, with no plan to replay its journal with.
        using var legacy = new TemporarySession();
        Publish(legacy.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)]);
        legacy.Store.ReleaseSegmentReaders();
        foreach (string[] args in new[] { ["rederive", legacy.Path], new[] { "rederive", legacy.Path, "--check" } })
        {
            (InterCatExitCode code, string output, string said) = await Run(args);
            Assert.Equal((InterCatExitCode.PermissionOrCapabilityFailure, string.Empty), (code, output));
            Assert.Contains("This generation has no retained normalization plan: an earlier version of InterCat published it "
                + "before plans were kept", said, StringComparison.Ordinal);
            Assert.Contains("Nothing was changed; the session stays readable as it is.", said, StringComparison.Ordinal);
            Assert.DoesNotContain("Replaying", said, StringComparison.Ordinal);
            Assert.DoesNotContain("Checking generation", said, StringComparison.Ordinal);
            Assert.DoesNotContain("migration", said, StringComparison.Ordinal);
        }

        // icat session says why in the same words.
        (_, string described, _) = await Run("session", legacy.Path);
        Assert.Contains("No retained normalization plan: an earlier version of InterCat published this session before plans "
            + "were kept, so its journal cannot be replayed. The session stays readable as it is.", described,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R20: InterCat's generated demo is said to have nothing to re-derive, as the demo it is, never as an earlier version's session")]
    public async Task TheDemoHasNothingToRederive()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(root.Path, "demo"));
        long generation = Generation(demo.ClientSession);
        const string Nothing = "InterCat generated this demo session. Its rows were written as they are, beside records made "
            + "to match them, not derived from those records, so there is nothing to re-derive. The session stays readable "
            + "as it is.";
        foreach (string[] args in new[] { ["rederive", demo.ClientSession], new[] { "rederive", demo.ClientSession, "--check" } })
        {
            (InterCatExitCode code, string output, string said) = await Run(args);
            Assert.Equal((InterCatExitCode.PermissionOrCapabilityFailure, string.Empty), (code, output));
            Assert.Contains(Nothing, said, StringComparison.Ordinal);
            Assert.DoesNotContain("earlier version", said, StringComparison.Ordinal);
            Assert.DoesNotContain("Replaying", said, StringComparison.Ordinal);
            Assert.DoesNotContain("Checking generation", said, StringComparison.Ordinal);
        }

        Assert.Equal(generation, Generation(demo.ClientSession));

        // icat session says so in the same words, beside the demo's disclosure, and names no command to run.
        (_, string described, _) = await Run("session", demo.ServerSession);
        Assert.Contains(Nothing, described, StringComparison.Ordinal);
        Assert.Contains(DemoInvestigation.Disclosure, described, StringComparison.Ordinal);
        Assert.DoesNotContain("earlier version", described, StringComparison.Ordinal);
        (_, string document, _) = await Run("session", demo.ServerSession, "--json");
        using JsonDocument parsed = JsonDocument.Parse(document);
        JsonElement rederivation = parsed.RootElement.GetProperty("rederivation");
        Assert.Equal((false, Nothing, JsonValueKind.Null), (rederivation.GetProperty("canAttempt").GetBoolean(),
            rederivation.GetProperty("explanation").GetString(), rederivation.GetProperty("command").ValueKind));
    }

    [Fact(DisplayName = "R20: a session read at its last complete generation is checked, and refused re-derivation before any replay")]
    public async Task ASessionReadAtItsLastGenerationIsRefusedFirst()
    {
        string session = await Followed();
        long generation = Generation(session);
        File.WriteAllText(Path.Combine(session, SessionPointerV1.FileName), "{");
        string[] files = Files(session);

        // A check reads and publishes nothing, so it goes ahead and says which generation it checked.
        (InterCatExitCode checkedCode, string report, string checking) = await Run("rederive", session, "--check");
        Assert.True(checkedCode == InterCatExitCode.PartialResultSuccess, checking);
        Assert.Contains($"The newest generation did not verify, so the retained last-known-good generation {generation - 1} is "
            + "checked: 'current-generation.json' is missing or unreadable.", checking, StringComparison.Ordinal);
        Assert.Contains("RE-DERIVATION CHECK PASSED", report, StringComparison.Ordinal);

        // A re-derivation would publish over it, so it is refused before replaying a record, naming the command that repairs.
        (InterCatExitCode refused, string output, string said) = await Run("rederive", session);
        Assert.Equal((InterCatExitCode.PermissionOrCapabilityFailure, string.Empty), (refused, output));
        Assert.Contains("The newest generation did not verify: 'current-generation.json' is missing or unreadable. This session "
            + $"is read at generation {generation - 1}, and nothing can be published into it until its pointer is repaired, so "
            + $"nothing was replayed. icat recover \"{session}\" reviews the pointer and repairs it once you confirm.", said,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Replaying", said, StringComparison.Ordinal);
        Assert.Equal(files, Files(session));
    }

    [Theory(DisplayName = "R18: icat rederive refuses an option it cannot read, saying what it expects")]
    [InlineData(new[] { "--rows-per-segment", "lots" }, "--rows-per-segment expects a number of rows from 1 to 4,000,000; 'lots' is not one.")]
    [InlineData(new[] { "--rows-per-segment", "0" }, "--rows-per-segment expects a number of rows from 1 to 4,000,000; '0' is not one.")]
    [InlineData(new[] { "--check", "--rows-per-segment", "2" }, "--rows-per-segment applies only when publishing; --check reads without staging segments.")]
    [InlineData(new[] { "--check", "--check" }, "--check is given more than once; give it once.")]
    public async Task AnOptionItCannotReadIsRefused(string[] options, string expected)
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output, string said) = await Run(["rederive", session.Path, .. options]);

        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Contains(expected, said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: icat rederive needs a session's folder, and says so of none and of one that is not there")]
    public async Task AFolderIsRequired()
    {
        (InterCatExitCode none, _, string noneSaid) = await Run("rederive");
        Assert.Equal(InterCatExitCode.InvalidInvocation, none);
        Assert.Contains("A published session directory is required: icat rederive <directory>.", noneSaid, StringComparison.Ordinal);
        Assert.Contains("A session an earlier version of InterCat published without a saved plan is refused", noneSaid,
            StringComparison.Ordinal);

        string missing = Path.Combine(root.Path, "nowhere");
        (InterCatExitCode gone, _, string goneSaid) = await Run("rederive", missing);
        Assert.Equal(InterCatExitCode.InvalidInvocation, gone);
        Assert.Contains($"No session directory at {missing}.", goneSaid, StringComparison.Ordinal);
    }

    /// <summary>A finished recording of four records in two chunks, followed into a session that keeps its plan.</summary>
    private async Task<string> Followed()
    {
        string evidence = Path.Combine(root.Path, "evidence");
        Directory.CreateDirectory(evidence);
        _ = await EvidenceRecordings.RecordEvidence(evidence, ordinals: [1, 2, 3, 4], bursts: [2]);
        string session = Path.Combine(root.Path, "followed");
        (InterCatExitCode code, _, string said) = await Run("follow", evidence, session);
        Assert.True(code == InterCatExitCode.Success, said);
        return session;
    }

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
