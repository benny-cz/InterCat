using System.Runtime.Versioning;
using InterCat.Capture.Journal;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

[SupportedOSPlatform("windows")]
public sealed class BrokerEvidenceFollowTests
{
    [Fact(DisplayName = "R16: an ordinary-integrity follower derives a session from protected broker evidence without writing to it")]
    public async Task OrdinaryIntegrityFollowerDerivesFromProtectedEvidence()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        PreparedCapturePlan plan = BrokerPrepareCompiler.Prepare(
            CompileFocused(),
            new(30, 1_048_576, 16_777_216),
            BrokerRetentionPolicy.StopAtLimit,
            Runtime,
            BrokerJournalPublication.Live).PreparedPlan!;
        CaptureId captureId = CaptureId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var ownership = new BrokerCaptureOwnership
        {
            CaptureId = captureId,
            Owner = OwnerA.Owner,
            Session = BrokerSessionOwnership.Create(captureId),
            PlanDigest = plan.Digest,
            State = CaptureLifecycle.Starting,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LeaseExpiresAtUtc = now.AddMinutes(1),
            StopMilestones = BrokerStopMilestones.None,
        };
        await using (var runtime = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host))
        {
            Assert.True((await runtime.StartAsync(ownership, plan, CancellationToken.None)).Started);
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            Assert.True((await runtime.StopAsync(ownership, CancellationToken.None)).Milestones.FullyFinalized);
        }

        string evidencePath = Path.Combine(temporary.Root.Path, $"capture-{captureId.Value:N}");
        string[] evidenceBefore = Snapshot(evidencePath);
        string sessionPath = Path.Combine(TemporaryBrokerRoot.CreateTemporaryParent(), "session");
        int lowered = TemporaryBrokerRoot.CurrentIntegrityLevel == BrokerIntegrityLevel.High
            ? BrokerIntegrityLevel.Medium
            : BrokerIntegrityLevel.Low;
        using var token = LoweredIntegrityToken.Create(lowered);
        FollowStep? step = null;
        Exception? failure = null;

        token.Run(() => failure = Record.Exception(() =>
        {
            SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
            Directory.CreateDirectory(sessionPath);
            SessionManifestV1 source = evidence.Current!;
            SessionStore derived = SessionStore.Open(
                LocalOwnedDirectory.Open(sessionPath), source.SessionId, source.SourceIdentity);
            step = LiveSessionFollower.Open(evidence, derived).CatchUp();
        }));

        Assert.Null(failure);
        Assert.NotNull(step);
        Assert.True(step.EvidenceChunks > 0);
        Assert.Equal(step.EvidenceChunks, step.DerivedChunks);
        Assert.Equal(evidenceBefore, Snapshot(evidencePath));
    }

    /// <summary>Names, lengths and write times: anything a follower wrote into the evidence would change it.</summary>
    private static string[] Snapshot(string directory) =>
    [
        .. new DirectoryInfo(directory).EnumerateFiles()
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}"),
    ];
}
