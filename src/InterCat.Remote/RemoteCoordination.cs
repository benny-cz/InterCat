using System.Net;

namespace InterCat.Remote;

/// <summary>
/// An agent's clock read across one request (`remote-agent-v1` §4.6): the controller's clock when it sent the request and
/// when the answer came, and the agent's when it answered. The agent read its clock between the controller's two
/// readings, so the sample says how far the agent's clock reads ahead of the controller's within half the round trip and
/// half the millisecond the agent writes its reading in. A delay on the way widens that bound and never moves the
/// reading: a barrier, never proof that two clocks agree.
/// </summary>
public sealed record RemoteClockSample(DateTimeOffset SentUtc, DateTimeOffset AnsweredUtc, DateTimeOffset ReceivedUtc)
{
    /// <summary>Half the millisecond an agent's reading is written in: the reading lies within it.</summary>
    private static readonly TimeSpan HalfReading = TimeSpan.FromMilliseconds(0.5);

    public TimeSpan RoundTrip => ReceivedUtc - SentUtc;

    /// <summary>The bound on <see cref="AgentAhead"/>, a half-width.</summary>
    public TimeSpan Within => (RoundTrip / 2) + HalfReading;

    /// <summary>How far the agent's clock read ahead of the controller's: negative where it read behind.</summary>
    public TimeSpan AgentAhead => AnsweredUtc + HalfReading - (SentUtc + (RoundTrip / 2));

    /// <summary>
    /// How far this agent's clock reads ahead of the one <paramref name="reference"/> read, and within what: each sample's
    /// bound, added, for two samples taken together, as one barrier's are.
    /// </summary>
    public (TimeSpan Ahead, TimeSpan Within) Against(RemoteClockSample reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return (AgentAhead - reference.AgentAhead, Within + reference.Within);
    }
}

/// <summary>
/// An agent a controller records on: where it listens, the pin its key has, what to call its host, and - where a rotation
/// of the controller's key is under way - the key that agent knows the controller by; null for the run's own.
/// </summary>
public sealed record RemoteCaptureHost(IPEndPoint Endpoint, RemotePin Agent, string Name, RemoteIdentity? Controller = null);

/// <summary>Where one host's part of a capture stopped short.</summary>
public enum RemoteHostStage
{
    /// <summary>No agent with the key expected answered.</summary>
    Connect,

    /// <summary>The agent refused or could not prepare the capture.</summary>
    Prepare,

    /// <summary>The agent refused or could not start it.</summary>
    Start,

    /// <summary>The capture's lease could not be kept: the agent was not reached again before it lapsed, or refused.</summary>
    Keep,

    /// <summary>The agent could not be reached, or refused, when the controller stopped the capture.</summary>
    Stop,
}

/// <summary>Why a host's part stopped short, at which stage, in the agent's or the connection's own words.</summary>
public sealed record RemoteHostFailure(RemoteHostStage Stage, string Reason);

/// <summary>What happened to a host's part as a capture across several agents went.</summary>
public enum RemoteHostStep
{
    Prepared,
    Started,
    Recording,
    Reconnected,
    Closed,
    Failed,
}

/// <summary>One step of one host's part, as it happened.</summary>
public sealed record RemoteHostEvent(RemoteHostStep Step, RemoteHostCapture Host);

/// <summary>One host's part in a capture across several agents: what it prepared, started, answered and became.</summary>
public sealed class RemoteHostCapture
{
    internal RemoteHostCapture(RemoteCaptureHost host) => Host = host;

    public RemoteCaptureHost Host { get; }

    /// <summary>What the host prepared - its preview: the profile its broker will record and what it collects.</summary>
    public RemotePreparedCapture? Prepared { get; internal set; }

    public Guid? CaptureId { get; internal set; }

    /// <summary>The start's acknowledgment, read across the start.</summary>
    public RemoteClockSample? Start { get; internal set; }

    /// <summary>
    /// The agent's last answer of the capture, read across it - its stop's, where the controller stopped it - from an agent
    /// of version 1.3 or later; null from one before.
    /// </summary>
    public RemoteClockSample? End { get; internal set; }

    /// <summary>Where the capture stood at the agent's last answer.</summary>
    public RemoteCaptureState? State { get; internal set; }

    /// <summary>Whether the controller stopped it, rather than its duration or its agent.</summary>
    public bool StoppedByController { get; internal set; }

    /// <summary>How many times the controller made its connection to the agent again while the capture's lease held.</summary>
    public int Reconnections { get; internal set; }

    /// <summary>Why this host's part stopped short, or null where it did not.</summary>
    public RemoteHostFailure? Failure { get; internal set; }

    public bool Started => CaptureId is not null;

    internal RemoteAgentConnection? Connection { get; set; }

    /// <summary>Until when, by the controller's clock, the agent holds the capture's lease at the least.</summary>
    internal DateTimeOffset HeldUntil { get; set; }
}

/// <summary>
/// One capture across several agents (`remote-agent-v1` §4.6), each its own capture on its own host. Every host is
/// prepared at once, its preview kept; every host prepared is started as one barrier - each start sent before any answer
/// is awaited, and each acknowledgment read with the controller's clock either side; each capture's lease is kept, and a
/// connection lost is made again while the lease holds; and when the controller stops, every capture still open is
/// stopped together, each stop's answer read as the start's was. A host that cannot be reached, refuses or fails stops
/// only its own part: the others record on, and every host says what became of its part. With one host it is the
/// capture `icat remote capture` makes of one agent.
/// </summary>
public static class RemoteCoordinatedCapture
{
    /// <summary>How long a lost connection waits before it is made again.</summary>
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Records <paramref name="request"/> on every host of <paramref name="hosts"/> as <paramref name="controller"/>, keeping a
    /// control lease of <paramref name="lease"/> on each, until every capture closed or <paramref name="stop"/> stops them.
    /// </summary>
    public static async Task<IReadOnlyList<RemoteHostCapture>> RunAsync(
        IReadOnlyList<RemoteCaptureHost> hosts,
        RemoteIdentity controller,
        RemoteCaptureRequest request,
        TimeSpan lease,
        IProgress<RemoteHostEvent>? progress,
        CancellationToken stop,
        TimeSpan? connectTimeout = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(request);
        if (hosts.Count == 0 || hosts.Select(host => host.Endpoint).Distinct().Count() != hosts.Count)
        {
            throw new ArgumentException("A capture is recorded on at least one agent, each named once.", nameof(hosts));
        }

        var run = new Run(controller, lease, connectTimeout ?? TimeSpan.FromSeconds(10), time ?? TimeProvider.System, progress);
        RemoteHostCapture[] parts = [.. hosts.Select(host => new RemoteHostCapture(host))];
        try
        {
            await Task.WhenAll(parts.Select(part => run.PrepareAsync(part, request, stop))).ConfigureAwait(false);

            // A preparation never started lapses on its agent on its own; stopped now, nothing starts.
            RemoteHostCapture[] prepared = [.. parts.Where(part => part.Prepared is not null)];
            if (prepared.Length > 0 && !stop.IsCancellationRequested)
            {
                // The barrier: every start is sent before any answer is awaited.
                Task[] starting = [.. prepared.Select(run.StartAsync)];
                await Task.WhenAll(starting).ConfigureAwait(false);
                await Task.WhenAll(prepared.Where(part => part.Started).Select(part => run.KeepAsync(part, stop))).ConfigureAwait(false);
            }

            return parts;
        }
        finally
        {
            foreach (RemoteHostCapture part in parts)
            {
                if (part.Connection is { } connection)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    part.Connection = null;
                }
            }
        }
    }

    private sealed class Run(RemoteIdentity controller, TimeSpan lease, TimeSpan connectTimeout, TimeProvider time, IProgress<RemoteHostEvent>? progress)
    {
        public async Task PrepareAsync(RemoteHostCapture part, RemoteCaptureRequest request, CancellationToken stop)
        {
            RemoteHostStage stage = RemoteHostStage.Connect;
            try
            {
                part.Connection = await RemoteAgentConnection
                    .ConnectAsync(part.Host.Endpoint, part.Host.Controller ?? controller, part.Host.Agent, connectTimeout, stop).ConfigureAwait(false);
                stage = RemoteHostStage.Prepare;
                part.Prepared = await part.Connection.PrepareCaptureAsync(request, stop).ConfigureAwait(false);
                Say(RemoteHostStep.Prepared, part);
            }
            catch (RemoteAgentException exception)
            {
                Fail(part, stage, exception.Message);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                Fail(part, stage, "Stopped before the capture started.");
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                Fail(part, stage, "The connection ended before the agent answered: " + exception.Message);
            }
        }

        /// <summary>
        /// Starts the part's prepared capture and reads the acknowledgment across it. A start is never cancelled once
        /// sent: one the agent made would otherwise record unknown until its lease lapsed, so it is answered and then
        /// stopped with the rest.
        /// </summary>
        public async Task StartAsync(RemoteHostCapture part)
        {
            DateTimeOffset sent = time.GetUtcNow();
            try
            {
                RemoteCaptureStarted started = await part.Connection!
                    .StartCaptureAsync(part.Prepared!.Prepared, lease, CancellationToken.None).ConfigureAwait(false);
                DateTimeOffset received = time.GetUtcNow();
                part.CaptureId = started.CaptureId;
                part.Start = new RemoteClockSample(sent, started.AcknowledgedUtc, received);
                part.HeldUntil = sent + lease;
                Say(RemoteHostStep.Started, part);
            }
            catch (RemoteAgentException exception)
            {
                Fail(part, RemoteHostStage.Start, exception.Message);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Whether the agent started it is not known; its lease, never renewed, stops it there.
                Fail(part, RemoteHostStage.Start, "The connection ended before the agent answered the start: " + exception.Message);
            }
        }

        /// <summary>Renews the part's lease every third of it until its capture closes, or stops it when the controller stops.</summary>
        public async Task KeepAsync(RemoteHostCapture part, CancellationToken stop)
        {
            TimeSpan renewal = TimeSpan.FromSeconds(Math.Max(1, lease.TotalSeconds / 3));
            while (true)
            {
                try
                {
                    await Task.Delay(renewal, time, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (await AskAsync(part, stopping: true).ConfigureAwait(false))
                    {
                        Say(RemoteHostStep.Closed, part);
                    }

                    return;
                }

                if (!await AskAsync(part, stopping: false).ConfigureAwait(false))
                {
                    return;
                }

                if (part.State!.State == RemoteCaptureLifecycle.Closed)
                {
                    Say(RemoteHostStep.Closed, part);
                    return;
                }

                Say(RemoteHostStep.Recording, part);
            }
        }

        /// <summary>
        /// Renews or stops the part's capture and reads the agent's answer across it, making the connection again where it
        /// was lost, for as long as the lease the agent holds has not lapsed. False where the part stopped short.
        /// </summary>
        private async Task<bool> AskAsync(RemoteHostCapture part, bool stopping)
        {
            RemoteHostStage stage = stopping ? RemoteHostStage.Stop : RemoteHostStage.Keep;
            while (true)
            {
                try
                {
                    if (part.Connection is null)
                    {
                        TimeSpan left = part.HeldUntil - time.GetUtcNow();
                        if (left <= TimeSpan.Zero)
                        {
                            break;
                        }

                        part.Connection = await RemoteAgentConnection.ConnectAsync(part.Host.Endpoint, part.Host.Controller ?? controller,
                            part.Host.Agent, left < connectTimeout ? left : connectTimeout, CancellationToken.None).ConfigureAwait(false);
                        part.Reconnections++;
                        Say(RemoteHostStep.Reconnected, part);
                    }

                    DateTimeOffset sent = time.GetUtcNow();
                    RemoteCaptureState state = stopping
                        ? await part.Connection.StopCaptureAsync(part.CaptureId!.Value, CancellationToken.None).ConfigureAwait(false)
                        : await part.Connection.RenewCaptureAsync(part.CaptureId!.Value, CancellationToken.None).ConfigureAwait(false);
                    DateTimeOffset received = time.GetUtcNow();
                    part.State = state;
                    if (state.AnsweredUtc is { } answered)
                    {
                        part.End = new RemoteClockSample(sent, answered, received);
                    }

                    // The agent renewed the lease when it answered, after this controller sent: the lease holds at least this long.
                    part.HeldUntil = sent + lease;
                    part.StoppedByController |= stopping;
                    return true;
                }
                catch (RemoteAgentException refused) when (refused.Code is not null)
                {
                    Fail(part, stage, refused.Message);
                    return false;
                }
                catch (Exception exception) when (exception is RemoteAgentException or IOException or ObjectDisposedException)
                {
                    // The connection was lost: it is made again while the lease holds.
                    if (part.Connection is { } lost)
                    {
                        part.Connection = null;
                        await lost.DisposeAsync().ConfigureAwait(false);
                    }

                    if (time.GetUtcNow() >= part.HeldUntil)
                    {
                        break;
                    }

                    await Task.Delay(Retry, time, CancellationToken.None).ConfigureAwait(false);
                }
            }

            Fail(part, stage, "The agent could not be reached again before the capture's lease lapsed; it stops a capture whose "
                + "lease lapsed on its own.");
            return false;
        }

        private void Fail(RemoteHostCapture part, RemoteHostStage stage, string reason)
        {
            part.Failure = new RemoteHostFailure(stage, reason);
            Say(RemoteHostStep.Failed, part);
        }

        private void Say(RemoteHostStep step, RemoteHostCapture part) => progress?.Report(new RemoteHostEvent(step, part));
    }
}
