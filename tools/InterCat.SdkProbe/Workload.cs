using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SdkProbe;

/// <summary>What every measurement of the probe's runs: the truth workload, waited for, and the product's capture around it.</summary>
internal static partial class Workload
{
    internal static async Task FinishedAsync(Process started)
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
    internal static partial Regex SessionName();

    /// <summary>One product capture: an <c>icat record</c> process and what it wrote.</summary>
    internal sealed class Recording : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> output;
        private readonly string session;

        private Recording(Process process, Task<string> output, string session)
        {
            this.process = process;
            this.output = output;
            this.session = session;
        }

        /// <summary>
        /// Starts the capture and returns once its session runs, so the workload starts inside it: under the opt-in sdk
        /// profile, or under <paramref name="request"/> - a profile and its options, such as a content request's.
        /// </summary>
        public static async Task<Recording> StartAsync(string icat, string session, int duration, IReadOnlyList<string>? request = null)
        {
            var named = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(icat,
                    ["record", session, .. request ?? ["--profile", "sdk"], "--duration", duration.ToString(CultureInfo.InvariantCulture), "--json"])
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
                throw new InvalidOperationException($"icat record never announced its session. {said}");
            }

            // The name is announced before the session starts: wait until ETW lists it running.
            string sessionName = named.Task.Result;
            var recording = new Recording(process, output, sessionName);
            var waited = Stopwatch.StartNew();
            while (!TraceEventSession.GetActiveSessionNames().Contains(sessionName, StringComparer.OrdinalIgnoreCase))
            {
                if (process.HasExited || waited.Elapsed > TimeSpan.FromSeconds(60))
                {
                    recording.Dispose();
                    throw new InvalidOperationException("icat record did not start its session.");
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            return recording;
        }

        /// <summary>Waits for the capture to stop and publish, and reads the health it reported.</summary>
        /// <remarks>
        /// A capture that completed is read whether it kept every record (exit 0) or published a partial result, its health
        /// saying what it lost (exit 1): a loss is a measurement. Any other exit is a failure of the run.
        /// </remarks>
        public async Task<Dictionary<string, object?>> FinishAsync()
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            string json = await output.ConfigureAwait(false);
            if (process.ExitCode is not (0 or 1))
            {
                throw new InvalidOperationException($"icat record exited with {process.ExitCode}: {json}");
            }

            using JsonDocument document = JsonDocument.Parse(json[json.IndexOf('{', StringComparison.Ordinal)..]);
            JsonElement health = document.RootElement.GetProperty("health");
            long Count(string name) => health.TryGetProperty(name, out JsonElement value) ? value.GetInt64() : 0;
            return new()
            {
                ["exitCode"] = process.ExitCode,
                ["lossFree"] = health.TryGetProperty("isLossFree", out JsonElement free) && free.GetBoolean(),
                ["observedRecords"] = Count("observedRecords"),
                ["admittedRecords"] = Count("admittedRecords"),
                ["providerReportedEventLoss"] = Count("providerReportedEventLoss"),
                ["consumerReportedBufferLoss"] = Count("consumerReportedBufferLoss"),
                ["applicationDrops"] = Count("applicationDrops"),
                ["undecodableRecords"] = Count("undecodableRecords"),
            };
        }

        /// <summary>
        /// Lets a capture a failed run leaves behind end as it would, its duration out; one that will not is killed, and its
        /// ETW session - which outlives the process that owned it - is stopped by its name, so no session of a run is left
        /// listening to slow the machine or tell a later run's processes their schema again.
        /// </summary>
        public void Dispose()
        {
            if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                if (TraceEventSession.GetActiveSessionNames().Contains(session, StringComparer.OrdinalIgnoreCase))
                {
                    using var orphan = new TraceEventSession(session, TraceEventSessionOptions.Attach);
                    orphan.Stop(noThrow: true);
                }
            }

            process.Dispose();
        }
    }
}
