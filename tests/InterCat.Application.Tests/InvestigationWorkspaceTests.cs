using System.Security.Cryptography;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// An investigation over separately valid sessions (§8.4, ADR-038): its workspace names each session by identity, one member
/// per capture, resolves each against where it was last found, and never writes to one.
/// </summary>
public sealed class InvestigationWorkspaceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "InterCat.Application.Tests.Workspace", Guid.NewGuid().ToString("N"));

    public InvestigationWorkspaceTests() => Directory.CreateDirectory(root);

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

    [Fact(DisplayName = "I9: a workspace names its sessions by identity and writes nothing to any of them")]
    public void AWorkspaceNamesItsSessionsAndWritesNothingToThem()
    {
        string workspace = Path.Combine(root, "case", "case" + InvestigationWorkspace.Extension);
        SessionStore alpha = NewSession(Path.Combine(root, "case", "alpha"), "lab-1");
        SessionStore beta = NewSession(Path.Combine(root, "elsewhere", "beta"), "lab-2", Guid.NewGuid(), CaptureId.New());
        IReadOnlyDictionary<string, string> before = Snapshot(alpha, beta);

        Assert.Equal(workspace, InvestigationWorkspace.PathFor(Path.Combine(root, "case", "case")));
        InvestigationWorkspaceFile created = InvestigationWorkspace.Create(workspace, Now);
        Assert.Equal((InvestigationWorkspace.Contract, 0), (created.Contract, created.Members.Count));
        WorkspaceMember first = InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now);
        WorkspaceMember second = InvestigationWorkspace.Add(workspace, beta.Root.Path, Now.AddMinutes(1));

        // A session under the workspace's folder is named relative to it; any other, by its whole path.
        Assert.Equal("alpha", first.Path);
        Assert.Equal(beta.Root.Path.Replace('\\', '/'), second.Path);
        SourceClockDescriptor clock = SessionSegments.SourceClock(alpha.Root, alpha.Current!)!.Value;
        Assert.Equal((alpha.Current!.SessionId, Capture.Value, alpha.Current.Generation, alpha.Current.Digest),
            (first.SessionId, first.CaptureId, first.Generation, first.ManifestDigest));
        Assert.Equal((HostId.Derive("lab-1").Value, clock.Id.Value, clock.CaptureEpochNativeTicks),
            (first.HostId, first.ClockId, first.CaptureEpochNativeTicks));

        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        Assert.Equal([first, second], read.Members);
        Assert.Equal((created.WorkspaceId, created.CreatedUtc, Now.AddMinutes(1)), (read.WorkspaceId, read.CreatedUtc, read.UpdatedUtc));
        Assert.All(InvestigationWorkspace.Resolve(workspace, read), resolution =>
            Assert.Equal((WorkspaceMemberState.Present, (string?)null), (resolution.State, resolution.Reason)));

        // Nothing was written to either session, and nothing was left beside the workspace.
        Assert.Equal(before, Snapshot(alpha, beta));
        Assert.Equal(["alpha", "case" + InvestigationWorkspace.Extension],
            Directory.EnumerateFileSystemEntries(Path.Combine(root, "case")).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // A workspace moved with the sessions under it still finds them; one named by its whole path, where it was.
        Directory.Move(Path.Combine(root, "case"), Path.Combine(root, "moved"));
        string moved = Path.Combine(root, "moved", "case" + InvestigationWorkspace.Extension);
        Assert.Equal([WorkspaceMemberState.Present, WorkspaceMemberState.Present],
            InvestigationWorkspace.Resolve(moved, InvestigationWorkspace.Read(moved)).Select(resolution => resolution.State));
    }

    [Fact(DisplayName = "R22: a capture is one member however many copies of it there are, and a store's source name makes none the same")]
    public void ACaptureIsOneMember()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now);
        string text = File.ReadAllText(workspace);

        // A copy of a session is the same capture.
        CopyDirectory(alpha.Root.Path, Path.Combine(root, "copy-of-alpha"));
        InvalidOperationException copy = Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Add(workspace, Path.Combine(root, "copy-of-alpha"), Now));
        Assert.Contains("already a member", copy.Message, StringComparison.Ordinal);

        // So is another session whose journal records the same capture.
        SessionStore again = NewSession(Path.Combine(root, "again"), "lab-1", Guid.NewGuid(), Capture);
        InvalidOperationException capture = Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Add(workspace, again.Root.Path, Now));
        Assert.Contains($"records capture {Capture.Value:N}", capture.Message, StringComparison.Ordinal);
        Assert.Equal(text, File.ReadAllText(workspace));

        // Two captures whose stores name one source - as every broker capture under one plan does - are two members.
        SessionStore planned = NewSession(Path.Combine(root, "planned-1"), "lab-1", Guid.NewGuid(), CaptureId.New(), "plan-digest");
        SessionStore plannedAgain = NewSession(Path.Combine(root, "planned-2"), "lab-1", Guid.NewGuid(), CaptureId.New(), "plan-digest");
        InvestigationWorkspace.Add(workspace, planned.Root.Path, Now);
        InvestigationWorkspace.Add(workspace, plannedAgain.Root.Path, Now);
        Assert.Equal(3, InvestigationWorkspace.Read(workspace).Members.Count);
    }

    [Fact(DisplayName = "R22: a member that moved, advanced or was replaced by another says so, and a relink takes only the member itself")]
    public void AMemberSaysWhyItIsNotPresent()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        SessionStore beta = NewSession(Path.Combine(root, "beta"), "lab-2", Guid.NewGuid(), CaptureId.New());
        WorkspaceMember member = InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now);

        // Moved: an unresolved reference, kept, until a relink finds the member itself.
        string moved = Path.Combine(root, "archive", "alpha");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(alpha.Root.Path, moved);
        WorkspaceMemberResolution missing = Single(workspace);
        Assert.Equal((WorkspaceMemberState.Missing, false), (missing.State, missing.HoldsItsCapture));
        Assert.Contains("moved or removed", missing.Reason, StringComparison.Ordinal);
        InvalidOperationException other = Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Relink(workspace, member.SessionId, beta.Root.Path, Now));
        Assert.Contains("relinked only to itself", other.Message, StringComparison.Ordinal);
        WorkspaceMember relinked = InvestigationWorkspace.Relink(workspace, member.SessionId, moved, Now.AddHours(1));
        Assert.Equal(("archive/alpha", member.AddedUtc), (relinked.Path, relinked.AddedUtc));
        Assert.Equal(WorkspaceMemberState.Present, Single(workspace).State);

        // Advanced: the session published a newer generation, which is said, and selected only by a relink.
        SessionStore reopened = SessionStore.Open(LocalOwnedDirectory.Open(moved), member.SessionId, "workspace-tests");
        Publish(reopened, Rows(2_000), capture: Capture, clock: SessionSegments.SourceClock(reopened.Root, reopened.Current!));
        reopened.ReleaseSegmentReaders();
        WorkspaceMemberResolution advanced = Single(workspace);
        Assert.Equal((WorkspaceMemberState.Advanced, 2L, true), (advanced.State, advanced.CurrentGeneration, advanced.HoldsItsCapture));
        Assert.Equal(2, InvestigationWorkspace.Relink(workspace, member.SessionId, moved, Now).Generation);
        Assert.Equal(WorkspaceMemberState.Present, Single(workspace).State);

        // Different: another session at its path. Unreadable: a directory holding no session, or a file.
        Directory.Move(moved, Path.Combine(root, "aside"));
        Directory.Move(beta.Root.Path, moved);
        WorkspaceMemberResolution different = Single(workspace);
        Assert.Equal(WorkspaceMemberState.Different, different.State);
        Assert.Contains("not this member", different.Reason, StringComparison.Ordinal);
        Directory.Move(moved, beta.Root.Path);
        Directory.CreateDirectory(moved);
        WorkspaceMemberResolution empty = Single(workspace);
        Assert.Equal(WorkspaceMemberState.Unreadable, empty.State);
        Assert.Contains("No session is published here", empty.Reason, StringComparison.Ordinal);
        Directory.Delete(moved);
        File.WriteAllText(moved, "not a session");
        Assert.Equal(WorkspaceMemberState.Unreadable, Single(workspace).State);
    }

    [Fact(DisplayName = "R22: a member put back to an earlier copy, or derived apart, is not the generation selected")]
    public void AReplacedMemberIsNotTheGenerationSelected()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        CopyDirectory(alpha.Root.Path, Path.Combine(root, "backup"));
        CopyDirectory(alpha.Root.Path, Path.Combine(root, "apart"));
        SourceClockDescriptor clock = SessionSegments.SourceClock(alpha.Root, alpha.Current!)!.Value;
        Publish(alpha, Rows(2_000), capture: Capture, clock: clock);
        alpha.ReleaseSegmentReaders();
        SessionStore apart = SessionStore.Open(LocalOwnedDirectory.Open(Path.Combine(root, "apart")), alpha.SessionId, "workspace-tests");
        Publish(apart, Rows(3_000), capture: Capture, clock: clock, committedUtc: Committed.AddHours(1));
        apart.ReleaseSegmentReaders();
        Assert.Equal(2, InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now).Generation);

        Directory.Move(alpha.Root.Path, Path.Combine(root, "newer"));
        Directory.Move(Path.Combine(root, "backup"), alpha.Root.Path);
        WorkspaceMemberResolution older = Single(workspace);
        Assert.Equal((WorkspaceMemberState.Replaced, 1L, true), (older.State, older.CurrentGeneration, older.HoldsItsCapture));
        Assert.Contains("older than the selected generation 2", older.Reason, StringComparison.Ordinal);

        Directory.Move(alpha.Root.Path, Path.Combine(root, "older"));
        Directory.Move(Path.Combine(root, "apart"), alpha.Root.Path);
        WorkspaceMemberResolution derivedApart = Single(workspace);
        Assert.Equal((WorkspaceMemberState.Replaced, 2L), (derivedApart.State, derivedApart.CurrentGeneration));
        Assert.Contains("derived apart", derivedApart.Reason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R22: hosts are one only by identity, and a name never makes two of them one")]
    public void HostsAreOneOnlyByIdentity()
    {
        string workspace = NewWorkspace();
        Guid first = Guid.Parse("11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        Guid second = Guid.Parse("11111111-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "a"), "lab-1", first, CaptureId.New()).Root.Path, Now);
        InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "b"), "lab-1", second, CaptureId.New()).Root.Path, Now);
        InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "c"), "lab-2", Guid.NewGuid(), CaptureId.New()).Root.Path, Now);

        Guid lab1 = HostId.Derive("lab-1").Value;
        Guid lab2 = HostId.Derive("lab-2").Value;
        IReadOnlyList<WorkspaceHost> hosts = InvestigationWorkspace.Hosts(InvestigationWorkspace.Read(workspace));
        Assert.Equal([lab1, lab2], hosts.Select(host => host.HostId));
        Assert.Equal([first, second], hosts[0].Members);

        InvestigationWorkspace.Alias(workspace, lab1, " build server ", Now);
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        Assert.Equal(["build server", null], InvestigationWorkspace.Hosts(read).Select(host => host.Alias));
        Assert.Equal(lab1, InvestigationWorkspace.HostNamed(read, "Build Server"));
        Assert.Equal(lab2, InvestigationWorkspace.HostNamed(read, lab2.ToString("N")[..8]));

        // One name for two identities would read as one host; a host no member was recorded on is no one's.
        Assert.Contains("already called", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Alias(workspace, lab2, "BUILD SERVER", Now)).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Alias(workspace, Guid.NewGuid(), "x", Now));
        InvestigationWorkspace.Alias(workspace, lab1, null, Now);
        Assert.Empty(InvestigationWorkspace.Read(workspace).HostAliases);

        // A member is named by its session identity or a leading part of it that only it has.
        Assert.Equal(second, InvestigationWorkspace.MemberNamed(read, "11111111-b").SessionId);
        Assert.Contains("give more of it", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.MemberNamed(read, "1111")).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.MemberNamed(read, "ffff"));
    }

    [Fact(DisplayName = "I9: a workspace file that holds anything else is refused whole, and a change made meanwhile is never written over")]
    public void AWorkspaceFileIsReadWholeOrRefused()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now);
        string text = File.ReadAllText(workspace);
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);

        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Create(workspace, Now));
        Assert.Contains("never in one", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Create(Path.Combine(alpha.Root.Path, "inside.icat-workspace"), Now)).Message, StringComparison.Ordinal);

        string[] refused =
        [
            text.Replace("\"workspace-v1\"", "\"workspace-v2\"", StringComparison.Ordinal),
            text.Replace("\"hostAliases\"", "\"notes\": [],\n  \"hostAliases\"", StringComparison.Ordinal),
            text.Replace("\"hostAliases\": []", "\"hostAliases\": null", StringComparison.Ordinal),
            text.Replace("\"hostAliases\": []", "\"hostAliases\": [{ \"hostId\": \"" + Guid.NewGuid() + "\", \"alias\": \" \" }]", StringComparison.Ordinal),
            "[]",
        ];
        foreach (string variant in refused)
        {
            Assert.NotEqual(text, variant);
            File.WriteAllText(workspace, variant);
            Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace));
        }

        // Two members of one capture, however written, are refused whole.
        WorkspaceMember member = read.Members[0];
        InvestigationWorkspace.Save(workspace, read with { Members = [member, member with { SessionId = Guid.NewGuid() }] }, readText: File.ReadAllText(workspace));
        Assert.Contains("count its records twice", Assert.Throws<InvalidDataException>(() =>
            InvestigationWorkspace.Read(workspace)).Message, StringComparison.Ordinal);

        // A write over text that changed since it was read is refused, and leaves the file as it was.
        File.WriteAllText(workspace, text);
        Assert.Contains("changed since it was read", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Save(workspace, read with { Members = [] }, readText: text + " ")).Message, StringComparison.Ordinal);
        Assert.Equal(text, File.ReadAllText(workspace));
        Assert.Equal([workspace], Directory.EnumerateFiles(root));
    }

    private string NewWorkspace()
    {
        string workspace = Path.Combine(root, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Now);
        return workspace;
    }

    private static WorkspaceMemberResolution Single(string workspace) =>
        Assert.Single(InvestigationWorkspace.Resolve(workspace, InvestigationWorkspace.Read(workspace)));

    /// <summary>A published session of one capture on <paramref name="host"/>, its segment readers released.</summary>
    private static SessionStore NewSession(
        string directory,
        string host,
        Guid? sessionId = null,
        CaptureId? capture = null,
        string source = "workspace-tests")
    {
        Directory.CreateDirectory(directory);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), sessionId ?? Session, source);
        Publish(store, Rows(1_000), capture: capture ?? Capture, clock: ClockFor(new ClockId(Guid.NewGuid()), host));
        store.ReleaseSegmentReaders();
        return store;
    }

    private static ObservationRowV1[] Rows(long ticks) =>
    [
        Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)ticks)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = ticks * 100 },
    ];

    /// <summary>Every file of these sessions, by path, with its length, last write and a digest of its bytes.</summary>
    private static Dictionary<string, string> Snapshot(params SessionStore[] stores) =>
        stores.SelectMany(store => Directory.EnumerateFiles(store.Root.Path)).ToDictionary(
            path => path,
            path => $"{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path):O}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}",
            StringComparer.OrdinalIgnoreCase);

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }
}
