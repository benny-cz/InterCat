using System.Globalization;
using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Sections two processes share (ADR-089): FX-SECT-001's named sections, one backed by the paging file and one by a file,
/// measured under a private system logger collecting the object manager's handle group and the kernel's file view flag - each
/// process's handles to a section at its object's one address, named at the close, and every view each process mapped and
/// unmapped, raised in it with its size, each section's views naming one value no handle names.
/// </summary>
public sealed class SectionLeadTests
{
    [Fact(DisplayName = "R22: FX-SECT-001's handle records join each process's handles to a section by its object's address - each creation named by nothing but its close, typed Section - and every view each process mapped and unmapped is a record of that process with its size, each section's views naming one value in both processes, while one base held both sections' views and no record names a byte moved through one")]
    public void SectionsAreRecordedByTheirHandlesAndViews()
    {
        Row[] ledger = Ledger();
        Truth[] truth = [.. TruthLog("truth-creator.jsonl"), .. TruthLog("truth-opener.jsonl")];
        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "scenario.json")));
        long bytes = scenario.RootElement.GetProperty("bytes").GetInt64();

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string section, string status, string misc) in new[] { ("memory", "section", "0xC4"), ("file", "file section", "0x04") })
        {
            // Each process's handle was created and closed once, both processes' at the section's one object, typed Section;
            // only the close names it.
            Row[] handles = [.. ledger.Where(row => row.Section == section && row.Kind is "Create" or "Close")];
            Assert.Single(handles.Select(static row => row.Object).Distinct());
            Assert.All(handles, row => Assert.Equal("Section", row.Type));
            Assert.Equal(["creator Close named", "creator Create ", "opener Close named", "opener Create "],
                handles.Select(static row => $"{row.Role} {row.Kind} {row.Named}").Order(StringComparer.Ordinal));

            // Every view each process mapped and unmapped, as many as its truth logged, of the size it logged; all of one
            // section's views name one value, in both processes, and the section's kind shows in their extra information.
            Row[] views = [.. ledger.Where(row => row.Section == section && row.Kind is "MapView" or "UnmapView")];
            foreach (string role in new[] { "creator", "opener" })
            {
                Assert.Equal(truth.Count(record => record.Role == role && record.Status == status && record.Kind == "ViewMapped"),
                    views.Count(row => row.Role == role && row.Kind == "MapView"));
                Assert.Equal(truth.Count(record => record.Role == role && record.Status == status && record.Kind == "ViewUnmapped"),
                    views.Count(row => row.Role == role && row.Kind == "UnmapView"));
            }

            Assert.All(views, row => Assert.Equal((bytes, 0L), (row.Bytes, row.Offset)));
            values[section] = Assert.Single(views.Select(static row => row.ViewValue).Distinct());
            Assert.Equal(misc, Assert.Single(views.Select(static row => row.MiscHigh).Distinct()));
        }

        // Each section's views name a value of their own, which is no number of an object a handle names: views join each
        // other across processes, never to their section's handles or its name.
        Assert.NotEqual(values["memory"], values["file"]);

        // The opener mapped every view of both sections at one base, so a view's base is never a section's identity (R22).
        Assert.Single(ledger.Where(static row => row.Role == "opener" && row.Kind == "MapView").Select(static row => row.View).Distinct());

        // The opener read the creator's bytes through every view, and no record says a byte moved: the ledger holds the
        // sections' handles and their views' mappings, nothing else.
        Assert.All(truth.Where(static record => record is { Role: "opener", Kind: "ViewUnmapped" }), record => Assert.Equal(bytes, record.CompletedBytes));
        Assert.Equal(["Close", "Create", "MapView", "UnmapView"], ledger.Select(static row => row.Kind).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "§4.2: a view's mapping and unmapping are raised at version 3, which no layout the machine registers describes - 44 bytes, a page-aligned field before the process at their end - and in three runs of views of 4 KiB to 1 MiB, nothing lost, every truth view was recorded with its size, each section's views naming one value no section object and no other record names")]
    public void ViewsAreRaisedAtAnUnregisteredVersion()
    {
        // The inspection: the file I/O class registers a view's mapping and unmapping at version 2 alone, five fields.
        string results = Path.Combine(RepositoryRoot(), "bench", "results");
        using (JsonDocument inspection = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Assert.Single(Directory.GetDirectories(results, "section-provider-inspection-*")), "report.json"))))
        {
            JsonElement fileIo = inspection.RootElement.GetProperty("classes").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString()!.StartsWith("File I/O", StringComparison.Ordinal));
            JsonElement[] views = [.. fileIo.GetProperty("events").EnumerateArray().Where(static item => item.GetProperty("opcode").GetInt32() is 37 or 38)];
            Assert.Equal(2, views.Length);
            Assert.All(views, view =>
            {
                Assert.Equal(2, view.GetProperty("version").GetInt32());
                Assert.Equal(["ViewBase", "FileObject", "MiscInfo", "ViewSize", "ProcessId"],
                    view.GetProperty("fields").EnumerateArray().Select(static field => field.GetProperty("name").GetString()));
            });
        }

        // Three runs, of 64 KiB views four times, 4 KiB sixteen and 1 MiB twice, nothing lost.
        string[] series = [.. Directory.GetDirectories(results, "section-measurement-*").Select(directory => Path.Combine(directory, "report.json"))
            .Order(StringComparer.Ordinal)];
        Assert.Equal(3, series.Length);
        var sizes = new List<int>();
        foreach (string file in series)
        {
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = report.RootElement;
            Assert.Equal(0, root.GetProperty("session").GetProperty("eventsLost").GetInt32());
            sizes.Add(root.GetProperty("workload").GetProperty("viewBytes").GetInt32());

            // Every mapping and unmapping the session delivered was raised at version 3, 44 bytes with eight between the size
            // and the process, a page-aligned offset, and the process the one that raised it.
            JsonElement layout = root.GetProperty("viewLayout");
            int[] Values(string property) => [.. layout.GetProperty(property).EnumerateArray().Select(static value => value.GetInt32())];
            Assert.Equal([3], Values("versions"));
            Assert.Equal([44], Values("payloadBytes"));
            Assert.Equal([8], Values("betweenSizeAndProcessBytes"));
            Assert.True(layout.GetProperty("betweenPageAligned").GetBoolean() && layout.GetProperty("processIsTheRaiser").GetBoolean(), file);

            // Every truth view recorded, of its size and unmapped at its base; each section's views one value in both
            // processes, no section object's address, named by no other record of the workload's.
            Assert.All(root.GetProperty("views").EnumerateArray(), view =>
            {
                Assert.Equal(view.GetProperty("truthViews").GetInt32(), view.GetProperty("recorded").GetInt32());
                Assert.True(view.GetProperty("sizesAsLogged").GetBoolean() && view.GetProperty("unmappedAtTheirBase").GetBoolean());
                Assert.Equal((1, true, false, 0), (view.GetProperty("viewValues").GetInt32(), view.GetProperty("viewValueInBothProcesses").GetBoolean(),
                    view.GetProperty("viewValueIsASectionObject").GetBoolean(), view.GetProperty("otherRecordsNamingIt").GetInt32()));
                Assert.Equal(view.GetProperty("section").GetString() == "memory" ? "0xC4" : "0x04",
                    Assert.Single(view.GetProperty("miscInfoHighBytes").EnumerateArray()).GetString());
            });

            // Each section one object, typed Section, in both processes.
            Assert.All(root.GetProperty("sections").EnumerateArray(), section => Assert.Equal((1, "Section"),
                (section.GetProperty("objects").GetInt32(), Assert.Single(section.GetProperty("typeNames").EnumerateArray()).GetString())));
        }

        Assert.Equal([4_096, 65_536, 1_048_576], sizes.Order());
    }

    [Fact(DisplayName = "§12: collecting the kernel's file view flag is measured in four series of pairs over a view-heavy workload - Low in the processor time it adds over trials of four seconds, the workload's own time more and its throughput past §12's target in three series of four, nothing lost - and the machine's other processes map views whatever the workload")]
    public void TheFileViewFlagsCollectionCostIsMeasured()
    {
        string results = Path.Combine(RepositoryRoot(), "bench", "results");
        string[] series = [.. Directory.EnumerateDirectories(results, "section-view-impact-*")
            .Select(directory => Path.Combine(directory, "impact.json")).Where(File.Exists).Order(StringComparer.Ordinal)];
        Assert.Equal(4, series.Length);
        var regressions = new List<bool>();
        foreach ((string file, int rounds) in series.Zip([20_000, 20_000, 100_000, 100_000]))
        {
            // Nine pairs of FX-SECT-001's opener mapping and unmapping each section's 4 KiB view that many times, nothing lost.
            using JsonDocument impact = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = impact.RootElement;
            Assert.Equal((9, 0L), (root.GetProperty("pairs").GetArrayLength(), root.GetProperty("eventsLost").GetInt64()));
            Assert.Equal(($"section-share --truth {{truth}} --rounds {rounds.ToString(CultureInfo.InvariantCulture)} --view-bytes 4096", "VAMap (0x8000)"),
                (root.GetProperty("workload").GetString(), root.GetProperty("flag").GetString()));

            // The machine's busy share is within the processor target in every series; the workload's own processor time grew, as
            // a view's records are raised in the thread that maps and unmaps it.
            Assert.True(root.GetProperty("cpuTargetMet").GetBoolean(), file);
            Assert.True(root.GetProperty("medianWorkloadCpuIncreasePercent").GetDouble() > 0, file);
            regressions.Add(!root.GetProperty("throughputTargetMet").GetBoolean());

            // The workload raised tens of thousands of view records a second, the machine's other processes a few in the second
            // before it.
            double rate = root.GetProperty("medianViewEventsPerSecond").GetDouble();
            Assert.InRange(rate, 50_000, 200_000);
            Assert.InRange(root.GetProperty("medianViewEventsInTheSecondBefore").GetDouble(), 0, rate / 10);

            // Over trials of about four seconds the processor time the session added, the workload's and the consumer's, is
            // Low in the median; over trials of a second the consumer's settling weighs on a second's work.
            if (rounds == 100_000)
            {
                double attributed = root.GetProperty("medianAttributedImpactPercentagePoints").GetDouble();
                Assert.Equal(OverheadClass.Low, OverheadClassCalculator.Classify(attributed));
                Assert.Equal(nameof(OverheadClass.Low), root.GetProperty("attributedOverhead").GetString());
                Assert.InRange(root.GetProperty("medianConsumerMicrosecondsPerEvent").GetDouble(), 0, 2);
            }
        }

        // The workload's throughput fell past §12's 5% target in three series of four.
        Assert.Equal(3, regressions.Count(static regressed => regressed));
    }

    private sealed record Row(string Kind, string Role, string Section, string Object, string Type, string Named, string View, long Bytes, long Offset,
        string ViewValue, string MiscHigh);

    private sealed record Truth(string Role, string Kind, string? Status, long? CompletedBytes);

    private static Row[] Ledger() =>
    [
        .. File.ReadLines(Path.Combine(Evidence(), "ledger.csv")).Skip(1).Where(static line => line.Length > 0).Select(static line => line.Split(','))
            .Select(static field => new Row(field[0], field[1], field[2], field[3], field[4], field[5], field[6],
                field[7].Length == 0 ? 0 : long.Parse(field[7], CultureInfo.InvariantCulture),
                field[8].Length == 0 ? 0 : long.Parse(field[8], CultureInfo.InvariantCulture), field[9], field[10])),
    ];

    private static Truth[] TruthLog(string file) =>
    [
        .. File.ReadLines(Path.Combine(Evidence(), file)).Where(static line => line.Length > 0).Select(static line =>
        {
            using JsonDocument record = JsonDocument.Parse(line);
            JsonElement root = record.RootElement;
            return new Truth(root.GetProperty("role").GetString()!, root.GetProperty("kind").GetString()!,
                root.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null,
                root.TryGetProperty("completedBytes", out JsonElement bytes) && bytes.ValueKind == JsonValueKind.Number ? bytes.GetInt64() : null);
        }),
    ];

    private static string Evidence() => Path.Combine(RepositoryRoot(), "fixtures", "FX-SECT-001", "evidence");

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
