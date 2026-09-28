using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.AlpcProbe;

/// <summary>
/// ADR-035's check of the product's own session conditions, where the feasibility run used the probe's: a session named
/// as the capture names one, created as the capture creates one (<c>Create | NoRestartOnCreate</c>, never adopted or
/// restarted), with the kernel's ALPC flag group and a manifest provider in it. It records whether the session became a
/// private system logger, whether both kinds of event arrived, how many system loggers the machine ran before, during and
/// after, whether the session was gone once stopped, and what happens when a manifest provider is enabled before the
/// kernel's flags. Only counters and modes are read; no other session is started, stopped or named in the result.
/// </summary>
internal static class SessionCheck
{
    private static readonly Guid KernelNetwork = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");
    private const ulong TcpKeywords = 0x30;
    private const uint SystemLoggerMode = 0x02000000;
    private const uint RealTimeMode = 0x00000100;

    public static async Task<Dictionary<string, object?>> RunAsync(string workload, string scratch)
    {
        int before = SystemLoggers();
        CaptureSessionIdentity identity = CaptureSessionIdentity.Create("alpc-check", Environment.ProcessId);
        long alpcSends = 0, alpcReceives = 0, alpcOther = 0, network = 0, other = 0;
        uint mode;
        int during;
        int lost;
        using (var session = new TraceEventSession(identity.SessionName,
            TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate) { StopOnDispose = true, BufferQuantumKB = 64 })
        {
            // The kernel's flags first: the first enablement starts the session, and a kernel provider starts it as a
            // private system logger.
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls);
            session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, TcpKeywords);
            mode = LogFileMode(identity.SessionName) ?? 0;
            during = SystemLoggers();
            session.Source.Kernel.ALPCSendMessage += _ => alpcSends++;
            session.Source.Kernel.ALPCReceiveMessage += _ => alpcReceives++;
            session.Source.Kernel.ALPCWaitForReply += _ => alpcOther++;
            session.Source.Kernel.ALPCUnwait += _ => alpcOther++;
            session.Source.Kernel.ALPCWaitForNewMessage += _ => alpcOther++;
            session.Source.Dynamic.All += data =>
            {
                if (data.ProviderGuid == KernelNetwork) network++;
                else other++;
            };
            Task pump = Task.Run(() => session.Source.Process());
            await Task.Delay(1_000).ConfigureAwait(false);
            await RunAsync(workload, ["tcp-loopback", "--truth", Path.Combine(scratch, "tcp"), "--messages", "20"]).ConfigureAwait(false);
            await RunAsync(workload, ["rpc-local", "--truth", Path.Combine(scratch, "rpc"), "--calls", "50"]).ConfigureAwait(false);
            await Task.Delay(1_000).ConfigureAwait(false);
            lost = session.EventsLost;
            session.Stop();
            await pump.ConfigureAwait(false);
        }

        bool gone = LogFileMode(identity.SessionName) is null;
        int after = SystemLoggers();

        // The other order: a manifest provider first starts an ordinary session, into which the kernel's flags are asked.
        CaptureSessionIdentity order = CaptureSessionIdentity.Create("alpc-order", Environment.ProcessId);
        string reversed;
        uint reversedMode;
        using (var session = new TraceEventSession(order.SessionName,
            TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate) { StopOnDispose = true, BufferQuantumKB = 64 })
        {
            session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, TcpKeywords);
            try
            {
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls);
                reversed = "accepted";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                reversed = $"{exception.GetType().Name}: {exception.Message}";
            }

            reversedMode = LogFileMode(order.SessionName) ?? 0;
            session.Stop();
        }

        bool reversedGone = LogFileMode(order.SessionName) is null;
        return new()
        {
            ["session"] = new Dictionary<string, object?>
            {
                ["createdAs"] = "TraceEventSession(name, Create | NoRestartOnCreate), named by CaptureSessionIdentity",
                ["privateSystemLogger"] = (mode & SystemLoggerMode) != 0,
                ["realTime"] = (mode & RealTimeMode) != 0,
                ["logFileMode"] = string.Create(CultureInfo.InvariantCulture, $"0x{mode:X8}"),
                ["eventsLost"] = lost,
                ["goneAfterStop"] = gone,
            },
            ["events"] = new Dictionary<string, object?>
            {
                ["alpcSends"] = alpcSends,
                ["alpcReceives"] = alpcReceives,
                ["alpcWaitsAndUnwaits"] = alpcOther,
                ["kernelNetworkTcp"] = network,
                ["other"] = other,
            },
            ["systemLoggersOnTheMachine"] = new Dictionary<string, object?>
            {
                ["before"] = before,
                ["during"] = during,
                ["after"] = after,
            },
            ["manifestProviderFirst"] = new Dictionary<string, object?>
            {
                ["kernelFlagsAfterward"] = reversed,
                ["privateSystemLogger"] = (reversedMode & SystemLoggerMode) != 0,
                ["logFileMode"] = string.Create(CultureInfo.InvariantCulture, $"0x{reversedMode:X8}"),
                ["goneAfterStop"] = reversedGone,
            },
        };
    }

    private static async Task RunAsync(string workload, string[] arguments)
    {
        using Process run = Process.Start(new ProcessStartInfo(workload, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        await run.WaitForExitAsync().ConfigureAwait(false);
        if (run.ExitCode != 0) throw new InvalidOperationException($"{arguments[0]} exited with {run.ExitCode}.");
    }

    /// <summary>How many running sessions are private system loggers. Only each session's mode is read.</summary>
    private static int SystemLoggers() =>
        TraceEventSession.GetActiveSessionNames().Count(name => ((LogFileMode(name) ?? 0) & SystemLoggerMode) != 0);

    /// <summary>A running session's log file mode, from EVENT_TRACE_CONTROL_QUERY; null when no such session runs.</summary>
    private static uint? LogFileMode(string name)
    {
        const int Properties = 120;
        const int NameBytes = 2048;
        IntPtr buffer = Marshal.AllocHGlobal(Properties + (2 * NameBytes));
        try
        {
            for (int offset = 0; offset < Properties + (2 * NameBytes); offset++) Marshal.WriteByte(buffer, offset, 0);
            Marshal.WriteInt32(buffer, 0, Properties + (2 * NameBytes));
            Marshal.WriteInt32(buffer, 44, 0x00020000);
            Marshal.WriteInt32(buffer, 112, Properties + NameBytes);
            Marshal.WriteInt32(buffer, 116, Properties);
            int status = ControlTraceW(0, name, buffer, 0);
            return status == 0 ? (uint)Marshal.ReadInt32(buffer, 64) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);
}
