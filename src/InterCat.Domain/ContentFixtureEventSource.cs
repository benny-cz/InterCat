using System.Diagnostics.Tracing;

namespace InterCat.Domain;

/// <summary>
/// InterCat's controlled content fixture (§11, ADR-036): a provider whose events carry an application message's bytes, so
/// the content path is exercised end to end by a workload InterCat owns and by no real application's messages. The
/// workload raises it, and the capture catalog reads its layout from this type, so the two cannot disagree.
/// </summary>
[EventSource(Name = ProviderName)]
public sealed class ContentFixtureEventSource : EventSource
{
    /// <summary>The provider's name; its identity is derived from it, as every such provider's is.</summary>
    public const string ProviderName = "InterCat-Fixture-Content";

    /// <summary>A message the raising process sent.</summary>
    public const int MessageSentId = 1;

    /// <summary>A message the raising process received.</summary>
    public const int MessageReceivedId = 2;

    /// <summary>The version both events are declared at.</summary>
    public const byte EventVersion = 1;

    private ContentFixtureEventSource()
        : base(EventSourceSettings.EtwManifestEventFormat)
    {
    }

    /// <summary>The one instance a process raises the fixture through.</summary>
    public static ContentFixtureEventSource Log { get; } = new();

    /// <summary>
    /// Raises a message the process <paramref name="processId"/> - the raising one, which names itself so each record's
    /// owner is in its own payload - sent on <paramref name="conversation"/>.
    /// </summary>
    [Event(MessageSentId, Level = EventLevel.Informational, Opcode = EventOpcode.Send, Version = EventVersion)]
    public void MessageSent(int processId, long conversation, byte[] message) =>
        WriteEvent(MessageSentId, processId, conversation, message);

    /// <summary>Raises a message the process <paramref name="processId"/> received on <paramref name="conversation"/>.</summary>
    [Event(MessageReceivedId, Level = EventLevel.Informational, Opcode = EventOpcode.Receive, Version = EventVersion)]
    public void MessageReceived(int processId, long conversation, byte[] message) =>
        WriteEvent(MessageReceivedId, processId, conversation, message);
}
