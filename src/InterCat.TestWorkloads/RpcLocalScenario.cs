using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one seeded local RPC run.</summary>
internal sealed record RpcLocalOptions
{
    public const string ScenarioId = "FX-RPC-001";

    /// <summary>
    /// The service control manager interface. The workload calls it through the documented Windows API,
    /// so the interface identity is a property of Windows rather than something InterCat chose.
    /// </summary>
    public const string ServiceControlInterface = "367abb81-9844-35f1-ad32-98f038001003";

    public required string TruthDirectory { get; init; }
    public int Calls { get; init; } = 12;
    public int InterCallDelayMilliseconds { get; init; } = 40;
    public string ServiceName { get; init; } = "Schedule";
}

/// <summary>
/// A local RPC workload (IC-006, section 13.1 scenario 5). The client issues a known number of calls to a
/// Windows RPC server through the ordinary service control API, and logs each call itself. The server is a
/// Windows service host rather than a workload process, which is stated in the scenario file so nobody
/// reads the result as a two-process fixture of our own making.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class RpcLocalScenario
{
    private const uint ServiceManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;

    // The service control manager is reached through its documented API, which carries the call over local
    // RPC. Nothing here speaks RPC directly: the workload stays an ordinary client of Windows.
    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenServiceControlManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenServiceHandle(IntPtr manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    private static readonly JsonSerializerOptions SummaryOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunCoordinatorAsync(RpcLocalOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        int serverProcessId = ResolveServiceHostProcessId();
        using Process client = StartChild(executable, "rpc-local-client", options);
        await client.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var summary = new
        {
            scenarioId = RpcLocalOptions.ScenarioId,
            options.Calls,
            options.ServiceName,
            expectedInterfaceUuid = RpcLocalOptions.ServiceControlInterface,
            clientProcessId = client.Id,
            expectedServerProcessId = serverProcessId,
            serverNote =
                "The RPC server is the Windows service control manager host, not a workload process. Its "
                + "process id is recorded so peer attribution can be checked, not assumed.",
            clientExitCode = client.ExitCode,
        };
        await File.WriteAllTextAsync(
            Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions),
            cancellationToken).ConfigureAwait(false);

        return client.ExitCode;
    }

    public static async Task<int> RunClientAsync(RpcLocalOptions options, CancellationToken cancellationToken)
    {
        await using var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-client.jsonl"),
            RpcLocalOptions.ScenarioId,
            "client");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: RpcLocalOptions.ServiceControlInterface);

        for (int call = 0; call < options.Calls; call++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            truth.Write(
                TruthEventKind.CallIssued,
                callId: call,
                resourceName: RpcLocalOptions.ServiceControlInterface,
                status: "service status query");

            string status = QueryServiceState(options.ServiceName);

            truth.Write(
                TruthEventKind.CallCompleted,
                callId: call,
                resourceName: RpcLocalOptions.ServiceControlInterface,
                status: status);

            if (options.InterCallDelayMilliseconds > 0)
            {
                await Task.Delay(options.InterCallDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    /// <summary>
    /// One service status query. Opening the manager, opening the service and querying it are separate
    /// calls to the same interface, which is why a truth call can answer to more than one observed call.
    /// </summary>
    private static string QueryServiceState(string serviceName)
    {
        IntPtr manager = OpenServiceControlManager(null, null, ServiceManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return string.Create(CultureInfo.InvariantCulture, $"failed to open the manager: {Marshal.GetLastWin32Error()}");
        }

        try
        {
            IntPtr service = OpenServiceHandle(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return string.Create(CultureInfo.InvariantCulture, $"failed to open the service: {Marshal.GetLastWin32Error()}");
            }

            try
            {
                return QueryServiceStatus(service, out ServiceStatus status)
                    ? string.Create(CultureInfo.InvariantCulture, $"state {status.CurrentState}")
                    : string.Create(CultureInfo.InvariantCulture, $"query failed: {Marshal.GetLastWin32Error()}");
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

    private static int ResolveServiceHostProcessId()
    {
        foreach (Process process in Process.GetProcessesByName("services"))
        {
            using (process)
            {
                return process.Id;
            }
        }

        return 0;
    }

    private static Process StartChild(string executable, string verb, RpcLocalOptions options)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(verb);
        start.ArgumentList.Add("--truth");
        start.ArgumentList.Add(options.TruthDirectory);
        start.ArgumentList.Add("--calls");
        start.ArgumentList.Add(options.Calls.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--service");
        start.ArgumentList.Add(options.ServiceName);

        return Process.Start(start)
            ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }
}
