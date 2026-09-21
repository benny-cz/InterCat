using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Storage.Tests;

public sealed class JournalProbeTests
{
    private static readonly JsonSerializerOptions FixtureJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact(DisplayName = "R17: metadata-only admission persists no body for an unknown or content schema")]
    public void MetadataOnlyPolicyOmitsUnapprovedBodies()
    {
        JournalFixture fixture = LoadFixture();
        List<JournalProbeAdmissionResult> admitted = AdmitFixture(fixture);
        JournalExpected expected = LoadExpected();

        Assert.Equal(expected.Dispositions, admitted.Select(result => result.Envelope.Body.Disposition.ToString()));
        Assert.True(admitted[1].Envelope.Body.RetainedBytes.IsEmpty);
        Assert.True(admitted[2].Envelope.Body.RetainedBytes.IsEmpty);

        byte[] encoded = JournalProbeCodec.EncodeBatch([.. admitted.Select(result => result.Envelope)]);
        Assert.False(Contains(encoded, fixture.Records[1].Body));
        Assert.False(Contains(encoded, fixture.Records[2].Body));
    }

    [Fact(DisplayName = "I13: every journal probe record has retained evidence or an explicit omission")]
    public void EverySourceRecordIsAccountedFor()
    {
        JournalFixture fixture = LoadFixture();
        JournalExpected expected = LoadExpected();
        JournalProbeAttributionReport report = JournalProbeAdmission.Summarize(AdmitFixture(fixture));

        Assert.True(report.IsComplete);
        Assert.Equal(expected.InputRecords, report.InputRecords);
        Assert.Equal(expected.EnvelopeRecords, report.EnvelopeRecords);
        Assert.Equal(expected.RetainedBodies, report.RetainedBodies);
        Assert.Equal(expected.ExplicitBodyOmissions, report.ExplicitBodyOmissions);
        Assert.Equal(expected.RecordsWithoutBodies, report.RecordsWithoutBodies);
        Assert.Equal(expected.OmittedExtendedItems, report.OmittedExtendedItems);
    }

    [Fact(DisplayName = "Journal probe replays admitted extended data and raw identities byte-for-byte")]
    public void ExtendedDataRoundTrips()
    {
        JournalFixture fixture = LoadFixture();
        List<JournalProbeAdmissionResult> admitted = AdmitFixture(fixture);
        byte[] encoded = JournalProbeCodec.EncodeBatch([.. admitted.Select(result => result.Envelope)]);

        IReadOnlyList<JournalProbeEnvelope> replayed = JournalProbeCodec.DecodeBatch(encoded);

        Assert.Equal(admitted.Count, replayed.Count);
        for (int index = 0; index < replayed.Count; index++)
        {
            JournalProbeEnvelope before = admitted[index].Envelope;
            JournalProbeEnvelope after = replayed[index];
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.NativeTimestamp, after.NativeTimestamp);
            Assert.Equal(before.ActivityId, after.ActivityId);
            Assert.Equal(before.Body.Disposition, after.Body.Disposition);
            Assert.True(before.Body.RetainedBytes.Span.SequenceEqual(after.Body.RetainedBytes.Span));
            Assert.Equal(before.ExtendedItems.Count, after.ExtendedItems.Count);
            for (int itemIndex = 0; itemIndex < before.ExtendedItems.Count; itemIndex++)
            {
                Assert.Equal(before.ExtendedItems[itemIndex].Type, after.ExtendedItems[itemIndex].Type);
                Assert.Equal(before.ExtendedItems[itemIndex].Flags, after.ExtendedItems[itemIndex].Flags);
                Assert.True(before.ExtendedItems[itemIndex].Bytes.Span.SequenceEqual(after.ExtendedItems[itemIndex].Bytes.Span));
            }
        }
    }

    [Fact(DisplayName = "Journal probe copies admitted bytes before source memory can change")]
    public void AdmissionOwnsCopiedBytes()
    {
        JournalFixture fixture = LoadFixture();
        byte[] body = fixture.Records[0].Body;
        byte[] extended = fixture.Records[0].ExtendedItems[0].Bytes;
        JournalProbeAdmissionResult result = JournalProbeAdmission.Admit(
            BuildSource(fixture, fixture.Records[0], 1, body, extended),
            BuildPolicy(fixture));

        body.AsSpan().Fill(0xff);
        extended.AsSpan().Fill(0xee);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Envelope.Body.RetainedBytes.ToArray());
        Assert.Equal(new byte[] { 16, 17, 18, 19, 20 }, result.Envelope.ExtendedItems[0].Bytes.ToArray());
    }

    [Fact(DisplayName = "Journal probe rejects a corrupted checksummed batch")]
    public void CorruptedBatchIsRejected()
    {
        JournalFixture fixture = LoadFixture();
        byte[] encoded = JournalProbeCodec.EncodeBatch(
            [.. AdmitFixture(fixture).Select(result => result.Envelope)]);
        encoded[^1] ^= 0xff;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => JournalProbeCodec.DecodeBatch(encoded));
        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Journal probe scoped content truncation records both retained and original lengths")]
    public void ScopedContentTruncationIsExplicit()
    {
        JournalFixture fixture = LoadFixture();
        JournalRecord record = fixture.Records[2];
        var policy = new JournalProbePolicy(
            "scoped-content-v0",
            JournalProbeAdmissionMode.ScopedContent,
            maximumRetainedBodyBytes: 5,
            maximumExtendedItemBytes: 16,
            [new(fixture.Provider, record.EventId, record.Version, record.SchemaFingerprint)],
            []);

        JournalProbeBodyEvidence body = JournalProbeAdmission.Admit(
            BuildSource(fixture, record, 3, record.Body, null),
            policy).Envelope.Body;

        Assert.Equal(JournalProbeBodyDisposition.TruncatedByPolicy, body.Disposition);
        Assert.Equal(record.Body.Length, body.OriginalLength);
        Assert.Equal(5, body.RetainedBytes.Length);
    }

    private static List<JournalProbeAdmissionResult> AdmitFixture(JournalFixture fixture)
    {
        JournalProbePolicy policy = BuildPolicy(fixture);
        var results = new List<JournalProbeAdmissionResult>(fixture.Records.Count);
        for (int index = 0; index < fixture.Records.Count; index++)
        {
            JournalRecord record = fixture.Records[index];
            results.Add(JournalProbeAdmission.Admit(
                BuildSource(fixture, record, index + 1, record.Body, null),
                policy));
        }

        return results;
    }

    private static JournalProbePolicy BuildPolicy(JournalFixture fixture) =>
        new(
            fixture.Policy.Id,
            JournalProbeAdmissionMode.MetadataOnly,
            fixture.Policy.MaximumRetainedBodyBytes,
            fixture.Policy.MaximumExtendedItemBytes,
            fixture.Policy.PermittedDescriptors.Select(descriptor => new JournalProbeDescriptor(
                fixture.Provider,
                descriptor.EventId,
                descriptor.Version,
                descriptor.SchemaFingerprint)),
            fixture.Policy.PermittedExtendedTypes);

    private static JournalProbeSourceRecord BuildSource(
        JournalFixture fixture,
        JournalRecord record,
        int ordinal,
        byte[] body,
        byte[]? firstExtendedOverride)
    {
        IReadOnlyList<JournalProbeExtendedItem> extended = record.ExtendedItems
            .Select((item, index) => new JournalProbeExtendedItem(
                item.Type,
                item.Flags,
                index == 0 && firstExtendedOverride is not null ? firstExtendedOverride : item.Bytes))
            .ToArray();
        return new()
        {
            Id = new(new CaptureId(fixture.Capture), 1, 1, (ulong)ordinal),
            ProviderGuid = fixture.Provider,
            EventId = record.EventId,
            Version = record.Version,
            Opcode = 1,
            NativeTimestamp = new(new ClockId(fixture.Clock), TimestampEncoding.Qpc, 1_000 + ordinal),
            HeaderProcessId = 4242,
            HeaderThreadId = 43,
            ProcessorNumber = 2,
            PointerSize = 8,
            ActivityId = Guid.Parse("99999999-9999-4999-8999-999999999999"),
            RelatedActivityId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            SchemaFingerprint = record.SchemaFingerprint,
            BodyClassification = record.Classification,
            Body = body,
            ExtendedItems = extended,
            ScopeMatched = record.ScopeMatched,
        };
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty)
        {
            return true;
        }

        for (int index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.Slice(index, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private static JournalFixture LoadFixture()
    {
        string json = File.ReadAllText(Path.Combine(RepositoryRoot(), "fixtures", "FX-JOURNAL-001", "scenario.json"));
        JournalFixtureDto dto = JsonSerializer.Deserialize<JournalFixtureDto>(json, FixtureJson)
            ?? throw new InvalidDataException("FX-JOURNAL-001 scenario is empty.");
        JournalRecord[] records =
        [
            .. dto.Records.Select(record => new JournalRecord(
                record.EventId,
                record.Version,
                record.SchemaFingerprint,
                record.Classification,
                Convert.FromBase64String(record.BodyBase64),
                record.ScopeMatched,
                [.. record.ExtendedItems.Select(item => new JournalExtendedItem(
                    item.Type,
                    item.Flags,
                    Convert.FromBase64String(item.BytesBase64)))])),
        ];
        return new(dto.Capture, dto.Clock, dto.Provider, dto.Policy, records);
    }

    private static JournalExpected LoadExpected()
    {
        string json = File.ReadAllText(Path.Combine(RepositoryRoot(), "fixtures", "FX-JOURNAL-001", "expected.json"));
        return JsonSerializer.Deserialize<JournalExpected>(json, FixtureJson)
            ?? throw new InvalidDataException("FX-JOURNAL-001 expected result is empty.");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }

    private sealed record JournalFixtureDto
    {
        public Guid Capture { get; init; }
        public Guid Clock { get; init; }
        public Guid Provider { get; init; }
        public JournalPolicy Policy { get; init; } = new();
        public IReadOnlyList<JournalRecordDto> Records { get; init; } = [];
    }

    private sealed record JournalRecordDto
    {
        public int EventId { get; init; }
        public int Version { get; init; }
        public string SchemaFingerprint { get; init; } = string.Empty;
        public JournalProbeBodyClassification Classification { get; init; }
        public string BodyBase64 { get; init; } = string.Empty;
        public bool ScopeMatched { get; init; }
        public IReadOnlyList<JournalExtendedItemDto> ExtendedItems { get; init; } = [];
    }

    private sealed record JournalExtendedItemDto
    {
        public ushort Type { get; init; }
        public ushort Flags { get; init; }
        public string BytesBase64 { get; init; } = string.Empty;
    }
}

internal sealed record JournalFixture(
    Guid Capture,
    Guid Clock,
    Guid Provider,
    JournalPolicy Policy,
    IReadOnlyList<JournalRecord> Records);

internal sealed record JournalPolicy
{
    public string Id { get; init; } = string.Empty;
    public int MaximumRetainedBodyBytes { get; init; }
    public int MaximumExtendedItemBytes { get; init; }
    public IReadOnlyList<ushort> PermittedExtendedTypes { get; init; } = [];
    public IReadOnlyList<JournalDescriptor> PermittedDescriptors { get; init; } = [];
}

internal sealed record JournalDescriptor
{
    public int EventId { get; init; }
    public int Version { get; init; }
    public string SchemaFingerprint { get; init; } = string.Empty;
}

internal sealed record JournalRecord(
    int EventId,
    int Version,
    string SchemaFingerprint,
    JournalProbeBodyClassification Classification,
    byte[] Body,
    bool ScopeMatched,
    IReadOnlyList<JournalExtendedItem> ExtendedItems);

internal sealed record JournalExtendedItem(ushort Type, ushort Flags, byte[] Bytes);

internal sealed record JournalExpected
{
    public int InputRecords { get; init; }
    public int EnvelopeRecords { get; init; }
    public int RetainedBodies { get; init; }
    public int ExplicitBodyOmissions { get; init; }
    public int RecordsWithoutBodies { get; init; }
    public int OmittedExtendedItems { get; init; }
    public IReadOnlyList<string> Dispositions { get; init; } = [];
}
