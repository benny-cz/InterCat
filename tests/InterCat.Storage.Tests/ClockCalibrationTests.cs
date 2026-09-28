using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// A live capture's clock calibration (`contracts/clock-calibration-v1.md`): its source clock against the wall clock, and
/// the boot it ran in, kept with the capture like its coverage ledger.
/// </summary>
public sealed class ClockCalibrationTests
{
    private static readonly Guid Capture = Guid.Parse("7c111111-2222-4333-8444-555555555555");
    private static readonly Guid Clock = Guid.Parse("7c222222-3333-4444-8555-666666666666");
    private static readonly Guid Boot = Guid.Parse("7c333333-4444-4555-8666-777777777777");

    [Fact(DisplayName = "R3: a clock calibration round-trips its samples and boot, and refuses what it cannot hold")]
    public void RoundTripAndRefusals()
    {
        ClockCalibrationV1 original = Example();
        ClockCalibrationV1 decoded = ClockCalibrationV1.Decode(original.Encode());
        Assert.Equal((original.CaptureId, original.ClockId, original.BootToken, original.BootCount, original.WallClock),
            (decoded.CaptureId, decoded.ClockId, decoded.BootToken, decoded.BootCount, decoded.WallClock));
        Assert.Equal(original.Samples, decoded.Samples);

        // A boot no token was kept for is unknown, and is written as nothing rather than a guessed value.
        ClockCalibrationV1 unknownBoot = ClockCalibrationV1.Decode((original with { BootToken = null, BootCount = null }).Encode());
        Assert.Equal(((Guid?)null, (long?)null), (unknownBoot.BootToken, unknownBoot.BootCount));
        Assert.DoesNotContain("bootToken", Encoding.UTF8.GetString((original with { BootToken = null }).Encode()), StringComparison.Ordinal);

        string json = Encoding.UTF8.GetString(original.Encode());
        foreach (string refused in new[]
        {
            json.Replace("\"contract\":", "\"surprise\":1,\"contract\":", StringComparison.Ordinal),
            json.Replace("clock-calibration-v1", "clock-calibration-v2", StringComparison.Ordinal),
            json.Replace(Capture.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal),
            json.Replace(Boot.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal),
            "{\"contract\":",
        })
        {
            Assert.Throws<InvalidDataException>(() => ClockCalibrationV1.Decode(Encoding.UTF8.GetBytes(refused)));
        }

        Assert.Throws<InvalidDataException>(() => ClockCalibrationV1.Decode([]));
        Assert.Throws<InvalidDataException>(() => (original with { Samples = [] }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { Samples = [.. original.Samples.Reverse()] }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with
        {
            Samples = [original.Samples[0] with { AcquisitionUncertaintyNanoseconds = -1 }],
        }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { Samples = [original.Samples[0] with { Utc = default }] }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { BootCount = -1 }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { WallClock = " " }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { Samples = [.. Enumerable.Repeat(original.Samples[0], 65)] }).Encode());
    }

    [Fact(DisplayName = "R3: a published calibration is read back, carried by re-derivation, and never released")]
    public void APublishedCalibrationIsCarriedAndKept()
    {
        using var directory = new TemporaryStoreDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "calibration-test");
        string name = DerivedGenerationBuilder.ClockCalibrationFileName(1);
        CommittedBoundary boundary = CommitWith(store, [(name, Example().Encode())]);

        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        Assert.Equal(Example().Samples, ClockCalibrationV1.Read(reopened.Root, reopened.Current!)!.Samples);
        ArgumentException refusal = Assert.Throws<ArgumentException>(() =>
            reopened.ReleaseDependencies([name], "try to release the calibration", DateTimeOffset.UtcNow));
        Assert.Contains("retention cannot release it", refusal.Message, StringComparison.Ordinal);

        _ = reopened.CommitReplacingDerived([], 1, boundary, DateTimeOffset.UtcNow);
        SessionStore replaced = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        Assert.Equal(2, replaced.Current!.Generation);
        Assert.Equal(Boot, ClockCalibrationV1.Read(replaced.Root, replaced.Current)!.BootToken);
    }

    [Fact(DisplayName = "R3: a generation without a calibration states none, and two are refused")]
    public void AbsentAndDuplicateCalibrations()
    {
        using var none = new TemporaryStoreDirectory();
        SessionStore plain = SessionStore.Open(LocalOwnedDirectory.Open(none.Path), Guid.NewGuid(), "calibration-test");
        _ = CommitWith(plain, []);
        Assert.Null(ClockCalibrationV1.Read(plain.Root, plain.Current!));

        using var two = new TemporaryStoreDirectory();
        SessionStore doubled = SessionStore.Open(LocalOwnedDirectory.Open(two.Path), Guid.NewGuid(), "calibration-test");
        _ = CommitWith(doubled,
        [
            (DerivedGenerationBuilder.ClockCalibrationFileName(1), Example().Encode()),
            ("clock-calibration-extra.json", Example().Encode()),
        ]);
        Assert.Contains("one capture has one", Assert.Throws<InvalidDataException>(() =>
            ClockCalibrationV1.Read(doubled.Root, doubled.Current!)).Message, StringComparison.Ordinal);
    }

    private static ClockCalibrationV1 Example() => new()
    {
        Contract = ClockCalibrationV1.ContractName,
        CaptureId = Capture,
        ClockId = Clock,
        BootToken = Boot,
        BootCount = 7,
        WallClock = "GetSystemTimePreciseAsFileTime",
        Samples =
        [
            new() { NativeTicks = 1_000_000, Utc = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero).AddTicks(1), AcquisitionUncertaintyNanoseconds = 200 },
            new() { NativeTicks = 31_000_000, Utc = new DateTimeOffset(2026, 9, 28, 12, 0, 3, TimeSpan.Zero).AddTicks(7), AcquisitionUncertaintyNanoseconds = 300 },
        ],
    };

    private static CommittedBoundary CommitWith(SessionStore store, (string Name, byte[] Bytes)[] calibrations)
    {
        var staged = new List<StoreStagingFile>();
        try
        {
            foreach ((string calibrationName, byte[] bytes) in calibrations)
            {
                StoreStagingFile calibration = store.Stage(calibrationName, StoreDependencyKind.ClockCalibration);
                staged.Add(calibration);
                calibration.Content.Write(bytes);
                _ = calibration.Complete();
            }

            byte[] journalBytes = Encoding.UTF8.GetBytes("journal");
            const string journalName = "journal-0000000001.icatj";
            StoreStagingFile journal = store.Stage(journalName, StoreDependencyKind.Journal);
            staged.Add(journal);
            journal.Content.Write(journalBytes);
            _ = journal.Complete();
            StoreStagingFile plan = store.Stage("normalizer-plan-0000000001.json", StoreDependencyKind.DerivationPlan);
            staged.Add(plan);
            plan.Content.Write(Encoding.UTF8.GetBytes("retained plan"));
            _ = plan.Complete();
            var boundary = new CommittedBoundary(
                journalName, journalBytes.Length, 1,
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(journalBytes)));
            _ = store.Commit([.. staged], boundary, DateTimeOffset.UtcNow);
            return boundary;
        }
        finally
        {
            foreach (StoreStagingFile file in staged)
            {
                file.Dispose();
            }
        }
    }

    private sealed class TemporaryStoreDirectory : IDisposable
    {
        public TemporaryStoreDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "InterCat.Storage.Tests.Calibration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
