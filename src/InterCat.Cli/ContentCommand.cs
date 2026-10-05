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
/// never saved as one (M8).
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

        string? sessionText = command.TakeOption("--session-id");
        string? generationText = command.TakeOption("--generation");
        string? segment = command.TakeOption("--segment");
        string? rowText = command.TakeOption("--row");
        string? fromText = command.TakeOption("--from");
        string? toText = command.TakeOption("--to");
        string? saveOption = command.TakeOption("--save");
        string? directory = command.TakePositional();
        bool reveal = command.TryTakeFlag("--reveal");
        bool wholePart = command.TryTakeFlag("--part");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        bool validSession = Guid.TryParse(sessionText, out Guid sessionId) && sessionId != Guid.Empty;
        bool validGeneration = long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture,
            out long generation) && generation > 0;
        bool validRow = int.TryParse(rowText, NumberStyles.None, CultureInfo.InvariantCulture, out int row);
        string? problem = directory is null ? "A session directory is required: icat content <directory>."
            : hasUnknown ? CommandLine.Unknown(unknown!)
            : !validSession ? "--session-id must be the nonempty GUID printed by icat evidence."
            : !validGeneration ? "--generation must be the positive number printed by icat evidence."
            : string.IsNullOrWhiteSpace(segment) ? "--segment must name an evidence-page segment."
            : !validRow ? "--row must be a nonnegative evidence-page segment row."
            // Bytes go to a person or to a file they name, never into a document programs read (ADR-036).
            : json && (reveal || saveOption is not null) ? "--json states a record's content facts only; its bytes are "
                + "shown with --reveal or saved with --save, never written into JSON."
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
                revealBytes: (reveal || savePath is not null) && !wholePart, cancellationToken);

            // A buffer of an HTTP head or body says whether its part was kept whole (M8); its bytes are read with --part.
            if (detail.Available && detail.Observation.Mechanism == Mechanism.Http)
            {
                part = SessionContentPartQuery.Read(store, sessionId, detail.Observation,
                    revealBytes: wholePart && (reveal || savePath is not null), cancellationToken);
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

        if (wholePart)
        {
            return await WholePartAsync(part, reveal, savePath, fromText, toText, overwrite, cancellationToken)
                .ConfigureAwait(false);
        }

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

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat content <session-directory> --session-id <guid> --generation <n> --segment <name> --row <n>");
        ConsoleUi.Line("             [--part] [--reveal] [--from <byte>] [--to <byte>] [--save <file> [--overwrite]] [--json]");
        ConsoleUi.Line("  One record's kept content, from an icat evidence page's exact row locator (ADR-036): what the");
        ConsoleUi.Line("  bytes are, how many of the message were kept and which are missing, and what they were kept");
        ConsoleUi.Line("  under. Its bytes are hidden unless --reveal shows them as inert hex (and, where the source");
        ConsoleUi.Line("  declares text, that text with control characters made visible), at most 64 KiB at once, or");
        ConsoleUi.Line("  --save writes them as they are to a new file. --from and --to choose the bytes, by offset in");
        ConsoleUi.Line("  the message, decimal or 0x hexadecimal. Content kept without consent to inspect it is never");
        ConsoleUi.Line("  shown or saved. --json states the facts only. For a buffer of an HTTP head or body it says whether");
        ConsoleUi.Line("  its part was kept whole, and --part shows or saves the whole part instead. A part that is not whole");
        ConsoleUi.Line("  is shown buffer by buffer with each gap in place - --from and --to then choose buffers by number -");
        ConsoleUi.Line("  and is never saved as one.");
    }
}
