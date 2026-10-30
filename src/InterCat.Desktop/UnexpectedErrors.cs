using System.Runtime.ExceptionServices;
using Avalonia.Controls;
using Avalonia.Threading;
using InterCat.Application;

namespace InterCat.Desktop;

/// <summary>A window that says, where it says what its actions did, an error none of them expected (§20.6 Internal).</summary>
internal interface IStatesUnexpected
{
    /// <summary>Says it, and whether it could: a window that has closed says nothing.</summary>
    bool SayUnexpected(Exception exception);
}

/// <summary>
/// An error no action of the window expected, contained where the window runs (§20.6 Internal). An exception that reaches
/// the dispatcher - from an event handler, or from an action started and not waited for - is a defect in InterCat, so the
/// window nearest the person says so and goes on running, rather than closing with everything it had open. The application
/// contains them and a test never does, so a defect fails the test that meets it. One no open window can say goes on to
/// end the program, as running out of memory does: neither is safe to go on from unsaid.
/// </summary>
internal static class UnexpectedErrors
{
    /// <summary>How a window says such an error: what stopped, the error itself, and that the window goes on.</summary>
    public static string Sentence(Exception exception) =>
        InternalErrorText.Stopped("What was being done", exception) + " The window keeps running.";

    /// <summary>
    /// Contains every exception that reaches <paramref name="dispatcher"/> that <paramref name="said"/> says, counting each
    /// in <paramref name="counted"/> for the window's support bundle, from now until what this returns is disposed: whatever
    /// it cannot say, and running out of memory, go on as if it were not there.
    /// </summary>
    public static IDisposable Contain(Dispatcher dispatcher, Func<Exception, bool> said, InternalErrorTally counted)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(said);
        ArgumentNullException.ThrowIfNull(counted);
        void Contained(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (!e.Handled && e.Exception is not OutOfMemoryException && said(e.Exception))
            {
                e.Handled = true;
                counted.Count(e.Exception);
            }
        }

        dispatcher.UnhandledException += Contained;
        return new Release(() => dispatcher.UnhandledException -= Contained);
    }

    /// <summary>
    /// Says the error in the window nearest the person that can: the active one, or the window owning the active one, as a
    /// dialog's owner does; then any other, in the order given. False when none could.
    /// </summary>
    public static bool SayIn(IReadOnlyList<Window> windows, Func<Window, bool> active, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(active);
        IEnumerable<Window> nearest = windows.Where(active).SelectMany(Owners).Concat(windows);
        foreach (IStatesUnexpected window in nearest.OfType<IStatesUnexpected>())
        {
            if (window.SayUnexpected(exception))
            {
                return true;
            }
        }

        return false;

        static IEnumerable<Window> Owners(Window window)
        {
            for (Window? owner = window; owner is not null; owner = owner.Owner as Window)
            {
                yield return owner;
            }
        }
    }

    /// <summary>
    /// Watches an action the window started and does not wait for: an exception it did not expect ends it on the dispatcher,
    /// as an event handler's would, so it is said rather than lost with the task, and a test that meets it fails. A cancelled
    /// action ended as it was asked to.
    /// </summary>
    public static void Watched(this Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = task.ContinueWith(
            static ended => Dispatcher.UIThread.Post(() =>
                ExceptionDispatchInfo.Throw(ended.Exception!.InnerExceptions is [var only] ? only : ended.Exception)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
