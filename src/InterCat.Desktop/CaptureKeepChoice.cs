using System.Collections.ObjectModel;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// What a capture started from the window keeps, as the Explore card's Keep selector lists it (§20.2, ADR-045): every
/// record of a capture bounded at ten minutes, or the newest stretch of session time of one that records until it is
/// stopped, for at most a day, its older records released between mirrors as `icat capture --keep-last` releases them.
/// </summary>
public sealed record CaptureKeepChoice(string Label, int MaximumDurationSeconds, TimeSpan? Keep) : IAccessibleRow
{
    /// <summary>The longest a capture records, which the broker bounds: a day.</summary>
    public const int UntilStoppedSeconds = 86_400;

    /// <summary>The choices, first the default: every record of a ten-minute capture, which needs no configuration.</summary>
    public static ReadOnlyCollection<CaptureKeepChoice> All { get; } = Array.AsReadOnly(
    [
        new CaptureKeepChoice("Every record, 10 minutes", 600, null),
        new CaptureKeepChoice("The last 10 minutes", UntilStoppedSeconds, TimeSpan.FromMinutes(10)),
        new CaptureKeepChoice("The last hour", UntilStoppedSeconds, TimeSpan.FromHours(1)),
    ]);

    /// <summary>The policy releasing what a capture's session holds before its newest window; null when it keeps every record.</summary>
    public RollingRetentionPolicy? Rolling => Keep is { } keep ? new(keep) : null;

    /// <summary>What a combo box reads as its value when this choice is made: the label, not the record's fields.</summary>
    public override string ToString() => Label;

    public string AccessibleName => Label + ": " + Explanation;

    /// <summary>What the capture records and keeps under this choice, and what can stop it sooner, in the card's words.</summary>
    public string Explanation => Rolling is not { } rolling
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
    };
}
