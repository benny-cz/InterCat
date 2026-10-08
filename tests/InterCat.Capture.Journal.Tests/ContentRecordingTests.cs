using System.Buffers;
using System.Diagnostics;
using System.Text;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// A live recording under the content fixture's scoped content policy (ADR-036): each message's metadata is journaled as
/// every record's is, and its bytes are kept beside the journal chunk - whole, cut to the record limit, or, once the session
/// limit is reached, omitted with their length, which stops the capture.
/// </summary>
public sealed class ContentRecordingTests
{
    [Fact(DisplayName = "I21: a recording keeps each fixture message's bytes beside its journal chunk, and stops at its content limit")]
    public async Task ARecordingKeepsContentBesideItsJournal()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "content-tests");
        CompiledBodyAdmissionPolicy policy = CaptureBodyAdmissionPolicies.ScopedContentFixture(8, 20, ContentInspectionMode.HexAndText);
        OwnedSessionPlan plan = Plan(policy);
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        string[] messages = ["hello", "0123456789AB", "abcdefghij"];
        for (int index = 0; index < messages.Length; index++)
        {
            AdmitMessage(host, plan, Encoding.UTF8.GetBytes(messages[index]), now + index, index + 1);
        }

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            plan, host, store, _ => host.Delivered.Task, DateTimeOffset.UtcNow);

        // "hello" whole, the next cut to 8, and the third past the 20-byte session limit: omitted, and the capture stopped.
        Assert.Equal((3L, 13L, true), (result.ContentFragments, result.ContentKeptBytes, result.ContentLimitReached));
        Assert.Equal(3, result.JournaledRecords);
        StoreDependency chunk = Assert.Single(store.Current!.Dependencies, dependency => dependency.Kind == StoreDependencyKind.Content);
        Assert.Equal(ContentChunkV1.FileName(store.Current.Generation), chunk.Name);
        using FileStream stream = store.Root.OpenOwnedFile(chunk.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
        ContentChunkContentsV1 contents = ContentChunkV1.Read(stream, plan.Identity.CaptureId);
        Assert.Equal((CaptureBodyAdmissionPolicies.ScopedContentFixturePolicyId, 8, ContentInspectionV1.HexAndText),
            (contents.Header.PolicyId, contents.Header.RecordLimit, contents.Header.Inspection));
        Assert.Equal(
            [ContentDispositionV1.Whole, ContentDispositionV1.TruncatedByRecordLimit, ContentDispositionV1.OmittedBySessionLimit],
            contents.Entries.Select(entry => entry.Fragment.Disposition));
        Assert.Equal([5L, 12L, 10L], contents.Entries.Select(entry => entry.Fragment.OriginalLength!.Value));
        Assert.All(contents.Entries, entry => Assert.Equal(
            (ContentClassificationV1.ApplicationPayload, Direction.Outbound, ContentEncodingV1.Binary),
            (entry.Fragment.Classification, entry.Fragment.Direction, entry.Fragment.Encoding)));
        Assert.Equal("hello"u8.ToArray(), ContentChunkV1.ReadBytes(stream, contents.Entries[0], 64));
        Assert.Equal("01234567"u8.ToArray(), ContentChunkV1.ReadBytes(stream, contents.Entries[1], 64));

        // Each message's metadata is an ordinary row: the application's own send, its length measured, its owner the process
        // its payload names.
        ObservationRowV1[] rows = [.. SessionSegments.Names(store.Current).SelectMany(name =>
        {
            SegmentReaderV1 segment = SessionSegments.Open(store, store.Current, name);
            return Enumerable.Range(0, segment.RowCount).Select(segment.Row);
        })];
        Assert.Equal([5L, 12L, 10L], rows.Select(row => row.ByteValue!.Value));
        Assert.All(rows, row => Assert.Equal((Mechanism.ApplicationSdk, ByteDomain.ApplicationPayload, 4_242),
            (row.Mechanism, row.ByteDomain!.Value, row.OwnerProcessId!.Value)));
    }

    [Fact(DisplayName = "I21: a recording keeps each WinINet buffer beside its journal chunk, with its exchange, its place and its ends")]
    public async Task ARecordingKeepsHttpBuffers()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "content-tests");
        CompiledBodyAdmissionPolicy policy = CaptureBodyAdmissionPolicies.ScopedContentRequest(ContentCapturePolicyCompiler.Compile(new()
        {
            SourceId = WindowsSourceCatalog.WinInetCaptureSourceId,
            Mechanism = Mechanism.Http,
            ProcessIds = [4_242],
            ChannelSelectors = [ContentCapturePolicyCompiler.EveryChannel],
            MaximumRecordBytes = 16,
            MaximumSessionBytes = 1_024,
            Retention = ContentRetentionMode.StopAtLimit,
            Inspection = ContentInspectionMode.HexAndText,
        }));
        WindowsSourceDefinition source = WindowsSourceCatalog.Find(WindowsSourceCatalog.WinInetCaptureSourceId)!;
        SourceAdmissionPlan http = AdmissionPlanCompiler.Compile(source, ManifestParser.Parse(WinInetManifest), 0, bodyPolicy: policy);
        var plan = new OwnedSessionPlan
        {
            Identity = CaptureSessionIdentity.Create("content-test", 4242),
            Providers =
            [
                new ProviderEnablementRequest
                {
                    SourceId = source.SourceId,
                    ProviderName = source.ProviderName,
                    ProviderGuid = http.ProviderGuid,
                    Level = 4,
                    MatchAnyKeyword = WindowsSourceCatalog.WinInetCaptureKeywords,
                    EventIdsToEnable = [2001, 2002, 2003, 2004],
                    ProcessIdsToInclude = [4_242],
                },
            ],
            Sources = [http],
        };

        // One exchange: a request head longer than the 16-byte record limit, and a response body in two buffers, the
        // second the empty one that ends it.
        (ushort EventId, uint Sequence, uint Flags, string Text)[] buffers =
            [(2001, 0, 3, "POST /x HTTP/1.1\r\n\r\n"), (2004, 0, 1, "hello"), (2004, 1, 2, "")];
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        for (int index = 0; index < buffers.Length; index++)
        {
            (ushort eventId, uint sequence, uint flags, string text) = buffers[index];
            IReadOnlyList<AdmittedSlotPlan> slots = http.Events.Single(descriptor => descriptor.EventId == eventId).Slots;
            int Slot(string field) => slots.Select((slot, at) => (slot, at)).Single(pair => pair.slot.FieldName == field).at;
            byte[] message = Encoding.ASCII.GetBytes(text);
            var admitted = new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = eventId,
                Version = 0,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                HeaderProcessId = 4_242,
                HeaderThreadId = 4_243,
                RecordOrdinal = index + 1,
            };
            admitted.SetSlot(Slot("SessionId"), 7);
            admitted.SetSlot(Slot("SequenceNumber"), sequence);
            admitted.SetSlot(Slot("Flags"), flags);
            admitted.SetSlot(Slot("PayloadByteLength"), message.Length);
            int kept = Math.Min(message.Length, policy.ContentRecordLimit);
            byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(kept, 1));
            message.AsSpan(0, kept).CopyTo(rented);
            admitted.SetContent(rented, kept, message.Length);
            host.Admit(admitted, new DeliveredRecord(http.ProviderGuid, admitted.EventId, admitted.Version, admitted.TimestampQpc));
        }

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            plan, host, store, _ => host.Delivered.Task, DateTimeOffset.UtcNow);

        // The head kept to the record limit with its length, the body's buffers whole, the empty one kept as empty.
        Assert.Equal((3L, 21L, false), (result.ContentFragments, result.ContentKeptBytes, result.ContentLimitReached));
        StoreDependency chunk = Assert.Single(store.Current!.Dependencies, dependency => dependency.Kind == StoreDependencyKind.Content);
        using FileStream stream = store.Root.OpenOwnedFile(chunk.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
        ContentChunkContentsV1 contents = ContentChunkV1.Read(stream, plan.Identity.CaptureId);
        Assert.Equal((CaptureBodyAdmissionPolicies.ScopedContentRequestPolicyId, 16), (contents.Header.PolicyId, contents.Header.RecordLimit));
        Assert.Equal(
            [(ContentDispositionV1.TruncatedByRecordLimit, 20L, Direction.Outbound), (ContentDispositionV1.Whole, 5L, Direction.Inbound),
                (ContentDispositionV1.Whole, 0L, Direction.Inbound)],
            contents.Entries.Select(entry => (entry.Fragment.Disposition, entry.Fragment.OriginalLength!.Value, entry.Fragment.Direction)));
        Assert.Equal("POST /x HTTP/1.1"u8.ToArray(), ContentChunkV1.ReadBytes(stream, contents.Entries[0], 64));

        // Each buffer is an HTTP row whose payload names no process, bound to the client that raised it, with its exchange,
        // its place in its part and its ends kept as source fields.
        ObservationRowV1[] rows = [.. SessionSegments.Names(store.Current).SelectMany(name =>
        {
            SegmentReaderV1 segment = SessionSegments.Open(store, store.Current, name);
            return Enumerable.Range(0, segment.RowCount).Select(segment.Row);
        })];
        Assert.All(rows, row => Assert.Equal((Mechanism.Http, (int?)null, 4_242),
            (row.Mechanism, row.OwnerProcessId, RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.HeaderProcessId)!.Value)));
        SourceFieldRowV1[] fields = [.. SessionSegments.FieldNames(store.Current).SelectMany(name =>
        {
            SegmentReaderV1 segment = SessionSegments.Open(store, store.Current, name);
            return Enumerable.Range(0, segment.RowCount).Select(segment.FieldRow);
        })];
        Assert.Equal([7L, 7L, 7L], fields.Where(field => field.Field == SourceField.HttpExchangeId).Select(field => field.Value!.Value));
        Assert.Equal([0L, 0L, 1L], fields.Where(field => field.Field == SourceField.ContentBufferSequence).Select(field => field.Value!.Value));
        Assert.Equal([3L, 1L, 2L], fields.Where(field => field.Field == SourceField.ContentBufferFlags).Select(field => field.Value!.Value));
    }

    // WinINet's capture provider's registered layout, as TDH gave it on Windows 10.0.26220 (ADR-037).
    private const string WinInetManifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Microsoft-Windows-WinINet-Capture" guid="{a70ff94f-570b-4979-ba5c-e59c9feab61b}">
           <templates>
            <template tid="Capture">
             <data name="SessionId" inType="win:UInt32" />
             <data name="SequenceNumber" inType="win:UInt32" />
             <data name="Flags" inType="win:UInt32" />
             <data name="PayloadByteLength" inType="win:UInt32" />
             <data name="Payload" inType="win:Binary" length="PayloadByteLength" />
            </template>
           </templates>
           <events>
            <event value="2001" version="0" level="win:Informational" template="Capture" />
            <event value="2002" version="0" level="win:Informational" template="Capture" />
            <event value="2003" version="0" level="win:Informational" template="Capture" />
            <event value="2004" version="0" level="win:Informational" template="Capture" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

    /// <summary>
    /// Admits one message the fixture's workload sent, its bytes copied as the callback copies them: at most the record
    /// limit of the plan's policy, with the length the message had.
    /// </summary>
    internal static void AdmitMessage(ScriptedHost host, OwnedSessionPlan plan, byte[] message, long timestampQpc, long ordinal)
    {
        AdmittedEventPlan descriptor = plan.Sources[0].Events
            .Single(candidate => candidate.EventId == ContentFixtureEventSource.MessageSentId);
        int Slot(string field) =>
            descriptor.Slots.Select((slot, index) => (slot, index)).Single(pair => pair.slot.FieldName == field).index;
        var admitted = new AdmittedEvent
        {
            SourceIndex = 0,
            EventId = ContentFixtureEventSource.MessageSentId,
            Version = ContentFixtureEventSource.EventVersion,
            TimestampQpc = timestampQpc,
            TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            HeaderProcessId = 4_242,
            HeaderThreadId = 4_243,
            RecordOrdinal = ordinal,
        };
        admitted.SetSlot(Slot("processId"), 4_242);
        admitted.SetSlot(Slot("conversation"), 1);
        admitted.SetSlot(Slot("messageSize"), message.Length);
        int kept = Math.Min(message.Length, descriptor.BodyPolicy.ContentRecordLimit);
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(kept, 1));
        message.AsSpan(0, kept).CopyTo(rented);
        admitted.SetContent(rented, kept, message.Length);
        host.Admit(admitted, new DeliveredRecord(plan.Sources[0].ProviderGuid, admitted.EventId, admitted.Version, admitted.TimestampQpc));
    }

    /// <summary>A plan recording the content fixture alone, compiled from its catalog entry under <paramref name="policy"/>.</summary>
    internal static OwnedSessionPlan Plan(CompiledBodyAdmissionPolicy policy)
    {
        WindowsSourceDefinition fixture = WindowsSourceCatalog.Find(WindowsSourceCatalog.ContentFixtureSourceId)!;
        SourceAdmissionPlan source = AdmissionPlanCompiler.Compile(fixture, ManifestParser.Parse(fixture.EmbeddedManifest!), 0,
            bodyPolicy: policy);
        return new()
        {
            Identity = CaptureSessionIdentity.Create("content-test", 4242),
            Providers =
            [
                new ProviderEnablementRequest
                {
                    SourceId = source.SourceId,
                    ProviderName = fixture.ProviderName,
                    ProviderGuid = source.ProviderGuid,
                    Level = 4,
                    MatchAnyKeyword = 0,
                    EventIdsToEnable = [ContentFixtureEventSource.MessageSentId, ContentFixtureEventSource.MessageReceivedId],
                },
            ],
            Sources = [source],
        };
    }
}
