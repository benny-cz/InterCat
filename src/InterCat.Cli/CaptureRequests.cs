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

    /// <summary>The profiles icat capture asks the broker for, as its help lists them and its refusal names them.</summary>
    public static IReadOnlyList<string> Profiles { get; } = ["explore", "focused-transport", "rpc-peers", "content"];
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
            // RPC peers records what Explore does and ALPC beside it (ADR-035): it takes no focus either.
            case "explore" or "focused-transport" or "rpc-peers":
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
                        + "explore and rpc-peers take neither.";
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
                problem = $"--profile is {string.Join(", ", Profiles.Take(Profiles.Count - 1))} or {Profiles[^1]}; "
                    + $"'{profile}' is not one. icat profiles lists what each collects.";
                return null;
        }
    }

    /// <summary>
    /// icat capture's help, which reads the same on any system: what it asks of the broker is read anywhere, though the
    /// broker runs on Windows alone.
    /// </summary>
    internal static void PrintHelp()
    {
        ConsoleUi.Line("  icat capture <new-session-dir> [--duration <seconds>]");
        ConsoleUi.Line("               [--profile " + string.Join("|", CaptureRequests.Profiles) + "]");
        ConsoleUi.Line("               [--mechanism tcp|http] [--pid <id,...>] [--allow-broader]");
        ConsoleUi.Line("               [--max-journal-mib <n>] [--min-free-mib <n>] [--keep-last <seconds>] [--broker <exe>]");
        ConsoleUi.Line("               [--json]");
        ConsoleUi.Line("               content: " + ContentRequestOptions.Synopsis);
        ConsoleUi.Line("                        " + ContentRequestOptions.Limits);
        ConsoleUi.Line("      Captures live without running icat elevated: the capture broker is started on demand");
        ConsoleUi.Line("      (Windows asks for approval), records evidence only, and this process derives the session.");
        ConsoleUi.Line($"      Stops at --duration (default {CaptureRequests.DefaultSeconds} s) or on Ctrl+C; everything published is "
            + "kept. If this");
        ConsoleUi.Line("      process ends early, icat follow <new-session-dir> finishes the session from the ticket");
        ConsoleUi.Line("      it leaves beside it. --keep-last keeps the session to the newest stretch of session time");
        ConsoleUi.Line("      that long, releasing the records read before it, but for what later ones rest on, and the");
        ConsoleUi.Line("      broker releases its own copy of what the session gave up, so --max-journal-mib bounds what");
        ConsoleUi.Line("      it holds rather than all it recorded. A pin (icat pin) keeps the records read from its");
        ConsoleUi.Line("      moment; once the session holds more than the pin allows, the capture stops rather than");
        ConsoleUi.Line("      release them.");
        ConsoleUi.Line("      --profile rpc-peers records lifecycle, RPC calls and TCP and UDP with the kernel's ALPC");
        ConsoleUi.Line("      messages, which link each RPC call to the process that served it, drawn in the graph as it");
        ConsoleUi.Line("      records. Its private system logger takes one of the eight a machine can run, and a machine");
        ConsoleUi.Line("      running all eight refuses it; icat profiles rpc-peers states its cost.");
        ConsoleUi.Line("      --profile content keeps the messages of the processes --pid names - WinINet's HTTP");
        ConsoleUi.Line("      exchanges, etw/manifest/Microsoft-Windows-WinINet-Capture - as icat record does, through");
        ConsoleUi.Line("      the broker: only your own processes, in this sign-in and at no higher integrity, which it");
        ConsoleUi.Line("      holds open while it records. The review names each by what it runs and when it started,");
        ConsoleUi.Line("      and a process whose ID passed to another before the capture starts is refused. It stops");
        ConsoleUi.Line("      when kept content reaches --max-session-bytes, and takes no --keep-last.");
        ConsoleUi.Line("      It runs on Windows alone, as the broker does; a session it captured opens anywhere.");
    }
}
