using System.Diagnostics;

namespace InterCat.Sdk;

/// <summary>
/// One operation of the application's own (`contracts/sdk-v1.md` §3): its start, under a parent where it has one, and its
/// completion with a status - an assertion the application makes, whose authenticity is its own. Nothing is raised while
/// no session listens.
/// </summary>
public sealed class InterCatOperation
{
    private int completed;

    private InterCatOperation(Guid id, Guid parent, string name)
    {
        Id = id;
        Parent = parent;
        Name = name;
    }

    /// <summary>The operation's identity, unique to it.</summary>
    public Guid Id { get; }

    /// <summary>The operation it is part of; the empty identity where none.</summary>
    public Guid Parent { get; }

    /// <summary>The operation's name, as the application gives it.</summary>
    public string Name { get; }

    /// <summary>Starts an operation named <paramref name="name"/>, under <paramref name="parent"/> where it has one, saying so.</summary>
    public static InterCatOperation Start(string name, Guid parent = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var operation = new InterCatOperation(Guid.NewGuid(), parent, name);
        InterCatSdkEventSource log = InterCatSdkEventSource.Log;
        if (log.IsEnabled())
        {
            log.OperationStarted(parent, Environment.ProcessId, operation.Id, Stopwatch.GetTimestamp(), name);
        }

        return operation;
    }

    /// <summary>
    /// Says the operation completed with <paramref name="status"/> - 0 its success, any other value a failure in the
    /// application's own code - once: a second completion says nothing.
    /// </summary>
    public void Complete(long status = 0)
    {
        InterCatSdkEventSource log = InterCatSdkEventSource.Log;
        if (Interlocked.Exchange(ref completed, 1) == 0 && log.IsEnabled())
        {
            log.OperationCompleted(Environment.ProcessId, Id, status, Stopwatch.GetTimestamp());
        }
    }
}
