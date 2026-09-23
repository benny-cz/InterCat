using System.Xml.Linq;
using Xunit;

namespace InterCat.Architecture.Tests;

public sealed class DependencyDirectionTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["InterCat.Domain"] = [],
            ["InterCat.Storage"] = ["InterCat.Domain"],
            ["InterCat.Analysis"] = ["InterCat.Domain", "InterCat.Storage"],
            ["InterCat.Benchmarks"] = ["InterCat.Domain"],
            ["InterCat.Application"] = ["InterCat.Domain", "InterCat.Storage", "InterCat.Analysis"],
            ["InterCat.Capture.Windows"] = ["InterCat.Domain"],
            ["InterCat.Capture.Recording"] = ["InterCat.Domain", "InterCat.Storage", "InterCat.Capture.Windows"],
            ["InterCat.Capture.Journal"] = ["InterCat.Domain", "InterCat.Storage", "InterCat.Capture.Windows", "InterCat.Capture.Recording"],
            ["InterCat.CaptureBroker"] = ["InterCat.Domain", "InterCat.Storage", "InterCat.Capture.Windows", "InterCat.Capture.Recording"],
            ["InterCat.Desktop"] = ["InterCat.Domain", "InterCat.Application"],
            ["InterCat.Cli"] = ["InterCat.Benchmarks", "InterCat.Domain", "InterCat.Storage", "InterCat.Analysis", "InterCat.Application", "InterCat.Capture.Windows", "InterCat.Capture.Journal"],
            ["InterCat.TestWorkloads"] = ["InterCat.Domain"],
        };

    [Fact(DisplayName = "R19: project references follow the inward dependency map")]
    public void ProjectReferencesFollowDependencyMap()
    {
        string root = FindRepositoryRoot();
        foreach ((string projectName, string[] allowed) in AllowedReferences)
        {
            string projectPath = Path.Combine(root, "src", projectName, $"{projectName}.csproj");
            XDocument project = XDocument.Load(projectPath);
            string[] actual = project.Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(allowed.Order(StringComparer.Ordinal), actual);
        }
    }

    [Fact(DisplayName = "R19: platform packages stay in their owning adapters")]
    public void PlatformPackagesStayInOwningProjects()
    {
        string root = FindRepositoryRoot();
        foreach (string projectPath in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            string projectName = Path.GetFileNameWithoutExtension(projectPath);
            XDocument project = XDocument.Load(projectPath);
            string[] packages = project.Descendants("PackageReference")
                .Select(reference => reference.Attribute("Include")!.Value)
                .ToArray();

            Assert.DoesNotContain(packages, package =>
                package.Equals("Microsoft.Diagnostics.Tracing.TraceEvent", StringComparison.Ordinal)
                && projectName != "InterCat.Capture.Windows");
            Assert.DoesNotContain(packages, package =>
                package.StartsWith("Avalonia", StringComparison.Ordinal)
                && projectName != "InterCat.Desktop");
        }
    }

    [Fact(DisplayName = "R19: portable core source has no Windows or UI imports")]
    public void PortableCoreHasNoPlatformImports()
    {
        string root = FindRepositoryRoot();
        string[] portableProjects = ["InterCat.Domain", "InterCat.Storage", "InterCat.Analysis", "InterCat.Benchmarks"];
        string[] prohibited = ["using Avalonia", "using Microsoft.Diagnostics.Tracing", "using System.Windows"];
        foreach (string project in portableProjects)
        {
            foreach (string sourcePath in Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(sourcePath);
                foreach (string prohibitedImport in prohibited)
                {
                    Assert.DoesNotContain(prohibitedImport, source, StringComparison.Ordinal);
                }
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
