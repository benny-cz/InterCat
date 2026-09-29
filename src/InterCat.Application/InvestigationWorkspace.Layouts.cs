namespace InterCat.Application;

/// <summary>A node a person placed on a session's graph: its stable graph key, and where they put it (§19.4).</summary>
public sealed record WorkspacePin
{
    /// <summary>The node's stable key: a process instance, a group or an aggregate, as the graph names it.</summary>
    public required string Key { get; init; }

    /// <summary>Where across the graph, from 0 to 1.</summary>
    public required double X { get; init; }

    /// <summary>Where down the graph, from 0 to 1.</summary>
    public required double Y { get; init; }
}

/// <summary>
/// How a person laid out a member session's graph (§26.3's workspace scope): the nodes they pinned, where. It is a
/// preference, not a finding, so a member has one, replaced as it changes rather than kept as revisions.
/// </summary>
public sealed record WorkspaceLayout
{
    public required Guid SessionId { get; init; }

    /// <summary>The pinned nodes, by key.</summary>
    public required IReadOnlyList<WorkspacePin> Pins { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }
}

public static partial class InvestigationWorkspace
{
    /// <summary>The most nodes one member's layout pins.</summary>
    public const int MostPins = 1_024;

    /// <summary>The longest a pinned node's key may be, in characters.</summary>
    public const int MostPinKeyCharacters = 256;

    /// <summary>
    /// Keeps <paramref name="pins"/> as how member <paramref name="sessionId"/>'s graph is laid out, replacing its earlier
    /// layout; no pin removes it. Null when it is removed.
    /// </summary>
    public static WorkspaceLayout? SetLayout(string workspacePath, Guid sessionId, IReadOnlyList<WorkspacePin> pins, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pins);
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string file) = Load(full);
        if (workspace.Members.All(member => member.SessionId != sessionId))
        {
            throw new InvalidOperationException($"Session {sessionId:N} is not a member of this investigation, so it keeps no layout here.");
        }

        if (PinsProblem(pins) is { } problem)
        {
            throw new InvalidOperationException($"The layout is refused: {problem}.");
        }

        WorkspaceLayout? layout = pins.Count == 0
            ? null
            : new() { SessionId = sessionId, Pins = [.. pins.OrderBy(pin => pin.Key, StringComparer.Ordinal)], UpdatedUtc = now };
        Save(full, workspace with
        {
            Layouts = [.. workspace.Layouts.Where(kept => kept.SessionId != sessionId), .. layout is null ? [] : new[] { layout }],
            UpdatedUtc = now,
        }, file);
        return layout;
    }

    /// <summary>How member <paramref name="sessionId"/>'s graph is laid out, or null when no node of it is pinned here.</summary>
    public static WorkspaceLayout? LayoutOf(InvestigationWorkspaceFile workspace, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.Layouts.FirstOrDefault(layout => layout.SessionId == sessionId);
    }

    private static string? PinsProblem(IReadOnlyList<WorkspacePin> pins) =>
        pins.Count > MostPins ? $"it pins more than {MostPins:N0} nodes"
        : pins.Any(pin => pin is null || string.IsNullOrWhiteSpace(pin.Key) || pin.Key.Length > MostPinKeyCharacters)
            ? $"a pinned node has no key, or one beyond {MostPinKeyCharacters} characters"
        : pins.GroupBy(pin => pin.Key, StringComparer.Ordinal).Any(group => group.Count() > 1) ? "a node is pinned twice"
        : pins.Any(pin => !new GraphPoint(pin.X, pin.Y).IsValid) ? "a node is pinned outside the graph"
        : null;

    /// <summary>What makes a file's layouts contradict themselves, or null (`contracts/workspace-v11.md` §7).</summary>
    private static string? LayoutProblem(InvestigationWorkspaceFile workspace)
    {
        // Layouts arrived with the eleventh version (revision 280).
        if (workspace.Layouts.Count > 0 && VersionOf(workspace) < 11)
        {
            return $"a {workspace.Contract} file holds no layout";
        }

        if (workspace.Layouts.Any(layout => layout is null || layout.Pins is null))
        {
            return "it lists an empty layout";
        }

        HashSet<Guid> members = [.. workspace.Members.Select(member => member.SessionId)];
        if (workspace.Layouts.GroupBy(layout => layout.SessionId).FirstOrDefault(group => group.Count() > 1) is { } twice)
        {
            return $"session {twice.Key:N} has two layouts";
        }

        return workspace.Layouts.FirstOrDefault(layout => !members.Contains(layout.SessionId) || layout.Pins.Count == 0
            || PinsProblem(layout.Pins) is not null) is { } wrong
            ? $"the layout of session {wrong.SessionId:N} "
                + (!members.Contains(wrong.SessionId) ? "is of no member" : wrong.Pins.Count == 0 ? "pins nothing" : "is refused: " + PinsProblem(wrong.Pins))
            : null;
    }
}
