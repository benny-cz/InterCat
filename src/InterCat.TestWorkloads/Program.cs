using System.Globalization;
using InterCat.TestWorkloads;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await RunAsync(args, cancellation.Token).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        PrintHelp();
        return args.Length == 0 ? 2 : 0;
    }

    string? truth = Option(args, "--truth");
    if (truth is null)
    {
        await Console.Error.WriteLineAsync("--truth <directory> is required: a workload always records its own oracle.")
            .ConfigureAwait(false);
        return 2;
    }

    var rpcOptions = new RpcLocalOptions
    {
        TruthDirectory = truth,
        Calls = Integer(args, "--calls") ?? 12,
        ServiceName = Option(args, "--service") ?? "Schedule",
    };

    var rpcTcpOptions = new RpcTcpOptions
    {
        TruthDirectory = truth,
        Calls = Integer(args, "--calls") ?? 12,
        InterCallDelayMilliseconds = Integer(args, "--delay") ?? 40,
        StartAfterSeconds = Integer(args, "--start-after") ?? 0,
        Concurrency = Integer(args, "--concurrency") ?? 1,
    };

    var contentOptions = new ContentFixtureOptions
    {
        TruthDirectory = truth,
        Seed = Integer(args, "--seed") ?? 20_260_928,
        Messages = Integer(args, "--messages") ?? 24,
        MaximumMessageBytes = Integer(args, "--bytes") ?? 6_000,
        InterMessageDelayMilliseconds = Integer(args, "--delay") ?? 20,
    };

    var httpOptions = new HttpWinInetOptions
    {
        TruthDirectory = truth,
        Seed = Integer(args, "--seed") ?? 20_260_929,
        Requests = Integer(args, "--requests") ?? 16,
        MaximumBodyBytes = Integer(args, "--bytes") ?? 96 * 1024,
        WaitForStart = args.Contains("--wait-for-start", StringComparer.Ordinal),
        StartAfterSeconds = Integer(args, "--start-after") ?? 0,
        Tls = args.Contains("--tls", StringComparer.Ordinal) || args.Contains("--http2", StringComparer.Ordinal),
        Concurrency = Integer(args, "--concurrency") ?? 1,
        Chunked = args.Contains("--chunked", StringComparer.Ordinal),
        Coding = Option(args, "--coding") ?? (args.Contains("--gzip", StringComparer.Ordinal) ? "gzip" : null),
        Decode = args.Contains("--decode", StringComparer.Ordinal),
        Async = args.Contains("--async", StringComparer.Ordinal),
        PaceMilliseconds = Integer(args, "--pace") ?? 0,
        Http2 = args.Contains("--http2", StringComparer.Ordinal),
        WriteBytes = Integer(args, "--write") ?? 0,
        RedirectStatus = Integer(args, "--redirect") ?? 0,
        Proxy = args.Contains("--proxy", StringComparer.Ordinal),
        Auth = Option(args, "--auth"),
    };

    var pipeOptions = new PipeLoopbackOptions
    {
        TruthDirectory = truth,
        Seed = Integer(args, "--seed") ?? 20_260_921,
        Messages = Integer(args, "--messages") ?? 8,
        MaximumMessageBytes = Integer(args, "--bytes") ?? 4_096,
        PipeName = Option(args, "--pipe") ?? string.Empty,
    };

    var mailslotOptions = new MailslotLoopbackOptions
    {
        TruthDirectory = truth,
        Seed = Integer(args, "--seed") ?? 20_261_105,
        Messages = Integer(args, "--messages") ?? 32,
        MaximumBytes = Integer(args, "--bytes") ?? 2_048,
        DelayMilliseconds = Integer(args, "--delay") ?? 0,
        Name = Option(args, "--name"),
    };

    var windowMessageOptions = new WindowMessageOptions
    {
        TruthDirectory = truth,
        Seed = Integer(args, "--seed") ?? 20_261_105,
        CopyData = Integer(args, "--copies") ?? 32,
        Posted = Integer(args, "--posts") ?? 32,
        MaximumBytes = Integer(args, "--bytes") ?? 2_048,
        ClassName = Option(args, "--class"),
        Generations = Integer(args, "--generations") ?? 1,
    };

    var udpOptions = new UdpLoopbackOptions
    {
        TruthDirectory = truth,
        Seed = Integer(args, "--seed") ?? 20_260_923,
        Sockets = Integer(args, "--sockets") ?? 2,
        DatagramsPerSocket = Integer(args, "--messages") ?? 8,
        MaximumDatagramBytes = Integer(args, "--bytes") ?? 1_400,
        InterDatagramDelayMilliseconds = Integer(args, "--delay") ?? 15,
        Ipv6 = args.Contains("--ipv6", StringComparer.Ordinal),
        Port = Integer(args, "--port") ?? 0,
    };

    // --churn runs FX-UNIX-002: sixty-four short connections one at a time, two small messages each, unpaced. --hold runs
    // FX-UNIX-003: eight connections opened at once, each waiting that long before its first message, then unpaced.
    bool churn = args.Contains("--churn", StringComparer.Ordinal);
    int hold = Integer(args, "--hold") ?? 0;
    int unixConnections = Integer(args, "--connections") ?? (churn ? 64 : hold > 0 ? 8 : 2);
    var unixOptions = new UnixLoopbackOptions
    {
        TruthDirectory = truth,
        Churn = churn,
        HoldMilliseconds = hold,
        Seed = Integer(args, "--seed") ?? 20_261_104,
        Connections = unixConnections,
        MessagesPerConnection = Integer(args, "--messages") ?? (churn ? 2 : hold > 0 ? 16 : 8),
        MaximumMessageBytes = Integer(args, "--bytes") ?? (churn ? 256 : 4_096),
        InterMessageDelayMilliseconds = Integer(args, "--delay") ?? (churn || hold > 0 ? 0 : 15),
        Concurrency = Integer(args, "--concurrency") ?? (hold > 0 ? unixConnections : 1),
        SocketPath = Option(args, "--socket"),
        ReadBufferBytes = Integer(args, "--read-buffer") ?? 0,
        WaitForStart = args.Contains("--wait-for-start", StringComparer.Ordinal),
    };

    var options = new TcpLoopbackOptions
    {
        TruthDirectory = truth,
        Seed = Integer(args, "--seed") ?? 20_260_920,
        Connections = Integer(args, "--connections") ?? 2,
        MessagesPerConnection = Integer(args, "--messages") ?? 8,
        MaximumMessageBytes = Integer(args, "--bytes") ?? 4_096,
        InterMessageDelayMilliseconds = Integer(args, "--delay") ?? 15,
        Concurrency = Integer(args, "--concurrency") ?? 1,
        Ipv6 = args.Contains("--ipv6", StringComparer.Ordinal),
        Port = Integer(args, "--port") ?? 0,
    };

    var comOptions = new ComLoopbackOptions
    {
        TruthDirectory = truth,
        Calls = Integer(args, "--calls") ?? 64,
        InterCallDelayMilliseconds = Integer(args, "--delay") ?? 5,
        Burst = Integer(args, "--burst") ?? 0,
        Moniker = Option(args, "--moniker"),
        WaitForStart = args.Contains("--wait-for-start", StringComparer.Ordinal),
        Servers = Integer(args, "--servers") ?? 1,
        ServerIndex = Integer(args, "--server-index"),
        MonikersOnInput = args.Contains("--monikers-on-input", StringComparer.Ordinal),
        Nested = args.Contains("--nested", StringComparer.Ordinal),
        Forward = Option(args, "--forward"),
        Forwarded = args.Contains("--forwarded", StringComparer.Ordinal),
        ServeMilliseconds = Integer(args, "--serve-ms") ?? 0,
    };

    var syncOptions = new SyncObjectsOptions
    {
        TruthDirectory = truth,
        Rounds = Integer(args, "--rounds") ?? 64,
        HoldMilliseconds = Integer(args, "--hold-ms") ?? 5,
        Name = Option(args, "--name"),
        Churn = Integer(args, "--churn") ?? 0,
        Duplicate = args.Contains("--duplicate", StringComparer.Ordinal),
        Generations = Integer(args, "--generations") ?? 1,
        HeldMilliseconds = Integer(args, "--held-ms") ?? 0,
    };

    if (args[0].StartsWith("unix-loopback", StringComparison.Ordinal) && churn && hold > 0)
    {
        await Console.Error.WriteLineAsync("--churn and --hold are two fixtures, FX-UNIX-002 and FX-UNIX-003: a run is one of them.")
            .ConfigureAwait(false);
        return 2;
    }

    // --duplicate, --generations and --held-ms are three fixtures, FX-SYNC-002, FX-SYNC-003 and FX-SYNC-004: a run is one of them.
    if (args[0].StartsWith("sync-objects", StringComparison.Ordinal) && (syncOptions.Generations < 1 || syncOptions.HeldMilliseconds < 0
        || (syncOptions.Duplicate ? 1 : 0) + (syncOptions.Generations > 1 ? 1 : 0) + (syncOptions.HeldMilliseconds > 0 ? 1 : 0) > 1))
    {
        await Console.Error.WriteLineAsync("--generations is at least one and --held-ms not negative, and --duplicate, --generations and --held-ms are three fixtures, FX-SYNC-002, FX-SYNC-003 and FX-SYNC-004: a run is one of them.")
            .ConfigureAwait(false);
        return 2;
    }

    if (args[0].StartsWith("window-message", StringComparison.Ordinal) && windowMessageOptions.Generations < 1)
    {
        await Console.Error.WriteLineAsync("--generations is at least one: the windows the receiver makes one after another.").ConfigureAwait(false);
        return 2;
    }

    try
    {
        return args[0] switch
        {
            "tcp-loopback" => await TcpLoopbackScenario.RunCoordinatorAsync(options, cancellationToken).ConfigureAwait(false),
            "tcp-loopback-server" => await TcpLoopbackScenario.RunServerAsync(options, cancellationToken).ConfigureAwait(false),
            "tcp-loopback-client" => await TcpLoopbackScenario.RunClientAsync(options, cancellationToken).ConfigureAwait(false),
            "udp-loopback" => await UdpLoopbackScenario.RunCoordinatorAsync(udpOptions, cancellationToken).ConfigureAwait(false),
            "udp-loopback-server" => await UdpLoopbackScenario.RunServerAsync(udpOptions, cancellationToken).ConfigureAwait(false),
            "udp-loopback-client" => await UdpLoopbackScenario.RunClientAsync(udpOptions, cancellationToken).ConfigureAwait(false),
            "unix-loopback" => await UnixLoopbackScenario.RunCoordinatorAsync(unixOptions, cancellationToken).ConfigureAwait(false),
            "unix-loopback-server" => await UnixLoopbackScenario.RunServerAsync(unixOptions, cancellationToken).ConfigureAwait(false),
            "unix-loopback-client" => await UnixLoopbackScenario.RunClientAsync(unixOptions, cancellationToken).ConfigureAwait(false),
            "pipe-loopback" when OperatingSystem.IsWindows() =>
                await PipeLoopbackScenario.RunCoordinatorAsync(pipeOptions, cancellationToken).ConfigureAwait(false),
            "pipe-loopback-server" when OperatingSystem.IsWindows() =>
                await PipeLoopbackScenario.RunServerAsync(pipeOptions, cancellationToken).ConfigureAwait(false),
            "pipe-loopback-client" when OperatingSystem.IsWindows() =>
                await PipeLoopbackScenario.RunClientAsync(pipeOptions, cancellationToken).ConfigureAwait(false),
            "pipe-loopback" or "pipe-loopback-server" or "pipe-loopback-client" =>
                await UnsupportedPlatformAsync().ConfigureAwait(false),
            "mailslot-loopback" when OperatingSystem.IsWindows() =>
                await MailslotLoopbackScenario.RunCoordinatorAsync(mailslotOptions, cancellationToken).ConfigureAwait(false),
            "mailslot-loopback-server" when OperatingSystem.IsWindows() =>
                await MailslotLoopbackScenario.RunServerAsync(mailslotOptions, cancellationToken).ConfigureAwait(false),
            "mailslot-loopback-client" when OperatingSystem.IsWindows() =>
                await MailslotLoopbackScenario.RunClientAsync(mailslotOptions, cancellationToken).ConfigureAwait(false),
            "mailslot-loopback" or "mailslot-loopback-server" or "mailslot-loopback-client" =>
                await UnsupportedPlatformAsync().ConfigureAwait(false),
            "window-message" when OperatingSystem.IsWindows() =>
                await WindowMessageScenario.RunCoordinatorAsync(windowMessageOptions, cancellationToken).ConfigureAwait(false),
            "window-message-receiver" when OperatingSystem.IsWindows() =>
                await WindowMessageScenario.RunReceiverAsync(windowMessageOptions, cancellationToken).ConfigureAwait(false),
            "window-message-sender" when OperatingSystem.IsWindows() =>
                await WindowMessageScenario.RunSenderAsync(windowMessageOptions, cancellationToken).ConfigureAwait(false),
            "window-message" or "window-message-receiver" or "window-message-sender" =>
                await UnsupportedPlatformAsync().ConfigureAwait(false),
            "com-loopback" when OperatingSystem.IsWindows() =>
                await ComLoopbackScenario.RunCoordinatorAsync(comOptions, cancellationToken).ConfigureAwait(false),
            "com-loopback-server" when OperatingSystem.IsWindows() =>
                await ComLoopbackScenario.RunServerAsync(comOptions, cancellationToken).ConfigureAwait(false),
            "com-loopback-client" when OperatingSystem.IsWindows() =>
                await ComLoopbackScenario.RunClientAsync(comOptions, cancellationToken).ConfigureAwait(false),
            "com-inproc" when OperatingSystem.IsWindows() =>
                await ComLoopbackScenario.RunInProcessAsync(comOptions, cancellationToken).ConfigureAwait(false),
            "com-loopback" or "com-loopback-server" or "com-loopback-client" or "com-inproc" =>
                await UnsupportedPlatformAsync().ConfigureAwait(false),
            "sync-objects" when OperatingSystem.IsWindows() =>
                await SyncObjectsScenario.RunCoordinatorAsync(syncOptions, cancellationToken).ConfigureAwait(false),
            "sync-objects-creator" when OperatingSystem.IsWindows() =>
                await SyncObjectsScenario.RunCreatorAsync(syncOptions, cancellationToken).ConfigureAwait(false),
            "sync-objects-opener" when OperatingSystem.IsWindows() =>
                await SyncObjectsScenario.RunOpenerAsync(syncOptions, cancellationToken).ConfigureAwait(false),
            "sync-objects" or "sync-objects-creator" or "sync-objects-opener" => await UnsupportedPlatformAsync().ConfigureAwait(false),
            "rpc-local" when OperatingSystem.IsWindows() =>
                await RpcLocalScenario.RunCoordinatorAsync(rpcOptions, cancellationToken).ConfigureAwait(false),
            "rpc-local-client" when OperatingSystem.IsWindows() =>
                await RpcLocalScenario.RunClientAsync(rpcOptions, cancellationToken).ConfigureAwait(false),
            "rpc-local" or "rpc-local-client" => await UnsupportedPlatformAsync().ConfigureAwait(false),
            "rpc-tcp" when OperatingSystem.IsWindows() =>
                await RpcTcpScenario.RunCoordinatorAsync(rpcTcpOptions, cancellationToken).ConfigureAwait(false),
            "rpc-tcp-client" when OperatingSystem.IsWindows() =>
                await RpcTcpScenario.RunClientAsync(rpcTcpOptions, cancellationToken).ConfigureAwait(false),
            "rpc-tcp" or "rpc-tcp-client" => await UnsupportedPlatformAsync().ConfigureAwait(false),
            "content-fixture" => await ContentFixtureScenario.RunAsync(contentOptions, cancellationToken).ConfigureAwait(false),
            "http-wininet" when OperatingSystem.IsWindows() =>
                await HttpWinInetScenario.RunAsync(httpOptions, cancellationToken).ConfigureAwait(false),
            "http-wininet" => await UnsupportedPlatformAsync().ConfigureAwait(false),
            _ => await UnknownAsync(args[0]).ConfigureAwait(false),
        };
    }
    catch (OperationCanceledException)
    {
        await Console.Error.WriteLineAsync("The workload was cancelled; its truth log ends where it stopped.")
            .ConfigureAwait(false);
        return 5;
    }
}

static async Task<int> UnsupportedPlatformAsync()
{
    await Console.Error.WriteLineAsync("This scenario uses a Windows feature and runs on Windows only.")
        .ConfigureAwait(false);
    return 3;
}

static async Task<int> UnknownAsync(string verb)
{
    await Console.Error.WriteLineAsync($"Unknown scenario: {verb}").ConfigureAwait(false);
    PrintHelp();
    return 2;
}

static string? Option(string[] args, string name)
{
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.Ordinal))
        {
            return args[index + 1];
        }
    }

    return null;
}

static int? Integer(string[] args, string name)
{
    string? raw = Option(args, name);
    return raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value
        : null;
}

static void PrintHelp()
{
    Console.WriteLine("InterCat truth workloads (M0)");
    Console.WriteLine();
    Console.WriteLine("  tcp-loopback --truth <dir> [--seed n] [--connections n] [--messages n] [--bytes n]");
    Console.WriteLine("                             [--delay milliseconds] [--concurrency n] [--ipv6]");
    Console.WriteLine("      FX-TCP-001: a seeded two-process loopback exchange. Each process writes its own");
    Console.WriteLine("      independent truth log; neither reads InterCat state. --delay 0 removes the");
    Console.WriteLine("      default 15 ms pacing so the exchange runs as fast as the sockets allow, and");
    Console.WriteLine("      --concurrency runs that many connections at once so the machine rather than");
    Console.WriteLine("      the fixture decides the rate. --ipv6 runs it over IPv6 loopback (::1) as FX-TCP-002.");
    Console.WriteLine();
    Console.WriteLine("  udp-loopback --truth <dir> [--seed n] [--sockets n] [--messages n] [--bytes n] [--delay ms] [--ipv6]");
    Console.WriteLine("      FX-UDP-001: a seeded two-process UDP loopback exchange. The client sends datagrams from");
    Console.WriteLine("      several bound sockets and the server acknowledges each to the endpoint it came from, so");
    Console.WriteLine("      both directions carry data and every truth record names both ports. --ipv6 runs it over");
    Console.WriteLine("      IPv6 loopback (::1) as FX-UDP-002.");
    Console.WriteLine();
    Console.WriteLine("  unix-loopback --truth <dir> [--seed n] [--connections n] [--messages n] [--bytes n] [--delay ms]");
    Console.WriteLine("                [--concurrency n] [--socket <path>] [--wait-for-start] [--read-buffer n] [--churn | --hold ms]");
    Console.WriteLine("      FX-UNIX-001: FX-TCP-001's two-process exchange over an AF_UNIX stream socket bound to a path -");
    Console.WriteLine("      by default fx.sock in the truth directory - each process logging every message by the path,");
    Console.WriteLine("      as a Unix-domain socket has no port. --wait-for-start has each process wait for a line on");
    Console.WriteLine("      standard input before it opens a socket, so a capture scoped to it is in place first.");
    Console.WriteLine("      --read-buffer has the server read each message in reads that ask for that many bytes, taking what");
    Console.WriteLine("      has arrived, so a read asks for more than it receives. --churn runs it as FX-UNIX-002: by default");
    Console.WriteLine("      64 short connections one at a time, two messages of up to 256 bytes each, unpaced, whose sockets");
    Console.WriteLine("      the kernel creates at addresses earlier ones held. --hold runs it as FX-UNIX-003: by default eight");
    Console.WriteLine("      connections opened at once, each waiting that many milliseconds before its first of 16 messages,");
    Console.WriteLine("      unpaced, so a capture started meanwhile finds them open.");
    Console.WriteLine();
    Console.WriteLine("  com-loopback --truth <dir> [--calls n] [--delay ms] [--burst n] [--servers n | --nested | --serve-ms ms]");
    Console.WriteLine("      FX-COM-001: a two-process out-of-process COM exchange. The server puts an automation object in");
    Console.WriteLine("      the running object table under an item moniker of its own, and the client takes it from there and");
    Console.WriteLine("      calls it through IDispatch, each process logging every call it made or served. Nothing is");
    Console.WriteLine("      registered with the machine. --burst has the client make its calls in bursts of that many,");
    Console.WriteLine("      unpaced, the delay between bursts. com-loopback-server and com-loopback-client, started apart,");
    Console.WriteLine("      take --wait-for-start: each waits for a line on standard input before it initializes COM.");
    Console.WriteLine("      --servers n is FX-COM-003: n servers one after another, each a process of its own under a");
    Console.WriteLine("      moniker of its own and logging to a truth log of its place, which one client follows, making");
    Console.WriteLine("      --calls calls to each in turn. --nested is FX-COM-004: the server, while serving each call,");
    Console.WriteLine("      calls a second server's object - a nested call, numbered 1,000,000 past the client's - logging");
    Console.WriteLine("      it as a client does. --serve-ms is FX-COM-005: the server takes that long to answer each call,");
    Console.WriteLine("      so a capture started or stopped while calls run cuts some. Windows only.");
    Console.WriteLine();
    Console.WriteLine("  com-inproc --truth <dir> [--calls n] [--delay ms]");
    Console.WriteLine("      FX-COM-002: one process calls the system's apartment-threaded in-memory dictionary, which COM");
    Console.WriteLine("      hosts in an apartment of its own and reaches through a proxy: calls that cross an apartment and");
    Console.WriteLine("      never a process, each one logged. Windows only.");
    Console.WriteLine();
    Console.WriteLine("  sync-objects --truth <dir> [--rounds n] [--hold-ms ms] [--churn n] [--duplicate | --generations n | --held-ms ms]");
    Console.WriteLine("      FX-SYNC-001: two processes take turns through three named objects of the session's namespace -");
    Console.WriteLine("      an event, a semaphore and a mutex - that one creates and the other opens by name. Each round the");
    Console.WriteLine("      creator, holding the mutex, sets the event and releases the mutex that many milliseconds later;");
    Console.WriteLine("      the opener waits on both, then releases the mutex and the semaphore, on which the creator waits.");
    Console.WriteLine("      Each process logs every object it created or opened, every signal it gave, every wait that ended");
    Console.WriteLine("      - whether it blocked first - and every handle it closed, by the object's name. --churn has the opener");
    Console.WriteLine("      open and close each object by its name that many times before the rounds, logging each: a workload");
    Console.WriteLine("      heavy in handles. --duplicate runs it as FX-SYNC-002, the opener also given each object's handle by a");
    Console.WriteLine("      duplication. --generations runs it as FX-SYNC-003: the three objects made that many times one after");
    Console.WriteLine("      another, each generation under names of its own and that many rounds through it, both processes");
    Console.WriteLine("      closing it before the next is made, so the kernel makes later objects where earlier ones were.");
    Console.WriteLine("      --held-ms runs it as FX-SYNC-004: both processes hold their handles that many milliseconds before");
    Console.WriteLine("      the first round, so a capture begun meanwhile sees none of them made, only their closes.");
    Console.WriteLine("      Windows only.");
    Console.WriteLine();
    Console.WriteLine("  mailslot-loopback --truth <dir> [--seed n] [--messages n] [--bytes n] [--delay ms]");
    Console.WriteLine("      FX-MAIL-001: a server makes a mailslot under a name of the run's own and a client opens it by");
    Console.WriteLine("      its name and writes seeded messages of up to that many bytes, which the server reads one at a");
    Console.WriteLine("      time, each read asking for more than any message holds. Each process logs the mailslot it made");
    Console.WriteLine("      or opened, every message with the bytes asked for and moved, and its close. Windows only.");
    Console.WriteLine();
    Console.WriteLine("  window-message --truth <dir> [--seed n] [--copies n] [--posts n] [--bytes n] [--generations n]");
    Console.WriteLine("      FX-WMSG-001: a receiver makes a message-only window of a class of the run's own and pumps its");
    Console.WriteLine("      messages; a sender finds it by its class, sends it that many WM_COPYDATA messages of seeded");
    Console.WriteLine("      payloads of up to that many bytes, posts it that many numbered messages, and last posts it a");
    Console.WriteLine("      close. Each process logs every message it sent or posted and every one its window received.");
    Console.WriteLine("      --generations runs it as FX-WMSG-002: the receiver makes that many windows one after another,");
    Console.WriteLine("      each under a name of its own once the last is destroyed, and the sender finds each by its class");
    Console.WriteLine("      and name and sends and posts it the messages, numbered on from the last window's, then its close.");
    Console.WriteLine("      Windows only.");
    Console.WriteLine();
    Console.WriteLine("  pipe-loopback --truth <dir> [--seed n] [--messages n] [--bytes n]");
    Console.WriteLine("      FX-PIPE-001: a seeded two-process named-pipe exchange in message mode, including one");
    Console.WriteLine("      deliberately short read so requested and completed sizes differ.");
    Console.WriteLine();
    Console.WriteLine("  rpc-local --truth <dir> [--calls n] [--service <name>]");
    Console.WriteLine("      FX-RPC-001: a known number of local RPC calls to the Windows service control");
    Console.WriteLine("      manager through the ordinary service API, with the client logging every call.");
    Console.WriteLine();
    Console.WriteLine("  rpc-tcp --truth <dir> [--calls n] [--delay ms] [--start-after s] [--concurrency n]");
    Console.WriteLine("      FX-RPC-002: a known number of RPC calls over TCP - the client asks the endpoint mapper on");
    Console.WriteLine("      127.0.0.1, port 135, where the service control manager listens over TCP, through the ordinary");
    Console.WriteLine("      RPC runtime API and with no credentials - logging every call and, from the system's TCP table,");
    Console.WriteLine("      its connections to port 135 while each call's binding is held. --start-after waits that many");
    Console.WriteLine("      seconds before the first call, so a capture is in place first; --concurrency deals the calls to");
    Console.WriteLine("      that many client threads at once.");
    Console.WriteLine();
    Console.WriteLine("  content-fixture --truth <dir> [--seed n] [--messages n] [--bytes n] [--delay ms]");
    Console.WriteLine("      FX-CONTENT-001: raises InterCat's own content fixture provider with seeded messages, some");
    Console.WriteLine("      longer than the content-fixture profile keeps, once a capture enables it (ADR-036). The");
    Console.WriteLine("      truth log names each message's length and SHA-256, never its bytes.");
    Console.WriteLine();
    Console.WriteLine("  http-wininet --truth <dir> [--seed n] [--requests n] [--bytes n] [--wait-for-start | --start-after s] [--tls]");
    Console.WriteLine("               [--concurrency n] [--chunked] [--gzip | --coding gzip|deflate|br [--decode]] [--async] [--pace ms]");
    Console.WriteLine("               [--http2] [--write n] [--redirect 301|302|303|307|308] [--proxy] [--auth basic|basic-late]");
    Console.WriteLine("      FX-HTTP-001: seeded HTTP/1.1 requests through WinINet to a server this process runs on");
    Console.WriteLine("      loopback, bodies from empty to past 64 KiB both ways. The server logs every head and body");
    Console.WriteLine("      it received and sent by length and SHA-256, never the bytes. --wait-for-start waits for a");
    Console.WriteLine("      line on standard input first, and --start-after waits that many seconds, so a capture scoped");
    Console.WriteLine("      to this process is in place before its first request. --tls runs it over TLS as FX-HTTP-002,");
    Console.WriteLine("      with a certificate made in memory for the run and installed nowhere; the server logs the");
    Console.WriteLine("      bytes above the encryption. --concurrency runs n clients at once, each on its own connection,");
    Console.WriteLine("      and --chunked has the server frame its responses in chunks, logging the body and the framed");
    Console.WriteLine("      bytes apart; either makes the run FX-HTTP-003. --gzip has the server code every response body in");
    Console.WriteLine("      gzip, declared by Content-Encoding, and the client accept it; the server logs the body before its");
    Console.WriteLine("      coding and the coded bytes apart. --decode has WinINet decode each response before the client reads");
    Console.WriteLine("      it; without it the client reads the coded bytes. --gzip makes the run FX-HTTP-004; --coding deflate");
    Console.WriteLine("      or --coding br codes the bodies in deflate (zlib) or Brotli instead, and makes it FX-HTTP-008. --async has");
    Console.WriteLine("      the client work WinINet asynchronously, each send and read it cannot finish at once completing");
    Console.WriteLine("      on WinINet's own threads, told by its status callback; it makes the run FX-HTTP-005, with any");
    Console.WriteLine("      of the others. --pace has the server wait that many milliseconds before each 16 KiB of a");
    Console.WriteLine("      response body. --http2 has the client enable HTTP/2 and Kestrel serve it over TLS, offered by");
    Console.WriteLine("      ALPN beside HTTP/1.1; the server logs each body as it read and wrote it and each request's");
    Console.WriteLine("      field names, never the frames; it makes the run FX-HTTP-006, and refuses --chunked. --write has");
    Console.WriteLine("      the client send each request's head with HttpSendRequestEx and write its body with InternetWriteFile");
    Console.WriteLine("      in seeded pieces of 1 to n bytes, logging each piece, then end it with HttpEndRequest; it makes");
    Console.WriteLine("      the run FX-HTTP-007, with any of the others. --redirect has the server answer each request first");
    Console.WriteLine("      with that redirect, of no body, to its path moved, which WinINet follows by itself; the server logs");
    Console.WriteLine("      both legs apart; it makes the run FX-HTTP-009, and refuses --http2. --proxy has the client reach");
    Console.WriteLine("      the server through a proxy, which the server is too: WinINet asks it for fx.invalid, over HTTP in");
    Console.WriteLine("      absolute form, over TLS through a tunnel CONNECT opens, inside which the server decrypts; the");
    Console.WriteLine("      server logs each CONNECT and its answer; it makes the run FX-HTTP-010, and refuses --http2. --auth");
    Console.WriteLine("      has the server challenge each request without the run's test credentials with a 401 naming Basic,");
    Console.WriteLine("      logging both legs apart; with basic the client gives WinINet the credentials when it connects, and");
    Console.WriteLine("      with basic-late only once challenged, sending the request again; it makes the run FX-HTTP-011, and");
    Console.WriteLine("      refuses --http2, --redirect and --write. With --proxy it is the proxy that challenges, with a 407,");
    Console.WriteLine("      each request sent it and each CONNECT without its credentials, and the client gives them as the");
    Console.WriteLine("      proxy's; it makes the run FX-HTTP-012.");
    Console.WriteLine();
    Console.WriteLine("  No scenario runs implicitly. Nothing is captured or observed by this executable.");
}
