using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>A session's HTTP exchanges, grouped by client process, each with what was recorded of its parts (ADR-037).</summary>
internal sealed record ExchangesDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required long Generation { get; init; }
    public required string GroupingRule { get; init; }
    public required string BindingRule { get; init; }
    public required string EvidencePolicy { get; init; }

    /// <summary>HTTP records whose source fields name no exchange, and so belong to none.</summary>
    public required long RecordsWithoutExchange { get; init; }

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
}

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
        string? sessionPath = command.TakePositional();
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null)
        {
            ConsoleUi.Failure("A session directory is required: icat exchanges <directory>");
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

        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated;
        if (policyOption is not null
            && (!Enum.TryParse(policyOption.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out policy)
                || !Enum.IsDefined(policy)))
        {
            ConsoleUi.Failure(
                $"--evidence-policy expects one of: {string.Join(", ", Enum.GetNames<EvidencePolicy>())}. '{policyOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? outputPath = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (outputPath is not null && File.Exists(outputPath) && !overwrite)
        {
            ConsoleUi.Failure($"{outputPath} exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Acquiring the current generation of {full} and verifying every dependency.");
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (store.Current is null)
        {
            ConsoleUi.Failure("This session has published no generation, so it holds no exchanges.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        ExchangesDocument document;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
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
            document = Describe(full, manifest.Generation, index, clock, policy, pid, list);
        }

        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"HTTP exchanges written to {outputPath}.");
        }

        return store.Recovery.RolledBackToLastKnownGood ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
    }

    private static ExchangesDocument Describe(
        string path,
        long generation,
        HttpExchangeIndex index,
        SourceClockDescriptor clock,
        EvidencePolicy policy,
        int? pid,
        int list)
    {
        List<ExchangesGroupDocument> groups = [];
        foreach (HttpExchangeGroup group in index.Groups)
        {
            if (pid is { } only && group.ProcessId != only)
            {
                continue;
            }

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
                    : [.. exchanges.Take(list).Select(exchange => new ExchangeDocument
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
                    })],
            });
        }

        return new()
        {
            Contract = "http-exchanges-v1",
            Path = path,
            Generation = generation,
            GroupingRule = HttpExchangeIndex.GroupingRule,
            BindingRule = ProcessInstanceIndex.BindingRule,
            EvidencePolicy = policy.ToString(),
            RecordsWithoutExchange = index.WithoutExchange,
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

    private static void Render(ExchangesDocument document)
    {
        ConsoleUi.Heading("HTTP exchanges");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(document.Generation));
        ConsoleUi.Field("Rules", $"{document.GroupingRule} over {document.BindingRule}, evidence policy {document.EvidencePolicy}");
        if (document.RecordsWithoutExchange > 0)
        {
            ConsoleUi.Field("Unnumbered", string.Create(CultureInfo.CurrentCulture,
                $"{document.RecordsWithoutExchange:N0} HTTP records name no exchange and belong to none"));
        }

        ConsoleUi.Line();
        if (document.Groups.Count == 0)
        {
            ConsoleUi.Note(document.RecordsWithoutExchange > 0
                ? "No HTTP exchange is grouped: its HTTP records name no exchange number, so none can be told apart - as in a "
                    + "redacted package made before revision 250, which withheld the numbers."
                : "This session holds no HTTP exchange: its capture kept no WinINet capture records, or none of the process "
                    + "asked for.");
            return;
        }

        ConsoleUi.Table(
            ["Process", "Exchanges", "Whole", "Buffers", "Sent", "Received", "Median"],
            [.. document.Groups.Select(group => (IReadOnlyList<string>)
            [
                group.Process is { } process
                    ? string.Create(CultureInfo.InvariantCulture, $"{process.ImageName ?? "PID"} · {group.ProcessId}")
                    : string.Create(CultureInfo.InvariantCulture, $"PID {group.ProcessId} ({group.Unattributed})"),
                ConsoleUi.Count(group.Exchanges),
                ConsoleUi.Count(group.RecordedWhole),
                ConsoleUi.Count(group.Records),
                ConsoleUi.Size(group.RequestBytes),
                ConsoleUi.Size(group.ResponseBytes),
                group.MedianDurationNanoseconds is { } median ? OperationText.Duration(median, CultureInfo.CurrentCulture) : "-",
            ])]);

        foreach (ExchangesGroupDocument group in document.Groups.Where(group => group.List is { Count: > 0 }))
        {
            ConsoleUi.Line();
            ConsoleUi.Heading(string.Create(CultureInfo.CurrentCulture, $"Exchanges of PID {group.ProcessId}, the first {group.List!.Count:N0}"));
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

    /// <summary>A part in a cell: its bytes and buffers, and whether it was recorded whole.</summary>
    private static string Part(HttpPartRecord part) => !part.Recorded
        ? "none recorded"
        : string.Create(CultureInfo.CurrentCulture,
            $"{part.Bytes:N0} B in {part.Buffers:N0}{(part.Whole ? string.Empty : ", not whole")}");

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat exchanges <session-directory> [--pid <id>] [--exchanges <n>] [--evidence-policy <name>]");
        ConsoleUi.Line("               [--output <path> [--overwrite]] [--json]");
        ConsoleUi.Line("  A session's HTTP exchanges through WinINet (ADR-037): its buffer records grouped by exchange number and");
        ConsoleUi.Line("  client process, each exchange with what was recorded of its request and response heads and bodies,");
        ConsoleUi.Line("  and how long it took. --pid narrows to one process and lists its first 20 exchanges; --exchanges");
        ConsoleUi.Line("  sets how many are listed per process. Nothing here reads content: icat content reads a buffer's.");
    }
}
