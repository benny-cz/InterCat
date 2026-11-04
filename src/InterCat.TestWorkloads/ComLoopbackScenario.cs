using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one FX-COM-001 run: how many calls the client makes, and how far apart.</summary>
internal sealed record ComLoopbackOptions
{
    public const string ScenarioId = "FX-COM-001";

    public const string InProcessScenarioId = "FX-COM-002";

    public required string TruthDirectory { get; init; }
    public int Calls { get; init; } = 64;
    public int InterCallDelayMilliseconds { get; init; } = 5;

    /// <summary>When above zero, the client makes its calls in bursts of this many, unpaced, the delay between bursts.</summary>
    public int Burst { get; init; }

    /// <summary>
    /// Whether each process waits for a line on standard input before it initializes COM, so a session scoped to its process
    /// is in place first.
    /// </summary>
    public bool WaitForStart { get; init; }

    /// <summary>The item the server registered its object under, which the coordinator hands its client.</summary>
    public string? Moniker { get; init; }
}

/// <summary>
/// A two-process out-of-process COM exchange with an independent truth log per process (plan M6, the COM/DCOM/WinRT work
/// package). The server puts an automation object in the running object table under an item moniker of its own; the client
/// takes it from there and calls it, each call an <c>IDispatch</c> call the standard marshaler carries to the server, which
/// logs each one it serves. Nothing is registered - no class, interface or type library - so the machine is left as it
/// was. It uses ordinary COM only: nothing here observes ETW or InterCat.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ComLoopbackScenario
{
    private const uint MultithreadedApartment = 0x0;
    private const int RegistrationKeepsAlive = 0x1;

    // The system's in-memory dictionary, registered apartment-threaded on every Windows build.
    private const string DictionaryClass = "Scripting.Dictionary";

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Runs the coordinator: it starts the server, hands its moniker to the client, and stops the server after.</summary>
    public static async Task<int> RunCoordinatorAsync(ComLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        using Process server = StartChild(executable, "com-loopback-server", options, redirect: true);
        string? handshake = await server.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        string? moniker = handshake is null ? null : JsonDocument.Parse(handshake).RootElement.GetProperty("moniker").GetString();
        if (moniker is null)
        {
            await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The server process exited before it registered its object.").ConfigureAwait(false);
            return 1;
        }

        using Process client = StartChild(executable, "com-loopback-client", options with { Moniker = moniker }, redirect: false);
        await client.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        // The client is done: the server revokes its object and exits.
        await server.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
        await server.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var summary = new
        {
            scenarioId = ComLoopbackOptions.ScenarioId,
            options.Calls,
            options.InterCallDelayMilliseconds,
            options.Burst,
            moniker,
            serverProcessId = server.Id,
            clientProcessId = client.Id,
            serverExitCode = server.ExitCode,
            clientExitCode = client.ExitCode,
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions), cancellationToken).ConfigureAwait(false);
        return server.ExitCode != 0 || client.ExitCode != 0 ? 1 : 0;
    }

    /// <summary>
    /// Registers the object under an item moniker named for this process, says so on standard output, serves every call
    /// that reaches it until a line arrives on standard input, and revokes it.
    /// </summary>
    public static async Task<int> RunServerAsync(ComLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-server.jsonl"), ComLoopbackOptions.ScenarioId, "server");
        truth.Write(TruthEventKind.ProcessStarted);
        await WaitForStartAsync(options, cancellationToken).ConfigureAwait(false);
        Initialize();

        string name = string.Create(CultureInfo.InvariantCulture, $"InterCat.{ComLoopbackOptions.ScenarioId}.{truth.ProcessId}");
        IRunningObjectTable table = Table();
        IMoniker moniker = Moniker(name);
        var served = new ComFixtureObject(truth, name);
        int cookie = table.Register(RegistrationKeepsAlive, served, moniker);
        truth.Write(TruthEventKind.ListenerBound, resourceName: name);
        try
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { moniker = name })).ConfigureAwait(false);
            await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
            _ = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            table.Revoke(cookie);
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    /// <summary>Takes the server's object from the running object table and calls it, logging each call and its completion.</summary>
    public static async Task<int> RunClientAsync(ComLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-client.jsonl"), ComLoopbackOptions.ScenarioId, "client");
        truth.Write(TruthEventKind.ProcessStarted);
        string name = options.Moniker ?? throw new InvalidOperationException("--moniker names the server's object.");
        await WaitForStartAsync(options, cancellationToken).ConfigureAwait(false);
        Initialize();

        Table().GetObject(Moniker(name), out object proxy);
        truth.Write(TruthEventKind.ConnectionEstablished, resourceName: name);
        int failures = 0;
        try
        {
            for (int call = 0; call < options.Calls; call++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                truth.Write(TruthEventKind.CallIssued, callId: call, resourceName: nameof(ComFixtureObject.Echo));
                string status;
                try
                {
                    object? answer = proxy.GetType().InvokeMember(nameof(ComFixtureObject.Echo), BindingFlags.InvokeMethod, null, proxy,
                        [call], CultureInfo.InvariantCulture);
                    status = answer is int echoed && echoed == call ? "S_OK" : "wrong answer";
                }
                catch (COMException exception)
                {
                    status = string.Create(CultureInfo.InvariantCulture, $"0x{exception.HResult:X8}");
                }

                failures += status == "S_OK" ? 0 : 1;
                truth.Write(TruthEventKind.CallCompleted, callId: call, resourceName: nameof(ComFixtureObject.Echo), status: status);
                if (options.InterCallDelayMilliseconds > 0 && (options.Burst <= 0 || (call + 1) % options.Burst == 0))
                {
                    await Task.Delay(options.InterCallDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(proxy);
        }

        truth.Write(TruthEventKind.ConnectionClosed, resourceName: name);
        truth.Write(TruthEventKind.ProcessExiting);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// FX-COM-002: one process's calls to an apartment-threaded object it created on its multithreaded apartment's thread,
    /// which COM therefore hosts in a single-threaded apartment of its own and reaches through a proxy - calls that cross
    /// an apartment and never a process. The class is the system's in-memory dictionary, and each call asks whether it holds
    /// a key, so nothing outside the process is touched.
    /// </summary>
    public static async Task<int> RunInProcessAsync(ComLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        int processId;
        int failures = 0;
        await using (var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-client.jsonl"), ComLoopbackOptions.InProcessScenarioId, "client"))
        {
            processId = truth.ProcessId;
            truth.Write(TruthEventKind.ProcessStarted);
            Initialize();
            Type type = Type.GetTypeFromProgID(DictionaryClass, throwOnError: true)!;
            object dictionary = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"{DictionaryClass} could not be created.");
            truth.Write(TruthEventKind.ConnectionEstablished, resourceName: DictionaryClass);
            try
            {
                for (int call = 0; call < options.Calls; call++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    truth.Write(TruthEventKind.CallIssued, callId: call, resourceName: "Exists");
                    string status;
                    try
                    {
                        object? answer = type.InvokeMember("Exists", BindingFlags.InvokeMethod, null, dictionary, [call], CultureInfo.InvariantCulture);
                        status = answer is false ? "S_OK" : "wrong answer";
                    }
                    catch (COMException exception)
                    {
                        status = string.Create(CultureInfo.InvariantCulture, $"0x{exception.HResult:X8}");
                    }

                    failures += status == "S_OK" ? 0 : 1;
                    truth.Write(TruthEventKind.CallCompleted, callId: call, resourceName: "Exists", status: status);
                    if (options.InterCallDelayMilliseconds > 0)
                    {
                        await Task.Delay(options.InterCallDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(dictionary);
            }

            truth.Write(TruthEventKind.ConnectionClosed, resourceName: DictionaryClass);
            truth.Write(TruthEventKind.ProcessExiting);
        }

        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "scenario.json"), JsonSerializer.Serialize(new
        {
            scenarioId = ComLoopbackOptions.InProcessScenarioId,
            options.Calls,
            options.InterCallDelayMilliseconds,
            @class = DictionaryClass,
            clientProcessId = processId,
        }, SummaryOptions), cancellationToken).ConfigureAwait(false);
        return failures == 0 ? 0 : 1;
    }

    private static async Task WaitForStartAsync(ComLoopbackOptions options, CancellationToken cancellationToken)
    {
        if (options.WaitForStart)
        {
            _ = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Initialize()
    {
        int result = CoInitializeEx(IntPtr.Zero, MultithreadedApartment);
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static IRunningObjectTable Table()
    {
        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out IntPtr table));
        try
        {
            return (IRunningObjectTable)Marshal.GetObjectForIUnknown(table);
        }
        finally
        {
            Marshal.Release(table);
        }
    }

    private static IMoniker Moniker(string item)
    {
        Marshal.ThrowExceptionForHR(CreateItemMoniker("!", item, out IntPtr moniker));
        try
        {
            return (IMoniker)Marshal.GetObjectForIUnknown(moniker);
        }
        finally
        {
            Marshal.Release(moniker);
        }
    }

    private static Process StartChild(string executable, string verb, ComLoopbackOptions options, bool redirect)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardInput = redirect,
        };
        start.ArgumentList.Add(verb);
        start.ArgumentList.Add("--truth");
        start.ArgumentList.Add(options.TruthDirectory);
        start.ArgumentList.Add("--calls");
        start.ArgumentList.Add(options.Calls.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--delay");
        start.ArgumentList.Add(options.InterCallDelayMilliseconds.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--burst");
        start.ArgumentList.Add(options.Burst.ToString(CultureInfo.InvariantCulture));
        if (options.Moniker is { } moniker)
        {
            start.ArgumentList.Add("--moniker");
            start.ArgumentList.Add(moniker);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint apartment);

    [LibraryImport("ole32.dll")]
    private static partial int GetRunningObjectTable(uint reserved, out IntPtr table);

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateItemMoniker(string delimiter, string item, out IntPtr moniker);
}

/// <summary>
/// The object FX-COM-001's server puts in the running object table: an automation object, reached through the
/// <c>IDispatch</c> its class interface carries, whose one method logs each call it serves.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed class ComFixtureObject
{
    private readonly TruthLog truth;
    private readonly string name;

    internal ComFixtureObject(TruthLog truth, string name)
    {
        this.truth = truth;
        this.name = name;
    }

    /// <summary>Answers a call with its own argument, logging it as served.</summary>
    public int Echo(int call)
    {
        truth.Write(TruthEventKind.CallServed, callId: call, resourceName: name);
        return call;
    }
}
