namespace InterCat.Storage;

/// <summary>§20.1's compaction targets. The defaults are that section's tunable values.</summary>
public sealed record CompactionOptions
{
    public static CompactionOptions Default { get; } = new();

    /// <summary>
    /// A publication unit is small while its observation rows and bytes are both below what a coalesced output should
    /// reach: at least 64,000 rows or 8 MiB.
    /// </summary>
    public int TargetRows { get; init; } = 64_000;

    public long TargetBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>How many small units a session holds before compaction is due: §20.1's 64 live segments.</summary>
    public int SmallUnitsBeforeCompaction { get; init; } = 64;

    /// <summary>
    /// The most observation rows one compaction rewrites, so a live recording's writer is held up for a bounded time.
    /// Zero coalesces every run of small units at once, as an offline compaction does.
    /// </summary>
    public long RowBudget { get; init; }

    public string? Validate() =>
        TargetRows < 1
            ? "A compaction's row target is at least 1."
            : TargetBytes < 4_096
                ? "A compaction's byte target is at least 4 KiB."
                : SmallUnitsBeforeCompaction < 2
                    ? "Compaction coalesces at least two units, so it is due at two small units at the earliest."
                    : RowBudget < 0
                        ? "A compaction's row budget is zero, for none, or positive."
                        : null;
}

/// <summary>
/// The derived files one generation published: its observation and field segments and its dictionaries. A segment
/// resolves its dictionaries through its own generation, so a unit's dictionaries serve only its segments and go with
/// them. <see cref="Rows"/> is null for a unit whose bytes already reach the target, which is not opened to count.
/// </summary>
public sealed record CompactionUnit(long Generation, long Bytes, long? Rows, long? FieldRows, IReadOnlyList<string> Files)
{
    public bool IsSmall(CompactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Rows is { } rows && rows < options.TargetRows && Bytes < options.TargetBytes;
    }
}

/// <summary>What a compaction would coalesce, measured before anything is written.</summary>
/// <param name="Runs">
/// The runs of consecutive small units a compaction coalesces, each into segments of its own, so no output spans the
/// time of a unit it did not coalesce.
/// </param>
public sealed record CompactionPlan(
    long SourceGeneration,
    IReadOnlyList<CompactionUnit> Units,
    IReadOnlyList<IReadOnlyList<CompactionUnit>> Runs,
    int SmallUnits,
    bool Due)
{
    public IEnumerable<CompactionUnit> Coalesced => Runs.SelectMany(run => run);

    /// <summary>Whether anything would be coalesced: at least two units become one set of segments.</summary>
    public bool CompactsAnything => Runs.Count > 0;

    public long CoalescedRows => Coalesced.Sum(unit => unit.Rows ?? 0);

    public long CoalescedFieldRows => Coalesced.Sum(unit => unit.FieldRows ?? 0);

    public long CoalescedBytes => Coalesced.Sum(unit => unit.Bytes);

    public int CoalescedFiles => Coalesced.Sum(unit => unit.Files.Count);

    public int ObservationSegments { get; init; }
}

/// <summary>A published compaction: what was planned, the generation it published and what that released.</summary>
public sealed record CompactionResult(CompactionPlan Plan, DerivedGenerationResult Generation, RetentionOutcome Retention);

/// <summary>
/// Coalesces a session's small segments (§20.1, ADR-026). A live recording publishes a few derived files at every
/// interval, and a long one would leave thousands of small segments to open and re-measure. Compaction rewrites the
/// rows of consecutive small publication units into bounded segments and releases the files they came from. Every row
/// is kept exactly - its raw-record locator, journal index and values - so every observation keeps its identity (I1,
/// I2), and the journals, plan, ledger and committed boundary are never touched.
/// </summary>
public static class SegmentCompaction
{
    /// <summary>
    /// Measures the current generation's publication units. Only a unit whose bytes are below the target is opened to
    /// count its rows. Nothing is written.
    /// </summary>
    public static CompactionPlan Plan(
        SessionStore store,
        CompactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        CompactionOptions bounds = options ?? CompactionOptions.Default;
        if (bounds.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        using EvidenceLease lease = store.AcquireLease();
        return PlanOf(store, lease.Manifest, bounds, cancellationToken);
    }

    /// <summary>
    /// Coalesces what <see cref="Plan"/> selects and publishes the result as one generation, or returns null when
    /// nothing would be coalesced. The previous generation stays current until the new one is complete.
    /// </summary>
    public static CompactionResult? Compact(
        SessionStore store,
        DateTimeOffset committedUtc,
        CompactionOptions? options = null,
        DerivedGenerationOptions? generationOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        CompactionOptions bounds = options ?? CompactionOptions.Default;
        if (bounds.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        // The rows are read under a lease, released before publication so the files they came from can go at once
        // unless another reader holds them (I18).
        CompactionPlan plan;
        DerivedGenerationBuilder? builder = null;
        try
        {
            long rows;
            long fieldRows;
            using (EvidenceLease lease = store.AcquireLease())
            {
                plan = PlanOf(store, lease.Manifest, bounds, cancellationToken);
                if (!plan.CompactsAnything)
                {
                    return null;
                }

                (builder, rows, fieldRows) = ReadRuns(store, lease.Manifest, plan, generationOptions, cancellationToken);
            }

            if (rows != plan.CoalescedRows || fieldRows != plan.CoalescedFieldRows
                || builder.RowCount != rows || builder.FieldRowCount != fieldRows)
            {
                throw new InvalidDataException(
                    $"Compaction read {rows} observation and {fieldRows} field rows where the plan counted "
                    + $"{plan.CoalescedRows} and {plan.CoalescedFieldRows}; nothing was published.");
            }

            long first = plan.Coalesced.Min(unit => unit.Generation);
            long last = plan.Coalesced.Max(unit => unit.Generation);
            string reason =
                FormattableString.Invariant($"Coalesced the derived files of {plan.Coalesced.Count()} publications, ")
                + FormattableString.Invariant($"generations {first} to {last}, into bounded segments: {rows} observation ")
                + FormattableString.Invariant($"and {fieldRows} field rows, every one kept (§20.1 compaction, ADR-026).");
            (DerivedGenerationResult generation, RetentionOutcome retention) = builder.CompleteCompaction(
                [.. plan.Coalesced.SelectMany(unit => unit.Files)],
                reason,
                committedUtc,
                cancellationToken);
            return new(plan, generation, retention);
        }
        finally
        {
            builder?.Dispose();
        }
    }

    /// <summary>
    /// Adds every row of the planned runs to a compaction builder, observations before fields within each unit. Each run
    /// ends its segments, so no output spans the time of a unit the compaction did not coalesce.
    /// </summary>
    private static (DerivedGenerationBuilder Builder, long Rows, long FieldRows) ReadRuns(
        SessionStore store,
        SessionManifestV1 manifest,
        CompactionPlan plan,
        DerivedGenerationOptions? generationOptions,
        CancellationToken cancellationToken)
    {
        DerivedGenerationBuilder? builder = null;
        long rows = 0;
        long fieldRows = 0;
        try
        {
            foreach (IReadOnlyList<CompactionUnit> run in plan.Runs)
            {
                foreach (CompactionUnit unit in run)
                {
                    foreach (string name in unit.Files.Where(IsObservationSegment))
                    {
                        SegmentReaderV1 segment = Open(name);
                        for (int row = 0; row < segment.RowCount; row++)
                        {
                            builder!.AddRow(segment.Row(row));
                        }

                        rows += segment.RowCount;
                    }

                    foreach (string name in unit.Files.Where(IsFieldSegment))
                    {
                        SegmentReaderV1 segment = Open(name);
                        for (int row = 0; row < segment.RowCount; row++)
                        {
                            builder!.AddFieldRow(segment.FieldRow(row));
                        }

                        fieldRows += segment.RowCount;
                    }
                }

                builder?.FlushSegment();
            }

            return (builder ?? throw new InvalidDataException("The planned runs hold no segment to compact."), rows, fieldRows);
        }
        catch
        {
            builder?.Dispose();
            throw;
        }

        // Every segment of a compaction is one capture's, on one clock, under one derivation.
        SegmentReaderV1 Open(string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
            builder ??= DerivedGenerationBuilder.BeginCompaction(store, IdentityOf(segment), manifest.Generation, generationOptions);
            RequireIdentity(builder, segment, name);
            return segment;
        }
    }

    private static CompactionPlan PlanOf(
        SessionStore store,
        SessionManifestV1 manifest,
        CompactionOptions options,
        CancellationToken cancellationToken)
    {
        var files = new SortedDictionary<long, List<StoreDependency>>();
        foreach (StoreDependency dependency in manifest.Dependencies)
        {
            if (dependency.Kind is StoreDependencyKind.Segment or StoreDependencyKind.Dictionary
                && SessionSegments.PublishingGeneration(dependency.Name) is { } generation)
            {
                if (!files.TryGetValue(generation, out List<StoreDependency>? unit))
                {
                    files[generation] = unit = [];
                }

                unit.Add(dependency);
            }
        }

        var units = new List<CompactionUnit>(files.Count);
        int observationSegments = 0;
        foreach ((long generation, List<StoreDependency> dependencies) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoreDependency[] observations = [.. dependencies.Where(dependency => IsObservationSegment(dependency.Name))];
            observationSegments += observations.Length;
            if (observations.Length == 0)
            {
                // A generation whose files hold no observations - fields or dictionaries alone - is not a unit a
                // compaction understands, so it is left exactly as it is.
                continue;
            }

            long bytes = observations.Sum(dependency => dependency.LengthBytes);
            long? rows = null;
            long? fieldRows = null;
            if (bytes < options.TargetBytes)
            {
                // A plan needs counts, not rows: each header's checksummed count, which the compaction's own reads of
                // these segments then hold to their columns.
                rows = observations.Sum(dependency =>
                    (long)SessionSegments.DeclaredRowCount(store.Root, manifest, dependency.Name));
                fieldRows = dependencies
                    .Where(dependency => IsFieldSegment(dependency.Name))
                    .Sum(dependency => (long)SessionSegments.DeclaredRowCount(store.Root, manifest, dependency.Name));
            }

            units.Add(new(
                generation,
                bytes,
                rows,
                fieldRows,
                [.. dependencies.Select(dependency => dependency.Name).Order(StringComparer.Ordinal)]));
        }

        int small = units.Count(unit => unit.IsSmall(options));
        return new(manifest.Generation, units, SelectRuns(units, options), small, small >= options.SmallUnitsBeforeCompaction)
        {
            ObservationSegments = observationSegments,
        };
    }

    /// <summary>
    /// The runs of two or more consecutive small units, oldest first. Under a row budget only the oldest run is taken,
    /// cut at the budget, so each step of a live compaction rewrites a bounded number of rows.
    /// </summary>
    private static List<IReadOnlyList<CompactionUnit>> SelectRuns(List<CompactionUnit> units, CompactionOptions options)
    {
        var runs = new List<IReadOnlyList<CompactionUnit>>();
        var run = new List<CompactionUnit>();
        void Close()
        {
            if (run.Count >= 2)
            {
                runs.Add([.. run]);
            }

            run.Clear();
        }

        foreach (CompactionUnit unit in units)
        {
            if (!unit.IsSmall(options))
            {
                Close();
                continue;
            }

            if (options.RowBudget > 0 && run.Count >= 2 && run.Sum(member => member.Rows!.Value) + unit.Rows!.Value > options.RowBudget)
            {
                Close();
                break;
            }

            run.Add(unit);
        }

        Close();
        return options.RowBudget > 0 ? [.. runs.Take(1)] : runs;
    }

    private static SegmentIdentityV1 IdentityOf(SegmentReaderV1 segment) => new()
    {
        CaptureId = segment.CaptureId,
        ClockId = segment.ClockId,
        TimestampEncoding = segment.TimestampEncoding,
        Derivation = segment.Derivation,
    };

    private static void RequireIdentity(DerivedGenerationBuilder builder, SegmentReaderV1 segment, string name)
    {
        if (builder.Identity != IdentityOf(segment))
        {
            throw new InvalidDataException(
                $"'{name}' belongs to another capture, clock or derivation than the segments before it. A compaction "
                + "coalesces one derivation of one capture, so nothing was published.");
        }
    }

    private static bool IsObservationSegment(string name) => name.StartsWith("seg-", StringComparison.Ordinal);

    private static bool IsFieldSegment(string name) => name.StartsWith("fld-", StringComparison.Ordinal);
}
