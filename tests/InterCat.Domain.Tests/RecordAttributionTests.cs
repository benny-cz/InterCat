using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

/// <summary>
/// Which process a record belongs to when its payload names none (ADR-030). Only a mechanism measured to be raised in
/// the process its records describe binds by the event header; every other header stays context (§4.1).
/// </summary>
public sealed class RecordAttributionTests
{
    [Fact(DisplayName = "ADR-030: only RPC, as measured on FX-RPC-001, binds a record by the process that raised it")]
    public void OnlyMeasuredMechanismsBindByTheirHeader()
    {
        // A mechanism added here needs its own measurement, and the texts that name RPC as the one such mechanism
        // (the grouped-answer caveat and the checkpoint refusal) name it too.
        Assert.Equal([Mechanism.Rpc], Enum.GetValues<Mechanism>().Where(RecordAttribution.RaisedByTheirProcess));
    }

    [Fact(DisplayName = "ADR-030: a payload owner always wins, and a header binds only where its mechanism's records are raised")]
    public void APayloadOwnerAlwaysWins()
    {
        Assert.Equal(77, RecordAttribution.OwnerOf(77, Mechanism.Rpc, 1_960));
        Assert.Equal(1_960, RecordAttribution.OwnerOf(null, Mechanism.Rpc, 1_960));
        Assert.Equal(77, RecordAttribution.OwnerOf(77, Mechanism.Tcp, 4));
        Assert.Null(RecordAttribution.OwnerOf(null, Mechanism.Tcp, 4));
    }
}
