using System.Text.Json;
using InterCat.Domain;
using InterCat.Sdk;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The cooperative SDK's content in the catalog (ADR-107's addendum): a message's content its own descriptor, compiled only
/// where a capture keeps the SDK's content under a request naming its processes, and asked of the provider by its keyword
/// there alone; its bytes kept beside the metadata projection, sized by the count before them; the source's process scope
/// and what keeping its content costs measured, as a request's admission requires.
/// </summary>
public sealed class SdkContentAdmissionTests
{
    private static readonly CompiledBodyAdmissionPolicy FixturePolicy =
        CaptureBodyAdmissionPolicies.ScopedContentFixture(4_096, 16L * 1024 * 1024, ContentInspectionMode.HexAndText);

    private static WindowsSourceDefinition Sdk => WindowsSourceCatalog.Find(WindowsSourceCatalog.CooperativeSdkSourceId)!;

    [Fact(DisplayName = "R17: the SDK's content is admitted only under a policy keeping it - its own descriptor, its bytes sized by the count before them - and under any other no descriptor of it is compiled")]
    public void ContentIsAdmittedOnlyWhereKept()
    {
        ProviderSchema schema = ManifestParser.Parse(Sdk.EmbeddedManifest!);

        // Under a metadata policy, or one keeping another source's content, the content descriptor is not compiled at all.
        foreach (CompiledBodyAdmissionPolicy other in new[] { CaptureBodyAdmissionPolicies.MetadataOnly, FixturePolicy })
        {
            SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(Sdk, schema, 0, bodyPolicy: other);
            Assert.Empty(plan.Diagnostics);
            Assert.Equal(Enumerable.Range(1, 9), plan.Events.Select(descriptor => (int)descriptor.EventId).Order());
            Assert.DoesNotContain(plan.Events.SelectMany(descriptor => descriptor.Slots), slot => slot.Kind == AdmittedSlotKind.Content);
        }

        // Under a request's policy naming it: the message's channel, epoch and sequence, and its bytes beside the projection.
        SourceAdmissionPlan kept = AdmissionPlanCompiler.Compile(Sdk, schema, 0, bodyPolicy: RequestPolicy());
        Assert.Empty(kept.Diagnostics);
        Assert.Equal(Enumerable.Range(1, 10), kept.Events.Select(descriptor => (int)descriptor.EventId).Order());
        AdmittedEventPlan content = Assert.Single(kept.Events, descriptor => descriptor.EventId == InterCatSdkEventSource.MessageContentId);
        Assert.Equal((Mechanism.ApplicationSdk, ObservationLayer.Application, ObservationKind.Inventory, Direction.Outbound),
            (content.Mechanism, content.Layer, content.Kind, content.Direction));
        Assert.Equal(
            [
                ("processId", FieldRole.ProcessAttribution, AdmittedSlotKind.Numeric, 0, (SourceField?)null),
                ("channel", FieldRole.CorrelationKey, AdmittedSlotKind.Identifier, 4, null),
                ("epoch", FieldRole.CorrelationKey, AdmittedSlotKind.Numeric, 20, SourceField.ChannelEpoch),
                ("sequence", FieldRole.CorrelationKey, AdmittedSlotKind.Numeric, 28, SourceField.MessageSequence),
                ("content", FieldRole.Content, AdmittedSlotKind.Content, 40, null),
            ],
            content.Slots.Select(slot => (slot.FieldName, slot.Role, slot.Kind, slot.Offset, slot.SourceField)));
        AdmittedSlotPlan bytes = content.Slots.Single(slot => slot.Kind == AdmittedSlotKind.Content);
        Assert.Equal((36, 4, ContentEvidenceClassification.ApplicationPayload, ContentFieldEncoding.Binary),
            (bytes.LengthOffset, bytes.LengthWidth, bytes.ContentClassification!.Value, bytes.ContentEncoding!.Value));

        // It is the one descriptor keeping content, and a statement of the message's bytes rather than a transfer of them:
        // nothing of it counts bytes, which its publication says once.
        Assert.Single(kept.Events.SelectMany(descriptor => descriptor.Slots), slot => slot.Kind == AdmittedSlotKind.Content);
        Assert.DoesNotContain(content.Slots, slot => slot.Role == FieldRole.ByteCount);
    }

    [Fact(DisplayName = "P28: the SDK's content is asked for by an explicit request only, of its named processes alone - its provider enabled for them with content's keyword and its content event - and the sdk profile asks for neither")]
    public void ARequestAsksForTheSdksContent()
    {
        ContentCaptureDecision decision = ContentCapturePolicyCompiler.Compile(Request());
        Assert.Equal((true, true, true, true),
            (decision.SourceBodyContractAvailable, decision.ScopeEnforceable, decision.CaptureImpactMeasured, decision.AdmissionPolicyAvailable));
        Assert.Contains("kept only from processes 4242", decision.AvailabilityReason, StringComparison.Ordinal);
        Assert.Contains("every channel of theirs", decision.AvailabilityReason, StringComparison.Ordinal);

        SourceAdmissionPlan sdk = AdmissionPlanCompiler.Compile(Sdk, ManifestParser.Parse(Sdk.EmbeddedManifest!), 1, bodyPolicy: RequestPolicy());
        EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.Content, Content: Request()),
            new SourcePlanCompilation([Lifecycle(), sdk], []));
        Assert.True(plan.CanStart);
        Assert.Equal(("content", CaptureBodyAdmissionPolicies.ScopedContentRequestPolicyId), (plan.EffectiveProfileId, plan.BodyPolicy!.PolicyId));

        // The provider is enabled for the named process alone, with EventSource's keywords and every one of the SDK's, content's
        // included, and the content event beside the rest; lifecycle stays whole-machine metadata.
        ProviderEnablementRequest provider = plan.Providers.Single(request => request.SourceId == WindowsSourceCatalog.CooperativeSdkSourceId);
        Assert.Equal((0x0000_F000_FFFF_FFFFUL, 4_242), (provider.MatchAnyKeyword, Assert.Single(provider.ProcessIdsToInclude)));
        Assert.Equal(Enumerable.Range(1, 10), provider.EventIdsToEnable);
        Assert.Empty(plan.Providers.Single(request => request.SourceId == WindowsSourceCatalog.KernelProcessSourceId).ProcessIdsToInclude);
        Assert.Equal(ProviderProcessScope.ProcessFiltered,
            plan.Scope.Sources.Single(source => source.SourceId == WindowsSourceCatalog.CooperativeSdkSourceId).ProcessScope);

        // What it keeps is said before anything is kept.
        Assert.Contains(Sdk.ContentContract!.ContentStatement!, plan.CollectionStatement, StringComparison.Ordinal);

        // The sdk profile's own plan asks for neither content's keyword nor its event.
        ProviderEnablementRequest metadata = Assert.Single(ProviderEnablementCompiler.Compile(
            [AdmissionPlanCompiler.Compile(Sdk, ManifestParser.Parse(Sdk.EmbeddedManifest!), 0)]));
        Assert.Equal((0x0000_F000_FFFF_FFFEUL, 9), (metadata.MatchAnyKeyword, metadata.EventIdsToEnable.Max()));
    }

    [Fact(DisplayName = "R17: the SDK's content contract holds its process scope before persistence and states what keeping it costs, as measured: a decoy beside a named burst never reached a session scoped to it")]
    public void TheScopeAndCostAreMeasured()
    {
        ValidatedContentSourceContract contract = Sdk.ContentContract!;
        Assert.Equal([InterCatSdkEventSource.MessageContentId], contract.EventIds);
        Assert.Equal(["content"], contract.SourceFields);
        Assert.Equal([ContentEvidenceClassification.ApplicationPayload], contract.Classifications);
        Assert.Equal((true, false, true), (contract.EnforcesProcessScopeBeforePersistence, contract.EnforcesChannelScopeBeforePersistence,
            Sdk.SupportsCaptureSideProcessFilter));
        Assert.True(CaptureBodyAdmissionPolicies.AdmitsContentRequests(WindowsSourceCatalog.CooperativeSdkSourceId));

        // The class and the evidence are the bench result's, which the repository keeps, and the source's own.
        Assert.StartsWith("bench/results/sdk-content-impact-", contract.CaptureImpactEvidence, StringComparison.Ordinal);
        Assert.Equal((contract.Overhead, contract.CaptureImpactEvidence), (Sdk.Overhead, Sdk.OverheadEvidence));
        Assert.EndsWith("/impact.json", contract.CaptureImpactEvidence, StringComparison.Ordinal);
        using JsonDocument impact = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), contract.CaptureImpactEvidence)));
        JsonElement root = impact.RootElement;
        Assert.Equal(contract.Overhead.ToString(), root.GetProperty("overheadClass").GetString());
        Assert.Equal(OverheadClassCalculator.Classify(Math.Max(0, root.GetProperty("medianCaptureCpuPercentagePoints").GetDouble())), contract.Overhead);

        // Every message's content reached the scoped session and nothing was lost; the decoy beside it reached it with no
        // record, and was never asked to say anything.
        long said = root.GetProperty("contentsSaid").GetInt64();
        Assert.Equal((said, 0L), (root.GetProperty("contentsCaptured").GetInt64(), root.GetProperty("eventsLost").GetInt64()));
        Assert.Equal(root.GetProperty("messages").GetInt64() * root.GetProperty("pairs").GetArrayLength(), said);
        JsonElement scope = root.GetProperty("scope");
        Assert.Equal((0L, false, 0L), (scope.GetProperty("decoyRecords").GetInt64(), scope.GetProperty("decoyListened").GetBoolean(),
            scope.GetProperty("decoyContentsSaid").GetInt64()));
        Assert.Equal(root.GetProperty("messages").GetInt64(), scope.GetProperty("namedContents").GetInt64());
    }

    private static ContentCaptureRequest Request() => new()
    {
        SourceId = WindowsSourceCatalog.CooperativeSdkSourceId,
        Mechanism = Mechanism.ApplicationSdk,
        ProcessIds = [4_242],
        ChannelSelectors = ["*"],
        MaximumRecordBytes = 4_096,
        MaximumSessionBytes = 16L * 1024 * 1024,
        Retention = ContentRetentionMode.StopAtLimit,
        Inspection = ContentInspectionMode.HexAndText,
    };

    private static CompiledBodyAdmissionPolicy RequestPolicy() =>
        CaptureBodyAdmissionPolicies.ScopedContentRequest(ContentCapturePolicyCompiler.Compile(Request()));

    private static string RepositoryRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "InterCat.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    /// <summary>A lifecycle plan of one admitted descriptor, which is all the profile compiler needs of it here.</summary>
    private static SourceAdmissionPlan Lifecycle()
    {
        Guid provider = Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
        return new()
        {
            SourceId = WindowsSourceCatalog.KernelProcessSourceId,
            ProviderGuid = provider,
            SourceIndex = 0,
            Events =
            [
                new AdmittedEventPlan
                {
                    SourceIndex = 0,
                    ProviderGuid = provider,
                    EventId = 1,
                    Version = 0,
                    Name = "fixture",
                    Mechanism = Mechanism.ProcessLifecycle,
                    Layer = ObservationLayer.Lifecycle,
                    Kind = ObservationKind.Create,
                    Direction = Direction.DirectionNotApplicable,
                    MinimumBodyLength = 0,
                    PointerSize = 8,
                    SchemaFingerprint = "sha256:fixture-lifecycle",
                    BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                    Slots = [],
                    FieldReport = [],
                },
            ],
            Diagnostics = [],
        };
    }
}
