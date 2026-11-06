using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SectionProbe;

/// <summary>
/// FX-SECT-001 recorded through the product (ADR-092's addendum): each run is the product's own <c>icat record</c> under the
/// opt-in sections profile, the workload started once its session runs, and the ledger of the run's section records read from
/// the session it published and evaluated against the run's truth. Each run's session is read and then deleted, as it holds
/// every process's views and section handles on the machine; what is kept is each run's ledger, evaluation and truth, and the
/// capture's own counters. FX-SECT-002 runs the same in generations, watched by the probe's own session too; FX-SECT-003's
/// workload holds its sections while the capture is launched (<see cref="RunHeldAsync"/>).
/// </summary>
internal static partial class Product
{
    public static async Task<Dictionary<string, object?>> RunAsync(string icat, string workload, string output, int runs, int rounds, int bytes, int generations, int duration)
    {
        string fixture = generations > 1 ? "FX-SECT-002" : "FX-SECT-001";
        var results = new List<Dictionary<string, object?>>();
        for (int run = 1; run <= runs; run++)
        {
            string folder = Path.Combine(output, "run-" + run.ToString(CultureInfo.InvariantCulture));
            string session = Path.Combine(Path.GetTempPath(), "sect-product-" + Guid.NewGuid().ToString("N")[..8]);
            string truth = Path.Combine(folder, "truth");
            Directory.CreateDirectory(folder);
            try
            {
                Dictionary<string, object?> health;
                Dictionary<string, object?>? watched = null;
                using (Recording recording = await Recording.StartAsync(icat, session, duration).ConfigureAwait(false))
                {
                    // FX-SECT-002's generations are watched by the probe's own session too, the only one that reads where the
                    // kernel made each section; it starts once the product's runs, and stops once the workload is done.
                    using (Generations? watch = generations > 1 ? await Generations.StartAsync().ConfigureAwait(false) : null)
                    {
                        using (Process started = Process.Start(new ProcessStartInfo(workload,
                            ["section-share", "--truth", truth, "--rounds", rounds.ToString(CultureInfo.InvariantCulture), "--view-bytes", bytes.ToString(CultureInfo.InvariantCulture),
                                "--generations", generations.ToString(CultureInfo.InvariantCulture)])
                            { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("The truth workload could not be started."))
                        {
                            await FinishedAsync(started).ConfigureAwait(false);
                        }

                        if (watch is not null)
                        {
                            watched = await watch.FinishAsync(truth).ConfigureAwait(false);
                        }
                    }

                    health = await recording.FinishAsync().ConfigureAwait(false);
                }

                SectionCoverageResult result = SessionLedger.Write(session, truth, Path.Combine(folder, "records.csv"));
                if (watched is not null)
                {
                    File.WriteAllText(Path.Combine(folder, "observed-by-the-probe.json"), JsonSerializer.Serialize(watched, Indented));
                }

                results.Add(new()
                {
                    ["run"] = run,
                    ["health"] = health,
                    ["tier"] = result.Assessment.Tier.ToString(),
                    ["truthSections"] = result.Measurement.TruthResources,
                    ["discoveredWithLifetime"] = result.Measurement.DiscoveredResourcesWithLifetime,
                    ["expectedMemberships"] = result.Measurement.ExpectedMemberships,
                    ["resolvedMemberships"] = result.Measurement.ResolvedMemberships,
                    ["falseMemberships"] = result.Measurement.FalsePeerAttributions,
                    ["truthOperations"] = result.Measurement.TruthOperations,
                    ["observed"] = result.Measurement.TruthOperationsWithAdmittedObservation,
                    ["recordsOfOtherProcessesOnItsSections"] = result.ObservationsOutsideScope,
                    ["gaps"] = result.Assessment.Gaps,
                    ["handleNumbers"] = result.Sections.Select(static section => section.SectionObjectNumber).OfType<long>().Distinct().Count(),
                    ["viewSectionNumbers"] = result.Sections.Select(static section => section.SectionNumber).OfType<long>().Distinct().Count(),
                    ["observedByTheProbe"] = watched,
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
            ["question"] = generations > 1
                ? $"Whether FX-SECT-002's sections, two a generation made and ended {generations} generations one after another in the same two "
                    + "processes, recorded through the product by icat record under the opt-in sections profile, are each found twice - by "
                    + "its handles' one number and by its views' one number - each a number of its own, bound to exactly the two processes, "
                    + "as ADR-092 measures them, where the probe's own session watching the same run counts the sections the kernel made "
                    + "at an address, or whose views it gave a value, an earlier generation's section held."
                : "Whether FX-SECT-001's two sections, recorded through the product by icat record under the opt-in sections profile, are "
                    + "each found twice - by its handles' one number from its creation to every close, and by its views' one number every "
                    + "mapping and unmapping - bound to exactly the two processes, as ADR-092 measures them.",
            ["scenario"] = fixture,
            ["truth"] = new Dictionary<string, object?> { ["sections"] = 2 * generations, ["generations"] = generations, ["openerRounds"] = rounds, ["viewBytes"] = bytes },
            ["profile"] = "sections",
            ["durationSeconds"] = duration,
            ["runs"] = results,
            ["notes"] = new[]
            {
                "Each capture is the product's own icat record process, recording into a scratch session that is deleted once its ledger is written.",
                "A run's health is the capture's own report: what its sources lost, what its queue dropped, and what a source's gate did not admit, by reason.",
                generations > 1
                    ? "Each run's generations were watched by the probe's own private system logger too, started once the capture's session "
                        + "ran: it alone reads where the kernel made each section - its object's address and its views' value - and writes "
                        + "counts, never an address."
                    : "Nothing but the product's capture recorded the run.",
            },
        };
    }

    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

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
                StartInfo = new ProcessStartInfo(icat, ["record", session, "--profile", "sections", "--duration", duration.ToString(CultureInfo.InvariantCulture), "--json"])
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
                throw new InvalidOperationException($"icat record --profile sections never announced its session. {said}");
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
                    throw new InvalidOperationException("icat record --profile sections did not start its session.");
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
