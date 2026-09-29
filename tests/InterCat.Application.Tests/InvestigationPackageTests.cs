using System.Globalization;
using System.Security.Cryptography;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §8.4's export: an investigation packaged with its sessions opens wherever it is moved as the same investigation, each
/// copy found beside its file, and every session not copied a reference to relink.
/// </summary>
public sealed class InvestigationPackageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "InterCat.Application.Tests.Package", Guid.NewGuid().ToString("N"));

    public InvestigationPackageTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "R22: an investigation packaged with its sessions opens wherever it is moved as the same investigation")]
    public void APackageOpensAsTheSameInvestigation()
    {
        string workspace = Path.Combine(root, "case", "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Now);
        SessionStore alpha = NewSession(Path.Combine(root, "case", "alpha"), "lab-1");
        SessionStore beta = NewSession(Path.Combine(root, "elsewhere", "beta"), "lab-2");
        SessionStore gamma = NewSession(Path.Combine(root, "gone", "gamma"), "lab-2");
        Guid a = InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now).SessionId;
        WorkspaceMember b = InvestigationWorkspace.Add(workspace, beta.Root.Path, Now);
        Guid c = InvestigationWorkspace.Add(workspace, gamma.Root.Path, Now).SessionId;
        InvestigationWorkspace.Alias(workspace, b.HostId, "lab two", Now);
        InvestigationWorkspace.Align(workspace, b.SessionId, 0, a, 0, 1_000_000, null, "read off both consoles", Now);
        Directory.Move(Path.Combine(root, "gone"), Path.Combine(root, "went"));
        Dictionary<string, string> before = Snapshot(alpha, beta);
        string sourceText = File.ReadAllText(workspace);

        // What would be copied is measured first, and what the package would expose is said, its warning last.
        InvestigationPackagePreview preview = InvestigationPackage.Preview(workspace);
        Assert.Equal([a, b.SessionId], preview.Copied.Select(member => member.SessionId));
        Assert.Equal(alpha.Current!.Dependencies.Count + beta.Current!.Dependencies.Count, preview.Files);
        Assert.True(preview.Unredacted);
        IReadOnlyList<string> said = InvestigationPackage.Disclosure(preview, CultureInfo.InvariantCulture);
        Assert.StartsWith("This saves the investigation case.icat-workspace with an exact copy of 2 of its 3 sessions",
            said[0], StringComparison.Ordinal);
        Assert.Contains(said, paragraph => paragraph.StartsWith("Unredacted: ", StringComparison.Ordinal));
        Assert.Contains($"Hosts: 2, identified as {HostOf(alpha):N} and {b.HostId:N} (lab two).", said);
        Assert.Contains(said, paragraph => paragraph.Contains(
            "identities, one name given to a host, one alignment revision and one note written on them. Names and notes are "
            + "copied as they were written, never pseudonymized.", StringComparison.Ordinal));
        Assert.Contains(said, paragraph => paragraph.StartsWith($"Not copied: {Short(c)} (gamma), missing. The package keeps it",
            StringComparison.Ordinal));
        Assert.Equal(InvestigationPackage.Warning, said[^1]);

        string target = Path.Combine(root, "out", "package");
        var reports = new List<InvestigationPackageProgress>();
        InvestigationPackageResult result = InvestigationPackage.Create(workspace, target, progress: new Synchronous(reports.Add));
        Assert.Equal((target, Path.Combine(target, "case" + InvestigationWorkspace.Extension)), (result.Directory, result.WorkspacePath));
        Assert.Equal(
        [
            (a, (string?)$"sessions/{Short(a)}-alpha", (long?)1L, WorkspaceMemberState.Present),
            (b.SessionId, $"sessions/{Short(b.SessionId)}-beta", 1L, WorkspaceMemberState.Present),
            (c, null, null, WorkspaceMemberState.Missing),
        ], result.Members.Select(member => (member.SessionId, member.PackagedPath, member.Generation, member.State)));
        Assert.StartsWith("Not copied, and kept as a reference: it is missing. Nothing is at this path", result.Members[2].Note, StringComparison.Ordinal);
        Assert.Equal((preview.Files, preview.Bytes), (result.Files, result.Bytes));
        Assert.Equal([1, 2], reports.Select(report => report.Session).Distinct());
        Assert.All(reports, report => Assert.Equal(2, report.Sessions));
        Assert.Equal(preview.Bytes, reports[^1].Done);

        // The package holds its file and the copies, nothing else, and nothing is left beside it; no source was touched.
        Assert.Equal(["case" + InvestigationWorkspace.Extension, "sessions"],
            Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { $"{Short(a)}-alpha", $"{Short(b.SessionId)}-beta" }.Order(StringComparer.Ordinal),
            Directory.EnumerateFileSystemEntries(Path.Combine(target, "sessions")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["package"], Directory.EnumerateFileSystemEntries(Path.Combine(root, "out")).Select(Path.GetFileName));
        Assert.Equal(before, Snapshot(alpha, beta));
        Assert.Equal(sourceText, File.ReadAllText(workspace));

        // It is the same investigation: its identity, time, names and revisions, each member only found elsewhere.
        InvestigationWorkspaceFile source = InvestigationWorkspace.Read(workspace);
        InvestigationWorkspaceFile packaged = InvestigationWorkspace.Read(result.WorkspacePath);
        Assert.Equal((source.WorkspaceId, source.CreatedUtc, source.UpdatedUtc, source.TimeReference),
            (packaged.WorkspaceId, packaged.CreatedUtc, packaged.UpdatedUtc, packaged.TimeReference));
        Assert.Equal(source.HostAliases, packaged.HostAliases);
        Assert.Equal(source.Alignments, packaged.Alignments);
        Assert.Equal(source.Joins, packaged.Joins);
        Assert.Equal(source.Members.Select(member => member with { Path = string.Empty }),
            packaged.Members.Select(member => member with { Path = string.Empty }));
        Assert.Equal(Path.Combine(root, "gone", "gamma").Replace('\\', '/'), packaged.Members[2].Path);

        // Moved anywhere, its copies are still found beside its file, and the session not copied is still to relink.
        string moved = Path.Combine(root, "received", "case-from-a-colleague");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(target, moved);
        string received = Path.Combine(moved, "case" + InvestigationWorkspace.Extension);
        Assert.Equal([WorkspaceMemberState.Present, WorkspaceMemberState.Present, WorkspaceMemberState.Missing],
            InvestigationWorkspace.Resolve(received, InvestigationWorkspace.Read(received)).Select(resolution => resolution.State));
        InvestigationWorkspace.Relink(received, c, Path.Combine(root, "went", "gamma"), Now);
        Assert.All(InvestigationWorkspace.Resolve(received, InvestigationWorkspace.Read(received)),
            resolution => Assert.Equal(WorkspaceMemberState.Present, resolution.State));
        Assert.Equal(sourceText, File.ReadAllText(workspace));
    }

    [Fact(DisplayName = "R22: a package goes only to a new folder outside every session, and one that stops leaves nothing")]
    public void APackageGoesOnlyToANewFolderAndLeavesNothingWhenItStops()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        SessionStore beta = NewSession(Path.Combine(root, "beta"), "lab-2");
        InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now);
        InvestigationWorkspace.Add(workspace, beta.Root.Path, Now);
        Dictionary<string, string> before = Snapshot(alpha, beta);

        Directory.CreateDirectory(Path.Combine(root, "taken"));
        Assert.Contains("exists", Assert.Throws<InvalidOperationException>(() =>
            InvestigationPackage.Create(workspace, Path.Combine(root, "taken"))).Message, StringComparison.Ordinal);
        Assert.Contains("inside the session", Assert.Throws<InvalidOperationException>(() =>
            InvestigationPackage.Create(workspace, Path.Combine(beta.Root.Path, "nested", "package"))).Message, StringComparison.Ordinal);

        // Stopped in the second copy: the first copy and the private folder go with it, and nothing has the package's name.
        using var cancellation = new CancellationTokenSource();
        string target = Path.Combine(root, "out", "package");
        Assert.ThrowsAny<OperationCanceledException>(() => InvestigationPackage.Create(workspace, target,
            progress: new Synchronous(report =>
            {
                if (report.Session == 2) cancellation.Cancel();
            }), cancellationToken: cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "out")));
        Assert.Equal(before, Snapshot(alpha, beta));
        Assert.False(Directory.Exists(Path.Combine(beta.Root.Path, "nested")));
    }

    [Fact(DisplayName = "R22: a person chooses the sessions a package copies, and one that moved on is copied as it is, still to relink")]
    public void APersonChoosesWhatIsCopied()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        SessionStore beta = NewSession(Path.Combine(root, "beta"), "lab-2");
        SessionStore gamma = NewSession(Path.Combine(root, "gamma"), "lab-2");
        Guid a = InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, beta.Root.Path, Now).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, gamma.Root.Path, Now).SessionId;
        Directory.Delete(gamma.Root.Path, recursive: true);

        // Only what is chosen is copied; a member that is not a member, or cannot be copied, is refused by name.
        InvestigationPackagePreview preview = InvestigationPackage.Preview(workspace);
        InvestigationPackagePreview chosen = InvestigationPackage.Select(preview, [b]);
        Assert.Equal([b], chosen.Copied.Select(member => member.SessionId));
        Assert.Equal("Not selected, so kept as a reference to where it is on this computer.", chosen.Members[0].LeftOut);
        Assert.StartsWith("This saves the investigation case.icat-workspace with an exact copy of 1 of its 3 sessions",
            InvestigationPackage.Disclosure(chosen, CultureInfo.InvariantCulture)[0], StringComparison.Ordinal);
        Assert.Contains("No member", Assert.Throws<InvalidOperationException>(() =>
            InvestigationPackage.Select(preview, [Guid.NewGuid()])).Message, StringComparison.Ordinal);
        Assert.Contains($"Session {Short(c)} is missing, so it cannot be copied.", Assert.Throws<InvalidOperationException>(() =>
            InvestigationPackage.Select(preview, [b, c])).Message, StringComparison.Ordinal);
        Assert.Equal("No session of case.icat-workspace would be copied, so there is nothing to package. Select a session that "
            + "is where the investigation last found it.",
            Assert.Single(InvestigationPackage.Disclosure(InvestigationPackage.Select(preview, []), CultureInfo.InvariantCulture)));

        InvestigationPackageResult only = InvestigationPackage.Create(workspace, Path.Combine(root, "only-beta"), [b]);
        Assert.Equal([(a, (string?)null, WorkspaceMemberState.Present), (b, $"sessions/{Short(b)}-beta", WorkspaceMemberState.Present),
            (c, null, WorkspaceMemberState.Missing)], only.Members.Select(member => (member.SessionId, member.PackagedPath, member.State)));
        Assert.Equal(alpha.Root.Path.Replace('\\', '/'), InvestigationWorkspace.Read(only.WorkspacePath).Members[0].Path);

        // Alpha publishes a newer generation: it is copied as it is, and reads as advanced in the package as it does here.
        SessionStore reopened = SessionStore.Open(LocalOwnedDirectory.Open(alpha.Root.Path), a, "package-tests");
        Publish(reopened, Rows(2_000), capture: CaptureOf(alpha), clock: SessionSegments.SourceClock(reopened.Root, reopened.Current!));
        reopened.ReleaseSegmentReaders();
        InvestigationPackagePreview advanced = InvestigationPackage.Preview(workspace);
        Assert.Contains(InvestigationPackage.Disclosure(advanced, CultureInfo.InvariantCulture), paragraph => paragraph.StartsWith(
            $"Session {Short(a)} (alpha) is copied as it is now, generation 2, which the investigation does not select: it selects "
            + "generation 1, so the copy reads as advanced in the package", StringComparison.Ordinal));
        InvestigationPackageResult all = InvestigationPackage.Create(workspace, Path.Combine(root, "all"));
        InvestigationPackageMember copied = all.Members[0];
        Assert.Equal((2L, WorkspaceMemberState.Advanced), (copied.Generation!.Value, copied.State));
        Assert.Contains("has published generation 2 since generation 1 was selected", copied.Note, StringComparison.Ordinal);
        Assert.Equal(1, InvestigationWorkspace.Read(all.WorkspacePath).Members[0].Generation);

        // An investigation none of whose sessions is where it was last found has nothing to package.
        string lost = Path.Combine(root, "lost" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(lost, Now);
        SessionStore delta = NewSession(Path.Combine(root, "delta"), "lab-3");
        InvestigationWorkspace.Add(lost, delta.Root.Path, Now);
        Directory.Delete(delta.Root.Path, recursive: true);
        Assert.Contains("nothing to copy", Assert.Throws<InvalidOperationException>(() =>
            InvestigationPackage.Create(lost, Path.Combine(root, "nothing"))).Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "nothing")));
    }

    [Fact(DisplayName = "§11.3: a package of redacted packages only is stated as pseudonymized, never unredacted")]
    public void APackageOfRedactedPackagesIsNeverCalledUnredacted()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        string redacted = Path.Combine(root, "alpha-redacted");
        _ = RedactedSessionPackage.Create(alpha, redacted, DateTimeOffset.UnixEpoch);
        InvestigationWorkspace.Add(workspace, redacted, Now);

        InvestigationPackagePreview preview = InvestigationPackage.Preview(workspace);
        Assert.False(preview.Unredacted);
        IReadOnlyList<string> said = InvestigationPackage.Disclosure(preview, CultureInfo.InvariantCulture);
        Assert.Contains(OriginalEvidencePackage.RedactedContents, said);
        Assert.Equal(RedactedSessionPackage.Warning, said[^1]);
        Assert.DoesNotContain(said, paragraph => paragraph.Contains("Unredacted", StringComparison.OrdinalIgnoreCase));

        // Beside a session as recorded, the package is unredacted, and says which of its copies are redacted packages.
        SessionStore beta = NewSession(Path.Combine(root, "beta"), "lab-2");
        InvestigationWorkspace.Add(workspace, beta.Root.Path, Now);
        IReadOnlyList<string> mixed = InvestigationPackage.Disclosure(InvestigationPackage.Preview(workspace), CultureInfo.InvariantCulture);
        Assert.Equal(InvestigationPackage.Warning, mixed[^1]);
        Guid pseudonymous = InvestigationWorkspace.Read(workspace).Members[0].SessionId;
        Assert.Contains($"Session {Short(pseudonymous)} is itself a redacted package, copied as it is: its pseudonyms and "
            + "synthetic records, never the original values.", mixed);
    }

    private string NewWorkspace()
    {
        string workspace = Path.Combine(root, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Now);
        return workspace;
    }

    /// <summary>A published session of a capture of its own on <paramref name="host"/>, its segment readers released.</summary>
    private static SessionStore NewSession(string directory, string host)
    {
        Directory.CreateDirectory(directory);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "package-tests");
        Publish(store, Rows(1_000), capture: CaptureId.New(), clock: ClockFor(new ClockId(Guid.NewGuid()), host));
        store.ReleaseSegmentReaders();
        return store;
    }

    private static CaptureId CaptureOf(SessionStore store) => SessionSegments.Source(store.Root, store.Current!)!.Value.Capture;

    private static Guid HostOf(SessionStore store) => SessionSegments.Source(store.Root, store.Current!)!.Value.Clock.HostId.Value;

    private static ObservationRowV1[] Rows(long ticks) =>
    [
        Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)ticks)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = ticks * 100 },
    ];

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    /// <summary>Every file of these sessions, by path, with its length, last write and a digest of its bytes.</summary>
    private static Dictionary<string, string> Snapshot(params SessionStore[] stores) =>
        stores.SelectMany(store => Directory.EnumerateFiles(store.Root.Path)).ToDictionary(
            path => path,
            path => $"{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path):O}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}",
            StringComparer.OrdinalIgnoreCase);

    private sealed class Synchronous(Action<InvestigationPackageProgress> report) : IProgress<InvestigationPackageProgress>
    {
        public void Report(InvestigationPackageProgress value) => report(value);
    }
}
