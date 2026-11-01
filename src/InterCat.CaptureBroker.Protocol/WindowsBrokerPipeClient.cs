using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace InterCat.CaptureBroker;

/// <summary>The connected pipe is not served by the broker process the client launched.</summary>
public sealed class BrokerServerIdentityException(string message) : Exception(message);

/// <summary>
/// The client half of broker authentication (contract §5.5). The broker checks the client's token; this checks the
/// broker. First-instance creation only stops the broker from joining a squatter's pipe, so a process that claimed the
/// name first would otherwise receive the client's requests. The client keeps the handle of the broker it launched and
/// refuses any pipe whose server is a different process, before a single byte is written.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsBrokerPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream stream;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>The exchange of a request whose caller stopped waiting for its answer, until the next request waits it out.</summary>
    private Task<BrokerWireFrame?>? abandoned;

    private WindowsBrokerPipeClient(NamedPipeClientStream stream, uint serverProcessId)
    {
        this.stream = stream;
        ServerProcessId = serverProcessId;
    }

    public uint ServerProcessId { get; }

    /// <param name="pipeName">The pipe name without the <c>\\.\pipe\</c> prefix.</param>
    /// <param name="expectedServerProcessId">The broker process the client launched and still holds a handle to.</param>
    public static async Task<WindowsBrokerPipeClient> ConnectAsync(
        string pipeName,
        int expectedServerProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedServerProcessId);
        var stream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        try
        {
            await stream.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(stream.SafePipeHandle, out uint serverProcessId))
            {
                throw new BrokerServerIdentityException(
                    $"The broker pipe's server process could not be read (error {Marshal.GetLastPInvokeError()}); "
                    + "the connection was closed without sending anything.");
            }

            if (serverProcessId != (uint)expectedServerProcessId)
            {
                throw new BrokerServerIdentityException(
                    $"The broker pipe is served by process {serverProcessId}, not by the broker InterCat started "
                    + $"(process {expectedServerProcessId}). Another program may be impersonating the broker; "
                    + "nothing was sent to it.");
            }

            return new(stream, serverProcessId);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends one request and returns its correlated response. Requests on one client are serialized. A caller whose token
    /// is cancelled stops waiting, but its request and answer still cross the pipe whole, and the next request is sent once
    /// that answer is read: a frame cut short, or an answer left unread, would be what the next request reads.
    /// </summary>
    public async Task<BrokerWireResponse> SendAsync(
        BrokerWireRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (abandoned is { } owed)
            {
                try
                {
                    _ = await owed.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (owed.IsCompleted)
                {
                    // How it ended was for the caller that stopped waiting; this request meets the pipe as it was left.
                }

                abandoned = null;
            }

            // A request whose caller has already stopped waiting is not sent at all.
            cancellationToken.ThrowIfCancellationRequested();
            Guid correlationId = Guid.NewGuid();
            Task<BrokerWireFrame?> exchange = ExchangeAsync(BrokerWireRequestCodec.Encode(request, correlationId));
            BrokerWireFrame? response;
            try
            {
                response = await exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!exchange.IsCompleted)
            {
                abandoned = exchange;
                throw;
            }

            if (response is null)
            {
                throw new IOException("The broker closed the connection without answering.");
            }

            if (response.CorrelationId != correlationId)
            {
                throw new InvalidDataException("The broker answered a different request.");
            }

            return BrokerWireResponseCodec.Decode(response);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);

        // An exchange no caller waits for fails with the pipe, and nobody is left to hear how; disposing never waits for it.
        _ = abandoned?.ContinueWith(static exchange => exchange.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        gate.Dispose();
    }

    /// <summary>One request and its answer, each frame whole: a caller's cancellation never cuts either short.</summary>
    private async Task<BrokerWireFrame?> ExchangeAsync(BrokerWireFrame request)
    {
        await BrokerWireFrameCodec.WriteAsync(stream, request, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return await BrokerWireFrameCodec.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
