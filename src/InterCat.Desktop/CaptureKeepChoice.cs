using System.Collections.ObjectModel;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// What a capture started from the window keeps, as the Explore card's Keep selector lists it (§20.2, ADR-045): every
/// record of a capture bounded at ten minutes, or the newest stretch of session time of one that records until it is
/// stopped, for at most a day, its older records released between mirrors as `icat capture --keep-last` releases them -
/// or, with <paramref name="LinksRpcCalls"/>, every record of ten minutes of RPC peers, Explore's sources with the kernel's
/// ALPC messages beside them, which link each RPC call to the process that served it (ADR-035) - or, with
/// <paramref name="Content"/>, the messages of processes of the person's own, which they choose when they start it and
/// review as the broker prepared it before anything is recorded (§11.1, ADR-049).
/// </summary>
public sealed record CaptureKeepChoice(
    string Label, int MaximumDurationSeconds, TimeSpan? Keep, bool Content = false, bool LinksRpcCalls = false)
    : IAccessibleRow
{
    /// <summary>The longest a capture records, which the broker bounds: a day.</summary>
    public const int UntilStoppedSeconds = 86_400;

    /// <summary>
    /// The choices, first the default: every record of a ten-minute capture, which needs no configuration. RPC peers is
    /// bounded as the default is, since its private system logger is one of the eight a machine can run, held for as long
    /// as it records; a longer one is icat capture's (--profile rpc-peers with --keep-last).
    /// </summary>
    public static ReadOnlyCollection<CaptureKeepChoice> All { get; } = Array.AsReadOnly(
    [
        new CaptureKeepChoice("Every record, 10 minutes", 600, null),
        new CaptureKeepChoice("The last 10 minutes", UntilStoppedSeconds, TimeSpan.FromMinutes(10)),
        new CaptureKeepChoice("The last hour", UntilStoppedSeconds, TimeSpan.FromHours(1)),
        new CaptureKeepChoice("RPC peers, 10 minutes", 600, null, LinksRpcCalls: true),
        new CaptureKeepChoice("Your processes' messages", 600, null, Content: true),
    ]);

    /// <summary>The policy releasing what a capture's session holds before its newest window; null when it keeps every record.</summary>
    public RollingRetentionPolicy? Rolling => Keep is { } keep ? new(keep) : null;

    /// <summary>What a combo box reads as its value when this choice is made: the label, not the record's fields.</summary>
    public override string ToString() => Label;

    public string AccessibleName => Label + ": " + Explanation;

    /// <summary>What the capture records and keeps under this choice, and what can stop it sooner, in the card's words.</summary>
    public string Explanation => Content
        ? "keeps the HTTP messages of processes of yours you choose next, within limits you set; you review it before "
            + "anything is recorded."
        : LinksRpcCalls
        ? "records for up to 10 minutes and keeps every record, with the kernel's ALPC messages, which link each RPC call to "
            + "the process that served it, so the graph draws RPC calls between processes. It is not the default: it takes "
            + "one of the eight system loggers a machine can run, and its overhead is moderate."
        : Rolling is not { } rolling
        ? "records for up to 10 minutes and keeps every record."
        : $"records until you stop it, for up to 24 hours, and keeps {rolling.Window} of session time, releasing older "
            + "records as it goes. The broker releases its own copy of what the session gave up, so its journal limit "
            + "bounds what it still holds rather than all it recorded.";

    /// <summary>What the runner is told: how long the capture may record, what its session keeps, and how it is projected.</summary>
    public CaptureRunOptions Options(Func<EvidencePolicy>? policy) => new()
    {
        MaximumDurationSeconds = MaximumDurationSeconds,
        Rolling = Rolling,
        EvidencePolicy = policy,
        LinksRpcCalls = LinksRpcCalls,
    };
}
