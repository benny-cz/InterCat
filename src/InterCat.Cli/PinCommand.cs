using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>A session's pins, with the one a command placed or removed, as a document a tool can read without this CLI.</summary>
internal sealed record PinsDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }

    /// <summary>"list", "pin" or "unpin".</summary>
    public required string Action { get; init; }
    public PinDocument? Placed { get; init; }
    public PinDocument? Removed { get; init; }

    /// <summary>The pins standing once the command ended, earliest moment first.</summary>
    public required IReadOnlyList<PinDocument> Pins { get; init; }

    /// <summary>What the session holds on disk, which a pin's allowance is measured against.</summary>
    public required long HeldBytes { get; init; }

    /// <summary>The boundary of the session's latest interval release, before which no pin can keep anything; null when none was made.</summary>
    public required long? KeptFromNanoseconds { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>One pin as a document states it.</summary>
internal sealed record PinDocument
{
    public required string Id { get; init; }
    public required string ShortId { get; init; }
    public required long FromNanoseconds { get; init; }

    /// <summary>The moment in words: where it falls on the wall clock the capture's machine read, and its session time.</summary>
    public required string From { get; init; }
    public required long AllowanceBytes { get; init; }
    public required DateTimeOffset PlacedUtc { get; init; }
    public required string Reason { get; init; }

    /// <summary>All of it in the words the window uses.</summary>
    public required string Statement { get; init; }

    /// <summary><paramref name="pin"/> as a document states it, its moment placed as <paramref name="from"/> says.</summary>
    public static PinDocument Of(RetentionPin pin, string from) => new()
    {
        Id = pin.Id.ToString("N", CultureInfo.InvariantCulture),
        ShortId = pin.ShortId,
        FromNanoseconds = pin.FromNanoseconds,
        From = from,
        AllowanceBytes = pin.AllowanceBytes,
        PlacedUtc = pin.PlacedUtc,
        Reason = pin.Reason,
        Statement = RetentionPinText.Describe(pin, from),
    };
}

/// <summary>
/// `icat pin`: keeps a session's records from a moment through every retention (store-v1 §8, ADR-046). A pin stands
/// beside the session's generations, so it can be placed while a follow writes the session; no interval release passes
/// it, no release by record number is made while it stands, and a follow keeping a rolling window stops once the session
/// holds more than the pin allows, rather than release what it keeps.
/// </summary>
internal static class PinCommand
{
    /// <summary>The largest allowance a pin declares: a pebibyte, far past any qualified session.</summary>
    private const long MaximumAllowanceMebibytes = 1L << 30;

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return Task.FromResult(InterCatExitCode.Success);
        }

        string? fromOption = command.TakeOption("--from");
        string? allowOption = command.TakeOption("--allow-mib");
        string? reasonOption = command.TakeOption("--reason");
        string? removeOption = command.TakeOption("--remove");
        string? pathOption = command.TakePositional();
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            return Task.FromResult(InterCatExitCode.InvalidInvocation);
        }

        return Task.FromResult(Run(pathOption, fromOption, allowOption, reasonOption, removeOption, json, cancellationToken));
    }

    private static InterCatExitCode Run(string? pathOption, string? fromOption, string? allowOption, string? reasonOption,
        string? removeOption, bool json, CancellationToken cancellationToken)
    {
        if (pathOption is null)
        {
            ConsoleUi.Failure("A session directory is required: icat pin <directory>.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        if (removeOption is not null && (fromOption ?? allowOption ?? reasonOption) is not null)
        {
            ConsoleUi.Failure("--remove names the pin it removes alone: it takes no --from, --allow-mib or --reason.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (fromOption is null && (allowOption ?? reasonOption) is not null)
        {
            ConsoleUi.Failure("--allow-mib and --reason go with the moment a pin keeps records from: "
                + "icat pin <directory> --from <moment> --reason <text>.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (fromOption is not null && string.IsNullOrWhiteSpace(reasonOption))
        {
            ConsoleUi.Failure("A pin says why it keeps the records, so whoever finds it later knows: --reason <text>.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (reasonOption is { Length: > RetentionPin.MaximumReasonLength })
        {
            ConsoleUi.Failure(string.Create(CultureInfo.CurrentCulture,
                $"A pin's reason is at most {RetentionPin.MaximumReasonLength} characters; this one is {reasonOption.Length:N0}."));
            return InterCatExitCode.InvalidInvocation;
        }

        long? allowance = null;
        if (allowOption is not null)
        {
            if (!long.TryParse(allowOption, NumberStyles.None, CultureInfo.InvariantCulture, out long mebibytes)
                || mebibytes is < 1 or > MaximumAllowanceMebibytes)
            {
                ConsoleUi.Failure(string.Create(CultureInfo.CurrentCulture,
                        $"--allow-mib expects whole mebibytes from 1 to {MaximumAllowanceMebibytes:N0}: ")
                    + "the most the session may hold while the pin stands.");
                return InterCatExitCode.InvalidInvocation;
            }

            allowance = mebibytes * 1024 * 1024;
        }

        string full = Path.GetFullPath(pathOption);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        // A pin reads no evidence: the generation's manifest and the pins file are what it rests on.
        SessionStore store = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(full));
        if (store.Current is not { } manifest)
        {
            return Icat.NoSession();
        }

        TimeRange? extent = SessionRecording.RecordsExtent(store, cancellationToken);
        SessionWallClock? wall = SessionRecording.WallClock(store);
        string Moment(long nanoseconds) => extent is { } range
            ? RetainCommand.Instant(nanoseconds, wall, range)
            : SessionTimeText.Seconds(nanoseconds, CultureInfo.CurrentCulture);

        var notes = new List<string>();
        RetentionPin? placed = null;
        RetentionPin? removed = null;
        string action = "list";
        if (fromOption is not null)
        {
            action = "pin";
            if (extent is not { } range)
            {
                ConsoleUi.Failure($"No record of this session has a session time, so {fromOption} places nothing in it.");
                return InterCatExitCode.InvalidInvocation;
            }

            if (!SessionMoment.TryPlace(fromOption, wall, TimeZoneInfo.Local, range, CultureInfo.CurrentCulture, out long ticks,
                    out string? problem, SessionRecording.RetainedFromNanoseconds(manifest)))
            {
                ConsoleUi.Failure(problem ?? "--from takes a moment: a time of day on the wall clock the capture's machine read, "
                    + "such as 14:32:05.120, with its date or offset where needed, or session time with its unit, such as "
                    + $"312.5 s. '{fromOption}' is neither.");
                ConsoleUi.Explain(PrintHelp);
                return InterCatExitCode.InvalidInvocation;
            }

            long held = manifest.HeldBytes();
            try
            {
                placed = store.Pin(ticks * 100, allowance ?? Math.Max(held, 1), reasonOption!, DateTimeOffset.UtcNow);
            }
            catch (InvalidOperationException exception)
            {
                ConsoleUi.Failure(exception.Message);
                return InterCatExitCode.InvalidInvocation;
            }

            notes.Add($"While it stands, no record this session holds that was read from {Moment(placed.FromNanoseconds)} on is "
                + "released: a release asked for past it stops there, a release by record number is not made, and a follow "
                + "keeping a rolling window keeps the session past it, stopping once the session holds more than "
                + $"{ByteSizeText.Of(placed.AllowanceBytes)}.");
            if (allowance is null)
            {
                notes.Add($"It allows the session what it holds now, {ByteSizeText.Of(placed.AllowanceBytes)}. For a session a "
                    + "follow is still writing, pin with --allow-mib to let it grow.");
            }

            if (manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content))
            {
                notes.Add("A pin keeps records and their rows. The content kept of their messages is restricted evidence a "
                    + "person may still release on its own, with icat retain --release-content.");
            }
        }
        else if (removeOption is not null)
        {
            action = "unpin";
            string named = removeOption.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            RetentionPin[] matching = named.Length == 0
                ? []
                : [.. store.Pins().Where(pin => pin.Id.ToString("N", CultureInfo.InvariantCulture).StartsWith(named, StringComparison.Ordinal))];
            if (matching.Length != 1)
            {
                ConsoleUi.Failure(matching.Length == 0
                    ? $"No pin of this session is named {removeOption}. icat pin {full} lists them."
                    : $"{removeOption} names {matching.Length:N0} pins of this session: "
                        + string.Join(", ", matching.Select(pin => pin.ShortId)) + ". Give more of its identity.");
                return InterCatExitCode.InvalidInvocation;
            }

            removed = store.Unpin(matching[0].Id);
            if (removed is null)
            {
                ConsoleUi.Failure($"Pin {matching[0].ShortId} was removed meanwhile, by another command.");
                return InterCatExitCode.InvalidInvocation;
            }

            notes.Add($"It no longer keeps the records read from {Moment(removed.FromNanoseconds)}: the next release that "
                + "reaches them may give them up, unless another pin keeps them.");
        }

        IReadOnlyList<RetentionPin> pins = store.Pins();
        long? keptFrom = store.Current?.LatestRelease(RetentionExtentKind.Interval)?.Record.Interval?.BoundaryNanoseconds;
        PinDocument Document(RetentionPin pin) => PinDocument.Of(pin, Moment(pin.FromNanoseconds));
        var document = new PinsDocument
        {
            Contract = "store-v1",
            Path = full,
            Action = action,
            Placed = placed is null ? null : Document(placed),
            Removed = removed is null ? null : Document(removed),
            Pins = [.. pins.Select(Document)],
            HeldBytes = (store.Current ?? manifest).HeldBytes(),
            KeptFromNanoseconds = keptFrom,
            Notes = notes,
        };
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            Render(document, Moment);
        }

        return InterCatExitCode.Success;
    }

    private static void Render(PinsDocument document, Func<long, string> moment)
    {
        ConsoleUi.Heading(document.Action switch
        {
            "pin" => "Pin placed",
            "unpin" => "Pin removed",
            _ => "Pins",
        });
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Holds", ByteSizeText.Of(document.HeldBytes));
        ConsoleUi.Field("Keeps every record", document.KeptFromNanoseconds is { } from
            ? "from " + moment(from) + ", before which a release gave records up"
            : "it recorded: nothing was released by session time");
        if (document.Pins.Count == 0)
        {
            ConsoleUi.Field("Pins", "none: a release may give up whatever it reaches");
        }

        foreach (PinDocument pin in document.Pins)
        {
            ConsoleUi.Field("Pin " + pin.ShortId, pin.Statement);
        }

        if (document.Notes.Count > 0)
        {
            ConsoleUi.Line();
            foreach (string note in document.Notes)
            {
                ConsoleUi.Note(note);
            }
        }
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat pin <directory> [--json]");
        ConsoleUi.Line("      Lists the pins standing on the session.");
        ConsoleUi.Line("  icat pin <directory> --from <moment> --reason <text> [--allow-mib <n>] [--json]");
        ConsoleUi.Line("      Keeps every record read from <moment> on through every retention while the pin stands:");
        ConsoleUi.Line("      a release asked for past it stops there, and none by record number is made. <moment> is");
        ConsoleUi.Line("      a time of day on the wall clock the capture's machine read, such as 14:32:05.120, or");
        ConsoleUi.Line("      session time with its unit, such as 312.5 s. --allow-mib is the most the session may");
        ConsoleUi.Line("      hold meanwhile, by default what it holds now: a follow keeping a rolling window stops");
        ConsoleUi.Line("      once the session holds more, rather than release what the pin keeps.");
        ConsoleUi.Line("  icat pin <directory> --remove <pin> [--json]");
        ConsoleUi.Line("      Removes a pin, named by its identity or its first characters, as the list shows it.");
    }
}
