using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>What resolving a member against its path found (`contracts/workspace-v17.md` §3).</summary>
public enum WorkspaceMemberState
{
    /// <summary>The path holds the member's session, at the selected generation.</summary>
    Present = 1,

    /// <summary>The path holds the member's session, which has published a newer generation since it was selected.</summary>
    Advanced = 2,

    /// <summary>The path holds the member's session at a generation that is not the selected one and not newer.</summary>
    Replaced = 3,

    /// <summary>Nothing is at the path.</summary>
    Missing = 4,

    /// <summary>Another session, or another capture, is at the path.</summary>
    Different = 5,

    /// <summary>What is at the path cannot be opened as a session.</summary>
    Unreadable = 6,
}

/// <summary>
/// One session of a workspace, by identity (`contracts/workspace-v17.md` §2): the session and the capture its journal
/// records, the generation selected and its manifest's digest, its source clock's host, clock and epoch, and where it was
/// last found.
/// </summary>
public sealed record WorkspaceMember
{
    public required Guid SessionId { get; init; }

    /// <summary>The capture the session's journal records: what makes two sessions one capture (ADR-038 decision 3).</summary>
    public required Guid CaptureId { get; init; }

    /// <summary>Relative to the workspace's folder when the session lies under it, else absolute; '/' separates.</summary>
    public required string Path { get; init; }

    public required long Generation { get; init; }

    public required string ManifestDigest { get; init; }

    public required Guid HostId { get; init; }

    public required Guid ClockId { get; init; }

    public required long CaptureEpochNativeTicks { get; init; }

    public required DateTimeOffset AddedUtc { get; init; }
}

/// <summary>A person's name for a host identity; it makes no two identities one host (§8.3).</summary>
public sealed record WorkspaceHostAlias(Guid HostId, string Alias);

/// <summary>
/// A workspace as its file holds it (`workspace-v17`): a `workspace-v1` file is read as one without alignments, a
/// `workspace-v2` file as one with manual alignments only, a `workspace-v3` file as one without join decisions, a
/// `workspace-v4` file as one whose alignments each have one anchor, a `workspace-v5` file as one whose members are each
/// aligned to the time reference itself, a `workspace-v6` file as one without host confirmations, a `workspace-v7` file
/// as one without address translations, a `workspace-v8` file as one without notes, a `workspace-v9` file as one
/// without saved views, a `workspace-v10` file as one without layouts, a `workspace-v11` file as one whose layouts
/// rank nothing, a `workspace-v12` file as one whose layouts count correlated evidence, a `workspace-v13` file as one
/// whose layouts read every timeline lane on one scale, a `workspace-v14` file as one that keeps no window's panes, a
/// `workspace-v15` file as one whose layouts pin no timeline lane, and a `workspace-v16` file as one whose layouts group
/// every session's processes by executable.
/// </summary>
public sealed record InvestigationWorkspaceFile
{
    public required string Contract { get; init; }

    public required Guid WorkspaceId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }

    public required IReadOnlyList<WorkspaceMember> Members { get; init; }

    public required IReadOnlyList<WorkspaceHostAlias> HostAliases { get; init; }

    /// <summary>The member whose session clock is the workspace's time, once a member is aligned to it; null before.</summary>
    public Guid? TimeReference { get; init; }

    /// <summary>Every alignment revision, in the order recorded; a member's latest one is in force (§8.2).</summary>
    public IReadOnlyList<WorkspaceAlignment> Alignments { get; init; } = [];

    /// <summary>Every join decision revision, in the order recorded; a pair's latest one is in force (ADR-041).</summary>
    public IReadOnlyList<WorkspaceJoin> Joins { get; init; } = [];

    /// <summary>Every revision of a person's confirmation that two host identities are one host, in the order recorded (§8.3).</summary>
    public IReadOnlyList<WorkspaceHostEquivalence> HostEquivalences { get; init; } = [];

    /// <summary>Every revision of a person's statement of a known address translation, in the order recorded (§8.3).</summary>
    public IReadOnlyList<WorkspaceAddressTranslation> AddressTranslations { get; init; } = [];

    /// <summary>Every revision of a person's notes on the investigation, in the order written (§8.4).</summary>
    public IReadOnlyList<WorkspaceNote> Notes { get; init; } = [];

    /// <summary>Every revision of a person's saved views of the investigation's time, in the order saved (§8.4).</summary>
    public IReadOnlyList<WorkspaceView> Views { get; init; } = [];

    /// <summary>How a person laid out each member's graph, one per member at most (§26.3).</summary>
    public IReadOnlyList<WorkspaceLayout> Layouts { get; init; } = [];

    /// <summary>
    /// How a person left the window's two main panes while showing its sessions (§6.1, §26.3); null when it keeps none, and
    /// a session opened from it leaves the panes as they are.
    /// </summary>
    public WorkspacePanes? Panes { get; init; }
}

/// <summary>A member as resolved against its path: the generation found there, and why it is not present, when not.</summary>
public sealed record WorkspaceMemberResolution(
    WorkspaceMember Member,
    string FullPath,
    WorkspaceMemberState State,
    long? CurrentGeneration,
    string? Reason)
{
    /// <summary>Whether the session found for the member is InterCat's generated demo, which every view of it says.</summary>
    public bool Demo { get; init; }

    /// <summary>Whether the path holds the member's capture, at whichever generation.</summary>
    public bool HoldsItsCapture => State is WorkspaceMemberState.Present or WorkspaceMemberState.Advanced
        or WorkspaceMemberState.Replaced;
}

/// <summary>
/// A host identity of a workspace's members, with a person's name for it when one was given, and the other identities a
/// person confirmed are one host with it.
/// </summary>
public sealed record WorkspaceHost(Guid HostId, string? Alias, IReadOnlyList<Guid> Members)
{
    public IReadOnlyList<Guid> OneHostWith { get; init; } = [];
}

/// <summary>
/// An investigation over separately valid sessions (§8.4, ADR-038): one `workspace-v17` file that references its members by
/// identity and never writes to a session. A capture is one member; a moved session stays an unresolved reference until a
/// person relinks it, and a relink checks identity. Its time is one member's clock, to which a person aligns the others.
/// </summary>
public static partial class InvestigationWorkspace
{
    public const string Contract = "workspace-v17";

    /// <summary>
    /// The sixteenth version, revision 388's: layouts pin nodes and timeline lanes, rank, keep an evidence policy and read
    /// each timeline lane on its own scale, and the window's panes are kept; no layout groups a session's processes by
    /// terminal session. It is read, and written as the current one.
    /// </summary>
    public const string SixteenthContract = "workspace-v16";

    /// <summary>
    /// The fifteenth version, revision 360's: layouts pin nodes, rank, keep an evidence policy and read each timeline lane on
    /// its own scale, and the window's panes are kept; no layout pins a timeline lane. It is read, and written as the current
    /// one.
    /// </summary>
    public const string FifteenthContract = "workspace-v15";

    /// <summary>
    /// The fourteenth version, revision 343's: layouts pin, rank, keep an evidence policy and read each timeline lane on its
    /// own scale, and no window's panes are kept. It is read, and written as the current one.
    /// </summary>
    public const string FourteenthContract = "workspace-v14";

    /// <summary>
    /// The thirteenth version, revision 339's: layouts pin, rank and keep an evidence policy, and read every timeline lane
    /// on one scale. It is read, and written as the current one.
    /// </summary>
    public const string ThirteenthContract = "workspace-v13";

    /// <summary>
    /// The twelfth version, revision 301's: layouts pin and rank, and count correlated evidence. It is read, and written as
    /// the current one.
    /// </summary>
    public const string TwelfthContract = "workspace-v12";

    /// <summary>The eleventh version, revision 280's: layouts pin, and rank nothing. It is read, and written as the current one.</summary>
    public const string EleventhContract = "workspace-v11";

    /// <summary>The tenth version, revision 271's: no layouts. It is read, and written as the current one.</summary>
    public const string TenthContract = "workspace-v10";

    /// <summary>The ninth version, revision 270's: no saved views. It is read, and written as the current one.</summary>
    public const string NinthContract = "workspace-v9";

    /// <summary>The eighth version, revision 269's: no notes. It is read, and written as the current one.</summary>
    public const string EighthContract = "workspace-v8";

    /// <summary>The seventh version, revision 266's: no address translations. It is read, and written as the current one.</summary>
    public const string SeventhContract = "workspace-v7";

    /// <summary>The sixth version, revision 265's: no host confirmations. It is read, and written as the current one.</summary>
    public const string SixthContract = "workspace-v6";

    /// <summary>The fifth version, revision 264's: every member aligned to the time reference. It is read, and written as the current one.</summary>
    public const string FifthContract = "workspace-v5";

    /// <summary>The fourth version, revision 260's: one anchor per alignment. It is read, and written as the current one.</summary>
    public const string FourthContract = "workspace-v4";

    /// <summary>The third version, revision 256's: no join decisions. It is read, and written as the current one.</summary>
    public const string ThirdContract = "workspace-v3";

    /// <summary>The first version, revision 253's: members and host names, no time. It is read, and written as the current one.</summary>
    public const string FirstContract = "workspace-v1";

    /// <summary>The second version, revision 254's: manual alignments only. It is read, and written as the current one.</summary>
    public const string SecondContract = "workspace-v2";

    /// <summary>A workspace file's conventional extension, added to a new workspace's name when it has none.</summary>
    public const string Extension = ".icat-workspace";

    /// <summary>
    /// A known file's version number: 1 for `workspace-v1` up to this one's. A kind of fact is refused only in a file of a
    /// version before the one that introduced it, so every later version reads what an earlier one wrote.
    /// </summary>
    internal static int VersionOf(InvestigationWorkspaceFile workspace) =>
        int.Parse(workspace.Contract.AsSpan("workspace-v".Length), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture);

    private const string KeptBeside = "A workspace is kept beside its sessions, never in one: a session keeps only its own files, "
        + "and would count the workspace as an orphan.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The path a new workspace named <paramref name="path"/> is made at: the conventional extension added when it has none.</summary>
    public static string PathFor(string path) =>
        System.IO.Path.GetFullPath(System.IO.Path.HasExtension(path) ? path : path + Extension);

    /// <summary>Makes an empty workspace at <paramref name="path"/>, which must not exist, and returns it.</summary>
    public static InvestigationWorkspaceFile Create(string path, DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full))
        {
            throw new InvalidOperationException($"{full} exists; a workspace is made new, never over another file.");
        }

        string folder = System.IO.Path.GetDirectoryName(full)!;
        if (File.Exists(System.IO.Path.Combine(folder, SessionPointerV1.FileName)))
        {
            throw new InvalidOperationException($"{folder} is a session's directory. {KeptBeside}");
        }

        Directory.CreateDirectory(folder);
        var workspace = new InvestigationWorkspaceFile
        {
            Contract = Contract,
            WorkspaceId = Guid.NewGuid(),
            CreatedUtc = now,
            UpdatedUtc = now,
            Members = [],
            HostAliases = [],
        };
        Save(full, workspace, readText: null);
        return workspace;
    }

    /// <summary>Reads and checks a workspace file; a file that holds anything else, or contradicts itself, is refused whole.</summary>
    public static InvestigationWorkspaceFile Read(string path) => Load(System.IO.Path.GetFullPath(path)).Workspace;

    /// <summary>
    /// Adds the session at <paramref name="sessionDirectory"/>: its identities, its current generation and that manifest's
    /// digest, and its source clock's host. A session whose capture a member already records is refused (ADR-038 decision 3).
    /// </summary>
    public static WorkspaceMember Add(string workspacePath, string sessionDirectory, DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        WorkspaceMember member = Describe(full, sessionDirectory, now);
        if (workspace.Members.FirstOrDefault(known => known.CaptureId == member.CaptureId || known.SessionId == member.SessionId)
            is { } same)
        {
            throw new InvalidOperationException(same.SessionId == member.SessionId
                ? $"Session {member.SessionId:N} is already a member, found at {same.Path}. A copy of a session is the same "
                    + "capture, and a capture added twice would count its records twice."
                : $"This session records capture {member.CaptureId:N}, which member {same.SessionId:N} at {same.Path} already "
                    + "records. A capture added twice would count its records twice.");
        }

        Save(full, workspace with { Members = [.. workspace.Members, member], UpdatedUtc = now }, text);
        return member;
    }

    /// <summary>
    /// Points the member <paramref name="sessionId"/> at <paramref name="sessionDirectory"/>, only when the session there is
    /// that session of that capture, and selects the generation found there (ADR-038 decision 5).
    /// </summary>
    public static WorkspaceMember Relink(string workspacePath, Guid sessionId, string sessionDirectory, DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        WorkspaceMember member = workspace.Members.FirstOrDefault(known => known.SessionId == sessionId)
            ?? throw new InvalidOperationException($"No member of this workspace is session {sessionId:N}.");
        WorkspaceMember found = Describe(full, sessionDirectory, now);
        if (found.SessionId != member.SessionId || found.CaptureId != member.CaptureId)
        {
            throw new InvalidOperationException($"The session at {System.IO.Path.GetFullPath(sessionDirectory)} is session "
                + $"{found.SessionId:N} of capture {found.CaptureId:N}, not member {member.SessionId:N} of capture "
                + $"{member.CaptureId:N}. A member is relinked only to itself.");
        }

        WorkspaceMember relinked = found with { AddedUtc = member.AddedUtc };
        Save(full, workspace with
        {
            Members = [.. workspace.Members.Select(known => known.SessionId == sessionId ? relinked : known)],
            UpdatedUtc = now,
        }, text);
        return relinked;
    }

    /// <summary>
    /// Names a member's host for people, or removes its name when <paramref name="alias"/> is empty. One name never names two
    /// host identities: it would read as one host, which only a person's confirmation may say (§8.3).
    /// </summary>
    public static void Alias(string workspacePath, Guid hostId, string? alias, DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        if (workspace.Members.All(member => member.HostId != hostId))
        {
            throw new InvalidOperationException($"No member of this workspace was recorded on host {hostId:N}.");
        }

        string? name = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        if (name is not null && workspace.HostAliases.FirstOrDefault(known => known.HostId != hostId
            && string.Equals(known.Alias, name, StringComparison.OrdinalIgnoreCase)) is { } taken)
        {
            throw new InvalidOperationException($"Host {taken.HostId:N} is already called '{taken.Alias}'. One name for two host "
                + "identities would read as one host, which their identities do not say.");
        }

        WorkspaceHostAlias[] others = [.. workspace.HostAliases.Where(known => known.HostId != hostId)];
        Save(full, workspace with
        {
            HostAliases = name is null ? others : [.. others, new WorkspaceHostAlias(hostId, name)],
            UpdatedUtc = now,
        }, text);
    }

    /// <summary>The distinct host identities of a workspace's members, in the order members were added, each with its name.</summary>
    public static IReadOnlyList<WorkspaceHost> Hosts(InvestigationWorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return [.. workspace.Members.GroupBy(member => member.HostId).Select(group => new WorkspaceHost(
            group.Key,
            workspace.HostAliases.FirstOrDefault(alias => alias.HostId == group.Key)?.Alias,
            [.. group.Select(member => member.SessionId)])
        {
            OneHostWith = OneHostWith(workspace, group.Key),
        })];
    }

    /// <summary>The member a person named by its session identity or a unique leading part of it.</summary>
    public static WorkspaceMember MemberNamed(InvestigationWorkspaceFile workspace, string text)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(text);
        return Named(workspace.Members, member => member.SessionId, text, "session")
            ?? throw new InvalidOperationException($"No member of this workspace is session '{text}'.");
    }

    /// <summary>The host a person named by its name, its identity or a unique leading part of it.</summary>
    public static Guid HostNamed(InvestigationWorkspaceFile workspace, string text)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(text);
        IReadOnlyList<WorkspaceHost> hosts = Hosts(workspace);
        return hosts.FirstOrDefault(host => string.Equals(host.Alias, text.Trim(), StringComparison.OrdinalIgnoreCase))?.HostId
            ?? Named(hosts, host => host.HostId, text, "host")?.HostId
            ?? throw new InvalidOperationException($"No member of this workspace was recorded on a host named '{text}'.");
    }

    /// <summary>Resolves every member against its path (`contracts/workspace-v17.md` §3), in the workspace's order.</summary>
    public static IReadOnlyList<WorkspaceMemberResolution> Resolve(
        string workspacePath,
        InvestigationWorkspaceFile workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(workspacePath))!;
        var resolutions = new List<WorkspaceMemberResolution>(workspace.Members.Count);
        foreach (WorkspaceMember member in workspace.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolutions.Add(ResolveOne(folder, member));
        }

        return resolutions;
    }

    private static WorkspaceMemberResolution ResolveOne(string folder, WorkspaceMember member)
    {
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, member.Path));
        if (File.Exists(full))
        {
            return new(member, full, WorkspaceMemberState.Unreadable, null, "A file is at this path, not a session's directory.");
        }

        if (!Directory.Exists(full))
        {
            return new(member, full, WorkspaceMemberState.Missing, null,
                "Nothing is at this path: the session was moved or removed. Relink the member where it is now.");
        }

        Found found;
        try
        {
            found = Open(full);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(member, full, WorkspaceMemberState.Unreadable, null, exception.Message);
        }

        SessionManifestV1 manifest = found.Manifest;
        if (manifest.SessionId != member.SessionId || found.Capture.Value != member.CaptureId)
        {
            return new(member, full, WorkspaceMemberState.Different, manifest.Generation,
                $"Session {manifest.SessionId:N} of capture {found.Capture.Value:N} is at this path, not this member.");
        }

        string? rolledBack = found.RollbackReason is null
            ? null
            : $"Its current generation could not be read, so its last-known-good was: {found.RollbackReason}.";
        bool demo = DemoInvestigation.IsDemo(manifest);
        if (manifest.Generation == member.Generation && manifest.Digest == member.ManifestDigest)
        {
            return new(member, full, WorkspaceMemberState.Present, manifest.Generation, rolledBack) { Demo = demo };
        }

        return (manifest.Generation > member.Generation
            ? new WorkspaceMemberResolution(member, full, WorkspaceMemberState.Advanced, manifest.Generation,
                $"The session has published generation {manifest.Generation} since generation {member.Generation} was "
                + "selected. Relink the member to its own path to select it.")
            : new WorkspaceMemberResolution(member, full, WorkspaceMemberState.Replaced, manifest.Generation, string.Join(' ', new[]
            {
                // An older generation is a copy put in its place, unless it is the one kept to fall back to, which the
                // next sentence says.
                manifest.Generation < member.Generation
                    ? $"The path holds generation {manifest.Generation} of this session, older than the selected generation "
                        + $"{member.Generation}{(rolledBack is null ? ": an earlier copy was put in its place." : ".")}"
                    : $"The path holds a generation {manifest.Generation} of this session derived apart from the one selected.",
                rolledBack,
                "Relink the member to its own path to select what is there.",
            }.OfType<string>()))) with { Demo = demo };
    }

    /// <summary>The member a session directory is, read from its current generation and its journal's capture and clock.</summary>
    private static WorkspaceMember Describe(string workspacePath, string sessionDirectory, DateTimeOffset now)
    {
        string directory = System.IO.Path.GetFullPath(sessionDirectory);
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"No session directory at {directory}.");
        }

        Found found = Open(directory);
        string relative = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(workspacePath)!, directory);
        if (relative == ".")
        {
            throw new InvalidOperationException($"The workspace is inside the session at {directory}. {KeptBeside}");
        }

        bool under = !System.IO.Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return new()
        {
            SessionId = found.Manifest.SessionId,
            CaptureId = found.Capture.Value,
            Path = (under ? relative : directory).Replace('\\', '/'),
            Generation = found.Manifest.Generation,
            ManifestDigest = found.Manifest.Digest,
            HostId = found.Clock.HostId.Value,
            ClockId = found.Clock.Id.Value,
            CaptureEpochNativeTicks = found.Clock.CaptureEpochNativeTicks,
            AddedUtc = now,
        };
    }

    /// <summary>
    /// Opens a session to learn what it is, as a viewer does: the pointer, the manifest's digest and every dependency's
    /// presence and length are checked, and the journal's header is read. Nothing is written.
    /// </summary>
    private static Found Open(string directory)
    {
        SessionStore store = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(directory));
        SessionManifestV1 manifest = store.Current
            ?? throw new InvalidDataException(SessionStore.NoGeneration);
        (CaptureId capture, SourceClockDescriptor clock) = SessionSegments.Source(store.Root, manifest)
            ?? throw new InvalidDataException(
                $"Generation {manifest.Generation} names no journal, so the session has no capture, host or clock to place.");
        return new(manifest, capture, clock, store.Recovery.RolledBackToLastKnownGood ? store.Recovery.RollbackReason : null, store.Root);
    }

    private static (InvestigationWorkspaceFile Workspace, string Text) Load(string full)
    {
        if (!File.Exists(full))
        {
            throw new InvalidOperationException($"No workspace at {full}; icat workspace new makes one.");
        }

        string text = File.ReadAllText(full);
        InvestigationWorkspaceFile? workspace;

        // A file a person may edit says where it went wrong: the place its text stops being JSON, or the fields it lacks or
        // holds that a workspace does not, never the parser's own words (§26.3).
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{full} is not a readable workspace: {JsonProblems.Syntax(exception)}.", exception);
        }

        using (document)
        {
            try
            {
                workspace = document.RootElement.Deserialize<InvestigationWorkspaceFile>(Json);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"{full} is not a readable workspace: {JsonProblems.Fields<InvestigationWorkspaceFile>(document.RootElement, exception, Json)}.",
                    exception);
            }
        }

        string? problem = workspace is null ? "it holds no workspace" : Problem(workspace);
        return problem is null ? (workspace!, text) : throw new InvalidDataException($"{full} is not a readable workspace: {problem}.");
    }

    private static string? Problem(InvestigationWorkspaceFile workspace)
    {
        if (workspace.Contract is not (Contract or SixteenthContract or FifteenthContract or FourteenthContract or ThirteenthContract
            or TwelfthContract or EleventhContract or TenthContract or NinthContract or EighthContract or SeventhContract
            or SixthContract or FifthContract or FourthContract or ThirdContract or SecondContract or FirstContract))
        {
            return $"it is '{workspace.Contract}', not {FirstContract}, {SecondContract}, {ThirdContract}, {FourthContract}, "
                + $"{FifthContract}, {SixthContract}, {SeventhContract}, {EighthContract}, {NinthContract}, {TenthContract}, "
                + $"{EleventhContract}, {TwelfthContract}, {ThirteenthContract}, {FourteenthContract}, {FifteenthContract}, "
                + $"{SixteenthContract} or {Contract}";
        }

        if (workspace.WorkspaceId == Guid.Empty)
        {
            return "it names no workspace identity";
        }

        if (workspace.Members.Any(member => member is null) || workspace.HostAliases.Any(alias => alias is null))
        {
            return "it lists an empty member or name";
        }

        if (workspace.Members.FirstOrDefault(member => member.SessionId == Guid.Empty || member.CaptureId == Guid.Empty
            || member.HostId == Guid.Empty || member.ClockId == Guid.Empty || member.Generation < 1
            || string.IsNullOrWhiteSpace(member.Path) || string.IsNullOrWhiteSpace(member.ManifestDigest)) is { } nameless)
        {
            return $"member {nameless.SessionId:N} lacks its session, capture, host, clock, generation, digest or path";
        }

        if (workspace.Members.GroupBy(member => member.SessionId).FirstOrDefault(group => group.Count() > 1) is { } session)
        {
            return $"two members are session {session.Key:N}";
        }

        if (workspace.Members.GroupBy(member => member.CaptureId).FirstOrDefault(group => group.Count() > 1) is { } capture)
        {
            return $"two members record capture {capture.Key:N}, which would count its records twice";
        }

        if (workspace.HostAliases.FirstOrDefault(alias => string.IsNullOrWhiteSpace(alias.Alias)) is { } blank)
        {
            return $"host {blank.HostId:N} has an empty name";
        }

        if (workspace.HostAliases.GroupBy(alias => alias.HostId).FirstOrDefault(group => group.Count() > 1) is { } host)
        {
            return $"host {host.Key:N} has two names";
        }

        return workspace.HostAliases.GroupBy(alias => alias.Alias.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1) is { } name
            ? $"'{name.Key}' names two host identities"
            : TimeProblem(workspace) ?? JoinProblem(workspace) ?? HostProblem(workspace) ?? TranslationProblem(workspace)
                ?? NoteProblem(workspace) ?? ViewProblem(workspace) ?? LayoutProblem(workspace) ?? PanesProblem(workspace);
    }

    /// <summary>
    /// Writes the whole file beside itself and moves it into place, so a reader sees the old file or the new, and only
    /// over the text it was read from: a change made meanwhile is never written over.
    /// </summary>
    internal static void Save(string full, InvestigationWorkspaceFile workspace, string? readText)
    {
        string temporary = $"{full}.{Guid.NewGuid():N}.writing";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(workspace with { Contract = Contract }, Json) + "\n");
            if (readText is not null && (!File.Exists(full) || File.ReadAllText(full) != readText))
            {
                throw new InvalidOperationException(
                    $"{full} changed since it was read, so this change was not written over it. Run it again.");
            }

            File.Move(temporary, full, overwrite: readText is not null);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static T? Named<T>(IEnumerable<T> candidates, Func<T, Guid> identity, string text, string what)
        where T : class
    {
        string trimmed = text.Trim();
        if (Guid.TryParse(trimmed, out Guid exact))
        {
            return candidates.FirstOrDefault(candidate => identity(candidate) == exact);
        }

        string prefix = trimmed.Replace("-", string.Empty, StringComparison.Ordinal);
        if (prefix.Length < 4 || !prefix.All(Uri.IsHexDigit))
        {
            return null;
        }

        T[] matches = [.. candidates.Where(candidate =>
            identity(candidate).ToString("N").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
        return matches.Length > 1
            ? throw new InvalidOperationException(
                $"'{text}' begins {matches.Length} {what} identities of this workspace; give more of it.")
            : matches.FirstOrDefault();
    }

    private sealed record Found(
        SessionManifestV1 Manifest,
        CaptureId Capture,
        SourceClockDescriptor Clock,
        string? RollbackReason,
        IOwnedDirectory Root);
}
