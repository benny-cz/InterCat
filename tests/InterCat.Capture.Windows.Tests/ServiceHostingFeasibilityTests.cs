using System.Text.Json;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Which services a process hosts (ADR-095): FX-SVC-001's sessions, Microsoft-Windows-Services enabled at every level and
/// keyword and asked for its state, set against the service control manager's own lists of the running services and the
/// process hosting each, read as each session began and once it stopped.
/// </summary>
public sealed class ServiceHostingFeasibilityTests
{
    private const string Bench = "bench/results/service-provider-measurement-20261106T155713Z/report.json";

    [Fact(DisplayName = "P27: FX-SVC-001's Microsoft-Windows-Services names no running service as a capture starts - enabled at every level and keyword and asked for its state it delivered nothing, in three runs, of the services the service control manager listed running throughout - while a service that changed during a run was named with the process hosting it, so it is no lead for which services a process hosts")]
    public void TheServicesProviderNamesNoRunningService()
    {
        string root = RepositoryRoot();
        string evidence = Path.Combine(root, "fixtures", "FX-SVC-001", "evidence");
        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "scenario.json")));
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, Bench)));

        // The fixture's measurement is its bench report's, and its truth the service control manager's lists the runs read.
        Assert.Equal(JsonSerializer.Serialize(report.RootElement), JsonSerializer.Serialize(measurement.RootElement));
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("runs").EnumerateArray()];
        JsonElement[] truth = [.. scenario.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal(3, runs.Length);
        Assert.Equal(runs.Select(static run => JsonSerializer.Serialize(run.GetProperty("serviceControlManager"))),
            truth.Select(static run => JsonSerializer.Serialize(run.GetProperty("serviceControlManager"))));

        foreach (JsonElement run in runs)
        {
            // Nothing lost, and nothing on the provider's enabling or on its state asked for.
            Assert.Equal(0, run.GetProperty("session").GetProperty("eventsLost").GetInt32());
            Assert.Equal(0, run.GetProperty("eventsOnEnableAlone").GetInt32());

            // The manager listed services running throughout, several to a process; the provider named none of them.
            JsonElement manager = run.GetProperty("serviceControlManager");
            JsonElement changes = run.GetProperty("statusChanges");
            Assert.InRange(Count(manager, "hostingProcesses"), 1, Count(manager, "runningThroughout") - 1);
            Assert.Equal(0, Count(changes, "ofTheServicesRunningThroughout"));

            // Every service the manager listed changing between its lists was named by a status change with its host, and
            // no status change named a service and host the manager never listed; only a status change names either.
            Assert.Equal(Count(changes, "servicesTheManagerListedChanged"), Count(changes, "ofThemNamed"));
            Assert.Equal(0, Count(changes, "namingAPairTheManagerNeverListed"));
            Assert.All(run.GetProperty("events").EnumerateArray().Where(static item => item.GetProperty("id").GetInt32() != 105),
                static item => Assert.Equal(0, Count(item, "namingAProcessAndAService")));
        }

        // A service changed by itself during one run at least, and its status change named it with its host.
        Assert.Contains(runs, static run => Count(run.GetProperty("statusChanges"), "ofThemNamed") > 0);
    }

    /// <summary>FX-SVC-002's three runs through the product, by tools/InterCat.ProcessProbe --services-product.</summary>
    private const string ProductBench = "bench/results/service-product-20261106T180159Z/report.json";

    [Fact(DisplayName = "R22: FX-SVC-002 through the product - icat record under the explore profile, three runs, nothing lost - names every running service the service control manager listed as each capture began under the instance whose PID and creation time are its host's, none left unnamed and none named that it did not list")]
    public void TheProductNamesEachServiceUnderItsHost()
    {
        string root = RepositoryRoot();
        string evidence = Path.Combine(root, "fixtures", "FX-SVC-002", "evidence");
        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "scenario.json")));
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ProductBench)));

        // The fixture's measurement is its bench report's, and its truth the probe's own reads of the service control manager.
        Assert.Equal(JsonSerializer.Serialize(report.RootElement), JsonSerializer.Serialize(measurement.RootElement));
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal<(string?, string?, int)>(("FX-SVC-002", "explore", 3),
            (measurement.RootElement.GetProperty("scenario").GetString(), measurement.RootElement.GetProperty("profile").GetString(), runs.Length));
        Assert.Equal(runs.Select(static run => JsonSerializer.Serialize(run.GetProperty("readByTheProbe"))),
            scenario.RootElement.GetProperty("runs").EnumerateArray().Select(static run => JsonSerializer.Serialize(run.GetProperty("readByTheProbe"))));

        foreach (JsonElement run in runs)
        {
            // Nothing lost, dropped or undecodable, and the capture read its services at both ends.
            JsonElement health = run.GetProperty("health");
            Assert.Equal(health.GetProperty("observedRecords").GetInt64(), health.GetProperty("admittedRecords").GetInt64());
            Assert.All(["providerReportedEventLoss", "consumerReportedBufferLoss", "applicationDrops", "undecodableRecords"],
                name => Assert.Equal(0L, health.GetProperty(name).GetInt64()));
            Assert.Equal(["Start", "Stop"], run.GetProperty("servicesRead").EnumerateArray().Select(static moment => moment.GetString()));

            // Every service the probe read with its host's creation time is named as hosted, as the capture started, by the one
            // instance with its host's PID and creation time; and the session names no other at the start.
            JsonElement read = run.GetProperty("readByTheProbe");
            JsonElement named = run.GetProperty("namedByTheSession");
            Assert.InRange(Count(read, "withTheirHostsCreationTime"), 1, int.MaxValue);
            Assert.InRange(Count(read, "hosts"), 1, Count(read, "services") - 1);
            Assert.Equal(Count(read, "services"), Count(read, "withTheirHostsCreationTime") + Count(read, "hostNotOpened"));
            Assert.Equal((Count(read, "withTheirHostsCreationTime"), 0, 0),
                (Count(named, "hostedAsTheCaptureStarted"), Count(named, "hostsInstanceNamingItNot"), Count(named, "noInstanceOfItsHost")));
            Assert.Equal(Count(read, "services"), Count(run, "servicesTheSessionNamesAtTheStart"));
        }
    }

    private static int Count(JsonElement element, string name) => element.GetProperty(name).GetInt32();

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
