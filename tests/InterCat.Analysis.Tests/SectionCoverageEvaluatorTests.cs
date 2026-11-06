using InterCat.Analysis;
using InterCat.Domain;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// Sections' coverage against a truth log (ADR-089, ADR-090): each truth section is found twice and its two findings never
/// joined - by its handles, the one number its closes name it by, and by its views, each mapping the nearest record of its
/// process and size, no image's, its unmapping the record of that view, the one number they all carry - and discovered with
/// its lifetime where both are whole and bound to exactly the processes that held it; no record says a byte moved, so the
/// mechanism claims neither bytes nor operations and reaches topology at most.
/// </summary>
public sealed class SectionCoverageEvaluatorTests
{
    private const int Creator = 100, Opener = 200, Other = 300;
    private const long Bytes = 65_536;
    private const long Pagefile = 0x00C4_0000_0000_0000, Image = 0x0047_0000_0000_0000;
    private const string Run = @"Local\InterCat-FX-SECT-001-0123456789abcdef";
    private static readonly string[] Sections = ["memory", "file"];
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "P27: a sections evaluation finds each truth section twice - by its handles' number from its creation to every close, and by its views' number every mapping and unmapping - bound to exactly the processes that held it, and is topology only: it claims no byte and no operation")]
    public void EachSectionIsFoundTwiceAndIsTopologyOnly()
    {
        (TruthRecord[] truth, List<SectionObservation> observed) = Scenario();
        SectionCoverageResult result = SectionCoverageEvaluator.Evaluate(truth, observed, Settings());

        Assert.All(result.Sections, section => Assert.True(section.DiscoveredWithLifetime, section.Gap));
        Assert.Equal(
            [("file", 2L, 11L, 10, 10), ("memory", 1L, 10L, 10, 10)],
            result.Sections.Select(static section => (section.Name[(section.Name.LastIndexOf('-') + 1)..], section.SectionObjectNumber!.Value,
                section.SectionNumber!.Value, section.TruthViews, section.ViewsSeen)));
        Assert.All(result.Sections, static section => Assert.Equal([Creator, Opener], section.MemberProcesses));

        // Every handle open and close and every view mapping and unmapping the truth logged, seen and bound; no byte.
        MechanismMeasurement measurement = result.Measurement;
        Assert.Equal((28L, 28L, 28L), (measurement.TruthOperations, measurement.TruthOperationsWithAdmittedObservation, measurement.TruthOperationsBoundToResourceInstance));
        Assert.Equal((2L, 2L, 4L, 4L, 0L), (measurement.TruthResources, measurement.DiscoveredResourcesWithLifetime, measurement.ExpectedMemberships,
            measurement.ResolvedMemberships, measurement.FalsePeerAttributions));
        Assert.Equal((0L, 0L, false), (measurement.ByteEligibleOperations, measurement.ByteMeasuredOperations, measurement.ClaimsBytesOrOperations));
        Assert.Equal(CapabilityTier.TopologyOnly, result.Assessment.Tier);
        Assert.Contains(result.Notes, static note => note.StartsWith("2 of 2 truth sections were found whole twice", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R22: a truth mapping is the nearest view of its process and size within the match window whose nearest mapping it is too, never an image's or another process's, and its unmapping that view's - so each process's own views beside the truth's leave the evaluation as it was")]
    public void MappingsAreMatchedAmongEachProcesssOwnViews()
    {
        (TruthRecord[] truth, List<SectionObservation> observed) = Scenario();
        long ordinal = observed.Max(static observation => observation.Ordinal);
        foreach (SectionObservation mapping in observed.Where(static observation => observation.Kind == ObservationKind.Map).ToArray())
        {
            // Nearer the truth line than the truth's own record: an image of the same size in the same process, another
            // process's view of the same size, and the process's own view of another size; and the process's own view of the
            // same size a second later.
            observed.Add(mapping with { Ordinal = ++ordinal, ViewNumber = 1_000 + ordinal, SectionNumber = 500, ViewInformation = Image, UtcTicks = mapping.UtcTicks + 50 });
            observed.Add(mapping with { Ordinal = ++ordinal, ViewNumber = 1_000 + ordinal, SectionNumber = 502, ProcessId = Other, UtcTicks = mapping.UtcTicks + 60 });
            observed.Add(mapping with { Ordinal = ++ordinal, ViewNumber = 1_000 + ordinal, SectionNumber = 503, ViewSize = 4_096, UtcTicks = mapping.UtcTicks + 70 });
            observed.Add(mapping with { Ordinal = ++ordinal, ViewNumber = 1_000 + ordinal, SectionNumber = 501, UtcTicks = mapping.UtcTicks + TimeSpan.TicksPerSecond });

            // And beside the creator's, the opener's own view of the same size nearer the creator's line than its own record.
            if (mapping.ProcessId == Creator)
            {
                observed.Add(mapping with { Ordinal = ++ordinal, ViewNumber = 1_000 + ordinal, SectionNumber = 504, ProcessId = Opener, UtcTicks = mapping.UtcTicks + 80 });
            }
        }

        SectionCoverageResult result = SectionCoverageEvaluator.Evaluate(truth, observed, Settings());
        Assert.All(result.Sections, section => Assert.True(section.DiscoveredWithLifetime, section.Gap));
        Assert.Equal([10L, 11L], result.Sections.Select(static section => section.SectionNumber!.Value).Order());
        Assert.Equal(10, result.ObservationsOutsideScope);
        Assert.Contains(result.Notes, static note => note.StartsWith("10 views of images in the truth's processes", StringComparison.Ordinal));

        // The creator's mapping of the file section lost: no view outside the match window stands in for it, though the
        // creator's own view of its size a second later has no nearer mapping.
        observed.Remove(observed.Single(static observation => observation is { Kind: ObservationKind.Map, ProcessId: Creator, SectionNumber: 11 }));
        SectionTruthCoverage file = SectionCoverageEvaluator.Evaluate(truth, observed, Settings()).Sections.Single(static section => section.Name.EndsWith("-file", StringComparison.Ordinal));
        Assert.Equal((false, 8, 11L, "Not every view the truth mapped or unmapped was seen in its own process."),
            (file.DiscoveredWithLifetime, file.ViewsSeen, file.SectionNumber!.Value, file.Gap));

        // A lone mapping whose only record of its process and size lies a second from its line is matched to none: beyond the
        // match window no record is the truth's, nearest or not.
        (truth, observed) = Scenario();
        TruthRecord[] lone = [.. truth.Where(static record => record.ProcessId == Creator && record.ResourceName!.EndsWith("-memory", StringComparison.Ordinal))];
        List<SectionObservation> far = [.. observed.Where(static observation => observation.ProcessId == Creator
            && (observation.SectionObjectNumber == 1 || observation.SectionNumber == 10))
            .Select(static observation => observation.Kind == ObservationKind.Map ? observation with { UtcTicks = observation.UtcTicks + TimeSpan.TicksPerSecond } : observation)];
        SectionTruthCoverage alone = Assert.Single(SectionCoverageEvaluator.Evaluate(lone, far, Settings()).Sections);
        Assert.Equal((false, 0, "Not every view the truth mapped or unmapped was seen in its own process."), (alone.DiscoveredWithLifetime, alone.ViewsSeen, alone.Gap));
    }

    [Fact(DisplayName = "R22: a section is not discovered when not every view of it was seen, when two truth sections' views carry one number - neither's then - or when a process the truth never names maps it; the other section stands where it was whole")]
    public void APartlySeenOrJoinedSectionIsNotDiscovered()
    {
        // One of the opener's mappings of the memory section lost: that mapping and its unmapping unseen, its neighbour's record
        // still its neighbour's, whose nearest mapping it is.
        (TruthRecord[] truth, List<SectionObservation> observed) = Scenario();
        SectionObservation lost = observed.Where(static observation => observation is { Kind: ObservationKind.Map, ProcessId: Opener, SectionNumber: 10 }).ElementAt(2);
        observed.Remove(lost);
        SectionCoverageResult partly = SectionCoverageEvaluator.Evaluate(truth, observed, Settings());
        SectionTruthCoverage memory = partly.Sections.Single(static section => section.Name.EndsWith("-memory", StringComparison.Ordinal));
        Assert.Equal((false, 8, "Not every view the truth mapped or unmapped was seen in its own process."), (memory.DiscoveredWithLifetime, memory.ViewsSeen, memory.Gap));
        Assert.True(partly.Sections.Single(static section => section.Name.EndsWith("-file", StringComparison.Ordinal)).DiscoveredWithLifetime);
        Assert.Equal((26L, 1L), (partly.Measurement.TruthOperationsWithAdmittedObservation, partly.Measurement.DiscoveredResourcesWithLifetime));

        // Short of topology: experimental evidence at most, and only where the run was reproduced.
        Assert.Equal(CapabilityTier.Unsupported, partly.Assessment.Tier);
        Assert.Equal(CapabilityTier.ExperimentalEvidence, SectionCoverageEvaluator.Evaluate(truth, observed, Settings() with { Reproduced = true }).Assessment.Tier);

        // Two of the opener's mappings of the memory section seen as one view: the gate numbered a view where there were two.
        (truth, observed) = Scenario();
        long[] opened = [.. observed.Where(static observation => observation is { Kind: ObservationKind.Map, ProcessId: Opener, SectionNumber: 10 })
            .Select(static observation => observation.ViewNumber!.Value)];
        List<SectionObservation> merged = [.. observed.Select(observation => observation.ViewNumber == opened[1] ? observation with { ViewNumber = opened[0] } : observation)];
        memory = SectionCoverageEvaluator.Evaluate(truth, merged, Settings()).Sections.Single(static section => section.Name.EndsWith("-memory", StringComparison.Ordinal));
        Assert.Equal((false, "Two of its mappings were seen as one view."), (memory.DiscoveredWithLifetime, memory.Gap));

        // The file section's views carrying the memory section's number: one number two truth sections' views carry is neither's.
        (truth, observed) = Scenario();
        List<SectionObservation> joined = [.. observed.Select(static observation => observation.SectionNumber == 11 ? observation with { SectionNumber = 10 } : observation)];
        SectionCoverageResult together = SectionCoverageEvaluator.Evaluate(truth, joined, Settings());
        Assert.All(together.Sections, static section => Assert.Equal((false, "Its views' section number is another truth section's too, so the number is no one section."),
            (section.DiscoveredWithLifetime, section.Gap)));

        // A process the truth never names mapping the memory section: its number bound to a process that never held it.
        (truth, observed) = Scenario();
        observed.Add(new() { Ordinal = 9_999, Kind = ObservationKind.Map, ProcessId = Other, ViewNumber = 9_999, SectionNumber = 10, ViewSize = Bytes, ViewInformation = Pagefile, UtcTicks = Start.UtcTicks });
        SectionCoverageResult stranger = SectionCoverageEvaluator.Evaluate(truth, observed, Settings());
        memory = stranger.Sections.Single(static section => section.Name.EndsWith("-memory", StringComparison.Ordinal));
        Assert.Equal((false, "Its numbers are bound to a process the truth says never held it."), (memory.DiscoveredWithLifetime, memory.Gap));
        Assert.Equal([Creator, Opener, Other], memory.MemberProcesses);
        Assert.Equal(1L, stranger.Measurement.FalsePeerAttributions);
    }

    [Fact(DisplayName = "R22: a section's handles are found as a named object's are - a section whose closes name it by no number, or by two, is not discovered - and its views alone, however whole, never discover it")]
    public void ASectionsHandlesAreFoundAsANamedObjectsAre()
    {
        (TruthRecord[] truth, List<SectionObservation> observed) = Scenario();
        List<SectionObservation> unnamed = [.. observed.Select(static observation => observation.SectionObjectNumber == 1 && observation.Kind == ObservationKind.Close
            ? observation with { Name = null } : observation)];
        SectionTruthCoverage memory = SectionCoverageEvaluator.Evaluate(truth, unnamed, Settings()).Sections
            .Single(static section => section.Name.EndsWith("-memory", StringComparison.Ordinal));
        Assert.Equal((false, (long?)null, 10L, 10, "No close of a handle to it named it under a number of its own."),
            (memory.DiscoveredWithLifetime, memory.SectionObjectNumber, memory.SectionNumber!.Value, memory.ViewsSeen, memory.Gap));

        List<SectionObservation> twice = [.. observed.Select(static observation => observation is { SectionObjectNumber: 1, Kind: ObservationKind.Close, ProcessId: Opener }
            ? observation with { SectionObjectNumber = 7 } : observation)];
        memory = SectionCoverageEvaluator.Evaluate(truth, twice, Settings()).Sections.Single(static section => section.Name.EndsWith("-memory", StringComparison.Ordinal));
        Assert.Equal((false, "Its closes named it under 2 numbers, so no one number is the section."), (memory.DiscoveredWithLifetime, memory.Gap));

        // Both sections' handles under one number: a number two truth sections' closes name is neither's.
        List<SectionObservation> joined = [.. observed.Select(static observation => observation.SectionObjectNumber == 2 ? observation with { SectionObjectNumber = 1 } : observation)];
        Assert.All(SectionCoverageEvaluator.Evaluate(truth, joined, Settings()).Sections, static section => Assert.Equal(
            (false, "Its handles' number is another truth section's too, so the number is no one section."), (section.DiscoveredWithLifetime, section.Gap)));

        // The opener's close of the memory section unseen: the creator's still names it, but not every close was seen.
        List<SectionObservation> unclosed = [.. observed.Where(static observation => observation is not { SectionObjectNumber: 1, Kind: ObservationKind.Close, ProcessId: Opener })];
        memory = SectionCoverageEvaluator.Evaluate(truth, unclosed, Settings()).Sections.Single(static section => section.Name.EndsWith("-memory", StringComparison.Ordinal));
        Assert.Equal((false, false, "Not every close the truth logged was seen, in its own process, under its number."),
            (memory.DiscoveredWithLifetime, memory.EveryCloseSeen, memory.Gap));
    }

    [Fact(DisplayName = "P27: FX-SECT-001's section records through the product reproduce its measured tier - each section found twice, by its handles from the creator's creation to both processes' closes and by every view both processes mapped and unmapped, among their own views and images, bound to exactly the two processes - and topology only")]
    public void CommittedEvidenceReproducesItsTier()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-SECT-001", "evidence");
        string product = Path.Combine(evidence, "product");
        TruthRecord[] truth = [.. ReadTruth(Path.Combine(product, "truth-creator.jsonl")), .. ReadTruth(Path.Combine(product, "truth-opener.jsonl"))];
        Dictionary<string, int> processes = truth.GroupBy(static record => record.Role).ToDictionary(static group => group.Key, static group => group.First().ProcessId);

        // The committed ledger holds the workload's records by role, its runtimes' own views and images among them - never a PID.
        SectionObservation[] records = [.. ReadLedger(Path.Combine(product, "records.csv"), processes)];
        SectionCoverageResult result = SectionCoverageEvaluator.Evaluate(truth, records, Settings() with { Reproduced = true });

        using System.Text.Json.JsonDocument measurement = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        System.Text.Json.JsonElement stated = measurement.RootElement.GetProperty("throughTheProduct").GetProperty("runs")[0].GetProperty("evaluation");
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal(
            (stated.GetProperty("truthSections").GetInt64(), stated.GetProperty("discoveredWithLifetime").GetInt64(),
                stated.GetProperty("expectedMemberships").GetInt64(), stated.GetProperty("resolvedMemberships").GetInt64(),
                stated.GetProperty("falseMemberships").GetInt64(), stated.GetProperty("truthOperations").GetInt64(), stated.GetProperty("observed").GetInt64()),
            (measured.TruthResources, measured.DiscoveredResourcesWithLifetime, measured.ExpectedMemberships, measured.ResolvedMemberships,
                measured.FalsePeerAttributions, measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation));
        Assert.Equal(stated.GetProperty("recordsOfOtherProcessesOnItsSections").GetInt32(), result.ObservationsOutsideScope);
        Assert.Equal(stated.GetProperty("gaps").EnumerateArray().Select(static gap => gap.GetString()), result.Assessment.Gaps);
        Assert.Equal(Enum.Parse<CapabilityTier>(stated.GetProperty("tier").GetString()!), result.Assessment.Tier);

        // Every one of them: two sections, each found twice and bound to exactly the two processes, every handle open and close
        // and every view mapping and unmapping the truth logged seen in its own process, among hundreds of the processes' own
        // views and images; no byte claimed, so the tier is topology.
        Assert.Equal((2L, 2L, 4L, 4L, 0L), (measured.TruthResources, measured.DiscoveredResourcesWithLifetime, measured.ExpectedMemberships,
            measured.ResolvedMemberships, measured.FalsePeerAttributions));
        Assert.Equal((28L, 28L, 0L, false), (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation,
            measured.ByteEligibleOperations, measured.ClaimsBytesOrOperations));
        int[] holders = [.. processes.Values.Order()];
        Assert.All(result.Sections, section => Assert.Equal(holders, section.MemberProcesses));
        Assert.True(records.Count(static record => record.IsView) > 500 && records.Any(static record => record.IsImage));
        Assert.Equal(CapabilityTier.TopologyOnly, result.Assessment.Tier);
    }

    [Fact(DisplayName = "R22: FX-SECT-002's section records through the product keep each of 128 sections made and ended in 64 generations one number of its own by its handles and one by its views, bound to exactly the two processes, while the kernel made some at an object address, and gave the views of more a value, an earlier generation's section had held")]
    public void SectionsMadeInGenerationsAreEachTheirOwn()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-SECT-002", "evidence");
        TruthRecord[] truth = [.. ReadTruth(Path.Combine(evidence, "truth-creator.jsonl")), .. ReadTruth(Path.Combine(evidence, "truth-opener.jsonl"))];
        Dictionary<string, int> processes = truth.GroupBy(static record => record.Role).ToDictionary(static group => group.Key, static group => group.First().ProcessId);
        SectionObservation[] records = [.. ReadLedger(Path.Combine(evidence, "records.csv"), processes)];
        SectionCoverageResult result = SectionCoverageEvaluator.Evaluate(truth, records, Settings() with { FixtureId = "FX-SECT-002", Reproduced = true });

        using System.Text.Json.JsonDocument measurement = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        System.Text.Json.JsonElement product = measurement.RootElement.GetProperty("throughTheProduct");
        int committed = measurement.RootElement.GetProperty("committedRun").GetInt32();
        System.Text.Json.JsonElement run = product.GetProperty("runs").EnumerateArray().Single(run => run.GetProperty("run").GetInt32() == committed);
        System.Text.Json.JsonElement stated = run.GetProperty("evaluation");
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal(
            (stated.GetProperty("truthSections").GetInt64(), stated.GetProperty("discoveredWithLifetime").GetInt64(),
                stated.GetProperty("expectedMemberships").GetInt64(), stated.GetProperty("resolvedMemberships").GetInt64(),
                stated.GetProperty("falseMemberships").GetInt64(), stated.GetProperty("truthOperations").GetInt64(), stated.GetProperty("observed").GetInt64(),
                stated.GetProperty("boundToTheirSection").GetInt64()),
            (measured.TruthResources, measured.DiscoveredResourcesWithLifetime, measured.ExpectedMemberships, measured.ResolvedMemberships,
                measured.FalsePeerAttributions, measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation,
                measured.TruthOperationsBoundToResourceInstance));
        Assert.Equal(stated.GetProperty("recordsOfOtherProcessesOnItsSections").GetInt32(), result.ObservationsOutsideScope);
        Assert.Equal(Enum.Parse<CapabilityTier>(stated.GetProperty("tier").GetString()!), result.Assessment.Tier);

        // Every one of them: each generation's two sections found twice, by its handles from the creator's creation to both
        // processes' closes and by every view both processes mapped and unmapped, each of its two numbers its own and no
        // other section's, bound to exactly the two processes, and every operation the truth logged seen and bound to it.
        Assert.Equal((128L, 128L, 256L, 256L, 0L), (measured.TruthResources, measured.DiscoveredResourcesWithLifetime, measured.ExpectedMemberships,
            measured.ResolvedMemberships, measured.FalsePeerAttributions));
        Assert.Equal((1280L, 1280L, 1280L, false), (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation,
            measured.TruthOperationsBoundToResourceInstance, measured.ClaimsBytesOrOperations));
        Assert.Equal((128, 128), (result.Sections.Select(static section => section.SectionObjectNumber).OfType<long>().Distinct().Count(),
            result.Sections.Select(static section => section.SectionNumber).OfType<long>().Distinct().Count()));
        Assert.Equal((stated.GetProperty("handleNumbers").GetInt32(), stated.GetProperty("viewSectionNumbers").GetInt32()), (128, 128));
        int[] holders = [.. processes.Values.Order()];
        Assert.All(result.Sections, section => Assert.Equal(holders, section.MemberProcesses));
        Assert.Equal(CapabilityTier.TopologyOnly, result.Assessment.Tier);

        // While the lab probe's own session, watching the same run, counted sections the kernel made at an object address an
        // earlier generation's section held, and sections whose views it gave a value an earlier generation's section's views
        // carried: a capture's records alone would have joined each to that earlier section.
        System.Text.Json.JsonElement watched = run.GetProperty("observedByTheProbe");
        using System.Text.Json.JsonDocument scenario = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "scenario.json")));
        Assert.Equal((64, 64, 128, 0), (scenario.RootElement.GetProperty("generations").GetInt32(), watched.GetProperty("generations").GetInt32(),
            watched.GetProperty("sections").GetInt32(), watched.GetProperty("eventsLost").GetInt32()));
        Assert.Equal((128, 128, 384, 384), (watched.GetProperty("sectionsWithOneAddress").GetInt32(), watched.GetProperty("sectionsWithOneViewValue").GetInt32(),
            watched.GetProperty("truthMappings").GetInt32(), watched.GetProperty("mappingsSeen").GetInt32()));
        int atAnAddress = watched.GetProperty("sectionsAtAnAddressAnEarlierGenerationsSectionHeld").GetInt32();
        int withAValue = watched.GetProperty("sectionsWhoseViewsCarriedAValueAnEarlierGenerationsSectionsViewsCarried").GetInt32();
        Assert.InRange(atAnAddress, 1, 127);
        Assert.InRange(withAValue, 1, 127);

        // The same run, as the probe reported it beside the product's evaluation, is the bench's.
        using System.Text.Json.JsonDocument bench = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), product.GetProperty("evidence").GetString()!)));
        System.Text.Json.JsonElement reported = bench.RootElement.GetProperty("runs").EnumerateArray().Single(entry => entry.GetProperty("run").GetInt32() == committed);
        Assert.Equal((atAnAddress, withAValue, 128, 1280), (
            reported.GetProperty("observedByTheProbe").GetProperty("sectionsAtAnAddressAnEarlierGenerationsSectionHeld").GetInt32(),
            reported.GetProperty("observedByTheProbe").GetProperty("sectionsWhoseViewsCarriedAValueAnEarlierGenerationsSectionsViewsCarried").GetInt32(),
            reported.GetProperty("discoveredWithLifetime").GetInt32(), reported.GetProperty("observed").GetInt32()));
    }

    private static IEnumerable<TruthRecord> ReadTruth(string file)
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return File.ReadLines(file).Where(static line => line.Length > 0).Select(line => System.Text.Json.JsonSerializer.Deserialize<TruthRecord>(line, options)!);
    }

    /// <summary>A product ledger read back as the session's records were, each role the process the truth names by it, others apart.</summary>
    private static IEnumerable<SectionObservation> ReadLedger(string ledger, Dictionary<string, int> processes)
    {
        foreach (string[] field in File.ReadLines(ledger).Skip(1).Where(static line => line.Length > 0).Select(static line => line.Split(',')))
        {
            static long? Number(string value) => value.Length == 0 ? null : long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            yield return new()
            {
                Ordinal = long.Parse(field[0], System.Globalization.CultureInfo.InvariantCulture),
                UtcTicks = Number(field[1]),
                ProcessId = field[2].Length == 0 ? null : processes.TryGetValue(field[2], out int id) ? id
                    : 1_000_000 + int.Parse(field[2]["other-".Length..], System.Globalization.CultureInfo.InvariantCulture),
                Kind = Enum.Parse<ObservationKind>(field[3]),
                SectionObjectNumber = Number(field[4]),
                ViewNumber = Number(field[5]),
                SectionNumber = Number(field[6]),
                ViewSize = Number(field[7]),
                ViewInformation = field[8].Length == 0 ? null
                    : (long)(ulong.Parse(field[8][2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture) << 48),
                Name = field[9].Length == 0 ? null : field[9],
            };
        }
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

    private static SectionCoverageSettings Settings() => new()
    {
        FixtureId = "FX-SECT-001",
        BuildId = "10.0.26220.0-x64",
        BuildIsSupported = true,
    };

    /// <summary>
    /// FX-SECT-001's run as a capture would hold it: the creator makes both sections and maps a view of each; the opener opens
    /// each, maps and unmaps a view of it four times and closes it; the creator unmaps its views and closes both. Each handle's
    /// section numbered 1 and 2 by the handle gate, each view's section 10 and 11 by the view gate, each view a number of its
    /// own, each mapping's record raised 10 microseconds before its truth line.
    /// </summary>
    private static (TruthRecord[] Truth, List<SectionObservation> Observed) Scenario()
    {
        var truth = new List<TruthRecord>();
        var observed = new List<SectionObservation>();
        var numbers = new Dictionary<string, (long Handle, long Section)> { ["memory"] = (1, 10), ["file"] = (2, 11) };
        var creatorViews = new Dictionary<string, long>();
        long sequence = 0, ordinal = 0, views = 0;

        void Truth(string role, int process, TruthEventKind kind, string section, long? call = null) => truth.Add(new()
        {
            ScenarioId = "FX-SECT-001",
            Role = role,
            Sequence = ++sequence,
            Kind = kind,
            ProcessId = process,
            ProcessStartFileTime = 0,
            MonotonicTicks = sequence,
            RecordedUtc = Start.AddMilliseconds(sequence),
            ResourceName = Run + "-" + section,
            CallId = call,
            DeclaredBytes = kind == TruthEventKind.ViewMapped ? Bytes : null,
            Status = "section",
        });
        void Handle(int process, ObservationKind kind, string section) => observed.Add(new()
        {
            Ordinal = ++ordinal,
            Kind = kind,
            ProcessId = process,
            SectionObjectNumber = numbers[section].Handle,
            Name = kind == ObservationKind.Close ? @"\Sessions\1\BaseNamedObjects\" + Run[(Run.IndexOf('\\') + 1)..] + "-" + section : null,
        });
        SectionObservation View(int process, ObservationKind kind, string section, long view) => new()
        {
            Ordinal = ++ordinal,
            Kind = kind,
            ProcessId = process,
            ViewNumber = view,
            SectionNumber = numbers[section].Section,
            ViewSize = Bytes,
            ViewInformation = Pagefile,
            UtcTicks = Start.AddMilliseconds(sequence).UtcTicks - 100,
        };

        foreach (string section in Sections)
        {
            Truth("creator", Creator, TruthEventKind.ObjectCreated, section);
            Handle(Creator, ObservationKind.Open, section);
            Truth("creator", Creator, TruthEventKind.ViewMapped, section, 1);
            observed.Add(View(Creator, ObservationKind.Map, section, creatorViews[section] = ++views));
        }

        foreach (string section in Sections)
        {
            Truth("opener", Opener, TruthEventKind.ObjectOpened, section);
            Handle(Opener, ObservationKind.Open, section);
            for (int round = 1; round <= 4; round++)
            {
                Truth("opener", Opener, TruthEventKind.ViewMapped, section, round);
                long view = ++views;
                observed.Add(View(Opener, ObservationKind.Map, section, view));
                Truth("opener", Opener, TruthEventKind.ViewUnmapped, section, round);
                observed.Add(View(Opener, ObservationKind.Unmap, section, view));
            }

            Truth("opener", Opener, TruthEventKind.HandleClosed, section);
            Handle(Opener, ObservationKind.Close, section);
        }

        foreach (string section in Sections)
        {
            Truth("creator", Creator, TruthEventKind.ViewUnmapped, section, 1);
            observed.Add(View(Creator, ObservationKind.Unmap, section, creatorViews[section]));
            Truth("creator", Creator, TruthEventKind.HandleClosed, section);
            Handle(Creator, ObservationKind.Close, section);
        }

        return ([.. truth], observed);
    }
}
