using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Sdk;

namespace InterCat.TestWorkloads;

/// <summary>
/// Parameters of one FX-SDK-003 or FX-SDK-004 run: its transport, its two connections' messages, and how many messages a
/// write carries and how many bytes a read asks for.
/// </summary>
internal sealed record SdkStreamOptions
{
    public const string PipeScenarioId = "FX-SDK-003";

    public const string SocketScenarioId = "FX-SDK-004";

    /// <summary>A frame's header: its message's length, then its sequence - 0 for the handshake that opens a connection.</summary>
    public const int HeaderBytes = 12;

    /// <summary>The longest message a frame carries.</summary>
    public const int MaximumMessageBytes = 4_096;

    public required string TruthDirectory { get; init; }

    /// <summary><c>pipe</c>, a named pipe in byte mode, or <c>socket</c>, a loopback TCP connection.</summary>
    public string Transport { get; init; } = "pipe";

    /// <summary>The pipe's name, which the coordinator chooses, or the listener's port, which the server does; null where not known yet.</summary>
    public string? Endpoint { get; init; }

    /// <summary>The identity both processes name the stream's channel by, which the coordinator chooses.</summary>
    public Guid Channel { get; init; }

    /// <summary>How many messages the client publishes over its first connection.</summary>
    public int Messages { get; init; } = 100;

    /// <summary>How many it publishes over its second, once it connected again: its sequence starts at 1 once more.</summary>
    public int RestartMessages { get; init; } = 30;

    /// <summary>The most messages one write carries: a write carries from one to this many, seeded.</summary>
    public int MessagesPerWrite { get; init; } = 3;

    /// <summary>The bytes each of the server's reads asks for - fewer than most messages, so a message spans reads.</summary>
    public int ReadBytes { get; init; } = 700;

    /// <summary>The seed of each write's messages and each message's length.</summary>
    public int Seed { get; init; } = 20_261_107;

    public bool Socket => Transport == "socket";

    public string ScenarioId => Socket ? SocketScenarioId : PipeScenarioId;

    /// <summary>The name both processes give the stream's channel as they open it.</summary>
    public string ChannelName => Socket ? "fx-sdk-socket" : "fx-sdk-pipe";

    public InterCatChannelKind Kind => Socket ? InterCatChannelKind.Socket : InterCatChannelKind.Pipe;
}

/// <summary>
/// FX-SDK-003 and FX-SDK-004: a client sending framed messages to a server over a byte stream - a named pipe, or a loopback
/// TCP connection - each process saying what it publishes and consumes through InterCat's cooperative SDK as an application
/// would (ADR-102). A write carries one to three messages, and the server reads a few hundred bytes at a time, so neither a
/// write nor a read is a message: the SDK says each message's boundaries as the application frames them, its logical length
/// beside the bytes its frame copied into the stream. Each connection is an epoch of the channel - the client connects
/// again for the second, its sequence from 1 once more - and an operation of the client's, whose identity its first frame
/// carries, under which the server starts its own. At the second's end the client publishes one message the server reads
/// but never consumes, which stays open. Each process logs every message it published or consumed by its channel, epoch,
/// sequence and reader, with its length, and the operations it started; none reads InterCat state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SdkStreamScenario
{
    /// <summary>The stream's connections: the first, and the one the client makes again.</summary>
    private const int Epochs = 2;

    /// <summary>The server's number as the channel's reader.</summary>
    private const int Reader = 1;

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// The coordinator: chooses the channel's identity and the pipe's name, starts the server, then the client once the
    /// server listens, and writes the run's parameters, each side's writes and reads, and the operations both started.
    /// </summary>
    public static async Task<int> RunCoordinatorAsync(SdkStreamOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");
        SdkStreamOptions run = options with
        {
            Channel = Guid.NewGuid(),
            Endpoint = options.Socket ? null : $"InterCat-{options.ScenarioId}-{Guid.NewGuid():N}",
        };
        using Process server = StartChild(executable, "sdk-stream-server", run);
        string? listening = await server.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (listening is null || !listening.StartsWith("listening ", StringComparison.Ordinal))
        {
            await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The server exited before it listened.").ConfigureAwait(false);
            return 1;
        }

        using Process client = StartChild(executable, "sdk-stream-client", run with { Endpoint = listening["listening ".Length..] });
        await Task.WhenAll(server.WaitForExitAsync(cancellationToken), client.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        if (server.ExitCode != 0 || client.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync($"The server exited with {server.ExitCode}, the client with {client.ExitCode}.").ConfigureAwait(false);
            return 1;
        }

        // Each side's own account of the run, merged into the scenario: the client's writes and the server's reads of
        // each connection, the messages the server read and never consumed, and the operations each started.
        SideSummary written = await ReadSummaryAsync(run, "client", cancellationToken).ConfigureAwait(false);
        SideSummary read = await ReadSummaryAsync(run, "server", cancellationToken).ConfigureAwait(false);
        var summary = new
        {
            scenarioId = run.ScenarioId,
            transport = run.Transport,
            channel = run.Channel,
            channelName = run.ChannelName,
            library = InterCatSdkEventSource.Library,
            run.Messages,
            run.RestartMessages,
            run.MessagesPerWrite,
            run.ReadBytes,
            run.Seed,
            epochs = Epochs,
            writes = written.Transfers,
            reads = read.Transfers,
            readNeverConsumed = read.Unconsumed,
            operations = written.Operations.Concat(read.Operations).ToArray(),
        };
        await File.WriteAllTextAsync(Path.Combine(run.TruthDirectory, "scenario.json"), JsonSerializer.Serialize(summary, SummaryOptions),
            cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The server: listens, and serves each of the client's connections as an epoch of the channel.</summary>
    public static async Task<int> RunServerAsync(SdkStreamOptions options, CancellationToken cancellationToken)
    {
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-server.jsonl"), options.ScenarioId, "server");
        truth.Write(TruthEventKind.ProcessStarted);
        var served = new SideSummary();
        if (options.Socket)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            truth.Write(TruthEventKind.ListenerBound, localPort: port);
            await SayAsync($"listening {port.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
            for (long epoch = 1; epoch <= Epochs; epoch++)
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                truth.Write(TruthEventKind.ConnectionAccepted, localPort: port, remotePort: ((IPEndPoint)accepted.Client.RemoteEndPoint!).Port);
                await using NetworkStream stream = accepted.GetStream();
                if (!await ServeAsync(options, stream, epoch, truth, served, cancellationToken).ConfigureAwait(false))
                {
                    return 1;
                }

                truth.Write(TruthEventKind.ConnectionClosed, localPort: port);
            }
        }
        else
        {
            string name = options.Endpoint ?? throw new InvalidOperationException("The server takes the pipe's name from --endpoint.");
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            truth.Write(TruthEventKind.ListenerBound, resourceName: PipePath(name));
            await SayAsync($"listening {name}", cancellationToken).ConfigureAwait(false);
            for (long epoch = 1; epoch <= Epochs; epoch++)
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                truth.Write(TruthEventKind.ConnectionAccepted, resourceName: PipePath(name));
                if (!await ServeAsync(options, pipe, epoch, truth, served, cancellationToken).ConfigureAwait(false))
                {
                    return 1;
                }

                pipe.Disconnect();
                truth.Write(TruthEventKind.ConnectionClosed, resourceName: PipePath(name));
            }
        }

        await WriteSummaryAsync(options, "server", served, cancellationToken).ConfigureAwait(false);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    /// <summary>The client: connects once an epoch, and writes that epoch's messages, framed, a few to a write.</summary>
    public static async Task<int> RunClientAsync(SdkStreamOptions options, CancellationToken cancellationToken)
    {
        string endpoint = options.Endpoint ?? throw new InvalidOperationException("The client takes the server's endpoint from --endpoint.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-client.jsonl"), options.ScenarioId, "client");
        truth.Write(TruthEventKind.ProcessStarted);
        var random = new Random(options.Seed);
        var sent = new SideSummary();
        for (long epoch = 1; epoch <= Epochs; epoch++)
        {
            long count = epoch == 1 ? options.Messages : options.RestartMessages;
            using (InterCatChannel channel = InterCatChannel.Open(options.Channel, options.ChannelName, options.Kind, epoch))
            {
                InterCatOperation operation = InterCatOperation.Start($"connection-{epoch.ToString(CultureInfo.InvariantCulture)}");
                int writes = 0;
                await using (Stream stream = await ConnectAsync(options, endpoint, truth, cancellationToken).ConfigureAwait(false))
                {
                    // The handshake names the client's operation, under which the server starts its own.
                    await stream.WriteAsync(Frame([(0, 16)], operation.Id), cancellationToken).ConfigureAwait(false);
                    writes++;

                    // At the last epoch's end, one more message - alone in its write - which the server reads and never consumes.
                    long last = epoch == Epochs ? count + 1 : count;
                    for (long sequence = 1; sequence <= last;)
                    {
                        int carried = sequence > count ? 1 : (int)Math.Min(random.Next(1, options.MessagesPerWrite + 1), count - sequence + 1);
                        (long Sequence, int Length)[] messages =
                            [.. Enumerable.Range(0, carried).Select(at => (sequence + at, random.Next(32, SdkStreamOptions.MaximumMessageBytes + 1)))];
                        await stream.WriteAsync(Frame(messages, Guid.Empty), cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        writes++;

                        // Each message is committed - where the server can read it - once its write returns.
                        foreach ((long published, int length) in messages)
                        {
                            channel.Published(published, length, copiedLength: SdkStreamOptions.HeaderBytes + length);
                            truth.Write(TruthEventKind.MessageSent, declaredBytes: length, completedBytes: SdkStreamOptions.HeaderBytes + length,
                                resourceName: options.ChannelName, status: published > count ? "unread" : null,
                                message: new(options.Channel, epoch, published, null));
                        }

                        sequence += carried;
                    }
                }

                // The connection closed, its operation completes: the server reads to the stream's end.
                truth.Write(TruthEventKind.ConnectionClosed, resourceName: options.ChannelName);
                operation.Complete();
                sent.Transfers.Add(writes);
                sent.Operations.Add(new(operation.Id, null, operation.Name, 0, 1, "client"));
            }
        }

        await WriteSummaryAsync(options, "client", sent, cancellationToken).ConfigureAwait(false);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    /// <summary>
    /// Serves one connection as an epoch of the channel: reads the handshake, starts the server's operation under the
    /// client's, and consumes the epoch's messages as their frames complete - a read a few hundred bytes long - until the
    /// stream ends, leaving any message past the epoch's count read but never consumed.
    /// </summary>
    private static async Task<bool> ServeAsync(SdkStreamOptions options, Stream stream, long epoch, TruthLog truth, SideSummary served,
        CancellationToken cancellationToken)
    {
        long count = epoch == 1 ? options.Messages : options.RestartMessages;
        byte[] buffer = new byte[options.ReadBytes];
        byte[] frame = new byte[SdkStreamOptions.HeaderBytes + SdkStreamOptions.MaximumMessageBytes];
        int have = 0;
        int reads = 0;
        long consumed = 0;
        long unconsumed = 0;
        InterCatOperation? operation = null;
        Guid parent = Guid.Empty;
        using InterCatChannel channel = InterCatChannel.Open(options.Channel, options.ChannelName, options.Kind, epoch);
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            reads++;
            for (int at = 0; at < read;)
            {
                // A frame's header first, then as much of its message as its header says.
                int wanted = have < SdkStreamOptions.HeaderBytes ? SdkStreamOptions.HeaderBytes : SdkStreamOptions.HeaderBytes + Length(frame);
                int taken = Math.Min(wanted - have, read - at);
                buffer.AsSpan(at, taken).CopyTo(frame.AsSpan(have));
                have += taken;
                at += taken;
                if (have == SdkStreamOptions.HeaderBytes && Length(frame) is < 0 or > SdkStreamOptions.MaximumMessageBytes)
                {
                    truth.Write(TruthEventKind.Failure, status: "a frame's header names no message the client frames");
                    return false;
                }

                if (have < SdkStreamOptions.HeaderBytes || have < SdkStreamOptions.HeaderBytes + Length(frame))
                {
                    continue;
                }

                int length = Length(frame);
                long sequence = BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(4));
                ReadOnlySpan<byte> message = frame.AsSpan(SdkStreamOptions.HeaderBytes, length);
                have = 0;
                if (sequence == 0)
                {
                    // The handshake: the client's operation, as its bytes say it, under which the server starts its own.
                    parent = new Guid(message[..16]);
                    operation = InterCatOperation.Start($"serve-{epoch.ToString(CultureInfo.InvariantCulture)}", parent);
                    continue;
                }

                if (!message.SequenceEqual(Payload(sequence, length)))
                {
                    truth.Write(TruthEventKind.Failure, status: "a message's bytes are not those its frame was written with");
                    return false;
                }

                // The epoch's messages are consumed as each completes; one past them is read and never consumed.
                if (consumed < count)
                {
                    channel.Consumed(sequence, length, consumer: Reader);
                    truth.Write(TruthEventKind.MessageReceived, declaredBytes: length, resourceName: options.ChannelName,
                        message: new(options.Channel, epoch, sequence, Reader));
                    consumed++;
                }
                else
                {
                    unconsumed++;
                }
            }
        }

        if (operation is null || have != 0 || consumed != count)
        {
            truth.Write(TruthEventKind.Failure, status: "the connection ended before its handshake, a frame or its messages");
            return false;
        }

        // A connection that left a message unconsumed fails the server's own operation.
        long status = unconsumed > 0 ? 1 : 0;
        operation.Complete(status);
        served.Transfers.Add(reads);
        served.Unconsumed += unconsumed;
        served.Operations.Add(new(operation.Id, parent, operation.Name, status, 1, "server"));
        return true;
    }

    private static async Task<Stream> ConnectAsync(SdkStreamOptions options, string endpoint, TruthLog truth, CancellationToken cancellationToken)
    {
        if (options.Socket)
        {
            int port = int.Parse(endpoint, NumberStyles.None, CultureInfo.InvariantCulture);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            truth.Write(TruthEventKind.ConnectionEstablished, localPort: ((IPEndPoint)socket.LocalEndPoint!).Port, remotePort: port);
            return new NetworkStream(socket, ownsSocket: true);
        }

        var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.Out, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        truth.Write(TruthEventKind.ConnectionEstablished, resourceName: PipePath(endpoint));
        return pipe;
    }

    /// <summary>
    /// One write's bytes: each message's frame - its length, its sequence and its bytes - or the handshake's, whose bytes are
    /// the client's operation.
    /// </summary>
    private static byte[] Frame((long Sequence, int Length)[] messages, Guid operation)
    {
        byte[] bytes = new byte[messages.Sum(message => SdkStreamOptions.HeaderBytes + message.Length)];
        int at = 0;
        foreach ((long sequence, int length) in messages)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at + 4), sequence);
            Span<byte> message = bytes.AsSpan(at + SdkStreamOptions.HeaderBytes, length);
            if (sequence == 0)
            {
                _ = operation.TryWriteBytes(message);
            }
            else
            {
                Payload(sequence, length).CopyTo(message);
            }

            at += SdkStreamOptions.HeaderBytes + length;
        }

        return bytes;
    }

    /// <summary>A message's bytes, which its sequence decides, so the server can tell a frame was read whole and in order.</summary>
    private static byte[] Payload(long sequence, int length)
    {
        byte[] bytes = new byte[length];
        for (int at = 0; at < length; at++)
        {
            bytes[at] = unchecked((byte)(sequence + at));
        }

        return bytes;
    }

    private static int Length(byte[] frame) => BinaryPrimitives.ReadInt32LittleEndian(frame);

    private static string PipePath(string name) => @"\\.\pipe\" + name;

    private static async Task SayAsync(string line, CancellationToken cancellationToken)
    {
        await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSummaryAsync(SdkStreamOptions options, string role, SideSummary summary, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, $"summary-{role}.json"), JsonSerializer.Serialize(summary, SummaryOptions),
            cancellationToken).ConfigureAwait(false);

    /// <summary>A side's summary, which the coordinator merges into the scenario and then deletes.</summary>
    private static async Task<SideSummary> ReadSummaryAsync(SdkStreamOptions options, string role, CancellationToken cancellationToken)
    {
        string path = Path.Combine(options.TruthDirectory, $"summary-{role}.json");
        SideSummary summary = JsonSerializer.Deserialize<SideSummary>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), SummaryOptions)
            ?? throw new InvalidOperationException($"The {role}'s summary is empty.");
        File.Delete(path);
        return summary;
    }

    private static Process StartChild(string executable, string verb, SdkStreamOptions options)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in new[]
        {
            verb, "--truth", options.TruthDirectory, "--transport", options.Transport, "--channel", options.Channel.ToString(),
            "--messages", options.Messages.ToString(CultureInfo.InvariantCulture),
            "--restart-messages", options.RestartMessages.ToString(CultureInfo.InvariantCulture),
            "--per-write", options.MessagesPerWrite.ToString(CultureInfo.InvariantCulture),
            "--read-bytes", options.ReadBytes.ToString(CultureInfo.InvariantCulture),
            "--seed", options.Seed.ToString(CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(argument);
        }

        if (options.Endpoint is { } endpoint)
        {
            start.ArgumentList.Add("--endpoint");
            start.ArgumentList.Add(endpoint);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }

    /// <summary>One side's account of the run: its writes or reads of each connection, what it read and never consumed, and its operations.</summary>
    private sealed class SideSummary
    {
        public List<int> Transfers { get; init; } = [];

        public long Unconsumed { get; set; }

        public List<SaidOperation> Operations { get; init; } = [];
    }

    /// <summary>An operation a side started: its identity, its parent's where it has one, its name, its status, how many times it was completed, and its side.</summary>
    private sealed record SaidOperation(Guid Operation, Guid? Parent, string Name, long Status, int Completions, string Role);
}
