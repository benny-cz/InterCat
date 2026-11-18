using InterCat.Desktop;

namespace InterCat.Ui.Tests;

/// <summary>
/// Holds answers a window's steps read, once armed by the name of the step that awaits them, each until its own gate is
/// opened - as a busy machine delivers an answer late, after what was asked since (§13.2's async publication). Installed as
/// <see cref="LaterTurn.Holding"/> it flows with the test that installed it; the test takes it off again when it ends,
/// since a headless window test's thread runs the next test.
/// </summary>
internal sealed class HeldReads : IDisposable
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Queue<TaskCompletionSource>> armed = new(StringComparer.Ordinal);
    private int held;

    private HeldReads()
    {
    }

    /// <summary>How many armed answers were read and held so far.</summary>
    public int Held
    {
        get
        {
            lock (gate)
            {
                return held;
            }
        }
    }

    public static HeldReads Install()
    {
        var reads = new HeldReads();
        LaterTurn.Holding.Value = reads.Answer;
        return reads;
    }

    /// <summary>Holds the next answer the step named <paramref name="asker"/> reads, until the gate returned is opened.</summary>
    public TaskCompletionSource Arm(string asker)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (!armed.TryGetValue(asker, out Queue<TaskCompletionSource>? gates))
            {
                armed[asker] = gates = new();
            }

            gates.Enqueue(opened);
        }

        return opened;
    }

    public void Dispose()
    {
        LaterTurn.Holding.Value = null;
        lock (gate)
        {
            foreach (TaskCompletionSource opened in armed.Values.SelectMany(gates => gates))
            {
                opened.TrySetResult();
            }

            armed.Clear();
        }
    }

    private Task Answer(string asker)
    {
        lock (gate)
        {
            if (!armed.TryGetValue(asker, out Queue<TaskCompletionSource>? gates) || gates.Count == 0)
            {
                return Task.CompletedTask;
            }

            held++;
            return gates.Dequeue().Task;
        }
    }
}
