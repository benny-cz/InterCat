using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace InterCat.Architecture.Tests;

/// <summary>
/// Prohibitions a fitness function can hold where no runtime test reaches: what the elevated broker can reach at all
/// (P18), and the intrusive means of resolving a peer no product code uses (P20).
/// </summary>
public sealed partial class ProhibitionFitnessTests
{
    /// <summary>What the elevated broker is built from: everything else is out of its reach.</summary>
    private static readonly string[] BrokerReaches =
    [
        "InterCat.Capture.Recording", "InterCat.Capture.Windows", "InterCat.CaptureBroker", "InterCat.CaptureBroker.Protocol",
        "InterCat.Domain", "InterCat.Storage",
    ];

    /// <summary>The importer and live follower, the content views and exports, the analysis, the command line and the window.</summary>
    private static readonly string[] OutOfTheBrokersReach =
    [
        "InterCat.Analysis", "InterCat.Application", "InterCat.Capture.Journal", "InterCat.Cli", "InterCat.Desktop",
    ];

    /// <summary>The code the broker runs that is its own: the broker, and the recorder it drives.</summary>
    private static readonly string[] BrokersOwnCode = ["InterCat.CaptureBroker", "InterCat.Capture.Recording"];

    /// <summary>Calls that read another process's memory, copy its handles, or list every handle on the machine.</summary>
    private static readonly string[] Intrusive =
    [
        "ReadProcessMemory", "NtReadVirtualMemory", "DuplicateHandle", "NtDuplicateObject", "NtQueryObject",
        "NtQuerySystemInformation", "PROCESS_VM_READ", "PROCESS_DUP_HANDLE",
    ];

    [Fact(DisplayName = "P18: the elevated broker can reach no importer, content view or UI, and its own code opens no ETL file")]
    public void TheBrokerReachesNoImporterContentViewOrUi()
    {
        string root = RepositoryRoot();

        // Its project closure is exactly the capture path: the importer and live follower (Capture.Journal), the content
        // views, redaction and export (Application), the analysis, and the window are each out of its reach.
        SortedSet<string> reached = Closure(root, "InterCat.CaptureBroker");
        Assert.Equal(BrokerReaches, reached);
        foreach (string outOfReach in OutOfTheBrokersReach)
        {
            Assert.DoesNotContain(outOfReach, reached);
        }

        // Nothing it is built from draws a window.
        foreach (string project in reached)
        {
            Assert.DoesNotContain(PackagesOf(root, project), package => package.StartsWith("Avalonia", StringComparison.Ordinal));
        }

        // In all it reaches, one type reads an ETL file - the replay the importer drives - and neither the broker's own
        // code nor the recorder it runs names it.
        Assert.Equal(["src/InterCat.Capture.Windows/Etw/EtlAdmissionReplay.cs"],
            reached.SelectMany(project => Sources(root, project))
                .Where(source => File.ReadAllText(source).Contains("TraceEventSourceType.FileOnly", StringComparison.Ordinal))
                .Select(source => Relative(root, source)));
        foreach (string source in BrokersOwnCode.SelectMany(project => Sources(root, project)))
        {
            Assert.DoesNotContain("EtlAdmissionReplay", File.ReadAllText(source), StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "P20: no product code reads another process's memory, duplicates its handles, or opens a pipe but the broker's")]
    public void NoProductCodeResolvesAPeerIntrusively()
    {
        string root = RepositoryRoot();

        // The workloads are fixtures that drive their own pipe and sockets; everything else ships.
        string[] product = [.. Directory.EnumerateDirectories(Path.Combine(root, "src"))
            .Select(Path.GetFileName)
            .Where(project => project is not null && project != "InterCat.TestWorkloads"
                && File.Exists(Path.Combine(root, "src", project, project + ".csproj")))
            .Select(project => project!)];
        Assert.Contains("InterCat.Capture.Windows", product);
        string[] sources = [.. product.SelectMany(project => Sources(root, project))];

        // No call that reads another process's memory, copies its handles or lists every handle on the machine.
        foreach (string source in sources)
        {
            string text = File.ReadAllText(source);
            foreach (string intrusive in Intrusive)
            {
                Assert.DoesNotContain(intrusive, text, StringComparison.Ordinal);
            }
        }

        // A process is opened only to hold its ID while a capture names it: to wait on it and read its basic facts.
        string[] opens = [.. sources.Where(source => OpenProcessCall().IsMatch(File.ReadAllText(source))).Select(source => Relative(root, source))];
        Assert.Equal(["src/InterCat.Capture.Windows/Etw/ProcessHolds.cs"], opens);
        string holds = File.ReadAllText(Path.Combine(root, opens[0]));
        Assert.Equal(["OpenProcess(Synchronize | QueryLimitedInformation, false, processId)"],
            OpenProcessCall().Matches(holds).Select(match => match.Value));
        Assert.Contains("const uint Synchronize = 0x0010_0000;", holds, StringComparison.Ordinal);
        Assert.Contains("const uint QueryLimitedInformation = 0x1000;", holds, StringComparison.Ordinal);

        // The one pipe a product connects to is the broker's, and only after checking the server is the broker it launched.
        Assert.Equal(["src/InterCat.CaptureBroker.Protocol/WindowsBrokerPipeClient.cs"],
            sources.Where(source => File.ReadAllText(source).Contains("new NamedPipeClientStream(", StringComparison.Ordinal))
                .Select(source => Relative(root, source)));
        Assert.Contains("serverProcessId != (uint)expectedServerProcessId",
            File.ReadAllText(Path.Combine(root, "src", "InterCat.CaptureBroker.Protocol", "WindowsBrokerPipeClient.cs")),
            StringComparison.Ordinal);
    }

    /// <summary>A project and every project it references, directly or through another.</summary>
    private static SortedSet<string> Closure(string root, string project)
    {
        var reached = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([project]);
        while (pending.TryPop(out string? next))
        {
            if (!reached.Add(next))
            {
                continue;
            }

            foreach (XElement reference in XDocument.Load(ProjectFile(root, next)).Descendants("ProjectReference"))
            {
                pending.Push(Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value));
            }
        }

        return reached;
    }

    private static IEnumerable<string> PackagesOf(string root, string project) =>
        XDocument.Load(ProjectFile(root, project)).Descendants("PackageReference")
            .Select(reference => reference.Attribute("Include")!.Value);

    private static string ProjectFile(string root, string project) => Path.Combine(root, "src", project, project + ".csproj");

    private static IEnumerable<string> Sources(string root, string project) =>
        Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(source => !source.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !source.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>A call of OpenProcess, not its declaration: the word, then its arguments up to the closing parenthesis.</summary>
    [GeneratedRegex(@"(?<!static partial SafeProcessHandle )OpenProcess\([^)]*\)")]
    private static partial Regex OpenProcessCall();

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
