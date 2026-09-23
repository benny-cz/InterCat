using System.Text;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

/// <summary>
/// The privileged, local-only input to preparation. Implementations compile from their installed
/// source catalog and schemas; no method accepts a client-supplied effective plan.
/// </summary>
public interface IBrokerCapturePlanSource
{
    ValueTask<CapabilityReport> ProbeAsync(CancellationToken cancellationToken);

    ValueTask<EffectiveCapturePlan> CompileAsync(
        CaptureProfileRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Production plan source for a broker host; constructing it does not start capture.</summary>
public sealed class WindowsBrokerCapturePlanSource : IBrokerCapturePlanSource
{
    private readonly CapabilityInventoryProbe inventory;
    private readonly ProbeEnvironment environment;
    private readonly TimeProvider clock;

    public WindowsBrokerCapturePlanSource(
        CapabilityInventoryProbe inventory,
        ProbeEnvironment environment,
        TimeProvider? clock = null)
    {
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        this.clock = clock ?? TimeProvider.System;
    }

    public ValueTask<CapabilityReport> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(inventory.Probe(environment));
    }

    public ValueTask<EffectiveCapturePlan> CompileAsync(
        CaptureProfileRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CaptureProfileCompiler.Compile(request, inventory, environment, clock));
    }
}

/// <summary>
/// Serializes local metadata access, freezes the compiled plan, and issues the owner-bound secret only
/// after the effective summary has been produced. Quota and retention are part of the frozen digest.
/// </summary>
public sealed class BrokerPreparationCoordinator : IDisposable
{
    private readonly IBrokerCapturePlanSource source;
    private readonly PreparedPlanRegistry registry;
    private readonly BrokerRuntimeIdentity runtime;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public BrokerPreparationCoordinator(
        IBrokerCapturePlanSource source,
        PreparedPlanRegistry registry,
        BrokerRuntimeIdentity runtime)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async ValueTask<BrokerCapabilitiesResponse> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CapabilityReport report = await source.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return ToResponse(report);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<BrokerPrepareCaptureResponse> PrepareAsync(
        BrokerPrepareCaptureRequest request,
        BrokerClientIdentity client,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);
        string? identityProblem = client.Validate();
        if (identityProblem is not null)
        {
            throw new ArgumentException(identityProblem, nameof(client));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EffectiveCapturePlan effective = await source
                .CompileAsync(BrokerPrepareRequestPolicy.ToProfileRequest(request), cancellationToken)
                .ConfigureAwait(false);
            BrokerEffectiveCaptureSummary summary = ToSummary(
                effective, request.Quota, request.Retention, request.Publication);
            BrokerPrepareResult preparation = BrokerPrepareCompiler.Prepare(
                effective,
                request.Quota,
                request.Retention,
                runtime,
                request.Publication);
            if (!preparation.IsPrepared)
            {
                return new(false, preparation.Refusal!.Code, Bound(preparation.Refusal.Message, 512), null, summary);
            }

            PreparedPlanGrant grant = registry.Issue(preparation.PreparedPlan!, client);
            return new(true, null, null, grant, summary);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            gate.Dispose();
        }
    }

    private static BrokerCapabilitiesResponse ToResponse(CapabilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new(
            report.ProbedAtUtc,
            Bound(report.Environment.OperatingSystem, 512),
            Bound(report.Environment.BuildId, 128),
            Bound(report.Environment.Architecture, 32),
            report.Environment.IsSupportedBuild,
            report.Environment.Support.Tier,
            report.Environment.IsElevated,
            Bound(report.AdapterVersion, 128),
            [
                .. CaptureProfileCatalog.All.Select(profile => new BrokerProfileCapability(
                    Bound(profile.Id, 64),
                    Bound(profile.DisplayName, 128),
                    profile.Admission,
                    profile.CompilationAvailable,
                    profile.RequestPreviewAvailable,
                    Bound(profile.Summary, 512),
                    BoundOptional(profile.UnavailableReason, 512))),
            ],
            [
                .. report.Mechanisms
                    .OrderBy(item => item.Mechanism)
                    .Take(64)
                    .Select(item => new BrokerMechanismCapability(
                        item.Mechanism,
                        item.State,
                        item.Tier,
                        Bound(item.Summary, 512),
                        BoundOptional(item.UnavailableReason, 512))),
            ]);
    }

    private static BrokerEffectiveCaptureSummary ToSummary(
        EffectiveCapturePlan plan,
        BrokerCaptureQuota quota,
        BrokerRetentionPolicy retention,
        BrokerJournalPublication publication) =>
        new(
            Bound(plan.RequestedProfileId, 64),
            BoundOptional(plan.EffectiveProfileId, 64),
            plan.RequestedAdmission,
            plan.EffectiveAdmission,
            plan.Scope.RequestedMechanism,
            plan.Scope.EffectiveMechanism,
            [.. plan.Scope.RequestedProcessIds],
            [.. plan.Scope.InitialViewProcessIds],
            plan.Scope.CapturesOutsideRequestedProcesses,
            plan.Scope.BroaderCaptureNeedsConsent,
            plan.Scope.BroaderCaptureAccepted,
            [
                .. plan.Scope.Sources.Take(32).Select(item => new BrokerEffectiveSourceSummary(
                    Bound(item.SourceId, 256),
                    item.ProcessScope,
                    [.. item.AppliedProcessIds],
                    item.CapturesOutsideRequestedProcesses,
                    Bound(item.Reason, 512))),
            ],
            Bound(plan.Scope.Disclosure, 1024),
            Bound(plan.CollectionStatement, 2048),
            quota,
            retention,
            [.. plan.Diagnostics.Take(32).Select(item => Bound(item, 512))],
            publication,
            Enum.IsDefined(publication) && quota.Validate() is null
                ? BrokerJournalPublicationPolicy.IntervalMilliseconds(publication, quota.MaximumDurationSeconds)
                : 0);

    private static string? BoundOptional(string? value, int maximumBytes) =>
        value is null ? null : Bound(value, maximumBytes);

    private static string Bound(string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        string cleaned = new(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        cleaned = cleaned.Trim();
        if (cleaned.Length == 0)
        {
            return "Unavailable.";
        }

        if (Encoding.UTF8.GetByteCount(cleaned) <= maximumBytes)
        {
            return cleaned;
        }

        const string suffix = "...";
        int length = cleaned.Length;
        while (length > 0
            && Encoding.UTF8.GetByteCount(cleaned.AsSpan(0, length)) + suffix.Length > maximumBytes)
        {
            length--;
            if (length > 0 && char.IsHighSurrogate(cleaned[length - 1]))
            {
                length--;
            }
        }

        return cleaned[..length].TrimEnd() + suffix;
    }
}
