using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>The icat command line: one invocation's arguments to the command they name.</summary>
internal static class Icat
{
    /// <summary>
    /// Runs one icat invocation and answers its exit code (§20.4). What it answers goes to <see cref="Console.Out"/> and what
    /// it says about itself to <see cref="Console.Error"/>, whichever writers those are, so a test can read both.
    /// </summary>
    public static async Task<InterCatExitCode> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            return await DispatchAsync(args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A report's last fields are written when its command ends, as every other group is when something follows it.
            ConsoleUi.Flush();
        }
    }

    private static async Task<InterCatExitCode> DispatchAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        var command = new CommandLine(args[1..]);
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "capabilities" => await CapabilitiesCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "profiles" => await ProfilesCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "measure" => await MeasureCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "import" => await ImportCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "record" => await RecordCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "capture" when OperatingSystem.IsWindows() => await CaptureCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "capture" => CaptureNeedsWindows(),
                "rederive" => await RederiveCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "session" => await SessionCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "overview" => await OverviewCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "channels" => await ChannelsCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "hosts" => await HostsCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "programs" => await ProgramsCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "ports" => await PortsCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "patterns" => await PatternsCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "links" => await LinksCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "waits" => await WaitsCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "outliers" => await OutliersCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "evidence" => await EvidenceCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "timeline" => await TimelineCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "compare" => await CompareCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "export" => await ExportCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "package" => await PackageCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "raw" => await RawCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "content" => await ContentCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "recover" => await RecoverCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "staging" => await StagingCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "retain" => await RetainCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "pin" => await PinCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "compact" => await CompactCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "checkpoint" => await CheckpointCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "follow" => await FollowCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "metric" => await MetricCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "processes" => await ProcessesCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "operations" => await OperationsCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "exchanges" => await ExchangesCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "workspace" => await WorkspaceCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "verify" => await VerifyCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "support" => await SupportCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "demo" => await DemoCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                "bench" => await BenchCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            ConsoleUi.Warn("Cancelled before a result was published.");
            return InterCatExitCode.Cancelled;
        }
        catch (NoSessionException)
        {
            return NoSession();
        }
        catch (UnauthorizedAccessException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }
        catch (IOException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.CorruptedInput;
        }
        catch (InvalidDataException exception)
        {
            // Every format reader refuses what it cannot read with this exception and a reason. It is not an
            // IOException, so without this a damaged session would end the process with a stack trace instead of
            // the documented exit code and the reader's own explanation (§20.4, §20.6 Storage).
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.CorruptedInput;
        }
    }

    /// <summary>
    /// Every command refuses a folder that holds no session in the same words and with the same code: naming the wrong
    /// folder, or a capture's that has published nothing yet, is a mistake in what was asked, as naming no folder is.
    /// </summary>
    internal static InterCatExitCode NoSession()
    {
        ConsoleUi.Failure(SessionStore.NoGeneration);
        return InterCatExitCode.InvalidInvocation;
    }

    /// <summary>
    /// icat capture starts the capture broker, which records through ETW and runs on Windows alone: elsewhere the command is
    /// known, and says why it cannot run and what can, rather than calling itself unknown.
    /// </summary>
    private static InterCatExitCode CaptureNeedsWindows()
    {
        ConsoleUi.Failure("icat capture needs Windows: it starts the capture broker, which records through ETW. Here, a "
            + "session captured on Windows can be opened, measured, exported and packaged.");
        return InterCatExitCode.PermissionOrCapabilityFailure;
    }

    private static InterCatExitCode UnknownCommand(string name)
    {
        ConsoleUi.Failure($"Unknown command: {name}");
        ConsoleUi.Explain(PrintHelp);
        return InterCatExitCode.InvalidInvocation;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("InterCat command line");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat capabilities [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Read-only source inventory. Starts no capture.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat profiles [profile] [--diagnostic-etl] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("  icat profiles focused-transport --mechanism tcp [--pid <id> ...]");
        ConsoleUi.Line("  icat profiles content --help");
        ConsoleUi.Line("      Lists capture intents or previews requested/effective settings and omissions.");
        ConsoleUi.Line("      Reads schemas only; never enables a provider or starts a capture.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat measure <tcp|udp|pipe|rpc> [--output <dir>] [--overwrite] [--json]");
        ConsoleUi.Line("      Runs the named fixture under one owned ETW session and computes its tier.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat measure tcp [--output <dir>] [--seed <n>] [--connections <n>] [--messages <n>]");
        ConsoleUi.Line("                   [--bytes <n>] [--workload <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Runs the seeded TCP loopback truth workload under an owned ETW session and");
        ConsoleUi.Line("      reports measured coverage against the independent truth log. Needs elevation.");
        ConsoleUi.Line("  icat measure udp [--output <dir>] [--seed <n>] [--sockets <n>] [--messages <n>]");
        ConsoleUi.Line("                   [--bytes <n>] [--workload <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      The same for the seeded UDP workload, with --sockets where TCP has --connections.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat capture <new-session-dir> [--duration <seconds>]");
        ConsoleUi.Line("               [--profile " + string.Join("|", CaptureRequests.Profiles) + "] [--mechanism tcp|http]");
        ConsoleUi.Line("               [--pid <id,...>] [--keep-last <seconds>] [--broker <exe>] [--json]");
        ConsoleUi.Line("      Captures live without running icat elevated: the capture broker is started on demand");
        ConsoleUi.Line("      (Windows asks for approval) and this process derives the session. Ctrl+C stops early.");
        ConsoleUi.Line("      RPC peers also records the kernel's ALPC messages, which link each RPC call to the process");
        ConsoleUi.Line("      that served it; a content capture keeps your own processes' messages (icat capture --help).");
        ConsoleUi.Line("      Windows only, as the broker is.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat record <new-session-dir> [--profile explore|focused-transport|rpc-peers|content|content-fixture]");
        ConsoleUi.Line("              [--mechanism tcp|udp|http] [--duration <seconds>] [--publish-every <seconds>]");
        ConsoleUi.Line("              [--evidence-only] [--json]");
        ConsoleUi.Line("      Captures live under an owned ETW session straight into a new session, which session,");
        ConsoleUi.Line("      processes and metric read like an imported one. Needs elevation; Ctrl+C stops early.");
        ConsoleUi.Line("      A content capture names its source, processes and limits (icat record --help).");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat import <source.etl> [--into <session-dir>] [--rows-per-segment <n>]");
        ConsoleUi.Line("             [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("             [--max-entries-in-memory <n>] [--spill-directory <dir>]");
        ConsoleUi.Line("      Reads a standalone ETL through the canonical import contract and reports its");
        ConsoleUi.Line("      source identity, import identity, derived clock, counts and multiplicity.");
        ConsoleUi.Line("      With --into it publishes a session: the admitted journal-v1 evidence and the");
        ConsoleUi.Line("      observation-v1 segments and capture coverage ledger as one generation.");
        ConsoleUi.Line("      --into requires a new empty directory; --overwrite replaces only a report file.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat rederive <directory> [--check] [--rows-per-segment <n>] [--output <path>] [--json]");
        ConsoleUi.Line("      Without --check, rebuilds from the retained journal and saved plan, publishing");
        ConsoleUi.Line("      a replacement generation while keeping the previous one as last-known-good.");
        ConsoleUi.Line("      --check fully replays without changing the session generation.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat session <directory> [--rows <n>] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Opens a published session, verifies every dependency the current generation");
        ConsoleUi.Line("      names, and reports its evidence boundary, its segments, their column");
        ConsoleUi.Line("      availability, byte metrics and capture coverage. Read-only; repairs nothing.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat overview <directory> [--json]");
        ConsoleUi.Line("      The Desktop's exact leased process graph, paired TCP and UDP channels and timeline bundle.");
        ConsoleUi.Line("      Graph eligibility is narrower than the all-observations timeline; caveats are included.");
        ConsoleUi.Line("      Its text names the busiest processes, relationships, pairs of programs and other hosts.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat hosts <directory> [--address <address>] [--program <name>] [--limit <n>] [--json]");
        ConsoleUi.Line("      The other hosts the session's processes reached, the busiest first, each naming the process");
        ConsoleUi.Line("      that reached it most; with --address every process that reached one: the address whole, or");
        ConsoleUi.Line("      begun up to a dot or colon; with --program one program's processes alone.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat programs <directory> [--program <name>] [--limit <n>] [--json]");
        ConsoleUi.Line("      The programs whose processes talked to each other, each pair named by which program's");
        ConsoleUi.Line("      processes connected to which, the busiest first; with --program the pairs one program is in.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat ports <directory> [--limit <n>] [--investigation <file>] [--json]");
        ConsoleUi.Line("      The ports its processes accepted TCP connections at, each with the process that accepted");
        ConsoleUi.Line("      them and who connected - its processes, other hosts or unobserved ones - the busiest first.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat patterns <directory> [--limit <n> | --pattern <rank>] [--all] [--evidence-policy <policy>]");
        ConsoleUi.Line("                [--json]");
        ConsoleUi.Line("      The recurring communication patterns: connections, UDP flows and RPC calls grouped by");
        ConsoleUi.Line("      mechanism, service port or RPC interface and procedure, and where the server is, with");
        ConsoleUi.Line("      who took part as client and server; --pattern says how one was grouped and who.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat links <directory> --process <pid | instance> [--to <pid | instance>]");
        ConsoleUi.Line("             [--evidence-policy <policy>] [--json]");
        ConsoleUi.Line("      A process's recorded links - its parent and children, whose calls it served or who served");
        ConsoleUi.Line("      its calls, and what it connected to or what connected to it - and, with --to, the shortest");
        ConsoleUi.Line("      chain of them to another process; processes created near it in time are never a link.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat waits <directory> [--process <pid | instance>] [--limit <n>] [--evidence-policy <policy>]");
        ConsoleUi.Line("             [--json]");
        ConsoleUi.Line("      The time processes spent inside the RPC calls they made, by interface and by the process");
        ConsoleUi.Line("      that served them, with that server's own time; no wait is said to have delayed anything.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat outliers <directory> --interval <start:end> [--baseline <start:end>]");
        ConsoleUi.Line("                [--group-by process|executable] [--evidence-policy <policy>] [--limit <n>] [--json]");
        ConsoleUi.Line("      The rows whose records over an interval are above or below every window of its baseline,");
        ConsoleUi.Line("      each window as long as the interval, with how often chance alone puts a row there.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat channels <directory> [--process <instance-guid> [--one-sided]] [--page-size <1-200>]");
        ConsoleUi.Line("                [--cursor <token>] [--json]");
        ConsoleUi.Line("      Bounded pages of paired TCP and UDP channels, including sessions above the overview cap,");
        ConsoleUi.Line("      the busiest first, each by the processes at its ends and which connected to which;");
        ConsoleUi.Line("      --one-sided lists a process's connections whose other end no record holds.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat evidence <directory> [--channel <paired-channel-key>] [--owner-process <instance-guid>]");
        ConsoleUi.Line("                [--interval <start:end>] [--page-size <1-200>] [--cursor <token>] [--json]");
        ConsoleUi.Line("      Leased pages of exact admitted observation rows; cursors restart on generation change.");
        ConsoleUi.Line("      --interval uses 100-nanosecond session-relative presentation ticks [start,end).");
        ConsoleUi.Line("      These are normalized source facts, not completion-paired operations or raw payloads.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat raw <directory> --session-id <guid> --generation <n> --segment <name> --row <n>");
        ConsoleUi.Line("           [--reveal-bytes] [--json]");
        ConsoleUi.Line("      One original retained journal record, from an icat evidence page's exact row locator.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat content <directory> --session-id <guid> --generation <n> --segment <name> --row <n>");
        ConsoleUi.Line("               [--part] [--reveal] [--from <byte>] [--to <byte>] [--save <file> [--overwrite]] [--json]");
        ConsoleUi.Line("      One record's kept content from the same locator: its facts, and its bytes only when asked,");
        ConsoleUi.Line("      shown bounded and inert or saved as they are (ADR-036). --part reads the HTTP head or body");
        ConsoleUi.Line("      a buffer belongs to instead: whole when every buffer of it was kept whole, else with each");
        ConsoleUi.Line("      gap in place, chosen by buffer and never saved as one.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat timeline <directory> --interval <start:end> [--columns <1-2000>] [--bytes] [--json]");
        ConsoleUi.Line("              [--mechanism <name> | --process <instance-id> [--direction <name>]");
        ConsoleUi.Line("               | --channel <key> [--end <0|1>]]");
        ConsoleUi.Line("      The timeline over any interval, its columns cut as the overview's are, as the Desktop draws a");
        ConsoleUi.Line("      zoomed viewport; a scope lists what one lane of its interval table lists, and --bytes adds");
        ConsoleUi.Line("      what each interval's records sent and received. An empty interval's coverage is the capture's.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat compare <directory> --baseline <start:end> --interval <start:end>");
        ConsoleUi.Line("               [--rank-by records|bytes-sent] [--group-by process|executable]");
        ConsoleUi.Line("               [--baseline-session <directory>] [--limit <n>] [--json | --output <path>]");
        ConsoleUi.Line("  icat compare --workspace <workspace> --saved <name> [--limit <n>] [--json | --output <path>]");
        ConsoleUi.Line("      How each relationship changed from one interval of a session to another, or to another");
        ConsoleUi.Line("      capture's by executable, as rates over each whole interval, the most changed first; where");
        ConsoleUi.Line("      the captures did not measure both alike, both values with why they cannot be compared,");
        ConsoleUi.Line("      never a change. --saved runs a comparison an investigation saved.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat export <directory> --output <path> [--at <row-key>]... [--interval <start:end>]");
        ConsoleUi.Line("              [--rank-by <ranking>] [--evidence [--limit <n>]] [--format json|csv]");
        ConsoleUi.Line("              [--share-redacted] [--overwrite]");
        ConsoleUi.Line("      Detailed export of one ladder rung; --evidence writes source-record metadata.");
        ConsoleUi.Line("      --rank-by ranks the machine and group rungs by records, bytes-sent, bytes-received,");
        ConsoleUi.Line("      bytes-sent-and-received, rpc-calls-made, rpc-calls-served, rpc-errors, rpc-call-time-median,");
        ConsoleUi.Line("      rpc-serve-time-median, active-peers, connections-made or connections-accepted, as the");
        ConsoleUi.Line("      Desktop's Rank by selector does.");
        ConsoleUi.Line("      --share-redacted writes a separate pseudonymized report, not a reopenable session.");
        ConsoleUi.Line("      Both formats are staged, never left partly written.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat package <directory> --redacted --output <new-directory> [--check] [--json]");
        ConsoleUi.Line("      A reopenable redacted session for sharing: fresh identities, pseudonymous names, IDs,");
        ConsoleUi.Line("      addresses and ports, synthetic records, and no original journal, payload or locator.");
        ConsoleUi.Line("      It is verified before it is published. --check measures and writes nothing.");
        ConsoleUi.Line("  icat package <directory> --original --output <new-directory> [--check] [--json]");
        ConsoleUi.Line("      Copies the session byte for byte - every journal and kept content, unredacted - into a new");
        ConsoleUi.Line("      directory, where it reopens as the same session; it says what it carries before it copies.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat recover <directory> [--confirm --expect-manifest <digest>] [--json]");
        ConsoleUi.Line("      Reviews a session whose newest generation does not verify; --confirm with the review's digest");
        ConsoleUi.Line("      re-points it to the generation readers read in its place. No file is deleted.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat staging <directory> [--confirm --expect-set <digest>] [--json]");
        ConsoleUi.Line("      Reviews the files staged in a session; --confirm with the review's digest removes only those");
        ConsoleUi.Line("      it listed whose writer let go. A writer's files and unmarked staging are always kept.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat retain <directory> --release-before <moment>");
        ConsoleUi.Line("             [--confirm --reason <text>] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("  icat retain <directory> --release-journal-before-record <n>");
        ConsoleUi.Line("             [--confirm --reason <text>] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("  icat retain <directory> --release-content [--confirm --reason <text>] [--json]");
        ConsoleUi.Line("      Measures what releasing the records read before a moment with their rows, a prefix of the");
        ConsoleUi.Line("      admitted journal, or every message's kept content would give up, and performs it only");
        ConsoleUi.Line("      with --confirm and a stated reason (ADR-010, ADR-043).");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat pin <directory> [--from <moment> --reason <text> [--allow-mib <n>]] [--remove <pin>]");
        ConsoleUi.Line("           [--json]");
        ConsoleUi.Line("      Lists, places or removes the pins that keep a session's records from a moment through");
        ConsoleUi.Line("      every retention, within the size they allow it (ADR-046).");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat follow <evidence-dir> <session-dir> [--poll <seconds>] [--once] [--keep-last <seconds>]");
        ConsoleUi.Line("              [--json]");
        ConsoleUi.Line("  icat follow <session-dir> [--json]");
        ConsoleUi.Line("      Derives a session, in this ordinary process, from what icat record --evidence-only");
        ConsoleUi.Line("      publishes: chunks copied byte for byte and checked, rows derived here (ADR-027). Given");
        ConsoleUi.Line("      a session alone, it finishes a capture that ended early from the ticket beside it.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat compact <directory> [--check] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Coalesces small publications into bounded segments, keeping every row (§20.1).");
        ConsoleUi.Line("      A live recording does this itself as it records and when it stops.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat checkpoint <directory> [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Publishes the session's derivation checkpoint, so opening it reads no record to");
        ConsoleUi.Line("      build its processes and relationships. Writers publish one when they finish.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat metric <directory> --metric <name> [--basis <name>] [--byte-domain <name>]");
        ConsoleUi.Line("             [--side <name>] [--rate-numerator <name>] [--layer <name>]");
        ConsoleUi.Line("             [--mechanism <name>] [--interval <start>:<end>] [--evidence <n>]");
        ConsoleUi.Line("             [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("  icat metric --matrix [--json]");
        ConsoleUi.Line("      Answers one metric over a published session against section 5.3's matrix. A");
        ConsoleUi.Line("      metric outside its basis is rejected with the compatible ones named; one this");
        ConsoleUi.Line("      session cannot derive is reported as unavailable, with what it needs.");
        ConsoleUi.Line("      Capture coverage is shown separately; rates remain observed, not corrected.");
        ConsoleUi.Line("      --group-by process|executable|session|mechanism|peer ranks it, with an exact remainder past --top.");
        ConsoleUi.Line("      --owner|--participant|--sender|--receiver <instance> focuses it on one process, and");
        ConsoleUi.Line("      --peer <instance> on one process at the other end, and --between <instances>");
        ConsoleUi.Line("      --and <instances> on the records connecting two sets (icat metric --help lists them).");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat processes <directory> [--top <n>] [--pid <id>] [--port <n>] [--rank-by <ranking>] [--json]");
        ConsoleUi.Line("      Lists the process instances the session's evidence supports, with each one's");
        ConsoleUi.Line("      lifetime, records, connections made and accepted, and transport bytes, the most bytes");
        ConsoleUi.Line("      first unless --rank-by names records, bytes-sent, bytes-received, connections-made or");
        ConsoleUi.Line("      connections-accepted; --pid shows one in full: the processes and other hosts it");
        ConsoleUi.Line("      exchanged data with, and who connected to whom; --port those that accepted connections");
        ConsoleUi.Line("      at that port of their own. A reused PID is two instances.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat operations <directory> [--pid <id>] [--interface <uuid>] [--calls <n>] [--json]");
        ConsoleUi.Line("      Lists the RPC calls: each start paired with its stop by activity id, grouped by process,");
        ConsoleUi.Line("      side and interface, with durations and every unpaired call's reason.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat exchanges <directory> [--pid <id>] [--exchanges <n>] [--json]");
        ConsoleUi.Line("      Lists the HTTP exchanges a content capture recorded through WinINet: each client process's");
        ConsoleUi.Line("      exchanges, what was recorded of each part, and how long each took, never their content.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat workspace <new|add|show|relink|alias|align|compare|correlate|timeline|join|same-host");
        ConsoleUi.Line("                 |translate|note|view|comparison|package> <workspace> ... [--json]");
        ConsoleUi.Line("      An investigation over separately captured sessions: one file naming each by identity, one");
        ConsoleUi.Line("      session per capture, never writing to one. show says where each session stands;");
        ConsoleUi.Line("      align, compare and correlate place them in one time, which timeline draws; join,");
        ConsoleUi.Line("      same-host and translate keep a person's decisions; note and view annotate it; comparison");
        ConsoleUi.Line("      saves a comparison of its sessions to run again; package copies it with its sessions.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat demo <directory> [--json]");
        ConsoleUi.Line("      Writes InterCat's generated demo: two hosts' sessions and the investigation that holds");
        ConsoleUi.Line("      them, aligned. Nothing in it was captured, and every view of it says so.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat verify <tcp|udp> --run <raw-run-dir> --output <curated-dir> [--overwrite] [--json]");
        ConsoleUi.Line("      Re-evaluates a run offline and writes only fixture-scoped shareable evidence.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat bench [--output <path>] [--series <series.json>] [--overwrite] [--json]");
        ConsoleUi.Line("             [--no-storage-probe]");
        ConsoleUi.Line("      Publishes the performance baseline: this machine against the section 12 reference,");
        ConsoleUi.Line("      every section 12 budget with what measured it, and the per-build validation");
        ConsoleUi.Line("      backlog. Starts no capture and needs no elevation.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat support [<session> ...] (--output <path> [--overwrite] | --json | --check)");
        ConsoleUi.Line("      A support bundle: versions, this machine's capability report, and each session's files,");
        ConsoleUi.Line("      rows, coverage and loss counters. It holds no content, endpoint, command line, raw record,");
        ConsoleUi.Line("      name or path, and lists what it holds before it is written.");
        ConsoleUi.Line();
        ConsoleUi.Line("  Exit codes: 0 success, 1 partial result, 2 invalid invocation,");
        ConsoleUi.Line("              3 permission or capability failure, 4 corrupted input, 5 cancelled.");
        ConsoleUi.Line();
        ConsoleUi.Line("  Options may come before or after a command's directory. An operand that starts with a dash,");
        ConsoleUi.Line("  such as a note, follows --; a negative number needs no --.");
        ConsoleUi.Line();
        ConsoleUi.Line("  Machine-readable data goes to stdout; progress, status and what explains a refusal go to stderr.");
        ConsoleUi.Line();
        ConsoleUi.Line("  icat <command> --help lists every option of a command, with what each does.");
    }
}
