using System.Diagnostics.Tracing;
using System.Text.Json;
using InterCat.Sdk;

namespace InterCat.TestWorkloads;

/// <summary>
/// The cooperative SDK's first use under a listener: a listener that enables the SDK's provider as the provider is made -
/// as an ETW session already running enables it in a process starting under it, a capture running as an application
/// starts - then the process's first use of the SDK, which makes the provider. It writes what the provider announced to
/// first-use.json: each announcement's library, and the library the SDK names. It reads no InterCat state.
/// </summary>
internal static class SdkFirstUseScenario
{
    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string truthDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(truthDirectory);
        string[] announced;
        using (var listener = new FirstUseListener())
        {
            // The process's first use of the SDK: its provider is made now, under the listener.
            using (InterCatChannel.Open(Guid.NewGuid(), "first-use", InterCatChannelKind.Other))
            {
            }

            announced = listener.Announced();
        }

        var summary = new { announced, library = InterCatSdkEventSource.Library };
        await File.WriteAllTextAsync(Path.Combine(truthDirectory, "first-use.json"), JsonSerializer.Serialize(summary, SummaryOptions),
            cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Enables the SDK's provider as it is made, and keeps the library each of its announcements names.</summary>
    private sealed class FirstUseListener : EventListener
    {
        private readonly List<string> announced = [];

        public string[] Announced()
        {
            lock (announced)
            {
                return [.. announced];
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == InterCatSdkEventSource.ProviderName)
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name == InterCatSdkEventSource.ProviderName && eventData.EventId == InterCatSdkEventSource.SdkStartedId)
            {
                lock (announced)
                {
                    announced.Add(eventData.Payload![^1] as string ?? string.Empty);
                }
            }
        }
    }
}
