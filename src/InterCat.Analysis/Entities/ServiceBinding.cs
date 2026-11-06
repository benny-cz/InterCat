using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

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

    /// <summary>
    /// Whether a service no instance hosted may have run in <paramref name="instance"/>, though the binding cannot tell: its
    /// PID is the service's, and the service's host's creation time, or the instance's own, was not read (ADR-096). A PID
    /// alone never binds a service to an instance, and never proves one hosted none either.
    /// </summary>
    public bool MayHaveHosted(ProcessInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        for (int at = 0; at < Unbound.Count; at++)
        {
            InventoriedServiceV1 service = Unbound[at].Service;
            if (service.ProcessId == instance.ProcessId && (service.HostCreatedUtc is null || instance.CreateFileTime is null))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The services <paramref name="instance"/> hosted when it is a service container (ADR-096) - an instance its capture's
    /// lists name as hosting a service - and null when it is none.
    /// </summary>
    public IReadOnlyList<HostedService>? ContainerOf(ProcessInstanceId instance) =>
        ByInstance.TryGetValue(instance, out IReadOnlyList<HostedService>? hosted) && hosted.Count > 0 ? hosted : null;
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
