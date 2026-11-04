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

    // --churn runs FX-UNIX-002: sixty-four short connections one at a time, two small messages each, unpaced.
    bool churn = args.Contains("--churn", StringComparer.Ordinal);
    var unixOptions = new UnixLoopbackOptions
    {
        TruthDirectory = truth,
        Churn = churn,
        Seed = Integer(args, "--seed") ?? 20_261_104,
        Connections = Integer(args, "--connections") ?? (churn ? 64 : 2),
        MessagesPerConnection = Integer(args, "--messages") ?? (churn ? 2 : 8),
        MaximumMessageBytes = Integer(args, "--bytes") ?? (churn ? 256 : 4_096),
        InterMessageDelayMilliseconds = Integer(args, "--delay") ?? (churn ? 0 : 15),
        Concurrency = Integer(args, "--concurrency") ?? 1,
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
    Console.WriteLine("                [--concurrency n] [--socket <path>] [--wait-for-start] [--read-buffer n] [--churn]");
    Console.WriteLine("      FX-UNIX-001: FX-TCP-001's two-process exchange over an AF_UNIX stream socket bound to a path -");
    Console.WriteLine("      by default fx.sock in the truth directory - each process logging every message by the path,");
    Console.WriteLine("      as a Unix-domain socket has no port. --wait-for-start has each process wait for a line on");
    Console.WriteLine("      standard input before it opens a socket, so a capture scoped to it is in place first.");
    Console.WriteLine("      --read-buffer has the server read each message in reads that ask for that many bytes, taking what");
    Console.WriteLine("      has arrived, so a read asks for more than it receives. --churn runs it as FX-UNIX-002: by default");
    Console.WriteLine("      64 short connections one at a time, two messages of up to 256 bytes each, unpaced, whose sockets");
    Console.WriteLine("      the kernel creates at addresses earlier ones held.");
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
