namespace InterCat.Application;

/// <summary>Where a note is pinned: an instant of a member's session, in nanoseconds of its own time.</summary>
public sealed record WorkspaceNoteAnchor(Guid SessionId, long Nanoseconds);

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

    /// <summary>Where it is pinned; null for a note about the whole investigation.</summary>
    public WorkspaceNoteAnchor? At { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }
}

public static partial class InvestigationWorkspace
{
    /// <summary>The most a note holds, in characters: a note, not a document.</summary>
    public const int MostNoteCharacters = 4_000;

    /// <summary>Adds a note, pinned at an instant of a member's session when <paramref name="at"/> is given.</summary>
    public static WorkspaceNote AddNote(string workspacePath, string text, WorkspaceNoteAnchor? at, DateTimeOffset now) =>
        WriteNote(workspacePath, Guid.NewGuid(), text, at, existing: false, now);

    /// <summary>Replaces a note's words, keeping where it is pinned; the earlier words are kept as a revision.</summary>
    public static WorkspaceNote EditNote(string workspacePath, Guid noteId, string text, DateTimeOffset now) =>
        WriteNote(workspacePath, noteId, text, null, existing: true, now);

    /// <summary>Removes a note; its revisions are kept.</summary>
    public static WorkspaceNote RemoveNote(string workspacePath, Guid noteId, DateTimeOffset now) =>
        WriteNote(workspacePath, noteId, null, null, existing: true, now);

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
            throw new InvalidOperationException($"No note {noteId:N} is in force.");
        }

        if (at is { } anchor && workspace.Members.All(member => member.SessionId != anchor.SessionId))
        {
            throw new InvalidOperationException($"No member of this workspace is session {anchor.SessionId:N}, so no note is pinned there.");
        }

        var note = new WorkspaceNote
        {
            Revision = workspace.Notes.Count == 0 ? 1 : checked(workspace.Notes.Max(known => known.Revision) + 1),
            NoteId = noteId,
            Text = words,
            At = at ?? current?.At,
            RecordedUtc = now,
        };
        Save(full, workspace with { Notes = [.. workspace.Notes, note], UpdatedUtc = now }, file);
        return note;
    }

    /// <summary>What makes a file's notes contradict themselves, or null (`contracts/workspace-v11.md` §7).</summary>
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
        return workspace.Notes.FirstOrDefault(note => note.NoteId == Guid.Empty
            || note.Text is { } words && (string.IsNullOrWhiteSpace(words) || words.Length > MostNoteCharacters)
            || note.At is { } anchor && !members.Contains(anchor.SessionId)) is { } wrong
            ? $"note revision {wrong.Revision} names no note, holds no words or too many, or is pinned to no member"
            : null;
    }
}
