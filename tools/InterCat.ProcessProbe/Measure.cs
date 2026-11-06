using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.ProcessProbe;

/// <summary>
/// FX-PROC-001 under a real-time session of the probe's own collecting Microsoft-Windows-Kernel-Process's process keyword, as
/// every capture does: each child's process start set against what the workload read of it before it ran - its package
/// full name and package-relative application ID by the application model, its token's elevation type, whether it is
/// elevated, its integrity level and its session - and the session's process rundown, which the probe asks for once the
/// session runs, set against the package the application model gives each process it names, read by the probe as the
/// rundown ran. It writes the fields as the records carried them and counts of the rundown's agreement, never a PID.
/// </summary>
internal static partial class Measure
{
    private static readonly Guid KernelProcess = Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    private const ulong ProcessKeyword = 0x10;
    private const int ProcessStart = 1;
    private const int ProcessRundown = 15;
    private const uint QueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string output, string workload)
    {
        string truth = Path.Combine(output, "truth");
        var records = new List<Record>();
        Dictionary<int, (long Creation, string? Package)> asRundownRan;
        int lost;
        string sessionName = "InterCat-process-probe-" + Guid.NewGuid().ToString("N");
        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true, BufferSizeMB = 64 })
        {
            var parser = new RegisteredTraceEventParser(session.Source);
            parser.All += data =>
            {
                if (data.ProviderGuid == KernelProcess && (int)data.ID is ProcessStart or ProcessRundown)
                {
                    records.Add(Read(data));
                }
            };
            Task processing = Task.Run(() => session.Source.Process());
            await Task.Delay(500).ConfigureAwait(false);
            session.EnableProvider(KernelProcess, TraceEventLevel.Informational, ProcessKeyword);
            await Task.Delay(1_000).ConfigureAwait(false);

            // The process rundown, as a capture asks for provider state; the probe reads every process's package as it runs.
            session.CaptureState(KernelProcess, (long)ProcessKeyword);
            asRundownRan = Packages();
            await Task.Delay(2_000).ConfigureAwait(false);

            using (Process started = Process.Start(new ProcessStartInfo(workload, ["process-package", "--truth", truth])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!)
            {
                await started.WaitForExitAsync().ConfigureAwait(false);
                if (started.ExitCode != 0)
                {
                    Console.Error.WriteLine($"The truth workload exited with {started.ExitCode}.");
                    return 4;
                }
            }

            await Task.Delay(1_500).ConfigureAwait(false);
            lost = session.EventsLost;
            session.Stop();
            await processing.ConfigureAwait(false);
        }

        using JsonDocument scenario = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(truth, "scenario.json")).ConfigureAwait(false));
        var children = new List<object>();
        foreach (JsonElement child in scenario.RootElement.GetProperty("children").EnumerateArray())
        {
            int process = child.GetProperty("processId").GetInt32();
            long creation = child.GetProperty("creationFileTime").GetInt64();
            Record[] starts = [.. records.Where(record => record.Id == ProcessStart && record.ProcessId == process && record.CreateFileTime == creation)];
            Record? start = starts.Length == 1 ? starts[0] : null;
            string? Truth(string name) => child.GetProperty(name).ValueKind == JsonValueKind.Null ? null : child.GetProperty(name).ToString();
            children.Add(new Dictionary<string, object?>
            {
                ["role"] = child.GetProperty("role").GetString(),
                ["startsRecorded"] = starts.Length,
                ["version"] = start?.Version,
                ["fields"] = new Dictionary<string, object?>
                {
                    ["packageFullName"] = Compare(Truth("packageFullName"), start?.PackageFullName),
                    ["packageRelativeApplicationId"] = Compare(Truth("packageRelativeApplicationId"), start?.PackageRelativeAppId),
                    ["tokenElevationType"] = Compare(Truth("tokenElevationType"), start?.ElevationType),
                    ["tokenIsElevated"] = Compare(child.GetProperty("tokenIsElevated").GetBoolean() ? "1" : "0", start?.IsElevated),
                    ["integrityLevel"] = Compare(Truth("integrityLevelSid"), start?.MandatoryLabel),
                    ["session"] = Compare(Truth("sessionId"), start?.SessionId),
                },
                ["packageFullNameAsTdhDecodesIt"] = Compare(Truth("packageFullName"), start?.DecodedPackageFullName),
            });
        }

        // The rundown against the application model as it ran: each process it named, by its PID and creation time, whose
        // package the probe could read, packaged or not, and whether the record said the same.
        Record[] rundown = [.. records.Where(static record => record.Id == ProcessRundown)];
        int readable = 0, agreePackaged = 0, agreeUnpackaged = 0, disagree = 0, changed = 0;
        foreach (Record record in rundown)
        {
            if (!asRundownRan.TryGetValue(record.ProcessId, out (long Creation, string? Package) read) || read.Creation != record.CreateFileTime)
            {
                changed++;
                continue;
            }

            readable++;
            string? recorded = string.IsNullOrEmpty(record.PackageFullName) ? null : record.PackageFullName;
            if (!string.Equals(recorded, read.Package, StringComparison.Ordinal))
            {
                disagree++;
            }
            else if (read.Package is null)
            {
                agreeUnpackaged++;
            }
            else
            {
                agreePackaged++;
            }
        }

        var report = new Dictionary<string, object?>
        {
            ["schema"] = "intercat.process-package-measurement.v1",
            ["scenario"] = "FX-PROC-001",
            ["question"] = "Whether Kernel-Process's process start and rundown, which every capture collects, carry a process's package "
                + "full name and package-relative application ID, its token's elevation and integrity level and its session, as the "
                + "application model and the process's token give them.",
            ["measuredUtc"] = DateTimeOffset.UtcNow,
            ["build"] = InterCat.Capture.Windows.CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            ["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}",
            ["session"] = new Dictionary<string, object?>
            {
                ["mode"] = "real-time session of the probe's own, Kernel-Process at Informational with the process keyword (0x10)",
                ["eventsLost"] = lost,
            },
            ["children"] = children,
            ["rundown"] = new Dictionary<string, object?>
            {
                ["records"] = rundown.Length,
                ["versions"] = rundown.Select(static record => record.Version).Distinct().Order().ToArray(),
                ["processesReadAsItRan"] = asRundownRan.Count,
                ["readable"] = readable,
                ["packagedAgreeing"] = agreePackaged,
                ["unpackagedAgreeing"] = agreeUnpackaged,
                ["disagreeing"] = disagree,
                ["changedOrUnreadable"] = changed,
                ["withAnElevationType"] = rundown.Count(static record => record.ElevationType is not null),
                ["withAMandatoryLabel"] = rundown.Count(static record => !string.IsNullOrEmpty(record.MandatoryLabel)),
                ["withAPackageAsTdhDecodesIt"] = rundown.Count(static record => !string.IsNullOrEmpty(record.DecodedPackageFullName)),
            },
        };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json)).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        return 0;
    }

    // What a field's truth and its record said, and whether they are the same.
    private static Dictionary<string, object?> Compare(string? truth, string? recorded)
    {
        string? normalized = string.IsNullOrEmpty(recorded) ? null : recorded;
        return new() { ["truth"] = truth, ["recorded"] = normalized, ["same"] = string.Equals(truth, normalized, StringComparison.Ordinal) };
    }

    // One process start (version 3 or 4) or rundown (1 or 2) by the layout the machine registers for it, read from its payload
    // as the product reads it: its fixed fields at their offsets, then the mandatory label's SID, whose own sub-authority count
    // says where the image after it begins, the image's checksum and time stamp, the package full name and the
    // package-relative application ID. Beside it, the package as TDH decodes it by name, which the comparison reports apart.
    private static Record Read(TraceEvent data)
    {
        int id = (int)data.ID;
        byte[] payload = data.EventData();
        string? decoded = data.PayloadIndex("PackageFullName") >= 0 ? Convert.ToString(data.PayloadByName("PackageFullName"), CultureInfo.InvariantCulture) : null;
        bool layout = (id == ProcessStart && data.Version >= 3) || (id == ProcessRundown && data.Version >= 1);
        if (!layout || payload.Length < 50)
        {
            return new(id, data.Version, payload.Length >= 4 ? BitConverter.ToInt32(payload, 0) : 0, 0, null, null, null, null, null, null, decoded);
        }

        int at = 48;
        string? label = Sid(payload, ref at);
        _ = Text(payload, ref at);
        at += 8;
        string? package = Text(payload, ref at);
        string? relative = Text(payload, ref at);
        return new(id, data.Version, BitConverter.ToInt32(payload, 0), BitConverter.ToInt64(payload, 12), package, relative,
            BitConverter.ToUInt32(payload, 40).ToString(CultureInfo.InvariantCulture), BitConverter.ToUInt32(payload, 44).ToString(CultureInfo.InvariantCulture),
            label, BitConverter.ToUInt32(payload, 32).ToString(CultureInfo.InvariantCulture), decoded);
    }

    // A SID at the offset, in its string form, the offset moved past it; null where the payload ends before it does.
    private static string? Sid(byte[] payload, ref int at)
    {
        if (at + 8 > payload.Length)
        {
            return null;
        }

        int count = payload[at + 1];
        int length = 8 + (4 * count);
        if (at + length > payload.Length)
        {
            return null;
        }

        ulong authority = 0;
        for (int index = 2; index < 8; index++)
        {
            authority = (authority << 8) | payload[at + index];
        }

        var text = new System.Text.StringBuilder(string.Create(CultureInfo.InvariantCulture, $"S-{payload[at]}-{authority}"));
        for (int index = 0; index < count; index++)
        {
            text.Append(CultureInfo.InvariantCulture, $"-{BitConverter.ToUInt32(payload, at + 8 + (4 * index))}");
        }

        at += length;
        return text.ToString();
    }

    // A null-terminated UTF-16 string at the offset, the offset moved past its terminator; null where it is empty.
    private static string? Text(byte[] payload, ref int at)
    {
        int end = at;
        while (end + 1 < payload.Length && (payload[end] != 0 || payload[end + 1] != 0))
        {
            end += 2;
        }

        string text = System.Text.Encoding.Unicode.GetString(payload, at, Math.Max(0, end - at));
        at = Math.Min(payload.Length, end + 2);
        return text.Length == 0 ? null : text;
    }

    // Every running process's creation time and package, as the application model gives it; unreadable ones are left out.
    private static unsafe Dictionary<int, (long Creation, string? Package)> Packages()
    {
        var packages = new Dictionary<int, (long, string?)>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                nint handle = OpenProcess(QueryLimitedInformation, false, process.Id);
                if (handle == 0)
                {
                    continue;
                }

                try
                {
                    if (!GetProcessTimes(handle, out long creation, out _, out _, out _))
                    {
                        continue;
                    }

                    uint length = 0;
                    string? package = null;
                    if (GetPackageFullName(handle, &length, null) == ErrorInsufficientBuffer)
                    {
                        char[] buffer = new char[length];
                        fixed (char* text = buffer)
                        {
                            package = GetPackageFullName(handle, &length, text) == 0 ? new string(buffer, 0, (int)Math.Max(0, length - 1)) : null;
                        }
                    }

                    packages[process.Id] = (creation, package);
                }
                finally
                {
                    _ = CloseHandle(handle);
                }
            }
        }

        return packages;
    }

    /// <summary>A process start or rundown: its event, version, process, creation time and the enrichment fields it carried.</summary>
    private sealed record Record(int Id, int Version, int ProcessId, long CreateFileTime, string? PackageFullName, string? PackageRelativeAppId,
        string? ElevationType, string? IsElevated, string? MandatoryLabel, string? SessionId, string? DecodedPackageFullName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetPackageFullName(nint process, uint* length, char* packageFullName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
