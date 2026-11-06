using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.UiMessageProbe;

/// <summary>
/// FX-WMSG-001 recorded through the product (ADR-088's addendum), FX-WMSG-002, its messages to windows made one after another
/// (its second addendum), or FX-WMSG-003, messages a capture's start and stop cut (its third): each run is the product's own
/// <c>icat record</c> under the opt-in window messages profile - the workload started once its session runs, or for
/// FX-WMSG-003 the session begun into the workload's run and ended before it - and the ledger of the workload's window message
/// records read from the session it published and evaluated against the run's truth within the capture's window. Each run's
/// session is read and then deleted, as it holds every window's messages on the machine; what is kept is each run's ledger,
/// evaluation and truth, and the capture's own counters.
/// </summary>
internal static partial class Product
{
    public static async Task<Dictionary<string, object?>> RunAsync(string icat, string workload, string output, int runs, int copies,
        int posts, int bytes, int generations, int handleMilliseconds, int captureAfter, int duration)
    {
        string fixture = handleMilliseconds > 0 ? "FX-WMSG-003" : generations > 1 ? "FX-WMSG-002" : "FX-WMSG-001";
        var results = new List<Dictionary<string, object?>>();
        for (int run = 1; run <= runs; run++)
        {
            string folder = Path.Combine(output, "run-" + run.ToString(CultureInfo.InvariantCulture));
            string session = Path.Combine(Path.GetTempPath(), "wmsg-product-" + Guid.NewGuid().ToString("N")[..8]);
            string truth = Path.Combine(folder, "truth");
            Directory.CreateDirectory(folder);
            string[] arguments =
            [
                "window-message", "--truth", truth, "--copies", copies.ToString(CultureInfo.InvariantCulture),
                "--posts", posts.ToString(CultureInfo.InvariantCulture), "--bytes", bytes.ToString(CultureInfo.InvariantCulture),
                "--generations", generations.ToString(CultureInfo.InvariantCulture),
                "--handle-ms", handleMilliseconds.ToString(CultureInfo.InvariantCulture),
            ];
            try
            {
                Dictionary<string, object?> health;
                if (handleMilliseconds > 0)
                {
                    // FX-WMSG-003: the workload first, then the capture begun into it and ended before it, so each of its
                    // edges falls while messages are in flight.
                    using Process started = StartWorkload(workload, arguments);
                    await Task.Delay(TimeSpan.FromSeconds(captureAfter)).ConfigureAwait(false);
                    using (Recording recording = await Recording.StartAsync(icat, session, duration).ConfigureAwait(false))
                    {
                        health = await recording.FinishAsync().ConfigureAwait(false);
                    }

                    await FinishedAsync(started).ConfigureAwait(false);
                }
                else
                {
                    using Recording recording = await Recording.StartAsync(icat, session, duration).ConfigureAwait(false);
                    using (Process started = StartWorkload(workload, arguments))
                    {
                        await FinishedAsync(started).ConfigureAwait(false);
                    }

                    health = await recording.FinishAsync().ConfigureAwait(false);
                }

                (WindowMessageCoverageResult result, long captureFrom, long captureTo) = SessionLedger.Write(session, truth, Path.Combine(folder, "ledger.csv"));
                results.Add(new()
                {
                    ["run"] = run,
                    ["health"] = health,
                    ["tier"] = result.Assessment.Tier.ToString(),
                    ["truthMessages"] = result.Measurement.TruthOperations,
                    ["truthWindows"] = result.Measurement.TruthResources,
                    ["resolvedMemberships"] = result.Measurement.ResolvedMemberships,
                    ["observed"] = result.Measurement.TruthOperationsWithAdmittedObservation,
                    ["withEveryEnd"] = result.MessagesWithEveryEnd,
                    ["peerAttributions"] = result.Measurement.PeerAttributions,
                    ["falsePeerAttributions"] = result.Measurement.FalsePeerAttributions,
                    ["beyondTruth"] = result.MessagesBeyondTruth,
                    ["outsideScope"] = result.ObservationsOutsideScope,
                    ["outsideCapture"] = result.MessagesOutsideCapture,
                    ["begunBeforeCapture"] = result.MessagesBegunBeforeCapture,
                    ["captureFromUtcTicks"] = captureFrom,
                    ["captureToUtcTicks"] = captureTo,
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
            ["question"] = fixture switch
            {
                "FX-WMSG-003" => "Whether FX-WMSG-003's messages, each taking the receiver a while, recorded through the product by icat record "
                    + "under the opt-in window messages profile begun into the run and ended before it, are ranked within the capture's "
                    + "window alone - one begun before it or after it matched to none and ends without their start naming none, one begun "
                    + "inside but ended after it ranked with its missing ends said - as ADR-088 measures them.",
                "FX-WMSG-002" => "Whether FX-WMSG-002's messages to windows made one after another, recorded through the product by icat record under "
                    + "the opt-in window messages profile, are each one number with every end in its own process, its other end the "
                    + "receiver, every window's close among the posts where the sender posted it, as ADR-088 measures them.",
                _ => "Whether FX-WMSG-001's messages, recorded through the product by icat record under the opt-in window messages "
                    + "profile, are each one number with every end in its own process, its other end the receiver, as ADR-088 measures them.",
            },
            ["scenario"] = fixture,
            ["truth"] = fixture switch
            {
                "FX-WMSG-003" => new Dictionary<string, object?>
                {
                    ["copiesSent"] = copies, ["messagesPosted"] = posts, ["bytes"] = bytes, ["handleMilliseconds"] = handleMilliseconds,
                },
                "FX-WMSG-002" => new Dictionary<string, object?>
                {
                    ["windows"] = generations, ["copiesSentEach"] = copies, ["messagesPostedEach"] = posts, ["bytes"] = bytes,
                },
                _ => new Dictionary<string, object?> { ["copiesSent"] = copies, ["messagesPosted"] = posts, ["bytes"] = bytes },
            },
            ["profile"] = "window-messages",
            ["captureAfterSeconds"] = handleMilliseconds > 0 ? captureAfter : 0,
            ["durationSeconds"] = duration,
            ["runs"] = results,
            ["notes"] = new[]
            {
                "Each capture is the product's own icat record process, recording into a scratch session that is deleted once its ledger is written.",
                "A run's health is the capture's own report: what its sources lost, what its queue dropped, and what a source's gate did not admit, by reason.",
            },
        };
    }

    private static Process StartWorkload(string workload, string[] arguments) =>
        Process.Start(new ProcessStartInfo(workload, arguments) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("The truth workload could not be started.");

    private static async Task FinishedAsync(Process started)
    {
        await started.WaitForExitAsync().ConfigureAwait(false);
        if (started.ExitCode != 0)
        {
            throw new InvalidOperationException($"The truth workload exited with {started.ExitCode}.");
        }
    }

    [GeneratedRegex(@"under owned session (InterCat-[A-Za-z0-9-]+)\.")]
    private static partial Regex SessionName();

    /// <summary>One product capture: an <c>icat record</c> process and what it wrote.</summary>
    private sealed class Recording : IDisposable
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
                StartInfo = new ProcessStartInfo(
                    icat,
                    ["record", session, "--profile", "window-messages", "--duration", duration.ToString(CultureInfo.InvariantCulture), "--json"])
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
                throw new InvalidOperationException($"icat record --profile window-messages never announced its session. {said}");
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
                    throw new InvalidOperationException("icat record --profile window-messages did not start its session.");
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
                ["gateOmissionsNotCounted"] = Count("gateOmissionsNotCounted"),
                ["gateOmissionsNoFollowedResource"] = Count("gateOmissionsNoFollowedResource"),
                ["gateOmissionsBeyondBound"] = Count("gateOmissionsBeyondBound"),
                ["gateOmissionsOutOfOrder"] = Count("gateOmissionsOutOfOrder"),
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
