using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The running services this machine's service control manager lists, each with the process hosting it (ADR-095): read by
/// its own query with the right to enumerate services alone, each service once, each host's creation time read once.
/// </summary>
public sealed class RunningServicesTests
{
    [Fact(DisplayName = "§18.5: the service control manager lists this machine's running services, each once with the PID hosting it, services sharing a host sharing its creation time")]
    public void TheManagerListsTheRunningServices()
    {
        IReadOnlyList<RunningService> services = RunningServices.Read();

        // A Windows machine runs services - the service control manager's own clients among them - each named once, its
        // name compared ignoring case as the manager compares it, each with a PID.
        Assert.NotEmpty(services);
        Assert.Equal(services.Count, services.Select(static service => service.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(services, static service => Assert.True(service.ProcessId > 0 && service.Name.Length > 0, service.Name));

        // A host's creation time is read once, so every service it hosts carries the same one, in UTC.
        Assert.All(services.GroupBy(static service => service.ProcessId),
            static host => Assert.Single(host.Select(static service => service.HostCreatedUtc).Distinct()));
        Assert.All(services.Where(static service => service.HostCreatedUtc is not null),
            static service => Assert.Equal(TimeSpan.Zero, service.HostCreatedUtc!.Value.Offset));
    }
}
