using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// `service-binding-v1` (ADR-095): the services a capture's service inventories list, each bound to the process instance
/// whose PID and creation time are its host's, and said in one set of words for the window and the command line.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class ServiceBindingTests
{
    [Fact(DisplayName = "R22: a service binds to the instance whose PID and creation time are its host's - never to a later holder of the PID - with the moments its inventories listed it at, and a host whose creation time was not read binds none")]
    public void AServiceBindsToItsHostsInstance()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields, ServiceInventoryV1[] inventories) = ServiceHosts();
        Publish(session.Store, rows, fields: fields, services: inventories);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessNode Node(int pid, int holder) => overview.Nodes.Single(node => node.ProcessId == pid && node.PidHolder == holder);

        // The first svchost hosted Dnscache and nsi as the capture started; the tool that took its PID later hosted none.
        Assert.Equal([new HostedService("Dnscache", true, false), new HostedService("nsi", true, false)], Node(1820, 1).Services);
        IReadOnlyList<HostedService>? tool = Node(1820, 2).Services;
        Assert.NotNull(tool);
        Assert.Empty(tool);

        // The second hosted W32Time at both ends and BITS at the stop alone.
        Assert.Equal([new HostedService("W32Time", true, true), new HostedService("BITS", false, true)], Node(2400, 1).Services);

        // Protected's host could not be opened, so no instance hosted it; the capture read its services at both ends.
        (ServiceInventoryMoment moment, InventoriedServiceV1 service) = Assert.Single(overview.Services.Unbound);
        Assert.Equal(("Protected", ServiceInventoryMoment.Start), (service.Name, moment));
        Assert.Equal([ServiceInventoryMoment.Start, ServiceInventoryMoment.Stop], overview.Services.Moments);

        // A capture that read none records none: no instance's services are known, none or any.
        SessionDerivationCache.Clear();
        using var plain = new TemporarySession();
        Publish(plain.Store, rows, fields: fields);
        SessionOverviewBundle unread = SessionOverviewProjector.Project(plain.Store);
        Assert.False(unread.Services.Recorded);
        Assert.All(unread.Nodes, static node => Assert.Null(node.Services));
    }

    [Fact(DisplayName = "R3: a process's services are said as named, none or not recorded, with the moments its capture read them at - a service at one end alone said to be there alone, and past six the rest counted")]
    public void TheServicesAreSaidInOneSetOfWords()
    {
        ServiceInventoryMoment[] both = [ServiceInventoryMoment.Start, ServiceInventoryMoment.Stop];
        Assert.Equal("Not recorded: this capture kept no list of the services running", ServiceText.Of(null, []));
        Assert.Equal("None as the capture started and stopped", ServiceText.Of([], both));
        Assert.Equal("None as the capture started; its stop's list was not read", ServiceText.Of([], [ServiceInventoryMoment.Start]));
        Assert.Equal("W32Time, BITS (at the stop alone), Dnscache (at the start alone) · as the capture started and stopped",
            ServiceText.Of([new("W32Time", true, true), new("BITS", false, true), new("Dnscache", true, false)], both));

        // A capture that read one end alone cannot say a service was there alone: none is tagged.
        Assert.Equal("Dnscache · as the capture started; its stop's list was not read",
            ServiceText.Of([new("Dnscache", true, false)], [ServiceInventoryMoment.Start]));
        Assert.Equal("s1, s2, s3, s4, s5, s6 and 2 more · as the capture started and stopped",
            ServiceText.Of([.. Enumerable.Range(1, 8).Select(index => new HostedService("s" + index, true, true))], both));
    }
}
