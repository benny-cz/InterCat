namespace InterCat.Ui.Tests;

/// <summary>
/// Holds a worker partway, at the first step it reports from its own thread that a test picks: a package writing into
/// its unsaved folder, a finish between its steps. Let go, the worker goes on, and stops there if it was cancelled
/// meanwhile, as a worker that looks between its steps does.
/// </summary>
internal sealed class WorkHold : IDisposable
{
    private readonly ManualResetEventSlim released = new();
    private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int held;

    /// <summary>Done once the worker is held; a worker that never comes fails the test rather than hang it.</summary>
    public Task Reached => reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>Holds the worker at the first step it reports that <paramref name="at"/> picks.</summary>
    public IProgress<T> On<T>(IProgress<T> window, Func<T, bool> at, CancellationToken cancellation) =>
        new Step<T>(update =>
        {
            if (at(update)) Here(cancellation);
            window.Report(update);
        });

    /// <summary>Holds the worker here, the first time it comes here, until it is let go.</summary>
    public void Here(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref held, 1) != 0) return;
        reached.SetResult();

        // Held whether or not it is cancelled meanwhile: a worker partway through a step finishes the step first.
        released.Wait(CancellationToken.None);
        cancellation.ThrowIfCancellationRequested();
    }

    public void Release() => released.Set();

    public void Dispose()
    {
        released.Set();
        released.Dispose();
    }

    private sealed class Step<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
