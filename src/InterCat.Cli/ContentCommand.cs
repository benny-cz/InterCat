using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// One record's kept content, named by an evidence page's exact row locator (§3.7, ADR-036): its facts, and its bytes only
/// when asked - shown bounded and inert, or saved as they are to a file the person names. A buffer of a part shows its
/// part with --part: a whole one as one run of bytes, and one that is not whole buffer by buffer with each gap in place,
/// never saved as one (M8). --decode reads its bytes with the decoder that reads its record, when one does (§11.2), and
/// with --part the whole part with the decoder that reads its part: an HTTP request or response head (M8,
/// `contracts/decoders-v1.md`). --decoders lists the decoders.
/// </summary>
internal static class ContentCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        // The decoders' list takes nothing else; anything given beside it is said.
        if (command.TryTakeFlag("--decoders"))
        {
            if (command.TryReportUnknown(out string? beside))
            {
                ConsoleUi.Failure($"--decoders lists InterCat's decoders and takes nothing else; {beside} was given beside it.");
                ConsoleUi.Explain(PrintHelp);
                return InterCatExitCode.InvalidInvocation;
            }

            PrintDecoders();
            return InterCatExitCode.Success;
        }

        string? sessionText = command.TakeOption("--session-id");
        string? generationText = command.TakeOption("--generation");
        string? segment = command.TakeOption("--segment");
        string? rowText = command.TakeOption("--row");
        string? fromText = command.TakeOption("--from");
        string? toText = command.TakeOption("--to");
        string? saveOption = command.TakeOption("--save");
        string? find = command.TakeOption("--find");
        string? pidText = command.TakeOption("--pid");
        string? processText = command.TakeOption("--process");
        string? policyText = command.TakeOption("--evidence-policy");
        string? limitText = command.TakeOption("--limit");
        string? outputText = command.TakeOption("--output");
        string? maxMibText = command.TakeOption("--max-mib");
        string? maxSecondsText = command.TakeOption("--max-seconds");
        string? directory = command.TakePositional();
        bool reveal = command.TryTakeFlag("--reveal");
        bool wholePart = command.TryTakeFlag("--part");
        bool decode = command.TryTakeFlag("--decode");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        bool ignoreCase = command.TryTakeFlag("--ignore-case");
        bool previews = command.TryTakeFlag("--previews");
        bool hasUnknown = command.TryReportUnknown(out string? unknown);

        // A search reads every record's kept content, not one record's; it is its own deliberate command (§11).
        if (find is not null)
        {
            bool names = sessionText is not null || generationText is not null || segment is not null || rowText is not null;
            int pid = 0;
            int limit = 50;
            int maxMib = (int)(ContentSearch.DefaultMaximumBytes / MiB);
            int maxSeconds = (int)ContentSearch.DefaultBudget.TotalSeconds;
            bool policyRead = EvidencePolicyOption.TryParse(policyText, out EvidencePolicy policy, out string? policyRefusal);
            string? refused = directory is null ? "A session directory is required: icat content <directory> --find <text>."
                : hasUnknown ? command.Refusal(unknown!)
                : find.Length == 0 ? "--find needs the text to look for."
                : json ? "--find lists the records holding the text for a person, never into JSON: leave out --json."
                : names || reveal || wholePart || decode || saveOption is not null || fromText is not null || toText is not null
                    ? "--find searches the session's kept content, not one record: leave out the record's locator and "
                        + "--reveal, --save, --decode, --part, --from and --to."
                : overwrite && outputText is null ? "--overwrite replaces the file --output names: give --output with it."
                // A PID another process may have been given again names no process (P6): --pid keeps to the records that
                // carry it, --process to the records bound to one process, as every other view binds them.
                : pidText is not null && processText is not null
                    ? "--pid keeps to the records that carry a PID, whichever process held it, and --process to the records "
                        + "of one process: give one of them."
                : pidText is not null && !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out pid)
                    ? $"--pid expects a process id; '{pidText}' is not one."
                : policyText is not null && processText is null
                    ? "--evidence-policy chooses which records bind to the process --process names: give --process with it."
                : !policyRead ? policyRefusal
                : limitText is not null && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                    || limit is < 1 or > 10_000)
                    ? $"--limit expects a number of records from 1 to 10,000; '{limitText}' is not one."
                : maxMibText is not null && (!int.TryParse(maxMibText, NumberStyles.None, CultureInfo.InvariantCulture, out maxMib)
                    || maxMib is < 1 or > MaximumMib)
                    ? $"--max-mib expects a number of mebibytes from 1 to 65,536; '{maxMibText}' is not one."
                : maxSecondsText is not null
                    && (!int.TryParse(maxSecondsText, NumberStyles.None, CultureInfo.InvariantCulture, out maxSeconds)
                        || maxSeconds is < 1 or > MaximumSeconds)
                    ? $"--max-seconds expects a number of seconds from 1 to 3,600; '{maxSecondsText}' is not one."
                : null;
            if (refused is not null)
            {
                ConsoleUi.Failure(refused);
                ConsoleUi.Explain(PrintHelp);
                return InterCatExitCode.InvalidInvocation;
            }

            return await FindAsync(directory!, processText, outputText, overwrite, new ContentSearchRequest
            {
                Text = find,
                IgnoreCase = ignoreCase,
                ProcessId = pidText is null ? null : pid,
                Policy = policy,
                Limit = limit,
                Previews = previews,
                MaximumBytes = maxMib * MiB,
                Budget = TimeSpan.FromSeconds(maxSeconds),
            }, cancellationToken).ConfigureAwait(false);
        }

        if (pidText is not null || processText is not null || policyText is not null || limitText is not null || ignoreCase
            || previews || outputText is not null || maxMibText is not null || maxSecondsText is not null)
        {
            ConsoleUi.Failure("--pid, --process, --evidence-policy, --limit, --max-mib, --max-seconds, --ignore-case, --previews "
                + "and --output apply only to --find.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        bool validSession = Guid.TryParse(sessionText, out Guid sessionId) && sessionId != Guid.Empty;
        bool validGeneration = long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture,
            out long generation) && generation > 0;
        bool validRow = int.TryParse(rowText, NumberStyles.None, CultureInfo.InvariantCulture, out int row);
        string? problem = directory is null ? "A session directory is required: icat content <directory>."
            : hasUnknown ? command.Refusal(unknown!)
            : !validSession ? "--session-id must be the nonempty GUID printed by icat evidence."
            : !validGeneration ? "--generation must be the positive number printed by icat evidence."
            : string.IsNullOrWhiteSpace(segment) ? "--segment must name an evidence-page segment."
            : !validRow ? "--row must be a nonnegative evidence-page segment row."
            // Bytes go to a person or to a file they name, never into a document programs read (ADR-036).
            : json && (reveal || saveOption is not null || decode) ? "--json states a record's content facts only; its bytes "
                + "are shown with --reveal, saved with --save or decoded with --decode, never written into JSON."
            : (fromText is not null || toText is not null) && !reveal && saveOption is null
                ? "--from and --to choose the bytes --reveal shows or --save writes; add one of them."
            : overwrite && saveOption is null ? "--overwrite applies only to --save."
            : null;
        if (problem is not null)
        {
            ConsoleUi.Failure(problem);
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory!);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? savePath = saveOption is null ? null : Path.GetFullPath(saveOption);
        if (savePath is not null && File.Exists(savePath) && !overwrite)
        {
            ConsoleUi.Failure($"{savePath} exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionContentDetail detail;
        SessionContentPartDetail? part = null;
        try
        {
            SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
            detail = SessionContentQuery.ReadAt(store, sessionId, generation, segment!, row,
                revealBytes: (reveal || savePath is not null || decode) && !wholePart, cancellationToken);

            // A buffer of an HTTP head or body says whether its part was kept whole (M8); its bytes are read with --part.
            if (detail.Available && detail.Observation.Mechanism == Mechanism.Http)
            {
                part = SessionContentPartQuery.Read(store, sessionId, detail.Observation,
                    revealBytes: wholePart && (reveal || savePath is not null || decode), cancellationToken);
            }
        }
        catch (ArgumentException exception)
        {
            ConsoleUi.Failure(exception);
            return InterCatExitCode.InvalidInvocation;
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Warn(exception.Message);
            return InterCatExitCode.PartialResultSuccess;
        }

        IReadOnlyList<ContentFact> facts = detail.Entry is { } found
            ? ContentBytesView.Facts(found, detail.Observation, detail.Generation, CultureInfo.CurrentCulture, part)
            : [];
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = "content-view-v1",
                SessionPath = path,
                detail.Available,
                detail.UnavailableReason,
                detail.Generation,
                Chunk = detail.Entry?.ChunkName,
                Fragment = detail.Entry?.Fragment,
                Policy = detail.Entry is { } entry
                    ? new { entry.Header.PolicyId, entry.Header.RecordLimit, entry.Header.Inspection }
                    : null,
                Facts = facts,
                Part = part is { IsPart: true } known
                    ? new
                    {
                        known.Name,
                        known.Complete,
                        known.Length,
                        Buffers = known.Buffers.Count,
                        known.Statement,
                        // Where the part lacks bytes, in its order: a whole part has no gap (M8).
                        Gaps = known.Pieces.Where(piece => piece.Kind != ContentPartPieceKind.Kept)
                            .Select(piece => new { piece.Kind, piece.FirstBuffer, piece.LastBuffer, piece.Length, piece.PartOffset }),
                    }
                    : null,
            }, JsonContracts.Indented));
            return detail.Available ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
        }

        ConsoleUi.Heading("Kept content of one record");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", detail.Generation.ToString("N0", CultureInfo.CurrentCulture));
        ObservationRowV1 observation = detail.Observation;
        ConsoleUi.Field("Record", string.Create(CultureInfo.InvariantCulture,
            $"raw {observation.RawStreamId}/{observation.RawSourceEpoch}/{observation.RawRecordOrdinal}"));
        if (!detail.Available || detail.Entry is not { } kept)
        {
            ConsoleUi.Warn(detail.UnavailableReason!);
            return InterCatExitCode.PartialResultSuccess;
        }

        foreach (ContentFact fact in facts)
        {
            ConsoleUi.Field(fact.Label, fact.Value);
        }

        if (part is { IsPart: true })
        {
            ConsoleUi.Field("Part", part.Statement);
        }

        // A decoding is said first, and answers alone unless the bytes are asked for too; the worse answer stands.
        if (wholePart)
        {
            InterCatExitCode decodedPart = InterCatExitCode.Success;
            if (decode && part is { IsPart: true })
            {
                decodedPart = DecodedPart(part, detail.Observation, kept);
                if (!reveal && savePath is null)
                {
                    return decodedPart;
                }
            }

            InterCatExitCode shownPart = await WholePartAsync(part, reveal, savePath, fromText, toText, overwrite, cancellationToken)
                .ConfigureAwait(false);
            return (InterCatExitCode)Math.Max((int)decodedPart, (int)shownPart);
        }

        InterCatExitCode decoded = decode ? Decoded(detail, kept, part) : InterCatExitCode.Success;
        if (decode && !reveal && savePath is null)
        {
            return decoded;
        }

        InterCatExitCode shown = await BytesAsync(detail, kept, reveal, savePath, fromText, toText, overwrite, cancellationToken)
            .ConfigureAwait(false);
        return (InterCatExitCode)Math.Max((int)decoded, (int)shown);
    }

    /// <summary>
    /// What the decoder that reads the record made of its kept bytes (§11.2): its name and version, each field with the
    /// bytes it came from, and what it did not decode and why. A record no decoder reads is said to be read by none - a
    /// buffer of a part a decoder reads whole saying so. As --reveal answers, bytes kept without consent to inspect them, or
    /// never kept, leave the answer partial.
    /// </summary>
    private static InterCatExitCode Decoded(SessionContentDetail detail, SessionContentEntry kept, SessionContentPartDetail? part)
    {
        if (ContentDecoders.Decode(detail) is not { } decoding)
        {
            ConsoleUi.Warn(part is { IsPart: true } && ContentDecoders.ForPart(detail.Observation) is not null
                ? $"No decoder reads this record alone: it is a buffer of {part.Name}, which --part --decode decodes whole."
                : "No decoder reads this record's content; icat content --decoders lists InterCat's decoders and what each reads.");
            return InterCatExitCode.PartialResultSuccess;
        }

        Print(decoding);
        bool withheld = decoding.Read is null && ContentBytesView.Kept(kept.Fragment) is not null;
        return withheld || kept.Fragment.Disposition == ContentDispositionV1.OmittedBySessionLimit
            ? InterCatExitCode.PartialResultSuccess
            : InterCatExitCode.Success;
    }

    /// <summary>The record's kept bytes, when asked: shown bounded and inert, or saved as they are (ADR-036).</summary>
    private static async Task<InterCatExitCode> BytesAsync(SessionContentDetail detail, SessionContentEntry kept, bool reveal,
        string? savePath, string? fromText, string? toText, bool overwrite, CancellationToken cancellationToken)
    {
        if (ContentBytesView.Kept(kept.Fragment) is not { } all)
        {
            // An empty message was kept whole; an omitted one was not kept, which a request for its bytes cannot meet.
            bool omitted = kept.Fragment.Disposition == ContentDispositionV1.OmittedBySessionLimit;
            ConsoleUi.Note(omitted
                ? "No byte of this message was kept, so there is nothing to show or save."
                : "The message held no bytes, so there is nothing to show or save.");
            return omitted && (reveal || savePath is not null) ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
        }

        if (!kept.Inspectable)
        {
            if (reveal || savePath is not null)
            {
                ConsoleUi.Warn("The capture kept these bytes without consent to inspect them, so they are never shown "
                    + "or saved one record at a time; an original evidence package carries them as evidence.");
                return InterCatExitCode.PartialResultSuccess;
            }

            return InterCatExitCode.Success;
        }

        if (detail.Bytes is not { } bytes)
        {
            ConsoleUi.Note("Its bytes are hidden. Add --reveal to show them, bounded and inert, or --save <file> to write "
                + "them as they are.");
            return InterCatExitCode.Success;
        }

        ContentRange chosen = all;
        if ((fromText is not null || toText is not null)
            && !ContentBytesView.TryParseRange(fromText ?? all.First.ToString(CultureInfo.InvariantCulture),
                toText ?? all.Last.ToString(CultureInfo.InvariantCulture), all, out chosen, out string? rangeProblem))
        {
            ConsoleUi.Failure(rangeProblem!);
            return InterCatExitCode.InvalidInvocation;
        }

        if (reveal)
        {
            ContentRange shown = ContentBytesView.Shown(chosen);
            ReadOnlyMemory<byte> window = ContentBytesView.Slice(bytes, all, shown);
            ConsoleUi.Heading("Chosen bytes · inert hexadecimal");
            ConsoleUi.Note("Chosen: " + chosen.Describe(CultureInfo.CurrentCulture)
                + (shown == chosen ? "." : $"; shown: its first {shown.Length.ToString("N0", CultureInfo.CurrentCulture)}."));
            foreach (ContentHexRow line in ContentBytesView.Rows(window.Span, shown.First))
            {
                ConsoleUi.Line("  " + line.Line);
            }

            if (ContentBytesView.DeclaresText(kept.Fragment.Encoding))
            {
                ContentText text = ContentBytesView.Text(window.Span, kept.Fragment.Encoding);
                ConsoleUi.Heading("The same bytes as the text their source declares");
                ConsoleUi.Note("Control and invisible characters are shown as marks, like ␀ or ⟨U+202E⟩"
                    + (text.Cut ? "; the text stops at its bound." : "."));
                foreach (string line in text.Text.Split('\n'))
                {
                    ConsoleUi.Line("  " + line);
                }
            }
        }

        if (savePath is not null)
        {
            await ExportFileWriter.WriteAsync(savePath, ContentBytesView.Slice(bytes, all, chosen), overwrite, cancellationToken)
                .ConfigureAwait(false);
            ConsoleUi.Success($"Saved {chosen.Describe(CultureInfo.CurrentCulture)} to {savePath}, as they are.");
        }

        return InterCatExitCode.Success;
    }

    /// <summary>
    /// What the decoder that reads the part made of it, read whole (M8, `contracts/decoders-v1.md`): a part that is not whole,
    /// or kept without consent to inspect it, is decoded not at all and says why; a part no decoder reads - a body - is said
    /// to be read by none.
    /// </summary>
    private static InterCatExitCode DecodedPart(SessionContentPartDetail part, ObservationRowV1 observation, SessionContentEntry kept)
    {
        if (ContentDecoders.DecodePart(part, observation, kept.Fragment.RecordIn(kept.Header.CaptureId)) is not { } decoding)
        {
            ConsoleUi.Warn($"No decoder reads {part.Name}: InterCat decodes an HTTP request or response head, never a body; "
                + "icat content --decoders lists its decoders.");
            return InterCatExitCode.PartialResultSuccess;
        }

        Print(decoding);
        return decoding.Read is null ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
    }

    /// <summary>A decoding as the command line says it: its decoder and version, what it read, each field, and what it did not decode.</summary>
    private static void Print(DecodedContent decoding)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading(decoding.Heading);
        if (decoding.Read is { } read)
        {
            ConsoleUi.Field("Read", read.Describe(culture) + (decoding.Part is { } part ? " of " + part : string.Empty));
        }

        if (decoding.Fields.Count > 0)
        {
            ConsoleUi.Table(["Field", "Bytes", "Value"],
                [.. decoding.Fields.Select(field => new[] { field.Name, field.Bytes.Describe(culture), field.Value })]);
        }

        if (decoding.NotDecoded is { } rest)
        {
            ConsoleUi.Note("Not decoded: " + rest);
        }
    }

    /// <summary>InterCat's decoders, each at its version with what it reads (`contracts/decoders-v1.md`).</summary>
    private static void PrintDecoders()
    {
        ConsoleUi.Heading("InterCat's content decoders");
        ConsoleUi.Table(["Decoder", "Version", "Reads"],
            [.. ContentDecoders.Known.Select(decoder => new[]
            {
                decoder.Id + " · " + decoder.Name, decoder.Version.ToString(CultureInfo.CurrentCulture), decoder.Reads,
            })]);
        ConsoleUi.Note("A decoding is made only when a person asks for it, from bytes kept with consent to inspect them, and is "
            + "never kept or written into a file.");
    }

    /// <summary>
    /// The whole part a buffer belongs to, shown or saved as one: only when every buffer from its first to its last was
    /// kept whole, since a part missing a buffer is never presented as whole (I21, P2). One that is not whole is shown
    /// with its gaps in place instead, and never saved as one.
    /// </summary>
    private static async Task<InterCatExitCode> WholePartAsync(SessionContentPartDetail? part, bool reveal, string? savePath,
        string? fromText, string? toText, bool overwrite, CancellationToken cancellationToken)
    {
        if (part is not { IsPart: true })
        {
            ConsoleUi.Failure("--part applies to a buffer of an HTTP head or body; this record's belongs to no part.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!part.Complete)
        {
            return GappedPart(part, reveal, savePath, fromText, toText);
        }

        if (!reveal && savePath is null)
        {
            ConsoleUi.Note("Its bytes are hidden. Add --reveal to show the whole part, bounded and inert, or --save <file> "
                + "to write it as it is.");
            return InterCatExitCode.Success;
        }

        if (part.Bytes is not { Length: > 0 } bytes)
        {
            ConsoleUi.Warn(part.Length == 0 ? "The part holds no bytes, so there is nothing to show or save."
                : "The part's bytes are not shown: " + part.Statement);
            return InterCatExitCode.PartialResultSuccess;
        }

        var all = new ContentRange(0, bytes.Length - 1);
        ContentRange chosen = all;
        if ((fromText is not null || toText is not null)
            && !ContentBytesView.TryParseRange(fromText ?? "0", toText ?? all.Last.ToString(CultureInfo.InvariantCulture), all,
                out chosen, out string? problem))
        {
            ConsoleUi.Failure(problem!);
            return InterCatExitCode.InvalidInvocation;
        }

        if (reveal)
        {
            ContentRange shown = ContentBytesView.Shown(chosen);
            ConsoleUi.Heading("The whole part · inert hexadecimal");
            ConsoleUi.Note("Chosen: " + chosen.Describe(CultureInfo.CurrentCulture) + " of " + part.Name
                + (shown == chosen ? "." : $"; shown: its first {shown.Length.ToString("N0", CultureInfo.CurrentCulture)}."));
            foreach (ContentHexRow line in ContentBytesView.Rows(ContentBytesView.Slice(bytes, all, shown).Span, shown.First))
            {
                ConsoleUi.Line("  " + line.Line);
            }
        }

        if (savePath is not null)
        {
            await ExportFileWriter.WriteAsync(savePath, ContentBytesView.Slice(bytes, all, chosen), overwrite, cancellationToken)
                .ConfigureAwait(false);
            ConsoleUi.Success($"Saved {chosen.Describe(CultureInfo.CurrentCulture)} of {part.Name} to {savePath}, as they are.");
        }

        return InterCatExitCode.Success;
    }

    /// <summary>
    /// A part that is not whole (M8): shown, when asked, buffer by buffer with each gap a line of its own - buffers never
    /// recorded, bytes cut or not kept - chosen by buffer number, and never saved as one, since nothing may stand in for
    /// its missing bytes (I21, P2). Its answer is partial: the part was asked for, and only some of it exists.
    /// </summary>
    private static InterCatExitCode GappedPart(SessionContentPartDetail part, bool reveal, string? savePath, string? fromText,
        string? toText)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (!reveal)
        {
            if (savePath is null)
            {
                ConsoleUi.Note("Its bytes are hidden. Add --reveal to show the part with each gap in place, bounded and inert.");
                return InterCatExitCode.Success;
            }

            ConsoleUi.Warn("The part is not whole, so it is never saved as one file: nothing may stand in for its missing "
                + "bytes. Save each buffer from its own record, without --part.");
            return InterCatExitCode.PartialResultSuccess;
        }

        if (!part.BytesRead || ContentBytesView.RecordedBuffers(part) is not { } recorded)
        {
            ConsoleUi.Warn("The part's bytes are not shown: " + part.Statement);
            return InterCatExitCode.PartialResultSuccess;
        }

        ContentBufferRange chosen = recorded;
        if ((fromText is not null || toText is not null)
            && !ContentBytesView.TryParseBuffers(fromText ?? recorded.First.ToString(CultureInfo.InvariantCulture),
                toText ?? recorded.Last.ToString(CultureInfo.InvariantCulture), recorded, out chosen, out string? problem))
        {
            ConsoleUi.Failure(problem!);
            return InterCatExitCode.InvalidInvocation;
        }

        ContentPartLines lines = ContentBytesView.PartLines(part, chosen, culture);
        ConsoleUi.Heading("The part, its gaps in place · inert hexadecimal");
        ConsoleUi.Note("Chosen: " + chosen.Describe(culture) + " of " + part.Name + ". Each buffer's bytes are numbered from "
            + "its own first byte, and nothing stands in for a gap.");
        foreach (ContentLine line in lines.Lines)
        {
            ConsoleUi.Line("  " + line.Line);
        }

        if (savePath is not null)
        {
            ConsoleUi.Warn("The part is not whole, so it is never saved as one file: nothing may stand in for its missing "
                + "bytes. Save each buffer from its own record, without --part.");
        }

        return InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>
    /// Searches the session's kept content for a text (`contracts/content-v1.md` §4): the records holding it, each with the
    /// locator that shows its bytes, what was searched and what was not, and - only with --previews - the bytes around each
    /// record's first match, inert. <paramref name="processText"/> keeps it to one process's records, the process named as
    /// every other command names it, and <paramref name="outputText"/> writes the records listed to a table.
    /// </summary>
    private static async Task<InterCatExitCode> FindAsync(string directory, string? processText, string? outputText, bool overwrite,
        ContentSearchRequest request, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? outputPath = outputText is null ? null : Path.GetFullPath(outputText);
        if (outputPath is not null && File.Exists(outputPath) && !overwrite)
        {
            ConsoleUi.Failure($"{outputPath} exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
        if (store.Current is null)
        {
            return Icat.NoSession();
        }

        // A process is named by a PID one process alone held, else by its instance, and the search keeps to the records
        // bound to it under the evidence policy - never to every holder of its PID (P6).
        ProcessNode? process = null;
        ContentSearchResult result;
        try
        {
            if (processText is not null)
            {
                ConsoleUi.Progress("Projecting the current generation, to find the process --process names.");
                Dictionary<ProcessInstanceId, ProcessNode> processes = SessionOverviewProjector.Project(store, request.Policy,
                    followRpcCalls: false, cancellationToken: cancellationToken).Nodes.ToDictionary(node => node.Id);
                if (ProcessOption.Resolve(processes, "--process", processText, out string? unresolved) is not { } named)
                {
                    ConsoleUi.Failure(unresolved!);
                    return InterCatExitCode.InvalidInvocation;
                }

                process = processes[named];
                request = request with { ProcessInstance = named };
            }

            ConsoleUi.Progress("Searching the content kept with consent to inspect it, each record's bytes on their own.");
            result = ContentSearch.Find(store, store.SessionId, request, cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        SourceClockDescriptor? clock = result.Clock;

        CultureInfo culture = CultureInfo.CurrentCulture;
        // The heading is set in capitals, so the text searched for - whose case may matter - is a field of its own.
        ConsoleUi.Heading("Kept content search");
        ConsoleUi.Field("Text", $"\"{request.Text}\"");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(result.Generation));
        if (request.ProcessId is { } pid) ConsoleUi.Field("Records of", string.Create(culture, $"PID {pid}, whichever process held it"));
        if (process is not null)
        {
            ConsoleUi.Field("Records of", $"{CollectorText.Named(process)}, instance {process.Id}");
            ConsoleUi.Field("Bound to it", "as the " + CommandArgument.Typed(request.Policy) + " evidence policy admits");
        }

        if (request.IgnoreCase) ConsoleUi.Field("Letters", "A to Z matched in either case");
        if (request.MaximumBytes != ContentSearch.DefaultMaximumBytes || request.Budget != ContentSearch.DefaultBudget)
        {
            ConsoleUi.Field("Budgets", string.Create(culture,
                $"{request.MaximumBytes / MiB:N0} MiB read and {request.Budget.TotalSeconds:N0} seconds"));
        }

        ConsoleUi.Field("Searched", "the kept bytes of " + CountText.Of(result.RecordsRead, "record")
            + string.Create(culture, $", {result.BytesRead:N0} B"));
        if (result.WithoutConsent > 0)
        {
            ConsoleUi.Field("Not searched", CountText.Of(result.WithoutConsent, "record") + " kept without consent to inspect "
                + CountText.Agree(result.WithoutConsent, "its bytes", "their bytes"));
        }

        if (result.Omitted > 0)
        {
            ConsoleUi.Field("Not searched", CountText.Of(result.Omitted, "record") + " the capture kept no byte of, having reached "
                + "its content limit");
        }

        if (result.NotAdmitted > 0)
        {
            ConsoleUi.Field("Not searched", CountText.Of(result.NotAdmitted, "record") + " bound to it more weakly than the "
                + CommandArgument.Typed(request.Policy) + " evidence policy admits");
        }

        ConsoleUi.Field("Found", result.Hits.Count == 0 ? "no record holding it"
            : result.More ? "more than " + CountText.Of(result.Hits.Count, "record") + string.Create(culture, $"; the first {result.Hits.Count:N0} listed")
            : CountText.Of(result.Hits.Count, "record"));
        if (result.NoneKept is { } none) ConsoleUi.Note(none);
        if (result.Unreadable is { } unreadable) ConsoleUi.Warn(unreadable);
        if (result.Stopped is { } stopped)
        {
            ConsoleUi.Warn(stopped);
            ConsoleUi.Note("--max-mib and --max-seconds let a search read more, up to 65,536 MiB and 3,600 seconds.");
        }
        if (result.Hits.Count > 0)
        {
            ConsoleUi.Line();
            ConsoleUi.Table(
                ["At", "PID", "Mechanism", "Event", "Matches", "First at", "Segment", "Row"],
                [.. result.Hits.Select(hit => (IReadOnlyList<string>)
                [
                    SessionText.Seconds(clock, hit.Observation.NativeTicks) ?? "-",
                    (hit.Observation.OwnerProcessId ?? hit.Observation.HeaderProcessId).ToString(CultureInfo.InvariantCulture),
                    MechanismText.Name(hit.Observation.Mechanism),
                    hit.Observation.EventId.ToString(CultureInfo.InvariantCulture),
                    ConsoleUi.Count(hit.Matches),
                    hit.FirstAt >= 0 ? string.Create(culture, $"byte {hit.FirstAt:N0}") : string.Create(culture, $"{-hit.FirstAt:N0} B before"),
                    hit.Segment,
                    hit.Row.ToString(CultureInfo.InvariantCulture),
                ])]);
            foreach (ContentSearchHit hit in result.Hits.Where(hit => hit.Preview is not null))
            {
                // The bytes around the first match, inert: what is not visible ASCII is written as \xHH, so the marks
                // around the match are never the content's own.
                ConsoleUi.Line(string.Create(culture, $"  {hit.Segment} row {hit.Row}: ")
                    + hit.Preview!.Before + "⟦" + hit.Preview.Match + "⟧" + hit.Preview.After);
            }
        }

        ConsoleUi.Line();
        if (result.Hits.Any(hit => hit.FirstAt < 0))
        {
            ConsoleUi.Note("A record first at \"N B before\" holds a match that began N bytes before its first byte, earlier in "
                + "its HTTP part.");
        }

        ConsoleUi.Note(ContentSearch.Exactness);
        if (result.Hits.Count > 0)
        {
            ConsoleUi.Note(string.Create(culture, $"icat content {path} --session-id {result.SessionId} --generation {result.Generation} ")
                + "--segment <segment> --row <row> --reveal shows a record's kept bytes.");
        }

        // The records listed go to a table only when asked; the bytes around their matches only with --previews, which
        // makes the table content.
        if (outputPath is not null)
        {
            await ExportFileWriter.WriteAsync(outputPath, ContentSearchTable.Csv(result, request), overwrite, cancellationToken)
                .ConfigureAwait(false);
            ConsoleUi.Success(result.Hits.Count == 0
                ? $"No record was listed, so the table written to {outputPath} holds its header alone."
                : $"The {CountText.Of(result.Hits.Count, "record")} listed written to {outputPath}"
                    + (request.Previews ? ", each with the bytes around its first match." : "."));
            if (request.Previews && result.Hits.Count > 0)
            {
                ConsoleUi.Warn(PreviewsContent);
            }
        }

        return result.Partial ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
    }

    /// <summary>A mebibyte, the unit --max-mib counts in.</summary>
    private const long MiB = 1024 * 1024;

    /// <summary>The most mebibytes --max-mib lets a search read.</summary>
    private const int MaximumMib = 65_536;

    /// <summary>The most seconds --max-seconds lets a search read for.</summary>
    private const int MaximumSeconds = 3_600;

    /// <summary>What a table of records found with previews holds, said beside the file written: content.</summary>
    internal const string PreviewsContent = "It holds the bytes around each match, read from content the session kept with "
        + "consent to inspect it: it is content, to be shared only as that content would be.";

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat content <session-directory> --session-id <guid> --generation <n> --segment <name> --row <n>");
        ConsoleUi.Line("             [--part] [--reveal] [--decode] [--from <byte>] [--to <byte>] [--save <file> [--overwrite]] [--json]");
        ConsoleUi.Line("icat content <session-directory> --find <text> [--ignore-case] [--process <pid | instance>");
        ConsoleUi.Line("             [--evidence-policy <policy>] | --pid <id>] [--limit <n>] [--previews]");
        ConsoleUi.Line("             [--max-mib <n>] [--max-seconds <n>] [--output <file.csv> [--overwrite]]");
        ConsoleUi.Line("icat content --decoders");
        ConsoleUi.Line("  One record's kept content, from an icat evidence page's exact row locator (ADR-036): what the");
        ConsoleUi.Line("  bytes are, how many of the message were kept and which are missing, and what they were kept");
        ConsoleUi.Line("  under. Its bytes are hidden unless --reveal shows them as inert hex (and, where the source");
        ConsoleUi.Line("  declares text, that text with control characters made visible), at most 64 KiB at once, or");
        ConsoleUi.Line("  --save writes them as they are to a new file. --from and --to choose the bytes, by offset in");
        ConsoleUi.Line("  the message, decimal or 0x hexadecimal. Content kept without consent to inspect it is never");
        ConsoleUi.Line("  shown or saved. --json states the facts only. For a buffer of an HTTP head or body it says whether");
        ConsoleUi.Line("  its part was kept whole, and --part shows or saves the whole part instead. A part that is not whole");
        ConsoleUi.Line("  is shown buffer by buffer with each gap in place - --from and --to then choose buffers by number -");
        ConsoleUi.Line("  and is never saved as one. --decode reads the kept bytes with the decoder that reads the record,");
        ConsoleUi.Line("  and with --part the whole part with the decoder that reads it - an HTTP request or response head,");
        ConsoleUi.Line("  decoded only whole - and lists each field with the bytes it came from, and what it did not decode");
        ConsoleUi.Line("  and why; content kept without consent to inspect it is never decoded. --decoders lists the");
        ConsoleUi.Line("  decoders, each at its version, with what it reads.");
        ConsoleUi.Line("  --find searches every record's kept content for the text - its UTF-8 bytes, or its UTF-16 bytes where");
        ConsoleUi.Line("  a record's bytes declare UTF-16 - and lists the records holding it, each with its locator, showing");
        ConsoleUi.Line("  none of their bytes unless --previews shows those around each one's first match. Content kept");
        ConsoleUi.Line("  without consent to inspect it is never searched. Each record is searched on its own, within 256 MiB");
        ConsoleUi.Line("  and 10 seconds unless --max-mib and --max-seconds set others, up to 65,536 MiB and 3,600 seconds,");
        ConsoleUi.Line("  and what was not searched is said; --limit sets how many records are listed (50).");
        ConsoleUi.Line("  --process keeps to one process's records: its PID where one process alone held it, else its");
        ConsoleUi.Line("  instance id as icat processes prints it, each record bound to it as the evidence policy admits");
        ConsoleUi.Line("  (include-correlated); --pid keeps to the records that carry a PID, whichever process held it.");
        ConsoleUi.Line("  --output writes the records listed to a CSV table, with the bytes around each first match only");
        ConsoleUi.Line("  with --previews, which makes the table content.");
    }
}
