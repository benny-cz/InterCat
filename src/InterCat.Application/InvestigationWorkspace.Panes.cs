using System.Text.Json.Serialization;

namespace InterCat.Application;

/// <summary>A main pane of the window, which a person may let fill its column (§6.1).</summary>
public enum WorkspacePane
{
    Graph = 1,
    Timeline = 2,
}

/// <summary>
/// How a person left the window's two main panes while showing an investigation's sessions (§6.1's persistence, §26.3's
/// workspace scope): the graph's share of the height it shares with the timeline, and the pane filling the column, if one
/// does. They are the window's rather than a session's, so an investigation keeps them once, for every session opened from
/// it, replaced as they change rather than kept as revisions.
/// </summary>
public sealed record WorkspacePanes
{
    /// <summary>The graph's share when the two panes share the column equally, as a window first lays them out.</summary>
    public const double EqualShare = 0.5;

    /// <summary>The decimal places a share is kept to: a ten-thousandth of the panes' height, under a pixel on any screen.</summary>
    public const int ShareDigits = 4;

    /// <summary>The graph's share of the height it shares with the timeline, above 0 and below 1; the timeline has the rest.</summary>
    public required double GraphShare { get; init; }

    /// <summary>The pane filling the column by a person's command, the other one click away; null when the two share it.</summary>
    public WorkspacePane? Expanded { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>Whether they keep anything: a split other than equal halves, or a pane filling the column.</summary>
    [JsonIgnore]
    public bool KeepsAnything => GraphShare != EqualShare || Expanded is not null;

    /// <summary>
    /// What they keep, in the words every place that says so uses - `icat workspace show`, the notice of a session opened
    /// from its investigation and that investigation's window: "the timeline filling the column and the graph at 37% of the
    /// panes' height when both are shown". Empty when they keep nothing.
    /// </summary>
    public string Describe(IFormatProvider culture) => Describe(GraphShare, Expanded, culture);

    /// <summary>
    /// What panes that give the graph <paramref name="graphShare"/> and let <paramref name="expanded"/> fill the column keep,
    /// in <see cref="Describe(IFormatProvider)"/>'s words: nothing of equal halves with both shown, so what is said is only
    /// what differs from a window as it first lays them out.
    /// </summary>
    public static string Describe(double graphShare, WorkspacePane? expanded, IFormatProvider culture) =>
        WorkspaceLayout.Series(Parts(graphShare, expanded, culture));

    /// <summary>
    /// Each thing <see cref="Describe(double, WorkspacePane?, IFormatProvider)"/> says such panes keep, in its order, so a
    /// notice can list it in one series with what else was put back.
    /// </summary>
    public static IReadOnlyList<string> Parts(double graphShare, WorkspacePane? expanded, IFormatProvider culture)
    {
        string? filling = expanded switch
        {
            WorkspacePane.Graph => "the graph filling the column",
            WorkspacePane.Timeline => "the timeline filling the column",
            _ => null,
        };

        // The split is said of the pane it gives the share to that is out of sight: the graph's, unless the graph fills the
        // column. A whole percent, never 0% or 100%, which would say a pane has none of the height when it has some.
        double share = Kept(graphShare);
        string? split = share == EqualShare ? null
            : expanded == WorkspacePane.Graph
                ? string.Create(culture, $"the timeline at {Math.Clamp(1 - share, 0.01, 0.99):P0} of the panes' height when both are shown")
            : string.Create(culture, $"the graph at {Math.Clamp(share, 0.01, 0.99):P0} of the panes' height")
                + (filling is null ? string.Empty : " when both are shown");
        return [.. new[] { filling, split }.OfType<string>()];
    }

    /// <summary>A share as an investigation keeps it: rounded to <see cref="ShareDigits"/> decimal places.</summary>
    public static double Kept(double share) => Math.Round(share, ShareDigits, MidpointRounding.AwayFromZero);
}

public static partial class InvestigationWorkspace
{
    /// <summary>
    /// Keeps the window's panes as a person left them while showing one of the investigation's sessions: the graph given
    /// <paramref name="graphShare"/> of the height it shares with the timeline, kept to <see cref="WorkspacePanes.ShareDigits"/>
    /// places, and <paramref name="expanded"/> filling the column, replacing what was kept before; equal halves with both
    /// shown keep nothing and remove it. Null when it is removed.
    /// </summary>
    public static WorkspacePanes? SetPanes(string workspacePath, double graphShare, WorkspacePane? expanded, DateTimeOffset now)
    {
        double share = WorkspacePanes.Kept(graphShare);
        if (PanesProblem(share, expanded) is { } problem)
        {
            throw new InvalidOperationException($"The panes are refused: {problem}.");
        }

        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string file) = Load(full);
        var kept = new WorkspacePanes { GraphShare = share, Expanded = expanded, UpdatedUtc = now };
        WorkspacePanes? panes = kept.KeepsAnything ? kept : null;
        Save(full, workspace with { Panes = panes, UpdatedUtc = now }, file);
        return panes;
    }

    private static string? PanesProblem(double share, WorkspacePane? expanded) =>
        !double.IsFinite(share) || share <= 0 || share >= 1
            ? "the graph's share of the panes' height is not above 0 and below 1, so one of them would have none of it, which "
                + "only letting the other fill the column does"
        : expanded is { } pane && !Enum.IsDefined(pane) ? "they let a pane the window does not have fill the column"
        : null;

    /// <summary>What makes a file's panes contradict themselves, or null (`contracts/workspace-v15.md` §7).</summary>
    private static string? PanesProblem(InvestigationWorkspaceFile workspace)
    {
        if (workspace.Panes is not { } panes)
        {
            return null;
        }

        // The window's panes arrived with the fifteenth version (revision 360).
        return VersionOf(workspace) < 15 ? $"a {workspace.Contract} file keeps no window's panes"
            : PanesProblem(panes.GraphShare, panes.Expanded) is { } problem ? "its panes are refused: " + problem
            : !panes.KeepsAnything ? "its panes keep nothing: equal halves, both shown, are what a window lays out unless told otherwise"
            : null;
    }
}
