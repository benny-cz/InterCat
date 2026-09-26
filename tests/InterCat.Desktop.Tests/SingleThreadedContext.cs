using System.Collections.Concurrent;

namespace InterCat.Desktop.Tests;

/// <summary>
/// Runs an asynchronous test on one thread with a message loop, the way the UI dispatcher runs a workspace in the app.
/// A count the workspace awaits then resumes only when the test itself awaits, never on a pool thread beside the test's
/// next step - so a test can see the moment before a count lands, and a navigation is never half-overwritten by it.
/// </summary>
internal sealed class SingleThreadedContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // The test has finished; work that resumes after it (a disposed workspace's count) is dropped.
        }
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("A test workspace never waits synchronously on its own thread.");

    public static void Run(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var context = new SingleThreadedContext();
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(context);
        try
        {
            Task test = body();
            _ = test.ContinueWith(_ => context.queue.CompleteAdding(), TaskScheduler.Default);
            foreach ((SendOrPostCallback callback, object? state) in context.queue.GetConsumingEnumerable())
            {
                callback(state);
            }

            test.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
            context.queue.Dispose();
        }
    }
}
