using System.ComponentModel;
using System.Reflection;
using System.Runtime.Versioning;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

/// <summary>What a broker process is composed from. Production supplies the real machine; tests a scripted one.</summary>
[SupportedOSPlatform("windows")]
public sealed record BrokerProcessDependencies(
    BrokerRootRequest Root,
    IEtwSessionHost EtwHost,
    IEtwSessionReclaimer EtwReclaimer,
    IBrokerCapturePlanSource PlanSource,
    BrokerRuntimeIdentity RuntimeIdentity)
{
    public static BrokerProcessDependencies Production(BrokerOwnerIdentity owner)
    {
        var etw = new TraceEventSessionHost();
        return new(
            BrokerRootLocation.Production(owner.UserSid),
            etw,
            etw,
            new WindowsBrokerCapturePlanSource(
                new CapabilityInventoryProbe(new TdhEtwMetadataSource()),
                CapabilityInventoryProbe.DescribeEnvironment(etw.IsElevated)),
            BrokerRuntimeIdentity.Current());
    }
}

/// <summary>
/// The broker executable: parse the launch contract, compose root → ownership store → evidence runtime → coordinators,
/// and run the host. Every failure is mapped to a documented exit code with a sentence that names what to do.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrokerProcess
{
    public static string Version { get; } =
        typeof(BrokerProcess).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { Length: > 0 and <= 64 } informational
            ? informational
            : "0.0.0";

    /// <summary>Another broker holds this machine's ownership log: one live capture broker runs at a time.</summary>
    public const int ExitAnotherBrokerRunning = 6;

    /// <summary>How long a new broker waits for a finishing predecessor to release the ownership log.</summary>
    public static readonly TimeSpan PredecessorWait = TimeSpan.FromSeconds(15);

    /// <summary>The broker stopped on an error nothing mapped; its next start recovers what it left.</summary>
    public const int ExitUnexpected = 70;

    private const int ErrorSharingViolation = 32;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        BrokerLaunchOptions? options = BrokerLaunchOptions.Parse(args, out BrokerLaunchParseError? error);
        if (options is null)
        {
            switch (error!.Kind)
            {
                case BrokerLaunchParseErrorKind.NotServe:
                    await stderr.WriteLineAsync(
                        "The InterCat capture broker is started by InterCat when you begin a live capture; it is not run directly.")
                        .ConfigureAwait(false);
                    await stderr.WriteLineAsync(BrokerLaunchOptions.Usage).ConfigureAwait(false);
                    return (int)InterCatExitCode.PermissionOrCapabilityFailure;
                default:
                    await stderr.WriteLineAsync(error.Message).ConfigureAwait(false);
                    await stderr.WriteLineAsync(BrokerLaunchOptions.Usage).ConfigureAwait(false);
                    return (int)InterCatExitCode.InvalidInvocation;
            }
        }

        return await ServeAsync(
                options,
                BrokerProcessDependencies.Production(options.Owner),
                stdout,
                stderr,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the ownership log, waiting for a predecessor that is still finishing (a broker exits a few seconds after
    /// its last client). Null when another broker keeps holding it: that one is recording, and this one must not start.
    /// </summary>
    private static async Task<FileBrokerLifecycleStore?> OpenStoreAsync(
        WindowsBrokerRoot root,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            try
            {
                return new FileBrokerLifecycleStore(root);
            }
            catch (IOException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: ErrorSharingViolation })
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return null;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Serves until idle or cancelled. The pipe path is written to <paramref name="stdout"/> as one
    /// <c>listening &lt;path&gt;</c> line; diagnostics go to <paramref name="stderr"/>.
    /// </summary>
    public static async Task<int> ServeAsync(
        BrokerLaunchOptions options,
        BrokerProcessDependencies dependencies,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        Action<string>? onListening = null,
        TimeSpan? predecessorWait = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (!string.Equals(
            WindowsBrokerTokenIdentity.CanonicalizeSid(dependencies.Root.CapturingUserSid),
            WindowsBrokerTokenIdentity.CanonicalizeSid(options.Owner.UserSid),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The broker root must grant read access to the capturing owner.", nameof(dependencies));
        }

        var errorLock = new Lock();
        void Diagnostic(string line)
        {
            lock (errorLock)
            {
                stderr.WriteLine(line);
                stderr.Flush();
            }
        }

        WindowsBrokerRoot root;
        try
        {
            root = WindowsBrokerRoot.Provision(dependencies.Root);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or Win32Exception
            or InvalidOperationException or InvalidDataException)
        {
            Diagnostic($"The broker's protected data folder could not be used: {exception.Message}");
                Diagnostic("Check the error above before changing the protected folder. Do not delete it just to retry.");
            return (int)InterCatExitCode.PermissionOrCapabilityFailure;
        }

        using (root)
        {
            FileBrokerLifecycleStore? store;
            try
            {
                store = await OpenStoreAsync(root, predecessorWait ?? PredecessorWait, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                Diagnostic($"The broker's capture ownership log is unreadable and was left untouched: {exception.Message}");
                return (int)InterCatExitCode.CorruptedInput;
            }

            if (store is null)
            {
                Diagnostic(
                    $"Another InterCat capture broker still holds this machine's ownership log after {(predecessorWait ?? PredecessorWait).TotalSeconds:0} s; "
                    + "one live capture runs at a time.");
                return ExitAnotherBrokerRunning;
            }

            using (store)
            {
                if (store.Recovery.TruncatedTailBytes > 0 || store.Recovery.DiscardedCompactionBytes > 0)
                {
                    Diagnostic(
                        $"Ownership log recovered at its last complete record ({store.Recovery.TruncatedTailBytes} torn "
                        + $"byte(s), {store.Recovery.DiscardedCompactionBytes} interrupted-compaction byte(s) discarded).");
                }

                await using var runtime = new BrokerEvidenceCaptureRuntime(
                    root,
                    dependencies.EtwHost,
                    dependencies.EtwReclaimer);
                var registry = new PreparedPlanRegistry();
                using var preparation = new BrokerPreparationCoordinator(
                    dependencies.PlanSource,
                    registry,
                    dependencies.RuntimeIdentity);
                using var lifecycle = new BrokerLifecycleCoordinator(registry, store, runtime);
                var settings = new BrokerHostSettings
                {
                    Owner = options.Owner,
                    ServerInstanceId = options.ServerInstanceId,
                    ServerVersion = Version,
                    IdleExitAfter = options.IdleExitAfter,
                };
                var host = new BrokerHost(
                    preparation,
                    lifecycle,
                    settings,
                    log: entry => Diagnostic($"{entry.AtUtc:O} {entry.Kind}: {entry.Message}"),
                    evidenceDirectory: runtime.EvidenceDirectory,
                    liveHealth: runtime.ReadHealth);

                BrokerHostResult result;
                try
                {
                    result = await host.RunAsync(
                            path =>
                            {
                                stdout.WriteLine($"listening {path}");
                                stdout.Flush();
                                onListening?.Invoke(path);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    // Pipe creation: first-instance semantics refuse a name another process already holds.
                    Diagnostic($"The broker could not open its control pipe: {exception.Message}");
                    return (int)InterCatExitCode.PermissionOrCapabilityFailure;
                }
                catch (InvalidDataException exception)
                {
                    Diagnostic($"Recovery refused the ownership log: {exception.Message}");
                    return (int)InterCatExitCode.CorruptedInput;
                }

                // An interrupted capture is closed but not finalized; the milestones, not the state, decide.
                bool partial = result.Recovery.Items
                    .Select(item => item.Milestones)
                    .Concat(result.ShutdownStops.Select(stop => stop.Milestones))
                    .Any(milestones => !milestones.FullyFinalized);
                return partial
                    ? (int)InterCatExitCode.PartialResultSuccess
                    : (int)InterCatExitCode.Success;
            }
        }
    }
}
