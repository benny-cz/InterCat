using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class EvidenceRowTextTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact]
    public void ARowSaysWhatHappenedWhenAndBetweenWhichEndpointsInTheDirectionItsKindStates()
    {
        ObservationRowV1 send = Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 1_024, 100)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_234_567_890 };
        ObservationRowV1 receive = Transfer(2, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 200)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = -500_000_000 };
        ObservationRowV1 udpReceive = Transfer(3, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200)
            .Between("127.0.0.1:50001", "127.0.0.1:9090") with { Mechanism = Mechanism.Udp };
        ObservationRowV1 connect = Transfer(4, ObservationKind.Connect, AccountingSide.EndpointActivity, null, 100)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { ByteAvailability = FieldAvailability.NotApplicable };

        Assert.Equal("TCP send", EvidenceRowText.Title(send));
        Assert.Equal("+1.234568 s", EvidenceRowText.When(send, Invariant));
        Assert.Equal("−0.500000 s", EvidenceRowText.When(receive, Invariant));
        Assert.Equal("time unavailable", EvidenceRowText.When(connect, Invariant));
        Assert.Equal("127.0.0.1:50000 → 127.0.0.1:8080", EvidenceRowText.Endpoints(send));
        Assert.Equal("127.0.0.1:8080 ← 127.0.0.1:50000", EvidenceRowText.Endpoints(receive));
        // A UDP receive names the datagram's sender first (ADR-019), so its own end is the second endpoint.
        Assert.Equal("127.0.0.1:9090 ← 127.0.0.1:50001", EvidenceRowText.Endpoints(udpReceive));
        Assert.Equal("127.0.0.1:50000 ↔ 127.0.0.1:8080", EvidenceRowText.Endpoints(connect));
        Assert.Equal("1,024 B", EvidenceRowText.Size(send, Invariant));
        Assert.Equal("size not exposed", EvidenceRowText.Size(receive, Invariant));
        Assert.Null(EvidenceRowText.Size(connect, Invariant));
        Assert.Equal("+1.234568 s · TCP send · 1,024 B · 127.0.0.1:50000 → 127.0.0.1:8080",
            EvidenceRowText.Summary(Record(send), Invariant));
    }

    [Fact]
    public void LifecycleRowsReadAsProcessEvents()
    {
        Assert.Equal("Process start", EvidenceRowText.Title(Lifecycle(1, ObservationKind.Create, 100, 1)));
        Assert.Equal("Process exit", EvidenceRowText.Title(Lifecycle(2, ObservationKind.Exit, 100, 2)));
        Assert.Equal("Process running at capture start",
            EvidenceRowText.Title(Lifecycle(3, ObservationKind.Inventory, 100, 3)));
        Assert.Null(EvidenceRowText.Endpoints(Lifecycle(3, ObservationKind.Inventory, 100, 3)));
    }

    [Fact]
    public void AnOwnerIsNamedOnlyAsFarAsItsBindingGoes()
    {
        ObservationRowV1 row = Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 8, 100);
        var instance = new ProcessInstanceId(Guid.NewGuid());

        Assert.Equal("PID 100", EvidenceRowText.Owner(Record(row), Invariant));
        Assert.Equal("PID 100 · executable not witnessed", EvidenceRowText.Owner(Record(row,
            new(instance, 100, null, RelationStrength.Direct, ProcessBindingReason.Bound, true)), Invariant));
        Assert.Equal("client.exe · PID 100", EvidenceRowText.Owner(Record(row,
            new(instance, 100, "client.exe", RelationStrength.Direct, ProcessBindingReason.Bound, true)), Invariant));
        Assert.Equal("client.exe · PID 100 · candidate · not admitted by the evidence policy",
            EvidenceRowText.Owner(Record(row,
                new(instance, 100, "client.exe", RelationStrength.Candidate, ProcessBindingReason.Bound, false)),
                Invariant));
        Assert.Equal("PID 100 · owner unresolved: after this PID's last instance exited",
            EvidenceRowText.Owner(Record(row,
                new(null, null, null, RelationStrength.Unresolved, ProcessBindingReason.AfterExit, false)), Invariant));
        Assert.Equal("no owner process named", EvidenceRowText.Owner(Record(
            Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 8, null),
            new(null, null, null, RelationStrength.Unresolved, ProcessBindingReason.NoOwner, false)), Invariant));
    }

    [Fact]
    public void TheValidatedProvidersAreNamedAndAnyOtherIsIdentifiedNotGuessed()
    {
        Assert.Equal("Microsoft-Windows-Kernel-Network", EvidenceRowText.ProviderName(NetworkProvider));
        Assert.Equal("Microsoft-Windows-Kernel-Process", EvidenceRowText.ProviderName(ProcessProvider));
        Guid other = Guid.Parse("01234567-89ab-4cde-8f01-23456789abcd");
        Assert.Equal("provider 01234567-89ab-4cde-8f01-23456789abcd", EvidenceRowText.ProviderName(other));
    }

    private static SessionEvidenceRecord Record(ObservationRowV1 row, SessionEvidenceOwner? owner = null) =>
        new(row.ObservationIdIn(Capture, NormalizerContractVersion.V1), "seg-0000000001-0000.icats", 0, row, owner);
}
