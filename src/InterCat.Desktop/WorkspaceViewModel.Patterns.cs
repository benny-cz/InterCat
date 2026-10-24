using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A recurring communication pattern's processes, chosen in the patterns window (`contracts/patterns-v1.md` §7), made the
/// selection as Ctrl+click makes one (§6.7).
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>
    /// Makes exactly these processes the selection: one the rung draws alone, as a click on its node selects it; else
    /// those the window holds together, which Enter turns into their records. One the window does not hold - a process a
    /// newer generation named - is left out. False where it holds none, or lists records, where no process is selected.
    /// </summary>
    public bool ChooseProcesses(IEnumerable<ProcessInstanceId> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (IsEvidenceRung) return false;
        HashSet<ProcessInstanceId> wanted = [.. processes];
        ProcessInstanceId[] held = [.. wholeSnapshot.Processes.Select(process => process.Id).Where(wanted.Contains)];
        if (held.Length == 0) return false;
        if (held.Length == 1 && Snapshot.Processes.Any(process => process.Id == held[0]))
        {
            SelectProcess(held[0]);
            return true;
        }

        chosenProcesses.Clear();
        chosenProcesses.UnionWith(held);
        ChooseTogether();
        return true;
    }
}
