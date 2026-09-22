using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>One metric answer, or the reason there is none. Both are results; neither is an error message.</summary>
internal sealed record MetricDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required long Generation { get; init; }
    public required bool FromLastKnownGood { get; init; }
    public required MetricSpecificationDocument Specification { get; init; }
    public required bool Available { get; init; }
    public required MetricUnavailableDocument? Unavailable { get; init; }
    public required long? Value { get; init; }
    public required string? Unit { get; init; }
    public required MetricRateDocument? Rate { get; init; }
    public required MetricAccountingDocument? Accounting { get; init; }
    public required MetricContributionsDocument Contributions { get; init; }
    public required MetricExclusionsDocument Excluded { get; init; }
    public required MetricReadDocument Read { get; init; }
    public required MetricClockDocument? Clock { get; init; }
    public required IReadOnlyList<MetricEvidenceDocument> Evidence { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }
}

/// <summary>The request as answered, with every default written out, in §23's specification member order.</summary>
internal sealed record MetricSpecificationDocument
{
    public required string Basis { get; init; }
    public required string Metric { get; init; }
    public required string? ByteDomain { get; init; }
    public required string? AccountingSide { get; init; }
    public required string? RateNumerator { get; init; }
    public required string TimeScope { get; init; }
    public required string? Layer { get; init; }
    public required string? Mechanism { get; init; }
    public required MetricIntervalDocument? Interval { get; init; }
}

internal sealed record MetricIntervalDocument
{
    public required long StartNativeTicks { get; init; }
    public required long EndNativeTicks { get; init; }
    public required string? StartSeconds { get; init; }
    public required string? EndSeconds { get; init; }
}

internal sealed record MetricUnavailableDocument
{
    public required string Reason { get; init; }
    public required string Explanation { get; init; }
}

/// <summary>A rate as the integers it is made of; the per-second figure is derived for reading, not stored.</summary>
internal sealed record MetricRateDocument
{
    public required long Numerator { get; init; }
    public required string NumeratorUnit { get; init; }
    public required long IntervalTicks { get; init; }
    public required long? TicksPerSecond { get; init; }
    public required string? PerSecond { get; init; }
    public required string? Unit { get; init; }
}

internal sealed record MetricAccountingDocument
{
    public required string Side { get; init; }
    public required string Meaning { get; init; }
    public required IReadOnlyList<string> TakenSides { get; init; }
}

internal sealed record MetricContributionsDocument
{
    public required long Known { get; init; }
    public required long Unknown { get; init; }
    public required double? MeasurementAvailability { get; init; }
    public required IReadOnlyDictionary<string, long> UnknownReasons { get; init; }
    public required IReadOnlyList<MetricSideDocument> BySide { get; init; }
    public required IReadOnlyDictionary<string, long> OtherDomains { get; init; }
}

internal sealed record MetricSideDocument
{
    public required string Side { get; init; }
    public required bool Taken { get; init; }
    public required long Known { get; init; }
    public required long Unknown { get; init; }
    public required long TotalBytes { get; init; }
}

internal sealed record MetricExclusionsDocument
{
    public required long OtherSide { get; init; }
    public required long OtherDomain { get; init; }
    public required long NoDeclaredSlot { get; init; }
    public required long ByProjection { get; init; }
    public required long OutsideInterval { get; init; }
}

internal sealed record MetricReadDocument
{
    public required int Segments { get; init; }
    public required long Rows { get; init; }
    public required IReadOnlyList<uint> Derivations { get; init; }
    public required long? FirstNativeTicks { get; init; }
    public required long? LastNativeTicks { get; init; }
}

internal sealed record MetricClockDocument
{
    public required string ClockId { get; init; }
    public required string Encoding { get; init; }
    public required long TicksPerSecond { get; init; }
    public required long CaptureEpochNativeTicks { get; init; }
}

/// <summary>One counted record: where it is in this generation and the identities that survive every re-read.</summary>
internal sealed record MetricEvidenceDocument
{
    public required string Segment { get; init; }
    public required int Row { get; init; }
    public required ObservationId ObservationId { get; init; }
    public required ulong? JournalRecordIndex { get; init; }
    public required long NativeTicks { get; init; }
    public required string? Seconds { get; init; }
    public required string Mechanism { get; init; }
    public required string Layer { get; init; }
    public required string Kind { get; init; }
    public required int? OwnerProcessId { get; init; }
    public required long? ByteValue { get; init; }
    public required string ByteAvailability { get; init; }
    public required string? AccountingSide { get; init; }
}

/// <summary>
/// Answers one metric over a published session, resolving the request against §5.3's matrix first. R18 requires
/// every analysis result the UI shows to be reachable from the CLI through the same specification, so this is
/// the specification, not a convenience view of it.
/// </summary>
internal static class MetricCommand
{
    private const int MaximumEvidence = 1_000;

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        if (command.TryTakeFlag("--matrix"))
        {
            bool asJson = command.TryTakeFlag("--json");
            if (command.TryReportUnknown(out string? extra))
            {
                ConsoleUi.Failure($"Unknown or incomplete option: {extra}");
                return InterCatExitCode.InvalidInvocation;
            }

            RenderMatrix(asJson);
            return InterCatExitCode.Success;
        }

        string? sessionPath = command.TakePositional();
        string? basisOption = command.TakeOption("--basis");
        string? metricOption = command.TakeOption("--metric");
        string? domainOption = command.TakeOption("--byte-domain");
        string? sideOption = command.TakeOption("--side");
        string? numeratorOption = command.TakeOption("--rate-numerator");
        string? layerOption = command.TakeOption("--layer");
        string? mechanismOption = command.TakeOption("--mechanism");
        string? intervalOption = command.TakeOption("--interval");
        string? evidenceOption = command.TakeOption("--evidence");
        string? outputOption = command.TakeOption("--output");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null || metricOption is null)
        {
            ConsoleUi.Failure(
                "A session directory and a metric are required: icat metric <directory> --metric <name>. "
                + "Run icat metric --matrix to see every metric and what it needs.");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sessionPath);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!TryParse(basisOption, AnalysisBasis.SourceObservations, "--basis", out AnalysisBasis basis, out string? problem)
            || !TryParse(metricOption, Metric.Observations, "--metric", out Metric metric, out problem)
            || !TryParseOptional(domainOption, "--byte-domain", out ByteDomain? domain, out problem)
            || !TryParseOptional(sideOption, "--side", out AccountingSide? side, out problem)
            || !TryParseOptional(numeratorOption, "--rate-numerator", out Metric? numerator, out problem)
            || !TryParseOptional(layerOption, "--layer", out ObservationLayer? layer, out problem)
            || !TryParseOptional(mechanismOption, "--mechanism", out Mechanism? mechanism, out problem)
            || !TryParseEvidence(evidenceOption, out int evidence, out problem))
        {
            ConsoleUi.Failure(problem!);
            return InterCatExitCode.InvalidInvocation;
        }

        string? outputPath = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (outputPath is not null && File.Exists(outputPath) && !overwrite)
        {
            ConsoleUi.Failure($"{outputPath} exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        var request = new MetricRequest
        {
            Basis = basis,
            Metric = metric,
            ByteDomain = domain,
            AccountingSide = side,
            RateNumerator = numerator,
            Layer = layer,
            Mechanism = mechanism,
        };

        // §19.1 resolves the request against §5.3's matrix before planning, and a rejected metric names the
        // compatible ones rather than being quietly substituted. This needs no session, so it is checked first.
        if (request.Check() is { } rejection)
        {
            ConsoleUi.Failure(rejection.Reason);
            if (rejection.CompatibleMetrics.Count > 0)
            {
                ConsoleUi.Line();
                ConsoleUi.Line($"  Metrics defined on a {basis} basis:");
                foreach (Metric compatible in rejection.CompatibleMetrics)
                {
                    ConsoleUi.Bullet($"{compatible} - {MetricCompatibility.DefinitionOf(compatible).Meaning}");
                }
            }

            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Acquiring the current generation of {full} and verifying every dependency.");
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (store.Current is not { } manifest)
        {
            ConsoleUi.Failure(
                "This session has published no generation, so there is nothing to answer from. An empty session is "
                + "not a session with no traffic.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (store.Recovery.RolledBackToLastKnownGood)
        {
            ConsoleUi.Warn(
                $"The newest generation did not verify ({store.Recovery.RollbackReason}); answering from the retained "
                + $"last-known-good generation {manifest.Generation}.");
        }

        if (intervalOption is not null)
        {
            SourceClockDescriptor? clock = SessionSegments.SourceClock(store.Root, manifest);
            if (!TryParseInterval(intervalOption, clock, out TimeRange? interval, out problem))
            {
                ConsoleUi.Failure(problem!);
                return InterCatExitCode.InvalidInvocation;
            }

            request = request with { Interval = interval };
        }

        ConsoleUi.Progress($"Answering {Name(metric)} over generation {manifest.Generation} under an evidence lease.");
        MetricResult result;
        try
        {
            result = SessionMetrics.Evaluate(store, request, new() { EvidenceLimit = evidence }, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            // A request the matrix accepts can still be one this session cannot scope, such as a tick interval over
            // readings on two clocks. That is the request's problem, reported beside it.
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.InvalidInvocation;
        }
        MetricDocument document = Describe(result, full, store.Recovery.RolledBackToLastKnownGood);
        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document, result);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Metric written to {outputPath}.");
        }

        return !result.IsAvailable
            ? InterCatExitCode.PermissionOrCapabilityFailure
            : store.Recovery.RolledBackToLastKnownGood
                ? InterCatExitCode.PartialResultSuccess
                : InterCatExitCode.Success;
    }

    private static MetricDocument Describe(MetricResult result, string path, bool fromLastKnownGood)
    {
        MetricRequest request = result.Request;
        SourceClockDescriptor? clock = result.Clock;
        IReadOnlyList<AccountingSide> taken = result.TakenSides;
        return new()
        {
            Contract = "metrics-v1",
            Path = path,
            Generation = result.Generation,
            FromLastKnownGood = fromLastKnownGood,
            Specification = new()
            {
                Basis = request.Basis.ToString(),
                Metric = request.Metric.ToString(),
                ByteDomain = request.ByteDomain?.ToString(),
                AccountingSide = request.AccountingSide?.ToString(),
                RateNumerator = request.RateNumerator?.ToString(),
                TimeScope = request.TimeScope.ToString(),
                Layer = request.Layer?.ToString(),
                Mechanism = request.Mechanism?.ToString(),
                Interval = request.Interval is { } interval
                    ? new()
                    {
                        StartNativeTicks = interval.StartTicks,
                        EndNativeTicks = interval.EndTicks,
                        StartSeconds = Seconds(clock, interval.StartTicks),
                        EndSeconds = Seconds(clock, interval.EndTicks),
                    }
                    : null,
            },
            Available = result.IsAvailable,
            Unavailable = result.IsAvailable
                ? null
                : new() { Reason = result.Unavailable.ToString(), Explanation = result.UnavailableExplanation ?? string.Empty },
            Value = result.Value,
            Unit = result.Unit?.ToString(),
            Rate = result.Rate is { } rate
                ? new()
                {
                    Numerator = rate.Numerator,
                    NumeratorUnit = rate.NumeratorUnit.ToString(),
                    IntervalTicks = rate.IntervalTicks,
                    TicksPerSecond = rate.TicksPerSecond,
                    PerSecond = rate.PerSecond?.ToString("0.000", CultureInfo.InvariantCulture),
                    Unit = rate.Unit?.ToString(),
                }
                : null,
            Accounting = request.AccountingSide is { } accounting && taken.Count > 0
                ? new()
                {
                    Side = accounting.ToString(),
                    Meaning = MetricCompatibility.Describe(accounting),
                    TakenSides = [.. taken.Select(item => item.ToString())],
                }
                : null,
            Contributions = new()
            {
                Known = result.KnownContributions,
                Unknown = result.UnknownContributions,
                MeasurementAvailability = result.MeasurementAvailability,
                UnknownReasons = result.UnknownReasons.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
                BySide =
                [
                    .. result.Sides.Select(item => new MetricSideDocument
                    {
                        Side = item.Side.ToString(),
                        Taken = taken.Contains(item.Side),
                        Known = item.KnownContributions,
                        Unknown = item.UnknownContributions,
                        TotalBytes = item.TotalBytes,
                    }),
                ],
                OtherDomains = result.OtherDomains.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
            },
            Excluded = new()
            {
                OtherSide = result.ExcludedOtherSide,
                OtherDomain = result.ExcludedOtherDomain,
                NoDeclaredSlot = result.ExcludedNoDeclaredSlot,
                ByProjection = result.ExcludedByProjection,
                OutsideInterval = result.ExcludedOutsideInterval,
            },
            Read = new()
            {
                Segments = result.Segments,
                Rows = result.RowsRead,
                Derivations = [.. result.Derivations.Select(derivation => derivation.Value)],
                FirstNativeTicks = result.FirstNativeTicks,
                LastNativeTicks = result.LastNativeTicks,
            },
            Clock = clock is { } described
                ? new()
                {
                    ClockId = described.Id.ToString(),
                    Encoding = described.Encoding.ToString(),
                    TicksPerSecond = described.TicksPerSecond,
                    CaptureEpochNativeTicks = described.CaptureEpochNativeTicks,
                }
                : null,
            Evidence =
            [
                .. result.Evidence.Select(item => new MetricEvidenceDocument
                {
                    Segment = item.Segment,
                    Row = item.Row,
                    ObservationId = item.ObservationId,
                    JournalRecordIndex = item.Observation.JournalRecordIndex,
                    NativeTicks = item.Observation.NativeTicks,
                    Seconds = Seconds(clock, item.Observation.NativeTicks),
                    Mechanism = item.Observation.Mechanism.ToString(),
                    Layer = item.Observation.Layer.ToString(),
                    Kind = item.Observation.Kind.ToString(),
                    OwnerProcessId = item.Observation.OwnerProcessId,
                    ByteValue = item.Observation.ByteValue,
                    ByteAvailability = item.Observation.ByteAvailability.ToString(),
                    AccountingSide = item.Observation.AccountingSide?.ToString(),
                }),
            ],
            Caveats = result.Caveats,
        };
    }

    private static void Render(MetricDocument document, MetricResult result)
    {
        MetricRequest request = result.Request;
        ConsoleUi.Heading(Name(request.Metric)
            + (request.RateNumerator is { } inner ? $" of {Name(inner).ToLowerInvariant()}" : string.Empty));
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field(
            "Generation",
            document.FromLastKnownGood
                ? $"{ConsoleUi.Count(document.Generation)} (the retained last-known-good)"
                : ConsoleUi.Count(document.Generation));
        ConsoleUi.Field("Basis", Words(request.Basis.ToString()));
        if (request.ByteDomain is { } domain)
        {
            ConsoleUi.Field("Byte domain", domain.ToString());
        }

        if (request.AccountingSide is { } side)
        {
            ConsoleUi.Field("Accounting", $"{side} - {MetricCompatibility.Describe(side)}");
        }

        ConsoleUi.Field("Scope", Scope(result));
        ConsoleUi.Field(
            "Projection",
            string.Join(
                ", ",
                new[] { request.Layer?.ToString(), request.Mechanism?.ToString() }
                    .Where(value => value is not null)
                    .DefaultIfEmpty("every layer and mechanism")));

        if (!document.Available)
        {
            ConsoleUi.Heading("Unavailable");
            ConsoleUi.Field("Reason", Words(document.Unavailable!.Reason));
            ConsoleUi.Line();
            ConsoleUi.Note(document.Unavailable.Explanation);
            RenderSides(document, result);
            return;
        }

        ConsoleUi.Heading("Answer");
        ConsoleUi.Field("Value", Value(document, result));
        if (document.Rate is { } rate)
        {
            ConsoleUi.Field(
                "Exactly",
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"{rate.Numerator:N0} {Unit(rate.NumeratorUnit)} over {rate.IntervalTicks:N0} ticks")
                + (rate.TicksPerSecond is { } perSecond
                    ? string.Create(CultureInfo.CurrentCulture, $" at {perSecond:N0} ticks/s")
                    : string.Empty));
        }

        if (result.TakenSides.Count > 0)
        {
            ConsoleUi.Field(
                "Measured on",
                document.Contributions.MeasurementAvailability is { } availability
                    ? string.Create(
                        CultureInfo.CurrentCulture,
                        $"{availability:P1} of {document.Contributions.Known + document.Contributions.Unknown:N0} declared contributions")
                    : "no declared contribution");
        }

        ConsoleUi.Field(
            "Read",
            string.Create(
                CultureInfo.CurrentCulture,
                $"{document.Read.Rows:N0} observations in {document.Read.Segments:N0} segment{(document.Read.Segments == 1 ? string.Empty : "s")}"));

        RenderSides(document, result);
        RenderExclusions(document, result);
        RenderEvidence(document, result);

        ConsoleUi.Line();
        foreach (string caveat in document.Caveats)
        {
            ConsoleUi.Note(caveat);
        }
    }

    private static void RenderSides(MetricDocument document, MetricResult result)
    {
        if (document.Contributions.BySide.Count == 0)
        {
            return;
        }

        ConsoleUi.Line();
        ConsoleUi.Line($"  Contributions in {result.Request.ByteDomain}, by the side each record measured:");
        ConsoleUi.Table(
            ["Side", "Taken", "Known", "Unknown", "Bytes"],
            [
                .. document.Contributions.BySide.Select(side => new[]
                {
                    side.Side,
                    side.Taken ? "yes" : "no",
                    ConsoleUi.Count(side.Known),
                    ConsoleUi.Count(side.Unknown),
                    ConsoleUi.Bytes(side.TotalBytes),
                }),
            ]);
    }

    private static void RenderExclusions(MetricDocument document, MetricResult result)
    {
        var rows = new List<string[]>();
        if (result.TakenSides.Count > 0)
        {
            rows.Add(["another accounting side", ConsoleUi.Count(document.Excluded.OtherSide)]);
            rows.Add(["another byte domain", ConsoleUi.Count(document.Excluded.OtherDomain)]);
            rows.Add(["no declared byte slot", ConsoleUi.Count(document.Excluded.NoDeclaredSlot)]);
        }

        rows.Add(["outside the projection", ConsoleUi.Count(document.Excluded.ByProjection)]);
        rows.Add(["outside the interval", ConsoleUi.Count(document.Excluded.OutsideInterval)]);
        ConsoleUi.Line();
        ConsoleUi.Line("  Records left out of this answer:");
        ConsoleUi.Table(["Why", "Records"], rows);
        if (document.Contributions.OtherDomains.Count > 0)
        {
            ConsoleUi.Line(
                "  Other domains measured in scope: "
                + string.Join(
                    ", ",
                    document.Contributions.OtherDomains.Select(entry =>
                        string.Create(CultureInfo.CurrentCulture, $"{entry.Key} ({entry.Value:N0})"))));
        }
    }

    private static void RenderEvidence(MetricDocument document, MetricResult result)
    {
        if (document.Evidence.Count == 0)
        {
            return;
        }

        long counted = result.TakenSides.Count > 0 ? result.KnownContributions + result.UnknownContributions : result.Value ?? 0;
        ConsoleUi.Line();
        ConsoleUi.Line(string.Create(
            CultureInfo.CurrentCulture,
            $"  The first {document.Evidence.Count:N0} of {counted:N0} records this answer counted:"));
        ConsoleUi.Table(
            ["Time", "Mechanism", "Kind", "Owner PID", "Bytes", "Side", "Journal record"],
            [
                .. document.Evidence.Select(item => new[]
                {
                    item.Seconds is null ? $"{item.NativeTicks:N0} ticks" : $"{item.Seconds} s",
                    item.Mechanism,
                    item.Kind,
                    item.OwnerProcessId?.ToString(CultureInfo.CurrentCulture) ?? "unknown",
                    item.ByteValue is { } value
                        ? value.ToString("N0", CultureInfo.CurrentCulture)
                        : item.ByteAvailability == nameof(FieldAvailability.NotApplicable) ? "n/a" : $"unknown ({item.ByteAvailability})",
                    item.AccountingSide ?? "n/a",
                    item.JournalRecordIndex?.ToString("N0", CultureInfo.CurrentCulture) ?? "none",
                }),
            ]);
        ConsoleUi.Note("Each record's observation identity is in the --json output; it is stable across re-reads (I1, I2).");
    }

    private static string Value(MetricDocument document, MetricResult result)
    {
        if (document.Rate is { } rate)
        {
            return rate.PerSecond is { } perSecond
                ? $"{decimal.Parse(perSecond, CultureInfo.InvariantCulture).ToString("N3", CultureInfo.CurrentCulture)} {Unit(rate.NumeratorUnit)}/s"
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"{rate.Numerator:N0} {Unit(rate.NumeratorUnit)} per {rate.IntervalTicks:N0} native ticks");
        }

        return result.Unit == MeasurementUnit.Count
            ? $"{ConsoleUi.Count(document.Value ?? 0)} {(result.Request.Metric == Metric.Observations ? "observations" : "counted")}"
            : ConsoleUi.Bytes(document.Value);
    }

    private static string Scope(MetricResult result)
    {
        SourceClockDescriptor? clock = result.Clock;
        if (result.Request.Interval is { } interval)
        {
            return clock is null
                ? string.Create(CultureInfo.CurrentCulture, $"[{interval.StartTicks:N0}, {interval.EndTicks:N0}) native ticks")
                : $"[{Seconds(clock, interval.StartTicks)} s, {Seconds(clock, interval.EndTicks)} s) after capture start"
                    + string.Create(CultureInfo.CurrentCulture, $" (native [{interval.StartTicks:N0}, {interval.EndTicks:N0}))");
        }

        return result.FirstNativeTicks is { } first && result.LastNativeTicks is { } last && clock is not null
            ? $"the whole retained capture: observations from {Seconds(clock, first)} s to {Seconds(clock, last)} s"
            : "the whole retained capture";
    }

    /// <summary>A native reading as seconds after the capture epoch, or null when the clock is not described.</summary>
    private static string? Seconds(SourceClockDescriptor? clock, long nativeTicks)
    {
        if (clock is not { } described)
        {
            return null;
        }

        Int128 nanoseconds = SourceClockMath.SessionNanoseconds(described, nativeTicks);
        decimal seconds = (decimal)nanoseconds / SourceClockMath.SessionTicksPerSecond;
        return seconds.ToString("0.000000", CultureInfo.InvariantCulture);
    }

    private static void RenderMatrix(bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                MetricCompatibility.Definitions.Select(definition => new
                {
                    metric = definition.Metric.ToString(),
                    meaning = definition.Meaning,
                    kind = definition.Kind.ToString(),
                    bases = definition.Bases.Select(basis => basis.ToString()),
                    byteDomain = definition.DomainRule.ToString(),
                    allowedDomains = definition.AllowedDomains.Select(domain => domain.ToString()),
                    accountingSide = definition.SideRule.ToString(),
                    allowedSides = definition.AllowedSides.Select(side => side.ToString()),
                    impliedLayer = definition.ImpliedLayer?.ToString(),
                    rateNumerator = definition.IsRateNumerator,
                }),
                JsonContracts.Indented));
            return;
        }

        ConsoleUi.Heading("Metric compatibility (section 5.3)");
        ConsoleUi.Table(
            ["Metric", "Source", "Operations", "Topology", "Byte domain", "Accounting side"],
            [
                .. MetricCompatibility.Definitions.Select(definition => new[]
                {
                    definition.Metric.ToString(),
                    definition.Bases.Contains(AnalysisBasis.SourceObservations) ? "yes" : "no",
                    definition.Bases.Contains(AnalysisBasis.LogicalOperations) ? "yes" : "no",
                    definition.Bases.Contains(AnalysisBasis.ResourceTopology) ? "yes" : "no",
                    definition.DomainRule switch
                    {
                        ByteDomainRule.NotApplicable => "n/a",
                        ByteDomainRule.Fixed => $"fixed {definition.FixedDomain}",
                        ByteDomainRule.Inherited => "from the numerator",
                        _ => $"one of {string.Join(", ", definition.AllowedDomains)}",
                    },
                    definition.SideRule switch
                    {
                        AccountingSideRule.NotApplicable => "n/a",
                        AccountingSideRule.Fixed => $"fixed {definition.FixedSide}",
                        AccountingSideRule.Inherited => "from the numerator",
                        AccountingSideRule.WhereKnown => "optional",
                        _ => $"one of {string.Join(", ", definition.AllowedSides)}",
                    },
                }),
            ]);
        ConsoleUi.Line();
        foreach (MetricDefinition definition in MetricCompatibility.Definitions)
        {
            ConsoleUi.Field(definition.Metric.ToString(), definition.Meaning, width: 26);
        }

        ConsoleUi.Line();
        ConsoleUi.Note("A metric outside its basis is rejected with the compatible ones named, never substituted.");
        ConsoleUi.Note("Two byte domains are never summed, ranked together or shown in one total (I6, P3).");
        ConsoleUi.Note("A permitted metric this session cannot derive is reported as unavailable, with what it needs.");
    }

    private static bool TryParse<T>(string? value, T fallback, string name, out T parsed, out string? problem)
        where T : struct, Enum
    {
        parsed = fallback;
        problem = null;
        if (value is null)
        {
            return true;
        }

        // A name is accepted as written or in lower-case kebab form, so `bytes-sent` and `BytesSent` are the same
        // request. A number is refused: codes are what the durable format stores, not what a person types.
        string compact = value.Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length == 0
            || char.IsAsciiDigit(compact[0])
            || !Enum.TryParse(compact, ignoreCase: true, out parsed)
            || !Enum.IsDefined(parsed))
        {
            problem = $"{name} expects one of: {string.Join(", ", Enum.GetNames<T>())}. '{value}' is not one.";
            return false;
        }

        return true;
    }

    private static bool TryParseOptional<T>(string? value, string name, out T? parsed, out string? problem)
        where T : struct, Enum
    {
        parsed = null;
        if (!TryParse(value, default, name, out T resolved, out problem))
        {
            return false;
        }

        parsed = value is null ? null : resolved;
        return true;
    }

    private static bool TryParseEvidence(string? value, out int evidence, out string? problem)
    {
        evidence = 0;
        problem = null;
        if (value is null)
        {
            return true;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out evidence) || evidence > MaximumEvidence)
        {
            problem = $"--evidence expects a whole number of records up to {MaximumEvidence:N0}; '{value}' is not one.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads <c>start:end</c>, half-open. A bare integer is a native tick of the session's own clock; a number with
    /// a unit - <c>s</c>, <c>ms</c>, <c>us</c> or <c>ns</c> - is time after the capture epoch, converted to the first
    /// native tick at or after it, which needs the clock the session's journal describes.
    /// </summary>
    private static bool TryParseInterval(
        string value,
        SourceClockDescriptor? clock,
        out TimeRange? interval,
        out string? problem)
    {
        interval = null;
        problem = null;
        string[] parts = value.Split(':');
        if (parts.Length != 2
            || !TryParseBound(parts[0], clock, out long start, out problem)
            || !TryParseBound(parts[1], clock, out long end, out problem))
        {
            problem ??=
                "--interval expects <start>:<end>, half-open. Each bound is a native tick (for example "
                + "18683281748319) or a time after capture start with a unit (for example 1.5s, 250ms, 40us). "
                + $"'{value}' is not that.";
            return false;
        }

        if (end <= start)
        {
            problem = $"--interval's end must be after its start; '{value}' names an empty or reversed interval.";
            return false;
        }

        interval = new TimeRange(start, end);
        return true;
    }

    private static bool TryParseBound(string text, SourceClockDescriptor? clock, out long nativeTicks, out string? problem)
    {
        nativeTicks = 0;
        problem = null;
        string trimmed = text.Trim();
        if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nativeTicks))
        {
            return true;
        }

        (string suffix, long nanosecondsPerUnit)[] units = [("ns", 1), ("us", 1_000), ("ms", 1_000_000), ("s", 1_000_000_000)];
        foreach ((string suffix, long scale) in units)
        {
            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || !decimal.TryParse(
                    trimmed[..^suffix.Length],
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out decimal amount))
            {
                continue;
            }

            if (clock is not { } described)
            {
                problem =
                    $"'{text}' is a time, and this session does not describe the clock its readings are on, so no "
                    + "native tick corresponds to it. Give the bound in native ticks instead (I8).";
                return false;
            }

            decimal nanoseconds = decimal.Round(amount * scale, 0, MidpointRounding.ToEven);
            if (nanoseconds != amount * scale)
            {
                problem = $"'{text}' is finer than a nanosecond, which is the finest session time InterCat keeps.";
                return false;
            }

            try
            {
                nativeTicks = SourceClockMath.FirstNativeAtOrAfter(described, new SessionTimestamp((long)nanoseconds));
                return true;
            }
            catch (OverflowException)
            {
                problem = $"'{text}' is outside the range of readings this session's clock can hold.";
                return false;
            }
        }

        return false;
    }

    /// <summary>A metric's name as a reader says it, e.g. "Bytes sent".</summary>
    private static string Name(Metric metric) => metric switch
    {
        Metric.BytesSent => "Bytes sent",
        Metric.BytesReceived => "Bytes received",
        Metric.RequestedIoBytes => "Requested I/O bytes",
        Metric.ApplicationPayloadBytes => "Application payload bytes",
        Metric.CapturedContentBytes => "Captured content bytes",
        Metric.EndpointActivityBytes => "Endpoint activity bytes",
        Metric.OperationsStarted => "Operations started",
        Metric.OperationsCompleted => "Operations completed",
        Metric.ActiveChannels => "Active channels",
        Metric.ActivePeers => "Active peers",
        Metric.MappingCapacity => "Mapping capacity",
        _ => metric.ToString(),
    };

    private static string Unit(string unit) => unit switch
    {
        nameof(MeasurementUnit.Bytes) => "B",
        nameof(MeasurementUnit.Count) => "records",
        _ => unit,
    };

    /// <summary>Splits a PascalCase identifier into lower-case words for reading, e.g. "source observations".</summary>
    private static string Words(string identifier)
    {
        var builder = new System.Text.StringBuilder(identifier.Length + 8);
        for (int index = 0; index < identifier.Length; index++)
        {
            char character = identifier[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append(' ');
            }

            builder.Append(index == 0 ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat metric <directory> --metric <name> [--basis <name>] [--byte-domain <name>]");
        ConsoleUi.Line("             [--side <name>] [--rate-numerator <name>] [--layer <name>]");
        ConsoleUi.Line("             [--mechanism <name>] [--interval <start>:<end>] [--evidence <n>]");
        ConsoleUi.Line("             [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("  icat metric --matrix [--json]");
        ConsoleUi.Line("      Answers one metric over a published session, resolving the request against");
        ConsoleUi.Line("      section 5.3's matrix first. A metric outside its basis is rejected with the");
        ConsoleUi.Line("      compatible ones named; one the matrix permits that this session cannot derive");
        ConsoleUi.Line("      is reported as unavailable with what it needs. --matrix prints the matrix.");
        ConsoleUi.Line("      --interval bounds are native ticks, or times after capture start such as 1.5s.");
        ConsoleUi.Line("      --evidence lists the first records the answer counted.");
        ConsoleUi.Line("      Exit codes: 0 answered, 1 answered from the last-known-good generation,");
        ConsoleUi.Line("      2 invalid request, 3 the session cannot supply it, 4 corrupted session.");
    }
}
