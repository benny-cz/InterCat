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

    [Fact(DisplayName = "R3: a record without a size says why it has none, and never reads as a size of zero")]
    public void ARecordWithoutASizeSaysWhy()
    {
        ObservationRowV1 receive = Transfer(2, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 200)
            .Between("127.0.0.1:8080", "127.0.0.1:50000");

        // Its source withheld it, the capture's profile left it out, the capture was denied it, a lost event took it, its
        // schema could not be read, or a package redacted it: each is said, none as 0 B.
        FieldAvailability[] missing =
        [
            FieldAvailability.NotExposed, FieldAvailability.ProfileDisabled, FieldAvailability.Denied, FieldAvailability.EventLost,
            FieldAvailability.SchemaUnknown, FieldAvailability.Redacted,
        ];
        Assert.Equal(
            ["size not exposed", "size not collected by the profile", "size denied", "size lost with its event",
                "size not decoded: schema unknown", "size redacted"],
            missing.Select(availability => EvidenceRowText.Size(receive with { ByteAvailability = availability }, Invariant)));

        // Where a label already names the size, the reason stands alone.
        Assert.Equal("redacted", EvidenceRowText.SizeWithDomain(receive with { ByteAvailability = FieldAvailability.Redacted }, Invariant));
        Assert.Equal("1,024 B carried by the transport", EvidenceRowText.SizeWithDomain(
            receive with { ByteValue = 1_024, ByteAvailability = FieldAvailability.Present, ByteDomain = ByteDomain.TransportObserved }, Invariant));

        // A size said to be present that the record does not hold is not recorded; one that does not apply is not mentioned.
        Assert.Equal("size not recorded", EvidenceRowText.Size(receive with { ByteAvailability = FieldAvailability.Present }, Invariant));
        Assert.Equal("not recorded", EvidenceRowText.SizeWithDomain(receive with { ByteAvailability = FieldAvailability.Present }, Invariant));
        Assert.Null(EvidenceRowText.Size(receive with { ByteAvailability = FieldAvailability.NotApplicable }, Invariant));
        Assert.Null(EvidenceRowText.SizeWithDomain(receive with { ByteAvailability = FieldAvailability.NotApplicable }, Invariant));

        // A record's kind reads as the window and the command line say it; one of no known kind is a record.
        Assert.Equal("TCP request start", EvidenceRowText.Title(receive with { Kind = ObservationKind.RequestStart }));
        Assert.Equal("TCP record", EvidenceRowText.Title(receive with { Kind = ObservationKind.UnknownKind }));
    }

    [Fact]
    public void ARecordsQualityReadsAsWordsNotEnumerationNames()
    {
        ObservationRowV1 send = Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 64, 100) with
        {
            AttributionQuality = QualityLevel.Proven,
            CorrelationQuality = QualityLevel.UnknownQuality,
            MeasurementQuality = QualityLevel.Qualified,
            TimingQuality = QualityLevel.Weak,
        };

        Assert.Equal("attribution proven, correlation unknown, measurement qualified, timing weak", EvidenceRowText.Quality(send));
        Assert.Equal("level 9", EvidenceRowText.QualityName((QualityLevel)9));
    }

    [Fact]
    public void AnIpv6RowNamesItsEndpointsBracketedInCanonicalText()
    {
        ObservationRowV1 send = Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 64, 100)
            .Between("[2001:db8:0:0:0:0:0:1]:50000", "[::1]:443");
        ObservationRowV1 udpReceive = Transfer(2, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200)
            .Between("[fe80::1:2]:5353", "[::ffff:192.0.2.1]:5353") with { Mechanism = Mechanism.Udp };

        Assert.Equal("[2001:db8::1]:50000 → [::1]:443", EvidenceRowText.Endpoints(send));
        Assert.Equal("[::ffff:192.0.2.1]:5353 ← [fe80::1:2]:5353", EvidenceRowText.Endpoints(udpReceive));

        // A record that names one endpoint shows that one; its IPv4 columns are empty and never read for it.
        Assert.Equal("[2001:db8::1]:50000", EvidenceRowText.Endpoints(send with { DestinationEndpointAddressV6 = null }));
        Assert.Null(EvidenceRowText.Endpoints(send with
        {
            SourceEndpointAddressV6 = null,
            DestinationEndpointAddressV6 = null,
        }));
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
        Assert.Equal("PID 100 · owner unresolved: after its PID's last instance exited",
            EvidenceRowText.Owner(Record(row,
                new(null, null, null, RelationStrength.Unresolved, ProcessBindingReason.AfterExit, false)), Invariant));
        Assert.Equal("no owner process named", EvidenceRowText.Owner(Record(
            Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 8, null),
            new(null, null, null, RelationStrength.Unresolved, ProcessBindingReason.NoOwner, false)), Invariant));
    }

    [Fact(DisplayName = "R5: why a record binds to no process reads in one set of words, the window's and icat's, an unknown reason by its number")]
    public void ABindingReasonReadsInOneSetOfWords()
    {
        ProcessBindingReason[] reasons = Enum.GetValues<ProcessBindingReason>();
        string[] words = [.. reasons.Select(BindingText.Reason)];
        Assert.Equal(reasons.Length, words.Distinct(StringComparer.Ordinal).Count());
        Assert.All(words, said => Assert.DoesNotMatch("[a-z][A-Z]", said));
        Assert.Equal("reason 99", BindingText.Reason((ProcessBindingReason)99));

        // A binding the policy does not admit is any weaker than it asks for, not only a reused PID's candidate: under
        // direct evidence only, a PID's first instance's records are not admitted either.
        Assert.Equal("a binding the evidence policy does not admit", BindingText.Reason(ProcessBindingReason.NotAdmittedByPolicy));

        // The window's evidence row names it in those words.
        ObservationRowV1 row = Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 8, 100);
        Assert.Equal("PID 100 · owner unresolved: before its PID's first lifecycle record", EvidenceRowText.Owner(Record(row,
            new(null, null, null, RelationStrength.Unresolved, ProcessBindingReason.BeforeFirstEvidence, false)), Invariant));
    }

    [Fact(DisplayName = "ADR-030: an RPC record is owned by the process that raised it, and a kernel record's header never is")]
    public void AnRpcRecordIsOwnedByTheProcessThatRaisedIt()
    {
        ObservationRowV1 call = RpcCall(1, ObservationKind.RequestStart, Direction.Inbound, raisedBy: 1_960, 1);
        Assert.Equal(1_960, EvidenceRowText.OwnerProcessId(call));
        Assert.Equal("raised by PID 1960", EvidenceRowText.Owner(Record(call), Invariant));
        Assert.Equal("raised by PID 1960 · owner unresolved: after its PID's last instance exited",
            EvidenceRowText.Owner(Record(call,
                new(null, null, null, RelationStrength.Unresolved, ProcessBindingReason.AfterExit, false)), Invariant));
        Assert.Equal("raised by services.exe · PID 1960", EvidenceRowText.Owner(Record(call,
            new(new ProcessInstanceId(Guid.NewGuid()), 1_960, "services.exe", RelationStrength.Correlated,
                ProcessBindingReason.Bound, true)), Invariant));

        // As a clause, the owner reads as what it is: raised by, owned by, or no owner named - never "owned by raised by".
        Assert.Equal("raised by PID 1960", EvidenceRowText.Ownership(Record(call), Invariant));
        Assert.Equal("owned by PID 100", EvidenceRowText.Ownership(
            Record(Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 8, 100)), Invariant));
        Assert.Equal("with no owner process named", EvidenceRowText.Ownership(Record(
            Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 8, null),
            new(null, null, null, RelationStrength.Unresolved, ProcessBindingReason.NoOwner, false)), Invariant));

        // A kernel record is raised in whatever process the kernel was in, so its header names no owner (§4.1).
        ObservationRowV1 kernel = Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 8, null) with { HeaderProcessId = 1_960 };
        Assert.Null(EvidenceRowText.OwnerProcessId(kernel));
        Assert.Equal("no owner process named", EvidenceRowText.Owner(Record(kernel), Invariant));
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
