using System.Globalization;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// A capture profile's focus or content scope, as `icat profiles` and `icat remote preview` take them from the command line
/// and as an agent reads them from a preview's request (`remote-agent-v1` §4.10): taken and checked in one place, so each
/// refuses a request in the same words before anything is compiled (`capture-profile-preview-v1`).
/// </summary>
internal sealed record ProfileRequestOptions
{
    /// <summary>The most processes a request names.</summary>
    public const int MostProcesses = 64;

    public Mechanism? Mechanism { get; init; }

    public IReadOnlyList<int> ProcessIds { get; init; } = [];

    public bool AllowBroaderCapture { get; init; }

    public string? ContentSource { get; init; }

    public IReadOnlyList<string> Channels { get; init; } = [];

    public int? MaximumRecordBytes { get; init; }

    public long? MaximumSessionBytes { get; init; }

    public ContentRetentionMode? Retention { get; init; }

    public ContentInspectionMode? Inspection { get; init; }

    /// <summary>Whether a content option is given: a source, a channel, a byte limit, a retention or an inspection.</summary>
    public bool HasContentOptions => ContentSource is not null || Channels.Count > 0 || MaximumRecordBytes is not null
        || MaximumSessionBytes is not null || Retention is not null || Inspection is not null;

    /// <summary>Whether any option is given at all.</summary>
    public bool Any => Mechanism is not null || ProcessIds.Count > 0 || AllowBroaderCapture || HasContentOptions;

    /// <summary>
    /// Takes the focus and content options from <paramref name="command"/>; null, having said why, where one does not read
    /// (<see cref="From"/>).
    /// </summary>
    public static ProfileRequestOptions? Take(CommandLine command)
    {
        ArgumentNullException.ThrowIfNull(command);
        string? mechanism = command.TakeOption("--mechanism");
        string? source = command.TakeOption("--source");
        string? maximumRecordBytes = command.TakeOption("--max-record-bytes");
        string? maximumSessionBytes = command.TakeOption("--max-session-bytes");
        string? retention = command.TakeOption("--retention");
        string? inspection = command.TakeOption("--inspection");
        bool allowBroaderCapture = command.TryTakeFlag("--allow-broader-capture");
        var processIds = new List<string>();
        string? taken;
        while ((taken = command.TakeOption("--pid")) is not null)
        {
            processIds.Add(taken);
        }

        var channels = new List<string>();
        while ((taken = command.TakeOption("--channel")) is not null)
        {
            channels.Add(taken);
        }

        ProfileRequestOptions? options = From(new ProfileOptionTexts(mechanism, processIds, allowBroaderCapture, source, channels,
            maximumRecordBytes, maximumSessionBytes, retention, inspection), out string? refusal);
        if (options is null)
        {
            ConsoleUi.Failure(refusal!);
        }

        return options;
    }

    /// <summary>
    /// The options <paramref name="texts"/> give - or null, and why one does not read: a process given twice or not a
    /// process, too many processes or channels, a selector too long, a limit that is not a positive integer, or a name that
    /// is none of the mechanisms, retentions or inspections. An agent reads a controller's options so, in the same words.
    /// </summary>
    public static ProfileRequestOptions? From(ProfileOptionTexts texts, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var processIds = new List<int>();
        foreach (string text in texts.ProcessIds)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int processId) || processId <= 0)
            {
                refusal = $"--pid expects a process id; '{text}' is not one.";
                return null;
            }

            if (processIds.Contains(processId))
            {
                refusal = string.Create(CultureInfo.InvariantCulture, $"--pid {processId} is given more than once; give it once.");
                return null;
            }

            processIds.Add(processId);
        }

        var channelSelectors = new List<string>();
        foreach (string channelSelector in texts.Channels)
        {
            if (ChannelProblem(channelSelector) is { } problem)
            {
                refusal = problem;
                return null;
            }

            if (channelSelectors.Contains(channelSelector, StringComparer.Ordinal))
            {
                refusal = $"--channel {channelSelector} is given more than once; give it once.";
                return null;
            }

            channelSelectors.Add(channelSelector);
        }

        if (processIds.Count > MostProcesses)
        {
            refusal = $"A profile preview accepts at most {MostProcesses} --pid values.";
            return null;
        }

        if (channelSelectors.Count > ContentCapturePolicyCompiler.MaximumChannelSelectors)
        {
            refusal = $"A content preview accepts at most {ContentCapturePolicyCompiler.MaximumChannelSelectors} --channel values.";
            return null;
        }

        int? maximumRecordBytes = null;
        if (texts.MaximumRecordBytes is { } recordText)
        {
            if (!int.TryParse(recordText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            {
                refusal = "--max-record-bytes requires a positive decimal integer.";
                return null;
            }

            maximumRecordBytes = parsed;
        }

        long? maximumSessionBytes = null;
        if (texts.MaximumSessionBytes is { } sessionText)
        {
            if (!long.TryParse(sessionText, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed <= 0)
            {
                refusal = "--max-session-bytes requires a positive decimal integer.";
                return null;
            }

            maximumSessionBytes = parsed;
        }

        ContentRetentionMode? retention = RetentionNamed(texts.Retention);
        if (texts.Retention is not null && retention is null)
        {
            refusal = CommandLine.Expects("--retention", texts.Retention, ["stop-at-limit"]);
            return null;
        }

        ContentInspectionMode? inspection = InspectionNamed(texts.Inspection);
        if (texts.Inspection is not null && inspection is null)
        {
            refusal = CommandLine.Expects("--inspection", texts.Inspection, ["disabled", "hex-text"]);
            return null;
        }

        Mechanism? mechanism = MechanismNamed(texts.Mechanism);
        if (texts.Mechanism is not null && mechanism is null)
        {
            refusal = CommandLine.Expects("--mechanism", texts.Mechanism, ["tcp", "udp", "rpc", "http", "sdk"])
                + " Which of them a profile can capture is checked per profile.";
            return null;
        }

        refusal = null;
        return new()
        {
            Mechanism = mechanism,
            ProcessIds = processIds,
            AllowBroaderCapture = texts.AllowBroaderCapture,
            ContentSource = texts.Source,
            Channels = channelSelectors,
            MaximumRecordBytes = maximumRecordBytes,
            MaximumSessionBytes = maximumSessionBytes,
            Retention = retention,
            Inspection = inspection,
        };
    }

    /// <summary>These options as their texts, in the command line's words, which <see cref="From"/> reads back.</summary>
    public ProfileOptionTexts Texts() => new(
        Mechanism is { } mechanism ? NameOf(mechanism) : null,
        [.. ProcessIds.Select(processId => processId.ToString(CultureInfo.InvariantCulture))],
        AllowBroaderCapture,
        ContentSource,
        Channels,
        MaximumRecordBytes?.ToString(CultureInfo.InvariantCulture),
        MaximumSessionBytes?.ToString(CultureInfo.InvariantCulture),
        Retention is { } retention ? NameOf(retention) : null,
        Inspection is { } inspection ? NameOf(inspection) : null);

    /// <summary>
    /// The compiler's request for <paramref name="selected"/> with these options - or null, and why they make none: a
    /// focus without its mechanism, a content scope missing anything it needs or one its compiler refuses, or options the
    /// profile does not take. <paramref name="explain"/> says whether the command's help would make it plainer.
    /// </summary>
    public CaptureProfileRequest? RequestFor(CaptureProfileDescriptor selected, bool diagnosticEtl, out string? refusal, out bool explain)
    {
        ArgumentNullException.ThrowIfNull(selected);
        explain = false;
        ContentCaptureRequest? contentRequest = null;
        if (selected.Kind == CaptureProfileKind.FocusedTransport)
        {
            if (HasContentOptions)
            {
                refusal = "Content source, channel, budget, retention and inspection options apply only to content.";
                return null;
            }

            if (Mechanism is null)
            {
                refusal = "focused-transport requires --mechanism tcp.";
                explain = true;
                return null;
            }

            if (AllowBroaderCapture && ProcessIds.Count == 0)
            {
                refusal = "--allow-broader-capture is meaningful only with at least one --pid selection.";
                return null;
            }
        }
        else if (selected.Kind == CaptureProfileKind.Content)
        {
            if (AllowBroaderCapture)
            {
                refusal = "Content scope never accepts --allow-broader-capture; an unenforceable content scope is refused.";
                return null;
            }

            if (Mechanism is null
                || ContentSource is null
                || ProcessIds.Count == 0
                || Channels.Count == 0
                || MaximumRecordBytes is null
                || MaximumSessionBytes is null
                || Retention is null
                || Inspection is null)
            {
                refusal = "content requires --source, --mechanism, at least one --pid and --channel, both byte limits, --retention, and --inspection.";
                explain = true;
                return null;
            }

            contentRequest = new()
            {
                SourceId = ContentSource,
                Mechanism = Mechanism.Value,
                ProcessIds = ProcessIds,
                ChannelSelectors = Channels,
                MaximumRecordBytes = MaximumRecordBytes.Value,
                MaximumSessionBytes = MaximumSessionBytes.Value,
                Retention = Retention.Value,
                Inspection = Inspection.Value,
            };

            if (ContentCapturePolicyCompiler.Validate(contentRequest) is { } contentRefusal)
            {
                refusal = contentRefusal;
                return null;
            }
        }
        else if (Any)
        {
            refusal = "Mechanism, process and content-scope options apply only to focused-transport or content.";
            return null;
        }

        refusal = null;
        return new(
            selected.Kind,
            diagnosticEtl,
            FocusedMechanism: selected.Kind == CaptureProfileKind.FocusedTransport ? Mechanism : null,
            FocusedProcessIds: selected.Kind == CaptureProfileKind.FocusedTransport ? ProcessIds : null,
            AllowBroaderCapture: AllowBroaderCapture,
            Content: contentRequest);
    }

    /// <summary>Why <paramref name="selector"/> is no channel selector; null where it is one.</summary>
    public static string? ChannelProblem(string selector) =>
        string.IsNullOrWhiteSpace(selector)
        || selector.Length > ContentCapturePolicyCompiler.MaximumChannelSelectorLength
        || selector.Any(char.IsControl)
            ? $"Each --channel selector must contain 1 to {ContentCapturePolicyCompiler.MaximumChannelSelectorLength} printable characters."
            : null;

    /// <summary>The mechanism a request names, in the words the command line takes; null for another word.</summary>
    public static Mechanism? MechanismNamed(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "tcp" => Domain.Mechanism.Tcp,
            "udp" => Domain.Mechanism.Udp,
            "rpc" => Domain.Mechanism.Rpc,
            "http" => Domain.Mechanism.Http,
            "sdk" => Domain.Mechanism.ApplicationSdk,
            _ => null,
        };

    /// <summary>The command line's word for <paramref name="mechanism"/>, which <see cref="MechanismNamed"/> reads back.</summary>
    public static string NameOf(Mechanism mechanism) => mechanism switch
    {
        Domain.Mechanism.Tcp => "tcp",
        Domain.Mechanism.Udp => "udp",
        Domain.Mechanism.Rpc => "rpc",
        Domain.Mechanism.Http => "http",
        Domain.Mechanism.ApplicationSdk => "sdk",
        _ => throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, "No request names this mechanism."),
    };

    public static ContentRetentionMode? RetentionNamed(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "stop-at-limit" => ContentRetentionMode.StopAtLimit,
            _ => null,
        };

    public static string NameOf(ContentRetentionMode retention) => retention switch
    {
        ContentRetentionMode.StopAtLimit => "stop-at-limit",
        _ => throw new ArgumentOutOfRangeException(nameof(retention), retention, "No request names this retention."),
    };

    public static ContentInspectionMode? InspectionNamed(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "disabled" => ContentInspectionMode.Disabled,
            "hex-text" => ContentInspectionMode.HexAndText,
            _ => null,
        };

    public static string NameOf(ContentInspectionMode inspection) => inspection switch
    {
        ContentInspectionMode.Disabled => "disabled",
        ContentInspectionMode.HexAndText => "hex-text",
        _ => throw new ArgumentOutOfRangeException(nameof(inspection), inspection, "No request names this inspection."),
    };
}

/// <summary>
/// A capture profile's focus and content options as texts, in the command line's words: as given on it, or as an agent
/// reads them from a controller's preview request (`remote-agent-v1` §4.10).
/// </summary>
internal sealed record ProfileOptionTexts(
    string? Mechanism,
    IReadOnlyList<string> ProcessIds,
    bool AllowBroaderCapture,
    string? Source,
    IReadOnlyList<string> Channels,
    string? MaximumRecordBytes,
    string? MaximumSessionBytes,
    string? Retention,
    string? Inspection);
