using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one seeded run. The same seed and counts reproduce the same datagram sizes (I14).</summary>
internal sealed record UdpLoopbackOptions
{
    public const string ScenarioId = "FX-UDP-001";

    public required string TruthDirectory { get; init; }
    public int Seed { get; init; } = 20_260_923;

    /// <summary>Client sockets, each bound to its own port, so each is a flow of its own.</summary>
    public int Sockets { get; init; } = 2;
    public int DatagramsPerSocket { get; init; } = 8;
    public int MinimumDatagramBytes { get; init; } = 64;

    /// <summary>Kept under a typical path MTU, so a datagram is one datagram on every interface it could cross.</summary>
    public int MaximumDatagramBytes { get; init; } = 1_400;
    public int InterDatagramDelayMilliseconds { get; init; } = 15;

    /// <summary>How long a process waits for one datagram before recording it as missing rather than hanging.</summary>
    public int ReceiveTimeoutMilliseconds { get; init; } = 10_000;
    public int Port { get; init; }
}

/// <summary>
/// A two-process UDP loopback workload with an independent truth log per process (FX-UDP-001, section 13.1). The
/// client sends seeded datagrams from several sockets; the server acknowledges each one to the endpoint it came from.
/// Both directions are exercised, and every truth record names the process's own port and the other one, so a capture
/// can be checked for which endpoint a descriptor names first. It uses ordinary sockets only: nothing here observes
/// ETW or InterCat.
/// </summary>
internal static class UdpLoopbackScenario
{
    private const int AckBytes = 8;
    private const int HeaderBytes = 8;

    private static readonly JsonSerializerOptions SummaryOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Runs the coordinator: it starts the server and the client as separate processes.</summary>
    public static async Task<int> RunCoordinatorAsync(UdpLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        using Process server = StartChild(executable, "udp-loopback-server", options, port: 0, redirectStdout: true);
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

        using Process client = StartChild(executable, "udp-loopback-client", options, port, redirectStdout: false);
        await client.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var summary = new
        {
            scenarioId = UdpLoopbackOptions.ScenarioId,
            options.Seed,
            options.Sockets,
            options.DatagramsPerSocket,
            options.MinimumDatagramBytes,
            options.MaximumDatagramBytes,
            options.InterDatagramDelayMilliseconds,
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

    /// <summary>
    /// Receives every datagram the client will send and acknowledges each to the endpoint it came from. A datagram that
    /// does not arrive in time is recorded as a failure rather than waited for forever: UDP promises no delivery.
    /// </summary>
    public static async Task<int> RunServerAsync(UdpLoopbackOptions options, CancellationToken cancellationToken)
    {
        await using var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-server.jsonl"),
            UdpLoopbackOptions.ScenarioId,
            "server");
        truth.Write(TruthEventKind.ProcessStarted);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        truth.Write(TruthEventKind.ListenerBound, localPort: port);

        await Console.Out.WriteLineAsync(
            string.Create(CultureInfo.InvariantCulture, $"{{\"port\":{port}}}")).ConfigureAwait(false);
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);

        byte[] buffer = new byte[options.MaximumDatagramBytes + HeaderBytes];
        byte[] ack = new byte[AckBytes];
        int expected = options.Sockets * options.DatagramsPerSocket;
        int exitCode = 0;
        for (int datagram = 0; datagram < expected; datagram++)
        {
            SocketReceiveFromResult received;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(options.ReceiveTimeoutMilliseconds);
                try
                {
                    received = await socket
                        .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    truth.Write(
                        TruthEventKind.Failure,
                        localPort: port,
                        status: $"{expected - datagram} datagrams did not arrive within the receive timeout");
                    exitCode = 1;
                    break;
                }
            }

            var sender = (IPEndPoint)received.RemoteEndPoint;
            long message = received.ReceivedBytes >= HeaderBytes ? BinaryPrimitives.ReadInt32LittleEndian(buffer) : -1;
            int declared = received.ReceivedBytes >= HeaderBytes ? BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4)) : -1;
            truth.Write(
                TruthEventKind.MessageReceived,
                callId: message,
                localPort: port,
                remotePort: sender.Port,
                declaredBytes: declared,
                completedBytes: received.ReceivedBytes,
                status: received.ReceivedBytes == declared ? "complete" : "truncated datagram");

            BinaryPrimitives.WriteInt64LittleEndian(ack, message);
            int sent = await socket.SendToAsync(ack, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
            truth.Write(
                TruthEventKind.MessageSent,
                callId: message,
                localPort: port,
                remotePort: sender.Port,
                declaredBytes: AckBytes,
                completedBytes: sent);
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return exitCode;
    }

    /// <summary>
    /// Sends each socket's datagrams in turn and waits for each acknowledgement. Each socket draws its sizes from its own
    /// generator, seeded from the run seed and the socket's index, so the sizes do not depend on timing (I14).
    /// </summary>
    public static async Task<int> RunClientAsync(UdpLoopbackOptions options, CancellationToken cancellationToken)
    {
        await using var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-client.jsonl"),
            UdpLoopbackOptions.ScenarioId,
            "client");
        truth.Write(TruthEventKind.ProcessStarted);
        if (options.MinimumDatagramBytes < HeaderBytes || options.MaximumDatagramBytes < options.MinimumDatagramBytes)
        {
            truth.Write(
                TruthEventKind.Failure,
                status: $"datagram sizes must lie between {HeaderBytes} and a maximum at least the minimum");
            return 2;
        }

        var server = new IPEndPoint(IPAddress.Loopback, options.Port);
        byte[] ack = new byte[AckBytes];
        int exitCode = 0;
        for (int index = 0; index < options.Sockets && exitCode == 0; index++)
        {
            var sizes = new Random(options.Seed + index);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Bound before the first send, so the port the truth log names is the one every datagram leaves from.
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int local = ((IPEndPoint)socket.LocalEndPoint!).Port;
            truth.Write(TruthEventKind.ListenerBound, callId: index, localPort: local);

            for (int datagram = 0; datagram < options.DatagramsPerSocket; datagram++)
            {
                int message = (index * options.DatagramsPerSocket) + datagram;
                int payload = sizes.Next(options.MinimumDatagramBytes, options.MaximumDatagramBytes + 1);
                byte[] buffer = new byte[payload];
                for (int offset = HeaderBytes; offset < buffer.Length; offset++)
                {
                    buffer[offset] = (byte)((offset - HeaderBytes) % 251);
                }

                BinaryPrimitives.WriteInt32LittleEndian(buffer, message);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), payload);
                int sent = await socket.SendToAsync(buffer, SocketFlags.None, server, cancellationToken).ConfigureAwait(false);
                truth.Write(
                    TruthEventKind.MessageSent,
                    callId: message,
                    localPort: local,
                    remotePort: server.Port,
                    declaredBytes: payload,
                    completedBytes: sent,
                    status: sent == payload ? "complete" : "partial send");

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(options.ReceiveTimeoutMilliseconds);
                    try
                    {
                        SocketReceiveFromResult received = await socket
                            .ReceiveFromAsync(ack, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token)
                            .ConfigureAwait(false);
                        truth.Write(
                            TruthEventKind.MessageReceived,
                            callId: message,
                            localPort: local,
                            remotePort: ((IPEndPoint)received.RemoteEndPoint).Port,
                            declaredBytes: AckBytes,
                            completedBytes: received.ReceivedBytes);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        truth.Write(
                            TruthEventKind.Failure,
                            callId: message,
                            localPort: local,
                            remotePort: server.Port,
                            status: "the acknowledgement did not arrive within the receive timeout");
                        exitCode = 1;
                        break;
                    }
                }

                if (options.InterDatagramDelayMilliseconds > 0)
                {
                    await Task.Delay(options.InterDatagramDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return exitCode;
    }

    private static Process StartChild(
        string executable,
        string verb,
        UdpLoopbackOptions options,
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
        start.ArgumentList.Add("--sockets");
        start.ArgumentList.Add(options.Sockets.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--messages");
        start.ArgumentList.Add(options.DatagramsPerSocket.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--bytes");
        start.ArgumentList.Add(options.MaximumDatagramBytes.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--delay");
        start.ArgumentList.Add(options.InterDatagramDelayMilliseconds.ToString(CultureInfo.InvariantCulture));
        if (port > 0)
        {
            start.ArgumentList.Add("--port");
            start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }
}
