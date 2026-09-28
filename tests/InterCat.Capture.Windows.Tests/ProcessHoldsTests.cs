using System.Diagnostics;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// A session's process filter names process IDs, and each is held open while the session lives, so no other process can be
/// given one of them and enter its scope (ADR-037).
/// </summary>
public sealed class ProcessHoldsTests
{
    [Fact(DisplayName = "R22: a process filter is held to running processes, each held open so its ID stays its own")]
    public void AFilterHoldsRunningProcessesOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var holds = new ProcessHolds();
        Assert.True(holds.TryHold([Environment.ProcessId], out string? none));
        Assert.Null(none);
        Assert.Equal([Environment.ProcessId], holds.Held);

        // A process that has exited, its ID still reserved by this test's own handle to it: it is no longer the process
        // that was named, so it is refused, and what was held before stays held.
        using Process exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        exited.WaitForExit();
        Assert.False(holds.TryHold([exited.Id], out string? gone));
        Assert.Contains($"process {exited.Id} has already exited", gone, StringComparison.Ordinal);
        Assert.Equal([Environment.ProcessId], holds.Held);

        // No process has this ID: Windows gives IDs in multiples of four, far below it.
        Assert.False(holds.TryHold([int.MaxValue - 3], out string? missing));
        Assert.Contains("is not running, so its ID could be given to any process", missing, StringComparison.Ordinal);

        holds.Dispose();
        Assert.Empty(holds.Held);
        Assert.Throws<ObjectDisposedException>(() => holds.TryHold([Environment.ProcessId], out _));
    }
}
