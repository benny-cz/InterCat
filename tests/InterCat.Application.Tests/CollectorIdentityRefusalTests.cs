using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>§19.5: a generation keeps the collectors of its own capture only (`contracts/collector-identities-v1.md`).</summary>
public sealed class CollectorIdentityRefusalTests
{
    [Fact(DisplayName = "§19.5: a generation refuses the collectors of another capture, and publishes nothing")]
    public void AGenerationRefusesAnotherCapturesCollectors()
    {
        using var session = new TemporarySession();
        var collectors = new CollectorIdentitiesV1
        {
            Contract = CollectorIdentitiesV1.ContractName,
            CaptureId = Guid.NewGuid(),
            Processes = [new() { Role = CollectorRole.Recorder, ProcessId = 100 }],
        };
        Assert.Throws<ArgumentException>(() => Publish(session.Store,
            [Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 }], collectors: collectors));
        Assert.Null(session.Store.Current);
    }
}
