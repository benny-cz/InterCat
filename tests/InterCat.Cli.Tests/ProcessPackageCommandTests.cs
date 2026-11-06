using System.Text.Json;
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
