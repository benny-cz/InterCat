using InterCat.Domain;

namespace InterCat.Capture.Windows;

public sealed record EtlCaptureStartResult(
    bool Started,
    CaptureLifecycle State,
    IReadOnlyList<ProviderEnablementResult> Providers,
    string? FailureReason,
    IReadOnlyList<string> Degradations,
    int OtherActiveSessionCount,
    string OutputPath);

public sealed record EtlCaptureStopResult(
    CaptureLifecycle State,
    string OutputPath,
    long FileLengthBytes,

    /// <summary>
    /// Provider-reported loss, or null when the session's counters could not be read at stop. Null is not
    /// zero: an unreadable counter leaves the loss unknown, and a degradation records why (R3, R21).
    /// </summary>
    long? EventsLost,
    DateTimeOffset RecordingStartedUtc,
    DateTimeOffset RecordingStoppedUtc,
    bool DurationLimitReached,
    IReadOnlyList<string> Degradations);

/// <summary>
/// Owns one bounded-duration diagnostic ETL capture. It refuses elevation gaps, name collisions and
/// existing output paths, and stops only the exact unique session identity it created.
/// </summary>
public sealed class OwnedEtlFileCapture : IAsyncDisposable
{
    private readonly EtlFileCapturePlan plan;
    private readonly IEtlFileSessionHost host;
    private readonly TimeProvider clock;
    private readonly Lock gate = new();
    private readonly List<string> degradations = [];
    private readonly CancellationTokenSource lifetime = new();

    private IOwnedEtlFileSession? session;
    private Task? deadlineTask;
    private CaptureLifecycle state = CaptureLifecycle.Idle;
    private string outputPath = string.Empty;
    private DateTimeOffset recordingStartedUtc;
    private DateTimeOffset recordingStoppedUtc;
    private long? eventsLost;
    private bool durationLimitReached;
    private bool disposed;

    public OwnedEtlFileCapture(EtlFileCapturePlan plan, IEtlFileSessionHost host, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(host);
        this.plan = plan;
        this.host = host;
        this.clock = clock ?? TimeProvider.System;
    }

    public CaptureLifecycle State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public IReadOnlyList<string> Degradations
    {
        get
        {
            lock (gate)
            {
                return degradations.ToArray();
            }
        }
    }

    public Task<EtlCaptureStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Transition(CaptureLifecycle.Idle, CaptureLifecycle.Probing);

        try
        {
            outputPath = plan.GetValidatedOutputPath();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return Task.FromResult(Fail(exception.Message, [], 0));
        }

        if (host.IsElevated == false)
        {
            return Task.FromResult(Fail(
                "Starting an ETL session requires elevation. No file session was created.",
                [],
                0));
        }

        IReadOnlyList<string> active;
        try
        {
            active = host.ListActiveSessionNames();
        }
        catch (EtwSessionException exception)
        {
            return Task.FromResult(Fail($"Active sessions could not be listed: {exception.Message}", [], 0));
        }

        int otherSessions = 0;
        foreach (string name in active)
        {
            if (plan.Session.Identity.Owns(name))
            {
                return Task.FromResult(Fail(
                    $"A session named '{name}' already exists. InterCat refuses to adopt or restart it.",
                    [],
                    otherSessions));
            }

            otherSessions++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Transition(CaptureLifecycle.Probing, CaptureLifecycle.Starting);

        string? directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Task.FromResult(Fail("The ETL output path has no parent directory.", [], otherSessions));
        }

        try
        {
            Directory.CreateDirectory(directory);
            session = host.CreateExclusive(plan, outputPath);
        }
        catch (Exception exception) when (exception is EtwSessionException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Fail(exception.Message, [], otherSessions));
        }

        var providerResults = new List<ProviderEnablementResult>(plan.Session.Providers.Count);
        try
        {
            foreach (ProviderEnablementRequest request in plan.Session.Providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProviderEnablementResult result = session.Enable(request);
                providerResults.Add(result);
                if (!result.Enabled)
                {
                    AddDegradation($"{result.SourceId}: {result.FailureReason ?? "the provider refused enablement."}");
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CleanUpOwnedSession();
            string reason = exception is OperationCanceledException
                ? "The ETL start was cancelled; the session this attempt created was stopped."
                : $"Provider enablement failed: {exception.Message}";
            return Task.FromResult(Fail(reason, providerResults, otherSessions));
        }

        if (!providerResults.Any(result => result.Enabled))
        {
            CleanUpOwnedSession();
            return Task.FromResult(Fail(
                "No requested provider could be enabled, so no ETL capture was started.",
                providerResults,
                otherSessions));
        }

        recordingStartedUtc = clock.GetUtcNow();
        Transition(CaptureLifecycle.Starting, CaptureLifecycle.Recording);

        foreach (ProviderEnablementRequest request in plan.Session.Providers)
        {
            if (request.RequestCaptureState
                && !session.TryRequestCaptureState(request, out string? failureReason))
            {
                AddDegradation(
                    $"{request.SourceId}: state rundown was refused ({failureReason ?? "no reason reported"}).");
            }
        }

        deadlineTask = EnforceDurationLimitAsync(lifetime.Token);
        return Task.FromResult(new EtlCaptureStartResult(
            true,
            CaptureLifecycle.Recording,
            providerResults,
            null,
            Degradations,
            otherSessions,
            outputPath));
    }

    public Task<EtlCaptureStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (state is CaptureLifecycle.Closed)
            {
                return Task.FromResult(BuildStopResult());
            }

            state = CaptureLifecycle.Stopping;
        }

        CleanUpOwnedSession();
        recordingStoppedUtc = clock.GetUtcNow();
        lock (gate)
        {
            state = CaptureLifecycle.Closed;
        }

        lifetime.Cancel();
        return Task.FromResult(BuildStopResult());
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        if (State is not CaptureLifecycle.Closed)
        {
            _ = await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (deadlineTask is not null)
        {
            await Task.WhenAny(deadlineTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }

        lifetime.Dispose();
    }

    private async Task EnforceDurationLimitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(plan.Session.MaximumDuration, clock, cancellationToken).ConfigureAwait(false);
            durationLimitReached = true;
            AddDegradation(
                $"The diagnostic ETL reached its {plan.Session.MaximumDuration} duration limit and was stopped.");
            _ = await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // An explicit stop cancelled the deadline.
        }
    }

    private void CleanUpOwnedSession()
    {
        IOwnedEtlFileSession? current = Interlocked.Exchange(ref session, null);
        if (current is null)
        {
            return;
        }

        if (!plan.Session.Identity.Owns(current.SessionName))
        {
            AddDegradation(
                $"Refused to stop or dispose '{current.SessionName}': it is not the session this attempt created.");
            return;
        }

        try
        {
            current.StopSession();
            eventsLost = Math.Max(eventsLost ?? 0, current.ReadEventsLost());
        }
        catch (EtwSessionException exception)
        {
            AddDegradation($"The owned ETL session did not stop cleanly: {exception.Message}");
        }
        finally
        {
            current.Dispose();
        }
    }

    private EtlCaptureStartResult Fail(
        string reason,
        IReadOnlyList<ProviderEnablementResult> providers,
        int otherSessions)
    {
        lock (gate)
        {
            state = CaptureLifecycle.Closed;
        }

        return new(false, CaptureLifecycle.Closed, providers, reason, Degradations, otherSessions, outputPath);
    }

    private EtlCaptureStopResult BuildStopResult()
    {
        long length = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
        return new(
            CaptureLifecycle.Closed,
            outputPath,
            length,
            eventsLost,
            recordingStartedUtc,
            recordingStoppedUtc == default ? clock.GetUtcNow() : recordingStoppedUtc,
            durationLimitReached,
            Degradations);
    }

    private void Transition(CaptureLifecycle expected, CaptureLifecycle next)
    {
        lock (gate)
        {
            if (state != expected)
            {
                throw new InvalidOperationException($"An ETL capture in state {state} cannot move to {next}.");
            }

            state = next;
        }
    }

    private void AddDegradation(string reason)
    {
        lock (gate)
        {
            if (!degradations.Contains(reason, StringComparer.Ordinal))
            {
                degradations.Add(reason);
            }
        }
    }
}
