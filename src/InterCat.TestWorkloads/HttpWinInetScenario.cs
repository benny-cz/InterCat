using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one seeded WinINet exchange.</summary>
internal sealed record HttpWinInetOptions
{
    public const string ScenarioId = "FX-HTTP-001";

    public required string TruthDirectory { get; init; }
    public int Seed { get; init; } = 20_260_929;
    public int Requests { get; init; } = 16;

    /// <summary>The longest body either way: past ETW's 64 KiB event limit, so how a source splits or cuts one shows.</summary>
    public int MaximumBodyBytes { get; init; } = 96 * 1024;

    /// <summary>Whether to wait for a line on standard input before the first request, so a capture enabled for this
    /// process is in place before it sends anything.</summary>
    public bool WaitForStart { get; init; }

    public int StartWaitSeconds { get; init; } = 30;
}

/// <summary>
/// FX-HTTP-001: a seeded run of HTTP/1.1 requests made through WinINet, Windows' own HTTP client, to a server this process
/// runs on loopback. The server is the wire-level witness: it logs the exact bytes of every request head and body it
/// received and every response head and body it sent, as a length and a SHA-256, never the bytes; the client logs what it
/// sent and read the same way. Bodies run from empty to past ETW's 64 KiB event limit, in both directions, so a source
/// that records them shows whether it keeps each whole, splits it or cuts it. Nothing leaves the machine.
/// </summary>
internal static partial class HttpWinInetScenario
{
    private const int OpenTypeDirect = 1;
    private const int ServiceHttp = 3;

    // No cache, no cookies, no prompts, and one kept-alive connection: every request goes to the server as it was made.
    private const uint RequestFlags = 0x80000000 | 0x04000000 | 0x00000100 | 0x00080000 | 0x00000200 | 0x00400000;
    private const string FixtureHeader = "X-InterCat-Fixture: FX-HTTP-001\r\n";
    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [LibraryImport("wininet.dll", EntryPoint = "InternetOpenW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr InternetOpen(string agent, int accessType, string? proxy, string? proxyBypass, int flags);

    [LibraryImport("wininet.dll", EntryPoint = "InternetConnectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr InternetConnect(IntPtr internet, string serverName, ushort port, string? user, string? password,
        int service, int flags, IntPtr context);

    [LibraryImport("wininet.dll", EntryPoint = "HttpOpenRequestW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr HttpOpenRequest(IntPtr connect, string verb, string objectName, string? version, string? referrer,
        IntPtr acceptTypes, uint flags, IntPtr context);

    [LibraryImport("wininet.dll", EntryPoint = "HttpSendRequestW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HttpSendRequest(IntPtr request, string? headers, int headersLength, byte[] optional, int optionalLength);

    [LibraryImport("wininet.dll", EntryPoint = "InternetReadFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetReadFile(IntPtr file, byte[] buffer, int toRead, out int read);

    [LibraryImport("wininet.dll", EntryPoint = "InternetCloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetCloseHandle(IntPtr handle);

    public static async Task<int> RunAsync(HttpWinInetOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int completed;
        long sentBytes = 0, readBytes = 0;
        await using (var server = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-http-server.jsonl"), HttpWinInetOptions.ScenarioId, "server"))
        await using (var client = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-http-client.jsonl"), HttpWinInetOptions.ScenarioId, "client"))
        {
            server.Write(TruthEventKind.ListenerBound, localPort: port);
            client.Write(TruthEventKind.ProcessStarted);
            if (options.WaitForStart && !await StartedAsync(options.StartWaitSeconds, cancellationToken).ConfigureAwait(false))
            {
                await Console.Error.WriteLineAsync("No start line arrived within the wait; no request was made.")
                    .ConfigureAwait(false);
                return 3;
            }

            using var serving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task served = ServeAsync(listener, server, options, serving.Token);
            (completed, sentBytes, readBytes) = await Task.Factory.StartNew(
                () => Exchange(port, client, options, cancellationToken), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            await serving.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            try
            {
                await served.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The listener was stopped under the accept loop; every exchange had finished.
            }

            client.Write(TruthEventKind.ProcessExiting);
        }

        var summary = new
        {
            scenarioId = HttpWinInetOptions.ScenarioId,
            processId = Environment.ProcessId,
            port,
            options.Seed,
            options.Requests,
            completed,
            sentBytes,
            readBytes,
            options.MaximumBodyBytes,
            note = "The server's truth log names every head and body it received and sent by length and SHA-256; "
                + "the client's names what it sent and read. Neither holds a byte of them.",
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions), cancellationToken).ConfigureAwait(false);
        return completed == options.Requests ? 0 : 4;
    }

    private static async Task<bool> StartedAsync(int waitSeconds, CancellationToken cancellationToken)
    {
        Task<string?> line = Console.In.ReadLineAsync(cancellationToken).AsTask();
        Task finished = await Task.WhenAny(line, Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken))
            .ConfigureAwait(false);
        return finished == line && line.Result is not null;
    }

    /// <summary>The client: every request through WinINet on one kept-alive connection, each logged as sent and read.</summary>
    private static (int Completed, long Sent, long Read) Exchange(int port, TruthLog client, HttpWinInetOptions options,
        CancellationToken cancellationToken)
    {
        IntPtr internet = InternetOpen("InterCat-" + HttpWinInetOptions.ScenarioId, OpenTypeDirect, null, null, 0);
        if (internet == IntPtr.Zero)
        {
            client.Write(TruthEventKind.Failure, status: $"InternetOpen failed: {Marshal.GetLastPInvokeError()}");
            return (0, 0, 0);
        }

        int completed = 0;
        long sent = 0, read = 0;
        IntPtr connect = IntPtr.Zero;
        try
        {
            connect = InternetConnect(internet, "127.0.0.1", (ushort)port, null, null, ServiceHttp, 0, IntPtr.Zero);
            if (connect == IntPtr.Zero)
            {
                client.Write(TruthEventKind.Failure, status: $"InternetConnect failed: {Marshal.GetLastPInvokeError()}");
                return (0, 0, 0);
            }

            byte[] buffer = new byte[16 * 1024];
            for (int index = 0; index < options.Requests; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] body = Body(options.Seed, index, options.MaximumBodyBytes, request: true);
                IntPtr request = HttpOpenRequest(connect, "POST", string.Create(CultureInfo.InvariantCulture, $"/fx/{index}"),
                    "HTTP/1.1", null, IntPtr.Zero, RequestFlags, IntPtr.Zero);
                if (request == IntPtr.Zero)
                {
                    client.Write(TruthEventKind.Failure, callId: index, status: $"HttpOpenRequest failed: {Marshal.GetLastPInvokeError()}");
                    continue;
                }

                try
                {
                    client.Write(TruthEventKind.CallIssued, callId: index, declaredBytes: body.Length, status: "request-body",
                        resourceName: Sha256(body));
                    if (!HttpSendRequest(request, FixtureHeader, -1, body, body.Length))
                    {
                        client.Write(TruthEventKind.Failure, callId: index, status: $"HttpSendRequest failed: {Marshal.GetLastPInvokeError()}");
                        continue;
                    }

                    sent += body.Length;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long total = 0;
                    while (InternetReadFile(request, buffer, buffer.Length, out int count) && count > 0)
                    {
                        hash.AppendData(buffer, 0, count);
                        total += count;
                    }

                    read += total;
                    completed++;
                    client.Write(TruthEventKind.CallCompleted, callId: index, completedBytes: total, status: "response-body",
                        resourceName: "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()));
                }
                finally
                {
                    InternetCloseHandle(request);
                }
            }
        }
        finally
        {
            if (connect != IntPtr.Zero) InternetCloseHandle(connect);
            InternetCloseHandle(internet);
        }

        return (completed, sent, read);
    }

    /// <summary>The server: each connection's requests read to their last byte and answered, every part logged as the wire held it.</summary>
    private static async Task ServeAsync(TcpListener listener, TruthLog server, HttpWinInetOptions options,
        CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var remote = (IPEndPoint)connection.Client.RemoteEndPoint!;
            server.Write(TruthEventKind.ConnectionAccepted, localPort: ((IPEndPoint)listener.LocalEndpoint).Port, remotePort: remote.Port);
            connections.Add(ServeConnectionAsync(connection, server, options, cancellationToken));
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
    }

    private static async Task ServeConnectionAsync(TcpClient connection, TruthLog server, HttpWinInetOptions options,
        CancellationToken cancellationToken)
    {
        using (connection)
        {
            NetworkStream stream = connection.GetStream();
            var pending = new ArrayBufferWriter<byte>();
            byte[] chunk = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    // The head: every byte up to and including the blank line that ends it.
                    int headEnd;
                    while ((headEnd = pending.WrittenSpan.IndexOf("\r\n\r\n"u8)) < 0)
                    {
                        if (pending.WrittenCount > 64 * 1024) return;
                        int count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                        if (count == 0) return;
                        pending.Write(chunk.AsSpan(0, count));
                    }

                    byte[] head = pending.WrittenSpan[..(headEnd + 4)].ToArray();
                    string headText = Encoding.ASCII.GetString(head);
                    int index = RequestIndex(headText);
                    int length = ContentLength(headText);
                    while (pending.WrittenCount < head.Length + length)
                    {
                        int count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                        if (count == 0) return;
                        pending.Write(chunk.AsSpan(0, count));
                    }

                    byte[] body = pending.WrittenSpan.Slice(head.Length, length).ToArray();
                    byte[] rest = pending.WrittenSpan[(head.Length + length)..].ToArray();
                    pending.Clear();
                    pending.Write(rest);
                    server.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: head.Length, status: "request-head",
                        resourceName: Sha256(head));
                    server.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: body.Length, status: "request-body",
                        resourceName: Sha256(body));

                    byte[] responseBody = Body(options.Seed, index, options.MaximumBodyBytes, request: false);
                    byte[] responseHead = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {responseBody.Length}\r\n{FixtureHeader}\r\n"));
                    await stream.WriteAsync(responseHead, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(responseBody, cancellationToken).ConfigureAwait(false);
                    server.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: responseHead.Length, status: "response-head",
                        resourceName: Sha256(responseHead));
                    server.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: responseBody.Length, status: "response-body",
                        resourceName: Sha256(responseBody));
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client closed its connection, or the run ended; what was exchanged is already logged.
            }
        }
    }

    private static int RequestIndex(string head)
    {
        int start = head.IndexOf("/fx/", StringComparison.Ordinal) + 4;
        int end = head.IndexOf(' ', start);
        return int.Parse(head.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static int ContentLength(string head)
    {
        foreach (string line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(line.AsSpan("Content-Length:".Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }

    /// <summary>
    /// The seeded body of request or response <paramref name="index"/>: the first request's empty and the second's as long
    /// as allowed, the first response as long as allowed and the second's empty, and after them seeded lengths of text and
    /// bytes in turn.
    /// </summary>
    private static byte[] Body(int seed, int index, int maximumBytes, bool request)
    {
        var random = new Random(unchecked((seed * 397) ^ (index * 31) ^ (request ? 0x5EED : 0xB0D1)));
        int length = (index, request) switch
        {
            (0, true) or (1, false) => 0,
            (1, true) or (0, false) => maximumBytes,
            _ => random.Next(1, Math.Max(2, maximumBytes / 6)),
        };
        byte[] body = new byte[length];
        if (index % 2 == 0)
        {
            random.NextBytes(body);
            return body;
        }

        ReadOnlySpan<byte> text = "InterCat FX-HTTP-001 fixture body. The quick brown fox jumps over the lazy dog. "u8;
        for (int at = 0; at < length; at += text.Length)
        {
            text[..Math.Min(text.Length, length - at)].CopyTo(body.AsSpan(at));
        }

        return body;
    }

    private static string Sha256(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
