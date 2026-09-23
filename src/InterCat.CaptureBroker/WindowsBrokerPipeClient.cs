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

    /// <summary>Sends one request and returns its correlated response. Requests on one client are serialized.</summary>
    public async Task<BrokerWireResponse> SendAsync(
        BrokerWireRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid correlationId = Guid.NewGuid();
            await BrokerWireFrameCodec
                .WriteAsync(stream, BrokerWireRequestCodec.Encode(request, correlationId), cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            BrokerWireFrame response = await BrokerWireFrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The broker closed the connection without answering.");
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
        gate.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
