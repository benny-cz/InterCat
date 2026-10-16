using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A group's processes at its rung where every one is the same program's (§6.1, §6.3). Thirty rows read "svchost.exe",
/// the PID that tells them apart cut from the rail after "created during", and nothing on a row said what the instance
/// did. Each is named by its PID, the program's name being the rung's own, and its second line says first what it did -
/// how many relationships it has, and where its connections to other hosts ran, naming the host where it reached one -
/// then how it was seen to start. A screen reader hears its own name, and its tooltip, crumb and export keep it.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The rows of a group's rung named by their instances, where two or more are one program's; else as they are.</summary>
    private IReadOnlyList<RungRow> InstancesNamed(IReadOnlyList<RungRow> rows)
    {
        if (rows.Count < 2 || rows.Any(row => row.Source.DescendsTo != DetailLevel.ProcessInstance
                || !string.Equals(row.Source.Label, rows[0].Source.Label, StringComparison.Ordinal)))
        {
            return rows;
        }

        // Only the rung's own instances are read: a busy machine's thousands of processes, relationships and host links are
        // not, on every read of the rows.
        HashSet<string> keys = new(rows.Select(row => row.Key), StringComparer.Ordinal);
        Dictionary<string, ProcessNode> processes = new(StringComparer.Ordinal);
        foreach (ProcessNode process in Snapshot.Processes)
        {
            string key = process.Id.ToString();
            if (keys.Contains(key))
            {
                processes.TryAdd(key, process);
            }
        }

        HashSet<ProcessInstanceId> listed = [.. processes.Values.Select(process => process.Id)];
        Dictionary<ProcessInstanceId, int> related = [];
        foreach (CommunicationEdge edge in Snapshot.Edges)
        {
            if (listed.Contains(edge.SourceId))
            {
                related[edge.SourceId] = related.GetValueOrDefault(edge.SourceId) + 1;
            }

            if (edge.TargetId != edge.SourceId && listed.Contains(edge.TargetId))
            {
                related[edge.TargetId] = related.GetValueOrDefault(edge.TargetId) + 1;
            }
        }

        // Connections to other hosts are the whole session's, as an aggregate's list says them, which a brush says.
        ILookup<ProcessInstanceId, HostLink> links = wholeSnapshot.HostLinks.Where(link => listed.Contains(link.Process))
            .ToLookup(link => link.Process);
        string wholeOnly = appliedInterval is null ? string.Empty : " · whole session";
        return [.. rows.Select(row => processes.TryGetValue(row.Key, out ProcessNode? process)
            ? row with
            {
                Label = process.PidLabel,
                Detail = Did(process, related.GetValueOrDefault(process.Id), links[process.Id], wholeOnly),
                SpokenLabel = process.NameWithPid,
            }
            : row)];
    }

    /// <summary>What an instance did, then how it was seen to start: "7 relationships · created during capture".</summary>
    private static string Did(ProcessNode process, int relationships, IEnumerable<HostLink> links, string wholeOnly)
    {
        string started = process.Caption.StartsWith(process.PidLabel + " · ", StringComparison.Ordinal)
            ? process.Caption[(process.PidLabel.Length + 3)..]
            : process.Caption;
        string[] reached = [.. links.OrderBy(link => link.Mechanism).Select(Reached)];
        return string.Join(" · ", new[]
        {
            relationships > 0 ? CountText.Of(relationships, "relationship") : string.Empty,
            reached.Length > 0 ? string.Join(" · ", reached) + wholeOnly : string.Empty,
            started,
        }.Where(part => part.Length > 0));
    }

    /// <summary>
    /// An instance's connections to other hosts over one transport, as its rung's rows name them: the host where it reached
    /// one - "→ 20.42.65.92:443" where it connected, "← 192.168.1.30 at port 3389" where it accepted them, "↔
    /// 192.168.1.1:53" for datagrams - else how many, to how many hosts.
    /// </summary>
    private static string Reached(HostLink link)
    {
        if (link.Addresses is not [HostAddress host])
        {
            return HostLinkText.Reach(link);
        }

        if (link.Mechanism != Mechanism.Udp && host.Accepted > 0 && host.Accepted == host.Connections)
        {
            return host.AcceptedPorts.Count == 0
                ? "← " + host.Address
                : $"← {host.Address} at {HostLinkText.Ports(host.AcceptedPorts)}";
        }

        string at = host.Ports is [ushort port] ? WithPort(host.Address, port) : host.Address;
        return (link.Mechanism == Mechanism.Udp ? "↔ " : "→ ") + at;
    }

    /// <summary>An address and a port, an IPv6 address bracketed so the port cannot read as one of its groups.</summary>
    private static string WithPort(string address, ushort port) =>
        (address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address) + ":"
        + port.ToString(CultureInfo.InvariantCulture);
}
