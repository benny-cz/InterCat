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

    /// <summary>
    /// Where a test holds an answer back once it is read, by the name of the step that asked for it, so it lands after what
    /// was asked since, as a busy machine can deliver it (§13.2's async publication); null in the product. It flows with
    /// the steps of the test that set it, never into another's.
    /// </summary>
    internal static readonly AsyncLocal<Func<string, Task>?> Holding = new();

    /// <summary>Awaits <paramref name="read"/> so that what follows runs on a later turn of the asking thread.</summary>
    public static ConfiguredTaskAwaitable<T> AnsweredLater<T>(this Task<T> read, [CallerMemberName] string asker = "") =>
        (Holding.Value is { } hold ? HeldAsync(read, hold, asker) : read).ConfigureAwait(OnTheAskingThreadLater);

    /// <summary>The answer once it is read and <paramref name="hold"/> lets it go, which a test decides by its asker.</summary>
    private static async Task<T> HeldAsync<T>(Task<T> read, Func<string, Task> hold, string asker)
    {
        T answer = await read.ConfigureAwait(false);
        await hold(asker).ConfigureAwait(false);
        return answer;
    }
}
