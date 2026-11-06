using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// A process's package in icat processes (ADR-094's addendum): the package its start or rundown named, said to be none where
/// the record named none, and nothing where no record said - kept apart in its JSON as a text, the empty text and null (R3).
/// </summary>
public sealed class ProcessPackageCommandTests
{
    private const string Package = "Microsoft.DesktopAppInstaller_1.29.250.0_x64__8wekyb3d8bbwe";

    [Fact(DisplayName = "R3: icat processes gives a process the package its lifecycle record named, says none where the record named none, and writes no package where no record said; its JSON keeps the three apart")]
    public async Task APackageIsShownAsItsLifecycleRecordNamedIt()
    {
        using var session = new TemporarySession();
        ObservationRowV1 winget = Lifecycle(10, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Program Files\WindowsApps\winget.exe" };
        ObservationRowV1 child = Lifecycle(20, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\child.exe" };
        ObservationRowV1 older = Lifecycle(30, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\older.exe" };
        Publish(session.Store, [winget, child, older], fields:
        [
            Field(winget, SourceField.ProcessPackageFullName, 0) with { Value = null, Text = Package },
            Field(child, SourceField.ProcessPackageFullName, 0),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("processes", session.Path, "--pid", "100");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Package +Microsoft\.DesktopAppInstaller_1\.29\.250\.0_x64__8wekyb3d8bbwe\r?$", output);
        (code, output) = await Run("processes", session.Path, "--pid", "200");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Package +none: its lifecycle record names no package it ran in\r?$", output);
        (code, output) = await Run("processes", session.Path, "--pid", "300");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.DoesNotMatch(@"(?m)^  Package ", output);

        (code, output) = await Run("processes", session.Path, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument listed = JsonDocument.Parse(output);
        Dictionary<int, JsonElement> packages = listed.RootElement.GetProperty("instances").EnumerateArray().ToDictionary(
            static item => item.GetProperty("process").GetProperty("processId").GetInt32(),
            static item => item.GetProperty("process").GetProperty("packageFullName"));
        Assert.Equal(Package, packages[100].GetString());
        Assert.Equal(string.Empty, packages[200].GetString());
        Assert.Equal(JsonValueKind.Null, packages[300].ValueKind);
    }

    [Fact(DisplayName = "R3: icat metric --group-by package ranks each package's records, names the processes in no package as a group of their own, and states those whose records said neither")]
    public async Task MetricGroupsByPackage()
    {
        using var session = new TemporarySession();
        ObservationRowV1 winget = Lifecycle(10, ObservationKind.Create, 100, 1);
        ObservationRowV1 child = Lifecycle(12, ObservationKind.Create, 300, 3);
        ObservationRowV1 older = Lifecycle(13, ObservationKind.Create, 400, 4);
        Publish(session.Store,
        [
            winget, child, older,
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 5).Between("127.0.0.1:50000", "10.0.0.5:443"),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 9, 400, 9).Between("127.0.0.1:50002", "10.0.0.6:443"),
        ], fields:
        [
            Field(winget, SourceField.ProcessPackageFullName, 0) with { Value = null, Text = Package },
            Field(child, SourceField.ProcessPackageFullName, 0),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("metric", session.Path, "--metric", "observations", "--group-by", "package");
        Assert.Equal(InterCatExitCode.Success, code);
        string text = output.ReplaceLineEndings("\n");
        Assert.Contains("\nOBSERVATIONS BY PACKAGE\n", text, StringComparison.Ordinal);
        Assert.Contains("  By package (", text, StringComparison.Ordinal);
        Assert.Matches(@"\n\s+1\s+Microsoft\.DesktopAppInstaller_1\.29\.250\.0_x64__8wekyb3d8bbwe\s+2\s", text);
        Assert.Matches(@"\n\s+2\s+In no package\s+1\s", text);
        Assert.Contains("  Not attributed to a package, by reason (never a peer, never ranked):", text, StringComparison.Ordinal);
        Assert.Contains("the process is known, but no lifecycle record named the package it ran in, or none", text, StringComparison.Ordinal);

        (code, output) = await Run("metric", session.Path, "--metric", "observations", "--group-by", "package", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement grouping = json.RootElement.GetProperty("grouping");
        Assert.Equal("Package", grouping.GetProperty("grouping").GetString());
        Assert.Equal(
            [("Package", Package, 2L), ("Package", string.Empty, 1L)],
            grouping.GetProperty("groups").EnumerateArray().Select(group => (
                group.GetProperty("kind").GetString(), group.GetProperty("package").GetString(), group.GetProperty("value").GetInt64())));
        Assert.Equal(
            [(nameof(ProcessBindingReason.PackageUnknown), 2L)],
            grouping.GetProperty("unattributed").EnumerateArray().Select(group => (
                group.GetProperty("reason").GetString(), group.GetProperty("value").GetInt64())));
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
