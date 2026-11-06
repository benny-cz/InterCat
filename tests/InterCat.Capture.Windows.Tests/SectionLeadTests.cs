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

    [Fact(DisplayName = "P27: sections have an inventoried lead - the kernel's file view flag, requested by no profile, its four view events at the version this build raises them at, admitted through the section view gate, its measured notes and Low cost stated beside its throughput regression - the early memory candidate claims none, and no build carries a tier for sections")]
    public void SectionsHaveAnInventoriedLeadAndNoTier()
    {
        // A kernel flag of the file I/O class, the one FX-SECT-001 was measured under, with no process filter; its view's
        // mapping and unmapping and both rundowns admitted at version 3, through the section view gate.
        WindowsSourceDefinition views = WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelFileViewsSourceId)!;
        Assert.Equal((SourceKind.KernelFlagGroup, 0x8000UL, Guid.Parse("90cbdc39-4a3e-11d1-84f4-0000f80464e3")),
            (views.Kind, views.KernelFlags, views.ClassicEventClass));
        Assert.Equal([Mechanism.SharedSection], views.Mechanisms);
        Assert.Equal((SourceContractStatus.Experimental, false), (views.ContractStatus, views.SupportsCaptureSideProcessFilter));
        Assert.Equal([(37, 3), (38, 3), (39, 3), (40, 3)], views.AdmittedEvents.Select(static intent => (intent.Opcode!.Value, intent.Version)).Order());
        Assert.Contains(views.Notes, note => note.Contains("section view gate (ADR-090)", StringComparison.Ordinal) && note.Contains("never by its base", StringComparison.Ordinal));
        Assert.Contains(views.Notes, note => note.Contains("layout measured for this build", StringComparison.Ordinal) && note.Contains("ADR-091", StringComparison.Ordinal));

        // Its cost is measured, Low in the processor time it adds over trials of four seconds, with the throughput regression
        // its notes state beside the class.
        Assert.Equal(OverheadClass.Low, views.Overhead);
        Assert.Matches(@"^bench/results/section-view-impact-\d{8}T\d{6}Z/impact\.json$", views.OverheadEvidence);
        using (JsonDocument impact = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), views.OverheadEvidence!))))
        {
            Assert.Equal((views.Overhead.ToString(), false), (impact.RootElement.GetProperty("attributedOverhead").GetString(),
                impact.RootElement.GetProperty("throughputTargetMet").GetBoolean()));
            Assert.Contains("--rounds 100000", impact.RootElement.GetProperty("workload").GetString(), StringComparison.Ordinal);
        }

        Assert.Contains(views.Notes, note => note.Contains("past the 5% target", StringComparison.Ordinal) && note.Contains("never Explore", StringComparison.Ordinal));
        Assert.Contains(views.Notes, note => note.Contains("FX-SECT-001", StringComparison.Ordinal) && note.Contains("no handle carries", StringComparison.Ordinal));
        Assert.Contains(views.Notes, note => note.Contains("at version 3", StringComparison.Ordinal) && note.Contains("no layout the machine registers", StringComparison.Ordinal));
        Assert.Contains(views.Notes, note => note.Contains("membership, never traffic", StringComparison.Ordinal));
        Assert.Contains(views.Notes, note => note.Contains("every view and every image", StringComparison.Ordinal) && note.Contains("0x47", StringComparison.Ordinal));
        Assert.Contains("images alike", views.FilteringNotes, StringComparison.Ordinal);

        // No profile requests it, no other source claims sections - the early memory candidate, whose events name no section,
        // claims none - and no build carries a tier for them.
        Assert.DoesNotContain(CaptureProfileCatalog.All.Concat(CaptureProfileCatalog.Fixtures),
            profile => profile.Sources.Any(source => source.SourceId == views.SourceId));
        Assert.All(WindowsSourceCatalog.All.Where(source => source.SourceId != views.SourceId),
            source => Assert.DoesNotContain(Mechanism.SharedSection, source.Mechanisms));
        WindowsSourceDefinition memory = WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelMemorySourceId)!;
        Assert.Empty(memory.Mechanisms);
        Assert.Contains(memory.Notes, note => note.Contains("name no section", StringComparison.Ordinal));
        Assert.DoesNotContain(MeasuredCoverage.Entries, entry => entry.Mechanism == Mechanism.SharedSection);
    }

    [Fact(DisplayName = "§4.2: on this build the object type table read before a capture starts, the session's type rundown at its stop and the table after all name the Section type 49, as they name every type but the type of types alike - and during FX-SECT-001 about one of the machine's handle records in thirty was a section's, nothing lost")]
    public void TheSectionTypesNumberIsMeasured()
    {
        using JsonDocument types = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Assert.Single(Directory.GetDirectories(Path.Combine(RepositoryRoot(), "bench", "results"), "section-types-*")), "report.json")));
        JsonElement root = types.RootElement;
        Assert.Equal(("10.0.26220.0-x64", 0), (root.GetProperty("build").GetString(), root.GetProperty("eventsLost").GetInt32()));

        // The three readings name Section alike, and every type but the type of types, which no rundown names.
        JsonElement section = root.GetProperty("sectionType");
        Assert.Equal((49, 49, 49), (section.GetProperty("before").GetInt32(), section.GetProperty("rundown").GetInt32(), section.GetProperty("after").GetInt32()));
        Assert.Equal(["2: before Type, rundown none, after Type"], root.GetProperty("disagreements").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(root.GetProperty("typesBefore").GetInt32(), root.GetProperty("typesInRundown").GetInt32() + 1);

        // A section's handle records are a small share of the machine's: a gate admitting sections alone keeps about one in thirty.
        double share = (double)root.GetProperty("createsOfSections").GetInt64() / root.GetProperty("creates").GetInt64();
        Assert.InRange(share, 0.01, 0.1);
        Assert.True(root.GetProperty("namedClosesOfSections").GetInt64() > 0 && root.GetProperty("duplicatesOfSections").GetInt64() > 0);
    }

    [Fact(DisplayName = "§4.2: the file view flag's rundowns at a session's start and stop are raised at version 3, 44 bytes, as a mapping is - no header naming a process, each record the process whose view it names, one the process rundown named alive - and in three runs, nothing lost, they named every view and every image the probe held across them, an image's view carrying 0x47 in its extra information's high byte as its mapping does")]
    public void TheRundownsNameEveryViewAndImageByItsProcess()
    {
        string[] runs = [.. Directory.GetDirectories(Path.Combine(RepositoryRoot(), "bench", "results"), "section-rundowns-*")
            .Select(static directory => Path.Combine(directory, "report.json")).Order(StringComparer.Ordinal)];
        Assert.Equal(3, runs.Length);
        foreach (string file in runs)
        {
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = report.RootElement;
            Assert.Equal(0, root.GetProperty("session").GetProperty("eventsLost").GetInt32());
            foreach ((string name, int opcode) in new[] { ("start", 39), ("stop", 40) })
            {
                // The mappings' layout: version 3, 44 bytes, a page-aligned field before the process at their end.
                JsonElement rundown = root.GetProperty("rundowns").GetProperty(name);
                int[] Values(string property) => [.. rundown.GetProperty(property).EnumerateArray().Select(static value => value.GetInt32())];
                Assert.Equal((opcode, true), (rundown.GetProperty("opcode").GetInt32(), rundown.GetProperty("betweenPageAligned").GetBoolean()));
                Assert.Equal([3], Values("versions"));
                Assert.Equal([44], Values("payloadBytes"));
                Assert.Equal([8], Values("betweenSizeAndProcessBytes"));

                // Every record names a process the session's process rundown named alive, and no header names one.
                int records = rundown.GetProperty("records").GetInt32();
                Assert.InRange(records, 50_000, 70_000);
                Assert.Equal((records, rundown.GetProperty("processes").GetInt32()),
                    (rundown.GetProperty("recordsOfProcessesNamedAlive").GetInt32(), rundown.GetProperty("processesNamedAlive").GetInt32()));
                Assert.Equal(("none", records), Assert.Single(rundown.GetProperty("header").EnumerateObject().Select(static entry => (entry.Name, entry.Value.GetInt32()))));

                // Every view and image the probe held across the rundown named, and every base named in neither of its
                // snapshots one it mapped meanwhile; an image's view 0x47, no other view's.
                JsonElement probe = rundown.GetProperty("probe");
                int Count(string property) => probe.GetProperty(property).GetInt32();
                Assert.True(Count("viewsThroughout") > 0 && Count("imagesThroughout") > 0, file);
                Assert.Equal((Count("viewsThroughout"), Count("imagesThroughout"), Count("basesNeitherBeforeNorAfter")),
                    (Count("viewsThroughoutNamed"), Count("imagesThroughoutNamed"), Count("ofThemMappedMeanwhile")));
                Assert.Equal(["0x47"], probe.GetProperty("miscInfoHighBytesOfImages").EnumerateArray().Select(static value => value.GetString()));
                Assert.DoesNotContain("0x47", probe.GetProperty("miscInfoHighBytesOfViews").EnumerateArray().Select(static value => value.GetString()));
            }

            // The views and images the probe mapped while the session ran, each named by a mapping record, an image's 0x47.
            JsonElement during = root.GetProperty("probeMappedDuringTheSession");
            foreach (string kind in new[] { "views", "images" })
            {
                JsonElement mapped = during.GetProperty(kind);
                Assert.True(mapped.GetProperty("mapped").GetInt32() > 0, file);
                Assert.Equal(mapped.GetProperty("mapped").GetInt32(), mapped.GetProperty("named").GetInt32());
                Assert.Equal(kind == "images", mapped.GetProperty("miscInfoHighBytes").EnumerateArray().Any(static value => value.GetString() == "0x47"));
            }
        }
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
