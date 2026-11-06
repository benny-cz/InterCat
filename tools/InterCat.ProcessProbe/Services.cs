using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.ProcessProbe;

/// <summary>
/// Which services a process hosts, as M6's process enrichment asks (plan M6: "enrich process nodes with package, service and
/// user-session information as accessible"): Microsoft-Windows-Services, enabled at every level and keyword under a real-time
/// session of the probe's own and asked for its state as a capture asks a provider, set against the service control
/// manager's own list of the running services and the process hosting each, read by the probe as the session runs and again
/// once it has stopped. It starts, stops and configures no service, so a status change it records is one the machine made
/// of itself; and it writes each event's identity and counts, never a PID or a service's name.
/// </summary>
internal static partial class Services
{
    private static readonly Guid ServicesProvider = Guid.Parse("0063715b-eeda-4007-9429-ad526f62696e");
    private const int StatusChange = 105;

    public static async Task<Dictionary<string, object?>> RunAsync(int seconds, int runs)
    {
        var results = new List<Dictionary<string, object?>>();
        for (int run = 1; run <= runs; run++)
        {
            Dictionary<string, object?> result = await OnceAsync(seconds).ConfigureAwait(false);
            result["run"] = run;
            results.Add(result);
        }

        return new()
        {
            ["schema"] = "intercat.service-provider-measurement.v1",
            ["question"] = "Whether Microsoft-Windows-Services, enabled at every level and keyword and asked for its state as a capture "
                + "asks a provider, names each running service with the process hosting it, as the service control manager lists them, "
                + "and whether a service's status change names the service and the process hosting it.",
            ["build"] = InterCat.Capture.Windows.CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            ["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}",
            ["secondsAsked"] = seconds,
            ["runs"] = results,
            ["notes"] = new[]
            {
                "The probe starts, stops and configures no service: a status change a run records is one the machine made of itself.",
                "Services are compared by their name, case ignored, and the process the service control manager says hosts each; "
                    + "the report keeps counts, never a PID or a service's name.",
            },
        };
    }

    // One session: the service control manager's list as it began, the provider enabled and its state asked for, and the list
    // again once it stopped.
    private static async Task<Dictionary<string, object?>> OnceAsync(int seconds)
    {
        var events = new List<(int Id, int Version, int? ProcessId, string? Service)>();
        HashSet<(int ProcessId, string Service)> before, after;
        int lost;
        string sessionName = "InterCat-services-probe-" + Guid.NewGuid().ToString("N");
        var watch = Stopwatch.StartNew();
        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true, BufferSizeMB = 64 })
        {
            var parser = new RegisteredTraceEventParser(session.Source);
            parser.All += data =>
            {
                if (data.ProviderGuid != ServicesProvider)
                {
                    return;
                }

                int? process = data.PayloadIndex("PID") >= 0 ? Convert.ToInt32(data.PayloadByName("PID"), System.Globalization.CultureInfo.InvariantCulture) : null;
                string? service = data.PayloadIndex("ServiceName") >= 0 ? (data.PayloadByName("ServiceName") as string)?.ToUpperInvariant() : null;
                lock (events)
                {
                    events.Add(((int)data.ID, data.Version, process, service));
                }
            };
            Task processing = Task.Run(() => session.Source.Process());
            await Task.Delay(500).ConfigureAwait(false);
            before = Running();
            session.EnableProvider(ServicesProvider, TraceEventLevel.Verbose, ulong.MaxValue);
            await Task.Delay(1_000).ConfigureAwait(false);
            int enabledOnly;
            lock (events)
            {
                enabledOnly = events.Count;
            }

            // The provider's state, as a capture asks for it once its session runs.
            session.CaptureState(ServicesProvider);
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            lost = session.EventsLost;
            session.Stop();
            await processing.ConfigureAwait(false);
            after = Running();

            (int Id, int Version, int? ProcessId, string? Service)[] seen;
            lock (events)
            {
                seen = [.. events];
            }

            HashSet<(int ProcessId, string Service)> named = [.. seen.Where(static item => item.Id == StatusChange && item.ProcessId is > 0 && item.Service is not null)
                .Select(static item => (item.ProcessId!.Value, item.Service!))];
            HashSet<(int ProcessId, string Service)> heldThroughout = [.. before.Intersect(after)];
            HashSet<(int ProcessId, string Service)> changed = [.. before.Except(after).Concat(after.Except(before))];
            return new()
            {
                ["measuredUtc"] = DateTimeOffset.UtcNow,
                ["session"] = new Dictionary<string, object?>
                {
                    ["mode"] = "real-time session of the probe's own, Microsoft-Windows-Services at Verbose with every keyword, then its state asked for",
                    ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1),
                    ["eventsLost"] = lost,
                },
                ["eventsOnEnableAlone"] = enabledOnly,
                ["events"] = seen.GroupBy(static item => (item.Id, item.Version)).OrderBy(static group => group.Key)
                    .Select(static group => new Dictionary<string, object?>
                    {
                        ["id"] = group.Key.Id,
                        ["version"] = group.Key.Version,
                        ["count"] = group.Count(),
                        ["namingAProcessAndAService"] = group.Count(static item => item.ProcessId is > 0 && item.Service is not null),
                    }).ToArray(),
                ["serviceControlManager"] = new Dictionary<string, object?>
                {
                    ["runningAsTheSessionBegan"] = before.Count,
                    ["runningOnceItStopped"] = after.Count,
                    ["runningThroughout"] = heldThroughout.Count,
                    ["hostingProcesses"] = heldThroughout.Select(static pair => pair.ProcessId).Distinct().Count(),
                },
                ["statusChanges"] = new Dictionary<string, object?>
                {
                    ["pairsNamed"] = named.Count,
                    ["ofTheServicesRunningThroughout"] = named.Count(heldThroughout.Contains),
                    ["servicesTheManagerListedChanged"] = changed.Count,
                    ["ofThemNamed"] = changed.Count(named.Contains),
                    ["namingAPairTheManagerNeverListed"] = named.Count(pair => !before.Contains(pair) && !after.Contains(pair)),
                },
            };
        }
    }

    // Every running service the service control manager lists, with the process hosting it.
    private static unsafe HashSet<(int ProcessId, string Service)> Running()
    {
        var running = new HashSet<(int, string)>();
        nint manager = OpenSCManagerW(null, null, 0x0004);
        if (manager == 0)
        {
            throw new InvalidOperationException($"The service control manager could not be opened ({Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            uint resume = 0;
            byte[] buffer = new byte[256 * 1024];
            while (true)
            {
                bool done;
                uint needed, returned;
                fixed (byte* at = buffer)
                {
                    done = EnumServicesStatusExW(manager, 0, 0x30, 0x1, at, (uint)buffer.Length, out needed, out returned, ref resume, null);
                    int error = done ? 0 : Marshal.GetLastPInvokeError();
                    if (!done && error != 234)
                    {
                        throw new InvalidOperationException($"The running services could not be listed ({error}).");
                    }

                    var entries = (EnumServiceStatusProcess*)at;
                    for (int index = 0; index < returned; index++)
                    {
                        if (entries[index].ProcessId != 0)
                        {
                            running.Add(((int)entries[index].ProcessId, new string(entries[index].ServiceName).ToUpperInvariant()));
                        }
                    }
                }

                if (done)
                {
                    return running;
                }

                if (needed > buffer.Length)
                {
                    buffer = new byte[needed];
                }
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct EnumServiceStatusProcess
    {
        public char* ServiceName;
        public char* DisplayName;
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

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumServicesStatusExW(nint manager, int infoLevel, uint serviceType, uint serviceState, byte* services,
        uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
