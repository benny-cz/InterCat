using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// How a process's own records were bound to it, in one set of words for the inspector and <c>icat processes</c> (R18,
/// §6.8: a process is one action from the rule, version and coverage behind its count).
/// </summary>
public static class ProcessBindingText
{
    /// <summary>A rule as a person reads it: its identity and version, "process-binding-v4, version 4".</summary>
    public static string Rule(RelationRule rule) =>
        string.Create(CultureInfo.CurrentCulture, $"{rule.Identity}, version {rule.Version:N0}");

    /// <summary>
    /// How a process's own records were bound to it over the session: the rule that bound them and how strongly, what the
    /// evidence policy left out of its total, and the capture's coverage. A later holder of a reused PID binds its records
    /// only as candidates, which a policy that admits none counts in no process; the explanation says so, and how many,
    /// so it does not read as a quiet process (R21, R22). A collector the capture names by its PID that no instance is
    /// (<paramref name="unfound"/>, §19.5) is said too, so the process is not taken for it.
    /// </summary>
    public static string Explain(ProcessNode process, IReadOnlyList<CollectorProcessV1>? unfound = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        string rule = Rule(RelationRule.Parse(ProcessInstanceIndex.BindingRule));
        string pid = string.Create(CultureInfo.InvariantCulture, $"PID {process.ProcessId}");
        string holders = CountText.Of(process.PidHolders, "process", "processes");
        string binding = process.PidHolders <= 1
            ? $"Each record naming {pid} while it ran is its own, correlated by {rule}: no other process held {pid} in this capture."
            : process.PidHolder <= 1
            ? $"Each record naming {pid} while it ran is its own, correlated by {rule}: it was the first of {holders} to hold {pid} in this capture."
            : $"{pid} was held by {holders} in this capture, and this was the {CountText.Ordinal(process.PidHolder)}. A record naming it "
                + $"while this one ran could be a late record of an earlier one, so {rule}, binds it here only as a candidate. "
                + (process.WithheldRecords > 0
                    ? "The evidence policy counts no candidate, so its total counts only its lifecycle records, leaving out "
                        + $"{CountText.Of(process.WithheldRecords, "record")} bound to it over the session."
                    : process.CandidateRecords > 0
                    ? "The evidence policy counts candidates, so its total includes the "
                        + $"{CountText.Of(process.CandidateRecords, "record")} bound to it over the session."
                    : "None is left out of its total.");
        string collector = process.Collector is { } role ? " " + CollectorText.Sentence(role) : string.Empty;
        // A collector the capture names by this PID that no instance is: said where a person would take this for it.
        string named = string.Concat((unfound ?? []).Where(other => other.ProcessId == process.ProcessId)
            .Select(other => " " + CollectorText.Unfound(other)));
        return $"{binding}{collector}{named} Coverage over the session: {CoverageStateText.Value(process.Coverage)}.";
    }
}
