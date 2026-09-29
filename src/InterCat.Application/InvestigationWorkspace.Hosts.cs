namespace InterCat.Application;

/// <summary>What a person decided of two host identities (`contracts/workspace-v10.md` §4).</summary>
public enum WorkspaceHostDecision
{
    /// <summary>A person confirmed the two are one host: a machine renamed or reinstalled, or a file imported from it.</summary>
    Confirmed = 1,

    /// <summary>A person withdrew their confirmation: from this revision the two are two hosts again.</summary>
    Withdrawn = 2,
}

/// <summary>
/// One revision of a person's statement that two host identities are one host, or its withdrawal (§8.3). Equal identities
/// are the strongest local evidence of one host and never proof; different ones are one host only by such a statement,
/// which is kept when a later revision withdraws it and is never evidence of its own.
/// </summary>
public sealed record WorkspaceHostEquivalence
{
    public required int Revision { get; init; }

    public required WorkspaceHostDecision Decision { get; init; }

    public required Guid First { get; init; }

    public required Guid Second { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }
}

public static partial class InvestigationWorkspace
{
    /// <summary>
    /// Records a person's confirmation that host identities <paramref name="first"/> and <paramref name="second"/> are one
    /// host, as a revision of the investigation. It changes no session: their identities stay what they recorded.
    /// </summary>
    public static WorkspaceHostEquivalence ConfirmOneHost(string workspacePath, Guid first, Guid second, string? note, DateTimeOffset now) =>
        DecideHosts(workspacePath, first, second, WorkspaceHostDecision.Confirmed, note, now);

    /// <summary>Withdraws a person's confirmation that two host identities are one host, kept as a revision of its own.</summary>
    public static WorkspaceHostEquivalence WithdrawOneHost(string workspacePath, Guid first, Guid second, DateTimeOffset now) =>
        DecideHosts(workspacePath, first, second, WorkspaceHostDecision.Withdrawn, null, now);

    /// <summary>
    /// Whether two host identities are one host: equal, or joined by confirmations in force - directly, or through another
    /// identity each was confirmed one host with.
    /// </summary>
    public static bool OneHost(InvestigationWorkspaceFile workspace, Guid first, Guid second) =>
        first == second || OneHostWith(workspace, first).Contains(second);

    /// <summary>Whether two identities are one host only by a person's confirmation: different, and confirmed one.</summary>
    public static bool OneHostByConfirmation(InvestigationWorkspaceFile workspace, Guid first, Guid second) =>
        first != second && OneHostWith(workspace, first).Contains(second);

    /// <summary>The other host identities a person's confirmations in force make one host with <paramref name="host"/>.</summary>
    public static IReadOnlyList<Guid> OneHostWith(InvestigationWorkspaceFile workspace, Guid host)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        (Guid First, Guid Second)[] confirmed = [.. EquivalencesInForce(workspace).Select(equivalence => (equivalence.First, equivalence.Second))];
        var reached = new List<Guid> { host };
        for (int index = 0; index < reached.Count; index++)
        {
            Guid current = reached[index];
            foreach ((Guid first, Guid second) in confirmed)
            {
                Guid? other = first == current ? second : second == current ? first : null;
                if (other is { } next && !reached.Contains(next))
                {
                    reached.Add(next);
                }
            }
        }

        return [.. reached.Skip(1)];
    }

    /// <summary>One identity to stand for a host: the least of those confirmed one host with it, itself when there are none.</summary>
    public static Guid HostKey(InvestigationWorkspaceFile workspace, Guid host) =>
        OneHostWith(workspace, host).Append(host).Min();

    /// <summary>Every confirmation in force, one per pair of identities, in the order first made.</summary>
    public static IReadOnlyList<WorkspaceHostEquivalence> EquivalencesInForce(InvestigationWorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return [.. workspace.HostEquivalences
            .GroupBy(equivalence => HostPair(equivalence.First, equivalence.Second))
            .Select(group => group.MaxBy(equivalence => equivalence.Revision)!)
            .Where(equivalence => equivalence.Decision == WorkspaceHostDecision.Confirmed)
            .OrderBy(equivalence => equivalence.Revision)];
    }

    private static WorkspaceHostEquivalence DecideHosts(
        string workspacePath,
        Guid first,
        Guid second,
        WorkspaceHostDecision decision,
        string? note,
        DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        foreach (Guid host in new[] { first, second })
        {
            if (workspace.Members.All(member => member.HostId != host))
            {
                throw new InvalidOperationException($"No member of this workspace was recorded on host {host:N}.");
            }
        }

        if (first == second)
        {
            throw new InvalidOperationException("One identity is one host already; a confirmation joins two different identities.");
        }

        bool inForce = EquivalencesInForce(workspace).Any(equivalence => HostPair(equivalence.First, equivalence.Second) == HostPair(first, second));
        if (decision == WorkspaceHostDecision.Withdrawn && !inForce)
        {
            throw new InvalidOperationException($"No confirmation that hosts {first:N} and {second:N} are one is in force, so there "
                + "is none to withdraw.");
        }

        if (decision == WorkspaceHostDecision.Confirmed && inForce)
        {
            throw new InvalidOperationException($"Hosts {first:N} and {second:N} are confirmed one host already.");
        }

        var equivalence = new WorkspaceHostEquivalence
        {
            Revision = workspace.HostEquivalences.Count == 0 ? 1 : checked(workspace.HostEquivalences.Max(known => known.Revision) + 1),
            Decision = decision,
            First = first,
            Second = second,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            RecordedUtc = now,
        };
        Save(full, workspace with { HostEquivalences = [.. workspace.HostEquivalences, equivalence], UpdatedUtc = now }, text);
        return equivalence;
    }

    private static (Guid, Guid) HostPair(Guid a, Guid b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);

    /// <summary>What makes a file's host confirmations contradict themselves, or null (`contracts/workspace-v10.md` §4).</summary>
    private static string? HostProblem(InvestigationWorkspaceFile workspace)
    {
        // A person's confirmation that two hosts are one arrived with the seventh version (revision 266).
        if (workspace.HostEquivalences.Count > 0 && VersionOf(workspace) < 7)
        {
            return $"a {workspace.Contract} file holds no confirmation that two hosts are one";
        }

        if (workspace.HostEquivalences.Any(equivalence => equivalence is null))
        {
            return "it lists an empty host confirmation";
        }

        if (workspace.HostEquivalences.GroupBy(equivalence => equivalence.Revision).FirstOrDefault(group => group.Key < 1 || group.Count() > 1)
            is { } revision)
        {
            return $"host confirmation revision {revision.Key} is not a unique positive number";
        }

        HashSet<Guid> hosts = [.. workspace.Members.Select(member => member.HostId)];
        return workspace.HostEquivalences.FirstOrDefault(equivalence => !Enum.IsDefined(equivalence.Decision)
            || !hosts.Contains(equivalence.First) || !hosts.Contains(equivalence.Second) || equivalence.First == equivalence.Second) is { } wrong
            ? $"host confirmation revision {wrong.Revision} is of no known decision, names a host no member was recorded on, "
                + "or joins an identity to itself"
            : null;
    }
}
