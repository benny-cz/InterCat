using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one seeded AF_UNIX run. The same seed and counts reproduce the same declared sizes (I14).</summary>
internal sealed record UnixLoopbackOptions
{
    public const string ScenarioId = "FX-UNIX-001";

    /// <summary>The longest path a Windows <c>sockaddr_un</c> holds, its terminator included.</summary>
    public const int MaximumPathLength = 108;

    public required string TruthDirectory { get; init; }
    public int Seed { get; init; } = 20_261_104;
    public int Connections { get; init; } = 2;
    public int MessagesPerConnection { get; init; } = 8;
    public int MaximumMessageBytes { get; init; } = 4_096;
    public int MinimumMessageBytes { get; init; } = 64;
    public int InterMessageDelayMilliseconds { get; init; } = 15;
    public int Concurrency { get; init; } = 1;

    /// <summary>The path the server's socket is bound to; by default a file of the truth directory.</summary>
    public string? SocketPath { get; init; }

    /// <summary>Whether each process waits for a line on standard input before it opens a socket, so a capture scoped to it is in place first.</summary>
    public bool WaitForStart { get; init; }

    public string ResolvedSocketPath => SocketPath ?? Path.Combine(TruthDirectory, "fx.sock");
}

/// <summary>
/// A two-process AF_UNIX stream workload with an independent truth log per process (plan M6, the Unix-domain sockets work
/// package): the exchange FX-TCP-001 makes over TCP loopback, over a socket bound to a path instead. It uses ordinary
/// sockets only: nothing here observes ETW or InterCat. Each log names the socket by its path, never by a port, which a
/// Unix-domain socket has none of.
/// </summary>
internal static class UnixLoopbackScenario
{
    private const int AckBytes = 8;
    private const int LengthPrefixBytes = 4;

    private static readonly JsonSerializerOptions SummaryOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Runs the coordinator: it starts the server and the client as separate processes.</summary>
    public static async Task<int> RunCoordinatorAsync(UnixLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");
        string path = options.ResolvedSocketPath;
        if (path.Length >= UnixLoopbackOptions.MaximumPathLength)
        {
            await Console.Error.WriteLineAsync($"The socket path is {path.Length} characters; a Unix-domain socket's holds fewer than {UnixLoopbackOptions.MaximumPathLength}.")
                .ConfigureAwait(false);
            return 2;
        }

        using Process server = StartChild(executable, "unix-loopback-server", options, redirectStdout: true);
        string? handshake = await server.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (handshake is null)
        {
            await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The server process exited before it reported its socket.").ConfigureAwait(false);
            return 1;
        }

        using Process client = StartChild(executable, "unix-loopback-client", options, redirectStdout: false);
        await client.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var summary = new
        {
            scenarioId = UnixLoopbackOptions.ScenarioId,
            options.Seed,
            options.Connections,
            options.MessagesPerConnection,
            options.MinimumMessageBytes,
            options.MaximumMessageBytes,
            options.InterMessageDelayMilliseconds,
            options.Concurrency,
            socketPath = path,
            serverProcessId = server.Id,
            clientProcessId = client.Id,
            serverExitCode = server.ExitCode,
            clientExitCode = client.ExitCode,
        };
        await File.WriteAllTextAsync(
            Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions),
            cancellationToken).ConfigureAwait(false);

        return server.ExitCode != 0 || client.ExitCode != 0 ? 1 : 0;
    }

    public static async Task<int> RunServerAsync(UnixLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        await using var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-server.jsonl"),
            UnixLoopbackOptions.ScenarioId,
            "server");
        truth.Write(TruthEventKind.ProcessStarted);
        await WaitForStartAsync(options, cancellationToken).ConfigureAwait(false);

        string path = options.ResolvedSocketPath;
        File.Delete(path);
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(options.Connections + 1);
            truth.Write(TruthEventKind.ListenerBound, resourceName: path);

            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { socket = path })).ConfigureAwait(false);
            await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Accept every connection first, then serve them together, as the TCP fixture does (IC-010).
            int concurrency = Math.Clamp(options.Concurrency, 1, Math.Max(1, options.Connections));
            var pending = new List<Task>(concurrency);
            for (int connection = 0; connection < options.Connections; connection++)
            {
                Socket accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                int index = connection;
                pending.Add(Task.Run(() => ServeAsync(accepted, index, options, truth, path, cancellationToken), cancellationToken));
                if (pending.Count >= concurrency)
                {
                    Task finished = await Task.WhenAny(pending).ConfigureAwait(false);
                    await finished.ConfigureAwait(false);
                    pending.Remove(finished);
                }
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    /// <summary>Serves one accepted connection: reads each message whole by its length prefix, and acknowledges it.</summary>
    private static async Task ServeAsync(
        Socket accepted,
        int connection,
        UnixLoopbackOptions options,
        TruthLog truth,
        string path,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[options.MaximumMessageBytes + LengthPrefixBytes];
        byte[] ack = new byte[AckBytes];
        using (accepted)
        {
            truth.Write(TruthEventKind.ConnectionAccepted, callId: connection, resourceName: path);
            for (int message = 0; message < options.MessagesPerConnection; message++)
            {
                int read = await ReadExactlyAsync(accepted, buffer, LengthPrefixBytes, cancellationToken).ConfigureAwait(false);
                if (read < LengthPrefixBytes)
                {
                    truth.Write(TruthEventKind.Failure, callId: message, resourceName: path,
                        status: "the peer closed before the declared length arrived");
                    break;
                }

                int declared = BinaryPrimitives.ReadInt32LittleEndian(buffer);
                int received = await ReadExactlyAsync(accepted, buffer, declared, cancellationToken).ConfigureAwait(false);

                // The prefix and the payload arrive as the sender wrote them, one message: the log counts both.
                truth.Write(
                    TruthEventKind.MessageReceived,
                    callId: message,
                    resourceName: path,
                    declaredBytes: LengthPrefixBytes + declared,
                    completedBytes: LengthPrefixBytes + received,
                    status: received == declared ? "complete" : "short read");

                BinaryPrimitives.WriteInt64LittleEndian(ack, message);
                int sent = await accepted.SendAsync(ack, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                truth.Write(TruthEventKind.MessageSent, callId: message, resourceName: path, declaredBytes: AckBytes, completedBytes: sent);
            }

            truth.Write(TruthEventKind.ConnectionClosed, callId: connection, resourceName: path);
        }
    }

    public static async Task<int> RunClientAsync(UnixLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        await using var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-client.jsonl"),
            UnixLoopbackOptions.ScenarioId,
            "client");
        truth.Write(TruthEventKind.ProcessStarted);
        await WaitForStartAsync(options, cancellationToken).ConfigureAwait(false);

        int concurrency = Math.Clamp(options.Concurrency, 1, Math.Max(1, options.Connections));
        var pending = new List<Task>(concurrency);
        for (int connection = 0; connection < options.Connections; connection++)
        {
            int index = connection;
            pending.Add(Task.Run(() => ExchangeAsync(index, options, truth, cancellationToken), cancellationToken));
            if (pending.Count >= concurrency)
            {
                Task finished = await Task.WhenAny(pending).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                pending.Remove(finished);
            }
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    /// <summary>
    /// Runs one connection's exchange, its sizes drawn from its own generator, seeded from the run seed and the connection
    /// index, so a concurrent run is as reproducible as a sequential one (I14).
    /// </summary>
    private static async Task ExchangeAsync(int connection, UnixLoopbackOptions options, TruthLog truth, CancellationToken cancellationToken)
    {
        var sizes = new Random(options.Seed + connection);
        byte[] ack = new byte[AckBytes];
        string path = options.ResolvedSocketPath;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
        truth.Write(TruthEventKind.ConnectionEstablished, callId: connection, resourceName: path);
        for (int message = 0; message < options.MessagesPerConnection; message++)
        {
            int payload = sizes.Next(options.MinimumMessageBytes, options.MaximumMessageBytes + 1);
            byte[] buffer = new byte[LengthPrefixBytes + payload];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, payload);
            for (int index = LengthPrefixBytes; index < buffer.Length; index++)
            {
                buffer[index] = (byte)((index - LengthPrefixBytes) % 251);
            }

            int sent = await socket.SendAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            truth.Write(
                TruthEventKind.MessageSent,
                callId: message,
                resourceName: path,
                declaredBytes: buffer.Length,
                completedBytes: sent,
                status: sent == buffer.Length ? "complete" : "partial send");

            int received = await ReadExactlyAsync(socket, ack, AckBytes, cancellationToken).ConfigureAwait(false);
            truth.Write(TruthEventKind.MessageReceived, callId: message, resourceName: path, declaredBytes: AckBytes, completedBytes: received);
            if (options.InterMessageDelayMilliseconds > 0)
            {
                await Task.Delay(options.InterMessageDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        socket.Shutdown(SocketShutdown.Both);
        truth.Write(TruthEventKind.ConnectionClosed, callId: connection, resourceName: path);
    }

    private static async Task WaitForStartAsync(UnixLoopbackOptions options, CancellationToken cancellationToken)
    {
        if (options.WaitForStart)
        {
            _ = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static Process StartChild(string executable, string verb, UnixLoopbackOptions options, bool redirectStdout)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectStdout,
        };
        start.ArgumentList.Add(verb);
        start.ArgumentList.Add("--truth");
        start.ArgumentList.Add(options.TruthDirectory);
        start.ArgumentList.Add("--seed");
        start.ArgumentList.Add(options.Seed.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--connections");
        start.ArgumentList.Add(options.Connections.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--messages");
        start.ArgumentList.Add(options.MessagesPerConnection.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--bytes");
        start.ArgumentList.Add(options.MaximumMessageBytes.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--delay");
        start.ArgumentList.Add(options.InterMessageDelayMilliseconds.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--concurrency");
        start.ArgumentList.Add(options.Concurrency.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--socket");
        start.ArgumentList.Add(options.ResolvedSocketPath);
        return Process.Start(start)
            ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }

    private static async Task<int> ReadExactlyAsync(Socket socket, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < count)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(total, count - total), SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }
}
