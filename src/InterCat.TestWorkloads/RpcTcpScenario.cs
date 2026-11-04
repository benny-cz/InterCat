using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one seeded RPC run over TCP.</summary>
internal sealed record RpcTcpOptions
{
    public const string ScenarioId = "FX-RPC-002";

    /// <summary>
    /// The endpoint mapper's interface (DCE RPC's <c>ept</c>, [C706] appendix O; [MS-RPCE] 2.2.1.2.5). A client asks it,
    /// over the transport it names, where a server listens for an interface; its identity is DCE's, not InterCat's.
    /// </summary>
    public const string EndpointMapperInterface = "e1af8308-5d1f-11c9-91a4-08002b14a0fa";

    /// <summary>The service control manager's interface, the one FX-RPC-001 calls over local RPC, version 2.0.</summary>
    public const string ServiceControlInterface = "367abb81-9844-35f1-ad32-98f038001003";

    /// <summary>The endpoint mapper's well-known TCP port ([MS-RPCE] 2.1.1.1).</summary>
    public const int EndpointMapperPort = 135;

    public required string TruthDirectory { get; init; }
    public int Calls { get; init; } = 12;
    public int InterCallDelayMilliseconds { get; init; } = 40;
    public int StartAfterSeconds { get; init; }
    public int Concurrency { get; init; } = 1;
}

/// <summary>
/// RPC over TCP (§13.1 scenario 5, "RPC over local and network transports"). The client asks the endpoint mapper where
/// the service control manager listens over TCP, a known number of times, through the documented runtime API
/// (<c>RpcEpResolveBinding</c>), on a binding to the loopback address the client composes itself: each resolution is a
/// call to the endpoint mapper over <c>ncacn_ip_tcp</c>, made with no credentials, which the endpoint mapper answers
/// without asking for any. The client logs each call and, from the system's TCP table rather than anything InterCat
/// reads, its own connections to the endpoint mapper's port while the call's binding is held. The server is the RPC
/// service's host, not a workload process, which the scenario file states so nobody reads the result as a two-process
/// fixture of our own making.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class RpcTcpScenario
{
    private const int RpcOk = 0;
    private const uint ServiceManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const int TcpTableOwnerPidAll = 5;
    private const int AddressFamilyIPv4 = 2;
    private const int ErrorInsufficientBuffer = 122;

    // The NDR transfer syntax, version 2.0 ([C706] 14; [MS-RPCE] 2.2.4.12), which a map request names beside the
    // interface it asks for.
    private static readonly Guid NdrTransferSyntax = Guid.Parse("8a885d04-1ceb-11c9-9fe8-08002b104860");

    private static readonly JsonSerializerOptions SummaryOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [LibraryImport("rpcrt4.dll", EntryPoint = "RpcStringBindingComposeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RpcStringBindingCompose(
        string? objectUuid, string protocolSequence, string networkAddress, string? endpoint, string? options, out IntPtr stringBinding);

    [LibraryImport("rpcrt4.dll", EntryPoint = "RpcBindingFromStringBindingW")]
    private static partial int RpcBindingFromStringBinding(IntPtr stringBinding, out IntPtr binding);

    [LibraryImport("rpcrt4.dll", EntryPoint = "RpcBindingToStringBindingW")]
    private static partial int RpcBindingToStringBinding(IntPtr binding, out IntPtr stringBinding);

    [LibraryImport("rpcrt4.dll", EntryPoint = "RpcStringFreeW")]
    private static partial int RpcStringFree(ref IntPtr text);

    [LibraryImport("rpcrt4.dll", EntryPoint = "RpcEpResolveBinding")]
    private static partial int RpcEpResolveBinding(IntPtr binding, IntPtr interfaceSpecification);

    [LibraryImport("rpcrt4.dll", EntryPoint = "RpcBindingFree")]
    private static partial int RpcBindingFree(ref IntPtr binding);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenServiceControlManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenServiceHandle(IntPtr manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess status, int size, out int needed);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedTcpTable")]
    private static partial uint GetExtendedTcpTable(
        IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SyntaxIdentifier
    {
        public Guid Syntax;
        public ushort MajorVersion;
        public ushort MinorVersion;
    }

    /// <summary>
    /// <c>RPC_CLIENT_INTERFACE</c>: the specification MIDL would generate for a client of the interface, naming it and its
    /// transfer syntax and nothing to dispatch, which is all <c>RpcEpResolveBinding</c> reads of it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ClientInterface
    {
        public uint Length;
        public SyntaxIdentifier InterfaceId;
        public SyntaxIdentifier TransferSyntax;
        public IntPtr DispatchTable;
        public uint ProtocolSequenceEndpointCount;
        public IntPtr ProtocolSequenceEndpoint;
        public nuint Reserved;
        public IntPtr InterpreterInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    public static async Task<int> RunCoordinatorAsync(RpcTcpOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        int serverProcessId = ServiceProcessId("RpcSs");
        using Process client = StartChild(executable, options);
        await client.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var summary = new
        {
            scenarioId = RpcTcpOptions.ScenarioId,
            options.Calls,
            options.Concurrency,
            protocolSequence = "ncacn_ip_tcp",
            networkAddress = "127.0.0.1",
            endpoint = RpcTcpOptions.EndpointMapperPort,
            expectedInterfaceUuid = RpcTcpOptions.EndpointMapperInterface,
            resolvedInterfaceUuid = RpcTcpOptions.ServiceControlInterface,
            clientProcessId = client.Id,
            expectedServerProcessId = serverProcessId,
            serverNote =
                "The RPC server is the endpoint mapper, in the host of the RpcSs service, not a workload process. Its "
                + "process id is recorded so peer attribution can be checked, not assumed.",
            clientExitCode = client.ExitCode,
        };
        await File.WriteAllTextAsync(
            Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions),
            cancellationToken).ConfigureAwait(false);

        return client.ExitCode;
    }

    public static async Task<int> RunClientAsync(RpcTcpOptions options, CancellationToken cancellationToken)
    {
        await using var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-client.jsonl"),
            RpcTcpOptions.ScenarioId,
            "client");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: RpcTcpOptions.EndpointMapperInterface);
        if (options.StartAfterSeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(options.StartAfterSeconds), cancellationToken).ConfigureAwait(false);
        }

        IntPtr specification = Marshal.AllocHGlobal(Marshal.SizeOf<ClientInterface>());
        try
        {
            Marshal.StructureToPtr(ServiceControlSpecification(), specification, fDeleteOld: false);

            // Each client is a thread of its own, since a resolution blocks it: the calls are dealt to the clients in
            // turn, so --concurrency n has n of them in flight at once.
            int clients = Math.Max(1, options.Concurrency);
            int failures = 0;
            var threads = new List<Thread>();
            for (int client = 0; client < clients; client++)
            {
                int first = client;
                var thread = new Thread(() =>
                {
                    for (int call = first; call < options.Calls && !cancellationToken.IsCancellationRequested; call += clients)
                    {
                        if (!Call(truth, specification, call))
                        {
                            Interlocked.Increment(ref failures);
                        }

                        if (options.InterCallDelayMilliseconds > 0)
                        {
                            Thread.Sleep(options.InterCallDelayMilliseconds);
                        }
                    }
                })
                { IsBackground = true, Name = string.Create(CultureInfo.InvariantCulture, $"client {client}") };
                threads.Add(thread);
                thread.Start();
            }

            foreach (Thread thread in threads)
            {
                await Task.Run(thread.Join, CancellationToken.None).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            truth.Write(TruthEventKind.ProcessExiting);
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Marshal.FreeHGlobal(specification);
        }
    }

    // The local ports of the connections to the endpoint mapper's port the truth already names.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> Named = new();

    /// <summary>One call, logged as issued and completed, with the connections beneath it; whether it succeeded.</summary>
    private static bool Call(TruthLog truth, IntPtr specification, int call)
    {
        truth.Write(
            TruthEventKind.CallIssued,
            callId: call,
            remotePort: RpcTcpOptions.EndpointMapperPort,
            resourceName: RpcTcpOptions.EndpointMapperInterface,
            status: "map " + RpcTcpOptions.ServiceControlInterface + " over ncacn_ip_tcp");

        (int status, string? resolved, IReadOnlyList<int> ports) = Resolve(specification);

        // Each connection of this process to the endpoint mapper's port the system listed while the call's binding
        // was held, by its local port: the transport beneath the calls, as the system has it, named once, by the
        // first call during which it was listed.
        foreach (int port in ports.Where(port => Named.TryAdd(port, true)))
        {
            truth.Write(
                TruthEventKind.ConnectionEstablished,
                callId: call,
                localPort: port,
                remotePort: RpcTcpOptions.EndpointMapperPort,
                resourceName: RpcTcpOptions.EndpointMapperInterface);
        }

        truth.Write(
            TruthEventKind.CallCompleted,
            callId: call,
            remotePort: RpcTcpOptions.EndpointMapperPort,
            resourceName: RpcTcpOptions.EndpointMapperInterface,
            status: status == RpcOk
                ? "resolved to " + resolved
                : string.Create(CultureInfo.InvariantCulture, $"status {status}"));
        return status == RpcOk;
    }

    private static ClientInterface ServiceControlSpecification() => new()
    {
        Length = (uint)Marshal.SizeOf<ClientInterface>(),
        InterfaceId = new() { Syntax = Guid.Parse(RpcTcpOptions.ServiceControlInterface), MajorVersion = 2, MinorVersion = 0 },
        TransferSyntax = new() { Syntax = NdrTransferSyntax, MajorVersion = 2, MinorVersion = 0 },
    };

    /// <summary>
    /// One resolution: a binding to the endpoint mapper's host over TCP with no endpoint, resolved by the endpoint
    /// mapper, and the string binding it was completed to; with the local ports of this process's connections to the
    /// endpoint mapper's port while the binding was held.
    /// </summary>
    private static (int Status, string? Resolved, IReadOnlyList<int> Ports) Resolve(IntPtr specification)
    {
        int status = RpcStringBindingCompose(null, "ncacn_ip_tcp", "127.0.0.1", null, null, out IntPtr composed);
        if (status != RpcOk)
        {
            return (status, null, []);
        }

        IntPtr binding = IntPtr.Zero;
        try
        {
            status = RpcBindingFromStringBinding(composed, out binding);
            if (status != RpcOk)
            {
                return (status, null, []);
            }

            status = RpcEpResolveBinding(binding, specification);
            IReadOnlyList<int> ports = ConnectionsTo(RpcTcpOptions.EndpointMapperPort);
            if (status != RpcOk)
            {
                return (status, null, ports);
            }

            string? resolved = null;
            if (RpcBindingToStringBinding(binding, out IntPtr text) == RpcOk)
            {
                resolved = Marshal.PtrToStringUni(text);
                _ = RpcStringFree(ref text);
            }

            return (status, resolved, ports);
        }
        finally
        {
            if (binding != IntPtr.Zero)
            {
                _ = RpcBindingFree(ref binding);
            }

            _ = RpcStringFree(ref composed);
        }
    }

    /// <summary>The local ports of this process's IPv4 TCP connections to <paramref name="remotePort"/> on loopback.</summary>
    private static List<int> ConnectionsTo(int remotePort)
    {
        var ports = new List<int>();
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AddressFamilyIPv4, TcpTableOwnerPidAll, 0);
        if (result != ErrorInsufficientBuffer)
        {
            return ports;
        }

        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, AddressFamilyIPv4, TcpTableOwnerPidAll, 0) != 0)
            {
                return ports;
            }

            int rows = Marshal.ReadInt32(table);
            int rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            int self = System.Environment.ProcessId;
            for (int index = 0; index < rows; index++)
            {
                TcpRowOwnerPid row = Marshal.PtrToStructure<TcpRowOwnerPid>(table + 4 + (index * rowSize));
                if (row.OwningProcessId == self && NetworkPort(row.RemotePort) == remotePort)
                {
                    ports.Add(NetworkPort(row.LocalPort));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }

        ports.Sort();
        return ports;
    }

    // A row's port is in network order in the low 16 bits of its field.
    private static int NetworkPort(uint field) => BinaryPrimitives.ReverseEndianness((ushort)field);

    private static int ServiceProcessId(string serviceName)
    {
        IntPtr manager = OpenServiceControlManager(null, null, ServiceManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            IntPtr service = OpenServiceHandle(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                return QueryServiceStatusEx(service, ScStatusProcessInfo, out ServiceStatusProcess status, Marshal.SizeOf<ServiceStatusProcess>(), out _)
                    ? (int)status.ProcessId
                    : 0;
            }
            finally
            {
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    private static Process StartChild(string executable, RpcTcpOptions options)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("rpc-tcp-client");
        start.ArgumentList.Add("--truth");
        start.ArgumentList.Add(options.TruthDirectory);
        start.ArgumentList.Add("--calls");
        start.ArgumentList.Add(options.Calls.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--delay");
        start.ArgumentList.Add(options.InterCallDelayMilliseconds.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--start-after");
        start.ArgumentList.Add(options.StartAfterSeconds.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--concurrency");
        start.ArgumentList.Add(options.Concurrency.ToString(CultureInfo.InvariantCulture));

        return Process.Start(start)
            ?? throw new InvalidOperationException("The rpc-tcp-client process could not be started.");
    }
}
