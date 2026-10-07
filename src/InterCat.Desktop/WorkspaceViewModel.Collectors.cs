using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// §19.5's visible view filter in the rail: which of the session's processes are InterCat's own, and, while they are set
/// aside from the rows, the graph and the channels, how many with how many records - which the timeline and the evidence
/// still count, since no record is removed.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>Whether the rail says which processes are InterCat's own, or that they are set aside: a session names some.</summary>
    public bool OffersCollectorsAside => realOverview && (CollectorsSetAside || OwnProcesses.Count > 0);

    /// <summary>Whether InterCat's own processes are set aside from this view's rows, graph and channels.</summary>
    public bool CollectorsSetAside => wholeSnapshot.SetAside.Count > 0;

    /// <summary>
    /// The rail's line, one line beside its command since the rail's height is its ranked rows': "InterCat's own:
    /// intercat-broker.exe · PID 4120", or while they are set aside, "InterCat's own set aside: 1 process, 312 records".
    /// <see cref="CollectorsText"/> says it whole.
    /// </summary>
    public string CollectorsLine => !OffersCollectorsAside ? string.Empty
        : CollectorsSetAside
            ? "InterCat's own set aside: " + CountText.Of(wholeSnapshot.SetAside.Count, "process", "processes") + ", "
                + CountText.Of(SetAsideRecords, "record")
            : "InterCat's own: " + string.Join(", ", OwnProcesses.Select(process => process.NameWithPid));

    /// <summary>
    /// The line whole, for its tooltip and a screen reader: which processes, what setting them aside does, and that no
    /// record is removed.
    /// </summary>
    public string CollectorsText => !OffersCollectorsAside ? string.Empty
        : CollectorsSetAside
            ? "InterCat's own processes are set aside from the ranked rows, the graph and the channels: "
                + WorkspaceLayout.Series([.. wholeSnapshot.SetAside.Select(CollectorText.Named)]) + ", with "
                + CountText.Of(SetAsideRecords, "record") + " over the whole session. No record is removed: the timeline and "
                + "the evidence still count theirs. Show them puts them back."
            : "InterCat's own processes, which collected this capture: "
                + WorkspaceLayout.Series([.. OwnProcesses.Select(CollectorText.Named)]) + ". Set aside, they leave the ranked "
                + "rows, the graph and the channels, with the channels they are an end of; no record is removed, and the "
                + "timeline and the evidence still count theirs."
            + string.Concat(wholeSnapshot.Collectors.Unfound.Select(collector => " " + CollectorText.Unfound(collector)));

    /// <summary>The command beside the line: set them aside, or show them again.</summary>
    public string CollectorsCommand => CollectorsSetAside ? "Show them" : "Set aside";

    /// <summary>InterCat's own processes the view shows, in its order.</summary>
    private IReadOnlyList<ProcessNode> OwnProcesses => [.. wholeSnapshot.Processes.Where(process => process.Collector is not null)];

    /// <summary>The records the set-aside processes hold over the whole session, under the evidence policy.</summary>
    private long SetAsideRecords => wholeSnapshot.SetAside.Sum(process => process.Records);
}
