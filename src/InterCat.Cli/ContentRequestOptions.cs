using System.Globalization;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// A content request in the words a command line states it (`contracts/content-v1.md` §5.1): its source, the processes it
/// names, the channels, both limits, the consent to inspect what is kept and the retention. icat record takes it to record
/// itself, elevated, and icat capture to ask the broker, which keeps content only from the asker's own processes
/// (ADR-049); both take it in these words.
/// </summary>
internal sealed class ContentRequestOptions
{
    /// <summary>The synopsis of a content request, as both commands' help gives it.</summary>
    public const string Synopsis =
        "--source <source-id> --mechanism http --pid <id> ... --channel * --inspection hex-text|disabled";

    /// <summary>The synopsis's second line: the limits every content request names, and its one retention.</summary>
    public const string Limits = "--max-record-bytes <n> --max-session-bytes <n> [--retention stop-at-limit]";

    private ContentRequestOptions(
        string? source,
        IReadOnlyList<string> processes,
        IReadOnlyList<string> channels,
        string? maximumRecord,
        string? maximumSession,
        string? inspection,
        string? retention)
    {
        Source = source;
        Processes = processes;
        Channels = channels;
        MaximumRecord = maximumRecord;
        MaximumSession = maximumSession;
        Inspection = inspection;
        Retention = retention;
    }

    public string? Source { get; }

    /// <summary>Each --pid as it was written: one process ID, or several separated by commas.</summary>
    public IReadOnlyList<string> Processes { get; }

    public IReadOnlyList<string> Channels { get; }

    public string? MaximumRecord { get; }

    public string? MaximumSession { get; }

    public string? Inspection { get; }

    public string? Retention { get; }

    /// <summary>
    /// Whether an option only a content request takes was written: its source, a channel, a limit, the inspection or the
    /// retention. A --pid is not one of them, since a focused capture names its processes too.
    /// </summary>
    public bool AnyBesideProcesses => Source is not null || Channels.Count > 0 || MaximumRecord is not null
        || MaximumSession is not null || Inspection is not null || Retention is not null;

    /// <summary>
    /// Takes every option of a content request off <paramref name="command"/>, each --pid and --channel as often as written.
    /// </summary>
    public static ContentRequestOptions Take(CommandLine command)
    {
        ArgumentNullException.ThrowIfNull(command);
        string? source = command.TakeOption("--source");
        string? maximumRecord = command.TakeOption("--max-record-bytes");
        string? maximumSession = command.TakeOption("--max-session-bytes");
        string? inspection = command.TakeOption("--inspection");
        string? retention = command.TakeOption("--retention");
        var processes = new List<string>();
        for (string? value; (value = command.TakeOption("--pid")) is not null;)
        {
            processes.Add(value);
        }

        var channels = new List<string>();
        for (string? value; (value = command.TakeOption("--channel")) is not null;)
        {
            channels.Add(value);
        }

        return new(source, processes, channels, maximumRecord, maximumSession, inspection, retention);
    }

    /// <summary>
    /// The process IDs <paramref name="written"/> names, in the order written - one to a --pid, or several separated by
    /// commas - or null, with what is wrong in <paramref name="problem"/>.
    /// </summary>
    public static IReadOnlyList<int>? ProcessIds(IReadOnlyList<string> written, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(written);
        var processIds = new List<int>();
        foreach (string item in written.SelectMany(value =>
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out int processId) || processId <= 0)
            {
                problem = $"--pid takes positive process IDs, one to a --pid or separated by commas; '{item}' is not one.";
                return null;
            }

            processIds.Add(processId);
        }

        problem = null;
        return processIds;
    }

    /// <summary>
    /// The request these options state for <paramref name="mechanism"/>, or the problem with them. Every part is the
    /// request's own and required, except the retention, whose one bounded behaviour is stop-at-limit
    /// (`contracts/content-v1.md` §5).
    /// </summary>
    public string? Compile(Mechanism mechanism, out ContentCaptureRequest? request)
    {
        request = null;
        if (Source is null || Processes.Count == 0 || Channels.Count == 0 || MaximumRecord is null || MaximumSession is null
            || Inspection is null)
        {
            return "--profile content needs --source, --mechanism, at least one --pid and --channel, --max-record-bytes, "
                + "--max-session-bytes and --inspection.";
        }

        if (ProcessIds(Processes, out string? problem) is not { } processIds)
        {
            return problem;
        }

        if (!int.TryParse(MaximumRecord, NumberStyles.None, CultureInfo.InvariantCulture, out int recordBytes))
        {
            return $"--max-record-bytes takes a whole number of bytes; '{MaximumRecord}' is not one.";
        }

        if (!long.TryParse(MaximumSession, NumberStyles.None, CultureInfo.InvariantCulture, out long sessionBytes))
        {
            return $"--max-session-bytes takes a whole number of bytes; '{MaximumSession}' is not one.";
        }

        ContentInspectionMode? mode = Inspection.Trim().ToLowerInvariant() switch
        {
            "hex-text" or "hextext" => ContentInspectionMode.HexAndText,
            "disabled" => ContentInspectionMode.Disabled,
            _ => null,
        };
        if (mode is null)
        {
            return "--inspection is hex-text, which lets a person see kept bytes when they ask, or disabled, which never does.";
        }

        if (Retention is not null && Retention.Trim().ToLowerInvariant() is not ("stop-at-limit" or "stopatlimit"))
        {
            return "--retention is stop-at-limit, the one bounded behaviour: the capture stops when kept content reaches its limit.";
        }

        request = new()
        {
            SourceId = Source,
            Mechanism = mechanism,
            ProcessIds = processIds,
            ChannelSelectors = Channels,
            MaximumRecordBytes = recordBytes,
            MaximumSessionBytes = sessionBytes,
            Retention = ContentRetentionMode.StopAtLimit,
            Inspection = mode.Value,
        };
        if (ContentCapturePolicyCompiler.Validate(request) is { } refused)
        {
            request = null;
            return refused;
        }

        return null;
    }
}
