using InterCat.Application;

namespace InterCat.Desktop;

/// <summary>
/// Where a way down with a relationship, or an edge to other hosts, chosen began (§3.2): the rung - its depth, level
/// and focus - and the relationship or edge by its key.
/// </summary>
public sealed record RelationshipOrigin(int Depth, DetailLevel Level, string? Focus, string Relationship);

/// <summary>
/// The way back to a relationship (§3.2, R15). E on a chosen relationship lists its records, and Enter or a double
/// click opens it, its channel or its linked calls; Esc or the breadcrumb back to the rung that way began chooses it
/// again there, and gives the keyboard to its row where the relationship table is shown, as the way back to an
/// aggregate's list chooses the process chosen there. An edge to other hosts is a row of the table too: E with it
/// chosen lists its source's records, and Enter or a double click opens its source, so the way back chooses it again
/// with its source. Each rung keeps its own way back, so one begun further down does not take the way back to a rung
/// above it; a way down that begins at a rung or above it is a way of its own, and a newer generation keeps them.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    // The ways back to a relationship or an edge to other hosts, one for each rung a way down with one chosen began at,
    // the topmost first.
    private readonly List<RelationshipOrigin> relationshipOrigins = [];

    /// <summary>
    /// How many times a way back has chosen a relationship, or an edge to other hosts, again and asked for the keyboard
    /// to go to its row (§3.2, R15); the window gives it there each time this changes, where the relationship table is
    /// shown.
    /// </summary>
    public int RelationshipAsked { get; private set; }

    /// <summary>
    /// The way back to the table's row <paramref name="relationship"/> keys from a way down beginning at this rung.
    /// </summary>
    private RelationshipOrigin OriginHere(string relationship) =>
        new(ladder.Depth, ladder.Current.Level, ladder.Current.Focus?.Key, relationship);

    /// <summary>
    /// Keeps the way back to <paramref name="origin"/>'s rung, once the way down from it is taken: taking it let go of
    /// those that rung and the ones below it kept.
    /// </summary>
    private void KeepWayBack(RelationshipOrigin origin) => relationshipOrigins.Add(origin);

    /// <summary>Lets go of the ways back to the rung at <paramref name="depth"/> and those below it, a chosen cell's too.</summary>
    private void ForgetWaysBackFrom(int depth)
    {
        relationshipOrigins.RemoveAll(origin => origin.Depth >= depth);
        cellOrigins.RemoveAll(origin => origin.Depth >= depth);
    }

    /// <summary>
    /// At a rung a way down with a relationship, or an edge to other hosts, chosen began, chooses it again and asks for
    /// the keyboard to go to its row, where the rung is still the one it began at and its table lists it; otherwise
    /// that way back is let go.
    /// </summary>
    private void ResumeRelationshipOrigin()
    {
        int kept = relationshipOrigins.FindIndex(origin => origin.Depth == ladder.Depth);
        if (kept < 0)
        {
            return;
        }

        // A relationship opened from below the machine rung is opened along the ladder from its top, so the rung at that
        // depth on the way back may be another: it is checked.
        RelationshipOrigin origin = relationshipOrigins[kept];
        if (ladder.Current.Level != origin.Level || !string.Equals(ladder.Current.Focus?.Key, origin.Focus, StringComparison.Ordinal)
            || TableRows.FirstOrDefault(row => string.Equals(row.Key, origin.Relationship, StringComparison.Ordinal)) is not { } row)
        {
            relationshipOrigins.RemoveAt(kept);
            return;
        }

        // An edge to other hosts is chosen with its source, which the way back may have selected already.
        if (!row.ToHosts || !ChooseHostEdgeAgain(row))
        {
            SelectedTableRow = row;
        }

        RelationshipAsked++;
        OnPropertyChanged(nameof(RelationshipAsked));
    }
}
