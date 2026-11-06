using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>A service a process instance hosted, and at which of its capture's inventories: its start's, its stop's, or both.</summary>
public sealed record HostedService(string Name, bool AtStart, bool AtStop);

/// <summary>
/// The services a capture's process instances hosted (`service-binding-v1`, `contracts/service-inventory-v1.md` §3): each
/// instance's, and the services no instance hosted - one whose host's creation time was not read, or no instance held.
/// </summary>
/// <param name="ByInstance">The services each instance that hosted any hosted, in the order its inventories list them.</param>
/// <param name="Unbound">The services no instance hosted, at each moment the capture read them.</param>
/// <param name="Moments">The moments the capture read its services at; none for a capture that read none.</param>
public sealed record ServiceMatch(
    IReadOnlyDictionary<ProcessInstanceId, IReadOnlyList<HostedService>> ByInstance,
    IReadOnlyList<(ServiceInventoryMoment Moment, InventoriedServiceV1 Service)> Unbound,
    IReadOnlyList<ServiceInventoryMoment> Moments)
{
    /// <summary>A capture that read no services: no instance hosted any it knows of, and none is unbound.</summary>
    public static ServiceMatch NotRecorded { get; } = new(new Dictionary<ProcessInstanceId, IReadOnlyList<HostedService>>(), [], []);

    /// <summary>Whether the capture read its services at all; an import, a package or an older capture did not.</summary>
    public bool Recorded => Moments.Count > 0;

    /// <summary>
    /// The services <paramref name="instance"/> hosted at the moments the capture read them: none where it hosted none it
    /// read, and null where the capture read none, so whether it hosted any is not recorded (R3).
    /// </summary>
    public IReadOnlyList<HostedService>? Of(ProcessInstanceId instance) =>
        !Recorded ? null : ByInstance.TryGetValue(instance, out IReadOnlyList<HostedService>? hosted) ? hosted : [];
}

/// <summary>
/// `service-binding-v1`: a process instance hosted a service at an inventory's moment when its PID is the service's and the
/// creation time its lifecycle records carry is the host's. A PID alone is reused by the machine and an image name proves
/// nothing, so neither is ever taken for a host, and a service whose host's creation time is unknown binds to no instance.
/// </summary>
public static class ServiceBinding
{
    public const string Rule = "service-binding-v1";

    /// <summary>The services <paramref name="inventories"/> list, each bound to the instance of <paramref name="instances"/> hosting it.</summary>
    public static ServiceMatch Match(IReadOnlyList<ProcessInstance> instances, IReadOnlyList<ServiceInventoryV1> inventories)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(inventories);
        if (inventories.Count == 0)
        {
            return ServiceMatch.NotRecorded;
        }

        // Each instance by the PID and creation time that identify it; one with no creation time hosts nothing it can be told by.
        var byHost = new Dictionary<(int ProcessId, long Created), ProcessInstanceId>();
        foreach (ProcessInstance instance in instances)
        {
            if (instance.CreateFileTime is { } created)
            {
                byHost.TryAdd((instance.ProcessId, created), instance.Id);
            }
        }

        var hosted = new Dictionary<ProcessInstanceId, List<HostedService>>();
        var unbound = new List<(ServiceInventoryMoment, InventoriedServiceV1)>();
        foreach (ServiceInventoryV1 inventory in inventories)
        {
            bool atStart = inventory.Moment == ServiceInventoryMoment.Start;
            foreach (InventoriedServiceV1 service in inventory.Services)
            {
                if (service.HostCreatedUtc is not { } created || !byHost.TryGetValue((service.ProcessId, created.ToFileTime()), out ProcessInstanceId host))
                {
                    unbound.Add((inventory.Moment, service));
                    continue;
                }

                List<HostedService> services = hosted.TryGetValue(host, out List<HostedService>? known) ? known : hosted[host] = [];
                int at = services.FindIndex(existing => string.Equals(existing.Name, service.Name, StringComparison.OrdinalIgnoreCase));
                HostedService entry = at < 0
                    ? new(service.Name, atStart, !atStart)
                    : services[at] with { AtStart = services[at].AtStart || atStart, AtStop = services[at].AtStop || !atStart };
                if (at < 0)
                {
                    services.Add(entry);
                }
                else
                {
                    services[at] = entry;
                }
            }
        }

        return new(
            hosted.ToDictionary(static entry => entry.Key, static entry => (IReadOnlyList<HostedService>)entry.Value.AsReadOnly()),
            unbound,
            [.. inventories.Select(static inventory => inventory.Moment).Distinct().Order()]);
    }
}

/// <summary>How a person reads which services a process hosted: one set of words for the window and the command line (R5).</summary>
public static class ServiceText
{
    /// <summary>At most this many services are named; the rest are counted.</summary>
    public const int Named = 6;

    /// <summary>
    /// The services a process hosted, as its inspector and <c>icat processes</c> say them: named, the rest counted, with the
    /// moments the capture read them at; none where it hosted none of those read; or that the capture kept no list.
    /// </summary>
    public static string Of(IReadOnlyList<HostedService>? services, IReadOnlyList<ServiceInventoryMoment> moments)
    {
        ArgumentNullException.ThrowIfNull(moments);
        if (services is null)
        {
            return "Not recorded: this capture kept no list of the services running";
        }

        string read = Moments(moments);
        if (services.Count == 0)
        {
            return "None " + read;
        }

        string names = string.Join(", ", services.Take(Named).Select(Named_));
        return names
            + (services.Count > Named ? string.Create(CultureInfo.CurrentCulture, $" and {services.Count - Named:N0} more") : string.Empty)
            + " · " + read;

        // A service listed at one end of a capture that read both is said to be there alone: it started or stopped between.
        string Named_(HostedService service) => moments.Count < 2 || (service.AtStart && service.AtStop) ? service.Name
            : service.AtStart ? service.Name + " (at the start alone)" : service.Name + " (at the stop alone)";
    }

    // The moments a capture read its services at, as the end of a sentence.
    private static string Moments(IReadOnlyList<ServiceInventoryMoment> moments) => moments switch
    {
        [ServiceInventoryMoment.Start] => "as the capture started; its stop's list was not read",
        [ServiceInventoryMoment.Stop] => "as the capture stopped; its start's list was not read",
        _ => "as the capture started and stopped",
    };
}
