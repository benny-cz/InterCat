using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// A fact that measures RPC call pairing only when asked: set <c>INTERCAT_RPC_SCALE_OUTPUT</c> to a new directory for its
/// report, and <c>INTERCAT_RPC_SCALE_SESSIONS</c> to keep the generated sessions for a later run. A measurement is not a
/// test of this machine's speed, so without the variable it is reported as skipped, never passed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RpcScaleFactAttribute : FactAttribute
{
    public const string Variable = "INTERCAT_RPC_SCALE_OUTPUT";
    public const string SessionsVariable = "INTERCAT_RPC_SCALE_SESSIONS";

    public RpcScaleFactAttribute()
    {
        if (OutputPath is null)
        {
            Skip = $"Set {Variable} to a new directory to measure RPC call pairing at 1M and 10M observations.";
        }
    }

    public static string? OutputPath => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path ? path : null;

    public static string? SessionsPath =>
        Environment.GetEnvironmentVariable(SessionsVariable) is { Length: > 0 } path ? path : null;
}

/// <summary>
/// RPC call pairing over finished sessions of 1M and 10M observations in which four records in ten are RPC call records,
/// each start with the procedure and protocol fields a real start carries: what a live generation pays to pair every call
/// again, what a reopened session pays for its first RPC channel rung, and pairing alone with every column in memory.
/// </summary>
public sealed class RpcPairingScaleTests
{
    private const int Pairs = 200;
    private const long RowSpacingTicks = 600;
    private const int ChunkRows = 500_000;
    private const int PairingRuns = 5;
    private const string Generator = "rpc-pairing-v1";
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [RpcScaleFact(DisplayName = "§12: RPC call pairing is measured over sessions of 1M and 10M observations")]
    public void MeasureRpcPairingAtScale()
    {
        string output = Path.GetFullPath(RpcScaleFactAttribute.OutputPath!);
        Directory.CreateDirectory(output);
        string sessionsRoot = Path.GetFullPath(RpcScaleFactAttribute.SessionsPath ?? Path.Combine(output, "sessions"));
        var sessions = new List<Dictionary<string, object?>>();
        foreach (long rows in new long[] { 1_000_000, 10_000_000 })
        {
            string path = Path.Combine(sessionsRoot, string.Create(CultureInfo.InvariantCulture, $"rpc-{rows}"));
            List<double>? generations = Ensure(path, rows);
            sessions.Add(Measure(path, rows, generations));
        }

        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema"] = "intercat.rpc-pairing.v1",
            ["measuredUtc"] = DateTimeOffset.UtcNow,
            ["machine"] = $"{Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS") ?? "?"} logical processors · {Environment.OSVersion}",
            ["processorsUsed"] = Environment.ProcessorCount,
            ["configuration"] =
#if DEBUG
                "Debug",
#else
                "Release",
#endif
            ["notes"] = new[]
            {
                "Sessions are synthetic and finished: 200 client and 200 server processes created at the start, one record "
                    + "every 60 µs, four records in ten an RPC call start or stop, each start carrying a procedure number and "
                    + "a protocol sequence as source fields. Every call pairs, so a session of N records holds N/5 calls. "
                    + "Published in chunks of 500,000 records, then its derivation checkpoint.",
                "perGenerationMs is what a live session pays when its RPC rung is open: after each chunk is published, "
                    + "every call of the generation is paired again (SessionRpcCalls.Channels on a cleared derivation cache).",
                "firstRungColdMs is the first RPC channel list of a process after a reopen, which reads the calls the "
                    + "session's operation index keeps; warm is the same list again. firstRungUnindexedMs is the first list "
                    + "in a copy of the session published again without its operation index, as a live generation, or a "
                    + "session published before calls were kept, reads it: it opens the segments and pairs every call. "
                    + "pairingInMemoryMs pairs every call with every column already read, as a distribution of five runs.",
                "operationIndexMiB is the size of the operation index, and segmentsMiB that of the observation segments.",
            },
            ["sessions"] = sessions,
        }, Json));
    }

    private static List<double>? Ensure(string path, long rows)
    {
        string marker = Path.Combine(path, "rpc-pairing-session.json");
        if (File.Exists(marker))
        {
            using JsonDocument existing = JsonDocument.Parse(File.ReadAllText(marker));
            return existing.RootElement.GetProperty("rows").GetInt64() == rows
                && existing.RootElement.GetProperty("generator").GetString() == Generator
                ? null
                : throw new InvalidOperationException($"{path} holds another session; remove it to generate this one.");
        }

        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidOperationException($"{path} is not empty and holds no RPC pairing session; it is not overwritten.");
        }

        Directory.CreateDirectory(path);
        SessionStore writer = SessionStore.Open(LocalOwnedDirectory.Open(path), Guid.NewGuid(), "rpc-pairing");
        var generations = new List<double>();
        for (long first = 0; first < rows; first += ChunkRows)
        {
            ObservationRowV1[] chunk = Rows(first, (int)Math.Min(ChunkRows, rows - first));
            SourceFieldRowV1[] carried =
            [
                .. chunk
                    .Where(row => row.Mechanism == Mechanism.Rpc && row.Kind == ObservationKind.RequestStart)
                    .SelectMany(row => new[]
                    {
                        Field(row, SourceField.RpcProcedureNumber, (long)(row.RawRecordOrdinal % 40)),
                        Field(row, SourceField.RpcProtocolSequence, 3),
                    }),
            ];
            _ = Publish(writer, chunk, fields: carried);
            ProcessInstanceId client = FirstClient(writer);
            SessionDerivationCache.Clear();
            _ = SessionOverviewProjector.Project(writer);
            var clock = Stopwatch.StartNew();
            _ = SessionRpcCalls.Channels(writer, client);
            generations.Add(Round(clock.Elapsed.TotalMilliseconds));
        }

        CheckpointPublication checkpoint = SessionCheckpoints.Publish(writer, DateTimeOffset.UtcNow);
        Assert.Equal(CheckpointOutcome.Published, checkpoint.Outcome);
        File.WriteAllText(marker, JsonSerializer.Serialize(new Dictionary<string, object> { ["rows"] = rows, ["generator"] = Generator }));
        return generations;
    }

    private static Dictionary<string, object?> Measure(string path, long rows, List<double>? generations)
    {
        SessionDerivationCache.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        var clock = Stopwatch.StartNew();
        SessionStore store = SharedSessionStores.Open(path);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(store);
        double reopen = clock.Elapsed.TotalMilliseconds;
        ProcessInstanceId client = OverviewWorkspace.From(overview).Processes.First(process => process.ProcessId == 10_000).Id;
        clock.Restart();
        RpcChannelList cold = SessionRpcCalls.Channels(store, client);
        double coldMs = clock.Elapsed.TotalMilliseconds;
        long held = GC.GetTotalMemory(forceFullCollection: true) - before;
        clock.Restart();
        _ = SessionRpcCalls.Channels(store, client);
        double warmMs = clock.Elapsed.TotalMilliseconds;
        string key = cold.Channels[0].Key;
        clock.Restart();
        _ = SessionRpcCalls.Calls(store, key, 0, 100);
        double pageMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        RpcCallSpanPage spans = SessionRpcCalls.Spans(store, key, OverviewWorkspace.From(overview).Extent);
        double spansMs = clock.Elapsed.TotalMilliseconds;
        (double unindexedMs, double indexMiB, double segmentsMiB) = Unindexed(path, client);

        SessionManifestV1 manifest = store.Current!;
        SegmentReaderV1[] readers = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        SourceClockDescriptor sourceClock = SessionSegments.SourceClock(store.Root, manifest)!.Value;
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(readers, sourceClock, fields);
        var pairing = new List<double>();
        long calls = 0;
        for (int run = 0; run < PairingRuns; run++)
        {
            clock.Restart();
            calls = RpcCallIndex.Derive(readers, fields, processes, sourceClock).Totals.Calls;
            pairing.Add(Round(clock.Elapsed.TotalMilliseconds));
        }

        pairing.Sort();
        return new()
        {
            ["rows"] = rows,
            ["calls"] = calls,
            ["perGenerationMs"] = generations,
            ["reopenMs"] = Round(reopen),
            ["firstRungColdMs"] = Round(coldMs),
            ["firstRungWarmMs"] = Round(warmMs),
            ["firstRungUnindexedMs"] = unindexedMs,
            ["operationIndexMiB"] = indexMiB,
            ["segmentsMiB"] = segmentsMiB,
            ["heldAfterFirstRungMiB"] = Math.Round(held / 1048576.0, 1),
            ["callPageMs"] = Round(pageMs),
            ["spansMs"] = Round(spansMs),
            ["spansDrawn"] = spans.Calls.Count,
            ["pairingInMemoryMs"] = new Dictionary<string, double>
            {
                ["minimum"] = pairing[0],
                ["median"] = pairing[PairingRuns / 2],
                ["maximum"] = pairing[^1],
            },
        };
    }

    /// <summary>
    /// The first RPC channel list of <paramref name="client"/> in a copy of the session published again without its
    /// operation index, read by a viewer that has opened nothing; and the sizes of the index and of the segments.
    /// </summary>
    private static (double FirstRungMs, double IndexMiB, double SegmentsMiB) Unindexed(string path, ProcessInstanceId client)
    {
        string copy = path + "-unindexed";
        if (Directory.Exists(copy))
        {
            Directory.Delete(copy, recursive: true);
        }

        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(copy, Path.GetRelativePath(path, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        try
        {
            SessionManifestV1 manifest = SessionStore.OpenExisting(LocalOwnedDirectory.Open(copy)).Current!;
            double index = Math.Round(OperationIndex.NamedBy(manifest)!.LengthBytes / 1048576.0, 1);
            double segments = Math.Round(manifest.Dependencies
                .Where(dependency => dependency.Kind == StoreDependencyKind.Segment)
                .Sum(dependency => dependency.LengthBytes) / 1048576.0, 1);
            SessionStore writer = SessionStore.Open(LocalOwnedDirectory.Open(copy), manifest.SessionId, manifest.SourceIdentity);
            long next = writer.NextGeneration;
            using (StoreStagingFile checkpoint = writer.Stage(DerivationCheckpoint.FileNameFor(next), StoreDependencyKind.Index))
            using (StoreStagingFile counts = writer.Stage(SessionOverviewIndex.FileNameFor(next), StoreDependencyKind.Index))
            {
                checkpoint.Content.Write(SessionSegments.ReadVerified(
                    writer.Root, DerivationCheckpoint.NamedBy(manifest)!, DerivationCheckpoint.MaximumBytes));
                _ = checkpoint.Complete();
                counts.Content.Write(SessionSegments.ReadVerified(
                    writer.Root, SessionOverviewIndex.NamedBy(manifest)!, SessionOverviewIndex.MaximumBytes));
                _ = counts.Complete();
                _ = writer.CommitIndex([checkpoint, counts], manifest.Generation, DateTimeOffset.UtcNow, next);
            }

            SessionDerivationCache.Clear();
            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(copy));
            _ = SessionOverviewProjector.Project(reopened);
            var clock = Stopwatch.StartNew();
            _ = SessionRpcCalls.Channels(reopened, client);
            double first = Round(clock.Elapsed.TotalMilliseconds);
            reopened.ReleaseSegmentReaders();
            writer.ReleaseSegmentReaders();
            return (first, index, segments);
        }
        finally
        {
            SessionDerivationCache.Clear();
            Directory.Delete(copy, recursive: true);
        }
    }

    private static ProcessInstanceId FirstClient(SessionStore store) =>
        OverviewWorkspace.From(SessionOverviewProjector.Project(store)).Processes.First(process => process.ProcessId == 10_000).Id;

    private static double Round(double milliseconds) => Math.Round(milliseconds, 1);

    private static ObservationRowV1[] Rows(long first, int count)
    {
        var rows = new ObservationRowV1[count];
        for (int offset = 0; offset < count; offset++)
        {
            long index = first + offset;
            long ticks = 10 + (index * RowSpacingTicks);
            ulong ordinal = (ulong)index + 1;
            ObservationRowV1 row;
            if (index < 2 * Pairs)
            {
                bool client = index < Pairs;
                int pair = (int)(index % Pairs);
                row = Lifecycle(ticks, ObservationKind.Create, (client ? 10_000 : 20_000) + pair, ordinal);
            }
            else
            {
                long conversation = index - (2 * Pairs);
                int pair = (int)(conversation % Pairs);
                long round = conversation / Pairs;
                int clientPid = 10_000 + pair;
                int serverPid = 20_000 + pair;
                string clientEnd = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{40_000 + pair}");
                string serverEnd = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{8_000 + pair}");
                Guid clientCall = new((int)(round / 10), (short)pair, 1, 0, 0, 0, 0, 0, 0, 0, 1);
                Guid serverCall = new((int)(round / 10), (short)pair, 2, 0, 0, 0, 0, 0, 0, 0, 2);
                row = (round % 10) switch
                {
                    0 or 1 => Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 512, clientPid, ordinal).Between(clientEnd, serverEnd),
                    2 or 3 => Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, 512, serverPid, ordinal).Between(serverEnd, clientEnd),
                    4 or 5 => Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 4_096, serverPid, ordinal).Between(serverEnd, clientEnd),
                    6 => RpcCall(ticks, ObservationKind.RequestStart, Direction.Outbound, clientPid, ordinal, clientCall, ServiceControl),
                    7 => RpcCall(ticks, ObservationKind.RequestStart, Direction.Inbound, serverPid, ordinal, serverCall, ServiceControl),
                    8 => RpcCall(ticks, ObservationKind.RequestEnd, Direction.Inbound, serverPid, ordinal, serverCall, status: 0),
                    _ => RpcCall(ticks, ObservationKind.RequestEnd, Direction.Outbound, clientPid, ordinal, clientCall, status: 0),
                };
            }

            rows[offset] = row with { SessionRelativeTicks = ticks * 100 };
        }

        return rows;
    }
}
