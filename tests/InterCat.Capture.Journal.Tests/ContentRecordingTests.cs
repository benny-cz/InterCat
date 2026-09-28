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
        IReadOnlyList<AdmittedSlotPlan> slots = plan.Sources[0].Events
            .Single(descriptor => descriptor.EventId == ContentFixtureEventSource.MessageSentId).Slots;
        int Slot(string field) => slots.Select((slot, index) => (slot, index)).Single(pair => pair.slot.FieldName == field).index;
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        string[] messages = ["hello", "0123456789AB", "abcdefghij"];
        for (int index = 0; index < messages.Length; index++)
        {
            byte[] message = Encoding.UTF8.GetBytes(messages[index]);
            var admitted = new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = ContentFixtureEventSource.MessageSentId,
                Version = ContentFixtureEventSource.EventVersion,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                HeaderProcessId = 4_242,
                HeaderThreadId = 4_243,
                RecordOrdinal = index + 1,
            };
            admitted.SetSlot(Slot("processId"), 4_242);
            admitted.SetSlot(Slot("conversation"), 1);
            admitted.SetSlot(Slot("messageSize"), message.Length);

            // What the callback copies: at most the record limit, with the length the message had.
            int kept = Math.Min(message.Length, policy.ContentRecordLimit);
            byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(kept, 1));
            message.AsSpan(0, kept).CopyTo(rented);
            admitted.SetContent(rented, kept, message.Length);
            host.Admit(admitted, new DeliveredRecord(plan.Sources[0].ProviderGuid, admitted.EventId, admitted.Version, admitted.TimestampQpc));
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

    /// <summary>A plan recording the content fixture alone, compiled from its catalog entry under <paramref name="policy"/>.</summary>
    private static OwnedSessionPlan Plan(CompiledBodyAdmissionPolicy policy)
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
