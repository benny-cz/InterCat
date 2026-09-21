using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

public sealed class IdentityContractTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    [Fact(DisplayName = "I12: PID and resource reuse produce distinct lifecycle epochs")]
    public void ReusedValuesProduceDistinctEpochs()
    {
        IdentityScenario scenario = LoadScenario();
        IdentityExpected expected = LoadExpected();
        (ProcessInstanceEpoch first, ProcessInstanceEpoch second) = BuildProcessEpochs(scenario);
        ResourceInstanceId firstResource = ResourceInstanceId.FromKey(ResourceInstanceKey.FromSourceInstance(
            scenario.HostA,
            scenario.Boot,
            Mechanism.NamedPipe,
            scenario.ResourceSourceKey,
            1));
        ResourceInstanceId secondResource = ResourceInstanceId.FromKey(ResourceInstanceKey.FromSourceInstance(
            scenario.HostA,
            scenario.Boot,
            Mechanism.NamedPipe,
            scenario.ResourceSourceKey,
            2));

        Assert.Equal(expected.SamePidDifferentStartKeys == "distinct", first.Id != second.Id);
        Assert.Equal(expected.SameResourceValueDifferentEpochs == "distinct", firstResource != secondResource);
    }

    [Fact(DisplayName = "P6: reusable values never resolve an instance without lifecycle evidence")]
    public void ReusedPidAloneDoesNotResolve()
    {
        IdentityScenario scenario = LoadScenario();
        IdentityExpected expected = LoadExpected();
        (ProcessInstanceEpoch first, ProcessInstanceEpoch second) = BuildProcessEpochs(scenario);

        ProcessEpochResolution resolution = ProcessEpochResolver.Resolve(
            [first, second],
            scenario.HostA,
            scenario.Boot,
            scenario.ProcessId,
            scenario.LateRecordNanoseconds,
            providerStartKey: null);

        Assert.Equal(expected.PidOnlyAfterReuse == "unresolved", !resolution.IsResolved);
        Assert.Equal(ProcessEpochResolutionKind.AmbiguousReuse, resolution.Kind);
    }

    [Fact(DisplayName = "I12: a late record with an old start key resolves to the earlier epoch")]
    public void LateRecordResolvesThroughStartKey()
    {
        IdentityScenario scenario = LoadScenario();
        IdentityExpected expected = LoadExpected();
        (ProcessInstanceEpoch first, ProcessInstanceEpoch second) = BuildProcessEpochs(scenario);

        ProcessEpochResolution resolution = ProcessEpochResolver.Resolve(
            [first, second],
            scenario.HostA,
            scenario.Boot,
            scenario.ProcessId,
            scenario.LateRecordNanoseconds,
            first.Key.ProviderStartKey);

        Assert.True(resolution.IsResolved);
        Assert.Equal(expected.LateOldStartKey == "first", first.Id == resolution.ProcessInstanceId);
        Assert.Equal(ProcessEpochResolutionKind.ProviderStartKey, resolution.Kind);
    }

    [Fact(DisplayName = "I12: equal process keys on different hosts never collide")]
    public void EqualProviderKeysOnDifferentHostsAreDistinct()
    {
        IdentityScenario scenario = LoadScenario();
        IdentityExpected expected = LoadExpected();
        var startKey = new ProcessStartKey(scenario.FirstSequence, scenario.FirstCreateFileTime);
        ProcessInstanceId first = ProcessInstanceId.FromKey(ProcessInstanceKey.FromProviderStart(
            scenario.HostA,
            scenario.Boot,
            scenario.ProcessId,
            1,
            startKey));
        ProcessInstanceId otherHost = ProcessInstanceId.FromKey(ProcessInstanceKey.FromProviderStart(
            scenario.HostB,
            scenario.Boot,
            scenario.ProcessId,
            1,
            startKey));

        Assert.Equal(expected.SameProcessKeyDifferentHost == "distinct", first != otherHost);
    }

    [Fact(DisplayName = "Identity: fact keys and normalized observation IDs are deterministic")]
    public void NormalizedObservationIdsAreDeterministic()
    {
        IdentityScenario scenario = LoadScenario();
        var raw = new RawRecordId(new CaptureId(scenario.Capture), 7, 3, 99);
        ObservationId first = ObservationId.Create(raw, NormalizerContractVersion.V1, "network-transfer", 2);
        ObservationId repeat = ObservationId.Create(raw, NormalizerContractVersion.V1, "network-transfer", 2);
        ObservationId otherFact = ObservationId.Create(raw, NormalizerContractVersion.V1, "network-transfer", 3);
        ObservationId otherVersion = ObservationId.Create(raw, new NormalizerContractVersion(2), "network-transfer", 2);

        Assert.Equal(first, repeat);
        Assert.NotEqual(first, otherFact);
        Assert.NotEqual(first, otherVersion);
    }

    [Fact(DisplayName = "Identity: pre-contract evidence remains readable and rewrites canonically")]
    public void LegacyObservationIdRemainsReadable()
    {
        const string json = """
            {"rawRecordId":{"captureId":{"value":"44444444-4444-4444-8444-444444444444"},"streamId":1,"sourceEpoch":1,"recordOrdinal":9},"factIndex":0}
            """;
        ObservationId parsed = JsonSerializer.Deserialize<ObservationId>(json, CamelCaseJson);
        string canonical = JsonSerializer.Serialize(parsed, CamelCaseJson);

        Assert.Equal(NormalizerContractVersion.V1, parsed.NormalizerContractVersion);
        Assert.Contains("\"normalizerContractVersion\":1", canonical, StringComparison.Ordinal);
        Assert.Contains("\"factKey\":", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("factIndex", canonical, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Identity: inventory presence is a witness, not an invented process start")]
    public void InventoryIdentityDoesNotInventStart()
    {
        IdentityScenario scenario = LoadScenario();
        var witness = new RawRecordId(new CaptureId(scenario.Capture), 2, 1, 17);
        ProcessInstanceKey key = ProcessInstanceKey.FromInventoryWitness(
            scenario.HostA,
            scenario.Boot,
            scenario.ProcessId,
            1,
            witness);

        Assert.Equal(ProcessIdentityEvidenceKind.ProvisionalInventoryWitness, key.EvidenceKind);
        Assert.Null(key.ProviderStartKey);
        Assert.Null(key.ObservedCreationTime);
        Assert.Equal(witness, key.ProvisionalWitness);
    }

    [Fact(DisplayName = "Identity: stronger lifecycle evidence creates an alias revision without replacing either identity")]
    public void StrongerEvidenceCreatesAliasRevision()
    {
        IdentityScenario scenario = LoadScenario();
        var raw = new RawRecordId(new CaptureId(scenario.Capture), 2, 1, 17);
        ProcessInstanceId provisional = ProcessInstanceId.FromKey(ProcessInstanceKey.FromInventoryWitness(
            scenario.HostA,
            scenario.Boot,
            scenario.ProcessId,
            1,
            raw));
        ProcessInstanceId proven = ProcessInstanceId.FromKey(ProcessInstanceKey.FromProviderStart(
            scenario.HostA,
            scenario.Boot,
            scenario.ProcessId,
            1,
            new ProcessStartKey(scenario.FirstSequence, scenario.FirstCreateFileTime)));
        ObservationId evidence = ObservationId.Create(raw, NormalizerContractVersion.V1, "process-lifecycle");

        var revision = new ProcessAliasRevision(
            provisional,
            proven,
            1,
            evidence,
            ProcessAliasReason.InventoryReconciledWithProviderStart);

        Assert.Equal(provisional, revision.AliasId);
        Assert.Equal(proven, revision.CanonicalId);
        Assert.NotEqual(revision.AliasId, revision.CanonicalId);
        Assert.Equal(evidence, revision.EvidenceId);
    }

    private static (ProcessInstanceEpoch First, ProcessInstanceEpoch Second) BuildProcessEpochs(IdentityScenario scenario)
    {
        ProcessInstanceKey firstKey = ProcessInstanceKey.FromProviderStart(
            scenario.HostA,
            scenario.Boot,
            scenario.ProcessId,
            1,
            new ProcessStartKey(scenario.FirstSequence, scenario.FirstCreateFileTime));
        ProcessInstanceKey secondKey = ProcessInstanceKey.FromProviderStart(
            scenario.HostA,
            scenario.Boot,
            scenario.ProcessId,
            2,
            new ProcessStartKey(scenario.SecondSequence, scenario.SecondCreateFileTime));
        return (
            new(firstKey, new LifecycleInterval(scenario.FirstStartNanoseconds, scenario.FirstEndNanoseconds)),
            new(secondKey, new LifecycleInterval(scenario.SecondStartNanoseconds, null)));
    }

    internal static IdentityScenario LoadScenario()
    {
        string root = RepositoryRoot();
        string json = File.ReadAllText(Path.Combine(root, "fixtures", "FX-IDENTITY-001", "scenario.json"));
        IdentityScenarioDto dto = JsonSerializer.Deserialize<IdentityScenarioDto>(json, CaseInsensitiveJson)
            ?? throw new InvalidDataException("FX-IDENTITY-001 scenario is empty.");
        return new(
            new HostId(dto.HostA),
            new HostId(dto.HostB),
            new BootId(dto.Boot),
            dto.Capture,
            new ClockId(dto.Clock),
            dto.ProcessId,
            dto.FirstSequence,
            dto.FirstCreateFileTime,
            dto.SecondSequence,
            dto.SecondCreateFileTime,
            dto.FirstStartNanoseconds,
            dto.FirstEndNanoseconds,
            dto.SecondStartNanoseconds,
            dto.LateRecordNanoseconds,
            dto.ResourceSourceKey,
            dto.QpcFrequency,
            dto.QpcEpoch,
            dto.QpcSample,
            dto.MaximumSessionNanoseconds);
    }

    internal static IdentityExpected LoadExpected()
    {
        string root = RepositoryRoot();
        string json = File.ReadAllText(Path.Combine(root, "fixtures", "FX-IDENTITY-001", "expected.json"));
        return JsonSerializer.Deserialize<IdentityExpected>(json, CaseInsensitiveJson)
            ?? throw new InvalidDataException("FX-IDENTITY-001 expected result is empty.");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }

    private sealed record IdentityScenarioDto
    {
        public Guid HostA { get; init; }
        public Guid HostB { get; init; }
        public Guid Boot { get; init; }
        public Guid Capture { get; init; }
        public Guid Clock { get; init; }
        public int ProcessId { get; init; }
        public ulong FirstSequence { get; init; }
        public long FirstCreateFileTime { get; init; }
        public ulong SecondSequence { get; init; }
        public long SecondCreateFileTime { get; init; }
        public long FirstStartNanoseconds { get; init; }
        public long FirstEndNanoseconds { get; init; }
        public long SecondStartNanoseconds { get; init; }
        public long LateRecordNanoseconds { get; init; }
        public ulong ResourceSourceKey { get; init; }
        public long QpcFrequency { get; init; }
        public long QpcEpoch { get; init; }
        public long QpcSample { get; init; }
        public long MaximumSessionNanoseconds { get; init; }
    }
}

internal sealed record IdentityScenario(
    HostId HostA,
    HostId HostB,
    BootId Boot,
    Guid Capture,
    ClockId Clock,
    int ProcessId,
    ulong FirstSequence,
    long FirstCreateFileTime,
    ulong SecondSequence,
    long SecondCreateFileTime,
    long FirstStartNanoseconds,
    long FirstEndNanoseconds,
    long SecondStartNanoseconds,
    long LateRecordNanoseconds,
    ulong ResourceSourceKey,
    long QpcFrequency,
    long QpcEpoch,
    long QpcSample,
    long MaximumSessionNanoseconds);

internal sealed record IdentityExpected
{
    public string SamePidDifferentStartKeys { get; init; } = string.Empty;
    public string SameResourceValueDifferentEpochs { get; init; } = string.Empty;
    public string LateOldStartKey { get; init; } = string.Empty;
    public string SameProcessKeyDifferentHost { get; init; } = string.Empty;
    public string PidOnlyAfterReuse { get; init; } = string.Empty;
    public long QpcSampleSessionNanoseconds { get; init; }
}
