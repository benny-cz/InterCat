using System.Text.Json;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// A person's pins on a session (store-v1 §8, ADR-046): what each keeps through every release, what it allows the session
/// to hold, and that pins that cannot be read refuse what they might forbid rather than let it through.
/// </summary>
public sealed partial class EvidenceRetentionTests
{
    [Fact(DisplayName = "I18: a pin keeps the records read from its moment: no interval release passes it, and none by record number is made while it stands")]
    public void APinKeepsTheRecordsFromItsMoment()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4, batchCapacity: 2);
        DerivedGenerationResult second = Publish(session.Store, 5, batchCapacity: 2);
        _ = Publish(session.Store, 6, batchCapacity: 2);
        SessionManifestV1 recorded = session.Store.Current!;
        var chunk = new IntervalJournalRelease { Chunks = [first.JournalName], Records = 4 };

        // A pin is placed beside the generations: none is published, and the sweep keeps it and its lock.
        RetentionPin pin = session.Store.Pin(400, recorded.HeldBytes(), "  the failover  ", Committed);
        Assert.Equal((400L, recorded.HeldBytes(), "the failover", Committed), (pin.FromNanoseconds, pin.AllowanceBytes, pin.Reason, pin.PlacedUtc));
        Assert.Equal(pin.Id.ToString("N")[..8], pin.ShortId);
        Assert.Equal([pin], session.Store.Pins());
        Assert.Equal(recorded.Generation, session.Store.Current!.Generation);
        Assert.True(File.Exists(Path.Combine(session.Path, RetentionPinsV1.FileName)));
        Assert.True(File.Exists(Path.Combine(session.Path, RetentionPinsV1.LockFileName)));
        Assert.DoesNotContain(session.Reopen().Recovery.OrphanFiles, name => name.StartsWith("retention-pins", StringComparison.Ordinal));
        Assert.Equal([pin], session.Store.Pins());
        Assert.Contains("pins", Assert.Throws<ArgumentException>(() =>
            session.Store.Stage(RetentionPinsV1.FileName, StoreDependencyKind.Segment)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => session.Store.Stage(RetentionPinsV1.LockFileName, StoreDependencyKind.Index));

        // An interval release whose boundary passes the pin - giving up a record read at 400 - is refused, naming the pin.
        RetentionPinnedException passed = Assert.Throws<RetentionPinnedException>(() => session.Store.CommitIntervalRelease(
            [], [], chunk, new ReleasedInterval(401, 4, 0), "past the pin", recorded.Generation, Committed));
        Assert.Equal(pin, passed.Pin);
        Assert.StartsWith("A pin keeps every record read from 0.0000004 s (the failover), and this release would give up records "
            + "read after it.", passed.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was published.", passed.Message, StringComparison.Ordinal);

        // So is any release by record number, which cannot say when its records were read.
        RetentionPinnedException numbered = Assert.Throws<RetentionPinnedException>(() =>
            session.Store.ReleaseJournalChunks([first.JournalName], 4, "by number", Committed, Committed));
        Assert.Equal(pin, numbered.Pin);
        Assert.Contains("A release by record number cannot say when its records were read", numbered.Message, StringComparison.Ordinal);
        Assert.Equal(recorded.Generation, session.Store.Current!.Generation);

        // One whose every record was read before the pin is published.
        RetentionOutcome outcome = session.Store.CommitIntervalRelease(
            [], [], chunk, new ReleasedInterval(400, 4, 0), "up to the pin", recorded.Generation, Committed, nowUtc: Committed);
        Assert.Equal(400L, outcome.Manifest.LatestRelease(RetentionExtentKind.Interval)!.Record.Interval!.BoundaryNanoseconds);

        // No pin keeps what was released: one before the boundary is refused, naming the earliest moment a pin can keep.
        InvalidOperationException early = Assert.Throws<InvalidOperationException>(() =>
            session.Store.Pin(399, long.MaxValue, "too late", Committed));
        Assert.Equal("The records read before 0.0000004 s were released (up to the pin), so no pin can keep them: the earliest "
            + "moment a pin keeps every record from is 0.0000004 s. No pin was placed.", early.Message);
        RetentionPin atBoundary = session.Store.Pin(400, long.MaxValue, "from the boundary", Committed);
        Assert.Equal([pin, atBoundary], session.Store.Pins());

        // Removed, a pin keeps nothing, and a release by record number goes on once none stands.
        Assert.Equal(pin, session.Store.Unpin(pin.Id));
        Assert.Null(session.Store.Unpin(pin.Id));
        Assert.Throws<RetentionPinnedException>(() =>
            session.Store.ReleaseJournalChunks([second.JournalName], 5, "by number", Committed, Committed));
        Assert.Equal(atBoundary, session.Store.Unpin(atBoundary.Id));
        Assert.Empty(session.Store.Pins());
        Assert.Equal(
            outcome.Manifest.Generation + 1,
            session.Store.ReleaseJournalChunks([second.JournalName], 5, "by number, unpinned", Committed, Committed).Manifest.Generation);

        // A single journal's prefix is refused while a pin stands, as a recording's chunks are.
        using var single = new TemporarySession();
        _ = Publish(single.Store, 8, batchCapacity: 2);
        _ = single.Store.Pin(0, long.MaxValue, "everything", Committed);
        Assert.Contains("cannot say when its records were read", Assert.Throws<RetentionPinnedException>(() =>
            JournalRetention.Release(single.Store, 4, "by number", Committed, Committed)).Message, StringComparison.Ordinal);
        Assert.Equal(1, single.Store.Current!.Generation);
    }

    [Fact(DisplayName = "I18: a pin declares what it lets the session hold, and is refused when that is less than the session holds")]
    public void APinDeclaresItsAllowance()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, 4);
        long held = session.Store.Current!.HeldBytes();
        Assert.Equal(session.Store.Current.Dependencies.Sum(dependency => dependency.LengthBytes), held);

        InvalidOperationException less = Assert.Throws<InvalidOperationException>(() =>
            session.Store.Pin(0, held - 1, "too little", Committed));
        Assert.StartsWith($"This session holds {InterCat.Domain.ByteSizeText.Of(held)}, and a pin allowing it "
            + $"{InterCat.Domain.ByteSizeText.Of(held - 1)} would allow less than it already keeps", less.Message, StringComparison.Ordinal);
        Assert.Empty(session.Store.Pins());
        Assert.False(File.Exists(Path.Combine(session.Path, RetentionPinsV1.FileName)));

        // A pin keeps from a session time, allows something, and says why in a bounded reason.
        Assert.Throws<ArgumentException>(() => session.Store.Pin(-1, held, "before the session", Committed));
        Assert.Throws<ArgumentException>(() => session.Store.Pin(0, 0, "nothing allowed", Committed));
        Assert.Throws<ArgumentException>(() => session.Store.Pin(0, held, " ", Committed));
        Assert.Throws<ArgumentException>(() => session.Store.Pin(0, held, new string('x', RetentionPin.MaximumReasonLength + 1), Committed));
        Assert.Equal(held, session.Store.Pin(0, held, new string('x', RetentionPin.MaximumReasonLength), Committed).AllowanceBytes);

        // A session keeps a bounded number of pins, listed earliest moment first whatever order they were placed in.
        for (int count = 1; count < RetentionPinsV1.MaximumPins; count++)
        {
            _ = session.Store.Pin(RetentionPinsV1.MaximumPins - count, held, "one more", Committed);
        }

        Assert.Contains("the most it keeps", Assert.Throws<InvalidOperationException>(() =>
            session.Store.Pin(0, held, "one too many", Committed)).Message, StringComparison.Ordinal);
        Assert.Equal(RetentionPinsV1.MaximumPins, session.Store.Pins().Count);
        Assert.Equal(Enumerable.Range(0, RetentionPinsV1.MaximumPins).Select(moment => (long)moment),
            session.Store.Pins().Select(pin => pin.FromNanoseconds));
    }

    [Fact(DisplayName = "I18: pins that cannot be read refuse every release and every change, rather than give up or lose what they keep")]
    public void UnreadablePinsRefuseReleases()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4, batchCapacity: 2);
        _ = Publish(session.Store, 5, batchCapacity: 2);
        SessionManifestV1 recorded = session.Store.Current!;
        RetentionPin pin = session.Store.Pin(1_000, long.MaxValue, "kept", Committed);
        string path = Path.Combine(session.Path, RetentionPinsV1.FileName);
        string written = File.ReadAllText(path);
        var pins = JsonSerializer.Deserialize<RetentionPinsV1>(written, SessionManifestV1.Json)!;
        Assert.Equal((RetentionPinsV1.CurrentFormatVersion, recorded.SessionId), (pins.FormatVersion, pins.SessionId));

        (string Bytes, string Problem)[] damaged =
        [
            ("{", "it is not a pins file this InterCat can read"),
            (written.Replace("\"reason\"", "\"why\"", StringComparison.Ordinal), "it is not a pins file this InterCat can read"),
            ("null", "it holds nothing"),
            (Serialize(pins with { FormatVersion = 2 }), "it declares format 2, and this InterCat reads format 1"),
            (Serialize(pins with { SessionId = Guid.NewGuid() }), "it names another session"),
            (Serialize(pins with { SessionId = Guid.Empty }), "it names another session"),
            (Serialize(pins with { Pins = [pin, pin] }), "it names one pin twice"),
            (Serialize(pins with { Pins = [pin with { Id = Guid.Empty }] }), "one of its pins is damaged"),
            (Serialize(pins with { Pins = [pin with { AllowanceBytes = 0 }] }), "one of its pins is damaged"),
            (Serialize(pins with { Pins = [.. Enumerable.Range(0, RetentionPinsV1.MaximumPins + 1).Select(_ => pin with { Id = Guid.NewGuid() })] }),
                "it holds more than the 64 pins a session keeps"),
            (new string(' ', (int)RetentionPinsV1.MaximumFileBytes + 1), "it is larger than a pins file can be"),
        ];
        foreach ((string bytes, string problem) in damaged)
        {
            File.WriteAllText(path, bytes);
            string unreadable = $"This session's pins, in retention-pins.json, could not be read: {problem}.";
            Assert.Equal(unreadable, Assert.Throws<InvalidDataException>(() => session.Store.Pins()).Message);

            // A release cannot tell what they keep, so none is made; nor is one written over them, losing them.
            RetentionPinnedException interval = Assert.Throws<RetentionPinnedException>(() => session.Store.CommitIntervalRelease(
                [], [], new() { Chunks = [first.JournalName], Records = 4 }, new ReleasedInterval(1, 4, 0), "anything",
                recorded.Generation, Committed));
            Assert.Null(interval.Pin);
            Assert.Equal(unreadable + " No release is made that a pin might forbid, so nothing was published.", interval.Message);
            Assert.Null(Assert.Throws<RetentionPinnedException>(() =>
                session.Store.ReleaseJournalChunks([first.JournalName], 4, "anything", Committed, Committed)).Pin);
            Assert.Equal(unreadable + " No pin was placed.", Assert.Throws<InvalidDataException>(() =>
                session.Store.Pin(2_000, long.MaxValue, "another", Committed)).Message);
            Assert.Equal(unreadable + " No pin was removed.", Assert.Throws<InvalidDataException>(() =>
                session.Store.Unpin(pin.Id)).Message);
            Assert.Equal(bytes, File.ReadAllText(path));
            Assert.Equal(recorded.Generation, session.Store.Current!.Generation);
        }

        // Written back, they are read again.
        File.WriteAllText(path, written);
        Assert.Equal([pin], session.Store.Pins());

        static string Serialize(RetentionPinsV1 pins) => JsonSerializer.Serialize(pins, SessionManifestV1.Json);
    }

    [Fact(DisplayName = "I18: placing a pin waits while a release holds the session's pins, so it is judged against what the release published")]
    public async Task APinWaitsForAReleaseHoldingThePins()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, 4);
        long held = session.Store.Current!.HeldBytes();

        // Another process's release holds the pins.
        Task<RetentionPin> placing;
        using (FileStream releasing = new(Path.Combine(session.Path, RetentionPinsV1.LockFileName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            placing = Task.Run(() => session.Store.Pin(0, held, "while releasing", Committed));
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(placing.IsCompleted);
            Assert.Empty(session.Store.Pins());
        }

        RetentionPin placed = await placing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([placed], session.Store.Pins());
    }
}
