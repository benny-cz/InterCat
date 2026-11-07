using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Sdk;

namespace InterCat.TestWorkloads;

/// <summary>
/// The cooperative SDK's content in a process of its own (ADR-107), whose provider no listener has enabled before: a message
/// published with its bytes while nothing listens; then under a listener asking for every keyword but content, as a capture
/// keeping no content asks; then with a listener asking for content beside it - a message within the most, one past it, one
/// of exactly the most and an empty one. A listener enables an EventSource's keywords for the life of the process, so the
/// order is the test: no call is made under a listener asking for content before every call that must say none. It writes
/// what each call returned and what each listener heard - each event's identity, sequence and lengths, and each content's
/// SHA-256, never its bytes - to content.json. It reads no InterCat state.
/// </summary>
internal static class SdkContentScenario
{
    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Every keyword the SDK may define but content's: what a capture keeping no content asks for.</summary>
    private const EventKeywords AllButContent = (EventKeywords)0xFFFF_FFFE;

    public static async Task<int> RunAsync(string truthDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(truthDirectory);
        var calls = new List<object>();
        object[] metadataHeard;
        object[] contentHeard;
        using (InterCatChannel channel = InterCatChannel.Open(Guid.NewGuid(), "content", InterCatChannelKind.Other))
        {
            // Nothing listens: nothing is said.
            calls.Add(Call(channel, "unheard", 1, 300));
            using var metadata = new Listener();
            metadata.EnableEvents(InterCatSdkEventSource.Log, EventLevel.Informational, AllButContent);

            // A listener keeping no content: the publication alone.
            calls.Add(Call(channel, "metadata", 2, 300));
            using var content = new Listener();
            content.EnableEvents(InterCatSdkEventSource.Log, EventLevel.Informational, InterCatSdkEventSource.Keywords.Content);

            // A listener asking for content beside it: within the most, past it, the most, and nothing.
            calls.Add(Call(channel, "content", 3, 300));
            calls.Add(Call(channel, "past the most", 4, InterCatChannel.MaximumContent + 1));
            calls.Add(Call(channel, "the most", 5, InterCatChannel.MaximumContent));
            calls.Add(Call(channel, "empty", 6, 0));
            metadataHeard = metadata.Heard();
            contentHeard = content.Heard();
        }

        var summary = new { maximumContent = InterCatChannel.MaximumContent, calls, metadataHeard, contentHeard };
        await File.WriteAllTextAsync(Path.Combine(truthDirectory, "content.json"), JsonSerializer.Serialize(summary, SummaryOptions),
            cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static object Call(InterCatChannel channel, string name, long sequence, int length)
    {
        byte[] message = Message(sequence, length);
        bool said = channel.Published(sequence, message);
        return new { name, sequence, length, sha256 = Convert.ToHexStringLower(SHA256.HashData(message)), said };
    }

    /// <summary>A message's bytes, made from its sequence, so no two messages here hold the same.</summary>
    public static byte[] Message(long sequence, int length)
    {
        var message = new byte[length];
        for (int index = 0; index < length; index++)
        {
            message[index] = unchecked((byte)((index * 7) + sequence));
        }

        return message;
    }

    /// <summary>Hears the SDK's publications and contents within this process, as a session hears them through ETW.</summary>
    private sealed class Listener : EventListener
    {
        private readonly List<object> heard = [];

        public object[] Heard()
        {
            lock (heard)
            {
                return [.. heard];
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != InterCatSdkEventSource.ProviderName
                || eventData.EventId is not (InterCatSdkEventSource.MessagePublishedId or InterCatSdkEventSource.MessageContentId))
            {
                return;
            }

            var payload = eventData.Payload!;
            object said = eventData.EventId == InterCatSdkEventSource.MessagePublishedId
                ? new { id = eventData.EventId, sequence = (long)payload[3]!, logicalLength = (long)payload[4]! }
                : new
                {
                    id = eventData.EventId,
                    sequence = (long)payload[3]!,
                    contentLength = ((byte[])payload[4]!).Length,
                    contentSha256 = Convert.ToHexStringLower(SHA256.HashData((byte[])payload[4]!)),
                };
            lock (heard)
            {
                heard.Add(said);
            }
        }
    }
}
