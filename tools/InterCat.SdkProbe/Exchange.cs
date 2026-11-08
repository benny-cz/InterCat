using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.SdkProbe;

/// <summary>
/// The alignment by shared operations qualified on one host (ADR-108): two processes start operations under each other's
/// through InterCat's SDK, each recorded by a capture of its own - the product's own <c>icat record</c> under a content
/// request naming that process alone, so the capture-side process filter keeps the other's records out, and the exchange
/// publishing no message, so no content is kept - and an investigation aligns the two sessions exactly, by the boot both
/// recorded, and by their shared operations, under no drift and under 50 ppm. Every ordering must hold in the exact
/// alignment - each start after its parent's - and each bound must hold the exact offset. The sessions are deleted once
/// read; what is kept is counts and nanoseconds, never a process ID, path or name of the machine.
/// </summary>
internal static class Exchange
{
    private const string Source = "etw/eventsource/InterCat-Sdk";

    /// <summary>The most content a capture keeps before it stops; the exchange publishes no message, so it keeps none.</summary>
    private const long SessionLimit = 16L * 1024 * 1024;

    /// <summary>The drift bounds each alignment by shared operations is made under: none, as one counter's, and a crystal's.</summary>
    private static readonly double[] Drifts = [0, 50];

    public static async Task<Dictionary<string, object?>> RunAsync(string icat, string workload, string output, int rounds, int duration)
    {
        string truth = Path.Combine(output, "evidence");
        string scratch = Path.Combine(Path.GetTempPath(), "sdk-exchange-" + Guid.NewGuid().ToString("N")[..8]);
        string clientSession = Path.Combine(scratch, "client");
        string serverSession = Path.Combine(scratch, "server");
        Directory.CreateDirectory(truth);
        Directory.CreateDirectory(scratch);
        Process? server = null;
        Process? client = null;
        try
        {
            // The server says its pipe; the client waits for a line on its input, so both captures name both processes before
            // either starts an operation.
            server = Start(workload, ["sdk-exchange-server", "--truth", truth], input: false);
            string? listening = await server.StandardOutput.ReadLineAsync().ConfigureAwait(false);
            if (listening is null || !listening.StartsWith("listening ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The exchange's server exited before it listened.");
            }

            client = Start(workload,
                ["sdk-exchange-client", "--truth", truth, "--endpoint", listening["listening ".Length..], "--rounds",
                    rounds.ToString(CultureInfo.InvariantCulture), "--wait-for-start"],
                input: true);
            Dictionary<string, object?> clientHealth;
            Dictionary<string, object?> serverHealth;
            using (Workload.Recording clientCapture = await Workload.Recording.StartAsync(icat, clientSession, duration, Request(client)).ConfigureAwait(false))
            using (Workload.Recording serverCapture = await Workload.Recording.StartAsync(icat, serverSession, duration, Request(server)).ConfigureAwait(false))
            {
                await client.StandardInput.WriteLineAsync("start").ConfigureAwait(false);
                await client.StandardInput.FlushAsync().ConfigureAwait(false);
                await Workload.FinishedAsync(client).ConfigureAwait(false);
                await Workload.FinishedAsync(server).ConfigureAwait(false);
                clientHealth = await clientCapture.FinishAsync().ConfigureAwait(false);
                serverHealth = await serverCapture.FinishAsync().ConfigureAwait(false);
            }

            // The truth: one boot's counter, which both captures read, places the server's instants in the client's exactly.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string exact = Path.Combine(scratch, "exact" + InvestigationWorkspace.Extension);
            (Guid clientId, Guid serverId) = Investigation(exact, clientSession, serverSession, now);
            WorkspaceAlignment boot = InvestigationWorkspace.AlignSameBoot(exact, serverId, clientId, null, now);
            long offset = checked(boot.ReferenceNanoseconds!.Value - boot.SessionNanoseconds!.Value);
            long rounding = boot.WithinNanoseconds!.Value;

            // Each ordering's slack there: how long after the start it was made under, by that one counter, each start came.
            IReadOnlyList<CooperativeOperationStart> serverStarts = Starts(serverSession);
            IReadOnlyList<CooperativeOperationStart> clientStarts = Starts(clientSession);
            IReadOnlyList<MarkerOrdering> orderings = SharedOperationMarkers.Between(serverStarts, clientStarts);
            long[] serverAfter = [.. orderings.Where(ordering => ordering.Order == MarkerOrder.MemberAfter)
                .Select(ordering => checked(ordering.MemberNanoseconds + offset - ordering.ReferenceNanoseconds)).Order()];
            long[] clientAfter = [.. orderings.Where(ordering => ordering.Order == MarkerOrder.ReferenceAfter)
                .Select(ordering => checked(ordering.ReferenceNanoseconds - ordering.MemberNanoseconds - offset)).Order()];
            bool ordered = serverAfter.All(slack => slack >= -rounding) && clientAfter.All(slack => slack >= -rounding);

            // The same two sessions aligned by their shared operations alone, under each drift bound.
            var bounds = new List<Dictionary<string, object?>>();
            foreach (double drift in Drifts)
            {
                string path = Path.Combine(scratch, "shared-" + drift.ToString(CultureInfo.InvariantCulture) + InvestigationWorkspace.Extension);
                _ = Investigation(path, clientSession, serverSession, now);
                WorkspaceAlignment shared = InvestigationWorkspace.AlignBySharedOperations(path, serverId, clientId, drift, null, now);
                long error = checked(shared.ReferenceNanoseconds!.Value - shared.SessionNanoseconds!.Value - offset);
                bounds.Add(new()
                {
                    ["driftPartsPerMillion"] = drift,
                    ["startsUnderReference"] = shared.StartsUnderReference,
                    ["startsUnderMember"] = shared.StartsUnderMember,
                    ["withinNanoseconds"] = shared.WithinNanoseconds,
                    ["errorNanoseconds"] = error,
                    ["held"] = Math.Abs(error) <= checked(shared.WithinNanoseconds!.Value + rounding),
                });
            }

            bool held = ordered && bounds.All(bound => (bool)bound["held"]!);
            return new()
            {
                ["qualification"] = "shared-operations-alignment",
                ["question"] = "Whether two captures of one host, each the product's own icat record scoped to one of two processes that start "
                    + "operations under each other's through InterCat's SDK, align by their shared operations within a bound that holds the "
                    + "exact offset one boot's counter gives them, every ordering holding there - each start after the one it was made under.",
                ["held"] = held,
                ["rounds"] = rounds,
                ["durationSeconds"] = duration,
                ["operationsStarted"] = new Dictionary<string, int> { ["client"] = Started(truth, "client"), ["server"] = Started(truth, "server") },
                ["startsRecorded"] = new Dictionary<string, int> { ["client"] = clientStarts.Count, ["server"] = serverStarts.Count },
                ["orderings"] = new Dictionary<string, int> { ["serverAfterClient"] = serverAfter.Length, ["clientAfterServer"] = clientAfter.Length },
                ["sameBootWithinNanoseconds"] = rounding,
                ["everyOrderingHeld"] = ordered,
                ["slackNanoseconds"] = new Dictionary<string, object?> { ["serverAfterClient"] = Spread(serverAfter), ["clientAfterServer"] = Spread(clientAfter) },
                ["bounds"] = bounds,
                ["captures"] = new Dictionary<string, object?> { ["client"] = clientHealth, ["server"] = serverHealth },
                ["notes"] = new[]
                {
                    "Each capture is the product's own icat record under a content request naming one process, so its capture-side process filter keeps the other's records out; the exchange publishes no message, so no content is kept.",
                    "The server's session is aligned to the client's: the exact alignment by the boot both recorded is the truth each bound is held to.",
                    "A slack is how long after the start it was made under each start came, by the one counter both captures read.",
                    "Counts and nanoseconds only: no process ID, path or name of the machine is kept, and both sessions are deleted once read.",
                },
            };
        }
        finally
        {
            foreach (Process? process in new[] { client, server })
            {
                if (process is not null)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.Dispose();
                }
            }

            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>A content request naming <paramref name="process"/> alone: the SDK's records of it and no other.</summary>
    private static string[] Request(Process process) =>
    [
        "--profile", "content", "--source", Source, "--mechanism", "sdk",
        "--pid", process.Id.ToString(CultureInfo.InvariantCulture), "--channel", "*",
        "--max-record-bytes", "4096", "--max-session-bytes", SessionLimit.ToString(CultureInfo.InvariantCulture), "--inspection", "hex-text",
    ];

    /// <summary>An investigation of the two sessions, the client's first.</summary>
    private static (Guid Client, Guid Server) Investigation(string path, string clientSession, string serverSession, DateTimeOffset now)
    {
        InvestigationWorkspace.Create(path, now);
        return (InvestigationWorkspace.Add(path, clientSession, now).SessionId, InvestigationWorkspace.Add(path, serverSession, now).SessionId);
    }

    /// <summary>Every operation start a session's records hold, read as the investigation reads them.</summary>
    private static IReadOnlyList<CooperativeOperationStart> Starts(string directory)
    {
        SessionStore store = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(directory));
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("The session names no clock.");
        return SharedOperationMarkers.Starts(SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name)), clock);
    }

    /// <summary>How many operations a side of the exchange says it started.</summary>
    private static int Started(string truth, string side)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, $"operations-{side}.json")));
        return document.RootElement.GetProperty("operations").GetArrayLength();
    }

    /// <summary>The least, median and most of sorted slacks; null for none.</summary>
    private static Dictionary<string, long>? Spread(long[] sorted) => sorted.Length == 0
        ? null
        : new() { ["least"] = sorted[0], ["median"] = sorted[sorted.Length / 2], ["most"] = sorted[^1] };

    private static Process Start(string workload, IReadOnlyList<string> arguments, bool input)
    {
        var start = new ProcessStartInfo(workload)
        {
            RedirectStandardInput = input,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The exchange's workload did not start.");
    }
}
