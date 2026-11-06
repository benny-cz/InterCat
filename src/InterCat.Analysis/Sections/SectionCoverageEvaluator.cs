using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>The fixture and build a sections evaluation is for.</summary>
public sealed record SectionCoverageSettings
{
    public required string FixtureId { get; init; }
    public required string BuildId { get; init; }
    public required bool BuildIsSupported { get; init; }
    public bool Reproduced { get; init; }

    /// <summary>
    /// How far from its truth line a mapping's record may lie: the kernel raises it as the call maps the view, and the truth
    /// logs the mapping once the call returned - at most 21 microseconds later on FX-SECT-001 (ADR-089) - though a capture's
    /// calibration may read either a little apart.
    /// </summary>
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromMilliseconds(5);
}

/// <summary>
/// One of a capture's section records as a session holds it (ADR-090), by the process it binds to since `process-binding-v8`:
/// a section handle's - a creation or a duplication, an open; a close; or a handle open at the stop, an inventory - with the
/// section's number the handle gate wrote over its address and the name a close and the stop's rundown carry; or a view's - its
/// mapping, its unmapping, or a rundown's, an inventory - with the view's and its section's numbers the view gate wrote over its
/// base and its value, its size, its extra information, and its time on the wall clock its capture's calibration reads.
/// </summary>
public sealed record SectionObservation
{
    /// <summary>The record's place in the capture, which orders its records.</summary>
    public required long Ordinal { get; init; }

    public required ObservationKind Kind { get; init; }

    /// <summary>The process the record binds to; null for one that binds to none.</summary>
    public int? ProcessId { get; init; }

    /// <summary>A handle's section by its number in the capture, never its address; null for a view's record.</summary>
    public long? SectionObjectNumber { get; init; }

    /// <summary>The section's full name, which only a handle's close and the stop's rundown carry; null for none.</summary>
    public string? Name { get; init; }

    /// <summary>A view's number in the capture, never its base; null for a handle's record.</summary>
    public long? ViewNumber { get; init; }

    /// <summary>A view's section by its number in the capture, never the value its views carry; never a handle's number.</summary>
    public long? SectionNumber { get; init; }

    /// <summary>A view's size as its mapping gives it: the extent mapped, never bytes moved.</summary>
    public long? ViewSize { get; init; }

    /// <summary>A view's extra information, whose high byte is 0x47 for an image's view.</summary>
    public long? ViewInformation { get; init; }

    /// <summary>A view record's time, in UTC ticks on the wall clock its capture's calibration reads; null where none was read.</summary>
    public long? UtcTicks { get; init; }

    /// <summary>Whether this is a view's record rather than a handle's.</summary>
    public bool IsView => ViewNumber is not null;

    /// <summary>Whether this is an image's view: its extra information's high byte 0x47, which no other view carries (ADR-089).</summary>
    public bool IsImage => ViewInformation is { } information && (ulong)information >> 48 == 0x47;
}

/// <summary>
/// One truth section and what the capture showed of it: the processes the truth says held it; the one number its handles'
/// closes named it by, and whether its creation and every close were seen under it; the one number its views were followed
/// by, and how many of its views were seen; the processes its two numbers' records bind to; and whether it was discovered with
/// its lifetime, or why not. Its two numbers are never joined: no record joins a section's handles to its views (ADR-089).
/// </summary>
public sealed record SectionTruthCoverage(
    string Name,
    IReadOnlyList<int> TruthProcesses,
    long? SectionObjectNumber,
    bool CreationSeen,
    bool EveryCloseSeen,
    long? SectionNumber,
    int TruthViews,
    int ViewsSeen,
    IReadOnlyList<int> MemberProcesses,
    bool DiscoveredWithLifetime,
    string? Gap);

/// <summary>The evaluation result: the section 14.2 counters plus the evidence behind every one of them.</summary>
public sealed record SectionCoverageResult
{
    public required MechanismMeasurement Measurement { get; init; }
    public required TierAssessment Assessment { get; init; }
    public required IReadOnlyList<SectionTruthCoverage> Sections { get; init; }

    /// <summary>The section records of processes the truth does not name: a machine-wide capture sees them; they are scope.</summary>
    public required int ObservationsOutsideScope { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Compares an independent truth log of sections two processes share - each section a process created or opened by its name,
/// each view of it the process mapped and unmapped, and each handle it closed - with a capture's section records (ADR-089,
/// ADR-090), and computes the section 14.2 counters. A section is found twice, and its two findings are never joined, as no
/// record joins them:
/// <list type="bullet">
/// <item>by its handles: the one number its handles' closes naming it carry, as a named synchronization object is (ADR-084),
/// its creation in the creator and every close the truth logged seen under it, each in the process that logged it;</item>
/// <item>by its views: each truth mapping is the record of its process mapping a view of its size, no image's, nearest its log
/// line within the match window where that line is the record's nearest mapping too, and its unmapping the record unmapping
/// that view's number in that process; the section is the one number every one of its views carries.</item>
/// </list>
/// A number two truth sections' records carry is neither's: the capture joined sections the truth says were apart. A section is
/// discovered with its lifetime where both findings are whole and the processes its two numbers' records bind to are exactly
/// the truth's. Its handle opens and closes and its views' mappings and unmappings are the truth's operations; no record says
/// a byte moved through a view, and a view's size is the extent mapped, so the mechanism claims neither bytes nor operations
/// and can reach no tier past topology (P3, P27). It is pure and portable: the same evaluation runs in a test on committed
/// evidence (R19, R18).
/// </summary>
public static class SectionCoverageEvaluator
{
    public static SectionCoverageResult Evaluate(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<SectionObservation> observations,
        SectionCoverageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(settings);

        // Each truth section by its name: who created and opened it, each handle closed, and each view mapped and unmapped.
        TruthRecord[] sectionRecords = [.. truth.Where(static record => record.ResourceName is not null && record.Kind is TruthEventKind.ObjectCreated
            or TruthEventKind.ObjectOpened or TruthEventKind.HandleClosed or TruthEventKind.ViewMapped or TruthEventKind.ViewUnmapped)];
        HashSet<int> processes = [.. truth.Select(static record => record.ProcessId)];
        SectionObservation[] inScope = [.. observations.Where(observation => observation.ProcessId is { } process && processes.Contains(process))
            .OrderBy(static observation => observation.Ordinal)];
        int outsideScope = observations.Count - inScope.Length;
        IGrouping<string, TruthRecord>[] sections = [.. sectionRecords.GroupBy(static record => record.ResourceName!, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)];

        // Each truth mapping's record: the view mapped in its process, of its size, no image's, nearest its log line within the
        // match window on either side - a capture's calibration reads the wall clock at its own moments, so a record raised
        // microseconds before the line may be read as just after it - where that log line is the record's nearest mapping too.
        // A record lost then leaves its own mapping unmatched, never taking its neighbour's and that one its next's.
        TruthRecord[] mappings = [.. sectionRecords.Where(static record => record.Kind == TruthEventKind.ViewMapped)];
        SectionObservation[] offered = [.. inScope.Where(static observation => observation is { IsView: true, IsImage: false, Kind: ObservationKind.Map, UtcTicks: not null })];
        long Distance(TruthRecord mapping, SectionObservation record) => Math.Abs(mapping.RecordedUtc.UtcTicks - record.UtcTicks!.Value);
        bool Fits(TruthRecord mapping, SectionObservation record) => record.ProcessId == mapping.ProcessId
            && record.ViewSize == mapping.DeclaredBytes && Distance(mapping, record) <= settings.MatchWindow.Ticks;
        var mapped = new Dictionary<TruthRecord, SectionObservation>(ReferenceEqualityComparer.Instance);
        foreach (TruthRecord mapping in mappings)
        {
            SectionObservation? record = offered.Where(candidate => Fits(mapping, candidate)).MinBy(candidate => Distance(mapping, candidate));
            if (record is not null && ReferenceEquals(mappings.Where(other => Fits(other, record)).MinBy(other => Distance(other, record)), mapping))
            {
                mapped[mapping] = record;
            }
        }

        var unmapped = new Dictionary<TruthRecord, SectionObservation>(ReferenceEqualityComparer.Instance);
        foreach (TruthRecord unmapping in sectionRecords.Where(static record => record.Kind == TruthEventKind.ViewUnmapped))
        {
            TruthRecord? mapping = sectionRecords.FirstOrDefault(record => record.Kind == TruthEventKind.ViewMapped
                && record.ProcessId == unmapping.ProcessId && record.ResourceName == unmapping.ResourceName && record.CallId == unmapping.CallId);
            if (mapping is not null && mapped.TryGetValue(mapping, out SectionObservation? view)
                && inScope.FirstOrDefault(observation => observation is { IsView: true, Kind: ObservationKind.Unmap }
                    && observation.ProcessId == unmapping.ProcessId && observation.ViewNumber == view.ViewNumber) is { } record)
            {
                unmapped[unmapping] = record;
            }
        }

        // Each truth section's two numbers: the one its handles' closes name it by, and the one its seen views carry.
        (IGrouping<string, TruthRecord> Named, long[] Handles, long[] Views)[] candidates =
        [
            .. sections.Select(named => (named,
                (long[])[.. inScope.Where(observation => observation is { IsView: false, Kind: ObservationKind.Close, SectionObjectNumber: not null }
                    && SyncObjectCoverageEvaluator.Names(observation.Name, named.Key)).Select(static observation => observation.SectionObjectNumber!.Value).Distinct().Order()],
                (long[])[.. named.Select(record => mapped.GetValueOrDefault(record) ?? unmapped.GetValueOrDefault(record))
                    .OfType<SectionObservation>().Where(static observation => observation.SectionNumber is not null)
                    .Select(static observation => observation.SectionNumber!.Value).Distinct().Order()])),
        ];

        // A number two truth sections' records carry is neither's: the capture joined sections the truth says were apart.
        HashSet<long> sharedHandles = [.. candidates.Where(static candidate => candidate.Handles.Length == 1)
            .GroupBy(static candidate => candidate.Handles[0]).Where(static group => group.Count() > 1).Select(static group => group.Key)];
        HashSet<long> sharedViews = [.. candidates.Where(static candidate => candidate.Views.Length == 1)
            .GroupBy(static candidate => candidate.Views[0]).Where(static group => group.Count() > 1).Select(static group => group.Key)];

        var found = new List<SectionTruthCoverage>();
        long truthOperations = 0;
        long observedOperations = 0;
        long boundOperations = 0;
        long expectedMemberships = 0;
        long resolvedMemberships = 0;
        long attributions = 0;
        long falseAttributions = 0;
        foreach ((IGrouping<string, TruthRecord> named, long[] handleNumbers, long[] viewNumbers) in candidates)
        {
            int[] holders = [.. named.Where(static record => record.Kind is TruthEventKind.ObjectCreated or TruthEventKind.ObjectOpened)
                .Select(static record => record.ProcessId).Distinct().Order()];
            int[] creators = [.. named.Where(static record => record.Kind == TruthEventKind.ObjectCreated).Select(static record => record.ProcessId).Distinct()];
            TruthRecord[] views = [.. named.Where(static record => record.Kind is TruthEventKind.ViewMapped or TruthEventKind.ViewUnmapped)];
            Dictionary<(int Process, bool Close), int> logged = named.Where(static record => record.Kind is not (TruthEventKind.ViewMapped or TruthEventKind.ViewUnmapped))
                .GroupBy(static record => (record.ProcessId, record.Kind == TruthEventKind.HandleClosed))
                .ToDictionary(static group => group.Key, static group => group.Count());
            truthOperations += named.Count();
            expectedMemberships += holders.Length;

            // By its handles: the one number its closes name it by, its creation and every close seen under it.
            long? handleNumber = handleNumbers.Length == 1 ? handleNumbers[0] : null;
            bool ownHandle = handleNumber is { } h && !sharedHandles.Contains(h);
            SectionObservation[] handleRecords = handleNumber is { } number
                ? [.. observations.Where(observation => !observation.IsView && observation.SectionObjectNumber == number)]
                : [];
            foreach (((int process, bool close), int count) in logged)
            {
                int seen = handleRecords.Count(observation => observation.ProcessId == process
                    && (close ? observation.Kind == ObservationKind.Close : observation.Kind == ObservationKind.Open));
                observedOperations += Math.Min(count, seen);
                boundOperations += ownHandle ? Math.Min(count, seen) : 0;
            }

            bool creation = creators.Length == 1 && handleRecords.Any(observation => observation.Kind == ObservationKind.Open && observation.ProcessId == creators[0]);
            bool everyClose = logged.Where(static pair => pair.Key.Close).All(pair => handleRecords.Count(observation =>
                observation.Kind == ObservationKind.Close && observation.ProcessId == pair.Key.Process) >= pair.Value);

            // By its views: each seen in its own process, the one number every one of them carries, each mapping a view of its own.
            long? sectionNumber = viewNumbers.Length == 1 ? viewNumbers[0] : null;
            bool ownViews = sectionNumber is { } s && !sharedViews.Contains(s);
            SectionObservation[] seenViews = [.. views.Select(record => mapped.GetValueOrDefault(record) ?? unmapped.GetValueOrDefault(record)).OfType<SectionObservation>()];
            observedOperations += seenViews.Length;
            boundOperations += ownViews ? seenViews.Count(observation => observation.SectionNumber == sectionNumber) : 0;
            bool everyView = seenViews.Length == views.Length;
            bool apart = views.Where(static record => record.Kind == TruthEventKind.ViewMapped).Select(record => mapped.GetValueOrDefault(record)?.ViewNumber)
                .OfType<long>().Distinct().Count() == views.Count(static record => record.Kind == TruthEventKind.ViewMapped);
            SectionObservation[] viewRecords = sectionNumber is { } section
                ? [.. observations.Where(observation => observation.IsView && observation.SectionNumber == section)]
                : [];

            // Its members: the processes its handles' number and its views' number bind to, which must be exactly the truth's.
            int[] members = [.. handleRecords.Concat(viewRecords).Where(static observation => observation.ProcessId is not null)
                .Select(static observation => observation.ProcessId!.Value).Distinct().Order()];
            int[] falseMembers = [.. members.Except(holders)];
            attributions += members.Length;
            falseAttributions += falseMembers.Length;
            resolvedMemberships += members.Intersect(holders).Count();

            bool discovered = ownHandle && creation && everyClose && ownViews && everyView && apart && falseMembers.Length == 0 && holders.All(members.Contains);
            string? gap = discovered ? null
                : handleNumbers.Length == 0 ? "No close of a handle to it named it under a number of its own."
                : handleNumbers.Length > 1 ? $"Its closes named it under {handleNumbers.Length} numbers, so no one number is the section."
                : !ownHandle ? "Its handles' number is another truth section's too, so the number is no one section."
                : !creation ? "No creation of its first handle, in the process that created it, carried its number."
                : !everyClose ? "Not every close the truth logged was seen, in its own process, under its number."
                : !everyView ? "Not every view the truth mapped or unmapped was seen in its own process."
                : sectionNumber is null ? $"Its views carried {viewNumbers.Length} section numbers, so no one number is the section's views."
                : !ownViews ? "Its views' section number is another truth section's too, so the number is no one section."
                : !apart ? "Two of its mappings were seen as one view."
                : falseMembers.Length > 0 ? "Its numbers are bound to a process the truth says never held it."
                : "Not every process that held it is bound to its numbers.";
            found.Add(new(named.Key, holders, handleNumber, creation, everyClose, sectionNumber, views.Length, seenViews.Length, members, discovered, gap));
        }

        int discoveredSections = found.Count(static item => item.DiscoveredWithLifetime);
        int images = inScope.Count(static observation => observation.IsImage);
        var measurement = new MechanismMeasurement
        {
            FixtureId = settings.FixtureId,
            BuildId = settings.BuildId,
            Reproduced = settings.Reproduced,
            MeasuredOnSupportedBuild = settings.BuildIsSupported,

            // A handle's open or close, or a view's mapping or unmapping, the truth logged, seen in its own process: observed,
            // and bound to the section, its resource instance, where the number it was seen under is the section's alone.
            TruthOperations = truthOperations,
            TruthOperationsWithAdmittedObservation = observedOperations,
            TruthOperationsBoundToResourceInstance = boundOperations,
            PeerAttributions = attributions,
            FalsePeerAttributions = falseAttributions,

            // No record says a byte moved through a view, and a view's size is the extent mapped, so no operation is byte
            // eligible: not measured, never a measured zero (R3, P1, P3).
            ByteEligibleOperations = 0,
            ByteMeasuredOperations = 0,
            TruthResources = found.Count,
            DiscoveredResourcesWithLifetime = discoveredSections,
            ExpectedMemberships = expectedMemberships,
            ResolvedMemberships = resolvedMemberships,

            // The handles and views show which processes share a section and when each mapped it, never a byte moved.
            ClaimsBytesOrOperations = false,
        };

        var notes = new List<string>
        {
            $"{discoveredSections} of {found.Count} truth sections were found whole twice - one number by their handles from the "
            + "creator's creation to every close the truth logged, and one by every view mapped and unmapped - each bound to exactly "
            + "the processes that held them; the two numbers are never joined, as no record joins a section's handles to its views.",
            $"{observedOperations} of {truthOperations} truth handle opens and closes and view mappings and unmappings were seen in "
            + "their own processes; no record says a byte moved through a view, so none is a byte measurement.",
            $"{images} views of images in the truth's processes, and every other view no truth mapping matched, are the processes' "
            + "own, not the truth's.",
            $"{outsideScope} section records were of processes the truth does not name: a machine-wide capture's scope, not error.",
        };

        return new()
        {
            Measurement = measurement,
            Assessment = CoverageTierCalculator.Assess(measurement),
            Sections = found,
            ObservationsOutsideScope = outsideScope,
            Notes = notes,
        };
    }
}
