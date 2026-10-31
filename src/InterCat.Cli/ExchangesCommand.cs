using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>A session's HTTP exchanges, grouped by client process, each with what was recorded of its parts (ADR-037).</summary>
internal sealed record ExchangesDocument
{
    public required string Contract { get; init; }

    /// <summary>
    /// What a document of decoded heads holds (`http-exchange-heads-v1`, `contracts/http-exchanges-v1.md` §5f): content,
    /// to be shared only as that content would be. Absent from `http-exchanges-v1`, which holds none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    /// <summary>The decoder that read the listed exchanges' heads, at its version; absent from `http-exchanges-v1`.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContentDecoderInfo? Decoder { get; init; }

    public required string Path { get; init; }
    public required long Generation { get; init; }
    public required string GroupingRule { get; init; }
    public required string BindingRule { get; init; }
    public required string EvidencePolicy { get; init; }

    /// <summary>HTTP records whose source fields name no exchange, and so belong to none.</summary>
    public required long RecordsWithoutExchange { get; init; }

    /// <summary>
    /// What the capture covered of HTTP over the session (R21): whether an exchange it lists none of could have been seen
    /// at all. The ledger says it, never the records' absence.
    /// </summary>
    public required IReadOnlyList<MechanismCoverage> Coverage { get; init; }

    public required IReadOnlyList<ExchangesGroupDocument> Groups { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }
}

internal sealed record ExchangesGroupDocument
{
    public required int ProcessId { get; init; }

    /// <summary>The instance the group's exchanges bind to, when the evidence policy admits it; null otherwise.</summary>
    public required ProcessInstanceDocument? Process { get; init; }

    /// <summary>Why no instance holds the group's exchanges under the evidence policy; null when one does.</summary>
    public required string? Unattributed { get; init; }

    /// <summary>The key the Desktop's ladder names the group by, when an instance holds it.</summary>
    public required string? Key { get; init; }

    public required int Exchanges { get; init; }
    public required int RecordedWhole { get; init; }
    public required long Records { get; init; }
    public required long RequestBytes { get; init; }
    public required long ResponseBytes { get; init; }
    public required long? MedianDurationNanoseconds { get; init; }

    /// <summary>The group's first exchanges in reading order, when exchanges were asked for; null otherwise.</summary>
    public IReadOnlyList<ExchangeDocument>? List { get; init; }
}

internal sealed record ExchangeDocument
{
    public required string? Key { get; init; }
    public required long Number { get; init; }
    public required string? FirstSeconds { get; init; }
    public required long? DurationNanoseconds { get; init; }
    public required bool RecordedWhole { get; init; }
    public required HttpPartRecord RequestHead { get; init; }
    public required HttpPartRecord RequestBody { get; init; }
    public required HttpPartRecord ResponseHead { get; init; }
    public required HttpPartRecord ResponseBody { get; init; }

    /// <summary>What its heads say, decoded when asked (`http-exchange-heads-v1`); absent from `http-exchanges-v1`.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExchangeHeadsDocument? Heads { get; init; }
}

/// <summary>
/// What an exchange's heads say (`contracts/http-exchanges-v1.md` §5f): what its request asked for, of which host, and
/// what its response answered, as `icat exchanges --decode` lists them; each head's decoding; and what each head declares
/// of its body beside what was recorded of it (§5e), null where the head was not decoded.
/// </summary>
internal sealed record ExchangeHeadsDocument
{
    public required string? Asked { get; init; }
    public required string? Host { get; init; }
    public required string? Answered { get; init; }

    /// <summary>The request head's decoding; null where no buffer of it was recorded.</summary>
    public required DecodingDocument? Request { get; init; }

    /// <summary>The response head's decoding; null where no buffer of it was recorded.</summary>
    public required DecodingDocument? Response { get; init; }

    public required HttpBodyDeclaration? RequestBody { get; init; }
    public required HttpBodyDeclaration? ResponseBody { get; init; }

    public static ExchangeHeadsDocument From(HttpExchangeHeads heads, ExchangeDocument exchange) => new()
    {
        Asked = heads.Asked,
        Host = heads.Host,
        Answered = heads.Answered,
        Request = heads.Request is { } request ? DecodingDocument.From(request) : null,
        Response = heads.Response is { } response ? DecodingDocument.From(response) : null,
        RequestBody = heads.RequestBody(exchange.RequestBody),
        ResponseBody = heads.ResponseBody(exchange.ResponseBody),
    };
}

/// <summary>
/// One head's decoding (`contracts/decoders-v1.md` §2): the part it read, the bytes it read by their offsets in the part,
/// each field with its value and the bytes it came from, what it did not decode and why, and whether it reached the
/// empty line that ends a head.
/// </summary>
internal sealed record DecodingDocument
{
    public required string? Part { get; init; }
    public required DecodedBytesDocument? Read { get; init; }
    public required IReadOnlyList<DecodedFieldDocument> Fields { get; init; }
    public required string? NotDecoded { get; init; }
    public required bool ReachedEnd { get; init; }

    public static DecodingDocument From(DecodedContent decoded) => new()
    {
        Part = decoded.Part,
        Read = decoded.Read is { } read ? new(read.First, read.Last) : null,
        Fields = [.. decoded.Fields.Select(field => new DecodedFieldDocument(field.Name, field.Value, field.Bytes.First, field.Bytes.Last))],
        NotDecoded = decoded.NotDecoded,
        ReachedEnd = decoded.ReachedEnd,
    };
}

/// <summary>Bytes of a head, by the offsets of the first and the last in its part.</summary>
internal sealed record DecodedBytesDocument(long First, long Last);

/// <summary>A decoded field: its name, its value as the decoder writes it, and the first and last bytes it came from.</summary>
internal sealed record DecodedFieldDocument(string Name, string Value, long First, long Last);

/// <summary>
/// Lists a session's HTTP exchanges (ADR-037, M8): WinINet capture records grouped by the exchange number and the process
/// their source fields and headers name, a number's uses told apart in time, each exchange bound to its client instance.
/// Every figure comes from records' metadata and source fields; no content is read.
/// </summary>
internal static class ExchangesCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? pidOption = command.TakeOption("--pid");
        string? listOption = command.TakeOption("--exchanges");
        string? policyOption = command.TakeOption("--evidence-policy");
        string? outputOption = command.TakeOption("--output");
        string? formatOption = command.TakeOption("--format");
        string? sessionPath = command.TakePositional();
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        bool decode = command.TryTakeFlag("--decode");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(command.Refusal(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null)
        {
            ConsoleUi.Failure("A session directory is required: icat exchanges <directory>.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sessionPath);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        int? pid = null;
        if (pidOption is not null)
        {
            if (!int.TryParse(pidOption, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                ConsoleUi.Failure($"--pid expects a process id; '{pidOption}' is not one.");
                return InterCatExitCode.InvalidInvocation;
            }

            pid = parsed;
        }

        int list = pid is null ? 0 : 20;
        if (listOption is not null
            && (!int.TryParse(listOption, NumberStyles.None, CultureInfo.InvariantCulture, out list) || list > 10_000))
        {
            ConsoleUi.Failure($"--exchanges expects a number of exchanges per process up to 10,000; '{listOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        // Decoded heads are read only of the exchanges listed, and enter a document only where it is asked for with them
        // (`contracts/http-exchanges-v1.md` §5f); a table is one line per listed exchange.
        ExportFormat? chosen = formatOption switch
        {
            null => outputOption?.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) == true ? ExportFormat.Csv : ExportFormat.Json,
            "json" => ExportFormat.Json,
            "csv" => ExportFormat.Csv,
            _ => null,
        };
        if (chosen is not { } format)
        {
            ConsoleUi.Failure(CommandLine.Expects("--format", formatOption, ["json", "csv"]));
            return InterCatExitCode.InvalidInvocation;
        }

        string? listProblem = formatOption is not null && outputOption is null
            ? "--format sets what --output writes: give --output with it."
            : decode && list == 0 ? "--decode reads the heads of the exchanges listed: give --pid, or --exchanges with a number."
            : format == ExportFormat.Csv && list == 0
                ? "A table holds one line per listed exchange: give --pid, or --exchanges with a number."
            : null;
        if (listProblem is not null)
        {
            ConsoleUi.Failure(listProblem);
            return InterCatExitCode.InvalidInvocation;
        }

        if (!EvidencePolicyOption.TryParse(policyOption, out EvidencePolicy policy, out string? policyRefusal))
        {
            ConsoleUi.Failure(policyRefusal!);
            return InterCatExitCode.InvalidInvocation;
        }

        string? outputPath = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (OutputFiles.Refusal(outputPath, overwrite) is { } refusedOutput)
        {
            ConsoleUi.Failure(refusedOutput);
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Acquiring the current generation of {full} and verifying every dependency.");
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (store.Current is null)
        {
            return Icat.NoSession();
        }

        ExchangesDocument document;
        IReadOnlyList<IReadOnlyList<HttpExchangeHeads>>? heads = null;
        Guid sessionId;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            sessionId = manifest.SessionId;
            if (SessionSegments.SourceClock(store.Root, manifest) is not { } clock)
            {
                ConsoleUi.Failure("The generation names no source clock, so no exchange has a process or a duration.");
                return InterCatExitCode.PermissionOrCapabilityFailure;
            }

            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
            ConsoleUi.Progress("Deriving process instances and grouping every HTTP buffer record by its exchange number.");
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, clock, fields, cancellationToken);
            HttpExchangeIndex index = HttpExchangeIndex.Derive(segments, fields, processes, cancellationToken);
            IReadOnlyList<MechanismCoverage> coverage =
                SessionCoverage.ForMechanisms(SessionSegments.CoverageLedger(store.Root, manifest), [Mechanism.Http]);
            if (decode)
            {
                // Each listed exchange's heads, read whole from the kept content, every group's in the document's order.
                ConsoleUi.Progress("Reading the listed exchanges' heads, each whole, from the content kept with consent to inspect it.");
                heads = [.. index.Groups.Where(group => pid is not { } only || group.ProcessId == only).Select(group =>
                    HttpExchangeHeadsQuery.Read(store, manifest, index, group, 0, list, cancellationToken))];
            }

            document = Describe(full, manifest.Generation, index, coverage, clock, policy, pid, list, heads);
        }

        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document, heads);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, format == ExportFormat.Csv ? Table(sessionId, document, heads) : payload,
                cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success(heads is null
                ? $"HTTP exchanges written to {outputPath}."
                : $"HTTP exchanges and what their heads say written to {outputPath}.");
            if (heads is not null)
            {
                ConsoleUi.Warn(HeadsContent);
            }
        }

        // A head recorded and not decoded leaves the answer partial, as icat content --decode's is.
        bool undecoded = heads?.Any(group => group.Any(exchange => exchange.Request is { Read: null } || exchange.Response is { Read: null }))
            == true;
        return store.Recovery.RolledBackToLastKnownGood || undecoded ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
    }

    /// <summary>
    /// What a document or file of decoded heads holds, said in `http-exchange-heads-v1` and beside a file written: content,
    /// to be shared only as that content would be.
    /// </summary>
    internal const string HeadsContent = "It holds what the listed exchanges' heads say, decoded from content the session "
        + "kept with consent to inspect it: it is content, to be shared only as that content would be. The decoder named "
        + "made each decoding at its version; a later version may decode the same bytes otherwise.";

    private static ExchangesDocument Describe(
        string path,
        long generation,
        HttpExchangeIndex index,
        IReadOnlyList<MechanismCoverage> coverage,
        SourceClockDescriptor clock,
        EvidencePolicy policy,
        int? pid,
        int list,
        IReadOnlyList<IReadOnlyList<HttpExchangeHeads>>? heads)
    {
        List<ExchangesGroupDocument> groups = [];
        foreach (HttpExchangeGroup group in index.Groups)
        {
            if (pid is { } only && group.ProcessId != only)
            {
                continue;
            }

            IReadOnlyList<HttpExchangeHeads>? decoded = heads?[groups.Count];

            bool admitted = group.Process.IsAdmittedUnder(policy);
            ProcessInstance? instance = admitted ? index.Processes.Instances[group.Process.Instance] : null;
            string? channelKey = instance is null ? null : HttpExchangeKeys.Channel(instance.Id);
            IReadOnlyList<HttpExchange> exchanges = index.ExchangesOf(group);
            long[] durations = [.. exchanges.Select(exchange => exchange.DurationNanoseconds).OfType<long>().Order()];
            groups.Add(new()
            {
                ProcessId = group.ProcessId,
                Process = instance is null ? null : ProcessInstanceDocument.From(instance, clock),
                Unattributed = admitted
                    ? null
                    : group.Process.IsBound ? ProcessBindingReason.NotAdmittedByPolicy.ToString() : group.Process.Reason.ToString(),
                Key = channelKey,
                Exchanges = group.Exchanges,
                RecordedWhole = group.Complete,
                Records = group.Records,
                RequestBytes = group.RequestBytes,
                ResponseBytes = group.ResponseBytes,
                MedianDurationNanoseconds = durations.Length == 0 ? null : durations[(durations.Length - 1) / 2],
                List = list == 0
                    ? null
                    : [.. exchanges.Take(list).Select((exchange, at) => Listed(new ExchangeDocument
                    {
                        Key = channelKey is null
                            ? null
                            : HttpExchangeKeys.Exchange(channelKey, exchange.First.Stream, exchange.First.Epoch, exchange.First.Ordinal),
                        Number = exchange.Number,
                        FirstSeconds = SessionText.Seconds(clock, exchange.FirstTicks),
                        DurationNanoseconds = exchange.DurationNanoseconds,
                        RecordedWhole = exchange.Complete,
                        RequestHead = exchange.RequestHead,
                        RequestBody = exchange.RequestBody,
                        ResponseHead = exchange.ResponseHead,
                        ResponseBody = exchange.ResponseBody,
                    }, decoded?[at]))],
            });
        }

        return new()
        {
            Contract = heads is null ? "http-exchanges-v1" : "http-exchange-heads-v1",
            Content = heads is null ? null : HeadsContent,
            Decoder = heads is null ? null : ContentDecoders.HttpHeadDecoder,
            Path = path,
            Generation = generation,
            GroupingRule = HttpExchangeIndex.GroupingRule,
            BindingRule = ProcessInstanceIndex.BindingRule,
            EvidencePolicy = policy.ToString(),
            RecordsWithoutExchange = index.WithoutExchange,
            Coverage = coverage,
            Groups = groups,
            Caveats =
            [
                "An exchange is one use of its client process's exchange number, told apart in time from another use of it "
                    + "(ADR-037); the number restarts with each client process.",
                "Every figure is read from the records' metadata and source fields - buffer counts, their measured bytes, "
                    + "their places and ends - and never from their content, which icat content reads when asked.",
                "A part is recorded whole when its buffers run from one flagged first to one flagged last, numbered from 0 "
                    + "in order. WinINet raises no record for an empty request body, so a request without one lacks nothing.",
                "A duration runs from an exchange's first recorded buffer to its response's last, as the client library held "
                    + "them; an exchange whose response end was not recorded has none.",
            ],
        };
    }

    /// <summary>A listed exchange, with what its heads say where they were decoded.</summary>
    private static ExchangeDocument Listed(ExchangeDocument exchange, HttpExchangeHeads? heads) =>
        heads is null ? exchange : exchange with { Heads = ExchangeHeadsDocument.From(heads, exchange) };

    /// <summary>The listed exchanges as a table (`http-exchanges-v1` §5f), with what their heads say where they were decoded.</summary>
    private static string Table(Guid sessionId, ExchangesDocument document, IReadOnlyList<IReadOnlyList<HttpExchangeHeads>>? heads) =>
        HttpExchangeTable.Csv(
        [
            .. document.Groups.SelectMany((group, at) => (group.List ?? []).Select((exchange, position) => new HttpExchangeLine
            {
                SessionId = sessionId,
                Generation = document.Generation,
                ProcessId = group.ProcessId,
                Process = group.Process?.ImageName,
                ProcessKey = group.Key,
                Unattributed = group.Unattributed,
                Key = exchange.Key,
                Number = exchange.Number,
                FirstSeconds = exchange.FirstSeconds,
                DurationNanoseconds = exchange.DurationNanoseconds,
                RecordedWhole = exchange.RecordedWhole,
                RequestHead = exchange.RequestHead,
                RequestBody = exchange.RequestBody,
                ResponseHead = exchange.ResponseHead,
                ResponseBody = exchange.ResponseBody,
                Heads = heads?[at][position],
            })),
        ], decoded: heads is not null);

    private static void Render(ExchangesDocument document, IReadOnlyList<IReadOnlyList<HttpExchangeHeads>>? heads)
    {
        ConsoleUi.Heading("HTTP exchanges");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(document.Generation));
        ConsoleUi.Field("Rules", $"{document.GroupingRule} over {document.BindingRule}, evidence policy "
            + EvidencePolicyOption.Typed(document.EvidencePolicy));
        if (document.RecordsWithoutExchange > 0)
        {
            ConsoleUi.Field("Unnumbered", document.RecordsWithoutExchange == 1
                ? "1 HTTP record names no exchange and belongs to none"
                : string.Create(CultureInfo.CurrentCulture,
                    $"{document.RecordsWithoutExchange:N0} HTTP records name no exchange and belong to none"));
        }

        // Whether an exchange listed nowhere could have been seen at all is the ledger's to say (R21).
        ConsoleUi.Note(SessionCoverage.Sentence(document.Coverage[0], "the session"));
        ConsoleUi.Line();
        if (document.Groups.Count == 0)
        {
            ConsoleUi.Note(document.RecordsWithoutExchange > 0
                ? "No HTTP exchange is grouped: its HTTP records name no exchange number, so none can be told apart - as in a "
                    + "redacted package made by an earlier version of InterCat, which withheld the numbers."
                : "This session holds no HTTP exchange: it holds no WinINet capture record, or none of the process asked "
                    + "for.");
            return;
        }

        ConsoleUi.Table(
            ["Process", "Exchanges", "Whole", "Buffers", "Sent", "Received", "Median"],
            [.. document.Groups.Select(group => (IReadOnlyList<string>)
            [
                group.Process is { } process
                    ? string.Create(CultureInfo.InvariantCulture, $"{process.ImageName ?? "executable not witnessed"} · {group.ProcessId}")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"PID {group.ProcessId} ({BindingText.Reason(Enum.Parse<ProcessBindingReason>(group.Unattributed!))})"),
                ConsoleUi.Count(group.Exchanges),
                ConsoleUi.Count(group.RecordedWhole),
                ConsoleUi.Count(group.Records),
                ConsoleUi.Size(group.RequestBytes),
                ConsoleUi.Size(group.ResponseBytes),
                group.MedianDurationNanoseconds is { } median ? OperationText.Duration(median, CultureInfo.CurrentCulture) : "untimed",
            ])]);

        if (heads is not null)
        {
            ConsoleUi.Line();
            ContentDecoderInfo decoder = ContentDecoders.HttpHeadDecoder;
            ConsoleUi.Note($"Heads decoded by {decoder.Name}, version {decoder.Version}: each read "
                + "whole, from bytes kept with consent to inspect them, and nothing decoded is kept unless --output writes it. "
                + "A head not whole, or kept without consent, is not decoded, and why is said beneath its exchanges, as is a "
                + "body that does not hold the length its head declared.");
        }

        for (int index = 0; index < document.Groups.Count; index++)
        {
            ExchangesGroupDocument group = document.Groups[index];
            if (group.List is not { Count: > 0 })
            {
                continue;
            }

            ConsoleUi.Line();
            ConsoleUi.Heading(string.Create(CultureInfo.CurrentCulture, $"Exchanges of PID {group.ProcessId}, the first {group.List!.Count:N0}"));
            if (heads?[index] is { } decoded)
            {
                Decoded(group.List, decoded);
                continue;
            }

            ConsoleUi.Table(
                ["At", "Exchange", "Took", "Request head", "Request body", "Response head", "Response body"],
                [.. group.List.Select(exchange => (IReadOnlyList<string>)
                [
                    exchange.FirstSeconds ?? "-",
                    ConsoleUi.Count(exchange.Number),
                    exchange.DurationNanoseconds is { } duration ? OperationText.Duration(duration, CultureInfo.CurrentCulture) : "no end",
                    Part(exchange.RequestHead),
                    Part(exchange.RequestBody),
                    Part(exchange.ResponseHead),
                    Part(exchange.ResponseBody),
                ])]);
        }

        ConsoleUi.Line();
        foreach (string caveat in document.Caveats)
        {
            ConsoleUi.Note(caveat);
        }
    }

    /// <summary>
    /// The listed exchanges with what their heads say (`http-exchanges-v1` §5b): what each request asked for, of which host,
    /// and what its response answered; then, for each head not decoded, or decoded only in part, why.
    /// </summary>
    private static void Decoded(IReadOnlyList<ExchangeDocument> exchanges, IReadOnlyList<HttpExchangeHeads> heads)
    {
        ConsoleUi.Table(
            ["At", "Exchange", "Took", "Request", "Host", "Response", "Recorded"],
            [.. exchanges.Select((exchange, at) => (IReadOnlyList<string>)
            [
                exchange.FirstSeconds ?? "-",
                ConsoleUi.Count(exchange.Number),
                exchange.DurationNanoseconds is { } duration ? OperationText.Duration(duration, CultureInfo.CurrentCulture) : "no end",
                heads[at].Asked ?? (heads[at].Request is null ? "no head recorded" : "not decoded"),
                heads[at].Host ?? "-",
                heads[at].Answered ?? (heads[at].Response is null ? "no head recorded" : "not decoded"),
                exchange.RecordedWhole ? "whole" : "not whole",
            ])]);
        for (int at = 0; at < exchanges.Count; at++)
        {
            foreach ((string head, DecodedContent? decoding) in new[] { ("request", heads[at].Request), ("response", heads[at].Response) })
            {
                if (decoding?.NotDecoded is { } why)
                {
                    ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture, $"Exchange {exchanges[at].Number:N0}, its {head} head: {why}"));
                }
            }

            // A body that holds what its head declared goes unsaid; one that does not is said, which its buffers cannot show.
            foreach ((string body, HttpBodyDeclaration? declared) in new[]
            {
                ("request", heads[at].RequestBody(exchanges[at].RequestBody)),
                ("response", heads[at].ResponseBody(exchanges[at].ResponseBody)),
            })
            {
                if (declared is { Contradicts: true, Statement: { } statement })
                {
                    ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture, $"Exchange {exchanges[at].Number:N0}, its {body} body: {statement}"));
                }
            }
        }
    }

    /// <summary>A part in a cell: its bytes and buffers, and whether it was recorded whole.</summary>
    private static string Part(HttpPartRecord part) => !part.Recorded
        ? "none recorded"
        : string.Create(CultureInfo.CurrentCulture,
            $"{part.Bytes:N0} B in {part.Buffers:N0}{(part.Whole ? string.Empty : ", not whole")}");

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat exchanges <session-directory> [--pid <id>] [--exchanges <n>] [--evidence-policy <name>]");
        ConsoleUi.Line("               [--decode] [--output <path> [--format json|csv] [--overwrite]] [--json]");
        ConsoleUi.Line("  A session's HTTP exchanges through WinINet (ADR-037): its buffer records grouped by exchange number and");
        ConsoleUi.Line("  client process, each exchange with what was recorded of its request and response heads and bodies,");
        ConsoleUi.Line("  and how long it took. --pid narrows to one process and lists its first 20 exchanges; --exchanges");
        ConsoleUi.Line("  sets how many are listed per process. Its figures read no content; icat content reads a buffer's.");
        ConsoleUi.Line("  --decode reads the listed exchanges' heads, each whole, from content kept with consent to inspect it,");
        ConsoleUi.Line("  and says what each request asked for, of which host, and what its response answered; a head not");
        ConsoleUi.Line("  whole, or kept without consent, is not decoded, and why is said, as is a body that does not hold");
        ConsoleUi.Line("  the length its head declared. With --json or --output it writes them too, as http-exchange-heads-v1:");
        ConsoleUi.Line("  content, to be shared only as that content would be. --output writes JSON, or with --format csv or a");
        ConsoleUi.Line("  .csv path a table, one line per listed exchange, its cells safe from a spreadsheet's evaluation.");
        ConsoleUi.Line("  What the capture covered of HTTP is said from its coverage ledger, never from a count of none.");
    }
}
