using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// One metric request, resolved against §5.3's matrix before it is planned. It names its basis, its metric,
/// the domain and side where those apply, the projection it answers over and the interval it is scoped to.
/// </summary>
/// <remarks>
/// The member names follow §23's specification order, so the canonical form IC-018 freezes can be built from
/// this record without renaming anything.
/// </remarks>
public sealed record MetricRequest
{
    public required AnalysisBasis Basis { get; init; }

    public required Metric Metric { get; init; }

    public ByteDomain? ByteDomain { get; init; }

    public AccountingSide? AccountingSide { get; init; }

    /// <summary>The metric a rate divides. Null for anything that is not a rate.</summary>
    public Metric? RateNumerator { get; init; }

    /// <summary>Restricts the request to one layer. Null means every layer the session carries.</summary>
    public ObservationLayer? Layer { get; init; }

    /// <summary>Restricts the request to one mechanism. Null means every mechanism the session carries.</summary>
    public Mechanism? Mechanism { get; init; }

    /// <summary>
    /// The analysis interval, half-open (I3), in the native ticks of the clock the session's segments are on.
    /// Null scopes the request to the whole retained capture. A rate divides by the whole of it, never by a
    /// shorter apparently healthy part of it (§19.2).
    /// </summary>
    public TimeRange? Interval { get; init; }

    /// <summary>`EN-TimeScope`: an interval scopes the request to it; no interval is the retained capture.</summary>
    public TimeScope TimeScope => Interval is null ? TimeScope.RetainedCapture : TimeScope.AnalysisInterval;

    /// <summary>`EN-Grouping`: how the total is broken down. Null answers one total.</summary>
    public LaneGrouping? Grouping { get; init; }

    /// <summary>
    /// `EN-EvidencePolicy`: which binding strengths a grouped total admits. The default admits direct and correlated
    /// bindings, so a record of a reused PID is not attributed to one of its instances unless candidates are asked for.
    /// </summary>
    public EvidencePolicy EvidencePolicy { get; init; } = EvidencePolicy.IncludeCorrelated;

    /// <summary>`requestedRows`: how many ranked groups to return. The rest are one exact remainder (§5.2).</summary>
    public int? RequestedRows { get; init; }

    /// <summary>The reason this request means nothing, or null when it means something.</summary>
    public MetricRejection? Check()
    {
        if (Grouping is { } grouping && !Enum.IsDefined(grouping))
        {
            return new("A request names a grouping §23 defines.", []);
        }

        if (!Enum.IsDefined(EvidencePolicy))
        {
            return new("A request names an evidence policy §23 defines.", []);
        }

        if (RequestedRows is { } rows && (rows < 1 || Grouping is null))
        {
            return new(
                Grouping is null
                    ? "Requested rows limit a grouped result; an ungrouped result is one row."
                    : "A grouped result returns at least one ranked row.",
                []);
        }

        return MetricCompatibility.Check(Basis, Metric, ByteDomain, AccountingSide, Layer, RateNumerator);
    }

    /// <summary>
    /// This request with every default it leaves implicit written out, so two spellings of one request become
    /// one request (§10.5). It assumes the request was accepted by <see cref="Check"/>.
    /// </summary>
    public MetricRequest Materialized()
    {
        (ByteDomain? domain, AccountingSide? side, ObservationLayer? layer) =
            MetricCompatibility.Materialize(Metric, ByteDomain, AccountingSide, Layer, RateNumerator);
        return this with { ByteDomain = domain, AccountingSide = side, Layer = layer };
    }
}

/// <summary>How much of the evidence behind an answer to hand back beside it.</summary>
public sealed record MetricEvaluationOptions
{
    public static MetricEvaluationOptions Default { get; } = new();

    /// <summary>
    /// How many of the records the answer counted to return, in the order the segments hold them. Zero returns
    /// none. Every number navigates to its evidence (§3.2); this is the one-step path from a total to the rows.
    /// </summary>
    public int EvidenceLimit { get; init; }
}

/// <summary>Why a request the matrix permits still cannot be answered from this session.</summary>
public enum MetricUnavailableReason
{
    /// <summary>It can be answered.</summary>
    None = 0,

    /// <summary>Nothing in this session derives operations yet.</summary>
    NoLogicalOperations = 1,

    /// <summary>Nothing in this session derives resources or memberships yet.</summary>
    NoResourceTopology = 2,

    /// <summary>The entity bindings a peer or channel count needs do not exist yet.</summary>
    NoEntityBindings = 3,

    /// <summary>No enumeration states what a source's status code means, so nothing decides what an error is.</summary>
    NoStatusDomain = 4,

    /// <summary>A rate needs the interval it divides, and the request named none.</summary>
    NoInterval = 5,

    /// <summary>This generation publishes no derived segment to read.</summary>
    NoDerivedData = 6,

    /// <summary>A canonical owner needs a proven transfer association, and no correlator has produced one.</summary>
    NoTransferAssociations = 7,

    /// <summary>
    /// Nothing in scope measured this domain: there is no declared slot, or every declared slot was unknown. A
    /// byte sum of nothing is not an observed zero (R3, R21, P1).
    /// </summary>
    NothingMeasured = 8,

    /// <summary>The grouping asked for needs a derivation this session does not have, such as image names.</summary>
    GroupingNotDerived = 9,
}

/// <summary>What one group of a grouped result is a group of.</summary>
public enum MetricGroupKind
{
    /// <summary>One process instance.</summary>
    ProcessInstance = 1,

    /// <summary>One mechanism.</summary>
    Mechanism = 2,

    /// <summary>Contributions that could not be attributed to a group, for one stated reason. Never a peer (§5.2).</summary>
    Unattributed = 3,

    /// <summary>Every ranked group past the requested rows, summed exactly. A grouping, not a peer (§5.2).</summary>
    Remainder = 4,

    /// <summary>All process instances whose witnessed full image path is the same.</summary>
    Executable = 5,
}

/// <summary>
/// One group of a grouped result. A group's value is computed exactly as the ungrouped total is, over the records
/// that belong to it, so the groups of a result partition its total.
/// </summary>
public sealed record MetricGroup
{
    public required MetricGroupKind Kind { get; init; }

    /// <summary>The instance, for a process group.</summary>
    public ProcessInstance? Process { get; init; }

    /// <summary>The witnessed image path, for an executable group. A name-only exit is not a path identity.</summary>
    public string? Executable { get; init; }

    /// <summary>The mechanism, for a mechanism group.</summary>
    public Mechanism? Mechanism { get; init; }

    /// <summary>Why the group's contributions are unattributed, for an unattributed group.</summary>
    public ProcessBindingReason? Reason { get; init; }

    /// <summary>The group's rank among ranked groups, from 1, or null for an unmeasured, unattributed or remainder group.</summary>
    public int? Rank { get; init; }

    /// <summary>A count, or a byte sum; null when nothing the group holds was measured.</summary>
    public long? Value { get; init; }

    public long KnownContributions { get; init; }

    public long UnknownContributions { get; init; }

    /// <summary>The group's contributions by the row side each was measured at, taken or not.</summary>
    public IReadOnlyList<SideMeasurement> Sides { get; init; } = [];

    /// <summary>The group's counted records by how strongly each is bound to it, for a process group.</summary>
    public IReadOnlyDictionary<RelationStrength, long> Bindings { get; init; } = new Dictionary<RelationStrength, long>();

    /// <summary>A rate's per-group value, when the request is a rate.</summary>
    public MetricRate? Rate { get; init; }

    /// <summary>How many groups a remainder holds.</summary>
    public int GroupsMerged { get; init; }

    public double? MeasurementAvailability =>
        KnownContributions + UnknownContributions == 0
            ? null
            : (double)KnownContributions / (KnownContributions + UnknownContributions);
}

/// <summary>
/// A rate, kept as the integers it is made of. No ratio is ever stored as a float (§10.5): the per-second value is
/// derived from these for presentation, and a caller that needs another unit scales the pair.
/// </summary>
public sealed record MetricRate
{
    /// <summary>The count or byte sum over the interval.</summary>
    public required long Numerator { get; init; }

    public required MeasurementUnit NumeratorUnit { get; init; }

    /// <summary>The whole selected interval, in native ticks.</summary>
    public required long IntervalTicks { get; init; }

    /// <summary>The clock's tick rate, or null when the session does not describe its clock.</summary>
    public long? TicksPerSecond { get; init; }

    /// <summary>The unit of <see cref="PerSecond"/>: per second for a count or a byte sum.</summary>
    public MeasurementUnit? Unit => NumeratorUnit switch
    {
        MeasurementUnit.Bytes => MeasurementUnit.BytesPerSecond,
        MeasurementUnit.Count => MeasurementUnit.CountPerSecond,
        _ => null,
    };

    /// <summary>
    /// The rate per second, rounded half to even to three decimal places for presentation only, or null when the
    /// clock's rate is not known. The exact value is <c>Numerator × TicksPerSecond / IntervalTicks</c>.
    /// </summary>
    public decimal? PerSecond => TicksPerSecond is { } rate
        ? decimal.Round((decimal)Numerator * rate / IntervalTicks, 3, MidpointRounding.ToEven)
        : null;
}

/// <summary>One record an answer counted, with the identities that let a caller navigate to it.</summary>
public sealed record MetricEvidence
{
    /// <summary>The published segment the record's row is in.</summary>
    public required string Segment { get; init; }

    /// <summary>The row's position in that segment, which is only an address inside this generation.</summary>
    public required int Row { get; init; }

    /// <summary>The observation identity of §7.2, which survives every re-read, replay and re-derivation (I1, I2).</summary>
    public required ObservationId ObservationId { get; init; }

    public required ObservationRowV1 Observation { get; init; }
}

/// <summary>
/// One answer, with everything needed to read it. A total is only readable beside what it excluded and what it
/// could not measure, so the exclusions and the unknowns are part of the result rather than a footnote
/// (R2, R3, I6).
/// </summary>
public sealed record MetricResult
{
    /// <summary>The request as answered, with every default materialized.</summary>
    public required MetricRequest Request { get; init; }

    /// <summary>The generation this answers, so a result names the snapshot it came from (I16).</summary>
    public required long Generation { get; init; }

    public required MetricUnavailableReason Unavailable { get; init; }

    /// <summary>What is missing, when the request is permitted but unanswerable here.</summary>
    public string? UnavailableExplanation { get; init; }

    /// <summary>
    /// The value: a count, or a byte sum in <see cref="Unit"/>. Null when it is unavailable, and null for a rate,
    /// whose answer is <see cref="Rate"/>. A byte sum is never zero for want of a measurement: zero is an observed
    /// zero, and a sum with no known contribution is <see cref="MetricUnavailableReason.NothingMeasured"/>.
    /// </summary>
    public long? Value { get; init; }

    public MeasurementUnit? Unit { get; init; }

    public MetricRate? Rate { get; init; }

    /// <summary>The row labels this total took, per the request's accounting. Empty for a count.</summary>
    public IReadOnlyList<AccountingSide> TakenSides { get; init; } = [];

    /// <summary>
    /// Every row label met inside the requested domain and scope, with its own sum and unknowns — the ones taken
    /// and the ones left out. Two labels are never added here; <see cref="TakenSides"/> says which the total is.
    /// </summary>
    public IReadOnlyList<SideMeasurement> Sides { get; init; } = [];

    public long KnownContributions { get; init; }

    public long UnknownContributions { get; init; }

    /// <summary>Why each unknown contribution the total would have taken is unknown (§7.3).</summary>
    public IReadOnlyDictionary<FieldAvailability, long> UnknownReasons { get; init; } =
        new Dictionary<FieldAvailability, long>();

    /// <summary>Rows in scope whose declared slot is in another domain, by that domain. Never summed (P3).</summary>
    public IReadOnlyDictionary<ByteDomain, long> OtherDomains { get; init; } = new Dictionary<ByteDomain, long>();

    public long ExcludedOtherDomain { get; init; }

    /// <summary>Contributions in this domain whose row label the accounting does not take. Visible, not added.</summary>
    public long ExcludedOtherSide { get; init; }

    public long ExcludedNoDeclaredSlot { get; init; }

    public long ExcludedByProjection { get; init; }

    public long ExcludedOutsideInterval { get; init; }

    /// <summary>How many segments were read, so a total names the breadth it covers.</summary>
    public int Segments { get; init; }

    /// <summary>How many rows the generation's segments hold in all, in scope or not.</summary>
    public long RowsRead { get; init; }

    /// <summary>
    /// The normalizer derivations the answer read, so a result names the revision of the facts it counted as well
    /// as the generation that published them (I16, §24's <c>normalizerContract</c> axis).
    /// </summary>
    public IReadOnlyList<NormalizerContractVersion> Derivations { get; init; } = [];

    /// <summary>The clock the session's native readings are on, when the session describes it.</summary>
    public SourceClockDescriptor? Clock { get; init; }

    /// <summary>The earliest and latest native reading the generation holds, whatever the request's scope.</summary>
    public long? FirstNativeTicks { get; init; }

    public long? LastNativeTicks { get; init; }

    /// <summary>The first records the answer counted, when evidence was asked for.</summary>
    public IReadOnlyList<MetricEvidence> Evidence { get; init; } = [];

    /// <summary>The ranked groups of a grouped result, then the unmeasured ones; empty when ungrouped.</summary>
    public IReadOnlyList<MetricGroup> Groups { get; init; } = [];

    /// <summary>Every ranked group past the requested rows, summed exactly, or null when none was cut.</summary>
    public MetricGroup? Remainder { get; init; }

    /// <summary>Contributions no group could take, one group per reason.</summary>
    public IReadOnlyList<MetricGroup> Unattributed { get; init; } = [];

    /// <summary>
    /// Whether the groups, the remainder and the unattributed groups together hold every contribution the ungrouped
    /// total holds, each exactly once. When they do, their values add up to <see cref="Value"/>; when they do not, a
    /// sum of groups is not a total and is never shown as one (§3.2, §5.1).
    /// </summary>
    public bool GroupsPartitionTotal { get; init; }

    /// <summary>The binding rule a process grouping was derived under, so a result names its entity revision (I16).</summary>
    public string? BindingRule { get; init; }

    /// <summary>Notes a caller must show beside the number. They are part of the answer, not decoration.</summary>
    public IReadOnlyList<string> Caveats { get; init; } = [];

    public bool IsAvailable => Unavailable == MetricUnavailableReason.None;

    /// <summary>
    /// The share of declared slots the total would take that carried a value, or null when there are none. It is
    /// measurement availability: how much of what could be measured was, never how much traffic was captured (§5).
    /// </summary>
    public double? MeasurementAvailability =>
        KnownContributions + UnknownContributions == 0
            ? null
            : (double)KnownContributions / (KnownContributions + UnknownContributions);

    public override string ToString() => IsAvailable
        ? Rate is { } rate
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{Request.Metric} of {Request.RateNumerator} = {rate.Numerator} over {rate.IntervalTicks} ticks, generation {Generation}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Request.Metric} = {Value} {Unit?.ToString() ?? "count"} over generation {Generation}")
        : string.Create(CultureInfo.InvariantCulture, $"{Request.Metric} unavailable: {Unavailable}");
}

/// <summary>
/// Answers a metric request over every segment of one published generation. It is the §19.1 query compiler's
/// accounting half: the request is resolved against §5.3's matrix first, then answered from the segments, and a
/// request the matrix permits but this session cannot derive is reported as unavailable with what it needs —
/// never approximated from what is there.
/// </summary>
/// <remarks>
/// It reads under an evidence lease and reads the manifest that lease holds, so neither a retention nor a commit
/// can change what it is reading mid-answer (I16, I18). Scheduling, supersession, cancellation and caching are
/// IC-017's; this fixes what the numbers mean.
/// </remarks>
public static partial class SessionMetrics
{
    /// <summary>
    /// Answers a request against the store's current generation, holding it under a lease for the duration.
    /// </summary>
    public static MetricResult Evaluate(
        SessionStore store,
        MetricRequest request,
        MetricEvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        MetricEvaluationOptions bounds = options ?? MetricEvaluationOptions.Default;
        if (bounds.EvidenceLimit is < 0 or > SegmentMeasurement.MaximumEvidenceRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                bounds.EvidenceLimit,
                $"An evidence listing returns between 0 and {SegmentMeasurement.MaximumEvidenceRows} records.");
        }

        MetricRejection? rejection = request.Check();
        if (rejection is not null)
        {
            throw new ArgumentException(rejection.ToString(), nameof(request));
        }

        MetricRequest materialized = request.Materialized();
        if (store.Current is null)
        {
            throw new InvalidOperationException(
                "This session has published no generation, so there is nothing to answer from. An empty "
                + "session is not a generation with no data.");
        }

        // The generation and every dependency it names are held for the whole answer, and the manifest read is
        // the one the lease holds, so neither retention nor a later commit changes what is being read (I18).
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        IReadOnlyList<string> names = SessionSegments.Names(manifest);
        if (names.Count == 0)
        {
            return Unavailable(
                materialized,
                manifest.Generation,
                MetricUnavailableReason.NoDerivedData,
                $"Generation {manifest.Generation} publishes evidence and no derived segment, so there is "
                + "nothing above it to count.");
        }

        MetricResult? blocked = WhatIsMissing(materialized, manifest.Generation);
        if (blocked is not null)
        {
            return blocked;
        }

        var segments = new List<(string Name, SegmentReaderV1 Reader)>(names.Count);
        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segments.Add((name, SessionSegments.Open(store.Root, manifest, name)));
        }

        SourceClockDescriptor? clock = ReadClock(store, manifest, segments, materialized);
        var context = new Context(materialized, manifest.Generation, segments, clock, bounds.EvidenceLimit);
        if (materialized.Grouping is { } grouping)
        {
            if (grouping is LaneGrouping.InstanceOnly or LaneGrouping.Executable)
            {
                // Instances need the capture's source fields - start keys, parents, sessions - when the generation
                // publishes them; a generation derived before they existed publishes none and is keyed without them.
                context = context with
                {
                    FieldSegments =
                    [
                        .. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name)),
                    ],
                };
            }

            if (bounds.EvidenceLimit > 0)
            {
                throw new ArgumentException(
                    "Evidence lists the records of one total. Ask for it without a grouping, projected onto the group "
                    + "whose records you want to see.",
                    nameof(options));
            }

            return WhatGroupingNeeds(materialized, manifest.Generation, clock)
                ?? Grouped(context, grouping, cancellationToken);
        }

        return materialized.Metric switch
        {
            Metric.Observations => Count(context, materialized, cancellationToken),
            Metric.Rate => Rate(context, cancellationToken),
            _ => Bytes(context, materialized, cancellationToken),
        };
    }

    /// <summary>
    /// What a permitted request still needs before this session can answer it. The distinction matters: a
    /// request outside §5.3's matrix means nothing, while one inside it that this session cannot derive is a
    /// gap with a name.
    /// </summary>
    private static MetricResult? WhatIsMissing(MetricRequest request, long generation)
    {
        if (request.Basis == AnalysisBasis.LogicalOperations)
        {
            return Unavailable(
                request,
                generation,
                MetricUnavailableReason.NoLogicalOperations,
                "Nothing in this session derives logical operations yet. §5.3 defines this metric on that "
                + "basis; the correlators that produce one do not exist, so it is reported as unavailable "
                + "rather than answered from source records renamed as operations (P4).");
        }

        if (request.Basis == AnalysisBasis.ResourceTopology)
        {
            return Unavailable(
                request,
                generation,
                MetricUnavailableReason.NoResourceTopology,
                "Nothing in this session derives resources or memberships yet, so a topology basis has no "
                + "rows. A resource projection built from source endpoint fields alone would establish "
                + "identity from reusable values, which R22 forbids.");
        }

        Metric effective = request.Metric == Metric.Rate ? request.RateNumerator!.Value : request.Metric;
        if (effective is Metric.ActiveChannels or Metric.ActivePeers)
        {
            return Unavailable(
                request,
                generation,
                MetricUnavailableReason.NoEntityBindings,
                $"{effective} counts distinct entity instances, and this session publishes source facts only. "
                + "Counting distinct process ids or address and port pairs instead would establish identity from "
                + "reusable numeric values, which R22 forbids. It needs the process, endpoint and channel instances "
                + "an entity derivation binds, and this session has none yet.");
        }

        if (effective == Metric.Errors)
        {
            return Unavailable(
                request,
                generation,
                MetricUnavailableReason.NoStatusDomain,
                "A status code is stored with its availability and no domain, because §7.3 names a status "
                + "domain that §23 assigns no enumeration. Nothing states which codes are errors, so the "
                + "count is unavailable rather than guessed from non-zero values.");
        }

        if (request.AccountingSide == AccountingSide.CanonicalOwner)
        {
            return Unavailable(
                request,
                generation,
                MetricUnavailableReason.NoTransferAssociations,
                "A canonical-owner total counts each proven transfer association once, by its owning "
                + "contribution (§5.3). No correlator has proven an association in this session, so there is "
                + "nothing to choose an owner for. Sender- or receiver-accounted totals answer from the records "
                + "themselves and need no association.");
        }

        return request.Metric == Metric.Rate && request.Interval is null
            ? Unavailable(
                request,
                generation,
                MetricUnavailableReason.NoInterval,
                "A rate divides by the whole selected interval, and the request named none. §19.2 forbids "
                + "dividing by a shorter apparently healthy interval and labelling the result a whole-interval "
                + "rate, and the span between the first and last observation is exactly such an interval, so "
                + "there is no default.")
            : null;
    }

    /// <summary>
    /// Refuses a generation whose segments cannot be read together, and reads the clock their readings are on.
    /// </summary>
    private static SourceClockDescriptor? ReadClock(
        SessionStore store,
        SessionManifestV1 manifest,
        IReadOnlyList<(string Name, SegmentReaderV1 Reader)> segments,
        MetricRequest request)
    {
        // Two derivations of one capture describe the same evidence twice. Summing them would count every
        // record once per derivation, so a generation that names both is refused rather than totalled (I2, R20).
        var derivations = new Dictionary<CaptureId, NormalizerContractVersion>();
        var clocks = new HashSet<ClockId>();
        foreach ((string name, SegmentReaderV1 reader) in segments)
        {
            if (derivations.TryGetValue(reader.CaptureId, out NormalizerContractVersion seen) && seen != reader.Derivation)
            {
                throw new InvalidDataException(
                    $"Generation {manifest.Generation} names segments of capture {reader.CaptureId} under "
                    + $"normalizer v{seen.Value} and v{reader.Derivation.Value} ('{name}'). Two derivations of one "
                    + "capture describe the same evidence twice, and a total over both would count it twice.");
            }

            derivations[reader.CaptureId] = reader.Derivation;
            _ = clocks.Add(reader.ClockId);
        }

        if (request.Interval is not null && clocks.Count > 1)
        {
            throw new ArgumentException(
                $"An interval in native ticks names one clock, and generation {manifest.Generation} holds readings "
                + $"on {clocks.Count}. Scoping them all by one tick range would compare readings of different "
                + "clocks as though they were one (I8).");
        }

        SourceClockDescriptor? clock = SessionSegments.SourceClock(store.Root, manifest);
        return clock is { } described && clocks.Count == 1 && !clocks.Contains(described.Id)
            ? throw new InvalidDataException(
                $"The segments of generation {manifest.Generation} are on clock {clocks.Single()} and the journal "
                + $"they derive from describes clock {described.Id}. Readings are never interpreted against a clock "
                + "that did not produce them (I8).")
            : clocks.Count == 1 ? clock : null;
    }

    private static MetricResult Count(Context context, MetricRequest request, CancellationToken cancellationToken)
    {
        long count = 0;
        long outsideProjection = 0;
        long outsideInterval = 0;
        var evidence = new List<MetricEvidence>();
        foreach ((string name, SegmentReaderV1 reader) in context.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservationCount counted = SegmentMeasurement.CountObservations(
                reader,
                request.Layer,
                request.Mechanism,
                request.Interval,
                Math.Max(0, context.EvidenceLimit - evidence.Count));
            count += counted.Counted;
            outsideProjection += counted.ExcludedByProjection;
            outsideInterval += counted.ExcludedOutsideInterval;
            evidence.AddRange(counted.EvidenceRows.Select(row => Evidence(name, reader, row)));
        }

        var caveats = new List<string>
        {
            "An observation count is sensitive to instrumentation density and says nothing about volume (§5). It is "
            + "not an operation count.",
        };
        if (count == 0)
        {
            caveats.Add(
                "No observation is in scope. That is a count of records, not a finding that nothing happened: "
                + "coverage is stated separately from data, and this session publishes no coverage ledger yet (R21).");
        }

        return context.Answer(request) with
        {
            Value = count,
            Unit = MeasurementUnit.Count,
            KnownContributions = count,
            ExcludedByProjection = outsideProjection,
            ExcludedOutsideInterval = outsideInterval,
            Evidence = evidence,
            Caveats = caveats,
        };
    }

    private static MetricResult Bytes(Context context, MetricRequest request, CancellationToken cancellationToken)
    {
        ByteDomain domain = request.ByteDomain!.Value;
        AccountingSide accounting = request.AccountingSide!.Value;
        IReadOnlyList<AccountingSide> taken = MetricCompatibility.RowSidesTakenBy(accounting);

        var totals = new SortedDictionary<AccountingSide, SideTotal>();
        var otherDomains = new Dictionary<ByteDomain, long>();
        long otherDomain = 0;
        long noSlot = 0;
        long outsideProjection = 0;
        long outsideInterval = 0;
        MeasurementUnit? unit = null;
        var evidence = new List<MetricEvidence>();
        foreach ((string name, SegmentReaderV1 reader) in context.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DomainMeasurement measured = SegmentMeasurement.MeasureDomain(
                reader,
                new()
                {
                    Domain = domain,
                    Layer = request.Layer,
                    Mechanism = request.Mechanism,
                    Interval = request.Interval,
                    EvidenceSides = taken,
                    EvidenceLimit = Math.Max(0, context.EvidenceLimit - evidence.Count),
                });
            if (measured.Unit is { } segmentUnit)
            {
                if (unit is { } established && established != segmentUnit)
                {
                    throw new InvalidDataException(
                        $"Segments of generation {context.Generation} measure {domain} in {established} and "
                        + $"{segmentUnit}. Two units are not summed and are not converted here.");
                }

                unit = segmentUnit;
            }

            foreach (SideMeasurement side in measured.Sides)
            {
                if (!totals.TryGetValue(side.Side, out SideTotal? running))
                {
                    running = new();
                    totals[side.Side] = running;
                }

                running.Add(side);
            }

            foreach ((ByteDomain other, long count) in measured.OtherDomains)
            {
                otherDomains[other] = (otherDomains.TryGetValue(other, out long already) ? already : 0) + count;
            }

            otherDomain += measured.ExcludedOtherDomain;
            noSlot += measured.ExcludedNoDeclaredSlot;
            outsideProjection += measured.ExcludedByProjection;
            outsideInterval += measured.ExcludedOutsideInterval;
            evidence.AddRange(measured.EvidenceRows.Select(row => Evidence(name, reader, row)));
        }

        IReadOnlyList<SideMeasurement> sides = [.. totals.Select(entry => entry.Value.ToMeasurement(entry.Key))];

        long value = 0;
        long knownTaken = 0;
        long unknownTaken = 0;
        long otherSide = 0;
        var unknownReasons = new Dictionary<FieldAvailability, long>();
        foreach (SideMeasurement side in sides)
        {
            if (!taken.Contains(side.Side))
            {
                otherSide += side.DeclaredContributions;
                continue;
            }

            value = checked(value + side.TotalBytes);
            knownTaken += side.KnownContributions;
            unknownTaken += side.UnknownContributions;
            foreach ((FieldAvailability reason, long count) in side.UnknownReasons)
            {
                unknownReasons[reason] = unknownReasons.TryGetValue(reason, out long already) ? already + count : count;
            }
        }

        MetricResult answer = context.Answer(request) with
        {
            Unit = unit ?? MeasurementUnit.Bytes,
            TakenSides = taken,
            Sides = sides,
            KnownContributions = knownTaken,
            UnknownContributions = unknownTaken,
            UnknownReasons = unknownReasons,
            OtherDomains = otherDomains,
            ExcludedOtherDomain = otherDomain,
            ExcludedOtherSide = otherSide,
            ExcludedNoDeclaredSlot = noSlot,
            ExcludedByProjection = outsideProjection,
            ExcludedOutsideInterval = outsideInterval,
            Evidence = evidence,
        };

        // A sum with no known contribution is not a zero: it is a statement that nothing in scope measured this
        // domain, and presenting it as "0 B" would render absence as observed zero activity (R3, R21, P1).
        if (knownTaken == 0)
        {
            return answer with
            {
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation = NothingMeasured(request, answer),
                Unit = null,
            };
        }

        return answer with { Value = value, Caveats = ByteCaveats(request, answer) };
    }

    private static MetricResult Rate(Context context, CancellationToken cancellationToken)
    {
        MetricRequest request = context.Request;
        TimeRange interval = request.Interval!.Value;
        MetricRequest numerator = request with { Metric = request.RateNumerator!.Value, RateNumerator = null };
        MetricResult inner = numerator.Metric == Metric.Observations
            ? Count(context, numerator, cancellationToken)
            : Bytes(context, numerator, cancellationToken);

        if (!inner.IsAvailable)
        {
            return inner with { Request = request };
        }

        // The denominator is the whole selected interval. §19.2 forbids dividing by a shorter apparently healthy
        // part of it and calling the result the whole-interval rate, and no covered-time variant is offered
        // without the source-specific exposure it needs.
        var rate = new MetricRate
        {
            Numerator = inner.Value!.Value,
            NumeratorUnit = inner.Unit ?? MeasurementUnit.Count,
            IntervalTicks = interval.SpanTicks,
            TicksPerSecond = context.Clock?.TicksPerSecond,
        };
        var caveats = new List<string>(inner.Caveats)
        {
            "The denominator is the whole selected interval. A rate is never divided by a shorter "
            + "healthy-looking part of it (§19.2).",
            "Coverage defects over this interval are not known here: nothing in this session publishes a coverage "
            + "ledger yet, so this is an observed rate and not a corrected one.",
        };
        if (context.Clock is null)
        {
            caveats.Add(
                "The session does not describe the clock its readings are on, so the rate is stated per native tick "
                + "and not per second (I8).");
        }

        return inner with
        {
            Request = request,
            Value = null,
            Unit = rate.Unit,
            Rate = rate,
            Caveats = caveats,
        };
    }

    private static string NothingMeasured(MetricRequest request, MetricResult answer)
    {
        string domain = request.ByteDomain!.Value.ToString();
        if (answer.UnknownContributions > 0)
        {
            return $"All {answer.UnknownContributions:N0} declared {domain} measurements this total would take are "
                + "unknown, so there is no measured value to report. They are counted as unknown and never as zero (R3).";
        }

        string elsewhere = answer.OtherDomains.Count > 0
            ? " Records in scope do measure "
                + string.Join(
                    ", ",
                    answer.OtherDomains.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key} ({entry.Value:N0})"))
                + ", which are other quantities and are never relabelled as this one (P3)."
            : string.Empty;
        string sides = answer.ExcludedOtherSide > 0
            ? $" {answer.ExcludedOtherSide:N0} {domain} contributions are labelled with a side this accounting does "
                + "not take."
            : string.Empty;
        return $"Nothing in scope measured {domain} bytes under this accounting: no record the total would take "
            + "declares that slot. A sum of nothing is not an observed zero (R21, P1)." + sides + elsewhere;
    }

    private static List<string> ByteCaveats(MetricRequest request, MetricResult answer)
    {
        // The accounting's own meaning travels with the request (MetricCompatibility.Describe), so it is not
        // repeated here; these are the notes that change how this particular number may be read.
        var caveats = new List<string>();
        if (request.Metric == Metric.EndpointActivityBytes)
        {
            caveats.Add(
                "Endpoint activity counts a local transfer at both of its ends by design, so it is not a transfer "
                + "total and is never compared with one (§5.1).");
        }
        else if (request.Metric is Metric.BytesSent or Metric.BytesReceived)
        {
            bool crossSide = (request.Metric == Metric.BytesSent && request.AccountingSide == AccountingSide.ReceiveSide)
                || (request.Metric == Metric.BytesReceived && request.AccountingSide == AccountingSide.SendSide);
            caveats.Add(
                crossSide
                    ? $"These are {(request.Metric == Metric.BytesSent ? "sent" : "received")} bytes measured at the "
                        + "other end of each transfer. That is well defined for the whole session; attributing them to "
                        + "one process needs a proven transfer association, which no correlator has produced yet."
                    : "This total spans every process in scope. Per-process totals need process instances, "
                        + "which this session does not derive yet (R22).");
        }

        if (answer.UnknownContributions > 0)
        {
            caveats.Add(
                $"{answer.UnknownContributions:N0} of {answer.KnownContributions + answer.UnknownContributions:N0} "
                + "declared measurements this total takes carried no value. They are counted as unknown and never "
                + "as zero (R3).");
        }

        if (answer.ExcludedOtherDomain > 0)
        {
            caveats.Add(
                $"{answer.ExcludedOtherDomain:N0} contributions are in another byte domain and are excluded, not "
                + "added: two domains are never summed (I6, P3).");
        }

        if (answer.Sides.Any(side => side.Side == AccountingSide.EndpointActivity)
            && answer.TakenSides.Contains(AccountingSide.EndpointActivity))
        {
            caveats.Add(
                "Some contributions come from descriptors that name no direction, such as a connect or a disconnect. "
                + "They count toward endpoint activity only, and what their size field measures is not established "
                + "by a truth workload (ADR-011).");
        }

        return caveats;
    }

    private static MetricEvidence Evidence(string segment, SegmentReaderV1 reader, int row)
    {
        ObservationRowV1 observation = reader.Row(row);
        return new()
        {
            Segment = segment,
            Row = row,
            ObservationId = observation.ObservationIdIn(reader.CaptureId, reader.Derivation),
            Observation = observation,
        };
    }

    private static MetricResult Unavailable(
        MetricRequest request,
        long generation,
        MetricUnavailableReason reason,
        string explanation) => new()
        {
            Request = request,
            Generation = generation,
            Unavailable = reason,
            UnavailableExplanation = explanation,
        };

    /// <summary>One row label's running sum across segments. Checked, so a long capture cannot wrap (§10.2).</summary>
    private sealed class SideTotal
    {
        private readonly Dictionary<FieldAvailability, long> reasons = [];
        private long total;
        private long known;
        private long unknown;

        public void Add(SideMeasurement side)
        {
            total = checked(total + side.TotalBytes);
            known += side.KnownContributions;
            unknown += side.UnknownContributions;
            foreach ((FieldAvailability reason, long count) in side.UnknownReasons)
            {
                reasons[reason] = reasons.TryGetValue(reason, out long already) ? already + count : count;
            }
        }

        public SideMeasurement ToMeasurement(AccountingSide side) => new()
        {
            Side = side,
            TotalBytes = total,
            KnownContributions = known,
            UnknownContributions = unknown,
            UnknownReasons = reasons,
        };
    }

    /// <summary>What every answer over one generation shares: the request, the segments and the clock.</summary>
    private sealed record Context(
        MetricRequest Request,
        long Generation,
        IReadOnlyList<(string Name, SegmentReaderV1 Reader)> Segments,
        SourceClockDescriptor? Clock,
        int EvidenceLimit)
    {
        /// <summary>The generation's `source-fields-v1` segments, opened only when a grouping needs them.</summary>
        public IReadOnlyList<SegmentReaderV1> FieldSegments { get; init; } = [];

        public MetricResult Answer(MetricRequest request) => new()
        {
            Request = request,
            Generation = Generation,
            Unavailable = MetricUnavailableReason.None,
            Segments = Segments.Count,
            RowsRead = Segments.Sum(segment => (long)segment.Reader.RowCount),
            Derivations =
            [
                .. Segments
                    .Select(segment => segment.Reader.Derivation)
                    .Distinct()
                    .OrderBy(derivation => derivation.Value),
            ],
            Clock = Clock,
            FirstNativeTicks = Segments.Min(segment => segment.Reader.MinNativeTicks),
            LastNativeTicks = Segments.Max(segment => segment.Reader.MaxNativeTicks),
        };
    }
}
