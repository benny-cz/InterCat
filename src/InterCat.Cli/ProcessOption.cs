using System.Globalization;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// A process as a command names it: its PID where one process alone held it in the session, else its instance id as
/// `icat processes` prints it; a reused PID is refused with each of its holders.
/// </summary>
internal static class ProcessOption
{
    /// <summary>The process <paramref name="text"/> names among <paramref name="processes"/>; null, with why, for anything else.</summary>
    public static ProcessInstanceId? Resolve(IReadOnlyDictionary<ProcessInstanceId, ProcessNode> processes, string option, string text,
        out string? refusal)
    {
        refusal = null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            ProcessNode[] held = [.. processes.Values.Where(node => node.ProcessId == pid).OrderBy(node => node.PidHolder)];
            if (held.Length == 1) return held[0].Id;
            refusal = held.Length == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{option} {pid} names no process: no process of the session held PID {pid}.")
                : string.Create(CultureInfo.InvariantCulture, $"{option} {pid} names {held.Length} processes, since the PID was reused: ")
                    + string.Join(", ", held.Select(node => $"{node.Id} ({node.NameWithPid})"))
                    + $". Name one by its instance id: {option} <instance>.";
            return null;
        }

        if (Guid.TryParse(text, out Guid instance) && processes.ContainsKey(new ProcessInstanceId(instance)))
        {
            return new ProcessInstanceId(instance);
        }

        refusal = Guid.TryParse(text, out _)
            ? $"{option} {text} names no process instance of the session's current generation."
            : $"{option} names a process by its PID or its instance id, as icat processes prints them; '{text}' is neither.";
        return null;
    }
}
