using System.Globalization;
using InterCat.CaptureBroker;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// What icat capture asks the broker to prepare, from its options: Explore, a focused transport, or a content request of
/// the person's own processes (ADR-049), with its duration and limits (`contracts/broker-v1.md` §2). Read apart from the
/// command that launches the broker, so every refusal is said before Windows asks for approval.
/// </summary>
internal static class CaptureRequests
{
    public const int DefaultSeconds = 60;
    public const int MaximumSeconds = 86_400;
    private const long DefaultJournalMebibytes = 1_024;
    private const long DefaultFreeMebibytes = 1_024;

    /// <summary>
    /// The request the options state, or null with what is wrong in <paramref name="problem"/>. A content request takes
    /// its parts in icat record's words; <paramref name="keepsWindow"/> says --keep-last was written, which a content
    /// capture does not take.
    /// </summary>
    public static BrokerPrepareCaptureRequest? Parse(
        string profile,
        string? mechanismOption,
        bool broader,
        string? durationOption,
        string? journalOption,
        string? freeOption,
        bool keepsWindow,
        ContentRequestOptions content,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(content);
        int seconds = DefaultSeconds;
        if (durationOption is not null
            && (!int.TryParse(durationOption.TrimEnd('s'), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                || seconds is < 1 or > MaximumSeconds))
        {
            problem = $"--duration is whole seconds from 1 to {MaximumSeconds:N0}; '{durationOption}' is not one.";
            return null;
        }

        long journal = DefaultJournalMebibytes;
        long free = DefaultFreeMebibytes;
        if ((journalOption is not null
                && (!long.TryParse(journalOption, NumberStyles.None, CultureInfo.InvariantCulture, out journal) || journal < 1))
            || (freeOption is not null
                && (!long.TryParse(freeOption, NumberStyles.None, CultureInfo.InvariantCulture, out free) || free < 0)))
        {
            problem = "--max-journal-mib and --min-free-mib take whole mebibytes.";
            return null;
        }

        Mechanism? mechanism = mechanismOption?.ToUpperInvariant() switch
        {
            null => null,
            "TCP" => Mechanism.Tcp,
            "HTTP" => Mechanism.Http,
            _ => (Mechanism)0,
        };
        var quota = new BrokerCaptureQuota(seconds, journal * 1024 * 1024, free * 1024 * 1024);
        switch (profile)
        {
            case "explore" or "focused-transport":
            {
                bool focused = profile == "focused-transport";
                if (content.AnyBesideProcesses)
                {
                    problem = "--source, --channel, the byte limits, --inspection and --retention make a content request, and "
                        + "only --profile content takes one.";
                    return null;
                }

                if (ContentRequestOptions.ProcessIds(content.Processes, out problem) is not { } processIds)
                {
                    return null;
                }

                if (focused ? mechanism != Mechanism.Tcp : mechanism is not null || processIds.Count > 0 || broader)
                {
                    problem = "focused-transport takes --mechanism tcp and optionally --pid <id,...> and --allow-broader; "
                        + "explore takes neither.";
                    return null;
                }

                // Live, so a capture interrupted by a killed broker still keeps everything published (plan revision 82).
                return new(profile, mechanism, processIds, broader, false, quota, BrokerRetentionPolicy.StopAtLimit, null,
                    BrokerJournalPublication.Live);
            }

            case "content":
            {
                if (mechanism is not { } messages || messages == (Mechanism)0 || broader)
                {
                    problem = "content takes --mechanism http, the messages it keeps, and never --allow-broader: a content "
                        + "capture keeps only the processes it names.";
                    return null;
                }

                // A window releases what a session read before it, while a content capture keeps everything until its
                // content limit stops it: the one bounded behaviour a content request has (`contracts/content-v1.md` §5).
                if (keepsWindow)
                {
                    problem = "--keep-last keeps a window of a session's records, while a content capture keeps what it "
                        + "records until its content limit stops it; record content without --keep-last.";
                    return null;
                }

                if (content.Compile(messages, out ContentCaptureRequest? request) is { } refused)
                {
                    problem = refused;
                    return null;
                }

                problem = null;
                return new(profile, null, [], false, false, quota, BrokerRetentionPolicy.StopAtLimit, request,
                    BrokerJournalPublication.Live);
            }

            default:
                problem = $"--profile is explore, focused-transport or content; '{profile}' is not one. icat profiles lists "
                    + "what each collects.";
                return null;
        }
    }
}
