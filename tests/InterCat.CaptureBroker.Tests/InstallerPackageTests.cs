using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// The Windows Installer package's source (ADR-115): it starts the installed broker only with install-time commands the
/// broker parses, as Local System, pre-creating the root once the files are installed and cleaning up before they are
/// removed - removing recorded captures only when asked, and never while an update removes the version it replaces; and
/// its build ships InterCat's code compiled ahead of time, the broker's with it.
/// </summary>
public sealed partial class InstallerPackageTests
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

        // Restart Manager may find InterCat's files in use but never close the process holding them: a recording broker
        // is refused by cleanup, never ended before cleanup runs.
        XElement restartManager = Assert.Single(package.Elements(Wix + "Property"), property => (string?)property.Attribute("Id") == "MSIRESTARTMANAGERCONTROL");
        Assert.Equal("DisableShutdown", (string?)restartManager.Attribute("Value"));

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

    [Fact(DisplayName = "§12: the package is built from its clients published for x64 Windows, compiled ahead of time, each staging the broker published alike - never the broker's build output over the client's precompiled assemblies")]
    public void ThePackageShipsItsCodePrecompiled()
    {
        // The clients' publish: for one runtime, framework-dependent, compiled ahead of time.
        Match publish = PublishCommand().Match(File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Build-Installer.ps1")));
        Assert.True(publish.Success, "Build-Installer.ps1 publishes nothing.");
        string[] arguments = publish.Groups["arguments"].Value.Replace("`", " ", StringComparison.Ordinal)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("-p:PublishReadyToRun=true", arguments);
        Assert.Equal(("win-x64", "false"), (arguments[Array.IndexOf(arguments, "-r") + 1], arguments[Array.IndexOf(arguments, "--self-contained") + 1]));

        // Each client stages the broker published as it is - for its runtime, precompiled when it is - and that publish.
        XDocument targets = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "InterCat.BrokerStage.targets"));
        XElement staging = Assert.Single(targets.Root!.Elements("Target"), target => (string?)target.Attribute("Name") == "StagePublishedCaptureBroker");
        XElement publishing = Assert.Single(staging.Elements("MSBuild"));
        Assert.Equal("Publish", (string?)publishing.Attribute("Targets"));
        Dictionary<string, string> properties = ((string)publishing.Attribute("Properties")!).Split(';')
            .Select(property => property.Split('=', 2)).ToDictionary(property => property[0], property => property[1], StringComparer.Ordinal);
        Assert.Equal(("$(RuntimeIdentifier)", "$(PublishReadyToRun)", "false"),
            (properties["RuntimeIdentifier"], properties["PublishReadyToRun"], properties["SelfContained"]));
        XElement staged = Assert.Single(staging.Elements("ItemGroup").Elements("_InterCatPublishedBrokerFiles"));
        Assert.Equal(properties["PublishDir"] + "**/*.*", (string?)staged.Attribute("Include"));

        // Built for a runtime, a client stages the broker's build output from that runtime's own folder, where its
        // executable reference was built, never another build's left in the folder above it.
        XElement[] output = [.. targets.Root.Elements("PropertyGroup").Elements("_InterCatBrokerOutput")];
        Assert.Equal(2, output.Length);
        Assert.Equal(("'$(RuntimeIdentifier)' != ''", "$(_InterCatBrokerOutput)$(RuntimeIdentifier)/"),
            ((string?)output[1].Attribute("Condition"), output[1].Value));
    }

    // A `dotnet publish` command, its lines continued by a backtick.
    [GeneratedRegex(@"dotnet publish (?<arguments>(?:[^\r\n`]|`\r?\n)*)")]
    private static partial Regex PublishCommand();

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
