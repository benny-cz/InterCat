using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// What a view of a session discloses, which its exports repeat as their first caveat (R18): what its evidence holds and
/// leaves out, never how the window reaches it.
/// </summary>
public sealed class OverviewDisclosureTests
{
    [Fact(DisplayName = "R18: a session's disclosure, ALPC's, a redacted package's and the demo's say what the evidence holds, never a key of the window")]
    public void ADisclosureNamesNoKey()
    {
        string[] disclosures =
        [
            OverviewWorkspace.SessionDisclosure,
            OverviewWorkspace.LinkedCallsDisclosure,
            OverviewWorkspace.RedactedDisclosure,
            DemoInvestigation.Disclosure,
        ];
        Assert.All(disclosures, disclosure =>
        {
            Assert.DoesNotContain("(E)", disclosure, StringComparison.Ordinal);
            Assert.DoesNotContain("Enter", disclosure, StringComparison.Ordinal);
            Assert.DoesNotContain("until requested", disclosure, StringComparison.Ordinal);
        });

        // Each still says what the graph draws and what the timeline holds.
        Assert.All(disclosures[..2], disclosure =>
        {
            Assert.StartsWith("The graph shows admitted paired TCP", disclosure, StringComparison.Ordinal);
            Assert.Contains("The timeline includes every observed row.", disclosure, StringComparison.Ordinal);
            Assert.EndsWith("TCP and UDP records are completed transfers with no operation rung.", disclosure,
                StringComparison.Ordinal);
        });
    }
}
