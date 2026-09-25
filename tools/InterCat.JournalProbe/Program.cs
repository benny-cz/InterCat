using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;
using InterCat.Storage;

const int DefaultRecordCount = 10_000;
const int DefaultIterations = 7;

string outputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("bench/results/journal-probe-portable.json");
int recordCount = args.Length > 1 && int.TryParse(args[1], out int parsedRecords) ? parsedRecords : DefaultRecordCount;
int iterations = args.Length > 2 && int.TryParse(args[2], out int parsedIterations) ? parsedIterations : DefaultIterations;
if (recordCount is < 4 or > 100_000 || iterations is < 3 or > 25)
{
    Console.Error.WriteLine("Usage: InterCat.JournalProbe [output.json] [records 4..100000] [iterations 3..25]");
    return 2;
}

List<JournalProbeSourceRecord> source = BuildCorpus(recordCount);
JournalProbePolicy metadataPolicy = BuildMetadataPolicy();
JournalProbePolicy originalPolicy = BuildOriginalPolicy(source);

_ = RunOnce(source, metadataPolicy);
List<ProbeSample> samples = [];
ProbeRun? final = null;
for (int iteration = 0; iteration < iterations; iteration++)
{
    final = RunOnce(source, metadataPolicy);
    samples.Add(final.Sample);
}

ProbeRun original = RunOnce(source, originalPolicy);
ProbeRun result = final ?? throw new InvalidOperationException("The probe produced no sample.");
int unexpectedRetainedBodies = result.Envelopes.Count(envelope =>
    envelope.Body.Classification is JournalProbeBodyClassification.Content or JournalProbeBodyClassification.OpaqueUnknown
    && !envelope.Body.RetainedBytes.IsEmpty);
bool replayIdentityMatched = result.Envelopes.Select(envelope => envelope.Id)
    .SequenceEqual(result.Replayed.Select(envelope => envelope.Id));
bool replayExtendedMatched = result.Envelopes.Zip(result.Replayed).All(pair =>
    pair.First.ExtendedItems.Count == pair.Second.ExtendedItems.Count
    && pair.First.ExtendedItems.Zip(pair.Second.ExtendedItems).All(items =>
        items.First.Type == items.Second.Type
        && items.First.Flags == items.Second.Flags
        && items.First.Bytes.Span.SequenceEqual(items.Second.Bytes.Span)));

ProbeSample median = MedianSample(samples);
var report = new ProbeReport
{
    SchemaVersion = 1,
    ProbeFormat = "journal-probe-v0",
    GeneratedUtc = DateTimeOffset.UtcNow,
    Environment = new()
    {
        OperatingSystem = RuntimeInformation.OSDescription,
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Framework = RuntimeInformation.FrameworkDescription,
        ProcessorCount = Environment.ProcessorCount,
        BuildConfiguration = BuildConfiguration(),
    },
    RecordCount = recordCount,
    Iterations = iterations,
    BaselineKind = "synthetic-original-evidence-copy; not ETL",
    DecisionReady = false,
    MetadataJournalBytes = result.EncodedBytes,
    SyntheticOriginalEvidenceBytes = original.EncodedBytes,
    MetadataToOriginalSizeRatio = original.EncodedBytes == 0 ? null : (double)result.EncodedBytes / original.EncodedBytes,
    Attribution = JournalProbeAdmission.Summarize(result.Results),
    UnexpectedUnapprovedBodiesRetained = unexpectedRetainedBodies,
    ReplayIdentityMatched = replayIdentityMatched,
    ReplayExtendedDataMatched = replayExtendedMatched,
    Samples = samples,
    Median = median,
    BlockingGaps =
    [
        "No ETL was produced or read, so this result cannot choose journal versus ETL.",
        "No elevated ETW acquisition ran, so callback, queue, disk-saturation and provider-loss overhead remain unmeasured.",
        "The v0 framing is disposable experimental code; contracts/journal-v1.md remains an IC-011 deliverable.",
    ],
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, JsonSerializer.Serialize(report, ProbeJson.Options) + Environment.NewLine);
Console.WriteLine(outputPath);
return 0;

static ProbeRun RunOnce(List<JournalProbeSourceRecord> source, JournalProbePolicy policy)
{
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    long started = Stopwatch.GetTimestamp();
    var results = new List<JournalProbeAdmissionResult>(source.Count);
    foreach (JournalProbeSourceRecord record in source)
    {
        results.Add(JournalProbeAdmission.Admit(record, policy));
    }

    List<JournalProbeEnvelope> envelopes = [.. results.Select(result => result.Envelope)];
    byte[] encoded = JournalProbeCodec.EncodeBatch(envelopes);
    IReadOnlyList<JournalProbeEnvelope> replayed = JournalProbeCodec.DecodeBatch(encoded);
    TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    double seconds = Math.Max(elapsed.TotalSeconds, 0.000_001);
    return new(
        results,
        envelopes,
        replayed,
        encoded.Length,
        new(elapsed.TotalMilliseconds, source.Count / seconds, encoded.Length / seconds, allocated));
}

static List<JournalProbeSourceRecord> BuildCorpus(int count)
{
    var capture = new CaptureId(Guid.Parse("66666666-6666-4666-8666-666666666666"));
    var clock = new ClockId(Guid.Parse("77777777-7777-4777-8777-777777777777"));
    Guid provider = Guid.Parse("88888888-8888-4888-8888-888888888888");
    var records = new List<JournalProbeSourceRecord>(count);
    for (int index = 0; index < count; index++)
    {
        int kind = index % 4;
        string fingerprint = kind switch
        {
            0 => "schema-known-metadata",
            1 => "schema-unknown-v2",
            2 => "schema-known-content",
            _ => "schema-header-only",
        };
        JournalProbeBodyClassification classification = kind switch
        {
            0 => JournalProbeBodyClassification.ApprovedMetadata,
            1 => JournalProbeBodyClassification.OpaqueUnknown,
            2 => JournalProbeBodyClassification.Content,
            _ => JournalProbeBodyClassification.None,
        };
        byte[] body = kind switch
        {
            0 => [1, 2, 3, 4],
            1 => "UNAPPROVED-SECRET-123"u8.ToArray(),
            2 => "CONTENT-SECRET-456"u8.ToArray(),
            _ => [],
        };
        JournalProbeExtendedItem[] extended = kind switch
        {
            0 => [new(1, 4, new byte[] { 16, 17, 18, 19, 20 }), new(99, 0, new byte[] { 0xde, 0xad })],
            1 => [new(2, 1, new byte[] { 32, 33, 34, 35, 36 })],
            _ => [],
        };
        records.Add(new()
        {
            Id = new(capture, 1, 1, (ulong)(index + 1)),
            ProviderGuid = provider,
            EventId = kind switch { 0 or 1 => 10, 2 => 11, _ => 12 },
            Version = kind == 1 ? 2 : 1,
            Opcode = 1,
            NativeTimestamp = new(clock, TimestampEncoding.Qpc, 1_000 + index),
            HeaderProcessId = 4242,
            HeaderThreadId = 43,
            ProcessorNumber = index % Math.Max(1, Environment.ProcessorCount),
            PointerSize = 8,
            ActivityId = Guid.Parse("99999999-9999-4999-8999-999999999999"),
            RelatedActivityId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            SchemaFingerprint = fingerprint,
            BodyClassification = classification,
            Body = body,
            ExtendedItems = extended,
            ScopeMatched = true,
        });
    }

    return records;
}

static JournalProbePolicy BuildMetadataPolicy() => new(
    "metadata-explore-v0",
    JournalProbeAdmissionMode.MetadataOnly,
    maximumRetainedBodyBytes: 8,
    maximumExtendedItemBytes: 16,
    [
        new(Guid.Parse("88888888-8888-4888-8888-888888888888"), 10, 1, "schema-known-metadata"),
        new(Guid.Parse("88888888-8888-4888-8888-888888888888"), 11, 1, "schema-known-content"),
    ],
    [1, 2]);

static JournalProbePolicy BuildOriginalPolicy(IEnumerable<JournalProbeSourceRecord> source) => new(
    "synthetic-original-v0",
    JournalProbeAdmissionMode.OriginalEvidence,
    maximumRetainedBodyBytes: 64,
    maximumExtendedItemBytes: 64,
    source.Select(record => new JournalProbeDescriptor(
        record.ProviderGuid,
        record.EventId,
        record.Version,
        record.SchemaFingerprint)),
    [1, 2, 99]);

static ProbeSample MedianSample(List<ProbeSample> samples)
{
    ProbeSample[] ordered = [.. samples.OrderBy(sample => sample.ElapsedMilliseconds)];
    return ordered[ordered.Length / 2];
}

static string BuildConfiguration()
{
#if DEBUG
    return "Debug";
#else
    return "Release";
#endif
}

internal static class ProbeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

internal sealed record ProbeRun(
    List<JournalProbeAdmissionResult> Results,
    List<JournalProbeEnvelope> Envelopes,
    IReadOnlyList<JournalProbeEnvelope> Replayed,
    int EncodedBytes,
    ProbeSample Sample);

internal sealed record ProbeSample(
    double ElapsedMilliseconds,
    double RecordsPerSecond,
    double EncodedBytesPerSecond,
    long AllocatedBytes);

internal sealed record ProbeEnvironment
{
    public required string OperatingSystem { get; init; }
    public required string ProcessArchitecture { get; init; }
    public required string Framework { get; init; }
    public required int ProcessorCount { get; init; }
    public required string BuildConfiguration { get; init; }
}

internal sealed record ProbeReport
{
    public required int SchemaVersion { get; init; }
    public required string ProbeFormat { get; init; }
    public required DateTimeOffset GeneratedUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required int RecordCount { get; init; }
    public required int Iterations { get; init; }
    public required string BaselineKind { get; init; }
    public required bool DecisionReady { get; init; }
    public required int MetadataJournalBytes { get; init; }
    public required int SyntheticOriginalEvidenceBytes { get; init; }
    public required double? MetadataToOriginalSizeRatio { get; init; }
    public required JournalProbeAttributionReport Attribution { get; init; }
    public required int UnexpectedUnapprovedBodiesRetained { get; init; }
    public required bool ReplayIdentityMatched { get; init; }
    public required bool ReplayExtendedDataMatched { get; init; }
    public required IReadOnlyList<ProbeSample> Samples { get; init; }
    public required ProbeSample Median { get; init; }
    public required IReadOnlyList<string> BlockingGaps { get; init; }
}
