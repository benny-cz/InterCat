using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace InterCat.CaptureBroker;

/// <summary>What to launch as the broker: its executable and any arguments that precede <c>serve</c>'s own.</summary>
public sealed record BrokerLaunchTarget(string ExecutablePath, IReadOnlyList<string> LeadingArguments)
{
    public const string ExecutableName = "InterCat.CaptureBroker.exe";

    /// <summary>The broker installed beside the running client, or null when it is not there.</summary>
    public static BrokerLaunchTarget? BesideCurrentProcess()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        return File.Exists(path) ? new(path, ["serve"]) : null;
    }
}

/// <summary>Why a broker could not be reached. Each is a designed state (plan §20.6), not an error dialog.</summary>
public enum BrokerLaunchFailure
{
    /// <summary>No broker executable where the client expected it.</summary>
    BrokerNotInstalled = 1,

    /// <summary>The user declined the elevation prompt. Nothing was started.</summary>
    ElevationDeclined = 2,

    /// <summary>Windows could not start the broker for another reason.</summary>
    LaunchFailed = 3,

    /// <summary>The broker started and exited before listening; its exit code says why.</summary>
    BrokerExited = 4,

    /// <summary>The broker is running but its pipe did not appear in time.</summary>
    NotListening = 5,

    /// <summary>The pipe is served by a process other than the broker this client launched.</summary>
    ServerNotTheBroker = 6,
}

public sealed class BrokerLaunchException(BrokerLaunchFailure failure, string message, int? exitCode = null)
    : Exception(message)
{
    public BrokerLaunchFailure Failure { get; } = failure;

    public int? BrokerExitCode { get; } = exitCode;
}

/// <summary>
/// A launched broker and the authenticated connection to it. The process handle is held for the connection's lifetime,
/// so the process ID the server check compared cannot be reused by another process meanwhile.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerConnection(Process broker, WindowsBrokerPipeClient client) : IAsyncDisposable
{
    public WindowsBrokerPipeClient Client { get; } = client;

    public int BrokerProcessId { get; } = broker.Id;

    public bool BrokerHasExited => broker.HasExited;

    public async ValueTask DisposeAsync()
    {
        // Closing the connection is not a stop: the broker keeps recording until stopped, its lease expires, or it
        // exits idle with nothing recording.
        await Client.DisposeAsync().ConfigureAwait(false);
        broker.Dispose();
    }
}

/// <summary>
/// The client half of the broker's launch contract (contract broker-v1 §5.5): start the broker elevated with the
/// caller's own SID and logon session, keep its process handle, and connect only to a pipe that process serves.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsBrokerLauncher
{
    public static readonly TimeSpan DefaultListenTimeout = TimeSpan.FromSeconds(30);

    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan ConnectAttempt = TimeSpan.FromMilliseconds(250);

    /// <summary>The arguments <c>serve</c> receives for this caller; public so the launch contract is testable.</summary>
    public static IReadOnlyList<string> BuildArguments(
        BrokerLaunchTarget target,
        BrokerOwnerIdentity owner,
        Guid instance,
        TimeSpan? idleExit)
    {
        ArgumentNullException.ThrowIfNull(target);
        List<string> arguments =
        [
            .. target.LeadingArguments,
            "--owner-sid", owner.UserSid,
            "--owner-logon-session", "0x" + owner.LogonSessionId.ToString("X", CultureInfo.InvariantCulture),
            "--instance", instance.ToString("D"),
        ];
        if (idleExit is { } idle)
        {
            arguments.Add("--idle-exit-seconds");
            arguments.Add(((int)idle.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    /// <summary>
    /// Launches the broker (Windows shows the elevation prompt unless the caller is already elevated) and returns an
    /// authenticated connection. Every failure is a <see cref="BrokerLaunchException"/> naming what to do.
    /// </summary>
    public static async Task<BrokerConnection> LaunchAsync(
        BrokerLaunchTarget target,
        TimeSpan? idleExit = null,
        TimeSpan? listenTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!File.Exists(target.ExecutablePath))
        {
            throw new BrokerLaunchException(
                BrokerLaunchFailure.BrokerNotInstalled,
                $"The InterCat capture broker was not found at '{target.ExecutablePath}'. Reinstall InterCat to capture live; "
                + "opening saved sessions does not need it.");
        }

        BrokerOwnerIdentity owner = WindowsBrokerTokenIdentity.ReadCurrentProcess().Owner;
        Guid instance = Guid.NewGuid();
        var start = new ProcessStartInfo(target.ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string argument in BuildArguments(target, owner, instance, idleExit))
        {
            start.ArgumentList.Add(argument);
        }

        Process broker;
        try
        {
            broker = Process.Start(start)
                ?? throw new BrokerLaunchException(BrokerLaunchFailure.LaunchFailed, "Windows did not start the capture broker.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            throw new BrokerLaunchException(
                BrokerLaunchFailure.ElevationDeclined,
                "Live capture needs administrator approval, which was not given. Nothing was started; saved sessions "
                + "can still be opened.");
        }
        catch (Win32Exception exception)
        {
            throw new BrokerLaunchException(
                BrokerLaunchFailure.LaunchFailed,
                $"Windows could not start the capture broker: {exception.Message}");
        }

        try
        {
            WindowsBrokerPipeClient client = await ConnectAsync(
                    broker, $"InterCat.Broker.v1.{instance:N}", listenTimeout ?? DefaultListenTimeout, cancellationToken)
                .ConfigureAwait(false);
            return new(broker, client);
        }
        catch
        {
            broker.Dispose();
            throw;
        }
    }

    /// <summary>What a broker exit code means to the person who asked for a capture (contract broker-v1 §5.5).</summary>
    public static string DescribeExit(int exitCode) => exitCode switch
    {
        2 => "The capture broker rejected how it was started. This InterCat installation may be damaged; reinstall it.",
        3 => "The capture broker could not use its protected data folder, or its control pipe name was already taken. "
            + "If %ProgramData%\\InterCat exists and was not created by InterCat, an administrator must remove it.",
        4 => "The capture broker's ownership log is unreadable. It was left untouched for diagnosis; live capture "
            + "cannot start until an administrator repairs or removes it.",
        _ => $"The capture broker exited unexpectedly (code {exitCode}).",
    };

    private static async Task<WindowsBrokerPipeClient> ConnectAsync(
        Process broker,
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (broker.HasExited)
            {
                int exitCode = broker.ExitCode;
                throw new BrokerLaunchException(BrokerLaunchFailure.BrokerExited, DescribeExit(exitCode), exitCode);
            }

            try
            {
                return await WindowsBrokerPipeClient
                    .ConnectAsync(pipeName, broker.Id, ConnectAttempt, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BrokerServerIdentityException exception)
            {
                throw new BrokerLaunchException(BrokerLaunchFailure.ServerNotTheBroker, exception.Message);
            }
            catch (TimeoutException) when (deadline.Elapsed < timeout)
            {
                // The broker is still recovering or provisioning; its pipe appears only after recovery finishes.
            }
            catch (TimeoutException)
            {
                throw new BrokerLaunchException(
                    BrokerLaunchFailure.NotListening,
                    $"The capture broker started but did not accept a connection within {timeout.TotalSeconds:0} s. "
                    + "It may still be recovering a previous capture; try again shortly.");
            }
        }
    }
}
