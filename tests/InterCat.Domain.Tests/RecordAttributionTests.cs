using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

/// <summary>
/// Which process a record belongs to when its payload names none (ADR-030). Only a mechanism measured to be raised in
/// the process its records describe binds by the event header, and never an inventory; every other header stays context
/// (§4.1).
/// </summary>
public sealed class RecordAttributionTests
{
    [Fact(DisplayName = "ADR-030: only RPC, HTTP, COM calls, synchronization handles, window messages and sections, each measured on its fixture, bind a record by the process that raised it")]
    public void OnlyMeasuredMechanismsBindByTheirHeader()
    {
        // A mechanism added here needs its own measurement - HTTP's is FX-HTTP-001's (ADR-037), COM calls' FX-COM-001's
        // (ADR-077), synchronization handles' FX-SYNC-001's and FX-SYNC-002's (ADR-082), window messages' FX-WMSG-001's
        // (ADR-086), sections' FX-SECT-001's (ADR-089) - a new binding rule identity, and the texts that name these
        // mechanisms (the grouped-answer caveat and the checkpoint refusal).
        Assert.Equal([Mechanism.Rpc, Mechanism.SharedSection, Mechanism.Synchronization, Mechanism.WindowMessage, Mechanism.Http, Mechanism.ComCall],
            Enum.GetValues<Mechanism>().Where(RecordAttribution.RaisedByTheirProcess));

        // A COM activation is not a call and carries no field: none binds by its header.
        Assert.False(RecordAttribution.RaisedByTheirProcess(Mechanism.ComActivation));
    }

    [Fact(DisplayName = "ADR-030: a payload owner always wins, and a header binds only where its mechanism's records are raised, never an inventory's")]
    public void APayloadOwnerAlwaysWins()
    {
        Assert.Equal(77, RecordAttribution.OwnerOf(77, Mechanism.Rpc, ObservationKind.RequestStart, 1_960));
        Assert.Equal(1_960, RecordAttribution.OwnerOf(null, Mechanism.Rpc, ObservationKind.RequestStart, 1_960));
        Assert.Equal(4_242, RecordAttribution.OwnerOf(null, Mechanism.Http, ObservationKind.Send, 4_242));
        Assert.Equal(5_151, RecordAttribution.OwnerOf(null, Mechanism.ComCall, ObservationKind.RequestStart, 5_151));
        Assert.Equal(77, RecordAttribution.OwnerOf(77, Mechanism.ComCall, ObservationKind.RequestStart, 5_151));
        Assert.Null(RecordAttribution.OwnerOf(null, Mechanism.ApplicationSdk, ObservationKind.Send, 4_242));
        Assert.Equal(77, RecordAttribution.OwnerOf(77, Mechanism.Tcp, ObservationKind.Send, 4));
        Assert.Null(RecordAttribution.OwnerOf(null, Mechanism.Tcp, ObservationKind.Send, 4));

        // A handle's creation and close bind to the process that raised them; a duplication and the stop's rundown name
        // their owner; an object type's inventory, raised wherever the rundown ran, belongs to no process.
        Assert.Equal(6_100, RecordAttribution.OwnerOf(null, Mechanism.Synchronization, ObservationKind.Open, 6_100));
        Assert.Equal(6_100, RecordAttribution.OwnerOf(null, Mechanism.Synchronization, ObservationKind.Close, 6_100));
        Assert.Equal(6_200, RecordAttribution.OwnerOf(6_200, Mechanism.Synchronization, ObservationKind.Open, 6_100));
        Assert.Equal(6_200, RecordAttribution.OwnerOf(6_200, Mechanism.Synchronization, ObservationKind.Inventory, 6_100));
        Assert.Null(RecordAttribution.OwnerOf(null, Mechanism.Synchronization, ObservationKind.Inventory, 6_100));
        Assert.False(RecordAttribution.HeaderBinds(Mechanism.Rpc, ObservationKind.Inventory));

        // Each end of a window message binds to the process that raised it: a send's start to the sender, never the
        // receiver it names, and the receiver's retrieval to the receiver.
        Assert.Equal(7_100, RecordAttribution.OwnerOf(null, Mechanism.WindowMessage, ObservationKind.RequestStart, 7_100));
        Assert.Equal(7_200, RecordAttribution.OwnerOf(null, Mechanism.WindowMessage, ObservationKind.Receive, 7_200));
        Assert.Null(RecordAttribution.OwnerOf(null, Mechanism.Clipboard, ObservationKind.Send, 7_100));

        // A section's handle binds where it was made and closed; a view names its process, a rundown's though its header
        // names none; an inventory a section's record carries no owner of belongs to no process.
        Assert.Equal(8_100, RecordAttribution.OwnerOf(null, Mechanism.SharedSection, ObservationKind.Open, 8_100));
        Assert.Equal(8_100, RecordAttribution.OwnerOf(null, Mechanism.SharedSection, ObservationKind.Close, 8_100));
        Assert.Equal(8_200, RecordAttribution.OwnerOf(8_200, Mechanism.SharedSection, ObservationKind.Inventory, 0));
        Assert.Null(RecordAttribution.OwnerOf(null, Mechanism.SharedSection, ObservationKind.Inventory, 900));
    }
}
