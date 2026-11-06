using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.ProcessProbe;

/// <summary>
/// FX-PROC-001 recorded through the product (ADR-094's second addendum): each run is the product's own <c>icat record</c>
/// under the explore profile, which collects Kernel-Process as every capture does, the workload started once its session
/// runs, and the session's process instances read back by <c>icat processes --json</c>. Each child's instance, found by the
/// PID and creation time the workload read, is set against the package and session the application model and its token
/// gave it; and every process the probe read by the application model once the capture's session ran is set against the
/// instance the session holds of it, by its PID and creation time. Each run's session is read and then deleted, as it holds
/// every process on the machine; what is kept is each run's truth, the children's instances by role and counts - never a
/// PID or a package the machine has installed.
/// </summary>
internal static partial class Product
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<Dictionary<string, object?>> RunAsync(string icat, string workload, string output, int runs, int duration)
    {
        var results = new List<Dictionary<string, object?>>();
        for (int run = 1; run <= runs; run++)
        {
            string folder = Path.Combine(output, "run-" + run.ToString(CultureInfo.InvariantCulture));
            string session = Path.Combine(Path.GetTempPath(), "proc-product-" + Guid.NewGuid().ToString("N")[..8]);
            string truth = Path.Combine(folder, "truth");
            Directory.CreateDirectory(folder);
            try
            {
                Dictionary<string, object?> health;
                Dictionary<int, (long Creation, string? Package)> asCaptureBegan;
                using (Recording recording = await Recording.StartAsync(icat, session, duration).ConfigureAwait(false))
                {
                    // Every running process's package as the application model gives it, once the capture's session runs: its
                    // rundown, or a process's own start since, names each in the session.
                    asCaptureBegan = Measure.Packages();
                    using (Process started = Process.Start(new ProcessStartInfo(workload, ["process-package", "--truth", truth])
                        { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("The truth workload could not be started."))
                    {
                        await FinishedAsync(started).ConfigureAwait(false);
                    }

                    health = await recording.FinishAsync().ConfigureAwait(false);
                }

                JsonElement[] instances = await InstancesAsync(icat, session).ConfigureAwait(false);
                using JsonDocument scenario = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(truth, "scenario.json")).ConfigureAwait(false));
                var children = new List<Dictionary<string, object?>>();
                var kept = new List<Dictionary<string, object?>>();
                foreach (JsonElement child in scenario.RootElement.GetProperty("children").EnumerateArray())
                {
                    string role = child.GetProperty("role").GetString()!;
                    JsonElement[] found = Of(instances, child.GetProperty("processId").GetInt32(), child.GetProperty("creationFileTime").GetInt64());
                    JsonElement? instance = found.Length == 1 ? found[0] : null;
                    string? truthPackage = child.GetProperty("packageFullName").ValueKind == JsonValueKind.Null ? null : child.GetProperty("packageFullName").GetString();
                    string? recorded = instance is { } one ? Text(one, "packageFullName") : null;
                    children.Add(new()
                    {
                        ["role"] = role,
                        ["instancesFound"] = found.Length,
                        ["witness"] = instance is { } witnessed ? Text(witnessed, "witness") : null,
                        ["truthPackaged"] = truthPackage is not null,
                        ["packageSame"] = instance is not null && string.Equals(recorded, truthPackage ?? string.Empty, StringComparison.Ordinal),
                        ["sessionSame"] = instance is { } sessioned && sessioned.GetProperty("sessionId").ValueKind == JsonValueKind.Number
                            && sessioned.GetProperty("sessionId").GetUInt32() == child.GetProperty("sessionId").GetUInt32(),
                    });

                    // The child's instance as icat processes gave it, by role, kept beside the run's truth.
                    if (instance is { } named)
                    {
                        kept.Add(new()
                        {
                            ["role"] = role,
                            ["witness"] = Text(named, "witness"),
                            ["lifecycleEpoch"] = named.GetProperty("lifecycleEpoch").GetUInt32(),
                            ["createFileTime"] = named.GetProperty("createFileTime").GetInt64(),
                            ["sessionId"] = named.GetProperty("sessionId").ValueKind == JsonValueKind.Number ? named.GetProperty("sessionId").GetUInt32() : null,
                            ["packageFullName"] = recorded,
                        });
                    }
                }

                File.WriteAllText(Path.Combine(folder, "children.json"), JsonSerializer.Serialize(kept, Indented));
                results.Add(new()
                {
                    ["run"] = run,
                    ["health"] = health,
                    ["children"] = children,
                    ["processesAsTheCaptureBegan"] = AsTheCaptureBegan(asCaptureBegan, instances),
                    ["instances"] = new Dictionary<string, object?>
                    {
                        ["all"] = instances.Length,
                        ["packaged"] = instances.Count(static item => Text(item, "packageFullName") is { Length: > 0 }),
                        ["inNone"] = instances.Count(static item => Text(item, "packageFullName") is { Length: 0 }),
                        ["unknown"] = instances.Count(static item => Text(item, "packageFullName") is null),
                        ["unknownWitnessedAtTheirExitOrActivityAlone"] = instances.Count(static item => Text(item, "packageFullName") is null
                            && Text(item, "witness") is "ExitOnly" or "ActivityOnly"),
                    },
                });
            }
            finally
            {
                if (Directory.Exists(session))
                {
                    Directory.Delete(session, recursive: true);
                }
            }
        }

        return new()
        {
            ["schema"] = "intercat.process-package-product.v1",
            ["scenario"] = "FX-PROC-001",
            ["question"] = "Whether each process instance a session recorded through the product by icat record under the explore "
                + "profile names the package the application model gave its process - FX-PROC-001's packaged child its package, its "
                + "unpackaged child none - and the terminal session its token gave it, and every process the probe read by the "
                + "application model as the capture began the package it read, as icat processes reads the session back (ADR-094).",
            ["measuredUtc"] = DateTimeOffset.UtcNow,
            ["build"] = InterCat.Capture.Windows.CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            ["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}",
            ["profile"] = "explore",
            ["durationSeconds"] = duration,
            ["runs"] = results,
            ["notes"] = new[]
            {
                "Each capture is the product's own icat record process, recording into a scratch session that is deleted once icat processes has read it back.",
                "A run's health is the capture's own report: what its sources lost, what its queue dropped and what it could not decode.",
                "A process the probe read is found in the session by its PID and creation time; one whose instance the session holds names "
                    + "its package or none, and one the session does not hold - begun and ended before the capture's process records ran, "
                    + "or the probe unable to read it - is counted apart, never guessed.",
                "An instance witnessed only by its exit or its activity carries no start or rundown record, so its package is unknown, as R3 asks.",
            },
        };
    }

    // The processes the probe read as the capture began, each found by its PID and creation time among the session's instances,
    // and whether the instance named the package the application model gave it; counts only.
    private static Dictionary<string, object?> AsTheCaptureBegan(Dictionary<int, (long Creation, string? Package)> read, JsonElement[] instances)
    {
        int found = 0, notInSession = 0, ambiguous = 0, packagedAgreeing = 0, unpackagedAgreeing = 0, disagreeing = 0, unknown = 0;
        foreach ((int process, (long creation, string? package)) in read)
        {
            JsonElement[] matched = Of(instances, process, creation);
            if (matched.Length != 1)
            {
                notInSession += matched.Length == 0 ? 1 : 0;
                ambiguous += matched.Length > 1 ? 1 : 0;
                continue;
            }

            found++;
            string? named = Text(matched[0], "packageFullName");
            if (named is null)
            {
                unknown++;
            }
            else if (!string.Equals(named, package ?? string.Empty, StringComparison.Ordinal))
            {
                disagreeing++;
            }
            else if (package is null)
            {
                unpackagedAgreeing++;
            }
            else
            {
                packagedAgreeing++;
            }
        }

        return new()
        {
            ["read"] = read.Count,
            ["found"] = found,
            ["packagedAgreeing"] = packagedAgreeing,
            ["unpackagedAgreeing"] = unpackagedAgreeing,
            ["disagreeing"] = disagreeing,
            ["unknownInTheSession"] = unknown,
            ["notInTheSession"] = notInSession,
            ["moreThanOneInstance"] = ambiguous,
        };
    }

    // The instances of a PID whose creation time is the one given.
    private static JsonElement[] Of(JsonElement[] instances, int process, long creation) =>
        [.. instances.Where(item => item.GetProperty("processId").GetInt32() == process
            && item.GetProperty("createFileTime").ValueKind == JsonValueKind.Number
            && item.GetProperty("createFileTime").GetInt64() == creation)];

    private static string? Text(JsonElement instance, string name) =>
        instance.GetProperty(name).ValueKind == JsonValueKind.String ? instance.GetProperty(name).GetString() : null;

    // Every process instance of the session, as icat processes --json lists them: each one's process document.
    private static async Task<JsonElement[]> InstancesAsync(string icat, string session)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(icat, ["processes", session, "--json"])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        Task<string> error = process.StandardError.ReadToEndAsync();
        string json = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"icat processes exited with {process.ExitCode}: {await error.ConfigureAwait(false)}");
        }

        using JsonDocument document = JsonDocument.Parse(json[json.IndexOf('{', StringComparison.Ordinal)..]);
        return [.. document.RootElement.GetProperty("instances").EnumerateArray().Select(static item => item.GetProperty("process").Clone())];
    }

    private static async Task FinishedAsync(Process started)
    {
        // A workload that has not finished in five minutes never will: it is stopped, and the run with it.
        using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await started.WaitForExitAsync(patience.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            started.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The truth workload did not finish in five minutes, so it was stopped.");
        }

        if (started.ExitCode != 0)
        {
            throw new InvalidOperationException($"The truth workload exited with {started.ExitCode}.");
        }
    }

    [GeneratedRegex(@"under owned session (InterCat-[A-Za-z0-9-]+)\.")]
    private static partial Regex SessionName();

    /// <summary>One product capture: an <c>icat record</c> process and what it wrote.</summary>
    internal sealed class Recording : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> output;

        private Recording(Process process, Task<string> output)
        {
            this.process = process;
            this.output = output;
        }

        /// <summary>Starts the capture and returns once its session runs, so the workload starts inside it.</summary>
        public static async Task<Recording> StartAsync(string icat, string session, int duration)
        {
            var named = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(icat, ["record", session, "--profile", "explore", "--duration", duration.ToString(CultureInfo.InvariantCulture), "--json"])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.ErrorDataReceived += (_, line) =>
            {
                if (line.Data is { } text && SessionName().Match(text) is { Success: true } match)
                {
                    named.TrySetResult(match.Groups[1].Value);
                }
            };
            process.Start();
            process.BeginErrorReadLine();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task finished = await Task.WhenAny(named.Task, process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(120))).ConfigureAwait(false);
            if (finished != named.Task)
            {
                string said = process.HasExited ? await output.ConfigureAwait(false) : string.Empty;
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw new InvalidOperationException($"icat record --profile explore never announced its session. {said}");
            }

            var recording = new Recording(process, output);

            // The name is announced before the session starts: wait until ETW lists it running.
            string sessionName = named.Task.Result;
            var waited = Stopwatch.StartNew();
            while (!TraceEventSession.GetActiveSessionNames().Contains(sessionName, StringComparer.OrdinalIgnoreCase))
            {
                if (process.HasExited || waited.Elapsed > TimeSpan.FromSeconds(60))
                {
                    recording.Dispose();
                    throw new InvalidOperationException("icat record --profile explore did not start its session.");
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            return recording;
        }

        /// <summary>Waits for the capture to stop and publish, and reads the health it reported.</summary>
        public async Task<Dictionary<string, object?>> FinishAsync()
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            string json = await output.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"icat record exited with {process.ExitCode}: {json}");
            }

            using JsonDocument document = JsonDocument.Parse(json[json.IndexOf('{', StringComparison.Ordinal)..]);
            JsonElement health = document.RootElement.GetProperty("health");
            long Count(string name) => health.TryGetProperty(name, out JsonElement value) ? value.GetInt64() : 0;
            return new()
            {
                ["observedRecords"] = Count("observedRecords"),
                ["admittedRecords"] = Count("admittedRecords"),
                ["providerReportedEventLoss"] = Count("providerReportedEventLoss"),
                ["consumerReportedBufferLoss"] = Count("consumerReportedBufferLoss"),
                ["applicationDrops"] = Count("applicationDrops"),
                ["undecodableRecords"] = Count("undecodableRecords"),
            };
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }
    }
}
