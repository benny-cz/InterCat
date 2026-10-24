namespace InterCat.Application;

/// <summary>Where a note is pinned: an instant of a member's session, in nanoseconds of its own time.</summary>
public sealed record WorkspaceNoteAnchor(Guid SessionId, long Nanoseconds);

/// <summary>
/// What a note is about where it is about a saved comparison (`contracts/workspace-v23.md` §7): the comparison, by
/// the name it is saved under, and one of its rows by the key a comparison gives it (`comparison-v1` §8) where it is about
/// that row - never by its rank, which a comparison run again may change.
/// </summary>
/// <param name="Comparison">The saved comparison's name.</param>
/// <param name="Row">The row's key; null for a note about the whole comparison.</param>
public sealed record WorkspaceNoteSubject(string Comparison, string? Row);

/// <summary>
/// One revision of a person's note on an investigation (§8.4): its words, and the instant it is pinned at, when it is - an
/// instant of a member's session, which the investigation's time places as it places any other. A later revision of the
/// same note replaces its words, or removes it; every revision is kept, and none changes a session.
/// </summary>
public sealed record WorkspaceNote
{
    public required int Revision { get; init; }

    public required Guid NoteId { get; init; }

    /// <summary>The note's words; null when this revision removes the note.</summary>
    public string? Text { get; init; }

    /// <summary>Where it is pinned; null for a note about the whole investigation or about a saved comparison.</summary>
    public WorkspaceNoteAnchor? At { get; init; }

    /// <summary>The saved comparison, or the row of one, it is about; null for any other note. Since version 23.</summary>
    public WorkspaceNoteSubject? About { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }
}

public static partial class InvestigationWorkspace
{
    /// <summary>The most a note holds, in characters: a note, not a document.</summary>
    public const int MostNoteCharacters = 4_000;

    /// <summary>The longest row key a note names, in characters: longer than any key a comparison gives a row.</summary>
    public const int MostRowKeyCharacters = 512;

    /// <summary>Adds a note, pinned at an instant of a member's session when <paramref name="at"/> is given.</summary>
    public static WorkspaceNote AddNote(string workspacePath, string text, WorkspaceNoteAnchor? at, DateTimeOffset now) =>
        WriteNote(workspacePath, Guid.NewGuid(), text, at, null, existing: false, now);

    /// <summary>
    /// Adds a note about a saved comparison in force, or one of its rows by key: the comparison named as it is saved, its
    /// row's key as given, trimmed.
    /// </summary>
    public static WorkspaceNote AddNoteAbout(string workspacePath, string text, WorkspaceNoteSubject about, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(about);
        return WriteNote(workspacePath, Guid.NewGuid(), text, null, about, existing: false, now);
    }

    /// <summary>Replaces a note's words, keeping where it is pinned; the earlier words are kept as a revision.</summary>
    public static WorkspaceNote EditNote(string workspacePath, Guid noteId, string text, DateTimeOffset now) =>
        WriteNote(workspacePath, noteId, text, null, null, existing: true, now);

    /// <summary>Removes a note; its revisions are kept.</summary>
    public static WorkspaceNote RemoveNote(string workspacePath, Guid noteId, DateTimeOffset now) =>
        WriteNote(workspacePath, noteId, null, null, null, existing: true, now);

    /// <summary>Every note in force - each note's latest revision, when that does not remove it - in the order first written.</summary>
    public static IReadOnlyList<WorkspaceNote> NotesInForce(InvestigationWorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return [.. workspace.Notes
            .GroupBy(note => note.NoteId)
            .OrderBy(group => group.Min(note => note.Revision))
            .Select(group => group.MaxBy(note => note.Revision)!)
            .Where(note => note.Text is not null)];
    }

    /// <summary>
    /// The notes in force about the saved comparison named <paramref name="comparison"/>, ignoring case - about it and about
    /// its rows - in the order first written.
    /// </summary>
    public static IReadOnlyList<WorkspaceNote> NotesAbout(InvestigationWorkspaceFile workspace, string comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return [.. NotesInForce(workspace).Where(note => note.About is { } about
            && string.Equals(about.Comparison, comparison.Trim(), StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// What a note about a comparison is about, in words: "about the comparison 'Before the update'", "about row
    /// tcp:… of the comparison 'Before the update'", and where the comparison is no longer saved, that it is not.
    /// </summary>
    public static string About(InvestigationWorkspaceFile workspace, WorkspaceNoteSubject subject)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(subject);
        bool saved = ComparisonsInForce(workspace).Any(comparison =>
            string.Equals(comparison.Name, subject.Comparison, StringComparison.OrdinalIgnoreCase));
        return (subject.Row is { } row ? $"about row {row} of the comparison '{subject.Comparison}'"
                : $"about the comparison '{subject.Comparison}'")
            + (saved ? string.Empty : ", which is no longer saved");
    }

    /// <summary>The note in force a person named by its identity or a unique leading part of it.</summary>
    public static WorkspaceNote NoteNamed(InvestigationWorkspaceFile workspace, string text)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(text);
        return Named(NotesInForce(workspace), note => note.NoteId, text, "note")
            ?? throw new InvalidOperationException($"No note of this investigation is '{text}'.");
    }

    private static WorkspaceNote WriteNote(
        string workspacePath,
        Guid noteId,
        string? text,
        WorkspaceNoteAnchor? at,
        WorkspaceNoteSubject? about,
        bool existing,
        DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string file) = Load(full);
        string? words = text?.Trim();
        if (text is not null && (words!.Length == 0 || words.Length > MostNoteCharacters))
        {
            throw new InvalidOperationException(words.Length == 0
                ? "A note says something: write its words."
                : $"A note holds at most {MostNoteCharacters:N0} characters; this one has {words.Length:N0}.");
        }

        WorkspaceNote? current = NotesInForce(workspace).FirstOrDefault(note => note.NoteId == noteId);
        if (existing && current is null)
        {
            throw new InvalidOperationException($"No note {Short(noteId)} is in force.");
        }

        if (at is { } anchor && workspace.Members.All(member => member.SessionId != anchor.SessionId))
        {
            throw new InvalidOperationException(
                $"Session {Short(anchor.SessionId)} is not in this investigation, so no note is pinned there.");
        }

        // A note about a comparison names it as it is saved, and a row by a key no longer than any a comparison gives.
        if (about is not null)
        {
            string? row = about.Row?.Trim();
            if (row is not null && (row.Length == 0 || row.Length > MostRowKeyCharacters))
            {
                throw new InvalidOperationException(row.Length == 0
                    ? "A note about a row names the row by its key: give the key icat compare --json writes for it."
                    : $"A row's key is at most {MostRowKeyCharacters:N0} characters; this one has {row.Length:N0}.");
            }

            about = new WorkspaceNoteSubject(ComparisonNamed(workspace, about.Comparison).Name, row);
        }

        var note = new WorkspaceNote
        {
            Revision = workspace.Notes.Count == 0 ? 1 : checked(workspace.Notes.Max(known => known.Revision) + 1),
            NoteId = noteId,
            Text = words,
            At = at ?? current?.At,
            About = about ?? current?.About,
            RecordedUtc = now,
        };
        Save(full, workspace with { Notes = [.. workspace.Notes, note], UpdatedUtc = now }, file);
        return note;
    }

    /// <summary>What makes a file's notes contradict themselves, or null (`contracts/workspace-v23.md` §7).</summary>
    private static string? NoteProblem(InvestigationWorkspaceFile workspace)
    {
        // Notes arrived with the ninth version (revision 270).
        if (workspace.Notes.Count > 0 && VersionOf(workspace) < 9)
        {
            return $"a {workspace.Contract} file holds no note";
        }

        if (workspace.Notes.Any(note => note is null))
        {
            return "it lists an empty note";
        }

        if (workspace.Notes.GroupBy(note => note.Revision).FirstOrDefault(group => group.Key < 1 || group.Count() > 1) is { } revision)
        {
            return $"note revision {revision.Key} is not a unique positive number";
        }

        HashSet<Guid> members = [.. workspace.Members.Select(member => member.SessionId)];
        if (workspace.Notes.FirstOrDefault(note => note.NoteId == Guid.Empty
            || note.Text is { } words && (string.IsNullOrWhiteSpace(words) || words.Length > MostNoteCharacters)
            || note.At is { } anchor && !members.Contains(anchor.SessionId)) is { } wrong)
        {
            return $"note revision {wrong.Revision} names no note, holds no words or too many, or is pinned to a session not in it";
        }

        // Notes about saved comparisons arrived with the twenty-third version (revision 771): each names a comparison the
        // file saved, by any revision, and a row by a key of some length; one pinned at an instant is about no comparison.
        if (workspace.Notes.Any(note => note.About is not null) && VersionOf(workspace) < 23)
        {
            return $"a {workspace.Contract} file holds no note about a comparison";
        }

        HashSet<string> saved = new(workspace.Comparisons.Select(comparison => comparison.Name), StringComparer.OrdinalIgnoreCase);
        return workspace.Notes.FirstOrDefault(note => note.About is { } about && (note.At is not null
            || about.Comparison is null || !saved.Contains(about.Comparison)
            || about.Row is { } row && (string.IsNullOrWhiteSpace(row) || row.Length > MostRowKeyCharacters))) is { } stray
            ? $"note revision {stray.Revision} is about a comparison the file never saved, names a row by no key or too long "
                + "a one, or is both pinned at an instant and about a comparison"
            : null;
    }
}
