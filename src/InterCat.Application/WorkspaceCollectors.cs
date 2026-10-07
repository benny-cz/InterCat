using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// §19.5's view filter: InterCat's own processes - the instances a capture's collectors are (`collector-binding-v1`) - set
/// aside from a snapshot's groups, processes, relationships and channels, which keeps what it set aside to be stated. No
/// record is removed: the timeline and every evidence page still read theirs, an export of the view says what it set aside,
/// and showing them again puts the snapshot back as it was projected.
/// </summary>
public static class WorkspaceCollectors
{
    /// <summary>
    /// <paramref name="snapshot"/> without InterCat's own processes, their relationships and channels, and a group left with
    /// none; the snapshot itself when it holds none of them.
    /// </summary>
    public static WorkspaceSnapshot SetAside(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ProcessNode[] own = [.. snapshot.Processes.Where(process => process.Collector is not null)];
        if (own.Length == 0)
        {
            return snapshot;
        }

        HashSet<ProcessInstanceId> aside = [.. own.Select(process => process.Id)];
        ProcessNode[] processes = [.. snapshot.Processes.Where(process => !aside.Contains(process.Id))];
        HashSet<string> groups = new(processes.Select(process => process.GroupKey), StringComparer.Ordinal);
        CommunicationEdge[] edges =
        [
            .. snapshot.Edges.Where(edge => !aside.Contains(edge.SourceId) && !aside.Contains(edge.TargetId)),
        ];
        HashSet<string> kept = new(edges.Select(edge => edge.Key), StringComparer.Ordinal);
        Channel[] channels = [.. snapshot.Channels.Where(channel => kept.Contains(channel.EdgeKey))];
        HashSet<string> keptChannels = new(channels.Select(channel => channel.Key), StringComparer.Ordinal);
        ChannelOperation[] operations = [.. snapshot.Operations.Where(operation => keptChannels.Contains(operation.ChannelKey))];
        HashSet<string> keptOperations = new(operations.Select(operation => operation.Key), StringComparer.Ordinal);
        return snapshot with
        {
            Groups = [.. snapshot.Groups.Where(group => groups.Contains(group.Key))],
            Processes = processes,
            Edges = edges,
            Channels = channels,
            Operations = operations,
            Evidence = [.. snapshot.Evidence.Where(mark => keptOperations.Contains(mark.OperationKey))],
            SetAside = own,
        };
    }

    /// <summary>
    /// The instances <paramref name="snapshot"/> sets aside, which a ranking's measures leave out of every total they state
    /// over the rows shown - a group's, and the machine rung's - while each keeps its own.
    /// </summary>
    public static IReadOnlySet<ProcessInstanceId> Instances(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.SetAside.Select(process => process.Id).ToHashSet();
    }

    /// <summary>The graph's identity for a snapshot whose collectors are set aside, so its layout is its own.</summary>
    public static string Identity(string graphIdentity, bool setAside)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        return setAside ? graphIdentity + "|collectors:aside" : graphIdentity;
    }
}
