using System.Diagnostics;
using System.Text.Json;
using InterCat.Capture.Windows;

namespace InterCat.ProcessProbe;

/// <summary>
/// FX-SVC-002, the services a capture keeps, through the product (ADR-095's fourth addendum): each run is the product's own
/// <c>icat record</c> under the explore profile, and the probe reads the service control manager's list of the running services
/// and their hosts by its own call once the capture's session runs; once the capture has published, <c>icat processes --json</c>
/// reads the session back. Each service the probe read, by its host's PID and creation time, is set against the instance the
/// session holds of that host and the services it names it hosting as the capture started. Each run's session is read and
/// then deleted; what is kept is counts - never a PID or a service's name.
/// </summary>
internal static class ServicesProduct
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<Dictionary<string, object?>> RunAsync(string icat, string output, int runs, int duration)
    {
        var results = new List<Dictionary<string, object?>>();
        for (int run = 1; run <= runs; run++)
        {
            string session = Path.Combine(Path.GetTempPath(), "svc-product-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Dictionary<string, object?> health;
                IReadOnlyList<RunningService> read;
                using (Product.Recording recording = await Product.Recording.StartAsync(icat, session, duration).ConfigureAwait(false))
                {
                    // The probe's own read, as the capture's start read its: the truth each named service is set against.
                    read = RunningServices.Read();
                    health = await recording.FinishAsync().ConfigureAwait(false);
                }

                using JsonDocument listed = await ListedAsync(icat, session).ConfigureAwait(false);
                results.Add(Compare(run, health, read, listed.RootElement));
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
            ["schema"] = "intercat.service-product.v1",
            ["scenario"] = "FX-SVC-002",
            ["question"] = "Whether each running service the service control manager lists as a capture starts, recorded through the "
                + "product by icat record under the explore profile, is named by icat processes as hosted by the process instance "
                + "whose PID and creation time are its host's, as the capture started (ADR-095).",
            ["measuredUtc"] = DateTimeOffset.UtcNow,
            ["build"] = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            ["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}",
            ["profile"] = "explore",
            ["durationSeconds"] = duration,
            ["runs"] = results,
            ["notes"] = new[]
            {
                "Each capture is the product's own icat record process, recording into a scratch session that is deleted once icat processes has read it back.",
                "The probe reads the service control manager by its own call once the capture's session runs; it reads no InterCat state.",
                "A service whose host the probe could not open has no creation time to find its instance by, and is counted apart.",
            },
        };
    }

    // One run's comparison of the probe's list with the services the session names each instance hosting; counts only.
    private static Dictionary<string, object?> Compare(int run, Dictionary<string, object?> health, IReadOnlyList<RunningService> read, JsonElement listed)
    {
        var byHost = new Dictionary<(int ProcessId, long Created), HashSet<string>>();
        foreach (JsonElement item in listed.GetProperty("instances").EnumerateArray())
        {
            JsonElement process = item.GetProperty("process");
            if (process.GetProperty("createFileTime").ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var started = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item.GetProperty("services").ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement service in item.GetProperty("services").EnumerateArray())
                {
                    if (service.GetProperty("atStart").GetBoolean())
                    {
                        started.Add(service.GetProperty("name").GetString()!);
                    }
                }
            }

            byHost[(process.GetProperty("processId").GetInt32(), process.GetProperty("createFileTime").GetInt64())] = started;
        }

        int compared = 0, named = 0, notNamed = 0, noInstance = 0, hostUnread = 0;
        foreach (RunningService service in read)
        {
            if (service.HostCreatedUtc is not { } created)
            {
                hostUnread++;
                continue;
            }

            compared++;
            if (!byHost.TryGetValue((service.ProcessId, created.ToFileTime()), out HashSet<string>? hosted))
            {
                noInstance++;
            }
            else if (hosted.Contains(service.Name))
            {
                named++;
            }
            else
            {
                notNamed++;
            }
        }

        string[] moments = [.. listed.GetProperty("servicesRead").EnumerateArray().Select(static moment => moment.GetString()!)];
        return new()
        {
            ["run"] = run,
            ["health"] = health,
            ["servicesRead"] = moments,
            ["readByTheProbe"] = new Dictionary<string, object?>
            {
                ["services"] = read.Count,
                ["hosts"] = read.Select(static service => service.ProcessId).Distinct().Count(),
                ["withTheirHostsCreationTime"] = compared,
                ["hostNotOpened"] = hostUnread,
            },
            ["namedByTheSession"] = new Dictionary<string, object?>
            {
                ["hostedAsTheCaptureStarted"] = named,
                ["hostsInstanceNamingItNot"] = notNamed,
                ["noInstanceOfItsHost"] = noInstance,
            },
            ["servicesTheSessionNamesAtTheStart"] = byHost.Values.Sum(static services => services.Count),
        };
    }

    // The session's process instances as icat processes --json lists them, with the services each hosted.
    private static async Task<JsonDocument> ListedAsync(string icat, string session)
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
        return process.ExitCode == 0
            ? JsonDocument.Parse(json[json.IndexOf('{', StringComparison.Ordinal)..])
            : throw new InvalidOperationException($"icat processes exited with {process.ExitCode}: {await error.ConfigureAwait(false)}");
    }
}
