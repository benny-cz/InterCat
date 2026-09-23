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
        Port = Integer(args, "--port") ?? 0,
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
    await Console.Error.WriteLineAsync("Named-pipe message mode is a Windows feature; this scenario runs on Windows only.")
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
    Console.WriteLine("                             [--delay milliseconds] [--concurrency n]");
    Console.WriteLine("      FX-TCP-001: a seeded two-process loopback exchange. Each process writes its own");
    Console.WriteLine("      independent truth log; neither reads InterCat state. --delay 0 removes the");
    Console.WriteLine("      default 15 ms pacing so the exchange runs as fast as the sockets allow, and");
    Console.WriteLine("      --concurrency runs that many connections at once so the machine rather than");
    Console.WriteLine("      the fixture decides the rate.");
    Console.WriteLine();
    Console.WriteLine("  udp-loopback --truth <dir> [--seed n] [--sockets n] [--messages n] [--bytes n] [--delay ms]");
    Console.WriteLine("      FX-UDP-001: a seeded two-process UDP loopback exchange. The client sends datagrams from");
    Console.WriteLine("      several bound sockets and the server acknowledges each to the endpoint it came from, so");
    Console.WriteLine("      both directions carry data and every truth record names both ports.");
    Console.WriteLine();
    Console.WriteLine("  pipe-loopback --truth <dir> [--seed n] [--messages n] [--bytes n]");
    Console.WriteLine("      FX-PIPE-001: a seeded two-process named-pipe exchange in message mode, including one");
    Console.WriteLine("      deliberately short read so requested and completed sizes differ.");
    Console.WriteLine();
    Console.WriteLine("  rpc-local --truth <dir> [--calls n] [--service <name>]");
    Console.WriteLine("      FX-RPC-001: a known number of local RPC calls to the Windows service control");
    Console.WriteLine("      manager through the ordinary service API, with the client logging every call.");
    Console.WriteLine();
    Console.WriteLine("  No scenario runs implicitly. Nothing is captured or observed by this executable.");
}
