using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using InterCat.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one seeded WinINet exchange.</summary>
internal sealed record HttpWinInetOptions
{
    public const string ScenarioId = "FX-HTTP-001";

    /// <summary>The same exchange over TLS: FX-HTTP-002.</summary>
    public const string TlsScenarioId = "FX-HTTP-002";

    /// <summary>Several exchanges at once in one process, or chunked responses: FX-HTTP-003.</summary>
    public const string InterleavedScenarioId = "FX-HTTP-003";

    /// <summary>Responses in the gzip content coding, which the client reads coded or has WinINet decode: FX-HTTP-004.</summary>
    public const string CodedScenarioId = "FX-HTTP-004";

    /// <summary>The exchange made through WinINet asynchronously, each send and read completing on WinINet's threads: FX-HTTP-005.</summary>
    public const string AsyncScenarioId = "FX-HTTP-005";

    /// <summary>The exchange over HTTP/2, which the client enables and the server offers by ALPN over TLS: FX-HTTP-006.</summary>
    public const string Http2ScenarioId = "FX-HTTP-006";

    /// <summary>Each request body written in seeded pieces after its head is sent: FX-HTTP-007.</summary>
    public const string WrittenScenarioId = "FX-HTTP-007";

    /// <summary>Responses in the deflate or br content coding, which the client reads coded or has WinINet decode: FX-HTTP-008.</summary>
    public const string CodingsScenarioId = "FX-HTTP-008";

    /// <summary>Each request redirected once, which WinINet follows by itself: FX-HTTP-009.</summary>
    public const string RedirectScenarioId = "FX-HTTP-009";

    public required string TruthDirectory { get; init; }
    public int Seed { get; init; } = 20_260_929;
    public int Requests { get; init; } = 16;

    /// <summary>The longest body either way: past ETW's 64 KiB event limit, so how a source splits or cuts one shows.</summary>
    public int MaximumBodyBytes { get; init; } = 96 * 1024;

    /// <summary>Whether to wait for a line on standard input before the first request, so a capture enabled for this
    /// process is in place before it sends anything.</summary>
    public bool WaitForStart { get; init; }

    public int StartWaitSeconds { get; init; } = 30;

    /// <summary>Seconds to wait before the first request, so a capture started for this process once it runs is in place.</summary>
    public int StartAfterSeconds { get; init; }

    /// <summary>
    /// Whether the exchange runs over TLS: the loopback server presents a self-signed certificate made in memory for this
    /// run and installed nowhere, and the client accepts it for its own requests only.
    /// </summary>
    public bool Tls { get; init; }

    /// <summary>
    /// How many exchanges run at once: that many client threads, each on its own connection handle and taking every nth
    /// request, so their exchanges' buffers interleave in one process.
    /// </summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Whether the server answers with chunked transfer coding, in seeded chunk sizes, rather than a Content-Length.</summary>
    public bool Chunked { get; init; }

    /// <summary>
    /// The content coding the server codes every response body in (RFC 9110 §8.4), declaring it by Content-Encoding, and which
    /// the client says it accepts: gzip (FX-HTTP-004), deflate - the zlib format RFC 9110 §8.4.1.2 names - or br, Brotli
    /// (FX-HTTP-008); null sends each body as it is.
    /// </summary>
    public string? Coding { get; init; }

    /// <summary>
    /// Whether the client has WinINet decode a coded response before it reads it (INTERNET_OPTION_HTTP_DECODING), so what it
    /// reads is the body before its coding; without it, it reads the coded bytes as they came.
    /// </summary>
    public bool Decode { get; init; }

    /// <summary>
    /// Whether the client works WinINet asynchronously (INTERNET_FLAG_ASYNC), as a client that must not block does: each send
    /// and each read is begun on the client's thread and, when WinINet cannot finish it at once, completes on one of
    /// WinINet's own, which its status callback tells.
    /// </summary>
    public bool Async { get; init; }

    /// <summary>
    /// Milliseconds the server waits before each slice of a response body it writes, 16 KiB at most, once the head is sent;
    /// 0 writes the body at once. Paced, a read finds no byte waiting and blocks, or, asynchronously, pends.
    /// </summary>
    public int PaceMilliseconds { get; init; }

    /// <summary>
    /// Whether the exchange runs over HTTP/2 (RFC 9113): the client enables it on its WinINet session
    /// (INTERNET_OPTION_ENABLE_HTTP_PROTOCOL), and the server - Kestrel, which frames HTTP/2 itself - offers it and HTTP/1.1
    /// by ALPN over TLS, so the run is over TLS too. HTTP/2 has no chunked transfer coding, so --chunked is refused with it.
    /// </summary>
    public bool Http2 { get; init; }

    /// <summary>
    /// The most bytes the client writes of a request body at once, where it writes the body in pieces: it sends the head
    /// with HttpSendRequestEx, declaring the body's length, writes the body with InternetWriteFile in seeded pieces of 1 to
    /// this many bytes, and ends the request with HttpEndRequest. 0 hands HttpSendRequest the body whole, as before.
    /// </summary>
    public int WriteBytes { get; init; }

    /// <summary>
    /// The status the server first answers each request with, a redirect of no body to the request's path moved, which
    /// WinINet follows by itself with a second request - 301, 302, 303, 307 or 308; 0 answers each request at once.
    /// </summary>
    public int RedirectStatus { get; init; }

    /// <summary>
    /// The scenario this run is: FX-HTTP-009 with each request redirected once, else FX-HTTP-008 with responses in deflate
    /// or br, else FX-HTTP-007 with bodies written in pieces,
    /// else FX-HTTP-006 over HTTP/2, else FX-HTTP-005 made asynchronously, else FX-HTTP-004 with responses in gzip, else
    /// FX-HTTP-003 with several clients at once or chunked responses, else FX-HTTP-002 over TLS, else FX-HTTP-001.
    /// </summary>
    public string Scenario => RedirectStatus > 0 ? RedirectScenarioId : Coding is not (null or "gzip") ? CodingsScenarioId
        : WriteBytes > 0 ? WrittenScenarioId
        : Http2 ? Http2ScenarioId : Async ? AsyncScenarioId : Coding is "gzip" ? CodedScenarioId
        : Concurrency > 1 || Chunked ? InterleavedScenarioId : Tls ? TlsScenarioId : ScenarioId;

    /// <summary>
    /// The content codings the workload writes: deflate-raw is a body its head declares deflate in, written as deflate data
    /// without the zlib wrapper RFC 9110 asks for, as servers that read RFC 1951 for deflate write one.
    /// </summary>
    public static IReadOnlyList<string> Codings { get; } = ["gzip", "deflate", "deflate-raw", "br"];

    /// <summary>The coding a head names for <see cref="Coding"/>: deflate for raw deflate data too.</summary>
    public string? DeclaredCoding => Coding is "deflate-raw" ? "deflate" : Coding;
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
    private static string FixtureHeader(HttpWinInetOptions options) => $"X-InterCat-Fixture: {options.Scenario}\r\n";

    // What the client's request adds to the fixture's header: that it accepts gzip, when the server codes its responses.
    private static string RequestHeaders(HttpWinInetOptions options) =>
        FixtureHeader(options) + (options.DeclaredCoding is { } coding ? $"Accept-Encoding: {coding}\r\n" : string.Empty);

    // Over TLS: a secure request whose certificate errors - an unknown issuer, a name, dates, revocation - WinINet is told to
    // accept for this request alone, so a self-signed certificate installed nowhere serves the loopback server.
    private const uint SecureRequestFlags = 0x00800000 | 0x00001000 | 0x00002000;
    private const int OptionSecurityFlags = 31;

    // A process-wide WinINet option, set for this process alone: how many connections one server may have at once, raised
    // to the run's concurrency so its clients' exchanges really overlap.
    private const int OptionMaxConnectionsPerServer = 73;

    // A request's own option: WinINet decodes a gzip or deflate response before the client reads it.
    private const int OptionHttpDecoding = 65;

    // An asynchronous session (INTERNET_FLAG_ASYNC): an operation on its handles that WinINet cannot finish at once fails
    // with ERROR_IO_PENDING and completes later, which the status callback tells as INTERNET_STATUS_REQUEST_COMPLETE with an
    // INTERNET_ASYNC_RESULT - the operation's result, then its error.
    private const int FlagAsync = 0x10000000;
    private const uint StatusRequestComplete = 100;
    private const int ErrorIoPending = 997;
    private const int ErrorTimeout = 1460;
    private const int ErrorRedirectNeedsConfirmation = 12168;
    private static readonly IntPtr InvalidStatusCallback = -1;
    private const uint IgnoreCertificateErrors = 0x00000100 | 0x00001000 | 0x00002000 | 0x00000080 | 0x00000200;
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

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(IntPtr handle, int option, ref uint buffer, int length);

    [LibraryImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetQueryOption(IntPtr handle, int option, ref uint buffer, ref int length);

    [LibraryImport("wininet.dll", EntryPoint = "InternetCloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetCloseHandle(IntPtr handle);

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetStatusCallbackW", SetLastError = true)]
    private static partial IntPtr InternetSetStatusCallback(IntPtr internet, IntPtr callback);

    // The same send and read with their bytes in memory that stays where it is until the operation completes, which an
    // asynchronous one may do after the call returns: WinINet reads the headers and body it sends, and writes the bytes it
    // read and their count, as it gets to them.
    [LibraryImport("wininet.dll", EntryPoint = "HttpSendRequestW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BeginHttpSendRequest(IntPtr request, IntPtr headers, int headersLength, IntPtr optional, int optionalLength);

    [LibraryImport("wininet.dll", EntryPoint = "InternetReadFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BeginInternetReadFile(IntPtr file, IntPtr buffer, int toRead, IntPtr read);

    // A request whose body is written after its head: the head sent with HttpSendRequestEx, which an INTERNET_BUFFERS
    // tells the headers and the body's whole length; the body with InternetWriteFile; the request ended with HttpEndRequest.
    [LibraryImport("wininet.dll", EntryPoint = "HttpSendRequestExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HttpSendRequestEx(IntPtr request, IntPtr buffersIn, IntPtr buffersOut, int flags, IntPtr context);

    [LibraryImport("wininet.dll", EntryPoint = "InternetWriteFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetWriteFile(IntPtr file, IntPtr buffer, int toWrite, IntPtr written);

    [LibraryImport("wininet.dll", EntryPoint = "HttpEndRequestW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HttpEndRequest(IntPtr request, IntPtr buffersOut, int flags, IntPtr context);

    /// <summary>INTERNET_BUFFERSW, as HttpSendRequestEx reads it: the headers to add and the length of the body to come.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InternetBuffers
    {
        public int StructSize;
        public IntPtr Next;
        public IntPtr Header;
        public int HeadersLength;
        public int HeadersTotal;
        public IntPtr Buffer;
        public int BufferLength;
        public int BufferTotal;
        public int OffsetLow;
        public int OffsetHigh;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static partial uint GetCurrentThreadId();

    // Each asynchronous client's completion, under the context its handles carry: WinINet's callback finds it there.
    private static readonly ConcurrentDictionary<nint, Completion> Completions = new();

    // How an asynchronous run's sends, writes and reads went: finished at once, or pending and completed by the callback.
    private static int sendsAtOnce, sendsPending, readsAtOnce, readsPending, writesAtOnce, writesPending;

    // HTTP/2 on a session (INTERNET_OPTION_ENABLE_HTTP_PROTOCOL, HTTP_PROTOCOL_FLAG_HTTP2), and which protocol a request
    // was made over once sent (INTERNET_OPTION_HTTP_PROTOCOL_USED); how many of the run's were HTTP/2.
    private const int OptionEnableHttpProtocol = 148;
    private const int OptionHttpProtocolUsed = 149;
    private const uint ProtocolFlagHttp2 = 0x2;
    private static int requestsOverHttp2;

    // The most a paced response writes at a time.
    private const int PacedSliceBytes = 16 * 1024;

    public static async Task<int> RunAsync(HttpWinInetOptions options, CancellationToken cancellationToken)
    {
        if (options.Coding is { } coding && !HttpWinInetOptions.Codings.Contains(coding, StringComparer.Ordinal))
        {
            await Console.Error.WriteLineAsync($"--coding takes {string.Join(", ", HttpWinInetOptions.Codings)}; '{coding}' is none of them.")
                .ConfigureAwait(false);
            return 2;
        }

        if (options.RedirectStatus != 0 && (options.RedirectStatus is not (301 or 302 or 303 or 307 or 308) || options.Http2))
        {
            await Console.Error.WriteLineAsync("--redirect takes 301, 302, 303, 307 or 308, and its server is HTTP/1.1's, so it is refused with --http2.")
                .ConfigureAwait(false);
            return 2;
        }

        if (options.Http2 && options.Chunked)
        {
            await Console.Error.WriteLineAsync("HTTP/2 frames a body itself and has no chunked transfer coding; --chunked is refused with --http2.")
                .ConfigureAwait(false);
            return 2;
        }

        Directory.CreateDirectory(options.TruthDirectory);

        // HTTP/1.1 is served by this workload's own loopback server, which logs every byte of every message; HTTP/2 by
        // Kestrel, which frames it, started once the server's truth log is open.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        if (!options.Http2) listener.Start();
        int port = options.Http2 ? 0 : ((IPEndPoint)listener.LocalEndpoint).Port;
        int completed;
        long sentBytes = 0, readBytes = 0;
        var clientThreads = new ConcurrentBag<uint>();
        using X509Certificate2? certificate = options.Tls ? LoopbackCertificate() : null;
        await using (var server = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-http-server.jsonl"), options.Scenario, "server"))
        await using (var client = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-http-client.jsonl"), options.Scenario, "client"))
        {
            await using WebApplication? kestrel = options.Http2
                ? await Http2ServerAsync(server, options, certificate!, cancellationToken).ConfigureAwait(false)
                : null;
            if (kestrel is not null) port = new Uri(kestrel.Urls.First()).Port;
            server.Write(TruthEventKind.ListenerBound, localPort: port);
            client.Write(TruthEventKind.ProcessStarted);
            if (options.WaitForStart && !await StartedAsync(options.StartWaitSeconds, cancellationToken).ConfigureAwait(false))
            {
                await Console.Error.WriteLineAsync("No start line arrived within the wait; no request was made.")
                    .ConfigureAwait(false);
                return 3;
            }

            if (options.StartAfterSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.StartAfterSeconds), cancellationToken).ConfigureAwait(false);
            }

            if (kestrel is not null)
            {
                (completed, sentBytes, readBytes) = await Task.Factory.StartNew(
                    () => Exchange(port, client, options, clientThreads, cancellationToken), cancellationToken,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
                await kestrel.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var serving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task served = ServeAsync(listener, server, options, certificate, serving.Token);
                (completed, sentBytes, readBytes) = await Task.Factory.StartNew(
                    () => Exchange(port, client, options, clientThreads, cancellationToken), cancellationToken,
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
            }

            client.Write(TruthEventKind.ProcessExiting);
        }

        var summary = new
        {
            scenarioId = options.Scenario,
            options.Tls,
            options.Concurrency,
            options.Chunked,
            options.Coding,
            options.Decode,
            options.Async,
            options.PaceMilliseconds,
            options.Http2,
            requestsOverHttp2 = options.Http2 ? requestsOverHttp2 : (int?)null,
            options.WriteBytes,
            options.RedirectStatus,
            asynchronous = options.Async ? new { sendsAtOnce, sendsPending, writesAtOnce, writesPending, readsAtOnce, readsPending } : null,
            processId = Environment.ProcessId,
            clientThreads = clientThreads.Order().ToArray(),
            port,
            options.Seed,
            options.Requests,
            completed,
            sentBytes,
            readBytes,
            options.MaximumBodyBytes,
            note = "The server's truth log names every head and body it received and sent by length and SHA-256; "
                + "the client's names what it sent and read. Neither holds a byte of them. The client threads are the "
                + "threads that made the requests, by their Windows thread IDs.",
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
        ConcurrentBag<uint> clientThreads, CancellationToken cancellationToken)
    {
        IntPtr internet = InternetOpen("InterCat-" + options.Scenario, OpenTypeDirect, null, null, options.Async ? FlagAsync : 0);
        if (internet == IntPtr.Zero)
        {
            client.Write(TruthEventKind.Failure, status: $"InternetOpen failed: {Marshal.GetLastPInvokeError()}");
            return (0, 0, 0);
        }

        try
        {
            // An asynchronous session tells every handle descended from it how its operations go through one callback.
            if (options.Async && InternetSetStatusCallback(internet, StatusCallback()) == InvalidStatusCallback)
            {
                client.Write(TruthEventKind.Failure, status: $"InternetSetStatusCallback failed: {Marshal.GetLastPInvokeError()}");
                return (0, 0, 0);
            }

            // HTTP/2 is the session's to enable: WinINet offers it by ALPN to a server over TLS, and speaks HTTP/1.1 otherwise.
            uint protocols = ProtocolFlagHttp2;
            if (options.Http2 && !InternetSetOption(internet, OptionEnableHttpProtocol, ref protocols, sizeof(uint)))
            {
                client.Write(TruthEventKind.Failure, status: $"InternetSetOption failed: {Marshal.GetLastPInvokeError()}");
                return (0, 0, 0);
            }

            uint connections = (uint)Math.Max(2, options.Concurrency);
            if (options.Concurrency > 1 && !InternetSetOption(IntPtr.Zero, OptionMaxConnectionsPerServer, ref connections, sizeof(uint)))
            {
                client.Write(TruthEventKind.Failure, status: $"InternetSetOption failed: {Marshal.GetLastPInvokeError()}");
            }

            // One client per thread, each taking every nth request: with one, the requests go in order on one connection.
            Task<(int Completed, long Sent, long Read)>[] clients =
            [
                .. Enumerable.Range(0, Math.Max(1, options.Concurrency)).Select(worker => Task.Factory.StartNew(
                    () => Requests(internet, port, client, options, worker, clientThreads, cancellationToken), cancellationToken,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default)),
            ];
            (int Completed, long Sent, long Read)[] results = Task.WhenAll(clients).GetAwaiter().GetResult();
            return (results.Sum(result => result.Completed), results.Sum(result => result.Sent), results.Sum(result => result.Read));
        }
        finally
        {
            if (options.Async) InternetSetStatusCallback(internet, IntPtr.Zero);
            InternetCloseHandle(internet);
        }
    }

    private static unsafe IntPtr StatusCallback() =>
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, nint, uint, IntPtr, uint, void>)&StatusChanged;

    /// <summary>
    /// WinINet's status callback, on whichever thread WinINet is working a request on: a completed operation ends its
    /// client's wait; every other step a request reports - resolving, connecting, sending, receiving, closing - is passed by.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void StatusChanged(IntPtr handle, nint context, uint status, IntPtr information, uint informationLength)
    {
        if (status == StatusRequestComplete && informationLength >= IntPtr.Size + sizeof(int)
            && Completions.TryGetValue(context, out Completion? completion))
        {
            completion.Complete(Marshal.ReadIntPtr(information), Marshal.ReadInt32(information, IntPtr.Size));
        }
    }

    /// <summary>
    /// One client's requests - every nth from its own - on its own connection handle; asynchronously, each send and read
    /// waits for the completion WinINet's callback reports under the context its handles carry.
    /// </summary>
    private static (int Completed, long Sent, long Read) Requests(IntPtr internet, int port, TruthLog client,
        HttpWinInetOptions options, int worker, ConcurrentBag<uint> clientThreads, CancellationToken cancellationToken)
    {
        int completed = 0;
        long sent = 0, read = 0;
        IntPtr connect = IntPtr.Zero;
        clientThreads.Add(GetCurrentThreadId());

        // A synchronous client's handles carry no context, as before; an asynchronous one's name its completion.
        nint context = options.Async ? worker + 1 : 0;
        using AsyncCalls? calls = options.Async ? new AsyncCalls(context) : null;
        try
        {
            connect = InternetConnect(internet, "127.0.0.1", (ushort)port, null, null, ServiceHttp, 0, context);
            if (connect == IntPtr.Zero)
            {
                client.Write(TruthEventKind.Failure, status: $"InternetConnect failed: {Marshal.GetLastPInvokeError()}");
                return (0, 0, 0);
            }

            byte[] buffer = new byte[16 * 1024];
            for (int index = worker; index < options.Requests; index += Math.Max(1, options.Concurrency))
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] body = Body(options.Seed, index, options.MaximumBodyBytes, request: true);
                IntPtr request = HttpOpenRequest(connect, "POST", string.Create(CultureInfo.InvariantCulture, $"/fx/{index}"),
                    "HTTP/1.1", null, IntPtr.Zero, RequestFlags | (options.Tls ? SecureRequestFlags : 0), context);
                if (request == IntPtr.Zero)
                {
                    client.Write(TruthEventKind.Failure, callId: index, status: $"HttpOpenRequest failed: {Marshal.GetLastPInvokeError()}");
                    continue;
                }

                try
                {
                    uint ignore = IgnoreCertificateErrors;
                    if (options.Tls && !InternetSetOption(request, OptionSecurityFlags, ref ignore, sizeof(uint)))
                    {
                        client.Write(TruthEventKind.Failure, callId: index, status: $"InternetSetOption failed: {Marshal.GetLastPInvokeError()}");
                        continue;
                    }

                    uint decode = 1;
                    if (options.Decode && !InternetSetOption(request, OptionHttpDecoding, ref decode, sizeof(uint)))
                    {
                        client.Write(TruthEventKind.Failure, callId: index, status: $"InternetSetOption failed: {Marshal.GetLastPInvokeError()}");
                        continue;
                    }

                    client.Write(TruthEventKind.CallIssued, callId: index, declaredBytes: body.Length, status: "request-body",
                        resourceName: Sha256(body));

                    // Written in pieces, each piece the client writes is logged in order, by length and SHA-256.
                    int[] pieces = options.WriteBytes > 0 ? Pieces(body.Length, options.Seed, index, options.WriteBytes) : [];
                    for (int piece = 0, at = 0; piece < pieces.Length; at += pieces[piece], piece++)
                    {
                        client.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: pieces[piece], status: "request-body-write",
                            resourceName: "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body.AsSpan(at, pieces[piece]))));
                    }

                    bool requestSent = options.WriteBytes > 0
                        ? SendWritten(calls, request, context, RequestHeaders(options), body, pieces, out int sendError)
                        : Send(calls, request, RequestHeaders(options), body, out sendError);

                    // A redirect that keeps a POST and its body WinINet follows only once confirmed, which its dialog would ask
                    // a person: the send fails with ERROR_HTTP_REDIRECT_NEEDS_CONFIRMATION. The client confirms it as that
                    // dialog's caller does, by sending the request again, which WinINet makes to the redirect's location.
                    if (!requestSent && sendError == ErrorRedirectNeedsConfirmation && options.RedirectStatus > 0 && options.WriteBytes == 0)
                    {
                        client.Write(TruthEventKind.MessageReceived, callId: index, status: "redirect-needs-confirmation");
                        requestSent = Send(calls, request, RequestHeaders(options), body, out sendError);
                    }

                    if (!requestSent)
                    {
                        client.Write(TruthEventKind.Failure, callId: index, status: $"HttpSendRequest failed: {sendError}");
                        if (calls?.Abandoned == true) break;
                        continue;
                    }

                    sent += body.Length;

                    // Which protocol WinINet made the request over, once it was sent: HTTP/2 only where the server agreed to it.
                    if (options.Http2)
                    {
                        uint used = 0;
                        int length = sizeof(uint);
                        bool known = InternetQueryOption(request, OptionHttpProtocolUsed, ref used, ref length);
                        bool http2 = known && (used & ProtocolFlagHttp2) != 0;
                        if (http2) Interlocked.Increment(ref requestsOverHttp2);
                        client.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: known ? used : -1, status: "protocol-used",
                            resourceName: !known ? "unknown" : http2 ? "HTTP/2" : "HTTP/1.1");
                    }

                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long total = 0;
                    int readError;
                    bool ended = false;
                    while (Read(calls, request, buffer, out int count, out readError))
                    {
                        if (count == 0)
                        {
                            ended = true;
                            break;
                        }

                        hash.AppendData(buffer, 0, count);
                        total += count;
                    }

                    if (!ended)
                    {
                        client.Write(TruthEventKind.Failure, callId: index, status: $"InternetReadFile failed: {readError}");
                        if (calls?.Abandoned == true) break;
                        continue;
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
        }

        return (completed, sent, read);
    }

    /// <summary>Sends a request's head and body, at once or, asynchronously, once WinINet completes it; false with its error.</summary>
    private static bool Send(AsyncCalls? calls, IntPtr request, string headers, byte[] body, out int error)
    {
        if (calls is not null) return calls.Send(request, headers, body, out error);
        bool sent = HttpSendRequest(request, headers, -1, body, body.Length);
        error = sent ? 0 : Marshal.GetLastPInvokeError();
        return sent;
    }

    /// <summary>
    /// The seeded lengths a request body of <paramref name="length"/> bytes is written in: pieces of 1 to
    /// <paramref name="most"/> bytes in turn until the body is written, none for an empty one.
    /// </summary>
    private static int[] Pieces(int length, int seed, int index, int most)
    {
        var random = new Random(unchecked((seed * 7919) ^ (index * 13) ^ 0x51CE));
        var pieces = new List<int>();
        for (int left = length; left > 0;)
        {
            int piece = Math.Min(left, random.Next(1, most + 1));
            pieces.Add(piece);
            left -= piece;
        }

        return [.. pieces];
    }

    /// <summary>
    /// Sends a request with its body written after its head: the head with HttpSendRequestEx, declaring the body's whole
    /// length, each piece with InternetWriteFile, then HttpEndRequest - each step at once, or asynchronously once WinINet
    /// completes it - every byte from memory that stays where it is until then; false with the failed step's error.
    /// </summary>
    private static bool SendWritten(AsyncCalls? calls, IntPtr request, nint context, string headers, byte[] body, int[] pieces,
        out int error)
    {
        int size = Marshal.SizeOf<InternetBuffers>();
        IntPtr sentHeaders = Marshal.StringToHGlobalUni(headers);
        IntPtr buffers = Marshal.AllocHGlobal(size);
        IntPtr piece = Marshal.AllocHGlobal(Math.Max(1, pieces.Length == 0 ? 1 : pieces.Max()));
        IntPtr written = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.StructureToPtr(new InternetBuffers
            {
                StructSize = size,
                Header = sentHeaders,
                HeadersLength = headers.Length,
                HeadersTotal = headers.Length,
                BufferTotal = body.Length,
            }, buffers, fDeleteOld: false);
            if (!Step(calls, () => HttpSendRequestEx(request, buffers, IntPtr.Zero, 0, context), sending: true, out error)) return false;
            for (int index = 0, at = 0; index < pieces.Length; at += pieces[index], index++)
            {
                int length = pieces[index];
                Marshal.Copy(body, at, piece, length);
                Marshal.WriteInt32(written, 0);
                if (!Step(calls, () => InternetWriteFile(request, piece, length, written), sending: false, out error)) return false;
                if (Marshal.ReadInt32(written) != length)
                {
                    // WinINet wrote less than it was given, which no measured client met; the request is not sent whole.
                    error = -Marshal.ReadInt32(written) - 1;
                    return false;
                }
            }

            return Step(calls, () => HttpEndRequest(request, IntPtr.Zero, 0, context), sending: true, out error);
        }
        finally
        {
            if (calls?.Abandoned != true)
            {
                Marshal.FreeHGlobal(sentHeaders);
                Marshal.FreeHGlobal(buffers);
                Marshal.FreeHGlobal(piece);
                Marshal.FreeHGlobal(written);
            }
        }
    }

    /// <summary>One step of a written request, at once, or asynchronously once WinINet's callback completes it.</summary>
    private static bool Step(AsyncCalls? calls, Func<bool> begin, bool sending, out int error)
    {
        if (calls is not null) return calls.Step(begin, sending, out error);
        bool done = begin();
        error = done ? 0 : Marshal.GetLastPInvokeError();
        return done;
    }

    /// <summary>Reads the next bytes of a response's body, none at its end; false with WinINet's error.</summary>
    private static bool Read(AsyncCalls? calls, IntPtr request, byte[] buffer, out int count, out int error)
    {
        if (calls is not null) return calls.Read(request, buffer, out count, out error);
        bool read = InternetReadFile(request, buffer, buffer.Length, out count);
        error = read ? 0 : Marshal.GetLastPInvokeError();
        return read;
    }

    /// <summary>
    /// One asynchronous client's calls, one at a time: each begun with its bytes in memory of its own, which stays where it
    /// is until WinINet is done with it, and, when WinINet cannot finish it at once, waited for until its callback reports
    /// it complete under this client's context. A call not completed within a minute abandons the client: WinINet may still
    /// complete it, into memory that is then kept for it.
    /// </summary>
    private sealed class AsyncCalls : IDisposable
    {
        private const int ReadBytes = 16 * 1024;
        private readonly nint context;
        private readonly Completion completion = new();
        private readonly IntPtr readBuffer = Marshal.AllocHGlobal(ReadBytes);
        private readonly IntPtr readCount = Marshal.AllocHGlobal(sizeof(int));

        public AsyncCalls(nint context)
        {
            this.context = context;
            Completions[context] = completion;
        }

        public bool Abandoned { get; private set; }

        public bool Send(IntPtr request, string headers, byte[] body, out int error)
        {
            IntPtr sentHeaders = Marshal.StringToHGlobalUni(headers);
            IntPtr sentBody = Marshal.AllocHGlobal(Math.Max(1, body.Length));
            Marshal.Copy(body, 0, sentBody, body.Length);
            completion.Begin();
            bool sent = Finished(BeginHttpSendRequest(request, sentHeaders, -1, sentBody, body.Length), out error,
                ref sendsAtOnce, ref sendsPending);
            if (!Abandoned)
            {
                Marshal.FreeHGlobal(sentHeaders);
                Marshal.FreeHGlobal(sentBody);
            }

            return sent;
        }

        /// <summary>A step of a written request: the head sent, a piece written, or the request ended.</summary>
        public bool Step(Func<bool> begin, bool sending, out int error)
        {
            completion.Begin();
            return sending
                ? Finished(begin(), out error, ref sendsAtOnce, ref sendsPending)
                : Finished(begin(), out error, ref writesAtOnce, ref writesPending);
        }

        public bool Read(IntPtr request, byte[] into, out int count, out int error)
        {
            count = 0;
            Marshal.WriteInt32(readCount, 0);
            completion.Begin();
            if (!Finished(BeginInternetReadFile(request, readBuffer, Math.Min(into.Length, ReadBytes), readCount), out error,
                ref readsAtOnce, ref readsPending))
            {
                return false;
            }

            // The count is WinINet's, written once the read completed, at once or later.
            count = Marshal.ReadInt32(readCount);
            Marshal.Copy(readBuffer, into, 0, count);
            return true;
        }

        /// <summary>
        /// Whether a call just made has completed and succeeded: at once, or once its callback said so, counted either way.
        /// </summary>
        private bool Finished(bool returned, out int error, ref int atOnce, ref int pending)
        {
            if (returned)
            {
                Interlocked.Increment(ref atOnce);
                error = 0;
                return true;
            }

            error = Marshal.GetLastPInvokeError();
            if (error != ErrorIoPending) return false;
            Interlocked.Increment(ref pending);
            if (completion.Wait(out error)) return true;
            Abandoned |= error == ErrorTimeout;
            return false;
        }

        public void Dispose()
        {
            if (Abandoned) return;
            Completions.TryRemove(context, out _);
            Marshal.FreeHGlobal(readBuffer);
            Marshal.FreeHGlobal(readCount);
            completion.Dispose();
        }
    }

    /// <summary>The completion of one operation at a time: reported by WinINet's callback, waited for by its client.</summary>
    private sealed class Completion : IDisposable
    {
        private readonly ManualResetEventSlim completed = new(initialState: false);
        private nint result;
        private int error;

        public void Begin() => completed.Reset();

        public void Complete(nint operationResult, int operationError)
        {
            result = operationResult;
            error = operationError;
            completed.Set();
        }

        /// <summary>Whether the operation completed within a minute and succeeded; its error otherwise.</summary>
        public bool Wait(out int operationError)
        {
            if (!completed.Wait(TimeSpan.FromMinutes(1)))
            {
                operationError = ErrorTimeout;
                return false;
            }

            operationError = result != 0 ? 0 : error;
            return result != 0;
        }

        public void Dispose() => completed.Dispose();
    }

    /// <summary>
    /// The server: each connection's requests read to their last byte and answered, every part logged as the wire held it,
    /// or over TLS as the server decrypted and encrypted it.
    /// </summary>
    private static async Task ServeAsync(TcpListener listener, TruthLog server, HttpWinInetOptions options,
        X509Certificate2? certificate, CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var remote = (IPEndPoint)connection.Client.RemoteEndPoint!;
            server.Write(TruthEventKind.ConnectionAccepted, localPort: ((IPEndPoint)listener.LocalEndpoint).Port, remotePort: remote.Port);
            connections.Add(ServeConnectionAsync(connection, server, options, certificate, cancellationToken));
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
    }

    private static async Task ServeConnectionAsync(TcpClient connection, TruthLog server, HttpWinInetOptions options,
        X509Certificate2? certificate, CancellationToken cancellationToken)
    {
        using (connection)
        {
            // Over TLS the server reads and writes the decrypted stream, so its truth is the bytes above the encryption.
            Stream stream = connection.GetStream();
            if (certificate is not null)
            {
                var secure = new SslStream(stream, leaveInnerStreamOpen: false);
                try
                {
                    await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false,
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is AuthenticationException or IOException)
                {
                    server.Write(TruthEventKind.Failure, status: "TLS handshake failed: " + exception.Message);
                    await secure.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                stream = secure;
            }

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

                    // Redirected, each request the client made is answered by the redirect alone, of no body, to its path
                    // moved, which WinINet follows by itself with a second request; each leg is logged apart.
                    if (options.RedirectStatus > 0 && !headText.Contains("/moved ", StringComparison.Ordinal))
                    {
                        server.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: head.Length,
                            status: "redirected-request-head", resourceName: Sha256(head));
                        server.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: body.Length,
                            status: "redirected-request-body", resourceName: Sha256(body));
                        byte[] redirect = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                            $"HTTP/1.1 {options.RedirectStatus} {Reason(options.RedirectStatus)}\r\nLocation: /fx/{index}/moved\r\nContent-Length: 0\r\n{FixtureHeader(options)}\r\n"));
                        await stream.WriteAsync(redirect, cancellationToken).ConfigureAwait(false);
                        server.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: redirect.Length,
                            status: "redirect-response-head", resourceName: Sha256(redirect));
                        server.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: 0, status: "redirect-response-body",
                            resourceName: Sha256([]));
                        continue;
                    }

                    server.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: head.Length, status: "request-head",
                        resourceName: Sha256(head));
                    server.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: body.Length, status: "request-body",
                        resourceName: Sha256(body));

                    // A chunked response frames its body in seeded chunks, and a coded one codes it first; the truth names the
                    // body before either, which a decoding client reads, and apart the coded bytes and the framed bytes the
                    // wire carried, so a capture can be matched with any of them.
                    byte[] responseBody = Body(options.Seed, index, options.MaximumBodyBytes, request: false);
                    byte[] coded = options.Coding is { } coding ? Code(responseBody, coding) : responseBody;
                    string framing = (options.DeclaredCoding is { } declared ? $"Content-Encoding: {declared}\r\n" : string.Empty) + (options.Chunked
                        ? "Transfer-Encoding: chunked"
                        : string.Create(CultureInfo.InvariantCulture, $"Content-Length: {coded.Length}"));
                    byte[] responseHead = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n{framing}\r\n{FixtureHeader(options)}\r\n"));
                    byte[] sentBody = options.Chunked ? Chunks(coded, options.Seed, index) : coded;
                    await stream.WriteAsync(responseHead, cancellationToken).ConfigureAwait(false);
                    if (options.PaceMilliseconds > 0)
                    {
                        for (int at = 0; at < sentBody.Length; at += PacedSliceBytes)
                        {
                            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                            await Task.Delay(options.PaceMilliseconds, cancellationToken).ConfigureAwait(false);
                            await stream.WriteAsync(sentBody.AsMemory(at, Math.Min(PacedSliceBytes, sentBody.Length - at)),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await stream.WriteAsync(sentBody, cancellationToken).ConfigureAwait(false);
                    }
                    server.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: responseHead.Length, status: "response-head",
                        resourceName: Sha256(responseHead));
                    server.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: responseBody.Length, status: "response-body",
                        resourceName: Sha256(responseBody));
                    if (options.Coding is not null)
                    {
                        server.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: coded.Length,
                            status: "response-body-coded", resourceName: Sha256(coded));
                    }

                    if (options.Chunked)
                    {
                        server.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: sentBody.Length,
                            status: "response-body-chunked", resourceName: Sha256(sentBody));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client closed its connection, or the run ended; what was exchanged is already logged.
            }
        }
    }

    /// <summary>
    /// The HTTP/2 server: Kestrel on loopback over TLS, offering HTTP/2 and HTTP/1.1 by ALPN, with the run's certificate.
    /// HTTP/2 frames every message and compresses its heads, so its truth is what Kestrel's handler read and wrote - each
    /// request body as taken from its DATA frames and each response body as written into them, and the names of each
    /// request's fields as Kestrel decoded them - never the frames, which TLS encrypts.
    /// </summary>
    private static async Task<WebApplication> Http2ServerAsync(TruthLog server, HttpWinInetOptions options, X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(certificate);
            });
        });
        WebApplication app = builder.Build();
        app.Run(context => AnswerAsync(context, server, options));
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return app;
    }

    /// <summary>One request read whole and answered as the loopback server answers it, each part logged as the handler held it.</summary>
    private static async Task AnswerAsync(HttpContext context, TruthLog server, HttpWinInetOptions options)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        int index = int.Parse(path.AsSpan("/fx/".Length), NumberStyles.None, CultureInfo.InvariantCulture);
        using var received = new MemoryStream();
        await context.Request.Body.CopyToAsync(received, context.RequestAborted).ConfigureAwait(false);
        byte[] body = received.ToArray();

        // The fields' names, never their values: the pseudo-headers HTTP/2 carries the request line in, then each header.
        string[] fields = [":method", ":scheme", ":authority", ":path", .. context.Request.Headers.Keys.Select(name => name.ToLowerInvariant())];
        server.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: 0, status: "request-protocol",
            resourceName: context.Request.Protocol);
        server.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: 0, status: "request-connection",
            resourceName: context.Connection.Id);
        server.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: fields.Length, status: "request-fields",
            resourceName: string.Join(',', fields));
        server.Write(TruthEventKind.MessageReceived, callId: index, declaredBytes: body.Length, status: "request-body",
            resourceName: Sha256(body));

        byte[] responseBody = Body(options.Seed, index, options.MaximumBodyBytes, request: false);
        byte[] coded = options.Coding is { } coding ? Code(responseBody, coding) : responseBody;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/octet-stream";
        if (options.DeclaredCoding is not null) context.Response.Headers.ContentEncoding = options.DeclaredCoding;
        context.Response.ContentLength = coded.Length;
        context.Response.Headers["X-InterCat-Fixture"] = options.Scenario;
        if (options.PaceMilliseconds > 0)
        {
            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            for (int at = 0; at < coded.Length; at += PacedSliceBytes)
            {
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                await Task.Delay(options.PaceMilliseconds, context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.WriteAsync(coded.AsMemory(at, Math.Min(PacedSliceBytes, coded.Length - at)),
                    context.RequestAborted).ConfigureAwait(false);
            }
        }
        else
        {
            await context.Response.Body.WriteAsync(coded, context.RequestAborted).ConfigureAwait(false);
        }

        server.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: responseBody.Length, status: "response-body",
            resourceName: Sha256(responseBody));
        if (options.Coding is not null)
        {
            server.Write(TruthEventKind.MessageSent, callId: index, declaredBytes: coded.Length, status: "response-body-coded",
                resourceName: Sha256(coded));
        }
    }

    /// <summary>The index a request's target names, /fx/{index}, or /fx/{index}/moved where a redirect sent it.</summary>
    private static int RequestIndex(string head)
    {
        int start = head.IndexOf("/fx/", StringComparison.Ordinal) + 4;
        int end = start;
        while (end < head.Length && char.IsAsciiDigit(head[end])) end++;
        return int.Parse(head.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>The reason phrase of a redirect's status.</summary>
    private static string Reason(int status) => status switch
    {
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a redirect the workload sends."),
    };

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

    /// <summary><paramref name="body"/> in the content coding <paramref name="coding"/> names, one of <see cref="HttpWinInetOptions.Codings"/>.</summary>
    private static byte[] Code(byte[] body, string coding) => coding switch
    {
        "gzip" => Gzip(body),
        "deflate" => Deflate(body),
        "deflate-raw" => RawDeflate(body),
        "br" => Brotli(body),
        _ => throw new ArgumentOutOfRangeException(nameof(coding), coding, "The workload writes no such content coding."),
    };

    /// <summary>
    /// <paramref name="body"/> in the deflate content coding: the zlib format (RFC 9110 §8.4.1.2, RFC 1950), its deflate data
    /// between a two-byte header and an Adler-32. An empty body is one zlib stream of an empty final block, written out, as
    /// for gzip, since .NET's stream may write nothing for one.
    /// </summary>
    private static byte[] Deflate(byte[] body)
    {
        if (body.Length == 0)
        {
            // The header (deflate, a 32 KiB window, default level), an empty final block of fixed codes, and Adler-32 of nothing.
            return [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];
        }

        using var coded = new MemoryStream();
        using (var zlib = new ZLibStream(coded, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(body);
        }

        return coded.ToArray();
    }

    /// <summary>
    /// <paramref name="body"/> as raw deflate data (RFC 1951), without the zlib wrapper: what a server that reads deflate as
    /// RFC 1951 sends under Content-Encoding: deflate. An empty body is one empty final block.
    /// </summary>
    private static byte[] RawDeflate(byte[] body)
    {
        if (body.Length == 0)
        {
            return [0x03, 0x00];
        }

        using var coded = new MemoryStream();
        using (var deflate = new DeflateStream(coded, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(body);
        }

        return coded.ToArray();
    }

    /// <summary>
    /// <paramref name="body"/> in the br content coding: Brotli (RFC 7932), at its best quality and a 4 MiB window, an empty
    /// body included, which Brotli writes as a stream of its own.
    /// </summary>
    private static byte[] Brotli(byte[] body)
    {
        byte[] coded = new byte[BrotliEncoder.GetMaxCompressedLength(body.Length)];
        if (!BrotliEncoder.TryCompress(body, coded, out int written, quality: 11, window: 22))
        {
            throw new InvalidOperationException("Brotli could not code a response body.");
        }

        return coded[..written];
    }

    /// <summary>
    /// <paramref name="body"/> in the gzip content coding (RFC 9110 §8.4.1.3). An empty body is one gzip member holding no
    /// data, as RFC 1952 writes it: .NET's stream writes nothing at all for one, which no gzip decoder reads as a member.
    /// </summary>
    private static byte[] Gzip(byte[] body)
    {
        if (body.Length == 0)
        {
            // The member's header, of no name or time and an unknown system; an empty final block; the CRC-32 and size of nothing.
            return [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 0xff, 0x03, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];
        }

        using var coded = new MemoryStream();
        using (var gzip = new GZipStream(coded, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(body);
        }

        return coded.ToArray();
    }

    /// <summary>
    /// <paramref name="body"/> in chunked transfer coding (RFC 9112 §7.1): seeded chunks of 1 to 9,000 bytes, each its size
    /// in hexadecimal, a line end, its bytes and a line end, then the last chunk, of size 0, and the empty trailer.
    /// </summary>
    private static byte[] Chunks(byte[] body, int seed, int index)
    {
        var random = new Random(unchecked((seed * 131) ^ (index * 17) ^ 0xC4A2));
        var framed = new ArrayBufferWriter<byte>(body.Length + 64);
        for (int at = 0; at < body.Length;)
        {
            int size = Math.Min(body.Length - at, random.Next(1, 9_001));
            framed.Write(Encoding.ASCII.GetBytes(size.ToString("x", CultureInfo.InvariantCulture) + "\r\n"));
            framed.Write(body.AsSpan(at, size));
            framed.Write("\r\n"u8);
            at += size;
        }

        framed.Write("0\r\n\r\n"u8);
        return framed.WrittenSpan.ToArray();
    }

    /// <summary>
    /// A certificate for 127.0.0.1, made in memory for one run: self-signed, valid for an hour, and installed in no store.
    /// SChannel cannot use a key that exists only in memory, so it is loaded again from its own PKCS #12 bytes into a
    /// temporary key container, which goes when the certificate is disposed.
    /// </summary>
    private static X509Certificate2 LoopbackCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using X509Certificate2 made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null);
    }
}
