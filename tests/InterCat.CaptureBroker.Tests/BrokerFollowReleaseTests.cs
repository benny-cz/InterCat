using System.Buffers.Binary;
using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// A capture whose evidence releases the chunks its follow gave up (ADR-048 decision 5): prepared only with live chunks, told
/// what the follow gave up by its owner as the owner renews its lease, in a field a broker that predates it skips.
/// </summary>
public sealed class BrokerFollowReleaseTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "R16: a renewal says how many chunks the capture's follow gave up, in an optional field and within its bound")]
    public void ARenewalSaysWhatTheFollowGaveUp()
    {
        CaptureId capture = new(Guid.NewGuid());

        // The count is an optional field, so a broker that predates it renews the lease and skips it.
        BrokerWireFrame frame = BrokerWireRequestCodec.Encode(new BrokerRenewOwnerLeaseRequest(capture, 5), Guid.NewGuid());
        (bool required, int offset) = FieldHeader(frame.Payload.Span, 2);
        Assert.False(required);
        Assert.Equal(5, BinaryPrimitives.ReadInt64LittleEndian(frame.Payload.Span[offset..]));
        Assert.Equal(new BrokerRenewOwnerLeaseRequest(capture, 5), BrokerWireRequestCodec.Decode(frame));

        // A count below zero or past the bound is refused written or read.
        Assert.Throws<InvalidDataException>(() => BrokerWireRequestCodec.Encode(new BrokerRenewOwnerLeaseRequest(capture, -1), Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => BrokerWireRequestCodec.Encode(
            new BrokerRenewOwnerLeaseRequest(capture, BrokerRenewOwnerLeaseRequest.MaximumFollowReleased + 1), Guid.NewGuid()));
        byte[] payload = frame.Payload.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), -3);
        Assert.Contains("as a count from zero", Assert.Throws<InvalidDataException>(() => BrokerWireRequestCodec.Decode(
            BrokerWireFrame.CreateRequest(BrokerMessageType.RenewOwnerLease, Guid.NewGuid(), payload))).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R16: a capture that releases what its follow gave up is prepared only with live chunks")]
    public void ReleasingWhatTheFollowGaveUpNeedsLiveChunks()
    {
        PreparedCapturePlan prepared = BrokerPrepareCompiler.Prepare(
            CompileFocused(), Quota, BrokerRetentionPolicy.ReleaseFollowed, Runtime, BrokerJournalPublication.Live).PreparedPlan!;
        Assert.Equal(BrokerRetentionPolicy.ReleaseFollowed, prepared.Retention);
        Assert.NotNull(prepared.PublicationInterval);

        BrokerPrepareResult once = BrokerPrepareCompiler.Prepare(
            CompileFocused(), Quota, BrokerRetentionPolicy.ReleaseFollowed, Runtime, BrokerJournalPublication.OnStop);
        Assert.Null(once.PreparedPlan);
        Assert.Contains("publishes live chunks", once.Refusal!.Message, StringComparison.Ordinal);
        Assert.Null(BrokerPrepareCompiler.Prepare(CompileFocused(), Quota, (BrokerRetentionPolicy)3, Runtime,
            BrokerJournalPublication.Live).PreparedPlan);

        // The wire refuses the same.
        Assert.Contains("publishes live chunks", Assert.Throws<InvalidDataException>(() => BrokerWireRequestCodec.Encode(
            Prepare(BrokerRetentionPolicy.ReleaseFollowed, BrokerJournalPublication.OnStop), Guid.NewGuid())).Message,
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => BrokerWireRequestCodec.Encode(
            Prepare((BrokerRetentionPolicy)3, BrokerJournalPublication.Live), Guid.NewGuid()));
    }

    [Fact(DisplayName = "R16: only an owner whose lease was renewed tells the runtime what its capture's follow gave up")]
    public async Task OnlyARenewedOwnerTellsTheRuntime()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new BrokerFakeRuntime();
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry, new InMemoryBrokerLifecycleStore(), runtime, clock, TimeSpan.FromSeconds(20));
        CaptureId capture = (await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA)).CaptureId!.Value;

        // The owner renews and says what its follow gave up; a renewal that says nothing tells nothing.
        Assert.Equal(BrokerOperationCode.LeaseRenewed, (await coordinator.RenewOwnerLeaseAsync(capture, OwnerA, 3)).Code);
        Assert.Equal(BrokerOperationCode.LeaseRenewed, (await coordinator.RenewOwnerLeaseAsync(capture, OwnerA)).Code);
        Assert.Equal([(capture, 3L)], runtime.FollowReleases);

        // Another owner is refused and tells nothing, and neither does an owner whose lease expired.
        Assert.Equal(BrokerOperationCode.CaptureUnavailable, (await coordinator.RenewOwnerLeaseAsync(capture, OwnerB, 9)).Code);
        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.Equal(BrokerOperationCode.LeaseExpired, (await coordinator.RenewOwnerLeaseAsync(capture, OwnerA, 9)).Code);
        Assert.Equal([(capture, 3L)], runtime.FollowReleases);
    }

    private static BrokerPrepareCaptureRequest Prepare(BrokerRetentionPolicy retention, BrokerJournalPublication publication) =>
        new("explore", null, [], false, false, Quota, retention, null, publication);

    /// <summary>Whether a payload's field is marked required, and where its value starts.</summary>
    private static (bool Required, int Offset) FieldHeader(ReadOnlySpan<byte> payload, ushort id)
    {
        for (int position = 0; position < payload.Length;)
        {
            ushort field = BinaryPrimitives.ReadUInt16LittleEndian(payload[position..]);
            bool required = payload[position + 3] != 0;
            int length = BinaryPrimitives.ReadInt32LittleEndian(payload[(position + 4)..]);
            if (field == id)
            {
                return (required, position + 8);
            }

            position += 8 + length;
        }

        throw new InvalidOperationException($"The payload holds no field {id}.");
    }
}
