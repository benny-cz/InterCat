using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InterCat.CaptureBroker;

public sealed record BrokerAuthenticatedPipeClient(
    BrokerClientIdentity Identity,
    uint DiagnosticProcessId);

/// <summary>
/// One first-instance, local-only broker pipe. Its DACL admits only SYSTEM and the configured user;
/// authorization still uses the impersonated token's SID and logon session rather than its PID.
/// </summary>
public sealed partial class WindowsBrokerPipeServerInstance : IDisposable, IAsyncDisposable
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint PipeUnlimitedWait = 0;
    private const int SddlRevision1 = 1;
    private const int BufferSize = BrokerWireFrameCodec.HeaderSize + BrokerWireFrameCodec.MaximumPayloadBytes;
    private readonly BrokerOwnerIdentity expectedOwner;
    private readonly NamedPipeServerStream stream;
    private bool disposed;

    private WindowsBrokerPipeServerInstance(
        Guid serverInstanceId,
        BrokerOwnerIdentity expectedOwner,
        string canonicalUserSid,
        string securityDescriptorSddl,
        NamedPipeServerStream stream)
    {
        ServerInstanceId = serverInstanceId;
        this.expectedOwner = expectedOwner with { UserSid = canonicalUserSid };
        SecurityDescriptorSddl = securityDescriptorSddl;
        this.stream = stream;
        PipeName = $"InterCat.Broker.v1.{serverInstanceId:N}";
    }

    public Guid ServerInstanceId { get; }
    public string PipeName { get; }
    public string SecurityDescriptorSddl { get; }
    public Stream Stream => stream;
    public bool IsConnected => stream.IsConnected;

    public static WindowsBrokerPipeServerInstance Create(
        Guid serverInstanceId,
        BrokerOwnerIdentity expectedOwner)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The InterCat broker pipe requires Windows.");
        }

        if (serverInstanceId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty server instance ID is required.", nameof(serverInstanceId));
        }

        if (expectedOwner.LogonSessionId == 0)
        {
            throw new ArgumentException("A non-empty expected logon session is required.", nameof(expectedOwner));
        }

        string userSid = WindowsBrokerTokenIdentity.CanonicalizeSid(expectedOwner.UserSid);
        string sddl = $"D:P(A;;GRGW;;;SY)(A;;GRGW;;;{userSid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
            sddl,
            SddlRevision1,
            out nint securityDescriptor,
            out _))
        {
            throw NativeFailure("The broker pipe security descriptor is invalid.");
        }

        try
        {
            var attributes = new SECURITY_ATTRIBUTES
            {
                Length = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = 0,
            };
            string pipeName = $@"\\.\pipe\InterCat.Broker.v1.{serverInstanceId:N}";
            SafePipeHandle handle = CreateNamedPipe(
                pipeName,
                PipeAccessDuplex | FileFlagOverlapped | FileFlagFirstPipeInstance,
                PipeRejectRemoteClients,
                maximumInstances: 1,
                BufferSize,
                BufferSize,
                PipeUnlimitedWait,
                in attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(
                    "The broker could not create its first-instance local control pipe.",
                    NativeFailure("CreateNamedPipeW failed.", error));
            }

            NamedPipeServerStream stream;
            try
            {
                stream = new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            return new(serverInstanceId, expectedOwner, userSid, sddl, stream);
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    public async ValueTask WaitForConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (stream.IsConnected)
        {
            throw new InvalidOperationException("The broker pipe already has a connected client.");
        }

        await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    public BrokerAuthenticatedPipeClient AuthenticateConnectedClient()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!stream.IsConnected)
        {
            throw new InvalidOperationException("Authenticate only after a named-pipe client connects.");
        }

        BrokerClientIdentity identity = WindowsBrokerTokenIdentity.ReadNamedPipeClient(stream.SafePipeHandle);
        if (!identity.Owner.Matches(expectedOwner))
        {
            throw new UnauthorizedAccessException(
                "The named-pipe client token does not match the broker's authorized user and logon session.");
        }

        if (!GetNamedPipeClientProcessId(stream.SafePipeHandle, out uint clientProcessId))
        {
            throw NativeFailure("The broker could not read the diagnostic client PID.");
        }

        return new(identity, clientProcessId);
    }

    /// <summary>
    /// Ends the current client so the same instance can wait for the next one. The broker keeps this one handle for its
    /// lifetime, so the pipe name is never free for another process to claim between clients.
    /// </summary>
    public void DisconnectClient()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            // A client that already closed leaves the instance broken rather than disconnected; Disconnect is the
            // only way back to listening in both cases.
            stream.Disconnect();
        }
        catch (InvalidOperationException)
        {
            // Never connected, or already disconnected.
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            stream.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Win32Exception NativeFailure(string message, int? error = null) =>
        new(error ?? Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        int stringSdRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeout,
        in SECURITY_ATTRIBUTES securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}

/// <summary>Runs the bounded frame/dispatcher loop for one already authenticated pipe client.</summary>
public static class BrokerPipeConnectionProcessor
{
    public const int MaximumRequestsPerConnection = 4096;

    public static async Task ProcessAsync(
        Stream stream,
        BrokerConnectionDispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(dispatcher);
        for (int requestCount = 0; requestCount < MaximumRequestsPerConnection; requestCount++)
        {
            BrokerWireFrame? request = await BrokerWireFrameCodec
                .ReadAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            BrokerWireFrame response = await dispatcher
                .DispatchAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await BrokerWireFrameCodec.WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidDataException(
            $"A broker connection exceeded {MaximumRequestsPerConnection} requests and was closed.");
    }
}
