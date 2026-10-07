using System.Globalization;
using System.Text;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §11.3's redacted package of an interval (`redacted-session-v1` §11): the interval's rows and, from outside it, the
/// lifecycle records of the processes it holds, bound and named as in the source, its coverage stated only for the
/// interval, and nothing of the rows it leaves out.
/// </summary>
public sealed partial class RedactedSessionPackageTests
{
    // The rich source's rows from 4.5 µs to 4.8 µs: the LAN pair and the IPv4-mapped send, of PIDs 1200 and 1500, the
    // first of them at the interval's first tick. Their lifecycle records from outside it are the agent's creation, and
    // the worker's creation and exit and the creation of the process that reused its PID.
    private static readonly TimeRange LanInterval = new(45, 48);

    [Fact(DisplayName = "I22: an interval package holds its rows and its processes' lifecycle records, bound and named as in the source")]
    public void AnIntervalPackageHoldsItsRowsAndItsProcesses()
    {
        using var source = new TemporarySession();
        PublishRichSource(source.Store);
        using var package = new PackageDirectory();

        RedactedSessionPackageResult result = RedactedSessionPackage.Create(source.Store, package.Path, Committed, LanInterval);
        SessionStore shared = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path));

        // The three rows of the interval and the four lifecycle records of PIDs 1200 and 1500 from outside it; nothing of
        // PIDs 4, 1300 or 1400, nor the source's other rows, timed or not.
        Assert.Equal((7L, 21L), (result.Counts.Rows, result.Source.SourceRows));
        Assert.Equal(new RedactedSessionInterval { StartTicks = 45, EndTicks = 48, LifecycleRowsOutside = 4 }, result.Source.Interval);
        Assert.Equal([1_000L, 4_000, 4_500, 4_600, 4_700, 5_000, 6_000],
            Segments(shared).SelectMany(Rows).Select(row => row.SessionRelativeTicks!.Value).Order());

        // Its processes are the source's instances of those PIDs - the agent, the worker and the process that reused the
        // worker's PID after it exited - and each of the interval's rows binds to its instance as its source row does.
        ProcessInstanceIndex sourceProcesses = ProcessInstanceIndex.Derive(Segments(source.Store), SourceClock,
            FieldSegments(source.Store));
        SourceClockDescriptor packageClock = SessionSegments.SourceClock(shared.Root, shared.Current!)!.Value;
        ProcessInstanceIndex packageProcesses = ProcessInstanceIndex.Derive(Segments(shared), packageClock,
            FieldSegments(shared));
        Assert.Equal(Shape(sourceProcesses, instance => instance.ProcessId is 1200 or 1500), Shape(packageProcesses, _ => true));
        Assert.Equal(Owners(Segments(source.Store), sourceProcesses, LanInterval), Owners(Segments(shared), packageProcesses, LanInterval));

        // The agent's parent, System, has no record in the interval: it is named by the agent's creation, and not linked.
        ProcessInstance agent = Assert.Single(packageProcesses.Instances, instance => instance.ParentProcessId == 4);
        Assert.Null(agent.Parent);
        Assert.NotNull(Assert.Single(sourceProcesses.Instances, instance => instance.ProcessId == 1200).Parent);

        // Each keeps its name: every process node is an executable pseudonym, never a bare PID.
        SessionOverviewBundle overview = SessionOverviewProjector.Project(shared);
        Assert.Equal(3, packageProcesses.Instances.Count);
        Assert.All(overview.Nodes, node => Assert.StartsWith(RedactedSessionPseudonyms.ExecutablePrefix, node.Name));

        // Its coverage over the interval is the source's, and outside it unknown, though the source covered it there.
        CoverageLedgerV1 sourceLedger = SessionSegments.CoverageLedger(source.Store.Root, source.Store.Current!)!;
        CoverageLedgerV1 ledger = SessionSegments.CoverageLedger(shared.Root, shared.Current!)!;
        CoverageEpochV1 epoch = Assert.Single(ledger.Epochs);
        Assert.Equal((45L, 47L, 45L, 47L), (epoch.FirstDeliveredNativeTicks!.Value, epoch.LastDeliveredNativeTicks!.Value,
            epoch.RecordedFromNativeTicks!.Value, epoch.RecordedToNativeTicks!.Value));
        Assert.Equal(sourceLedger.Epochs[0].Losses, epoch.Losses);
        Assert.Equal(States(sourceLedger, new(SourceEpoch + 45, SourceEpoch + 48)), States(ledger, new(45, 48)));
        Assert.Equal(CoverageState.PartialGap, SessionCoverage.Of(sourceLedger, Mechanism.Tcp, new(SourceEpoch + 10, SourceEpoch + 20)).State);
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(ledger, Mechanism.Tcp, new(10, 20)).State);
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(ledger, Mechanism.Tcp, new(48, 60)).State);

        // So over its whole time, which its lifecycle records reach beyond the interval, every mechanism's coverage is
        // unknown, where its source's whole capture lost records: an absence outside the interval is never an observed zero.
        Assert.True(ledger.HoldsRecordsOutsideItsEpochs);
        Assert.False(sourceLedger.HoldsRecordsOutsideItsEpochs);
        Assert.Equal(CoverageState.PartialGap, SessionCoverage.Of(sourceLedger, Mechanism.Tcp).State);
        Assert.All(overview.MechanismCoverage, coverage => Assert.Equal(
            (CoverageState.UnknownCoverage, SessionCoverage.OutsideItsEpochs), (coverage.State, coverage.Reason)));
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Capture(ledger));
        Assert.All(overview.Nodes, node => Assert.Equal(CoverageState.UnknownCoverage, node.Coverage));

        // Within the interval, where its ledger speaks, the overview carries what the source covered there.
        Assert.Equal(States(sourceLedger, new(SourceEpoch + 45, SourceEpoch + 48)), overview.IntervalCoverage!.Select(entry => entry.State));
        Assert.Null(SessionOverviewProjector.Project(source.Store).IntervalCoverage);

        // Its policy states the interval, and every reader says so in one sentence.
        SessionRedaction redaction = SessionRedaction.Read(shared.Root, shared.Current!)!;
        Assert.Equal(result.Source.Interval, redaction.Interval);
        Assert.Equal("It holds its source's records from 0.0000045 s to 0.0000048 s and, outside that interval, only the "
            + "lifecycle records of the processes it holds, so they keep their names; its coverage outside the interval is "
            + "unknown.", SessionRedaction.Holds(redaction.Interval!, CultureInfo.InvariantCulture));
        Assert.Contains(SessionRedaction.Holds(redaction.Interval!, CultureInfo.CurrentCulture), redaction.Statement(CultureInfo.CurrentCulture),
            StringComparison.Ordinal);
        Assert.Contains(redaction.Statement(CultureInfo.CurrentCulture), overview.Caveats);

        // Nothing of the rows it leaves out: not the pipe's name, nor a pseudonym of it. They are still read, so the byte scan
        // looks for every name, provider and schema of the source, as a whole package's does.
        using (var whole = new PackageDirectory())
        {
            RedactedSessionPackageResult all = RedactedSessionPackage.Create(source.Store, whole.Path, Committed);
            Assert.Equal((all.IdentityNeedles, all.NameNeedles), (result.IdentityNeedles, result.NameNeedles));
        }

        foreach (string file in Directory.EnumerateFiles(package.Path))
        {
            byte[] bytes = File.ReadAllBytes(file);
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Contoso-Secret-Pipe")));
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(SecretPipe)));
        }

        // The agent's creation carries its five fields, its wall-clock creation time redacted; no other field is held.
        Assert.Equal((5L, 1L), (result.Counts.SourceFieldRows, result.Counts.SourceFieldRowsRedacted));
        Assert.DoesNotContain(Segments(shared).SelectMany(Rows), row => row.Mechanism is Mechanism.NamedPipe or Mechanism.Rpc);
    }

    [Fact(DisplayName = "I22: an interval package's ledger keeps only the epochs that spoke for the interval, numbered again, or none")]
    public void AnIntervalPackagesLedgerSpeaksOnlyForTheInterval()
    {
        // Two epochs: the first spoke until 3 µs, the second from 3.1 µs. Only the second spoke for the interval.
        CoverageEpochV1 whole = Ledger().Epochs[0];
        CoverageLedgerV1 twoEpochs = Ledger() with
        {
            Epochs =
            [
                whole with { RecordedFromNativeTicks = null, RecordedToNativeTicks = null, LastDeliveredNativeTicks = SourceEpoch + 30 },
                whole with
                {
                    Epoch = 2,
                    FirstDeliveredNativeTicks = SourceEpoch + 31,
                    RecordedFromNativeTicks = SourceEpoch + 31,
                    Losses = [.. whole.Losses.Select(loss => loss with { Lost = 0 })],
                },
            ],
        };
        using (var source = new TemporarySession())
        {
            PublishRichSource(source.Store, twoEpochs);
            using var package = new PackageDirectory();
            RedactedSessionPackage.Create(source.Store, package.Path, Committed, LanInterval);
            SessionStore shared = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path));
            CoverageEpochV1 kept = Assert.Single(SessionSegments.CoverageLedger(shared.Root, shared.Current!)!.Epochs);
            Assert.Equal((1, 45L, 47L), (kept.Epoch, kept.FirstDeliveredNativeTicks!.Value, kept.LastDeliveredNativeTicks!.Value));

            // The second epoch lost nothing, so the interval is covered, though the source's capture lost records.
            Assert.Equal(CoverageState.Covered, SessionCoverage.Of(SessionSegments.CoverageLedger(shared.Root, shared.Current!),
                Mechanism.Tcp, new(45, 48)).State);
        }

        // An interval no epoch spoke for gives a package with no ledger: its coverage there is unknown, as the source's was.
        using (var source = new TemporarySession())
        {
            PublishRichSource(source.Store, Ledger() with { Epochs = [twoEpochs.Epochs[0]] });
            RedactedSessionPackagePreview preview = RedactedSessionPackage.Preview(source.Store, LanInterval);
            Assert.Equal((true, false), (preview.SourceCoverageLedger, preview.CoverageLedger));
            using var package = new PackageDirectory();
            RedactedSessionPackageResult result = RedactedSessionPackage.Create(source.Store, package.Path, Committed, LanInterval);
            SessionStore shared = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path));
            Assert.Null(SessionSegments.CoverageLedger(shared.Root, shared.Current!));
            Assert.False(result.Counts.CoverageLedger);
        }
    }

    [Fact(DisplayName = "I22: an interval's preview measures what its package holds, and an interval with no record is refused")]
    public void AnIntervalsPreviewMeasuresItsPackage()
    {
        using var source = new TemporarySession();
        PublishRichSource(source.Store);
        RedactedSessionPackagePreview preview = RedactedSessionPackage.Preview(source.Store, LanInterval);
        Assert.Equal((7L, 21L, 5L, 1L), (preview.Rows, preview.SourceRows, preview.SourceFieldRows, preview.SourceFieldRowsRedacted));
        Assert.Equal(4, preview.Interval!.LifecycleRowsOutside);
        Assert.True(preview.CoverageLedger && preview.SourceCoverageLedger);

        // Its end is not in it: without the IPv4-mapped send at 4.7 µs, PID 1500 still holds its LAN send, so only that row
        // goes.
        Assert.Equal(6L, RedactedSessionPackage.Preview(source.Store, new TimeRange(45, 47)).Rows);

        // A whole session's preview holds every row.
        RedactedSessionPackagePreview whole = RedactedSessionPackage.Preview(source.Store);
        Assert.Equal((21L, 21L, (RedactedSessionInterval?)null), (whole.Rows, whole.SourceRows, whole.Interval));

        // An interval no record lies in has nothing to package, and writes nothing.
        using var package = new PackageDirectory();
        InvalidOperationException empty = Assert.Throws<InvalidOperationException>(() =>
            RedactedSessionPackage.Create(source.Store, package.Path, Committed, new TimeRange(1_000, 2_000)));
        Assert.StartsWith("No record of this session lies from ", empty.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(package.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(package.Path)!,
            Path.GetFileName(package.Path) + ".partial-*"));
    }

    [Fact(DisplayName = "I22: a policy's interval that does not end after it starts, or holds as many records from outside it as the package, is refused")]
    public void AnImpossibleIntervalIsRefused()
    {
        RedactedSessionCounts counts = new()
        {
            Rows = 7,
            SourceFieldRows = 0,
            SourceFieldRowsRedacted = 0,
            CoverageLedger = false,
            Names = 0,
            ProcessesAndThreads = 0,
            Addresses = 0,
            Ports = 0,
            Identifiers = 0,
            Providers = 0,
            PublicProviders = 0,
            Schemas = 0,
            StartSequences = 0,
            KernelObjects = 0,
        };
        RedactedSessionPolicyV1 valid = RedactedSessionPackage.PolicyFor(Committed, counts,
            new() { StartTicks = 45, EndTicks = 48, LifecycleRowsOutside = 4 });
        Assert.Equal(valid.Interval, RedactedSessionPolicyV1.Decode(valid.Encode()).Interval);
        Assert.Contains(valid.Omitted, entry => entry.StartsWith("Every row whose session time lies outside the interval", StringComparison.Ordinal));

        // A whole package's file names no interval, as it never did.
        Assert.DoesNotContain("interval", Encoding.UTF8.GetString(RedactedSessionPackage.PolicyFor(Committed, counts).Encode()),
            StringComparison.OrdinalIgnoreCase);

        foreach (RedactedSessionInterval impossible in new RedactedSessionInterval[]
        {
            new() { StartTicks = 48, EndTicks = 48, LifecycleRowsOutside = 0 },
            new() { StartTicks = 45, EndTicks = 48, LifecycleRowsOutside = 7 },
            new() { StartTicks = 45, EndTicks = 48, LifecycleRowsOutside = -1 },
        })
        {
            Assert.Throws<InvalidDataException>(() => (valid with { Interval = impossible }).Encode());
        }
    }

    [Fact(DisplayName = "I22: an interval's coverage spans exactly the readings whose session time it holds, its bounds either side of zero")]
    public void AnIntervalSpansTheReadingsItHolds()
    {
        // A nanosecond clock, so a reading's session time is not a whole tick and a tick truncated toward zero shows: the
        // readings from -99 ns to 99 ns are all tick 0.
        var nanoseconds = new SourceClockDescriptor(SourceClock.Id, SourceClock.HostId, SourceClock.Kind, SourceClock.Encoding,
            1_000_000_000, SourceEpoch, SourceClock.Rounding, SourceClock.MaximumAbsoluteSessionNanoseconds);
        foreach (SourceClockDescriptor clock in new[] { SourceClock, nanoseconds })
        {
            foreach ((long start, long end) in new[] { (44L, 48L), (0L, 1L), (-1L, 0L), (-3L, 2L), (-5L, -2L), (1L, 3L) })
            {
                var range = new TimeRange(start, end);
                (long first, long stop) = SessionNativeInterval.Readings(range, clock);
                for (long reading = SourceEpoch - 900; reading <= SourceEpoch + 900; reading++)
                {
                    long at = SourceClockMath.ConvertToSession(clock, new(clock.Id, clock.Encoding, reading)).SessionTime!.Value
                        .Nanoseconds;
                    Assert.True(range.Contains(at / 100) == (reading >= first && reading < stop),
                        $"Reading {reading} at {at} ns in [{start}, {end}): the interval {(range.Contains(at / 100) ? "holds" : "leaves")} it.");
                }
            }
        }
    }

    private static IReadOnlyList<CoverageState> States(CoverageLedgerV1 ledger, TimeRange nativeInterval) =>
        [.. SessionCoverage.ByMechanism(ledger, nativeInterval).Select(coverage => coverage.State)];

    /// <summary>
    /// What an index concludes of the instances <paramref name="kept"/> admits, without the identities a package replaces
    /// and without the link to a parent, which an interval package holds only when the parent has records in the interval.
    /// </summary>
    private static string[] Shape(ProcessInstanceIndex index, Func<ProcessInstance, bool> kept) =>
    [
        .. index.Instances.Where(kept).Select(instance => string.Join('|',
            instance.Witness, instance.Gaps, instance.LifecycleEpoch, instance.StartKey is not null,
            instance.CreatedNativeTicks is not null, instance.ExitedNativeTicks is not null, instance.ExitCode,
            instance.ParentProcessId is not null, instance.ParentStartSequence is not null, instance.SessionId,
            instance.ImagePath is not null, instance.ImageName is not null))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>How each row of an interval binds to its owner, keyed by its time and kind.</summary>
    private static string[] Owners(IReadOnlyList<SegmentReaderV1> segments, ProcessInstanceIndex processes, TimeRange interval) =>
    [
        .. segments.SelectMany(segment =>
        {
            ProcessBinding[] owners = processes.OwnersOf(segment);
            return Enumerable.Range(0, segment.RowCount)
                .Where(index => segment.Row(index).SessionRelativeTicks is { } at && interval.Contains(at / 100))
                .Select(index => $"{segment.Row(index).SessionRelativeTicks}|{segment.Row(index).Kind}"
                    + $"|{owners[index].Strength}/{owners[index].Reason}");
        }).Order(StringComparer.Ordinal),
    ];
}
