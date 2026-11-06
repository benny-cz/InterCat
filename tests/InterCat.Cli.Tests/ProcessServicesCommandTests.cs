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
