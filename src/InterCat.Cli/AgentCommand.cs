using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Application;
using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;
using InterCat.Remote;

namespace InterCat.Cli;

/// <summary>
/// `icat agent`: InterCat's remote agent on this host, run by its administrator from an elevated prompt and stopped when
/// done (ADR-117, `remote-agent-v1`). It issues enrollment tokens, serves enrolled controllers while it runs, lists and
/// revokes them, and shows what was asked of it. Nothing is installed and nothing runs once it stops.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class AgentCommand
{
    private const string DefaultGrants = "metadata-capture,download";

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? verb = command.TakeVerb();
        if (verb is null)
        {
            ConsoleUi.Failure("icat agent takes token, serve, controllers, revoke or audit.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        return verb switch
        {
            "token" => Token(command),
            "serve" => await ServeAsync(command, cancellationToken).ConfigureAwait(false),
            "controllers" => Controllers(command),
            "revoke" => Revoke(command),
            "audit" => Audit(command),
            _ => Unknown(verb),
        };
    }

    private static InterCatExitCode Token(CommandLine command)
    {
        string? root = command.TakeOption("--root");
        string grantText = command.TakeOption("--grant") ?? DefaultGrants;
        int? minutes = command.TakeIntegerOption("--lifetime");
        bool json = command.TryTakeFlag("--json");
        if (Refused(command) is { } refused)
        {
            return refused;
        }

        if (!RemoteGrantsExtensions.TryParse(grantText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), out RemoteGrants grants)
            || grants == RemoteGrants.None)
        {
            ConsoleUi.Failure($"--grant takes one or more of metadata-capture, contents, download and collectors, separated by commas, not '{grantText}'.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (minutes is < 1 or > 60)
        {
            ConsoleUi.Failure("--lifetime takes a token's lifetime in minutes, from 1 to 60: a token is for one enrollment soon.");
            return InterCatExitCode.InvalidInvocation;
        }

        return WithAgent(root, agent =>
        {
            using RemoteIdentity identity = agent.Identity();
            EnrollmentToken token = agent.Enrollments.Issue(identity.Pin, grants, TimeSpan.FromMinutes(minutes ?? 10));
            DateTimeOffset lapses = DateTimeOffset.UtcNow.AddMinutes(minutes ?? 10);
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    Contract = "remote-agent-token-v1",
                    Token = token.ToString(),
                    Agent = identity.Pin.Hex,
                    Grants = grants.Names(),
                    ExpiresUtc = lapses.UtcDateTime,
                }, JsonContracts.Indented));
                return InterCatExitCode.Success;
            }

            ConsoleUi.Heading("Enrollment token");
            ConsoleUi.Field("Agent", identity.Pin.Hex);
            ConsoleUi.Field("Grants", string.Join(", ", grants.Names()));
            ConsoleUi.Field("Lapses", lapses.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
                + $", {minutes ?? 10} minutes from now, or once one controller enrolls with it");
            ConsoleUi.Field("Token", token.ToString());
            ConsoleUi.Note("Give the token whole to the person enrolling a controller, by a channel you trust: it enrolls one "
                + "controller with these grants. What this agent's policy forbids - contents unless it runs with "
                + "--allow-contents, collectors always - is refused whatever a token grants.");
            return InterCatExitCode.Success;
        });
    }

    private static async Task<InterCatExitCode> ServeAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? root = command.TakeOption("--root");
        string? listen = command.TakeOption("--listen");
        bool allowContents = command.TryTakeFlag("--allow-contents");
        string? brokerOption = command.TakeOption("--broker");
        int? maxMinutes = command.TakeIntegerOption("--max-minutes");
        int? maxJournal = command.TakeIntegerOption("--max-journal-mib");
        if (Refused(command) is { } refused)
        {
            return refused;
        }

        if (maxMinutes is < 1 or > 1_440 || maxJournal is < 1 or > 1_048_576)
        {
            ConsoleUi.Failure("--max-minutes takes the longest capture this agent records for a controller, 1 to 1,440 minutes, and "
                + "--max-journal-mib its largest journal, 1 to 1,048,576 MiB.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (listen is null || !IPEndPoint.TryParse(listen, out IPEndPoint? endpoint) || endpoint.Port == 0)
        {
            ConsoleUi.Failure("--listen takes the address and port the agent listens on, such as 192.0.2.10:7457 or [::]:7457: an "
                + "address, not a name - the agent looks up no name - and the port you opened for it in this host's firewall.");
            return InterCatExitCode.InvalidInvocation;
        }

        AgentState? agent = Open(root, out InterCatExitCode failed);
        if (agent is null)
        {
            return failed;
        }

        RemoteIdentity identity;
        try
        {
            identity = agent.Identity();
        }
        catch (CryptographicException exception)
        {
            ConsoleUi.Failure($"The agent's key in {agent.Root} is not this machine's, or was altered: {exception.Message}");
            return InterCatExitCode.CorruptedInput;
        }

        using (identity)
        {
            var policy = new RemoteAgentPolicy(RemoteAgentPolicy.Default.Allowed | (allowContents ? RemoteGrants.Contents : RemoteGrants.None));
            var host = new RemoteAgentHost(LocalHostIdentity.Current.Value, Environment.MachineName,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription, SupportBundle.ProductVersion);
            // Captures go through this host's capture broker, launched for the first and kept until the agent stops; where the
            // broker is not installed beside icat, the agent serves none.
            BrokerLaunchTarget? target = brokerOption is not null
                ? new(Path.GetFullPath(brokerOption), ["serve"])
                : BrokerLaunchTarget.BesideCurrentProcess();
            var limits = RemoteCapturePolicy.Default with
            {
                MaximumSeconds = (uint)((maxMinutes ?? 60) * 60),
                MaximumJournalMebibytes = (uint)(maxJournal ?? 4_096),
            };
            RemoteCaptures? captures = target is null ? null : new RemoteCaptures(new BrokerCaptureDriver(async launching =>
            {
                try
                {
                    return new LaunchedBroker(await WindowsBrokerLauncher.LaunchAsync(target, cancellationToken: launching).ConfigureAwait(false));
                }
                catch (BrokerLaunchException exception)
                {
                    throw new RemoteCaptureRefusedException("The host's capture broker did not start: " + exception.Message, exception);
                }
            }), limits, audit: agent.Audit, records: agent.Captures, evidence: new StoreEvidenceSource());
            var server = new RemoteAgentServer(identity, agent.Enrollments, host, policy, agent.Audit, captures: captures);
            try
            {
                await using (server.ConfigureAwait(false))
                {
                    IPEndPoint listening;
                    try
                    {
                        listening = server.Start(endpoint);
                    }
                    catch (System.Net.Sockets.SocketException exception)
                    {
                        ConsoleUi.Failure($"The agent cannot listen on {endpoint}: {exception.Message}");
                        return InterCatExitCode.PermissionOrCapabilityFailure;
                    }

                    ConsoleUi.Heading("Remote agent");
                    ConsoleUi.Field("Key", identity.Pin.Hex);
                    ConsoleUi.Field("Host", $"{host.MachineName} ({host.HostId:D})");
                    ConsoleUi.Field("Listening", listening.ToString());
                    ConsoleUi.Field("Allows", string.Join(", ", policy.Effective(RemoteGrantsExtensions.Defined).Names()));
                    ConsoleUi.Field("Captures", captures is null
                        ? "none: InterCat's capture broker is not beside icat; --broker names it in a development build"
                        : $"one at a time, up to {limits.MaximumSeconds / 60:N0} minutes and {limits.MaximumJournalMebibytes:N0} MiB, stopped when its controller's lease lapses; "
                            + "each fetched by the controller that started it, once it closes");
                    ConsoleUi.Field("Folder", agent.Root);
                    ConsoleUi.Note("Enrolled controllers may ask what this agent allows until you stop it with Ctrl+C; icat agent "
                        + "audit lists what they asked.");
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Ctrl+C is how an administrator stops the agent.
                    }
                }
            }
            finally
            {
                // The server first, so no request reaches a capture being stopped; then the capture, stopped with the agent.
                if (captures is not null)
                {
                    await captures.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        ConsoleUi.Success("The agent stopped; nothing of it runs on.");
        return InterCatExitCode.Success;
    }

    private static InterCatExitCode Controllers(CommandLine command)
    {
        string? root = command.TakeOption("--root");
        bool json = command.TryTakeFlag("--json");
        if (Refused(command) is { } refused)
        {
            return refused;
        }

        return WithAgent(root, agent =>
        {
            IReadOnlyList<EnrolledController> enrolled = agent.Enrollments.Controllers;
            IReadOnlyList<RemotePin> revoked = agent.Enrollments.Revoked;
            int pending = agent.Enrollments.PendingTokens;
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    Contract = "remote-agent-controllers-v1",
                    Controllers = enrolled.Select(controller => new
                    {
                        Pin = controller.Pin.Hex,
                        Grants = controller.Grants.Names(),
                        EnrolledUtc = controller.EnrolledUtc.UtcDateTime,
                    }),
                    Revoked = revoked.Select(pin => pin.Hex),
                    PendingTokens = pending,
                }, JsonContracts.Indented));
                return InterCatExitCode.Success;
            }

            ConsoleUi.Heading("Enrolled controllers");
            if (enrolled.Count == 0)
            {
                ConsoleUi.Note("None: icat agent token issues a token one controller enrolls with.");
            }
            else
            {
                ConsoleUi.Table(["Key", "Grants", "Enrolled"], [.. enrolled.Select(controller => (IReadOnlyList<string>)
                [
                    controller.Pin.Hex,
                    string.Join(", ", controller.Grants.Names()),
                    controller.EnrolledUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                ])]);
            }

            ConsoleUi.Field("Revoked", revoked.Count == 0 ? "none" : string.Join(", ", revoked.Select(pin => pin.Abbreviated)));
            ConsoleUi.Field("Tokens unused", CountText.Of(pending, "token"));
            return InterCatExitCode.Success;
        });
    }

    private static InterCatExitCode Revoke(CommandLine command)
    {
        string? root = command.TakeOption("--root");
        string? key = command.TakePositional();
        if (Refused(command) is { } refused)
        {
            return refused;
        }

        if (key is null || key.Length < 16 || !key.All(char.IsAsciiHexDigit))
        {
            ConsoleUi.Failure("Name the controller's key to revoke: its pin, or its first 16 digits or more, as icat agent controllers lists it.");
            return InterCatExitCode.InvalidInvocation;
        }

        return WithAgent(root, agent =>
        {
            RemotePin[] matching = [.. agent.Enrollments.Controllers.Select(controller => controller.Pin)
                .Where(pin => pin.Hex.StartsWith(key, StringComparison.OrdinalIgnoreCase))];
            if (matching.Length != 1)
            {
                ConsoleUi.Failure(matching.Length == 0
                    ? $"No enrolled controller's key begins {key}; icat agent controllers lists them."
                    : $"{matching.Length} enrolled controllers' keys begin {key}: give more of the pin.");
                return InterCatExitCode.InvalidInvocation;
            }

            _ = agent.Enrollments.Revoke(matching[0]);
            ConsoleUi.Success($"Revoked {matching[0].Hex}: its next request is refused, and no token enrolls this key again.");
            return InterCatExitCode.Success;
        });
    }

    private static InterCatExitCode Audit(CommandLine command)
    {
        string? root = command.TakeOption("--root");
        int? last = command.TakeIntegerOption("--last");
        bool json = command.TryTakeFlag("--json");
        if (Refused(command) is { } refused)
        {
            return refused;
        }

        if (last is < 1)
        {
            ConsoleUi.Failure("--last takes how many of the newest requests to list, at least 1.");
            return InterCatExitCode.InvalidInvocation;
        }

        return WithAgent(root, agent =>
        {
            IReadOnlyList<RemoteAuditEntry> entries = FileRemoteAudit.Read(agent.Root, last ?? 50);
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    Contract = "remote-agent-audit-v1",
                    Entries = entries.Select(entry => new
                    {
                        Utc = entry.Utc.UtcDateTime,
                        Controller = entry.Controller.IsEmpty ? null : entry.Controller.Hex,
                        Action = FileRemoteAudit.Words(entry.Action),
                        entry.Outcome,
                    }),
                }, JsonContracts.Indented));
                return InterCatExitCode.Success;
            }

            ConsoleUi.Heading("What controllers asked");
            if (entries.Count == 0)
            {
                ConsoleUi.Note("Nothing yet.");
                return InterCatExitCode.Success;
            }

            ConsoleUi.Table(["Time", "Controller", "Asked", "Outcome"], [.. entries.Select(entry => (IReadOnlyList<string>)
            [
                entry.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                entry.Controller.Abbreviated,
                FileRemoteAudit.Words(entry.Action),
                entry.Outcome,
            ])]);
            return InterCatExitCode.Success;
        });
    }

    private static InterCatExitCode WithAgent(string? root, Func<AgentState, InterCatExitCode> act)
    {
        AgentState? agent = Open(root, out InterCatExitCode failed);
        if (agent is null)
        {
            return failed;
        }

        try
        {
            return act(agent);
        }
        catch (InvalidDataException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.CorruptedInput;
        }
        catch (CryptographicException exception)
        {
            ConsoleUi.Failure($"The agent's key in {agent.Root} is not this machine's, or was altered: {exception.Message}");
            return InterCatExitCode.CorruptedInput;
        }
    }

    private static AgentState? Open(string? root, out InterCatExitCode failed)
    {
        failed = InterCatExitCode.PermissionOrCapabilityFailure;
        string folder = root ?? AgentState.DefaultRoot();
        try
        {
            return AgentState.Open(folder);
        }
        catch (UnauthorizedAccessException exception)
        {
            ConsoleUi.Failure(exception.Message);
        }
        catch (DirectoryNotFoundException)
        {
            ConsoleUi.Failure(root is null
                ? $"InterCat's root on this host, {Path.GetDirectoryName(folder)}, does not exist: install InterCat here, or name the agent's folder with --root."
                : $"The folder that would hold {Path.GetFullPath(folder)} does not exist.");
        }

        return null;
    }

    private static InterCatExitCode? Refused(CommandLine command)
    {
        if (!command.TryReportUnknown(out string? unknown))
        {
            return null;
        }

        ConsoleUi.Failure(command.Refusal(unknown!));
        ConsoleUi.Explain(PrintHelp);
        return InterCatExitCode.InvalidInvocation;
    }

    private static InterCatExitCode Unknown(string verb)
    {
        ConsoleUi.Failure($"icat agent has no '{verb}': it takes token, serve, controllers, revoke or audit.");
        ConsoleUi.Explain(PrintHelp);
        return InterCatExitCode.InvalidInvocation;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat agent token [--grant <grants>] [--lifetime <minutes>] [--root <folder>] [--json]");
        ConsoleUi.Line("icat agent serve --listen <address:port> [--allow-contents] [--max-minutes <minutes>] [--max-journal-mib <mib>]");
        ConsoleUi.Line("                 [--broker <path>] [--root <folder>]");
        ConsoleUi.Line("icat agent controllers [--root <folder>] [--json]");
        ConsoleUi.Line("icat agent revoke <key> [--root <folder>]");
        ConsoleUi.Line("icat agent audit [--last <count>] [--root <folder>] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  InterCat's remote agent on this host, run by its administrator from an elevated prompt. token issues a");
        ConsoleUi.Line("  one-time token a controller enrolls with, granting metadata-capture and download unless --grant says");
        ConsoleUi.Line("  otherwise, lapsing after 10 minutes unless --lifetime says otherwise. serve listens on the address and");
        ConsoleUi.Line("  port given until Ctrl+C, answering enrolled controllers within what the agent allows - contents only");
        ConsoleUi.Line("  with --allow-contents, collectors never - and records one metadata capture at a time for a controller");
        ConsoleUi.Line("  holding metadata-capture, up to 60 minutes and 4,096 MiB unless --max-minutes and --max-journal-mib say");
        ConsoleUi.Line("  otherwise, through this host's capture broker (--broker names it in a development build), stopping it");
        ConsoleUi.Line("  when its controller's lease lapses or the agent stops. controllers lists the keys enrolled and revoked, revoke");
        ConsoleUi.Line("  revokes one, and audit lists what controllers asked, by key, never by address. The agent keeps its key,");
        ConsoleUi.Line("  enrollments and audit in a folder only administrators may use: remote-agent in InterCat's root under the");
        ConsoleUi.Line("  machine's program data, unless --root names another.");
    }
}
