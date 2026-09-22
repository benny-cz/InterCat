using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>What a metric requires of a byte domain, per §5.3.</summary>
public enum ByteDomainRule
{
    /// <summary>The metric measures no bytes. A request that names a domain is rejected.</summary>
    NotApplicable = 1,

    /// <summary>
    /// Exactly one of the traffic domains, chosen by the request. Two are never summed (I6, P3), and a domain a
    /// fixed-domain metric owns is refused here rather than relabelled as traffic.
    /// </summary>
    Required = 2,

    /// <summary>One domain, fixed by the metric. A request naming another is rejected, never substituted.</summary>
    Fixed = 3,

    /// <summary>Inherited from the numerator this metric divides.</summary>
    Inherited = 4,
}

/// <summary>What a metric requires of an accounting side, per §5.3.</summary>
public enum AccountingSideRule
{
    NotApplicable = 1,

    /// <summary>Exactly one of the sides the metric allows, chosen by the request. There is no default.</summary>
    Required = 2,

    Inherited = 3,

    /// <summary>Optional: carried where a side is known, absent where it is not.</summary>
    WhereKnown = 4,

    /// <summary>One side, fixed by the metric.</summary>
    Fixed = 5,
}

/// <summary>What kind of number a metric is, so a caller can present it without guessing its unit.</summary>
public enum MetricValueKind
{
    /// <summary>A count of source records, operations or errors.</summary>
    Count = 1,

    /// <summary>A byte sum in one named domain and accounting.</summary>
    ByteSum = 2,

    /// <summary>A count or byte sum divided by an interval.</summary>
    Rate = 3,

    /// <summary>A named interval's length, over a stated cohort.</summary>
    Duration = 4,

    /// <summary>A count of distinct entity instances.</summary>
    DistinctCount = 5,

    /// <summary>A resource's capacity. Never traffic.</summary>
    Capacity = 6,
}

/// <summary>
/// One row of §5.3's matrix: which bases a metric is defined for, and what it requires of a byte domain and an
/// accounting side.
/// </summary>
public sealed record MetricDefinition
{
    public required Metric Metric { get; init; }

    /// <summary>One line saying what the number is. It is what a caller shows beside the metric's name.</summary>
    public required string Meaning { get; init; }

    public required MetricValueKind Kind { get; init; }

    public required IReadOnlyList<AnalysisBasis> Bases { get; init; }

    public required ByteDomainRule DomainRule { get; init; }

    /// <summary>The domains a request may name: every traffic domain for a required one, the fixed one otherwise.</summary>
    public IReadOnlyList<ByteDomain> AllowedDomains { get; init; } = [];

    public required AccountingSideRule SideRule { get; init; }

    /// <summary>The accounting sides a request may name.</summary>
    public IReadOnlyList<AccountingSide> AllowedSides { get; init; } = [];

    /// <summary>
    /// The layer this metric counts evidence from, when it counts only one. A request is projected onto it by
    /// default and rejected when it names another (I11).
    /// </summary>
    public ObservationLayer? ImpliedLayer { get; init; }

    /// <summary>Whether a rate may divide this metric by an interval.</summary>
    public bool IsRateNumerator { get; init; }

    /// <summary>What to say when a basis does not define this metric, naming what does.</summary>
    public required string BasisAlternative { get; init; }

    /// <summary>The single domain a <see cref="ByteDomainRule.Fixed"/> metric measures in.</summary>
    public ByteDomain? FixedDomain => DomainRule == ByteDomainRule.Fixed ? AllowedDomains[0] : null;

    /// <summary>The single side a <see cref="AccountingSideRule.Fixed"/> metric is accounted to.</summary>
    public AccountingSide? FixedSide => SideRule == AccountingSideRule.Fixed ? AllowedSides[0] : null;
}

/// <summary>Why a request is not a valid one, and what would be.</summary>
public sealed record MetricRejection(string Reason, IReadOnlyList<Metric> CompatibleMetrics)
{
    public override string ToString() => CompatibleMetrics.Count == 0
        ? Reason
        : $"{Reason} Compatible metrics for this basis: {string.Join(", ", CompatibleMetrics)}.";
}

/// <summary>
/// §5.3's metric compatibility matrix, as a compiler rather than a document. §19.1 requires every query to
/// resolve against it before planning, and requires a metric outside its basis to be rejected with the
/// compatible alternatives named — never silently substituted.
/// </summary>
/// <remarks>
/// It is pure and portable, and it decides only whether a request <em>means</em> anything. Whether a meaningful
/// request can be answered from a given session is a separate question with a separate answer: a canonical-owner
/// total is a well-formed request that no session can answer until a correlator proves a transfer association,
/// and that is reported as unavailable, not rejected (ADR-012).
/// </remarks>
public static class MetricCompatibility
{
    /// <summary>
    /// The domains a byte metric that does not fix its own may be asked for. Every other domain belongs to a
    /// metric that names it: requested lengths are `RequestedIoBytes`, application lengths are
    /// `ApplicationPayloadBytes`, retained content is `CapturedContentBytes` and capacity is `MappingCapacity`.
    /// Accepting one of those here would relabel it as traffic, which is P3.
    /// </summary>
    public static IReadOnlyList<ByteDomain> TrafficDomains { get; } = [ByteDomain.TransportObserved, ByteDomain.CompletedIo];

    private static readonly IReadOnlyList<AccountingSide> OneDirectionSides =
        [AccountingSide.SendSide, AccountingSide.ReceiveSide, AccountingSide.CanonicalOwner];

    private static readonly IReadOnlyList<AccountingSide> EverySide =
        [AccountingSide.SendSide, AccountingSide.ReceiveSide, AccountingSide.EndpointActivity, AccountingSide.CanonicalOwner];

    /// <summary>Every row of the matrix, in `EN-Metric` order.</summary>
    public static IReadOnlyList<MetricDefinition> Definitions { get; } =
    [
        new()
        {
            Metric = Metric.Observations,
            Meaning = "Normalized source records. Sensitive to instrumentation density; not a volume and not an operation count.",
            Kind = MetricValueKind.Count,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.ResourceTopology],
            DomainRule = ByteDomainRule.NotApplicable,
            SideRule = AccountingSideRule.NotApplicable,
            IsRateNumerator = true,
            BasisAlternative =
                "A logical-operations basis counts operations, not the source records beneath them: counting "
                + "both would count one exchange twice (§5.1, P4).",
        },
        new()
        {
            Metric = Metric.OperationsStarted,
            Meaning = "Distinct logical operations with qualifying observed start evidence.",
            Kind = MetricValueKind.Count,
            Bases = [AnalysisBasis.LogicalOperations],
            DomainRule = ByteDomainRule.NotApplicable,
            SideRule = AccountingSideRule.NotApplicable,
            IsRateNumerator = true,
            BasisAlternative =
                "Only a logical-operations basis has operations. A source basis counts observations, and a raw "
                + "event count renamed as a call is exactly what §5 forbids.",
        },
        new()
        {
            Metric = Metric.OperationsCompleted,
            Meaning = "Distinct logical operations with qualifying observed completion evidence.",
            Kind = MetricValueKind.Count,
            Bases = [AnalysisBasis.LogicalOperations],
            DomainRule = ByteDomainRule.NotApplicable,
            SideRule = AccountingSideRule.NotApplicable,
            IsRateNumerator = true,
            BasisAlternative =
                "Only a logical-operations basis has operations. A source basis counts observations.",
        },
        new()
        {
            Metric = Metric.BytesSent,
            Meaning = "Bytes flowing out of the entity a total is grouped by, in one traffic domain and one accounting.",
            Kind = MetricValueKind.ByteSum,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.LogicalOperations],
            DomainRule = ByteDomainRule.Required,
            AllowedDomains = TrafficDomains,
            SideRule = AccountingSideRule.Required,
            AllowedSides = OneDirectionSides,
            IsRateNumerator = true,
            BasisAlternative =
                "A resource-topology basis describes resources and memberships; it cannot invent a traffic "
                + "value for them (§19.1).",
        },
        new()
        {
            Metric = Metric.BytesReceived,
            Meaning = "Bytes flowing into the entity a total is grouped by, in one traffic domain and one accounting.",
            Kind = MetricValueKind.ByteSum,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.LogicalOperations],
            DomainRule = ByteDomainRule.Required,
            AllowedDomains = TrafficDomains,
            SideRule = AccountingSideRule.Required,
            AllowedSides = OneDirectionSides,
            IsRateNumerator = true,
            BasisAlternative =
                "A resource-topology basis describes resources and memberships; it cannot invent a traffic "
                + "value for them (§19.1).",
        },
        new()
        {
            Metric = Metric.RequestedIoBytes,
            Meaning = "Requested I/O lengths. Separate from completed bytes, and never a transfer claim.",
            Kind = MetricValueKind.ByteSum,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.LogicalOperations],
            DomainRule = ByteDomainRule.Fixed,
            AllowedDomains = [ByteDomain.RequestedIo],
            SideRule = AccountingSideRule.Required,
            AllowedSides = EverySide,
            IsRateNumerator = true,
            BasisAlternative = "A resource-topology basis carries no I/O request.",
        },
        new()
        {
            Metric = Metric.ApplicationPayloadBytes,
            Meaning = "Source-verified application lengths, from application-layer sources only.",
            Kind = MetricValueKind.ByteSum,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.LogicalOperations],
            DomainRule = ByteDomainRule.Fixed,
            AllowedDomains = [ByteDomain.ApplicationPayload],
            SideRule = AccountingSideRule.Required,
            AllowedSides = EverySide,
            ImpliedLayer = ObservationLayer.Application,
            IsRateNumerator = true,
            BasisAlternative = "A resource-topology basis carries no payload length.",
        },
        new()
        {
            Metric = Metric.CapturedContentBytes,
            Meaning = "Content bytes retained for inspection. Not the volume that was transferred.",
            Kind = MetricValueKind.ByteSum,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.LogicalOperations],
            DomainRule = ByteDomainRule.Fixed,
            AllowedDomains = [ByteDomain.CapturedContent],
            SideRule = AccountingSideRule.Required,
            AllowedSides = EverySide,
            IsRateNumerator = true,
            BasisAlternative = "A resource-topology basis retains no content.",
        },
        new()
        {
            Metric = Metric.Rate,
            Meaning = "A named count or compatible byte sum divided by the whole selected interval.",
            Kind = MetricValueKind.Rate,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.LogicalOperations],
            DomainRule = ByteDomainRule.Inherited,
            SideRule = AccountingSideRule.Inherited,
            BasisAlternative = "A resource-topology basis has no traffic to divide by an interval.",
        },
        new()
        {
            Metric = Metric.Duration,
            Meaning = "A named interval: a call, an execution, an I/O completion or a mapping lifetime. Never interchangeable.",
            Kind = MetricValueKind.Duration,
            Bases = [AnalysisBasis.LogicalOperations, AnalysisBasis.ResourceTopology],
            DomainRule = ByteDomainRule.NotApplicable,
            SideRule = AccountingSideRule.NotApplicable,
            BasisAlternative =
                "A source observation is a point fact with no interval of its own. Duration needs an "
                + "operation with a named cohort, or a resource's mapping lifetime.",
        },
        new()
        {
            Metric = Metric.ActiveChannels,
            Meaning = "Distinct channel instances under the selected interval and evidence policy.",
            Kind = MetricValueKind.DistinctCount,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.LogicalOperations, AnalysisBasis.ResourceTopology],
            DomainRule = ByteDomainRule.NotApplicable,
            SideRule = AccountingSideRule.NotApplicable,
            BasisAlternative = string.Empty,
        },
        new()
        {
            Metric = Metric.ActivePeers,
            Meaning = "Distinct peer entity instances under the selected interval and evidence policy.",
            Kind = MetricValueKind.DistinctCount,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.LogicalOperations, AnalysisBasis.ResourceTopology],
            DomainRule = ByteDomainRule.NotApplicable,
            SideRule = AccountingSideRule.NotApplicable,
            BasisAlternative = string.Empty,
        },
        new()
        {
            Metric = Metric.MappingCapacity,
            Meaning = "A resource's capacity, once per identified resource. Topology, never traffic.",
            Kind = MetricValueKind.Capacity,
            Bases = [AnalysisBasis.ResourceTopology],
            DomainRule = ByteDomainRule.Fixed,
            AllowedDomains = [ByteDomain.Capacity],
            SideRule = AccountingSideRule.NotApplicable,
            BasisAlternative =
                "Capacity is a property of a resource, not of a record or an operation. It is available on a "
                + "resource-topology basis and is never traffic (§5).",
        },
        new()
        {
            Metric = Metric.Errors,
            Meaning = "Observations or operations whose source-reported status is an error.",
            Kind = MetricValueKind.Count,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.LogicalOperations],
            DomainRule = ByteDomainRule.NotApplicable,
            SideRule = AccountingSideRule.WhereKnown,
            AllowedSides = [AccountingSide.SendSide, AccountingSide.ReceiveSide, AccountingSide.EndpointActivity],
            IsRateNumerator = true,
            BasisAlternative = "A resource-topology basis reports no operation status.",
        },
        new()
        {
            Metric = Metric.EndpointActivityBytes,
            Meaning =
                "Every byte measurement at the endpoint that recorded it, sent and received alike, in one traffic "
                + "domain. Summed across endpoints it counts a local transfer at both of its ends, by design.",
            Kind = MetricValueKind.ByteSum,
            Bases = [AnalysisBasis.SourceObservations, AnalysisBasis.LogicalOperations],
            DomainRule = ByteDomainRule.Required,
            AllowedDomains = TrafficDomains,
            SideRule = AccountingSideRule.Fixed,
            AllowedSides = [AccountingSide.EndpointActivity],
            IsRateNumerator = true,
            BasisAlternative =
                "A resource-topology basis describes resources and memberships; it cannot invent a traffic "
                + "value for them (§19.1).",
        },
    ];

    /// <summary>The metrics a basis defines, for naming the alternatives to a rejected one.</summary>
    public static IReadOnlyList<Metric> MetricsFor(AnalysisBasis basis) =>
        [.. Definitions.Where(definition => definition.Bases.Contains(basis)).Select(definition => definition.Metric)];

    public static MetricDefinition DefinitionOf(Metric metric) =>
        Definitions.FirstOrDefault(definition => definition.Metric == metric)
        ?? throw new ArgumentOutOfRangeException(
            nameof(metric),
            metric,
            "§5.3's matrix has no row for this metric, so nothing states what it means.");

    /// <summary>
    /// The row labels a total under an accounting takes. A row's accounting side is a fact about the record — the
    /// end of the exchange its measurement describes — while a request's accounting is a rule for building a total
    /// from those facts, so the mapping is the metric layer's and lives here once (ADR-012).
    /// </summary>
    /// <remarks>
    /// Sender accounting takes send-side records and receiver accounting takes receive-side ones. Endpoint
    /// activity takes every record at the endpoint that made it, including one whose descriptor names no side,
    /// which is why a total under it counts both ends of a local transfer and says so. A canonical owner is
    /// chosen per proven transfer association, so it maps to no row label at all.
    /// </remarks>
    public static IReadOnlyList<AccountingSide> RowSidesTakenBy(AccountingSide accounting) => accounting switch
    {
        AccountingSide.SendSide => [AccountingSide.SendSide],
        AccountingSide.ReceiveSide => [AccountingSide.ReceiveSide],
        AccountingSide.EndpointActivity =>
            [AccountingSide.SendSide, AccountingSide.ReceiveSide, AccountingSide.EndpointActivity],
        AccountingSide.CanonicalOwner => throw new InvalidOperationException(
            "A canonical owner is chosen per proven transfer association, not by a row's label (§5.3)."),
        _ => throw new ArgumentOutOfRangeException(nameof(accounting), accounting, "An accounting side is a §23 code."),
    };

    /// <summary>What an accounting means, in one line a caller can show beside a total.</summary>
    public static string Describe(AccountingSide accounting) => accounting switch
    {
        AccountingSide.SendSide => "sender-accounted: each transfer is measured at its sending end",
        AccountingSide.ReceiveSide => "receiver-accounted: each transfer is measured at its receiving end",
        AccountingSide.EndpointActivity =>
            "endpoint activity: every measurement at the endpoint that recorded it, so a local transfer counts at both ends",
        AccountingSide.CanonicalOwner =>
            "canonical owner: each proven transfer association is measured once, by its owning contribution",
        _ => accounting.ToString(),
    };

    /// <summary>
    /// Resolves a request against the matrix. Returns null when the request means something, and a rejection
    /// naming the compatible alternatives when it does not.
    /// </summary>
    public static MetricRejection? Check(
        AnalysisBasis basis,
        Metric metric,
        ByteDomain? byteDomain,
        AccountingSide? accountingSide,
        ObservationLayer? layer = null,
        Metric? rateNumerator = null)
    {
        if (!Enum.IsDefined(basis))
        {
            return new("A request names a basis §23 defines.", []);
        }

        if (!Enum.IsDefined(metric))
        {
            return new("A request names a metric §23 defines.", MetricsFor(basis));
        }

        if (byteDomain is { } domain && !Enum.IsDefined(domain))
        {
            return new("A request names a byte domain §23 defines.", []);
        }

        if (accountingSide is { } side && !Enum.IsDefined(side))
        {
            return new("A request names an accounting side §23 defines.", []);
        }

        if (layer is { } projected && !Enum.IsDefined(projected))
        {
            return new("A request names a layer §23 defines.", []);
        }

        MetricDefinition definition = DefinitionOf(metric);
        if (!definition.Bases.Contains(basis))
        {
            return new(
                $"{metric} is not defined on a {basis} basis. {definition.BasisAlternative}".TrimEnd(),
                MetricsFor(basis));
        }

        if (metric == Metric.Rate)
        {
            return CheckRate(basis, byteDomain, accountingSide, layer, rateNumerator);
        }

        if (rateNumerator is not null)
        {
            return new(
                $"Only a rate divides by an interval, so a numerator does not apply to {metric}.",
                []);
        }

        return CheckDomain(definition, byteDomain)
            ?? CheckSide(definition, accountingSide)
            ?? CheckLayer(definition, layer, basis);
    }

    /// <summary>
    /// Materializes every default a request leaves implicit — a fixed domain, a fixed side, an implied layer — so
    /// that "unset" and "set to the value the metric fixes" become the same request, which is what §10.5 needs
    /// before a request can be identified. It assumes <see cref="Check"/> accepted the request.
    /// </summary>
    public static (ByteDomain? Domain, AccountingSide? Side, ObservationLayer? Layer) Materialize(
        Metric metric,
        ByteDomain? byteDomain,
        AccountingSide? accountingSide,
        ObservationLayer? layer,
        Metric? rateNumerator)
    {
        MetricDefinition definition = DefinitionOf(metric == Metric.Rate && rateNumerator is { } inner ? inner : metric);
        return (
            byteDomain ?? definition.FixedDomain,
            accountingSide ?? definition.FixedSide,
            layer ?? definition.ImpliedLayer);
    }

    private static MetricRejection? CheckDomain(MetricDefinition definition, ByteDomain? byteDomain)
    {
        switch (definition.DomainRule)
        {
            case ByteDomainRule.NotApplicable when byteDomain is not null:
                return new($"{definition.Metric} measures no bytes, so a byte domain does not apply to it.", []);
            case ByteDomainRule.Required when byteDomain is null:
                return new(
                    $"{definition.Metric} covers exactly one byte domain and the request names none. Two domains "
                    + "are never summed, so there is no default: name one of "
                    + $"{string.Join(" or ", definition.AllowedDomains)} (I6, P3).",
                    []);
            case ByteDomainRule.Required when !definition.AllowedDomains.Contains(byteDomain!.Value):
                Metric owner = OwnerOf(byteDomain.Value);
                return new(
                    $"{byteDomain} bytes are measured by {owner}, not by {definition.Metric}. Reporting them as "
                    + "traffic would relabel one byte domain as another, which is exactly what P3 forbids.",
                    []);
            case ByteDomainRule.Fixed when byteDomain is not null && byteDomain != definition.FixedDomain:
                return new(
                    $"{definition.Metric} is measured in {definition.FixedDomain} and the request names "
                    + $"{byteDomain}. Relabelling one domain as another is exactly what P3 forbids.",
                    []);
            default:
                return null;
        }
    }

    private static MetricRejection? CheckSide(MetricDefinition definition, AccountingSide? accountingSide)
    {
        switch (definition.SideRule)
        {
            case AccountingSideRule.NotApplicable when accountingSide is not null:
                return new(
                    $"{definition.Metric} has no observation side, so an accounting side does not apply to it.",
                    []);
            case AccountingSideRule.Required when accountingSide is null:
                return new(
                    $"{definition.Metric} is accounted to one side of the exchange and the request names none. A "
                    + "byte total with no stated side is the unexplained volume number §5 exists to prevent. Name "
                    + $"one of {string.Join(", ", definition.AllowedSides)}.",
                    []);
            case AccountingSideRule.Fixed when accountingSide is not null && accountingSide != definition.FixedSide:
                return new(
                    $"{definition.Metric} is accounted as {definition.FixedSide} by definition, and the request "
                    + $"names {accountingSide}.",
                    []);
            case AccountingSideRule.Required or AccountingSideRule.WhereKnown
                when accountingSide is { } side && !definition.AllowedSides.Contains(side):
                return side == AccountingSide.EndpointActivity
                    ? new(
                        $"{definition.Metric} names one direction and endpoint activity counts both, so the request "
                        + $"contradicts itself. Ask for {Metric.EndpointActivityBytes} instead: it is the "
                        + "separately labelled total §5.1 describes (ADR-012).",
                        [])
                    : new(
                        $"{definition.Metric} is not accounted as {side}. Name one of "
                        + $"{string.Join(", ", definition.AllowedSides)}.",
                        []);
            default:
                return null;
        }
    }

    private static MetricRejection? CheckLayer(MetricDefinition definition, ObservationLayer? layer, AnalysisBasis basis) =>
        definition.ImpliedLayer is { } required && layer is { } named && named != required
            ? new(
                $"{definition.Metric} counts only {required}-layer evidence, and the request projects onto "
                + $"{named}. Reporting it from another layer would let a transport length be read as an "
                + "application payload length (§5.3, I11).",
                MetricsFor(basis))
            : null;

    private static MetricRejection? CheckRate(
        AnalysisBasis basis,
        ByteDomain? byteDomain,
        AccountingSide? accountingSide,
        ObservationLayer? layer,
        Metric? rateNumerator)
    {
        if (rateNumerator is not { } numerator)
        {
            return new(
                "A rate divides a named count or byte sum by an interval, so the request names its numerator. "
                + "A rate with no numerator has no unit (§5).",
                RateNumeratorsFor(basis));
        }

        if (!Enum.IsDefined(numerator))
        {
            return new("A rate's numerator is a metric §23 defines.", RateNumeratorsFor(basis));
        }

        if (numerator == Metric.Rate)
        {
            return new("A rate is not its own numerator.", RateNumeratorsFor(basis));
        }

        MetricDefinition inner = DefinitionOf(numerator);
        if (!inner.IsRateNumerator)
        {
            return new(
                $"{numerator} is not additive over time, so dividing it by an interval produces no rate: a "
                + "distinct count, a duration or a capacity per second is not a quantity §5 defines.",
                RateNumeratorsFor(basis));
        }

        return Check(basis, numerator, byteDomain, accountingSide, layer);
    }

    private static IReadOnlyList<Metric> RateNumeratorsFor(AnalysisBasis basis) =>
        [.. Definitions
            .Where(definition => definition.IsRateNumerator && definition.Bases.Contains(basis))
            .Select(definition => definition.Metric)];

    /// <summary>The metric that owns a byte domain a traffic metric may not be asked for.</summary>
    private static Metric OwnerOf(ByteDomain domain) => domain switch
    {
        ByteDomain.RequestedIo => Metric.RequestedIoBytes,
        ByteDomain.ApplicationPayload => Metric.ApplicationPayloadBytes,
        ByteDomain.CapturedContent => Metric.CapturedContentBytes,
        ByteDomain.Capacity => Metric.MappingCapacity,
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "A traffic domain has no owning metric."),
    };
}
