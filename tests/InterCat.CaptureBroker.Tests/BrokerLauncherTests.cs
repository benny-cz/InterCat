using System.Runtime.Versioning;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

[SupportedOSPlatform("windows")]
public sealed class BrokerLauncherTests
{
    [Fact(DisplayName = "R16: the broker accepts exactly the launch arguments the client builds")]
    public void ClientLaunchArgumentsParseToTheSameOwnerAndInstance()
    {
        var owner = new BrokerOwnerIdentity("S-1-5-21-11-22-33-1001", 0x1_0000_3E7A);
        Guid instance = Guid.NewGuid();
        var target = new BrokerLaunchTarget(@"C:\Program Files\InterCat\InterCat.CaptureBroker.exe", ["serve"]);

        IReadOnlyList<string> arguments = WindowsBrokerLauncher.BuildArguments(
            target, owner, instance, TimeSpan.FromMinutes(2));
        BrokerLaunchOptions? parsed = BrokerLaunchOptions.Parse(arguments, out BrokerLaunchParseError? error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.True(owner.Matches(parsed.Owner));
        Assert.Equal(instance, parsed.ServerInstanceId);
        Assert.Equal(TimeSpan.FromMinutes(2), parsed.IdleExitAfter);
        Assert.Equal(BrokerHostSettings.DefaultIdleExit, BrokerLaunchOptions.Parse(
            WindowsBrokerLauncher.BuildArguments(target, owner, instance, null), out _)!.IdleExitAfter);
    }

    [Fact]
    public async Task AMissingBrokerIsReportedAsNotInstalledWithoutPrompting()
    {
        var target = new BrokerLaunchTarget(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}", BrokerLaunchTarget.ExecutableName),
            ["serve"]);

        BrokerLaunchException failure = await Assert.ThrowsAsync<BrokerLaunchException>(() =>
            WindowsBrokerLauncher.LaunchAsync(target));

        Assert.Equal(BrokerLaunchFailure.BrokerNotInstalled, failure.Failure);
        Assert.Contains("saved sessions", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDocumentedBrokerExitHasItsOwnExplanation()
    {
        int[] exitCodes = [2, 3, 4, 99, 6, 70];
        string[] explanations = [.. exitCodes.Select(WindowsBrokerLauncher.DescribeExit)];

        Assert.Equal(explanations.Length, explanations.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("%ProgramData%\\InterCat", explanations[1], StringComparison.Ordinal);
        Assert.Contains("do not delete", explanations[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("99", explanations[3], StringComparison.Ordinal);
    }
}
