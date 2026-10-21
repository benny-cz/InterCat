using System.Globalization;
using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// §18.4 as a person runs icat import: a folder already holding this file's import is reused, read and written no more; any
/// other folder that is not new is refused with what it holds and what to do instead, before a schema is compiled or a
/// record read; and the cleanup of an import that stopped leaves a folder the import publishes into.
/// </summary>
public sealed class ImportReuseTests : IDisposable
{
    private const string Compiling = "Compiling admission plans";

    private readonly TemporaryDirectory root = new();

    public void Dispose() => root.Dispose();

    [Fact(DisplayName = "§18.4: icat import into the folder holding this file's import reuses its session, hashing the file and writing nothing")]
    public async Task AFolderHoldingThisImportIsReused()
    {
        string etl = Etl("trace.etl", 1);
        ImportIdentity identity = IdentityOf(etl);
        string session = Imported(identity, etl);
        string[] before = Files(session);

        (InterCatExitCode code, string output, string said) = await Run("import", etl, "--into", session);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"Hashing {etl} to compare it with the session {session} holds.", said, StringComparison.Ordinal);
        Assert.DoesNotContain(Compiling, said, StringComparison.Ordinal);
        Assert.Contains("IMPORT REUSED", output, StringComparison.Ordinal);
        Assert.Matches($@"(?m)^  Import identity +{identity.Digest}\r?$", output);
        Assert.Matches($@"(?m)^  Session +{Regex(session)}\r?$", output);
        Assert.Matches($@"(?m)^  Generation +1, published {Regex(Published)}\r?$", output);
        Assert.Matches(@"(?m)^  Observations +3\r?$", output);
        Assert.Contains("This folder already holds this file's import, so it was reused", output, StringComparison.Ordinal);
        Assert.Contains($"Open it with: icat session \"{session}\"", output, StringComparison.Ordinal);
        Assert.Equal(before, Files(session));

        (code, string json, _) = await Run("import", etl, "--into", session, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            JsonElement reused = document.RootElement;
            Assert.Equal("import-reuse-v1", reused.GetProperty("contract").GetString());
            Assert.Equal(identity.Digest, reused.GetProperty("importDigest").GetString());
            Assert.Equal(identity.Source.ContentDigest, reused.GetProperty("sourceContentDigest").GetString());
            Assert.Equal(etl, reused.GetProperty("sourcePath").GetString());
            Assert.Equal("MetadataOnly", reused.GetProperty("retainedEvidence").GetString());
            JsonElement held = reused.GetProperty("session");
            Assert.Equal(identity.CaptureId.Value.ToString("N"), held.GetProperty("sessionId").GetString());
            Assert.Equal(1, held.GetProperty("generation").GetInt64());
            Assert.Equal(3, held.GetProperty("observationRows").GetInt64());
            Assert.Equal(JsonValueKind.Null, held.GetProperty("rolledBackBecause").ValueKind);
        }

        // What would shape a new publication is said not to have shaped the one reused, and where it would.
        (code, output, _) = await Run("import", etl, "--into", session, "--rows-per-segment", "500", "--journal-batch-records", "64");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("--rows-per-segment shapes a new publication, so this session keeps the segments it was published "
            + $"with. icat rederive \"{session}\" --rows-per-segment 500 derives them again from its journal.", output,
            StringComparison.Ordinal);
        Assert.Contains("--journal-batch-records shapes a new journal, so this session keeps the journal it was imported with.",
            output, StringComparison.Ordinal);
        (_, output, _) = await Run("import", etl, "--into", session, "--journal-batch-records", "64");
        Assert.DoesNotContain("--rows-per-segment", output, StringComparison.Ordinal);
        Assert.Contains("--journal-batch-records shapes", output, StringComparison.Ordinal);
        Assert.Equal(before, Files(session));

        // The report is written where asked, and never over one already there: refused before the file is hashed again.
        string report = Path.Combine(root.Path, "reuse.json");
        (code, _, said) = await Run("import", etl, "--into", session, "--output", report);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains($"Import summary written to {report}.", said, StringComparison.Ordinal);
        Assert.Contains("\"import-reuse-v1\"", File.ReadAllText(report), StringComparison.Ordinal);
        (code, _, said) = await Run("import", etl, "--into", session, "--output", report);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{report} exists. Pass --overwrite to replace it.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Hashing", said, StringComparison.Ordinal);
        Assert.Equal(before, Files(session));
    }

    [Fact(DisplayName = "§18.4: this file's import read at its last-known-good generation is reused as a partial result, saying why")]
    public async Task ThisImportAtItsLastKnownGoodIsReusedSayingWhy()
    {
        string etl = Etl("trace.etl", 2);
        ImportIdentity identity = IdentityOf(etl);
        string session = Imported(identity, etl);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(session), identity.CaptureId.Value, $"import-v1:{etl}");
        string[] first = [.. store.Current!.Dependencies.Select(dependency => dependency.Name)];
        Publish(store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 32, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")]);
        store.ReleaseSegmentReaders();
        StoreDependency newest = store.Current!.Dependencies.First(dependency =>
            dependency.Kind == StoreDependencyKind.Segment && !first.Contains(dependency.Name));
        string path = Path.Combine(session, newest.Name);
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..^1]);
        string why = newest.LengthMismatch(newest.LengthBytes - 1, 2);

        (InterCatExitCode code, string output, string said) = await Run("import", etl, "--into", session);
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
        Assert.Matches(@"(?m)^  Generation +1, published ", output);
        Assert.Contains($"Its newest generation did not verify, so the session reads the retained last-known-good one: {why}. "
            + $"Nothing was repaired and nothing was removed. icat recover \"{session}\" reviews the pointer", output,
            StringComparison.Ordinal);

        (_, string json, _) = await Run("import", etl, "--into", session, "--json");
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(why, document.RootElement.GetProperty("session").GetProperty("rolledBackBecause").GetString());
    }

    [Fact(DisplayName = "§18.4: a folder holding another session is refused, naming what it was derived from, before a schema is compiled")]
    public async Task AnotherSessionIsRefusedNamingIt()
    {
        string etl = Etl("trace.etl", 3);
        using var other = new TemporarySession();
        Publish(other.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)]);
        other.Store.ReleaseSegmentReaders();
        string[] before = Files(other.Path);

        (InterCatExitCode code, string output, string said) = await Run("import", etl, "--into", other.Path);
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Contains($"{other.Path} holds another session, derived from metric-tests, published {Published}. An import "
            + "publishes only into a new folder, or reuses one that already holds this file's import; publishing into another "
            + $"session would mix its evidence with this file's. Open it with icat session \"{other.Path}\", or choose a new "
            + "folder. --overwrite replaces only the report file.", said, StringComparison.Ordinal);
        Assert.DoesNotContain(Compiling, said, StringComparison.Ordinal);
        Assert.Equal(before, Files(other.Path));

        // An import of this path made from the bytes the file held before is named as such.
        string changed = Etl("changed.etl", 4);
        string earlier = Imported(IdentityOf(changed), changed);
        File.WriteAllBytes(changed, Bytes(5));
        (code, _, said) = await Run("import", changed, "--into", earlier);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{earlier} holds an import of {changed} made from other bytes than the file holds now, or under "
            + "another import contract, so it is not this import and is not reused: publishing into it would mix the two. "
            + $"Open it with icat session \"{earlier}\", or choose a new folder for this import.", said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§18.4: an import that stopped is named with icat staging, whose cleanup leaves a folder the import publishes into")]
    public async Task AStoppedImportIsCleanedUpAndImportedAgain()
    {
        string etl = Etl("trace.etl", 6);
        ImportIdentity identity = IdentityOf(etl);
        string session = Path.Combine(root.Path, "stopped");
        Directory.CreateDirectory(session);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(session), identity.CaptureId.Value, $"import-v1:{etl}");
        using (StoreStagingFile journal = store.Stage("journal-0000000001.icatj", StoreDependencyKind.Journal))
        {
            journal.Content.Write(new byte[2048]);
        }

        (InterCatExitCode code, _, string said) = await Run("import", etl, "--into", session);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{session} holds no session: only 2 files ({ByteSizeText.Of(2048)}) that a publication staged and "
            + "never published, as an import or a recording that stopped before it finished leaves them. icat staging "
            + $"\"{session}\" lists them and removes them once you confirm; then run this import again, or choose a new "
            + "folder.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Hashing", said, StringComparison.Ordinal);
        Assert.DoesNotContain(Compiling, said, StringComparison.Ordinal);

        // While a writer holds what it stages there, the folder is being published into.
        using (StoreStagingFile held = store.Stage("segment-0000000001.icats", StoreDependencyKind.Segment))
        {
            held.Content.Write(new byte[1024]);
            (code, _, said) = await Run("import", etl, "--into", session);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains($"{session} holds no session yet, and something is publishing into it now: a writer holds files "
                + "it is staging there. Wait for it to finish and run this import again, which reuses the session if it "
                + "holds this file's import, or choose a new folder.", said, StringComparison.Ordinal);
        }

        (code, string review, _) = await Run("staging", session, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        string digest;
        using (JsonDocument document = JsonDocument.Parse(review))
        {
            digest = document.RootElement.GetProperty("report").GetProperty("candidateDigest").GetString()!;
        }

        (code, _, _) = await Run("staging", session, "--confirm", "--expect-set", digest);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Equal([SessionStore.PublicationLockFileName], Files(session));

        // What the cleanup left is a lock, which keeps no import out: this one goes on to read the file.
        (_, _, said) = await Run("import", etl, "--into", session);
        Assert.Contains(Compiling, said, StringComparison.Ordinal);
        Assert.DoesNotContain($"{session} holds", said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§18.4: files no session owns, and a file named as the folder, are refused with what is there")]
    public async Task WhatNoSessionOwnsIsRefused()
    {
        string etl = Etl("trace.etl", 7);
        string folder = Path.Combine(root.Path, "notes");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");
        (InterCatExitCode code, _, string said) = await Run("import", etl, "--into", folder);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{folder} holds notes.txt, which no session owns. An import publishes only into a new folder, so its "
            + "evidence is never mixed with anything else: choose a new folder. --overwrite replaces only the report file.",
            said, StringComparison.Ordinal);
        Assert.DoesNotContain(Compiling, said, StringComparison.Ordinal);

        foreach (string name in new[] { "a.txt", "b.txt", "c.txt", "d.txt" })
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }

        (_, _, said) = await Run("import", etl, "--into", folder);
        Assert.Contains($"{folder} holds a.txt, b.txt, c.txt and 2 more, which no session owns.", said, StringComparison.Ordinal);

        (code, _, said) = await Run("import", etl, "--into", etl);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{etl} is a file. --into names the folder an import publishes its session into: a new one, or one "
            + "that already holds this file's import.", said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§18.4: a folder whose session does not verify is refused as damaged input, saying whether it was this file's import")]
    public async Task AnUnreadableSessionIsRefused()
    {
        string etl = Etl("trace.etl", 8);
        ImportIdentity identity = IdentityOf(etl);
        string changed = Imported(identity, etl);
        SessionManifestV1 manifest = SessionStore.OpenExisting(LocalOwnedDirectory.Open(changed)).Current!;
        string segment = Path.Combine(changed, SessionSegments.Names(manifest)[0]);
        byte[] bytes = File.ReadAllBytes(segment);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(segment, bytes);

        (InterCatExitCode code, _, string said) = await Run("import", etl, "--into", changed);
        Assert.Equal(InterCatExitCode.CorruptedInput, code);
        Assert.Contains($"{changed} holds this file's import, but it cannot be reused. This session cannot be read, because no "
            + "generation of it verifies: ", said, StringComparison.Ordinal);
        Assert.Contains($"icat recover \"{changed}\" reviews it; or choose a new folder for this import.", said,
            StringComparison.Ordinal);
        Assert.DoesNotContain(Compiling, said, StringComparison.Ordinal);

        File.Delete(segment);
        (code, _, said) = await Run("import", etl, "--into", changed);
        Assert.Equal(InterCatExitCode.CorruptedInput, code);
        Assert.Contains($"{changed} holds a session whose identity cannot be read, so it is neither reused nor imported into. "
            + "This session cannot be read, because no generation of it verifies: ", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Hashing", said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§18.4: icat import's help says a folder holding this file's import is reused")]
    public async Task HelpSaysAFolderHoldingThisImportIsReused()
    {
        (InterCatExitCode code, string output, _) = await Run("import", "--help");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("A folder that already holds this file's import is", output, StringComparison.Ordinal);
        Assert.Contains("reused, not imported into again; any other session or file there is refused.", output,
            StringComparison.Ordinal);
    }

    private static string Published => Committed.ToString("u", CultureInfo.InvariantCulture);

    private string Etl(string name, int seed)
    {
        string path = Path.Combine(root.Path, name);
        File.WriteAllBytes(path, Bytes(seed));
        return path;
    }

    /// <summary>Bytes no reader of a trace is asked to read: an import reused or refused hashes them at most.</summary>
    private static byte[] Bytes(int seed)
    {
        var bytes = new byte[4096];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static ImportIdentity IdentityOf(string etl) =>
        ImportIdentity.Create(ImportSourceIdentity.OfFile(ImportSourceKind.StandaloneEtl, etl), RetainedEvidencePolicy.MetadataOnly);

    /// <summary>A session published as an import of <paramref name="etl"/> publishes it: three records, one generation.</summary>
    private string Imported(ImportIdentity identity, string etl)
    {
        string session = Path.Combine(root.Path, "session-" + Path.GetFileNameWithoutExtension(etl) + "-" + identity.Digest[7..15]);
        Directory.CreateDirectory(session);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(session), identity.CaptureId.Value, $"import-v1:{etl}");
        Publish(store,
        [
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(101, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 2).Between("127.0.0.1:8080", "127.0.0.1:50000"),
            Transfer(102, ObservationKind.Send, AccountingSide.SendSide, 16, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        ]);
        store.ReleaseSegmentReaders();
        return session;
    }

    private static string[] Files(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private static string Regex(string text) => System.Text.RegularExpressions.Regex.Escape(text);

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
