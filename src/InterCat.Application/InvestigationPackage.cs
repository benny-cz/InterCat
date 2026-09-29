using System.Globalization;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>What packaging an investigation does with one member: copies it, or keeps it a reference, and why.</summary>
/// <param name="Measured">
/// What a copy of the member would hold, measured from its files; null when its capture is not where the investigation
/// last found it, so no copy can be made.
/// </param>
/// <param name="Selected">Whether it is copied: always false when there is nothing to copy.</param>
public sealed record InvestigationPackageMemberPreview(
    WorkspaceMemberResolution Resolution,
    OriginalEvidencePackagePreview? Measured,
    bool Selected)
{
    public Guid SessionId => Resolution.Member.SessionId;

    /// <summary>The folder it was last found in, by name, which is how a person tells members apart.</summary>
    public string Folder => Path.GetFileName(Path.TrimEndingDirectorySeparator(Resolution.FullPath));

    /// <summary>What its copy holds; null when it is not copied.</summary>
    public OriginalEvidencePackagePreview? Copy => Selected ? Measured : null;

    /// <summary>Why it is kept a reference rather than copied; null when it is copied.</summary>
    public string? LeftOut => Selected ? null
        : Measured is null
            ? $"Not copied, and kept as a reference: it is {Resolution.State.ToString().ToLowerInvariant()}. {Resolution.Reason}"
            : "Not selected, so kept as a reference to where it is on this computer.";
}

/// <summary>
/// What an investigation package would hold, measured without writing anything: every member, what its copy holds or why
/// it has none, and the investigation itself. It is what a person is shown before anything is saved (§8.4, §11.3).
/// </summary>
public sealed record InvestigationPackagePreview(
    string WorkspacePath,
    InvestigationWorkspaceFile Workspace,
    IReadOnlyList<InvestigationPackageMemberPreview> Members)
{
    /// <summary>The members that are copied, in the investigation's order.</summary>
    public IReadOnlyList<InvestigationPackageMemberPreview> Copied => [.. Members.Where(member => member.Copy is not null)];

    public long Rows => Members.Sum(member => member.Copy?.Rows ?? 0);

    public int Files => Members.Sum(member => member.Copy?.Files.Count ?? 0);

    public long Bytes => Members.Sum(member => member.Copy?.Bytes ?? 0);

    /// <summary>Whether any copy is a session as it was recorded rather than itself a redacted package.</summary>
    public bool Unredacted => Members.Any(member => member.Copy is { Redacted: false });
}

/// <summary>What one member became in a published package.</summary>
/// <param name="PackagedPath">Its copy's path, relative to the package's investigation file; null for a reference.</param>
/// <param name="Generation">The generation its copy holds; null for a reference.</param>
/// <param name="Files">The files its copy's generation names, each verified; 0 for a reference.</param>
/// <param name="Bytes">Those files' bytes; 0 for a reference.</param>
/// <param name="State">How it resolved when the package was reopened, as a recipient on this computer finds it.</param>
/// <param name="Note">Why it is not present at its selected generation in the package; null when it is.</param>
public sealed record InvestigationPackageMember(
    Guid SessionId,
    string? PackagedPath,
    long? Generation,
    int Files,
    long Bytes,
    WorkspaceMemberState State,
    string? Note);

/// <summary>A published, verified investigation package: where it is, its investigation's file, and each member.</summary>
public sealed record InvestigationPackageResult(
    string Directory,
    string WorkspacePath,
    InvestigationPackagePreview Source,
    IReadOnlyList<InvestigationPackageMember> Members)
{
    /// <summary>The files the copies hold, as a preview counts them: each copy's manifest and pointer besides.</summary>
    public int Files => Members.Sum(member => member.Files);

    public long Bytes => Members.Sum(member => member.Bytes);
}

/// <summary>How far a package is: which copy of how many, its stage, and bytes done of all the copies' bytes.</summary>
public sealed record InvestigationPackageProgress(int Session, int Sessions, OriginalPackageStage Stage, long Done, long Total);

/// <summary>
/// §8.4's export: an investigation packaged with its sessions, so another person can open it whole. Each selected member
/// whose capture is where the investigation last found it is copied as an original evidence package
/// (`contracts/original-evidence-package-v1.md`) under `sessions/`, and the investigation's file beside them names each
/// copy by a path relative to itself, so the folder moves as one; the investigation's identity, time reference,
/// alignments, host names and join decisions go with it unchanged, since every session keeps its identity. Any other
/// member stays a reference to where it was last found, to relink wherever the package is opened
/// (`contracts/workspace-v11.md` §8). The package is built in a private directory beside its destination, each copy
/// verified as it is made, the investigation reopened and resolved as a recipient would, and only then moved into place.
/// </summary>
public static class InvestigationPackage
{
    /// <summary>The folder of the package that holds the copies.</summary>
    public const string SessionsFolder = "sessions";

    public const string Warning = "Unredacted. Each session is copied as it was recorded, so anyone who opens the package sees "
        + "everything InterCat saw in each of them. Share it only with someone who may see all of it.";

    /// <summary>Measures what a package of every member that can be copied would hold; nothing is written.</summary>
    public static InvestigationPackagePreview Preview(string workspacePath, CancellationToken cancellationToken = default)
    {
        string full = Path.GetFullPath(workspacePath);
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(full);
        var members = new List<InvestigationPackageMemberPreview>(workspace.Members.Count);
        foreach (WorkspaceMemberResolution resolution in InvestigationWorkspace.Resolve(full, workspace, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OriginalEvidencePackagePreview? measured = null;
            if (resolution.HoldsItsCapture)
            {
                measured = OriginalEvidencePackage.Preview(Open(resolution.FullPath));
                if (measured.SessionId != resolution.Member.SessionId)
                {
                    throw new InvalidDataException($"Session {measured.SessionId:N} is at {resolution.FullPath} now, not "
                        + $"member {resolution.Member.SessionId:N}. Look at the investigation again before packaging it.");
                }
            }

            members.Add(new(resolution, measured, measured is not null));
        }

        return new(full, workspace, members);
    }

    /// <summary>
    /// The preview with only <paramref name="only"/> copied, or every member that can be when it is null. A member named that
    /// is not in the investigation, or whose capture is not where it was last found, is refused, since it cannot be copied.
    /// </summary>
    public static InvestigationPackagePreview Select(InvestigationPackagePreview preview, IReadOnlyCollection<Guid>? only)
    {
        ArgumentNullException.ThrowIfNull(preview);
        foreach (Guid named in only ?? [])
        {
            InvestigationPackageMemberPreview member = preview.Members.FirstOrDefault(known => known.SessionId == named)
                ?? throw new InvalidOperationException($"No member of this investigation is session {named:N}.");
            if (member.Measured is null)
            {
                throw new InvalidOperationException($"Session {Short(named)} is "
                    + $"{member.Resolution.State.ToString().ToLowerInvariant()}, so it cannot be copied. {member.Resolution.Reason}");
            }
        }

        return preview with
        {
            Members = [.. preview.Members.Select(member => member with
            {
                Selected = member.Measured is not null && (only is null || only.Contains(member.SessionId)),
            })],
        };
    }

    /// <summary>
    /// Builds, verifies and publishes a package of the investigation at <paramref name="destination"/>, which must not exist
    /// and must not lie inside a session. Nothing appears under that name unless every copy and the investigation verified.
    /// </summary>
    /// <param name="only">The members to copy; null copies every member whose capture is where it was last found.</param>
    public static InvestigationPackageResult Create(
        string workspacePath,
        string destination,
        IReadOnlyCollection<Guid>? only = null,
        IProgress<InvestigationPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        InvestigationPackagePreview preview = Select(Preview(workspacePath, cancellationToken), only);
        IReadOnlyList<InvestigationPackageMemberPreview> copied = preview.Copied;
        if (copied.Count == 0)
        {
            throw new InvalidOperationException(preview.Members.Count == 0
                ? "This investigation names no session, so there is nothing to package."
                : "No session of this investigation is where it was last found, so there is nothing to copy. Relink its "
                    + "sessions, or share the investigation's file itself.");
        }

        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new InvalidOperationException($"{target} exists. An investigation is packaged only into a new folder.");
        }

        string parent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException($"{target} has no parent folder to make the package in.");
        for (string? folder = parent; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (File.Exists(Path.Combine(folder, SessionPointerV1.FileName)))
            {
                throw new InvalidOperationException($"{target} is inside the session at {folder}. A package is made beside "
                    + "sessions, never in one: a session keeps only its own files.");
            }
        }

        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $"{Path.GetFileName(target)}.partial-{Guid.NewGuid():N}");
        string sessions = Path.Combine(staging, SessionsFolder);
        Directory.CreateDirectory(sessions);
        try
        {
            var folders = new Dictionary<Guid, (string Name, OriginalEvidencePackageResult Copy)>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var packaged = new List<WorkspaceMember>(preview.Members.Count);
            long before = 0;
            long total = preview.Bytes;
            foreach (InvestigationPackageMemberPreview member in preview.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WorkspaceMember reference = member.Resolution.Member;
                if (member.Copy is null)
                {
                    // Kept by the whole path it was last found at: relative to the original investigation's folder, it would
                    // name nothing beside the package, or something else.
                    packaged.Add(reference with { Path = member.Resolution.FullPath.Replace('\\', '/') });
                    continue;
                }

                string name = FolderFor(member, used);
                int session = folders.Count + 1;
                long offset = before;

                // A copy is verified once all of it is copied, so its verification is reported at its end: the share done of
                // all the copies' bytes never goes back.
                Relay? relay = progress is null ? null : new(update => progress.Report(new(
                    session, copied.Count, update.Stage,
                    offset + (update.Stage == OriginalPackageStage.Verifying ? update.Total : update.Done),
                    Math.Max(total, offset + update.Total))));
                OriginalEvidencePackageResult copy = OriginalEvidencePackage.Create(
                    Open(member.Resolution.FullPath), Path.Combine(sessions, name), relay, cancellationToken);
                before += copy.BytesVerified;
                folders.Add(reference.SessionId, (name, copy));

                // The selection is kept as it is: a member whose session has moved on reads as it did, advanced, until a
                // person relinks it, which is the only way a generation is selected.
                packaged.Add(reference with { Path = $"{SessionsFolder}/{name}" });
            }

            string file = Path.Combine(staging, Path.GetFileName(preview.WorkspacePath));
            InvestigationWorkspace.Save(file, preview.Workspace with { Members = packaged }, readText: null);
            IReadOnlyList<WorkspaceMemberResolution> reopened = Verify(file, sessions, folders, cancellationToken);
            Directory.Move(staging, target);
            return new(target, Path.Combine(target, Path.GetFileName(file)), preview,
            [
                .. preview.Members.Zip(reopened, (member, found) => folders.TryGetValue(member.SessionId, out var copy)
                    ? new InvestigationPackageMember(member.SessionId, $"{SessionsFolder}/{copy.Name}", copy.Copy.Source.Generation,
                        copy.Copy.Source.Files.Count, copy.Copy.Source.Bytes, found.State,
                        found.State == WorkspaceMemberState.Present ? null : found.Reason)
                    : new InvestigationPackageMember(member.SessionId, null, null, 0, 0, found.State, member.LeftOut)),
            ]);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>
    /// What the confirmation states before a package is saved, a paragraph each: what it is, what its copies hold, their
    /// hosts, what the investigation's file carries, what is not copied, and the warning, last (§11.3).
    /// </summary>
    public static IReadOnlyList<string> Disclosure(InvestigationPackagePreview preview, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(culture);
        IReadOnlyList<InvestigationPackageMemberPreview> copied = preview.Copied;
        int members = preview.Members.Count;
        string name = Path.GetFileName(preview.WorkspacePath);
        if (copied.Count == 0)
        {
            return [$"No session of {name} would be copied, so there is nothing to package. Select a session that is where "
                + "the investigation last found it."];
        }

        var paragraphs = new List<string>
        {
            string.Create(culture, $"This saves the investigation {name} with an exact copy of ")
                + (copied.Count == members
                    ? members == 1 ? "its one session" : string.Create(culture, $"each of its {members:N0} sessions")
                    : string.Create(culture, $"{copied.Count:N0} of its {members:N0} sessions"))
                + " in a new folder, which opens in InterCat as the same investigation. The investigation and its sessions "
                + "are not changed.",
            string.Create(culture, $"The copies hold {preview.Rows:N0} {(preview.Rows == 1 ? "record" : "records")} in ")
                + string.Create(culture, $"{preview.Files:N0} {(preview.Files == 1 ? "file" : "files")} ({Size(preview.Bytes, culture)}): ")
                + "each session's current generation, with no earlier one.",
        };

        InvestigationPackageMemberPreview[] unredacted = [.. copied.Where(member => !member.Copy!.Redacted)];
        InvestigationPackageMemberPreview[] redacted = [.. copied.Where(member => member.Copy!.Redacted)];
        if (unredacted.Length > 0)
        {
            InvestigationPackageMemberPreview[] kept = [.. unredacted.Where(member => member.Copy!.ContentChunks.Count > 0)];
            long fragments = kept.Sum(member => member.Copy!.ContentFragments);
            long bytes = kept.Sum(member => member.Copy!.ContentChunks.Sum(file => file.LengthBytes));
            paragraphs.Add("Unredacted: " + (kept.Length == 0
                ? OriginalEvidencePackage.Contents
                : OriginalEvidencePackage.Contents[..OriginalEvidencePackage.Contents.IndexOf(" InterCat records metadata only", StringComparison.Ordinal)]
                    + (kept.Length == 1 ? " One session also holds the message content its capture kept"
                        : string.Create(culture, $" {kept.Length:N0} sessions also hold the message content their captures kept"))
                    + string.Create(culture, $": the bytes of {fragments:N0} {(fragments == 1 ? "record" : "records")}, ")
                    + string.Create(culture, $"{bytes:N0} bytes in all, as sensitive as the messages they came from.")));
        }

        if (redacted.Length > 0)
        {
            paragraphs.Add(unredacted.Length == 0
                ? OriginalEvidencePackage.RedactedContents
                : redacted.Length == 1
                    ? $"Session {Short(redacted[0].SessionId)} is itself a redacted package, copied as it is: its pseudonyms and "
                        + "synthetic records, never the original values."
                    : $"Sessions {string.Join(", ", redacted.Select(member => Short(member.SessionId)))} are themselves redacted "
                        + "packages, copied as they are: their pseudonyms and synthetic records, never the original values.");
        }

        List<string> hosts = [.. copied.Select(member => member.Resolution.Member.HostId).Distinct().Select(host =>
            preview.Workspace.HostAliases.FirstOrDefault(alias => alias.HostId == host)?.Alias is { } alias
                ? $"{host:N} ({alias})"
                : host.ToString("N"))];
        paragraphs.Add((hosts.Count == 1 ? "Hosts: one, identified as " : string.Create(culture, $"Hosts: {hosts.Count:N0}, identified as "))
            + Listed(hosts) + ".");

        InvestigationWorkspaceFile workspace = preview.Workspace;
        int notes = workspace.Alignments.Count(alignment => alignment.Note is not null) + workspace.Joins.Count(join => join.Note is not null);
        var carried = new List<string> { "each session's capture, host and clock identities" };
        if (workspace.HostAliases.Count > 0) carried.Add(Counted(workspace.HostAliases.Count, "name given to a host", "names given to hosts", culture));
        if (workspace.Alignments.Count > 0) carried.Add(Counted(workspace.Alignments.Count, "alignment revision", "alignment revisions", culture));
        if (workspace.Joins.Count > 0) carried.Add(Counted(workspace.Joins.Count, "join decision revision", "join decision revisions", culture));
        if (notes > 0) carried.Add(Counted(notes, "note written on them", "notes written on them", culture));
        paragraphs.Add("The investigation's file goes with them, holding " + Listed(carried) + "."
            + (workspace.HostAliases.Count + notes > 0 ? " Names and notes are copied as they were written, never pseudonymized." : string.Empty));

        InvestigationPackageMemberPreview[] references = [.. preview.Members.Where(member => member.Copy is null)];
        if (references.Length > 0)
        {
            paragraphs.Add((references.Length == 1 ? "Not copied: " : string.Create(culture, $"Not copied, {references.Length:N0} sessions: "))
                + string.Join("; ", references.Select(member => $"{Short(member.SessionId)} ({member.Folder}), "
                    + (member.Measured is null ? member.Resolution.State.ToString().ToLowerInvariant() : "not selected")))
                + ". The package keeps " + (references.Length == 1 ? "it" : "each") + " as a reference to the path it was "
                + "last found at on this computer, so whoever opens the package sees that path; elsewhere "
                + (references.Length == 1 ? "it opens" : "each opens") + " as missing until it is relinked.");
        }

        foreach (InvestigationPackageMemberPreview member in copied.Where(member => member.Resolution.State != WorkspaceMemberState.Present))
        {
            paragraphs.Add(string.Create(culture, $"Session {Short(member.SessionId)} ({member.Folder}) is copied as it is now, ")
                + string.Create(culture, $"generation {member.Copy!.Generation:N0}, which the investigation does not select: ")
                + string.Create(culture, $"it selects generation {member.Resolution.Member.Generation:N0}, so the copy reads as ")
                + $"{member.Resolution.State.ToString().ToLowerInvariant()} in the package, as it does here, until it is relinked. "
                + "Relink it first to select what the package will hold.");
        }

        paragraphs.Add(WarningFor(preview));
        return paragraphs;
    }

    /// <summary>
    /// The warning a package states: <see cref="Warning"/> when any copy is a session as recorded, or the redacted package's
    /// own when every copy is itself a redacted package - pseudonymized, not anonymous, and never unredacted.
    /// </summary>
    public static string WarningFor(InvestigationPackagePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return preview.Copied.Count > 0 && !preview.Unredacted ? RedactedSessionPackage.Warning : Warning;
    }

    /// <summary>
    /// Reopens the package's investigation as a recipient would, from its own file: every copy must be found where the file
    /// says, as the session it is at the generation copied, and the sessions folder must hold the copies and nothing else.
    /// </summary>
    private static IReadOnlyList<WorkspaceMemberResolution> Verify(
        string file,
        string sessions,
        Dictionary<Guid, (string Name, OriginalEvidencePackageResult Copy)> folders,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkspaceMemberResolution> reopened =
            InvestigationWorkspace.Resolve(file, InvestigationWorkspace.Read(file), cancellationToken);
        foreach (WorkspaceMemberResolution found in reopened)
        {
            if (folders.TryGetValue(found.Member.SessionId, out var copy)
                && (!found.HoldsItsCapture || found.CurrentGeneration != copy.Copy.Source.Generation))
            {
                throw new InvalidDataException($"The package did not verify when it was reopened: its copy of session "
                    + $"{found.Member.SessionId:N} is {found.State.ToString().ToLowerInvariant()}. {found.Reason}");
            }
        }

        string[] strays = [.. Directory.EnumerateFileSystemEntries(sessions).Select(Path.GetFileName).OfType<string>()
            .Where(entry => folders.Values.All(copy => !string.Equals(copy.Name, entry, StringComparison.OrdinalIgnoreCase)))];
        return strays.Length == 0
            ? reopened
            : throw new InvalidDataException($"The package did not verify when it was reopened: its sessions folder holds "
                + $"{string.Join(", ", strays)}, which is no copy.");
    }

    /// <summary>A copy's folder: the session's short identity and the name of the folder it was found in, made safe.</summary>
    private static string FolderFor(InvestigationPackageMemberPreview member, HashSet<string> used)
    {
        char[] kept = [.. member.Folder.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_')];
        string stem = new string(kept).Trim('.', ' ');
        stem = stem.Length == 0 ? "session" : stem[..Math.Min(stem.Length, 48)];
        string name = $"{Short(member.SessionId)}-{stem}";
        return used.Add(name) ? name : used.Add(name = $"{member.SessionId:N}-{stem}") ? name
            : throw new InvalidOperationException($"Two members would be copied to {name}.");
    }

    private static SessionStore Open(string directory) => SessionStore.OpenForViewing(LocalOwnedDirectory.Open(directory));

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    private static string Counted(long count, string one, string many, CultureInfo culture) =>
        count == 1 ? "one " + one : string.Create(culture, $"{count:N0} {many}");

    private static string Listed(List<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    /// <summary>A size as a person reads it: "812 KB", "4.2 MB", "37 MB", "1.3 GB".</summary>
    private static string Size(long bytes, CultureInfo culture) => bytes switch
    {
        < 1_000_000 => string.Create(culture, $"{Math.Max(1, bytes / 1_000):N0} KB"),
        < 10_000_000 => string.Create(culture, $"{bytes / 1_000_000d:N1} MB"),
        < 1_000_000_000 => string.Create(culture, $"{bytes / 1_000_000d:N0} MB"),
        _ => string.Create(culture, $"{bytes / 1_000_000_000d:N1} GB"),
    };

    private static void TryDelete(string staging)
    {
        try
        {
            Directory.Delete(staging, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A package that did not verify never appears under its name; a leftover private folder beside it is named as
            // partial and holds nothing its sessions do not.
        }
    }

    /// <summary>Passes a copy's progress on as it is reported, on the copying thread, in order.</summary>
    private sealed class Relay(Action<OriginalPackageProgress> report) : IProgress<OriginalPackageProgress>
    {
        public void Report(OriginalPackageProgress value) => report(value);
    }
}
