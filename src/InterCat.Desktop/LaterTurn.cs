using System.Runtime.CompilerServices;

namespace InterCat.Desktop;

/// <summary>
/// How the view awaits a read it started in the background: the answer is applied on a later turn of the thread that
/// asked, never inside the step that asked for it. A read that has finished by the time it is awaited would otherwise
/// continue inline, applying its answer within the setter, navigation or publication that started it - re-entering that
/// step before it ends, and skipping the "reading" state it had just published, which a person may never see then and a
/// test of it saw only when the read was slower than the step.
/// </summary>
internal static class LaterTurn
{
    private const ConfigureAwaitOptions OnTheAskingThreadLater =
        ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding;

    /// <summary>Awaits <paramref name="read"/> so that what follows runs on a later turn of the asking thread.</summary>
    public static ConfiguredTaskAwaitable<T> AnsweredLater<T>(this Task<T> read) => read.ConfigureAwait(OnTheAskingThreadLater);
}
