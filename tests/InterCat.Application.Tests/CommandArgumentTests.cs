using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// A value in an icat command the window or icat cites (R18): put so cmd and PowerShell take it as one argument, a name or
/// a path as it is, one a shell would split or read in double quotes.
/// </summary>
public sealed class CommandArgumentTests
{
    [Theory(DisplayName = "R18: a cited command's value is taken whole, in double quotes where a shell would split or read it")]
    [InlineData("svchost.exe", "svchost.exe")]
    [InlineData(@"C:\Apps\updater.exe", @"C:\Apps\updater.exe")]
    [InlineData("/usr/bin/ssh-agent", "/usr/bin/ssh-agent")]
    [InlineData("g++.exe", "g++.exe")]
    [InlineData("naïve.exe", "naïve.exe")]
    [InlineData("git.exe (cmd)", "\"git.exe (cmd)\"")]
    [InlineData("Executable not witnessed", "\"Executable not witnessed\"")]
    [InlineData(@"C:\Program Files\App\app.exe", @"""C:\Program Files\App\app.exe""")]
    [InlineData("a&b.exe", "\"a&b.exe\"")]
    [InlineData("$env.exe", "\"$env.exe\"")]
    [InlineData("a;b.exe", "\"a;b.exe\"")]
    [InlineData("", "\"\"")]
    public void ACitedValueIsTakenWhole(string value, string argument) => Assert.Equal(argument, CommandArgument.Of(value));

    [Fact(DisplayName = "R18: an enumeration's value is cited as a person types it, each word after the first behind a hyphen")]
    public void AnEnumerationIsCitedAsTyped()
    {
        Assert.Equal("direct-only", CommandArgument.Typed(EvidencePolicy.DirectOnly));
        Assert.Equal("all-including-conflicting", CommandArgument.Typed(EvidencePolicy.AllIncludingConflicting));
        Assert.Equal("bytes-sent", CommandArgument.Typed(Metric.BytesSent));
        Assert.Equal("tcp", CommandArgument.Typed(Mechanism.Tcp));
    }
}
