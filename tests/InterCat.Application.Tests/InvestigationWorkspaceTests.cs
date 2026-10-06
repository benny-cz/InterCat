using System.Globalization;
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

    [Fact(DisplayName = "§26.3: a workspace file that cannot be read says where to look - a line, the fields it lacks or holds - never the parser's words")]
    public void AnUnreadableWorkspaceSaysWhereToLook()
    {
        string workspace = Path.Combine(root, "case" + InvestigationWorkspace.Extension);
        string Refusal(string text)
        {
            File.WriteAllText(workspace, text);
            string message = Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace)).Message;
            Assert.StartsWith(workspace + " is not a readable workspace: ", message, StringComparison.Ordinal);
            string reason = message[(workspace.Length + " is not a readable workspace: ".Length)..];
            Assert.DoesNotContain("InterCat.", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("BytePositionInLine", reason, StringComparison.Ordinal);
            return reason;
        }

        // Text that is not JSON, or JSON cut off, by the line and character where it stops being JSON.
        Assert.Equal("it is not valid JSON at line 1, character 1.", Refusal("hello"));
        Assert.Equal("it is not valid JSON at line 3, character 1.", Refusal("{\n  \"contract\": \"workspace-v12\",\n"));

        // JSON that is not a workspace, by the fields it lacks or holds, or the value that does not fit.
        Assert.Equal("it holds no object of fields.", Refusal("[1, 2]"));
        Assert.Equal("it lacks the fields contract, workspaceId, createdUtc, updatedUtc, members, hostAliases.", Refusal("{}"));
        string valid = """{"contract":"workspace-v12","workspaceId":"00000000-0000-0000-0000-000000000001","createdUtc":"2026-01-01T00:00:00Z","updatedUtc":"2026-01-01T00:00:00Z","members":[],"hostAliases":[]""";
        Assert.Equal("it holds a field this version does not know: colour.", Refusal(valid + ",\"colour\":\"red\"}"));
        Assert.Equal("its value at $.members is not of the kind that field holds.", Refusal(valid.Replace("\"members\":[]", "\"members\":5", StringComparison.Ordinal) + "}"));
        File.WriteAllText(workspace, valid + "}");
        Assert.Empty(InvestigationWorkspace.Read(workspace).Members);
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
        Assert.Equal(SessionStore.NoGeneration, empty.Reason);
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

    [Fact(DisplayName = "R22: a member whose newest generation does not verify is its last complete one, said as such and not as a copy")]
    public void AMemberWhoseNewestGenerationFailsSaysWhy()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        string[] first = [.. alpha.Current!.Dependencies.Select(dependency => dependency.Name)];
        Publish(alpha, Rows(2_000), capture: Capture, clock: SessionSegments.SourceClock(alpha.Root, alpha.Current!));
        alpha.ReleaseSegmentReaders();
        Assert.Equal(2, InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now).Generation);

        // A file only the newest generation needs is cut short, which a viewer's open finds from the listing.
        StoreDependency newest = alpha.Current!.Dependencies.First(dependency =>
            dependency.Kind == StoreDependencyKind.Segment && !first.Contains(dependency.Name));
        string path = Path.Combine(alpha.Root.Path, newest.Name);
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..^1]);

        WorkspaceMemberResolution failed = Single(workspace);
        Assert.Equal((WorkspaceMemberState.Replaced, 1L), (failed.State, failed.CurrentGeneration));
        Assert.Equal(
            "The path holds generation 1 of this session, older than the selected generation 2. Its current generation could "
            + $"not be read, so its last-known-good was: {newest.LengthMismatch(newest.LengthBytes - 1, 2)}. Relink the member "
            + "to its own path to select what is there.",
            failed.Reason);
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
            text.Replace($"\"{InvestigationWorkspace.Contract}\"", "\"workspace-v999\"", StringComparison.Ordinal),
            text.Replace("\"hostAliases\"", "\"aFieldNoVersionDefines\": [],\n  \"hostAliases\"", StringComparison.Ordinal),
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

    [Fact(DisplayName = "I9: a manual alignment is an annotation, kept and reopened as recorded, and changes no timestamp")]
    public void AManualAlignmentIsAnAnnotation()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        SessionStore beta = NewSession(Path.Combine(root, "beta"), "lab-2", Guid.NewGuid(), CaptureId.New());
        Guid a = InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, beta.Root.Path, Now).SessionId;
        IReadOnlyDictionary<string, string> before = Snapshot(alpha, beta);

        // Beta's 2 s is alpha's 5 s, within 500 µs, the clocks drifting apart by at most 50 ppm: alpha's clock becomes the
        // workspace's time.
        WorkspaceAlignment aligned = InvestigationWorkspace.Align(workspace, b, Seconds(2), a, Seconds(5), 500_000, 50, " shared connect ", Now);
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        Assert.Equal((a, 1, "shared connect"), (read.TimeReference, aligned.Revision, aligned.Note));
        Assert.Equal([aligned], read.Alignments);
        Assert.Equal(Seconds(5), InvestigationWorkspace.Place(read, b, Seconds(2)).WorkspaceNanoseconds);
        Assert.Equal(new TimeUncertainty(500_000, 0), InvestigationWorkspace.Place(read, b, Seconds(2)).Uncertainty);
        Assert.Equal(new TimeUncertainty(600_000, 0), InvestigationWorkspace.Place(read, b, Seconds(4)).Uncertainty);
        Assert.Equal((Seconds(7), TimeUncertainty.Exact), Placed(InvestigationWorkspace.Place(read, a, Seconds(7))));
        Assert.Equal((WorkspaceTimeGap.None, (long?)null), (InvestigationWorkspace.Place(read, a, Seconds(7)).Gap, InvestigationWorkspace.Place(read, a, Seconds(7)).FromAnchorNanoseconds));
        Assert.Equal(Seconds(2), InvestigationWorkspace.Place(read, b, Seconds(4)).FromAnchorNanoseconds);

        // Withdrawn, the member has no workspace time, and with no member aligned the workspace has no time reference; both
        // revisions are kept.
        InvestigationWorkspace.Withdraw(workspace, b, Now.AddMinutes(1));
        read = InvestigationWorkspace.Read(workspace);
        Assert.Null(read.TimeReference);
        Assert.Equal([WorkspaceAlignmentMode.Manual, WorkspaceAlignmentMode.Withdrawn], read.Alignments.Select(alignment => alignment.Mode));
        Assert.Equal(WorkspaceTimeGap.NoTimeReference, InvestigationWorkspace.Place(read, b, Seconds(2)).Gap);
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Withdraw(workspace, b, Now));

        // Any member may then be the reference. Its sessions were never written to.
        InvestigationWorkspace.Align(workspace, a, Seconds(5), b, Seconds(2), 1_000_000, null, null, Now.AddMinutes(2));
        read = InvestigationWorkspace.Read(workspace);
        Assert.Equal((b, 3), (read.TimeReference, read.Alignments[^1].Revision));
        Assert.Equal(before, Snapshot(alpha, beta));
    }

    [Fact(DisplayName = "R21: a workspace orders instants across members only as far as their alignments allow")]
    public void AWorkspaceOrdersInstantsOnlyAsFarAsItsAlignmentsAllow()
    {
        string workspace = NewWorkspace();
        Guid a = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "a"), "lab-1").Root.Path, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "b"), "lab-2", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "c"), "lab-3", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        Guid d = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "d"), "lab-4", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        InvestigationWorkspace.Align(workspace, b, Seconds(2), a, Seconds(5), 500_000, 50, null, Now);
        InvestigationWorkspace.Align(workspace, c, Seconds(1), a, Seconds(5), 1_000_000, null, null, Now);
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);

        // Within the anchor's bound, no order; beyond it, an order with the pair's uncertainty.
        WorkspaceComparison close = InvestigationWorkspace.Compare(read, a, Seconds(5), b, Seconds(2) + 400_000);
        Assert.Equal((TimeOrder.Ambiguous, 400_000L), (close.Result.Order, close.Result.DifferenceNanoseconds));
        WorkspaceComparison apart = InvestigationWorkspace.Compare(read, a, Seconds(5), b, Seconds(2) + 1_000_000);
        Assert.Equal(TimeOrder.Before, apart.Result.Order);
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"The first is before the second by {1.0m:N1} ms, more than their combined uncertainty of ±{501m:N0} µs."),
            apart.Statement(CultureInfo.CurrentCulture));

        // Two aligned members' bounds add: 500 µs and 1 ms make 1.5 ms. Away from its anchor, a member with no drift bound
        // has an unknown uncertainty, so nothing is stated, not even the difference; nor for a member not aligned at all.
        Assert.Equal(1_500_000, InvestigationWorkspace.Compare(read, b, Seconds(2), c, Seconds(1)).Result.Uncertainty!.Value.HalfWidthNanoseconds);
        WorkspaceComparison drifting = InvestigationWorkspace.Compare(read, b, Seconds(2), c, Seconds(3));
        Assert.Equal(new TimeComparison(TimeOrder.Unknown, null, null), drifting.Result);
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"No order is stated: the second instant's session's drift from the time reference is not stated, so {2.0m:N1} s from the nearest instant it was aligned at, its uncertainty is unknown."),
            drifting.Statement(CultureInfo.CurrentCulture));
        Assert.Equal("No order is stated: the second instant's session is not aligned to the workspace's time.",
            InvestigationWorkspace.Compare(read, a, Seconds(1), d, Seconds(1)).Statement(CultureInfo.CurrentCulture));

        // Two instants of one member are ordered exactly, on its one clock, aligned or not.
        WorkspaceComparison one = InvestigationWorkspace.Compare(read, d, Seconds(3), d, Seconds(1));
        Assert.Equal((TimeOrder.After, TimeUncertainty.Exact), (one.Result.Order, one.Result.Uncertainty));
        Assert.EndsWith("on one clock.", one.Statement(CultureInfo.CurrentCulture), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R22: a member is aligned to the workspace's one time reference, and a file whose time contradicts itself is refused")]
    public void AnAlignmentIsToTheOneTimeReference()
    {
        string workspace = NewWorkspace();
        Guid a = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "a"), "lab-1").Root.Path, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "b"), "lab-2", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "c"), "lab-3", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        string members = File.ReadAllText(workspace);
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000, 10, null, Now);

        Assert.Contains("is the workspace's time reference", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Align(workspace, a, 0, b, 0, 1_000, 10, null, Now)).Message, StringComparison.Ordinal);
        Assert.Contains("has no place in the workspace's time", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Align(workspace, b, 0, c, 0, 1_000, 10, null, Now)).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Align(workspace, c, 0, c, 0, 1_000, 10, null, Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Align(workspace, c, 0, a, 0, -1, 10, null, Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Align(workspace, c, 0, a, 0, 1_000, -10, null, Now));
        string aligned = File.ReadAllText(workspace);

        // Revision 253's files, which hold no time, are read, and written as the current version.
        File.WriteAllText(workspace, members.Replace($"\"{InvestigationWorkspace.Contract}\"", "\"workspace-v1\"", StringComparison.Ordinal));
        Assert.Equal(3, InvestigationWorkspace.Read(workspace).Members.Count);
        InvestigationWorkspace.Alias(workspace, InvestigationWorkspace.Read(workspace).Members[0].HostId, "lab", Now);
        Assert.Equal(InvestigationWorkspace.Contract, InvestigationWorkspace.Read(workspace).Contract);

        // Revision 254's files hold manual alignments, and are read.
        File.WriteAllText(workspace, aligned.Replace($"\"{InvestigationWorkspace.Contract}\"", "\"workspace-v2\"", StringComparison.Ordinal));
        Assert.Single(InvestigationWorkspace.Read(workspace).Alignments);

        // A file whose time contradicts itself is refused whole.
        string[] refused =
        [
            aligned.Replace($"\"{InvestigationWorkspace.Contract}\"", "\"workspace-v1\"", StringComparison.Ordinal),
            aligned.Replace($"\"timeReference\": \"{a}\"", $"\"timeReference\": \"{c}\"", StringComparison.Ordinal),
            aligned.Replace($"\"timeReference\": \"{a}\"", $"\"timeReference\": \"{Guid.NewGuid()}\"", StringComparison.Ordinal),
            aligned.Replace("\"withinNanoseconds\": 1000", "\"withinNanoseconds\": -1", StringComparison.Ordinal),
            aligned.Replace("\"mode\": \"Manual\"", "\"mode\": \"Withdrawn\"", StringComparison.Ordinal),
            aligned.Replace("\"revision\": 1", "\"revision\": 0", StringComparison.Ordinal),
        ];
        foreach (string variant in refused)
        {
            Assert.NotEqual(aligned, variant);
            File.WriteAllText(workspace, variant);
            Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace));
        }
    }

    [Fact(DisplayName = "R22: two captures of one boot align exactly through their epochs, and captures of two boots never do")]
    public void OneBootsCapturesAlignExactly()
    {
        string workspace = NewWorkspace();
        Guid boot = Guid.NewGuid();
        Guid a = InvestigationWorkspace.Add(workspace, CalibratedSession(Path.Combine(root, "a"), "lab-1", 1_000_000, boot).Root.Path, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, CalibratedSession(Path.Combine(root, "b"), "lab-1", 51_000_000, boot).Root.Path, Now).SessionId;

        // B's epoch is 50,000,000 ticks of 100 ns after A's: its instant 0 is A's 5 s, exactly, with no drift.
        WorkspaceAlignment same = InvestigationWorkspace.AlignSameBoot(workspace, b, a, null, Now);
        Assert.Equal((WorkspaceAlignmentMode.SameBoot, 0L, Seconds(5), 0L, 0.0, (Guid?)boot),
            (same.Mode, same.SessionNanoseconds, same.ReferenceNanoseconds, same.WithinNanoseconds, same.DriftPartsPerMillion, same.BootToken));
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        WorkspaceComparison tie = InvestigationWorkspace.Compare(read, a, Seconds(5), b, 0);
        Assert.Equal((TimeOrder.Ambiguous, (TimeUncertainty?)TimeUncertainty.Exact), (tie.Result.Order, tie.Result.Uncertainty));
        Assert.Equal("Both are one instant, exactly, so no order between them is stated.", tie.Statement(CultureInfo.InvariantCulture));
        Assert.Equal((TimeOrder.Before, 100L), (InvestigationWorkspace.Compare(read, a, Seconds(5), b, 100).Result.Order,
            InvestigationWorkspace.Compare(read, a, Seconds(5), b, 100).Result.DifferenceNanoseconds!.Value));

        // Another boot, no calibration, or no boot token: nothing is aligned.
        Guid other = InvestigationWorkspace.Add(workspace, CalibratedSession(Path.Combine(root, "c"), "lab-1", 1_000_000, Guid.NewGuid()).Root.Path, Now).SessionId;
        Assert.Contains("ran in different boots", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.AlignSameBoot(workspace, other, a, null, Now)).Message, StringComparison.Ordinal);
        Guid plain = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "d"), "lab-1", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        Assert.Contains("records no clock calibration", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.AlignSameBoot(workspace, plain, a, null, Now)).Message, StringComparison.Ordinal);
        Guid unbooted = InvestigationWorkspace.Add(workspace, CalibratedSession(Path.Combine(root, "e"), "lab-1", 1_000_000, null).Root.Path, Now).SessionId;
        Assert.Contains("recorded no boot token", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.AlignSameBoot(workspace, unbooted, a, null, Now)).Message, StringComparison.Ordinal);

        // A version 2 file holds manual alignments only.
        string text = File.ReadAllText(workspace);
        File.WriteAllText(workspace, text.Replace($"\"{InvestigationWorkspace.Contract}\"", "\"workspace-v2\"", StringComparison.Ordinal));
        Assert.Contains("holds only manual alignments", Assert.Throws<InvalidDataException>(() =>
            InvestigationWorkspace.Read(workspace)).Message, StringComparison.Ordinal);

        // A tick that is no whole number of nanoseconds rounds each side's session time and the offset: ±2 ns.
        string rounded = Path.Combine(root, "rounded", "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(rounded, Now);
        Guid g = InvestigationWorkspace.Add(rounded, CalibratedSession(Path.Combine(root, "rounded", "g"), "lab-1", 1_000, boot, 3_000_000).Root.Path, Now).SessionId;
        Guid h = InvestigationWorkspace.Add(rounded, CalibratedSession(Path.Combine(root, "rounded", "h"), "lab-1", 1_001, boot, 3_000_000).Root.Path, Now).SessionId;
        WorkspaceAlignment third = InvestigationWorkspace.AlignSameBoot(rounded, h, g, null, Now);
        Assert.Equal((333L, 2L), (third.ReferenceNanoseconds!.Value, third.WithinNanoseconds!.Value));
    }

    [Fact(DisplayName = "R3: a wall-clock alignment is bounded by the clocks' stated agreement, the samples' acquisition and the stated drift")]
    public void AWallClockAlignmentStatesItsBounds()
    {
        string workspace = NewWorkspace();
        ClockCalibrationSampleV1 Sample(long ticks, int second, long uncertainty) =>
            new() { NativeTicks = ticks, Utc = Now.AddSeconds(second), AcquisitionUncertaintyNanoseconds = uncertainty };
        Guid a = InvestigationWorkspace.Add(workspace, CalibratedSession(Path.Combine(root, "a"), "lab-1", 1_000_000, Guid.NewGuid(),
            samples: [Sample(1_000_000, 0, 200), Sample(31_000_000, 3, 200)]).Root.Path, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, CalibratedSession(Path.Combine(root, "b"), "lab-2", 5_000_000, Guid.NewGuid(),
            samples: [Sample(5_000_000, 1, 300), Sample(35_000_000, 4, 300)]).Root.Path, Now).SessionId;

        // The samples taken closest in wall-clock time - B's start, a second after A's - anchor it: B's 0 s is A's 1 s, within
        // the stated 1 ms agreement, 500 ns of acquisition, and 10 ppm over the second between the samples.
        WorkspaceAlignment wall = InvestigationWorkspace.AlignByWallClock(workspace, b, a, 1_000_000, 10, null, Now);
        Assert.Equal((WorkspaceAlignmentMode.WallClock, 0L, Seconds(1), 1_010_500L),
            (wall.Mode, wall.SessionNanoseconds!.Value, wall.ReferenceNanoseconds!.Value, wall.WithinNanoseconds!.Value));
        Assert.Equal(((long?)1_000_000, (long?)500, (long?)Seconds(1)), (wall.SynchronizationNanoseconds, wall.AcquisitionNanoseconds, wall.GapNanoseconds));
        Assert.Equal([1_000_000.0, 500.0, 10_000.0],
            InvestigationWorkspace.MappingOf(wall).Contributions.Take(3).Select(contribution => contribution.Nanoseconds!.Value));

        // Away from the anchor the stated drift grows: 2 s later, 20 µs more; an order is stated only beyond the pair's bound.
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        Assert.Equal(new TimeUncertainty(1_030_500, 0), InvestigationWorkspace.Place(read, b, Seconds(2)).Uncertainty);
        Assert.Equal(TimeOrder.Ambiguous, InvestigationWorkspace.Compare(read, a, Seconds(3), b, Seconds(2)).Result.Order);
        Assert.Equal(TimeOrder.After, InvestigationWorkspace.Compare(read, a, Seconds(3) + 2_000_000, b, Seconds(2)).Result.Order);
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.AlignByWallClock(workspace, b, a, -1, 10, null, Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.AlignByWallClock(workspace, b, a, 1_000, double.NaN, null, Now));
    }

    [Fact(DisplayName = "R22: a saved view is a named interval of the investigation's time, kept in its reference's clock")]
    public void ASavedViewIsKeptInItsReferencesClock()
    {
        string workspace = NewWorkspace();
        Guid a = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "alpha"), "lab-1").Root.Path, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "beta"), "lab-2", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;

        // With no time yet there is nothing to view.
        Assert.Contains("no time yet", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.SaveView(workspace, "Upload", new TimeRange(0, 10), Now)).Message, StringComparison.Ordinal);
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000, 1, null, Now);

        // A view is saved by name in the reference's clock; one of that name, in any case, replaces it.
        WorkspaceView first = InvestigationWorkspace.SaveView(workspace, " Upload ", new TimeRange(10_000_000, 20_000_000), Now);
        InvestigationWorkspace.SaveView(workspace, "upload", new TimeRange(12_000_000, 18_000_000), Now);
        InvestigationWorkspace.SaveView(workspace, "Handshake", new TimeRange(0, 1_000_000), Now);
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        Assert.Equal(("Upload", (Guid?)a), (first.Name, first.Reference));
        Assert.Equal([("upload", new TimeRange(12_000_000, 18_000_000)), ("Handshake", new TimeRange(0, 1_000_000))],
            InvestigationWorkspace.ViewsInForce(read).Select(view => (view.Name, view.Interval!.Value)));
        Assert.All(InvestigationWorkspace.ViewsInForce(read), view => Assert.True(InvestigationWorkspace.ViewIsCurrent(read, view)));

        // Removed, a view is gone; when the time reference changes, the rest are kept and are not of the time now.
        InvestigationWorkspace.RemoveView(workspace, "HANDSHAKE", Now);
        InvestigationWorkspace.Withdraw(workspace, b, Now);
        InvestigationWorkspace.Align(workspace, a, 0, b, 0, 1_000, 1, null, Now);
        read = InvestigationWorkspace.Read(workspace);
        WorkspaceView stale = Assert.Single(InvestigationWorkspace.ViewsInForce(read));
        Assert.False(InvestigationWorkspace.ViewIsCurrent(read, stale));
        Assert.Equal(4, read.Views.Count);

        // A view wider than a tick count shows no interval, and its file is refused whole.
        string valid = File.ReadAllText(workspace);
        string tooWide = valid
            .Replace("\"startTicks\": 12000000", "\"startTicks\": -9223372036854775808", StringComparison.Ordinal)
            .Replace("\"endTicks\": 18000000", "\"endTicks\": 9223372036854775807", StringComparison.Ordinal);
        Assert.NotEqual(valid, tooWide);
        File.WriteAllText(workspace, tooWide);
        Assert.Contains("shows no interval", Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace)).Message,
            StringComparison.Ordinal);
        File.WriteAllText(workspace, valid);

        // A view is named; none is removed that is not there; an earlier version's file holds none.
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.SaveView(workspace, "  ", new TimeRange(0, 10), Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.RemoveView(workspace, "Handshake", Now));
        File.WriteAllText(workspace, File.ReadAllText(workspace).Replace($"\"{InvestigationWorkspace.Contract}\"",
            $"\"{InvestigationWorkspace.NinthContract}\"", StringComparison.Ordinal));
        Assert.Contains("holds no saved view", Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace)).Message,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R22: a file an earlier version wrote keeps reading, with everything that version could hold")]
    public void AnEarlierVersionsFileKeepsReading()
    {
        // Each version's newest kind of fact, alone in a file of that version: every later version reads it as written.
        string workspace = NewWorkspace();
        Guid a = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "alpha"), "lab-1").Root.Path, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace,
            NewSession(Path.Combine(root, "beta"), "lab-2", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace,
            NewSession(Path.Combine(root, "gamma"), "lab-3", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        IReadOnlyList<WorkspaceMember> members = InvestigationWorkspace.Read(workspace).Members;
        Guid Host(Guid session) => members.Single(member => member.SessionId == session).HostId;
        string start = File.ReadAllText(workspace);

        void As(string version, Action write, Func<InvestigationWorkspaceFile, bool> holds)
        {
            File.WriteAllText(workspace, start);
            write();
            File.WriteAllText(workspace, File.ReadAllText(workspace).Replace(
                $"\"{InvestigationWorkspace.Contract}\"", $"\"{version}\"", StringComparison.Ordinal));
            InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
            Assert.Equal(version, read.Contract);
            Assert.True(holds(read), version);
        }

        As(InvestigationWorkspace.FifthContract,
            () => InvestigationWorkspace.Align(workspace, b, 0, a, 1_000, 1_000, null, null, Now, (1_000_000_000, 1_000_001_000)),
            read => read.Alignments.Single().SecondSessionNanoseconds is not null);
        As(InvestigationWorkspace.SixthContract,
            () =>
            {
                InvestigationWorkspace.Align(workspace, b, 0, a, 1_000, 1_000, 1, null, Now);
                InvestigationWorkspace.Align(workspace, c, 0, b, 1_000, 1_000, 1, null, Now);
            },
            read => InvestigationWorkspace.ActiveAlignment(read, c)?.ReferenceSessionId == b && InvestigationWorkspace.ChainOf(read, c) is not null);
        As(InvestigationWorkspace.SeventhContract,
            () => InvestigationWorkspace.ConfirmOneHost(workspace, Host(a), Host(b), null, Now),
            read => read.HostEquivalences.Count == 1);
        As(InvestigationWorkspace.EighthContract,
            () => InvestigationWorkspace.StateTranslation(workspace, "10.0.0.5:443", "203.0.113.7:443", null, Now),
            read => read.AddressTranslations.Count == 1);
        As(InvestigationWorkspace.NinthContract,
            () => InvestigationWorkspace.AddNote(workspace, "The upload stalls.", null, Now),
            read => read.Notes.Count == 1);
        As(InvestigationWorkspace.TenthContract,
            () =>
            {
                InvestigationWorkspace.Align(workspace, b, 0, a, 1_000, 1_000, 1, null, Now);
                InvestigationWorkspace.SaveView(workspace, "Start", new TimeRange(0, 10), Now);
            },
            read => read.Views.Count == 1);
        As(InvestigationWorkspace.EleventhContract,
            () => InvestigationWorkspace.SetLayout(workspace, a, [new WorkspacePin { Key = "group:a", X = 0.5, Y = 0.5 }], Now),
            read => read.Layouts.Single() is { Pins.Count: 1, RankBy: null, PerSecond: false });

        // Written again, such a file is the current version, and loses nothing.
        InvestigationWorkspace.AddNote(workspace, "And again.", null, Now);
        InvestigationWorkspaceFile rewritten = InvestigationWorkspace.Read(workspace);
        Assert.Equal((InvestigationWorkspace.Contract, 1, 1), (rewritten.Contract, rewritten.Layouts.Count, rewritten.Notes.Count));
    }

    [Fact(DisplayName = "R22: a member's layout is kept by node and ranking, replaced as it changes, and refused where it cannot be")]
    public void AMembersLayoutIsKept()
    {
        string workspace = NewWorkspace();
        Guid a = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "alpha"), "lab-1").Root.Path, Now).SessionId;
        static WorkspacePin Pin(string key, double x, double y) => new() { Key = key, X = x, Y = y };

        // Kept by node, in key order; a later layout replaces it whole, and one pinning nothing removes it.
        InvestigationWorkspace.SetLayout(workspace, a, [Pin("group:b", 0.5, 0.5), Pin("group:a", 0.1, 0.9)], Now);
        Assert.Equal(["group:a", "group:b"],
            InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.Pins.Select(pin => pin.Key));
        InvestigationWorkspace.SetLayout(workspace, a, [Pin("group:a", 0.2, 0.3)], Now);
        WorkspacePin moved = Assert.Single(Assert.Single(InvestigationWorkspace.Read(workspace).Layouts).Pins);
        Assert.Equal(("group:a", 0.2, 0.3), (moved.Key, moved.X, moved.Y));
        Assert.Null(InvestigationWorkspace.SetLayout(workspace, a, [], Now));
        Assert.Empty(InvestigationWorkspace.Read(workspace).Layouts);

        // Refused: a session not a member, a node pinned twice, outside the graph, or by no key.
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.SetLayout(workspace, Guid.NewGuid(), [Pin("k", 0.5, 0.5)], Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.SetLayout(workspace, a, [Pin("k", 0.5, 0.5), Pin("k", 0.1, 0.1)], Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.SetLayout(workspace, a, [Pin("k", 1.5, 0.5)], Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.SetLayout(workspace, a, [Pin(" ", 0.5, 0.5)], Now));

        // A layout keeps what the rows are ranked by, and whether per second (§26.3's sort): with no pin it keeps that alone,
        // and ranking by records counted whole, with no pin, it keeps nothing and is removed. Records per second is a
        // ranking kept, by no metric's name.
        WorkspaceLayout ranked = InvestigationWorkspace.SetLayout(workspace, a, [], Now, RankingMetric.RpcCallsMade, perSecond: true)!;
        Assert.Equal((RankingMetric.RpcCallsMade, true, 0), (ranked.RankBy, ranked.PerSecond, ranked.Pins.Count));
        WorkspaceLayout reread = InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!;
        Assert.Equal((RankingMetric.RpcCallsMade, true, 0), (reread.RankBy, reread.PerSecond, reread.Pins.Count));
        WorkspaceLayout records = InvestigationWorkspace.SetLayout(workspace, a, [], Now, RankingMetric.Records, perSecond: true)!;
        Assert.Equal(((RankingMetric?)null, true), (records.RankBy, records.PerSecond));
        Assert.Null(InvestigationWorkspace.SetLayout(workspace, a, [], Now, RankingMetric.Records, perSecond: false));
        Assert.Empty(InvestigationWorkspace.Read(workspace).Layouts);
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.SetLayout(workspace, a, [], Now, (RankingMetric)99));

        // A file of a version before layouts holds none, and one before rankings ranks nothing; a layout that keeps
        // nothing, or ranks by records by name or by no metric, is refused.
        InvestigationWorkspace.SetLayout(workspace, a, [Pin("group:a", 0.2, 0.3)], Now, RankingMetric.BytesSent);
        string written = File.ReadAllText(workspace);
        Assert.Contains("\"rankBy\": \"BytesSent\"", written, StringComparison.Ordinal);
        foreach ((string text, string problem) in new[]
        {
            (written.Replace($"\"{InvestigationWorkspace.Contract}\"", $"\"{InvestigationWorkspace.TenthContract}\"", StringComparison.Ordinal),
                "holds no layout"),
            (written.Replace($"\"{InvestigationWorkspace.Contract}\"", $"\"{InvestigationWorkspace.EleventhContract}\"", StringComparison.Ordinal),
                "ranks no session's rows"),
            (written.Replace("\"rankBy\": \"BytesSent\"", "\"rankBy\": \"Records\"", StringComparison.Ordinal), "or by records by name"),
            (written.Replace("\"rankBy\": \"BytesSent\"", "\"rankBy\": 99", StringComparison.Ordinal), "ranks by no metric"),
        })
        {
            Assert.NotEqual(written, text);
            File.WriteAllText(workspace, text);
            Assert.Contains(problem, Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace)).Message,
                StringComparison.Ordinal);
        }

        File.WriteAllText(workspace, written);
        InvestigationWorkspace.SetLayout(workspace, a, [], Now, RankingMetric.BytesSent);
        File.WriteAllText(workspace, File.ReadAllText(workspace).Replace("\"rankBy\": \"BytesSent\"", "\"rankBy\": null",
            StringComparison.Ordinal));
        Assert.Contains("keeps nothing", Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace)).Message,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R22: a note is a person's words on the investigation, pinned at a session's instant or not, kept as revisions")]
    public void ANoteIsKeptAsRevisions()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        Guid a = InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now).SessionId;

        // A note about the whole investigation, and one pinned at alpha's 2.5 s; rewording keeps where it is pinned.
        WorkspaceNote about = InvestigationWorkspace.AddNote(workspace, " The upload stalls twice. ", null, Now);
        WorkspaceNote pinned = InvestigationWorkspace.AddNote(workspace, "First stall", new WorkspaceNoteAnchor(a, 2_500_000_000), Now);
        WorkspaceNote reworded = InvestigationWorkspace.EditNote(workspace, pinned.NoteId, "First stall: 800 ms", Now);
        Assert.Equal(("The upload stalls twice.", (WorkspaceNoteAnchor?)null), (about.Text, about.At));
        Assert.Equal((pinned.NoteId, "First stall: 800 ms", pinned.At), (reworded.NoteId, reworded.Text, reworded.At));
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        Assert.Equal([about.NoteId, pinned.NoteId], InvestigationWorkspace.NotesInForce(read).Select(note => note.NoteId));
        Assert.Equal("First stall: 800 ms", InvestigationWorkspace.NoteNamed(read, pinned.NoteId.ToString("N")[..8]).Text);

        // Removed, a note is gone from those in force, and every revision stays in the file.
        InvestigationWorkspace.RemoveNote(workspace, about.NoteId, Now);
        read = InvestigationWorkspace.Read(workspace);
        Assert.Equal([pinned.NoteId], InvestigationWorkspace.NotesInForce(read).Select(note => note.NoteId));
        Assert.Equal(4, read.Notes.Count);

        // A note says something, not too much, and is pinned only at a member; one removed or never written is none.
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.AddNote(workspace, "  ", null, Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.AddNote(workspace, new string('x', InvestigationWorkspace.MostNoteCharacters + 1), null, Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.AddNote(workspace, "Elsewhere", new WorkspaceNoteAnchor(Guid.NewGuid(), 0), Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.EditNote(workspace, about.NoteId, "Back", Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.RemoveNote(workspace, Guid.NewGuid(), Now));

        // An earlier version's file holds no note.
        File.WriteAllText(workspace, File.ReadAllText(workspace).Replace($"\"{InvestigationWorkspace.Contract}\"",
            $"\"{InvestigationWorkspace.EighthContract}\"", StringComparison.Ordinal));
        Assert.Contains("holds no note", Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace)).Message,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R22: two host identities are one host only by a person's confirmation, kept as revisions and withdrawn as one")]
    public void TwoHostIdentitiesAreOneOnlyByAPersonsWord()
    {
        string workspace = NewWorkspace();
        InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "old"), "lab-1").Root.Path, Now);
        InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "renamed"), "lab-1b", Guid.NewGuid(), CaptureId.New()).Root.Path, Now);
        InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "imported"), "lab-1-file", Guid.NewGuid(), CaptureId.New()).Root.Path, Now);
        InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "elsewhere"), "lab-2", Guid.NewGuid(), CaptureId.New()).Root.Path, Now);
        Guid[] hosts = [.. InvestigationWorkspace.Read(workspace).Members.Select(member => member.HostId)];
        (Guid old, Guid renamed, Guid imported, Guid elsewhere) = (hosts[0], hosts[1], hosts[2], hosts[3]);

        // Different identities are two hosts until a person says otherwise; then they are one, and so is one confirmed one
        // host with either - through it.
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        Assert.False(InvestigationWorkspace.OneHost(read, old, renamed));
        WorkspaceHostEquivalence first = InvestigationWorkspace.ConfirmOneHost(workspace, old, renamed, " renamed after the first capture ", Now);
        InvestigationWorkspace.ConfirmOneHost(workspace, imported, renamed, null, Now);
        read = InvestigationWorkspace.Read(workspace);
        Assert.Equal((1, WorkspaceHostDecision.Confirmed, "renamed after the first capture"), (first.Revision, first.Decision, first.Note));
        Assert.True(InvestigationWorkspace.OneHost(read, old, imported));
        Assert.True(InvestigationWorkspace.OneHostByConfirmation(read, old, imported));
        Assert.False(InvestigationWorkspace.OneHostByConfirmation(read, old, old));
        Assert.False(InvestigationWorkspace.OneHost(read, old, elsewhere));
        Assert.Equal(new[] { renamed, imported }.Order(), InvestigationWorkspace.OneHostWith(read, old).Order());
        Assert.Equal(new[] { old, renamed, imported }.Min(), InvestigationWorkspace.HostKey(read, imported));
        Assert.Equal(new[] { renamed, imported }.Order(), InvestigationWorkspace.Hosts(read)[0].OneHostWith.Order());
        Assert.Empty(InvestigationWorkspace.Hosts(read)[3].OneHostWith);

        // A confirmation is made once and withdrawn once, only of two members' hosts; the withdrawal is kept.
        Assert.Contains("already", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.ConfirmOneHost(workspace, renamed, old, null, Now)).Message, StringComparison.Ordinal);
        Assert.Contains("none to withdraw", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.WithdrawOneHost(workspace, old, imported, Now)).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.ConfirmOneHost(workspace, old, old, null, Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.ConfirmOneHost(workspace, old, Guid.NewGuid(), null, Now));
        InvestigationWorkspace.WithdrawOneHost(workspace, renamed, old, Now);
        read = InvestigationWorkspace.Read(workspace);
        Assert.False(InvestigationWorkspace.OneHost(read, old, renamed));
        Assert.True(InvestigationWorkspace.OneHost(read, imported, renamed));
        Assert.Equal(3, read.HostEquivalences.Count);

        // An earlier version's file holds no confirmation, and a confirmation of a host no member was recorded on is refused.
        string text = File.ReadAllText(workspace);
        File.WriteAllText(workspace, text.Replace($"\"{InvestigationWorkspace.Contract}\"", $"\"{InvestigationWorkspace.SixthContract}\"", StringComparison.Ordinal));
        Assert.Contains("holds no confirmation that two hosts are one", Assert.Throws<InvalidDataException>(() =>
            InvestigationWorkspace.Read(workspace)).Message, StringComparison.Ordinal);
        int member = text.IndexOf(imported.ToString(), StringComparison.Ordinal);
        File.WriteAllText(workspace, string.Concat(text.AsSpan(0, member), Guid.NewGuid().ToString(), text.AsSpan(member + 36)));
        Assert.Contains("names a host no member was recorded on", Assert.Throws<InvalidDataException>(() =>
            InvestigationWorkspace.Read(workspace)).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: a member aligned through another is placed through both, and two members of one chain compare as it allows")]
    public void AMemberAlignedThroughAnotherIsPlacedThroughBoth()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        Guid a = InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "beta"), "lab-2", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "gamma"), "lab-2", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        Guid d = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "delta"), "lab-3", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;
        Guid e = InvestigationWorkspace.Add(workspace, NewSession(Path.Combine(root, "epsilon"), "lab-4", Guid.NewGuid(), CaptureId.New()).Root.Path, Now).SessionId;

        // Beta is placed on alpha's clock within 1 ms, drifting 10 ppm; gamma, of beta's host, on beta's within 2 µs; delta on
        // alpha's as beta is. Gamma's 0 s is beta's 1 s, which is alpha's 6 s.
        InvestigationWorkspace.Align(workspace, b, 0, a, Seconds(5), 1_000_000, 10, null, Now);
        InvestigationWorkspace.Align(workspace, c, 0, b, Seconds(1), 2_000, 1, "one host", Now);
        InvestigationWorkspace.Align(workspace, d, 0, a, Seconds(5), 1_000_000, 10, null, Now);
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        Assert.Equal((a, InvestigationWorkspace.Contract), (read.TimeReference!.Value, read.Contract));
        Assert.Equal([c, b], InvestigationWorkspace.ChainOf(read, c)!.Links.Select(link => link.Clock));

        // Through both, gamma carries its own 2 µs, and beta's 1 ms and 10 µs of drift a second from its anchor.
        WorkspaceInstant placed = InvestigationWorkspace.Place(read, c, 0);
        Assert.Equal(Seconds(6), placed.WorkspaceNanoseconds);
        Assert.InRange(placed.Uncertainty!.Value.HalfWidthNanoseconds, 1_012_000, 1_012_001);

        // Gamma against beta, whose alignment both share, differs only by gamma's own: 2.001 µs apart is an order, 2 µs is not.
        // Against delta, aligned apart, the two sides' uncertainties add, and a microsecond orders nothing.
        Assert.Equal(TimeOrder.Before, InvestigationWorkspace.Compare(read, c, 0, b, Seconds(1) + 2_001).Result.Order);
        WorkspaceComparison tie = InvestigationWorkspace.Compare(read, c, 0, b, Seconds(1));
        Assert.Equal(TimeOrder.Ambiguous, tie.Result.Order);
        Assert.InRange(tie.Result.Uncertainty!.Value.HalfWidthNanoseconds, 2_000, 2_001);
        WorkspaceComparison apart = InvestigationWorkspace.Compare(read, c, 0, d, Seconds(1) + 1_000);
        Assert.Equal(TimeOrder.Ambiguous, apart.Result.Order);
        Assert.InRange(apart.Result.Uncertainty!.Value.HalfWidthNanoseconds, 2_022_000, 2_022_001);

        // Nothing is aligned to a member with no place, nor through itself; a member others are aligned through keeps its own.
        Assert.Contains("has no place in the workspace's time", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Align(workspace, c, 0, e, 0, 1_000, 1, null, Now)).Message, StringComparison.Ordinal);
        Assert.Contains($"is aligned through session {b:N}", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Align(workspace, b, 0, c, 0, 1_000, 1, null, Now)).Message, StringComparison.Ordinal);
        Assert.Contains($"Session {c.ToString("N")[..8]} is aligned through session {b:N}", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.Withdraw(workspace, b, Now)).Message, StringComparison.Ordinal);

        // A decision about gamma and delta was made under beta's alignment too, so re-aligning beta flags it for review.
        WorkspaceJoin join = InvestigationWorkspace.Decide(workspace, new(c, "connection:first"), new(d, "connection:second"),
            WorkspaceJoinDecision.Accepted, null, Now);
        Assert.Equal([c, d, b], join.DecidedUnder.Select(under => under.SessionId));
        Assert.True(InvestigationWorkspace.DecidedUnderCurrentTime(InvestigationWorkspace.Read(workspace), join));
        InvestigationWorkspace.Align(workspace, b, 0, a, Seconds(5), 500_000, 10, null, Now);
        Assert.False(InvestigationWorkspace.DecidedUnderCurrentTime(InvestigationWorkspace.Read(workspace), join));

        // A version 5 file aligned every member to the time reference, and is refused whole when it seems not to.
        string text = File.ReadAllText(workspace);
        File.WriteAllText(workspace, text.Replace($"\"{InvestigationWorkspace.Contract}\"", $"\"{InvestigationWorkspace.FifthContract}\"", StringComparison.Ordinal));
        Assert.Contains("is aligned to a member that is not the workspace's time reference", Assert.Throws<InvalidDataException>(() =>
            InvestigationWorkspace.Read(workspace)).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: two instants read in both measure the clocks' rate, and its wander bounds the time between and beyond them")]
    public void TwoInstantsMeasureTheClocksRate()
    {
        string workspace = NewWorkspace();
        SessionStore alpha = NewSession(Path.Combine(root, "alpha"), "lab-1");
        SessionStore beta = NewSession(Path.Combine(root, "beta"), "lab-2", Guid.NewGuid(), CaptureId.New());
        Guid a = InvestigationWorkspace.Add(workspace, alpha.Root.Path, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, beta.Root.Path, Now).SessionId;

        // Beta's 1 s is alpha's 3 s, and its 11 s alpha's 13.0001 s: beta's clock runs 10 ppm slow against alpha's.
        WorkspaceAlignment alignment = InvestigationWorkspace.Align(
            workspace, b, Seconds(1), a, Seconds(3), 1_000, 0.5, "two consoles", Now, (Seconds(11), Seconds(13) + 100_000));
        Assert.Equal((Seconds(11), Seconds(13) + 100_000), (alignment.SecondSessionNanoseconds!.Value, alignment.SecondReferenceNanoseconds!.Value));
        Assert.Equal(10, InvestigationWorkspace.MeasuredPartsPerMillion(alignment), 6);
        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        Assert.Equal((InvestigationWorkspace.Contract, alignment), (read.Contract, Assert.Single(read.Alignments)));

        // At either instant the person's bound holds, and a nanosecond for rounding; midway, a rate wandering 0.5 ppm moves an
        // instant by up to 2 × 0.5 ppm × 5 s = 5 µs; 2 s beyond the second, the bound grows along the rate's own uncertainty,
        // 2 × 1 µs over 10 s, by 0.4 µs, and the wander by 2 µs.
        Assert.Equal(((long?)(Seconds(13) + 100_000), (TimeUncertainty?)new TimeUncertainty(1_001, 0)), Placed(InvestigationWorkspace.Place(read, b, Seconds(11))));
        Assert.Equal(((long?)8_000_050_000, (TimeUncertainty?)new TimeUncertainty(6_001, 0)), Placed(InvestigationWorkspace.Place(read, b, Seconds(6))));
        WorkspaceInstant beyond = InvestigationWorkspace.Place(read, b, Seconds(13));
        Assert.Equal((15_000_120_000L, Seconds(2)), (beyond.WorkspaceNanoseconds!.Value, beyond.FromAnchorNanoseconds!.Value));
        Assert.Equal(3_401, beyond.Uncertainty!.Value.HalfWidthNanoseconds, 3);

        // An order is stated only beyond that: 6.002 µs after beta's 6 s is after it, 6.001 µs is not.
        Assert.Equal(TimeOrder.After, InvestigationWorkspace.Compare(read, a, 8_000_050_000 + 6_002, b, Seconds(6)).Result.Order);
        Assert.Equal(TimeOrder.Ambiguous, InvestigationWorkspace.Compare(read, a, 8_000_050_000 + 6_001, b, Seconds(6)).Result.Order);

        // With no bound on the wander, only the two instants themselves are placed with a known uncertainty.
        InvestigationWorkspace.Align(workspace, b, Seconds(1), a, Seconds(3), 1_000, null, null, Now, (Seconds(11), Seconds(13) + 100_000));
        read = InvestigationWorkspace.Read(workspace);
        Assert.Equal(new TimeUncertainty(1_001, 0), InvestigationWorkspace.Place(read, b, Seconds(1)).Uncertainty);
        WorkspaceInstant between = InvestigationWorkspace.Place(read, b, Seconds(4));
        Assert.Equal((WorkspaceTimeGap.DriftUnknown, (TimeUncertainty?)null, (long?)Seconds(3)), (between.Gap, between.Uncertainty, between.FromAnchorNanoseconds));

        // One instant twice measures nothing, and a rate no working clock runs at says an instant was misread.
        Assert.Contains("is the first one again", Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Align(
            workspace, b, Seconds(1), a, Seconds(3), 1_000, null, null, Now, (Seconds(1), Seconds(5)))).Message, StringComparison.Ordinal);
        Assert.Contains("ppm any working clock stays within", Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Align(
            workspace, b, Seconds(1), a, Seconds(3), 1_000, null, null, Now, (Seconds(11), Seconds(13) + 20_000_000))).Message, StringComparison.Ordinal);

        // An earlier version's file holds no second anchor, and is refused whole when it seems to; its join decisions still read.
        File.WriteAllText(workspace, File.ReadAllText(workspace).Replace($"\"{InvestigationWorkspace.Contract}\"", $"\"{InvestigationWorkspace.FourthContract}\"", StringComparison.Ordinal));
        Assert.Contains("holds no alignment with a second anchor", Assert.Throws<InvalidDataException>(() =>
            InvestigationWorkspace.Read(workspace)).Message, StringComparison.Ordinal);
        string decided = Path.Combine(root, "decided" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(decided, Now);
        InvestigationWorkspace.Add(decided, alpha.Root.Path, Now);
        InvestigationWorkspace.Add(decided, beta.Root.Path, Now);
        InvestigationWorkspace.Decide(decided, new(a, "connection:first"), new(b, "connection:second"), WorkspaceJoinDecision.Accepted, null, Now);
        File.WriteAllText(decided, File.ReadAllText(decided).Replace($"\"{InvestigationWorkspace.Contract}\"", $"\"{InvestigationWorkspace.FourthContract}\"", StringComparison.Ordinal));
        Assert.Single(InvestigationWorkspace.Read(decided).Joins);
    }

    /// <summary>
    /// A published session whose capture recorded a clock calibration: a clock of <paramref name="ticksPerSecond"/> with its
    /// epoch at <paramref name="epoch"/>, on <paramref name="host"/>, in the boot <paramref name="boot"/> when one was kept.
    /// </summary>
    private static SessionStore CalibratedSession(
        string directory,
        string host,
        long epoch,
        Guid? boot,
        long ticksPerSecond = 10_000_000,
        ClockCalibrationSampleV1[]? samples = null)
    {
        Directory.CreateDirectory(directory);
        CaptureId capture = CaptureId.New();
        var clock = new SourceClockDescriptor(ClockId.New(), HostId.Derive(host), SourceClockKind.Monotonic, TimestampEncoding.Qpc,
            ticksPerSecond, epoch, TimestampRounding.NearestEven, SourceClockMath.SessionTicksPerSecond * 60);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "workspace-tests");
        Publish(store, Rows(epoch + 1_000), capture: capture, clock: clock, calibration: new ClockCalibrationV1
        {
            Contract = ClockCalibrationV1.ContractName,
            CaptureId = capture.Value,
            ClockId = clock.Id.Value,
            BootToken = boot,
            BootCount = boot is null ? null : 7,
            WallClock = "test-wall-clock",
            Samples = samples ?? [new() { NativeTicks = epoch, Utc = Now, AcquisitionUncertaintyNanoseconds = 200 }],
        });
        store.ReleaseSegmentReaders();
        return store;
    }

    private static long Seconds(int seconds) => seconds * 1_000_000_000L;

    private static (long?, TimeUncertainty?) Placed(WorkspaceInstant instant) => (instant.WorkspaceNanoseconds, instant.Uncertainty);

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
