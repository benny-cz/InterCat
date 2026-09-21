using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one seeded run. The same seed and counts reproduce the same declared sizes (I14).</summary>
internal sealed record TcpLoopbackOptions
{
    public const string ScenarioId = "FX-TCP-001";

    public required string TruthDirectory { get; init; }
    public int Seed { get; init; } = 20_260_920;
    public int Connections { get; init; } = 2;
    public int MessagesPerConnection { get; init; } = 8;
    public int MaximumMessageBytes { get; init; } = 4_096;
    public int MinimumMessageBytes { get; init; } = 64;
    /// <summary>
    /// Pacing between messages. The 15 ms default keeps the fixture readable; a load series sets it to
    /// zero so the workload, rather than the pacing, decides the rate. It is recorded with every run,
    /// because a rate without its pacing is not a measurement (section 12).
    /// </summary>
    public int InterMessageDelayMilliseconds { get; init; } = 15;

    /// <summary>
    /// How many connections are in flight at once. One keeps the original sequential exchange, where the
    /// fixture's own round trips set the rate. A higher value lets the machine rather than the fixture
    /// decide the rate, which is what a benchmark needs before any section 12 ingest budget can be judged
    /// (IC-010).
    /// </summary>
    public int Concurrency { get; init; } = 1;
    public int Port { get; init; }
}

/// <summary>
/// A two-process TCP loopback workload with an independent truth log per process (IC-004, section 13.1
/// scenario 1). It uses ordinary sockets only: nothing here observes ETW or InterCat.
/// </summary>
internal static class TcpLoopbackScenario
{
    private const int AckBytes = 8;
    private const int LengthPrefixBytes = 4;

    private static readonly JsonSerializerOptions SummaryOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Runs the coordinator: it starts the server and the client as separate processes.</summary>
    public static async Task<int> RunCoordinatorAsync(TcpLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        using Process server = StartChild(executable, "tcp-loopback-server", options, port: 0, redirectStdout: true);
        string? handshake = await server.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (handshake is null)
        {
            await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The server process exited before it reported a port.").ConfigureAwait(false);
            return 1;
        }

        int port;
        try
        {
            using JsonDocument document = JsonDocument.Parse(handshake);
            port = document.RootElement.GetProperty("port").GetInt32();
        }
        catch (JsonException exception)
        {
            await Console.Error.WriteLineAsync($"The server handshake was unreadable: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }

        using Process client = StartChild(executable, "tcp-loopback-client", options, port, redirectStdout: false);
        await client.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var summary = new
        {
            scenarioId = TcpLoopbackOptions.ScenarioId,
            options.Seed,
            options.Connections,
            options.MessagesPerConnection,
            options.MinimumMessageBytes,
            options.MaximumMessageBytes,
            options.InterMessageDelayMilliseconds,
            options.Concurrency,
            port,
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

    public static async Task<int> RunServerAsync(TcpLoopbackOptions options, CancellationToken cancellationToken)
    {
        await using var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-server.jsonl"),
            TcpLoopbackOptions.ScenarioId,
            "server");
        truth.Write(TruthEventKind.ProcessStarted);

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(options.Connections + 1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        truth.Write(TruthEventKind.ListenerBound, localPort: port);

        await Console.Out.WriteLineAsync(
            string.Create(CultureInfo.InvariantCulture, $"{{\"port\":{port}}}")).ConfigureAwait(false);
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Accept every connection first, then serve them together. Accepting inside the serve loop would
        // make the fixture sequential no matter what concurrency was asked for (IC-010).
        int concurrency = Math.Clamp(options.Concurrency, 1, Math.Max(1, options.Connections));
        var pending = new List<Task>(concurrency);
        for (int connection = 0; connection < options.Connections; connection++)
        {
            Socket accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            int index = connection;
            pending.Add(Task.Run(
                () => ServeAsync(accepted, index, options, truth, cancellationToken),
                cancellationToken));
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

    /// <summary>Serves one accepted connection. Several of these run at once when concurrency is above one.</summary>
    private static async Task ServeAsync(
        Socket accepted,
        int connection,
        TcpLoopbackOptions options,
        TruthLog truth,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[options.MaximumMessageBytes + LengthPrefixBytes];
        byte[] ack = new byte[AckBytes];
        using (accepted)
        {
            accepted.NoDelay = true;
            var local = (IPEndPoint)accepted.LocalEndPoint!;
            var remote = (IPEndPoint)accepted.RemoteEndPoint!;
            truth.Write(
                TruthEventKind.ConnectionAccepted,
                callId: connection,
                localPort: local.Port,
                remotePort: remote.Port);

            for (int message = 0; message < options.MessagesPerConnection; message++)
            {
                int declared = await ReadPrefixAsync(accepted, buffer, cancellationToken).ConfigureAwait(false);
                if (declared < 0)
                {
                    truth.Write(
                        TruthEventKind.Failure,
                        callId: message,
                        localPort: local.Port,
                        remotePort: remote.Port,
                        status: "the peer closed before the declared length arrived");
                    break;
                }

                int received = await ReadExactlyAsync(accepted, buffer, declared, cancellationToken).ConfigureAwait(false);
                truth.Write(
                    TruthEventKind.MessageReceived,
                    callId: message,
                    localPort: local.Port,
                    remotePort: remote.Port,
                    declaredBytes: declared,
                    completedBytes: received,
                    status: received == declared ? "complete" : "short read");

                BinaryPrimitives.WriteInt64LittleEndian(ack, message);
                int sent = await accepted.SendAsync(ack, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                truth.Write(
                    TruthEventKind.MessageSent,
                    callId: message,
                    localPort: local.Port,
                    remotePort: remote.Port,
                    declaredBytes: AckBytes,
                    completedBytes: sent);
            }

            truth.Write(
                TruthEventKind.ConnectionClosed,
                callId: connection,
                localPort: local.Port,
                remotePort: remote.Port);
        }
    }

    public static async Task<int> RunClientAsync(TcpLoopbackOptions options, CancellationToken cancellationToken)
    {
        await using var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-client.jsonl"),
            TcpLoopbackOptions.ScenarioId,
            "client");
        truth.Write(TruthEventKind.ProcessStarted);

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
    /// Runs one connection's exchange. Each connection draws its sizes from its own generator, seeded
    /// from the run seed and the connection index, so a concurrent run is as reproducible as a sequential
    /// one: interleaving cannot change which bytes a connection sends (I14).
    /// </summary>
    private static async Task ExchangeAsync(
        int connection,
        TcpLoopbackOptions options,
        TruthLog truth,
        CancellationToken cancellationToken)
    {
        var sizes = new Random(options.Seed + connection);
        byte[] ack = new byte[AckBytes];
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, options.Port), cancellationToken)
                .ConfigureAwait(false);
            socket.NoDelay = true;
            var local = (IPEndPoint)socket.LocalEndPoint!;
            var remote = (IPEndPoint)socket.RemoteEndPoint!;
            truth.Write(
                TruthEventKind.ConnectionEstablished,
                callId: connection,
                localPort: local.Port,
                remotePort: remote.Port);

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
                    localPort: local.Port,
                    remotePort: remote.Port,
                    declaredBytes: buffer.Length,
                    completedBytes: sent,
                    status: sent == buffer.Length ? "complete" : "partial send");

                int received = await ReadExactlyAsync(socket, ack, AckBytes, cancellationToken).ConfigureAwait(false);
                truth.Write(
                    TruthEventKind.MessageReceived,
                    callId: message,
                    localPort: local.Port,
                    remotePort: remote.Port,
                    declaredBytes: AckBytes,
                    completedBytes: received);

                if (options.InterMessageDelayMilliseconds > 0)
                {
                    await Task.Delay(options.InterMessageDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }

            socket.Shutdown(SocketShutdown.Both);
            truth.Write(
                TruthEventKind.ConnectionClosed,
                callId: connection,
                localPort: local.Port,
                remotePort: remote.Port);
        }
    }

    private static Process StartChild(
        string executable,
        string verb,
        TcpLoopbackOptions options,
        int port,
        bool redirectStdout)
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
        if (port > 0)
        {
            start.ArgumentList.Add("--port");
            start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }

    private static async Task<int> ReadPrefixAsync(Socket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = await ReadExactlyAsync(socket, buffer, LengthPrefixBytes, cancellationToken).ConfigureAwait(false);
        return read < LengthPrefixBytes ? -1 : BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task<int> ReadExactlyAsync(
        Socket socket,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < count)
        {
            int read = await socket
                .ReceiveAsync(buffer.AsMemory(total, count - total), SocketFlags.None, cancellationToken)
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
