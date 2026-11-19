using System.ComponentModel;
using System.Runtime.Versioning;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

/// <summary>What an installer starts the broker for (ADR-115, broker-v1 §5.10).</summary>
public enum BrokerInstallCommand
{
    /// <summary>Pre-create the root with its final security, exactly as a serving broker's first step does.</summary>
    ProvisionRoot = 1,

    /// <summary>Stop what a previous broker left recording, then remove the root when it holds nothing kept.</summary>
    Cleanup = 2,
}

/// <summary>
/// An install-time command line: the command, the user the root grants read access to - the user installing or removing
/// InterCat - and, for cleanup, whether the captures recorded in the root go with it.
/// </summary>
public sealed record BrokerInstallOptions(BrokerInstallCommand Command, string OwnerSid, bool RemoveEvidence)
{
    public const string ProvisionRootCommand = "provision-root";

    public const string CleanupCommand = "cleanup";

    public static string Usage =>
        $"""
        Usage: InterCat.CaptureBroker {ProvisionRootCommand} --owner-sid <SID>
               InterCat.CaptureBroker {CleanupCommand} --owner-sid <SID> [--remove-evidence]

          --owner-sid        The user the broker's folder grants read access to: the one installing or removing InterCat.
          --remove-evidence  Remove the folder with every capture recorded in it; without it, a folder holding a capture
                             is kept.
        """;

    /// <summary>True when the command line names an install-time command, whatever follows it.</summary>
    public static bool IsInstallCommand(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && args[0] is ProvisionRootCommand or CleanupCommand;

    /// <summary>Parses an install-time command line. Returns null with a sentence for anything but a complete one.</summary>
    public static BrokerInstallOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        if (!IsInstallCommand(args))
        {
            error = $"Name '{ProvisionRootCommand}' or '{CleanupCommand}'.";
            return null;
        }

        BrokerInstallCommand command = args[0] == ProvisionRootCommand
            ? BrokerInstallCommand.ProvisionRoot
            : BrokerInstallCommand.Cleanup;
        string? sid = null;
        bool removeEvidence = false;
        for (int index = 1; index < args.Count; index++)
        {
            string name = args[index];
            if (command == BrokerInstallCommand.Cleanup && name == "--remove-evidence")
            {
                if (removeEvidence)
                {
                    error = "'--remove-evidence' was given twice.";
                    return null;
                }

                removeEvidence = true;
                continue;
            }

            if (name != "--owner-sid")
            {
                error = $"Unknown argument '{name}'.";
                return null;
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = "'--owner-sid' needs a value.";
                return null;
            }

            if (sid is not null)
            {
                error = "'--owner-sid' was given twice.";
                return null;
            }

            sid = args[++index];
        }

        if (sid is null)
        {
            error = "--owner-sid is required.";
            return null;
        }

        if (!sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) || sid.Length > 184 || sid.Any(char.IsControl))
        {
            error = "--owner-sid is not a SID.";
            return null;
        }

        return new(command, sid.ToUpperInvariant(), removeEvidence);
    }
}

/// <summary>
/// The broker's install-time commands (ADR-115, broker-v1 §5.10). An installer pre-creates the root before any user can
/// take its name; a removal or an update first stops what a previous broker left recording - refusing while a broker
/// may be recording - and removes the root only when it holds nothing kept. Each writes one line saying what became of
/// the root to stdout and its reasons to stderr.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrokerInstallation
{
    /// <summary>How long cleanup waits for a finishing broker to release the ownership log before refusing.</summary>
    public static readonly TimeSpan CleanupWait = BrokerProcess.PredecessorWait;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        BrokerInstallOptions? options = BrokerInstallOptions.Parse(args, out string? error);
        if (options is null)
        {
            await stderr.WriteLineAsync(error).ConfigureAwait(false);
            await stderr.WriteLineAsync(BrokerInstallOptions.Usage).ConfigureAwait(false);
            return (int)InterCatExitCode.InvalidInvocation;
        }

        return options.Command == BrokerInstallCommand.ProvisionRoot
            ? ProvisionRoot(BrokerRootLocation.Production(options.OwnerSid), stdout, stderr)
            : await CleanupAsync(
                    BrokerProcessDependencies.Production(options.OwnerSid),
                    options.RemoveEvidence,
                    stdout,
                    stderr,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Provisions the root as a serving broker does - created with its final security, an existing one adopted only from
    /// a trusted owner and its drifted security re-applied, an untrusted owner refused and named - and releases it.
    /// </summary>
    public static int ProvisionRoot(BrokerRootRequest request, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        try
        {
            using WindowsBrokerRoot root = WindowsBrokerRoot.Provision(request);
            string outcome = root.Report.CreatedByThisBroker ? "created" : root.Report.SecurityReapplied ? "repaired" : "kept";
            stdout.WriteLine($"root {root.Path} {outcome}");
            return (int)InterCatExitCode.Success;
        }
        catch (Exception exception) when (IsRootRefusal(exception))
        {
            stderr.WriteLine($"The broker's protected data folder could not be used: {exception.Message}");
            return (int)InterCatExitCode.PermissionOrCapabilityFailure;
        }
    }

    /// <summary>
    /// Refuses while another broker holds the ownership log, since it may be recording; otherwise runs the recovery a
    /// starting broker runs, then removes the root if it holds nothing kept - or everything, when asked - and keeps it,
    /// saying so, when it holds a capture or a session the recovery could not prove stopped.
    /// </summary>
    public static async Task<int> CleanupAsync(
        BrokerProcessDependencies dependencies,
        bool removeEvidence,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        TimeSpan? predecessorWait = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        string expected = Path.Combine(dependencies.Root.TrustedParentDirectory, dependencies.Root.RootDirectoryName);
        if (!Directory.Exists(expected))
        {
            await stdout.WriteLineAsync($"root {expected} absent").ConfigureAwait(false);
            return (int)InterCatExitCode.Success;
        }

        WindowsBrokerRoot root;
        try
        {
            root = WindowsBrokerRoot.Provision(dependencies.Root);
        }
        catch (Exception exception) when (IsRootRefusal(exception))
        {
            await stderr.WriteLineAsync($"The broker's protected data folder could not be used: {exception.Message}")
                .ConfigureAwait(false);
            return (int)InterCatExitCode.PermissionOrCapabilityFailure;
        }

        string path = root.Path;
        BrokerRecoveryReport recovery;
        using (root)
        {
            FileBrokerLifecycleStore? store;
            try
            {
                store = await BrokerProcess.OpenStoreAsync(root, predecessorWait ?? CleanupWait, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                return await KeepUnreadableAsync(path, exception, stdout, stderr).ConfigureAwait(false);
            }

            if (store is null)
            {
                await stderr.WriteLineAsync(
                        "Another InterCat capture broker still holds this machine's ownership log and may be recording; "
                        + "stop its capture in InterCat, then remove or update InterCat again.")
                    .ConfigureAwait(false);
                return BrokerProcess.ExitAnotherBrokerRunning;
            }

            using (store)
            {
                await using var runtime = new BrokerEvidenceCaptureRuntime(
                    root,
                    dependencies.EtwHost,
                    dependencies.EtwReclaimer,
                    processes: dependencies.ProcessReader);
                using var lifecycle = new BrokerLifecycleCoordinator(new PreparedPlanRegistry(), store, runtime);
                try
                {
                    recovery = await lifecycle.RecoverAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException exception)
                {
                    return await KeepUnreadableAsync(path, exception, stdout, stderr).ConfigureAwait(false);
                }
            }
        }

        foreach (BrokerRecoveryItem item in recovery.Items)
        {
            await stderr.WriteLineAsync($"capture {item.CaptureId.Value:N} {item.Action} -> {item.State}: {item.Summary}")
                .ConfigureAwait(false);
        }

        int running = recovery.Items.Count(item => !item.Milestones.ProvidersStopped);
        if (running > 0)
        {
            // Removing the folder would take the ownership record a later broker needs to stop that session.
            await stderr.WriteLineAsync(
                    $"{CountText.Of(running, "capture")} recorded by a previous broker could not be proven stopped; "
                    + "nothing was removed. Restart Windows, then remove or update InterCat again.")
                .ConfigureAwait(false);
            await stdout.WriteLineAsync($"root {path} kept: {CountText.Of(running, "capture")} not proven stopped")
                .ConfigureAwait(false);
            return (int)InterCatExitCode.PartialResultSuccess;
        }

        return await RemoveAsync(path, removeEvidence, stdout, stderr).ConfigureAwait(false);
    }

    private static async Task<int> KeepUnreadableAsync(string path, InvalidDataException exception, TextWriter stdout, TextWriter stderr)
    {
        // What the log would say is unknown, so nothing is stopped and nothing is removed; the removal goes on, since no
        // later attempt could read the log either.
        await stderr.WriteLineAsync(
                $"The broker's capture ownership log is unreadable and was left untouched, with its folder: {exception.Message}")
            .ConfigureAwait(false);
        await stdout.WriteLineAsync($"root {path} kept: its ownership log is unreadable").ConfigureAwait(false);
        return (int)InterCatExitCode.Success;
    }

    private static async Task<int> RemoveAsync(string path, bool removeEvidence, TextWriter stdout, TextWriter stderr)
    {
        // Besides its ownership log, a broker writes only capture directories into its root; anything else is kept too.
        string[] held = [.. Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).OfType<string>()
            .Where(name => name is not (FileBrokerLifecycleStore.FileName or FileBrokerLifecycleStore.CompactionFileName))];
        int captures = held.Count(name => name.StartsWith("capture-", StringComparison.Ordinal)
            && Directory.Exists(Path.Combine(path, name)));
        if (!removeEvidence && held.Length > 0)
        {
            await stdout.WriteLineAsync(captures == held.Length
                    ? $"root {path} kept with {CountText.Of(captures, "capture")}"
                    : $"root {path} kept with {CountText.Of(captures, "capture")} and "
                        + $"{CountText.Of(held.Length - captures, "entry", "entries")} a broker does not write")
                .ConfigureAwait(false);
            return (int)InterCatExitCode.Success;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync(
                    $"The broker's folder {path} could not be removed: {exception.Message} "
                    + "Close anything reading a capture in it, then retry.")
                .ConfigureAwait(false);
            return (int)InterCatExitCode.PermissionOrCapabilityFailure;
        }

        await stdout.WriteLineAsync(removeEvidence && captures > 0
                ? $"root {path} removed with {CountText.Of(captures, "capture")}"
                : $"root {path} removed")
            .ConfigureAwait(false);
        return (int)InterCatExitCode.Success;
    }

    private static bool IsRootRefusal(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or Win32Exception or InvalidOperationException
            or InvalidDataException;
}
