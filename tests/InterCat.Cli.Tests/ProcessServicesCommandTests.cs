using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// A process's services in icat processes (ADR-095): the services its capture's inventories list by its PID and creation
/// time, none where it hosted none of those read, and not recorded where the capture read none - kept apart in its JSON as
/// a list, an empty list and null, beside the moments the capture read them at (R3).
/// </summary>
public sealed class ProcessServicesCommandTests
{
    [Fact(DisplayName = "R3: icat processes names the services a process hosted as its capture started and stopped, says none for a later holder of a host's PID, and not recorded for a capture that read none; its JSON keeps the three apart")]
    public async Task AProcesssServicesAreShown()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields, ServiceInventoryV1[] inventories) = ServiceHosts();
        Publish(session.Store, rows, fields: fields, services: inventories);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "2400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Services +W32Time, BITS \(at the stop alone\) · as the capture started and stopped\r?$", output);
        (code, output) = await Run("processes", session.Path, "--pid", "1820");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Services +Dnscache \(at the start alone\), nsi \(at the start alone\) · as the capture started and stopped\r?$", output);
        Assert.Matches(@"(?m)^  Services +None as the capture started and stopped\r?$", output);

        (code, output) = await Run("processes", session.Path, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using (JsonDocument listed = JsonDocument.Parse(output))
        {
            Assert.Equal(["Start", "Stop"], listed.RootElement.GetProperty("servicesRead").EnumerateArray().Select(static moment => moment.GetString()));
            JsonElement[] hosts = [.. listed.RootElement.GetProperty("instances").EnumerateArray()
                .Where(static item => item.GetProperty("process").GetProperty("processId").GetInt32() == 2400)];
            JsonElement services = Assert.Single(hosts).GetProperty("services");
            Assert.Equal([("W32Time", true, true), ("BITS", false, true)], services.EnumerateArray().Select(static service =>
                (service.GetProperty("name").GetString(), service.GetProperty("atStart").GetBoolean(), service.GetProperty("atStop").GetBoolean())));
        }

        // A capture that read no services records none for any process: null, never an empty list.
        using var plain = new TemporarySession();
        Publish(plain.Store, rows, fields: fields);
        plain.Store.ReleaseSegmentReaders();
        (code, output) = await Run("processes", plain.Path, "--pid", "2400");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Services +Not recorded: this capture kept no list of the services running\r?$", output);
        (code, output) = await Run("processes", plain.Path, "--json");
        using JsonDocument unread = JsonDocument.Parse(output);
        Assert.Empty(unread.RootElement.GetProperty("servicesRead").EnumerateArray());
        Assert.All(unread.RootElement.GetProperty("instances").EnumerateArray(),
            static item => Assert.Equal(JsonValueKind.Null, item.GetProperty("services").ValueKind));
    }

    [Fact(DisplayName = "R3: icat metric --group-by service-container ranks each host of the services its capture listed as a group of its own, named by its services, every other process by its executable, and refuses a capture that listed none")]
    public async Task MetricGroupsByServiceContainer()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields, ServiceInventoryV1[] inventories) = ServiceHosts();
        Publish(session.Store, rows, fields: fields, services: inventories);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("metric", session.Path, "--metric", "observations", "--group-by", "service-container");
        Assert.Equal(InterCatExitCode.Success, code);
        string text = output.ReplaceLineEndings("\n");
        Assert.Contains("\nOBSERVATIONS BY SERVICE CONTAINER\n", text, StringComparison.Ordinal);
        Assert.Contains("  By service container, every other process by its executable (", text, StringComparison.Ordinal);

        // PID 1820's first instance hosted Dnscache and nsi, and its rundown and exit are its group's two records; the later
        // holder of the PID hosted none and is grouped by its executable, ranked before PID 2400's container on the tie.
        Assert.Matches(@"\n\s+1\s+PID 1820 svchost\.exe hosting Dnscache, nsi\s+2\s", text);
        Assert.Matches(@"\n\s+2\s+C:\\Tools\\tool\.exe\s+1\s", text);
        Assert.Matches(@"\n\s+3\s+PID 2400 svchost\.exe hosting W32Time, BITS\s+1\s", text);

        (code, output) = await Run("metric", session.Path, "--metric", "observations", "--group-by", "service-container", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using (JsonDocument json = JsonDocument.Parse(output))
        {
            JsonElement grouping = json.RootElement.GetProperty("grouping");
            Assert.Equal("ServiceContainer", grouping.GetProperty("grouping").GetString());
            Assert.Equal(
                [("ServiceContainer", "Dnscache,nsi", 2L), ("Executable", string.Empty, 1L), ("ServiceContainer", "W32Time,BITS", 1L)],
                grouping.GetProperty("groups").EnumerateArray().Select(static group => (
                    group.GetProperty("kind").GetString(),
                    group.GetProperty("services").ValueKind == JsonValueKind.Null
                        ? string.Empty
                        : string.Join(',', group.GetProperty("services").EnumerateArray().Select(static name => name.GetString())),
                    group.GetProperty("value").GetInt64())));
        }

        // A service listed in PID 1820 whose host's creation time was not read may have run in its later holder, which is
        // then stated by that reason rather than grouped by its executable.
        using var unsure = new TemporarySession();
        Publish(unsure.Store, rows, fields: fields, services:
        [
            inventories[0] with { Services = [.. inventories[0].Services, new() { Name = "Hidden", ProcessId = 1820 }] },
            inventories[1],
        ]);
        unsure.Store.ReleaseSegmentReaders();
        (code, output) = await Run("metric", unsure.Path, "--metric", "observations", "--group-by", "service-container");
        Assert.Equal(InterCatExitCode.Success, code);
        text = output.ReplaceLineEndings("\n");
        Assert.Contains("  Not attributed to a service container or an executable, by reason (never a peer, never ranked):", text, StringComparison.Ordinal);
        Assert.Contains("the process is known, but a service listed running may have run in it, and no creation time tells", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Tools\tool.exe", text, StringComparison.Ordinal);

        // A capture that kept no list of its services names no container, and none is guessed from a process's image.
        using var plain = new TemporarySession();
        Publish(plain.Store, rows, fields: fields);
        plain.Store.ReleaseSegmentReaders();
        (code, output) = await Run("metric", plain.Path, "--metric", "observations", "--group-by", "service-container");
        Assert.Contains("kept no list of the services running", output, StringComparison.Ordinal);
    }

    private static async Task<(InterCatExitCode Code, string Output)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        using var answered = new StringWriter();
        Console.SetOut(answered);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString());
        }
        finally
        {
            Console.SetOut(output);
        }
    }
}
