using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

/// <summary>A process as a client saw it under an ID it names: its image and when it started, each where readable.</summary>
public sealed record SeenProcess(int ProcessId, string? Image, DateTimeOffset? StartedUtc);

/// <summary>
/// What a client says of the processes a content capture keeps content from (ADR-049, R22): each as the client saw it
/// before asking and as the broker pinned it - its ID and its start - so a process whose ID passed to another between the
/// two is refused before the capture starts rather than recorded as a stranger, and a review names each by what it runs
/// and when it started rather than by an ID alone. icat capture and the window say it in these words.
/// </summary>
public static class BrokerContentReview
{
    /// <summary>The process now holding <paramref name="processId"/>, as this process can read it; null when none is running.</summary>
    public static SeenProcess? See(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return null;
                }
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (Win32Exception)
            {
                // A process this one may not wait on is still running; what can be read of it is said below.
            }

            return new(
                processId,
                Read(() => process.ProcessName),
                Read(() => StartOf(process)));
        }
    }

    /// <summary>
    /// The processes a person may choose to keep content from, as this process can read them: each in this terminal session
    /// whose start it can read, but this process itself, by name and then start. Whose each one is, and at what integrity,
    /// the broker reads when it prepares; one that is not the asker's own is refused then, by name.
    /// </summary>
    public static IReadOnlyList<SeenProcess> Running()
    {
        using Process current = Process.GetCurrentProcess();
        int session = current.SessionId;
        var running = new List<SeenProcess>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == current.Id || Read<int?>(() => process.SessionId) != session
                    || Read(() => StartOf(process)) is not { } started)
                {
                    continue;
                }

                running.Add(new(process.Id, Read(() => process.ProcessName), started));
            }
        }

        return
        [
            .. running.OrderBy(process => process.Image, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.StartedUtc)
                .ThenBy(process => process.ProcessId),
        ];
    }

    /// <summary>Why a content request that names a process not running records nothing, in every client's words.</summary>
    public static string NotRunning(int processId) =>
        string.Create(CultureInfo.InvariantCulture, $"Process {processId} is not running, so nothing was recorded: ")
        + "a content request names running processes, which the capture holds open so their IDs stay theirs. Task Manager's "
        + "Details tab lists running processes with their IDs.";

    /// <summary>A process as a review names it: "notepad, started 2026-10-08 11:00:00.123", in <paramref name="zone"/>'s time.</summary>
    public static string Describe(SeenProcess process, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(process);
        return (process.Image ?? "its image not readable") + ", "
            + (process.StartedUtc is { } started ? "started " + Instant(started, zone) : "its start not readable");
    }

    /// <summary>
    /// Why the capture the broker prepared must not start: a process it pinned started at another time than the one this
    /// command saw under that ID before asking, so the ID passed to another process between. Null when each process seen is
    /// the one pinned; one whose start could not be read here rests on the broker's own check alone.
    /// </summary>
    public static string? Mismatch(BrokerEffectiveCaptureSummary summary, IReadOnlyDictionary<int, SeenProcess> seen, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(seen);
        if (summary.Content is not { } content)
        {
            return null;
        }

        foreach ((int processId, DateTimeOffset pinned) in summary.RequestedProcessIds.Zip(content.ProcessStartsUtc))
        {
            if (seen.GetValueOrDefault(processId) is { StartedUtc: { } saw } && saw != pinned)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Process {processId} is not the one named: ")
                    + $"the broker prepared the process that started at {Instant(pinned, zone)}, but the one named here "
                    + $"started at {Instant(saw, zone)}, so its ID passed to another process between. Name the process now "
                    + "running, or the one you meant if it still runs under another ID.";
            }
        }

        return null;
    }

    /// <summary>
    /// What a content capture's review says it keeps, as label and value: the <paramref name="mechanism"/>'s messages of its
    /// source, each process by its ID, what it runs and when it started - the start the broker pinned - its limits, and
    /// whether a person may see what it keeps.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> Lines(
        BrokerEffectiveCaptureSummary summary,
        Mechanism mechanism,
        IReadOnlyDictionary<int, SeenProcess> seen,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(seen);
        if (summary.Content is not { } content)
        {
            return [];
        }

        var lines = new List<(string, string)>
        {
            ("Content", $"{MechanismText.Name(mechanism)} messages {ContentSources.Of(content.SourceId)}, "
                + (content.ChannelSelectors is [ContentCaptureRequest.EveryChannel]
                    ? "every channel of the processes below"
                    : (content.ChannelSelectors.Count == 1 ? "the channel " : "the channels ")
                        + string.Join(", ", content.ChannelSelectors) + " of the processes below")),
        };
        foreach ((int processId, DateTimeOffset pinned) in summary.RequestedProcessIds.Zip(content.ProcessStartsUtc))
        {
            SeenProcess process = (seen.GetValueOrDefault(processId) ?? new(processId, null, null)) with { StartedUtc = pinned };
            lines.Add((string.Create(CultureInfo.InvariantCulture, $"Process {processId}"), Describe(process, zone)));
        }

        lines.Add(("Limits", $"at most {ByteSizeText.Of(content.MaximumRecordBytes)} a record and "
            + $"{ByteSizeText.Of(content.MaximumSessionBytes)} in all; the capture stops when what it keeps reaches that"));
        lines.Add(("Inspection", content.Inspection == ContentInspectionMode.HexAndText
            ? "its bytes may be shown as hex and text when you ask"
            : "its bytes are never shown"));
        return lines;
    }

    /// <summary>
    /// What changed between the plan a person reviewed and the one the broker prepares when they confirm it, as a clause;
    /// null when it keeps what was reviewed - the same processes from the same starts, the same messages kept within the
    /// same limits under the same consent, the same sources collecting the same, and the same limits. The first difference
    /// is named.
    /// </summary>
    public static string? Changed(BrokerEffectiveCaptureSummary reviewed, BrokerEffectiveCaptureSummary now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentNullException.ThrowIfNull(now);
        if (!reviewed.RequestedProcessIds.SequenceEqual(now.RequestedProcessIds))
        {
            return "the processes it names";
        }

        if (reviewed.Content is { } before && now.Content is { } after)
        {
            foreach ((int processId, (DateTimeOffset was, DateTimeOffset started)) in reviewed.RequestedProcessIds
                .Zip(before.ProcessStartsUtc.Zip(after.ProcessStartsUtc)))
            {
                if (was != started)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"process {processId} is now the one that started at ")
                        + $"{Instant(started, zone)}, not {Instant(was, zone)}";
                }
            }

            if (!string.Equals(before.SourceId, after.SourceId, StringComparison.Ordinal)
                || !before.ChannelSelectors.SequenceEqual(after.ChannelSelectors, StringComparer.Ordinal)
                || before.MaximumRecordBytes != after.MaximumRecordBytes
                || before.MaximumSessionBytes != after.MaximumSessionBytes
                || before.Inspection != after.Inspection)
            {
                return "what it keeps of the messages";
            }
        }
        else if (reviewed.Content is not null || now.Content is not null)
        {
            return "whether it keeps content";
        }

        return !reviewed.Sources.Select(source => source.SourceId).SequenceEqual(now.Sources.Select(source => source.SourceId),
                StringComparer.Ordinal)
            || !string.Equals(reviewed.CollectionStatement, now.CollectionStatement, StringComparison.Ordinal)
            || !string.Equals(reviewed.Disclosure, now.Disclosure, StringComparison.Ordinal)
                ? "what it collects"
            : reviewed.Quota != now.Quota || reviewed.Retention != now.Retention
                ? "its limits"
                : null;
    }

    private static string Instant(DateTimeOffset value, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(value, zone).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>When a process started, as the instant its local start time names.</summary>
    private static DateTimeOffset? StartOf(Process process) => new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);

    private static T? Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return default;
        }
    }
}
