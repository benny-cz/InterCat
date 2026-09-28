using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace InterCat.Capture.Windows;

/// <summary>
/// Pairs this machine's performance counter - the source clock of every live capture here (<see cref="EtwSourceClock"/>) -
/// with its precise wall clock, bracketing each wall-clock read between two counter reads so how far apart the pair may be
/// is measured (§8.1).
/// </summary>
public static partial class LocalClockReading
{
    /// <summary>The wall clock the readings read.</summary>
    public const string WallClock = "GetSystemTimePreciseAsFileTime";

    /// <summary>The wall clock's own resolution: a FILETIME counts 100-nanosecond intervals.</summary>
    public const long WallClockResolutionNanoseconds = 100;

    /// <summary>
    /// The tightest of <paramref name="attempts"/> bracketed readings: the counter midway between the two reads around
    /// one wall-clock read, that wall-clock reading as a UTC FILETIME, and how far the middle may be from the wall-clock
    /// read in counter ticks - half the bracketing interval widened by the counter's own tick, rounded up, so two reads
    /// on one tick still bound the pair by that tick.
    /// </summary>
    public static (long NativeTicks, long FileTimeUtc, long HalfIntervalTicks) Read(int attempts = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The precise wall clock is read on Windows.");
        }

        long bestInterval = long.MaxValue;
        long bestMiddle = 0;
        long bestFileTime = 0;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            long before = Stopwatch.GetTimestamp();
            GetSystemTimePreciseAsFileTime(out long fileTime);
            long after = Stopwatch.GetTimestamp();
            long interval = after - before;
            if (interval < bestInterval)
            {
                bestInterval = interval;
                bestMiddle = before + (interval / 2);
                bestFileTime = fileTime;
            }
        }

        return (bestMiddle, bestFileTime, (bestInterval + 2) / 2);
    }

    [LibraryImport("kernel32.dll")]
    private static partial void GetSystemTimePreciseAsFileTime(out long fileTime);
}

/// <summary>
/// The boot a live capture runs in. Windows counts its boots, but a count is no identity - a clone counts as its original
/// did - so the first capture of a boot mints a random token into a volatile registry key, which Windows deletes when it
/// restarts: every capture of one boot of one machine reads the same token, and no capture of another boot or machine can.
/// </summary>
public static class LocalBootIdentity
{
    /// <summary>The volatile key holding this boot's token, under HKEY_LOCAL_MACHINE; it lasts until Windows restarts.</summary>
    public const string TokenKey = @"SOFTWARE\InterCat.Boot";

    private const string TokenValue = "Token";

    private const string CountKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";

    /// <summary>
    /// This boot's token: the one a capture of this boot minted, or, when <paramref name="mint"/> is set and none exists, a
    /// new one kept in the volatile key, which needs a process that may write the machine's registry. Null when there is
    /// none and none could be kept.
    /// </summary>
    public static Guid? Token(bool mint)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            Guid? existing = Existing();
            if (existing is not null || !mint)
            {
                return existing;
            }

            // One process mints; another that raced it reads what the first kept.
            using var minting = new Mutex(initiallyOwned: false, @"Global\InterCat.BootToken");
            bool owned;
            try
            {
                owned = minting.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            try
            {
                if (Existing() is { } raced)
                {
                    return raced;
                }

                using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey key = machine.CreateSubKey(TokenKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile);
                Guid token = Guid.NewGuid();
                key.SetValue(TokenValue, token.ToString("D"), RegistryValueKind.String);
                return token;
            }
            finally
            {
                if (owned)
                {
                    minting.ReleaseMutex();
                }
            }
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException
            or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    /// <summary>How many times Windows reports it has booted, for people; null when it cannot be read. It is no identity.</summary>
    public static long? Count()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = machine.OpenSubKey(CountKey);
            return key?.GetValue("BootId") is int count and >= 0 ? count : null;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static Guid? Existing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = machine.OpenSubKey(TokenKey);
        return key?.GetValue(TokenValue) is string text && Guid.TryParse(text, out Guid token) && token != Guid.Empty ? token : null;
    }
}
