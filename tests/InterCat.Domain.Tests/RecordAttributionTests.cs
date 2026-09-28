using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

/// <summary>
/// Which process a record belongs to when its payload names none (ADR-030). Only a mechanism measured to be raised in
/// the process its records describe binds by the event header; every other header stays context (§4.1).
/// </summary>
public sealed class RecordAttributionTests
{
    [Fact(DisplayName = "ADR-030: only RPC and HTTP, each measured on its fixture, bind a record by the process that raised it")]
    public void OnlyMeasuredMechanismsBindByTheirHeader()
    {
        // A mechanism added here needs its own measurement - HTTP's is FX-HTTP-001's (ADR-037) - a new binding rule
        // identity, and the texts that name these mechanisms (the grouped-answer caveat and the checkpoint refusal).
        Assert.Equal([Mechanism.Rpc, Mechanism.Http], Enum.GetValues<Mechanism>().Where(RecordAttribution.RaisedByTheirProcess));
    }

    [Fact(DisplayName = "ADR-030: a payload owner always wins, and a header binds only where its mechanism's records are raised")]
    public void APayloadOwnerAlwaysWins()
    {
        Assert.Equal(77, RecordAttribution.OwnerOf(77, Mechanism.Rpc, 1_960));
        Assert.Equal(1_960, RecordAttribution.OwnerOf(null, Mechanism.Rpc, 1_960));
        Assert.Equal(4_242, RecordAttribution.OwnerOf(null, Mechanism.Http, 4_242));
        Assert.Null(RecordAttribution.OwnerOf(null, Mechanism.ApplicationSdk, 4_242));
        Assert.Equal(77, RecordAttribution.OwnerOf(77, Mechanism.Tcp, 4));
        Assert.Null(RecordAttribution.OwnerOf(null, Mechanism.Tcp, 4));
    }
}
