using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// contracts/import-v1.md §8.1 as a person runs icat import: an import published or reused is recorded in the catalogue of
/// imports, and an import of the same file into a new folder reuses the folder the catalogue lists once it verifies,
/// writing nothing into the new one; --again imports it there all the same; folders that no longer hold it are forgotten;
/// and a catalogue that cannot be read or written is named and never stops an import.
/// </summary>
public sealed partial class ImportReuseTests
{
    /// <summary>When the catalogue entries a test writes were recorded: long before any import a test runs.</summary>
    private static readonly DateTimeOffset LongAgo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "§8.1: an import reused is recorded, and one of the file into a new folder reuses the folder the catalogue lists, writing nothing into the new one")]
    public async Task AnImportTheCatalogueListsIsReused()
    {
        string etl = Etl("trace.etl", 11);
        ImportIdentity identity = IdentityOf(etl);
        string session = Imported(identity, etl);
        string[] before = Files(session);

        // Reused where it is, it is recorded.
        (InterCatExitCode code, string output, string said) = await Run("import", etl, "--into", session);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("nothing was written into it.", output, StringComparison.Ordinal);
        ImportCatalogueEntry recorded = Assert.Single(ImportCatalogue.Read(Catalogue).Entries);
        Assert.Equal(
            (identity.Digest, identity.CaptureId.Value, session, etl, 4_096L),
            (recorded.ImportDigest, recorded.SessionId, recorded.Path, recorded.SourcePath, recorded.SourceLength));

        // Into a folder not there yet: the folder the catalogue lists is verified and reused, and the new one is not made.
        string fresh = Path.Combine(root.Path, "fresh");
        (code, output, said) = await Run("import", etl, "--into", fresh);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"Hashing {etl}: the catalogue of imports {Catalogue} lists 1 import of a file its length.", said,
            StringComparison.Ordinal);
        Assert.Contains($"Verifying {session}, which the catalogue of imports lists as holding this file's import.", said,
            StringComparison.Ordinal);
        Assert.DoesNotContain(Compiling, said, StringComparison.Ordinal);
        Assert.Contains("IMPORT REUSED", output, StringComparison.Ordinal);
        Assert.Matches($@"(?m)^  Session +{Regex(session)}\r?$", output);
        Assert.Matches($@"(?m)^  Not imported into +{Regex(fresh)}\r?$", output);
        Assert.Matches(@"(?m)^  Observations +3\r?$", output);
        Assert.Contains($"The catalogue of imports {Catalogue} lists {session} as holding this file's import, so it was reused "
            + $"rather than imported into {fresh}: a session's identity is derived from the file's bytes, the evidence an "
            + "import keeps and the import contract, and every file its generation names verified. The file was hashed and "
            + $"not read again, and nothing was written into either folder. Pass --again to import it into {fresh} all the "
            + "same.", output, StringComparison.Ordinal);
        Assert.Contains($"Open it with: icat session \"{session}\"", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fresh));
        Assert.Equal(before, Files(session));
        Assert.Equal([session], ImportCatalogue.Read(Catalogue).Entries.Select(entry => entry.Path));

        // An empty folder is left empty; the report names the folder it was not imported into and the catalogue.
        string empty = Path.Combine(root.Path, "empty");
        Directory.CreateDirectory(empty);
        (code, string json, _) = await Run("import", etl, "--into", empty, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            JsonElement reused = document.RootElement;
            Assert.Equal("import-reuse-v1", reused.GetProperty("contract").GetString());
            Assert.Equal(empty, reused.GetProperty("into").GetString());
            Assert.Equal(Catalogue, reused.GetProperty("catalogue").GetString());
            Assert.Equal(session, reused.GetProperty("session").GetProperty("path").GetString());
            Assert.Equal(identity.Digest, reused.GetProperty("importDigest").GetString());
        }

        Assert.Empty(Files(empty));

        // The folder --into named holds it: no catalogue is named, and none says where else it is.
        (_, json, _) = await Run("import", etl, "--into", session, "--json");
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            Assert.Equal(session, document.RootElement.GetProperty("into").GetString());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("catalogue").ValueKind);
        }

        // --again imports into the new folder all the same, looking in no other.
        (_, _, said) = await Run("import", etl, "--into", fresh, "--again");
        Assert.Contains(Compiling, said, StringComparison.Ordinal);
        Assert.DoesNotContain("the catalogue of imports", said, StringComparison.Ordinal);

        // Into the folder holding it, it is reused all the same, saying why.
        (code, output, _) = await Run("import", etl, "--into", session, "--again");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("--again imports a file into a new folder even where the catalogue of imports lists another that holds "
            + "it; this folder holds it already, and an import never publishes into a session twice.", output,
            StringComparison.Ordinal);
        Assert.Equal(before, Files(session));
    }

    [Fact(DisplayName = "§8.1: the folders the catalogue lists that no longer hold the import are forgotten once one that does is reused; an import of another file stays listed")]
    public async Task FoldersNoLongerHoldingTheImportAreForgotten()
    {
        string etl = Etl("trace.etl", 12);
        ImportIdentity identity = IdentityOf(etl);
        string session = Imported(identity, etl);
        string other = Etl("other.etl", 13);
        ImportIdentity otherIdentity = IdentityOf(other);
        string otherSession = Imported(otherIdentity, other);
        using var another = new TemporarySession();
        Publish(another.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)]);
        another.Store.ReleaseSegmentReaders();
        string notes = Path.Combine(root.Path, "notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "notes.txt"), "mine");
        string removed = Path.Combine(root.Path, "removed");
        string fresh = Path.Combine(root.Path, "fresh");

        // Oldest first: the folder holding it and an import of another file, then folders that no longer hold it - one
        // holding another session, one holding notes, one removed, and the very folder this import is told to publish into.
        Listed(identity, session, etl, 1);
        Listed(otherIdentity, otherSession, other, 2);
        Listed(identity, another.Path, etl, 3);
        Listed(identity, notes, etl, 4);
        Listed(identity, removed, etl, 5);
        Listed(identity, fresh, etl, 6);

        (InterCatExitCode code, string output, string said) = await Run("import", etl, "--into", fresh);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"Hashing {etl}: the catalogue of imports {Catalogue} lists 6 imports of a file its length.", said,
            StringComparison.Ordinal);
        Assert.Matches($@"(?m)^  Session +{Regex(session)}\r?$", output);
        Assert.DoesNotContain($"Verifying {fresh},", said, StringComparison.Ordinal);
        Assert.DoesNotContain($"Verifying {otherSession},", said, StringComparison.Ordinal);
        Assert.Equal([session, otherSession], ImportCatalogue.Read(Catalogue).Entries.Select(entry => entry.Path));
        Assert.Equal(["notes.txt"], Files(notes));
        Assert.False(Directory.Exists(removed));
        Assert.False(Directory.Exists(fresh));
    }

    [Fact(DisplayName = "§8.1: a folder the catalogue lists that cannot be read is named and stays listed, and the import goes on into the new folder or an older folder that holds it")]
    public async Task AListedFolderThatCannotBeReadIsNamed()
    {
        string etl = Etl("trace.etl", 14);
        ImportIdentity identity = IdentityOf(etl);
        string session = Imported(identity, etl);
        string damaged = Path.Combine(root.Path, "damaged");
        CopyFolder(session, damaged);
        Directory.Delete(session, recursive: true);
        SessionManifestV1 manifest = SessionStore.OpenExisting(LocalOwnedDirectory.Open(damaged)).Current!;
        string segment = Path.Combine(damaged, SessionSegments.Names(manifest)[0]);
        byte[] bytes = File.ReadAllBytes(segment);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(segment, bytes);
        Listed(identity, damaged, etl, 2);

        string fresh = Path.Combine(root.Path, "fresh");
        (_, _, string said) = await Run("import", etl, "--into", fresh);
        Assert.Contains($"The catalogue of imports lists {damaged} as holding this file's import, but it cannot be reused. This "
            + "session cannot be read, because no generation of it verifies: ", said, StringComparison.Ordinal);
        Assert.Contains($"icat recover \"{damaged}\" reviews it.", said, StringComparison.Ordinal);
        Assert.Contains(Compiling, said, StringComparison.Ordinal);
        Assert.Equal([damaged], ImportCatalogue.Read(Catalogue).Entries.Select(entry => entry.Path));

        // An older folder that holds it is reused; the one that cannot be read stays listed, as it may yet be recovered.
        string held = Imported(identity, etl);
        Listed(identity, held, etl, 1);
        (InterCatExitCode code, string output, said) = await Run("import", etl, "--into", fresh);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"The catalogue of imports lists {damaged} as holding this file's import, but it cannot be reused.", said,
            StringComparison.Ordinal);
        Assert.Matches($@"(?m)^  Session +{Regex(held)}\r?$", output);
        Assert.Equal([held, damaged], ImportCatalogue.Read(Catalogue).Entries.Select(entry => entry.Path));
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    [Fact(DisplayName = "§8.1: the file is hashed only when the catalogue lists an import of a file its length, and an import of other bytes is not reused")]
    public async Task OnlyAnImportOfTheSameBytesIsReused()
    {
        string etl = Etl("trace.etl", 16);
        string fresh = Path.Combine(root.Path, "fresh");

        // Nothing listed, or only an import of a file of another length: the file is not hashed before it is read.
        (_, _, string said) = await Run("import", etl, "--into", fresh);
        Assert.DoesNotContain("Hashing", said, StringComparison.Ordinal);
        Assert.Contains(Compiling, said, StringComparison.Ordinal);
        string shorter = Path.Combine(root.Path, "short.etl");
        File.WriteAllBytes(shorter, Bytes(17)[..100]);
        Listed(IdentityOf(shorter), Path.Combine(root.Path, "short"), shorter, 1);
        (_, _, said) = await Run("import", etl, "--into", fresh);
        Assert.DoesNotContain("Hashing", said, StringComparison.Ordinal);

        // An import of other bytes of its length: hashed, compared, and neither verified nor reused.
        string other = Etl("other.etl", 18);
        string otherSession = Imported(IdentityOf(other), other);
        Listed(IdentityOf(other), otherSession, other, 2);
        (_, _, said) = await Run("import", etl, "--into", fresh);
        Assert.Contains($"Hashing {etl}: the catalogue of imports {Catalogue} lists 1 import of a file its length.", said,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Verifying", said, StringComparison.Ordinal);
        Assert.Contains(Compiling, said, StringComparison.Ordinal);
        Assert.Equal(
            [otherSession, Path.Combine(root.Path, "short")],
            ImportCatalogue.Read(Catalogue).Entries.Select(entry => entry.Path));
    }

    [Fact(DisplayName = "§8.1: a catalogue that cannot be read is named once and left as it is, and keeps no import from being reused or read")]
    public async Task ACatalogueThatCannotBeReadIsLeftAsItIs()
    {
        string etl = Etl("trace.etl", 19);
        string session = Imported(IdentityOf(etl), etl);
        Directory.CreateDirectory(Path.GetDirectoryName(Catalogue)!);
        foreach ((string content, string problem) in new[]
        {
            ("not a catalogue", $"{Catalogue} is not a catalogue of imports: "),
            ("{\"contract\":\"import-catalogue-v2\",\"imports\":[]}",
                $"{Catalogue} is a catalogue of import-catalogue-v2, which this InterCat does not read. "),
        })
        {
            File.WriteAllText(Catalogue, content);
            (InterCatExitCode code, _, string said) = await Run("import", etl, "--into", session);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains($"The catalogue of imports is not read: {problem}", said, StringComparison.Ordinal);
            Assert.Contains("It is left as it is, so no other folder is looked in for this file's import and none is recorded "
                + "in it; remove it, or pass --catalogue <file>, to keep a new one.", said, StringComparison.Ordinal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(said, "The catalogue of imports is not read"));
            Assert.DoesNotContain(".. ", said, StringComparison.Ordinal);

            (_, _, said) = await Run("import", etl, "--into", Path.Combine(root.Path, "fresh"));
            Assert.Contains($"The catalogue of imports is not read: {problem}", said, StringComparison.Ordinal);
            Assert.Contains(Compiling, said, StringComparison.Ordinal);
            Assert.Equal(content, File.ReadAllText(Catalogue));
        }
    }

    [Fact(DisplayName = "§8.1: --catalogue naming a folder is refused; a catalogue that cannot be written is named, and the import stands")]
    public async Task ACatalogueThatCannotBeWrittenIsNamed()
    {
        string etl = Etl("trace.etl", 20);
        string session = Imported(IdentityOf(etl), etl);

        (InterCatExitCode code, string output, string said) = await Run("import", etl, "--into", session, "--catalogue", root.Path);
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Contains($"{root.Path} is a folder. --catalogue names the file the catalogue of imports is kept in, such as "
            + $"{Path.Combine(root.Path, ImportCatalogue.FileName)}.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Hashing", said, StringComparison.Ordinal);

        // Kept in a folder that cannot be made, as a file holds its place.
        string blocked = Path.Combine(etl, ImportCatalogue.FileName);
        (code, output, said) = await Run("import", etl, "--into", session, "--catalogue", blocked);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("IMPORT REUSED", output, StringComparison.Ordinal);
        Assert.Contains($"{session} could not be recorded in the catalogue of imports {blocked}: ", said, StringComparison.Ordinal);
        Assert.Contains("An import of this file into another folder will read the file again rather than reuse this one.", said,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§8.1: icat import's help says each import is recorded, where the catalogue is kept and what --again does")]
    public async Task HelpSaysWhereTheCatalogueIsKept()
    {
        (InterCatExitCode code, string output, _) = await Run("import", "--help");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("[--journal-batch-records <n>] [--catalogue <file>] [--again]", output, StringComparison.Ordinal);
        Assert.Contains("Each import published or reused is recorded in your catalogue of imports, and", output,
            StringComparison.Ordinal);
        Assert.Contains("folder; --again imports the file into the new folder all the same. The", output, StringComparison.Ordinal);
        Assert.Contains($"catalogue is kept in {ImportCatalogue.DefaultPath()},", output, StringComparison.Ordinal);
    }

    /// <summary>Lists <paramref name="folder"/> in the catalogue as holding the import of <paramref name="identity"/>.</summary>
    private void Listed(ImportIdentity identity, string folder, string source, int minute) =>
        _ = ImportCatalogue.Update(
            Catalogue,
            new()
            {
                ImportDigest = identity.Digest,
                SessionId = identity.CaptureId.Value,
                Path = folder,
                SourcePath = source,
                SourceLength = identity.Source.Length,
                RecordedUtc = LongAgo.AddMinutes(minute),
            },
            []);
}
