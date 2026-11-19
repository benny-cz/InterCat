using System.Xml.Linq;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// The Windows Installer package's source (ADR-115): it starts the installed broker only with install-time commands the
/// broker parses, as Local System, pre-creating the root once the files are installed and cleaning up before they are
/// removed - removing recorded captures only when asked, and never while an update removes the version it replaces.
/// </summary>
public sealed class InstallerPackageTests
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    [Fact(DisplayName = "§20.5: the package starts the broker only with install-time commands it parses, as Local System, pre-creating the root once the files are in and cleaning up before they go, removing captures only when asked outside an update")]
    public void ThePackageStartsTheBrokersOwnCommands()
    {
        XDocument source = XDocument.Load(Path.Combine(RepositoryRoot(), "installer", "InterCat.wxs"));
        XElement package = Assert.Single(source.Root!.Elements(Wix + "Package"));
        Assert.Equal("perMachine", (string?)package.Attribute("Scope"));
        Assert.NotNull(package.Element(Wix + "MajorUpgrade"));

        // The command people type is the CLI's own executable, installed a second time as icat.exe beside it.
        XElement icat = Assert.Single(package.Descendants(Wix + "File"), file => (string?)file.Attribute("Id") == "IcatExe");
        Assert.Equal(("icat.exe", @"$(var.Stage)\InterCat.Cli.exe"), ((string?)icat.Attribute("Name"), (string?)icat.Attribute("Source")));

        // Each action runs the installed broker as Local System, its command line - the user's SID put in, as Windows
        // Installer formats it when the script is written - one the broker's own parser accepts.
        var actions = package.Elements(Wix + "CustomAction").Select(action =>
        {
            Assert.Equal(("BrokerExe", "no"), ((string?)action.Attribute("FileRef"), (string?)action.Attribute("Impersonate")));
            string[] command = ((string)action.Attribute("ExeCommand")!).Replace("[UserSID]", Sid, StringComparison.Ordinal).Split(' ');
            BrokerInstallOptions? parsed = BrokerInstallOptions.Parse(command, out string? error);
            Assert.True(parsed is not null, error);
            Assert.Equal(Sid, parsed.OwnerSid);
            return (Id: (string)action.Attribute("Id")!, Execute: (string?)action.Attribute("Execute"), Return: (string?)action.Attribute("Return"),
                parsed.Command, parsed.RemoveEvidence);
        }).OrderBy(action => action.Id, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            [
                ("CleanUp", "deferred", "check", BrokerInstallCommand.Cleanup, false),
                ("CleanUpRemovingEvidence", "deferred", "check", BrokerInstallCommand.Cleanup, true),
                ("ProvisionRoot", "deferred", "check", BrokerInstallCommand.ProvisionRoot, false),
                ("RollBackProvisionRoot", "rollback", "ignore", BrokerInstallCommand.Cleanup, false),
            ],
            actions);

        // When each runs: the root provisioned once the files are in, a failed installation's rollback cleaning up a root
        // left holding nothing; cleanup before the files go, removing captures only when asked and never in an update.
        Dictionary<string, (string? Before, string? After, string? Condition)> scheduled = package.Elements(Wix + "InstallExecuteSequence")
            .Elements(Wix + "Custom").ToDictionary(
                custom => (string)custom.Attribute("Action")!,
                custom => ((string?)custom.Attribute("Before"), (string?)custom.Attribute("After"), (string?)custom.Attribute("Condition")));
        Assert.Equal(actions.Select(action => action.Id), scheduled.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((null, "InstallFiles", "NOT REMOVE~=\"ALL\""), scheduled["RollBackProvisionRoot"]);
        Assert.Equal((null, "RollBackProvisionRoot", "NOT REMOVE~=\"ALL\""), scheduled["ProvisionRoot"]);
        Assert.Equal(("RemoveFiles", null, "REMOVE~=\"ALL\" AND NOT (INTERCAT_REMOVE_EVIDENCE=\"1\" AND NOT UPGRADINGPRODUCTCODE)"),
            scheduled["CleanUp"]);
        Assert.Equal(("RemoveFiles", null, "REMOVE~=\"ALL\" AND INTERCAT_REMOVE_EVIDENCE=\"1\" AND NOT UPGRADINGPRODUCTCODE"),
            scheduled["CleanUpRemovingEvidence"]);
    }

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
