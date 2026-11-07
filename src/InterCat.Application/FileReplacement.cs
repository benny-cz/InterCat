using System.Diagnostics;

namespace InterCat.Application;

/// <summary>
/// Moves a staged file over the one it replaces, waiting out another handle's brief hold on it. Windows refuses to replace a
/// file any reader holds open, whatever that reader shares - InterCat's own window re-reading an investigation, or a scanner
/// reading what was just written - so the replace is tried again every <see cref="Pause"/> until <see cref="Patience"/> has
/// passed, and only then refused. What no hold explains - a missing file, a destination kept where it is not to be replaced -
/// is refused at once.
/// </summary>
public static class FileReplacement
{
    /// <summary>How long a replace waits out another handle's hold on its file before it is refused.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <summary>How long a replace waits between its tries.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(50);

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION, as an IOException carries them.
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/>, over it where <paramref name="overwrite"/> says,
    /// waiting out another handle's hold on either for up to <see cref="Patience"/>.
    /// </summary>
    public static void Move(string source, string destination, bool overwrite) => Move(source, destination, overwrite, Patience);

    /// <summary><see cref="Move(string, string, bool)"/>, waiting out a hold for up to <paramref name="patience"/>.</summary>
    internal static void Move(string source, string destination, bool overwrite, TimeSpan patience)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (UnauthorizedAccessException) when (Stopwatch.GetElapsedTime(started) < patience)
            {
                // A reader holding the destination refuses its replace as access denied, as a lack of rights would: both
                // are waited out, and a lack of rights is refused once the patience has passed.
                Thread.Sleep(Pause);
            }
            catch (IOException exception) when (exception.HResult is SharingViolation or LockViolation
                && Stopwatch.GetElapsedTime(started) < patience)
            {
                Thread.Sleep(Pause);
            }
        }
    }
}
