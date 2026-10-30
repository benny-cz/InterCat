using System.Text.RegularExpressions;
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
            ["InterCat.CaptureBroker.Protocol"] = ["InterCat.Domain"],
            ["InterCat.CaptureBroker"] = ["InterCat.Domain", "InterCat.CaptureBroker.Protocol", "InterCat.Storage", "InterCat.Capture.Windows", "InterCat.Capture.Recording"],
            ["InterCat.Desktop"] = ["InterCat.Domain", "InterCat.Application", "InterCat.Storage", "InterCat.Capture.Journal", "InterCat.CaptureBroker.Protocol"],
            ["InterCat.Cli"] = ["InterCat.Benchmarks", "InterCat.Domain", "InterCat.Storage", "InterCat.Analysis", "InterCat.Application", "InterCat.Capture.Windows", "InterCat.Capture.Journal", "InterCat.CaptureBroker.Protocol"],
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

    [Fact(DisplayName = "R19: the plan's solution layout names every project the solution holds, each where it is, and none it does not")]
    public void TheLayoutNamesEveryProject()
    {
        string root = FindRepositoryRoot();
        string plan = File.ReadAllText(Path.Combine(root, "docs", "design", "INTERCAT-IMPLEMENTATION-PLAN.md"));
        int section = plan.IndexOf("### 26.1 Suggested solution layout", StringComparison.Ordinal);
        Assert.True(section >= 0, "§26.1 no longer lays out the solution.");
        int start = plan.IndexOf("```text", section, StringComparison.Ordinal) + "```text".Length;
        int end = plan.IndexOf("```", start, StringComparison.Ordinal);

        // A folder of the repository on a line of its own, then its projects, each indented two spaces; a line that goes on
        // describing one is indented further.
        var laidOut = new List<string>();
        string? folder = null;
        foreach (string line in plan[start..end].Split('\n'))
        {
            if (Regex.Match(line, @"^(src|tools|tests)/\s*$") is { Success: true } top)
            {
                folder = top.Groups[1].Value;
            }
            else if (Regex.Match(line, @"^  (InterCat\.[A-Za-z.]+)/") is { Success: true } project)
            {
                laidOut.Add($"{folder}/{project.Groups[1].Value}");
            }
        }

        string[] solution = [.. XDocument.Load(Path.Combine(root, "InterCat.slnx")).Descendants("Project")
            .Select(project => Path.GetDirectoryName(project.Attribute("Path")!.Value)!.Replace('\\', '/'))];
        Assert.Equal(solution.Order(StringComparer.Ordinal), laidOut.Order(StringComparer.Ordinal));
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
