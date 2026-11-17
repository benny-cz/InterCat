using System.Text.Json;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The probes' committed reports of one kind under bench/results, each with the build it was measured on, so a test holds
/// every measured build's constants to the reports that measured them rather than one build's alone.
/// </summary>
internal static class MeasuredReports
{
    /// <summary>The prefix of the version a report's machine line names: "24 logical processors · Microsoft Windows NT 10.0.26220.0".</summary>
    private const string WindowsNt = "Microsoft Windows NT ";

    /// <summary>Each report.json whose directory under bench/results begins with <paramref name="prefix"/>, by name.</summary>
    public static IReadOnlyList<string> Files(string prefix) =>
    [
        .. Directory.GetDirectories(Path.Combine(RepositoryRoot(), "bench", "results"), prefix + "*")
            .Select(static directory => Path.Combine(directory, "report.json"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The build a report was measured on: its own build, or - from a probe that wrote none - the version its machine line
    /// names, on the x64 development machine every committed report was measured on.
    /// </summary>
    public static string BuildOf(JsonElement root)
    {
        if (root.TryGetProperty("build", out JsonElement build))
        {
            return build.GetString()!;
        }

        string machine = root.GetProperty("machine").GetString()!;
        int at = machine.IndexOf(WindowsNt, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The report's machine line names no version: {machine}");
        return machine[(at + WindowsNt.Length)..] + "-x64";
    }

    /// <summary>The builds the reports of <paramref name="prefix"/> were measured on, each once, in order.</summary>
    public static IReadOnlyList<string> Builds(string prefix) =>
    [
        .. Files(prefix).Select(static file =>
        {
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(file));
            return BuildOf(report.RootElement);
        }).Distinct().Order(StringComparer.Ordinal),
    ];

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
