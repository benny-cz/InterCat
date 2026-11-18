namespace InterCat.CaptureBroker;

/// <summary>
/// What a console close, a logoff or a system shutdown asks of the broker, as Windows signals it: stop the host, which
/// stops and finalizes every capture still recording (contract broker-v1 §5.5), and hold the signal until the host has
/// finished or <paramref name="grace"/> has passed - Windows ends the process once the signal's handler returns, and
/// allows it only a few seconds. A host that has not finished by then leaves what it was recording to the next broker's
/// recovery.
/// </summary>
/// <param name="shutdown">The host's shutdown, which the signal cancels.</param>
/// <param name="grace">How long the signal is held for the host to finish.</param>
public sealed class BrokerTermination(CancellationTokenSource shutdown, TimeSpan grace) : IDisposable
{
    /// <summary>How long a signal is held: within the five seconds Windows gives a logoff or shutdown handler.</summary>
    public static readonly TimeSpan WindowsGrace = TimeSpan.FromSeconds(4.5);

    private readonly ManualResetEventSlim finished = new();

    /// <summary>Stops the host and waits for it to have finished, within the grace; whether it had.</summary>
    public bool Terminate()
    {
        shutdown.Cancel();
        return finished.Wait(grace);
    }

    /// <summary>Says the host has finished, which releases a signal held for it.</summary>
    public void Finished() => finished.Set();

    public void Dispose() => finished.Dispose();
}
