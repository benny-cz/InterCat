using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Schema tests run against a committed manifest excerpt rather than the local machine, so the adapter's
/// interpretation is verified without Windows, a registered provider, or elevation (R19).
/// </summary>
public sealed class ManifestSchemaTests
{
    private const string SampleManifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation>
          <events>
           <provider name="Sample-Provider" guid="{7dd42a49-5329-4832-8dfd-43d979153a88}">
            <keywords>
             <keyword name="SAMPLE_KEYWORD_IPV4" mask="0x10" />
             <keyword name="SAMPLE_KEYWORD_IPV6" mask="0x20" />
            </keywords>
            <tasks>
             <task name="SAMPLE_TASK" value="10">
              <opcodes>
               <opcode name="Datasent." value="10" />
              </opcodes>
             </task>
            </tasks>
            <templates>
             <template tid="SentArgs">
              <data name="PID" inType="win:UInt32" />
              <data name="size" inType="win:UInt32" />
              <data name="daddr" inType="win:UInt32" />
              <data name="saddr" inType="win:UInt32" />
              <data name="dport" inType="win:UInt16" />
              <data name="sport" inType="win:UInt16" />
              <data name="connid" inType="win:UInt32" />
             </template>
             <template tid="NamedArgs">
              <data name="ImageName" inType="win:UnicodeString" />
              <data name="ProcessID" inType="win:UInt32" />
             </template>
            </templates>
            <events>
             <event value="10" version="0" task="SAMPLE_TASK" opcode="Datasent." level="win:Informational"
                    keywords="SAMPLE_KEYWORD_IPV4" template="SentArgs" />
             <event value="20" version="1" task="SAMPLE_TASK" level="win:Informational" template="NamedArgs" />
            </events>
           </provider>
          </events>
         </instrumentation>
        </instrumentationManifest>
        """;

    [Fact(DisplayName = "R5: the manifest parser resolves descriptors, keyword masks and template fields")]
    public void ParsesDescriptorsAndFields()
    {
        ProviderSchema schema = ManifestParser.Parse(SampleManifest);

        Assert.Equal("Sample-Provider", schema.ProviderName);
        Assert.Equal(Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88"), schema.ProviderGuid);
        Assert.Equal(2, schema.Events.Count);

        ProviderSchemaEvent sent = schema.FindEvent(10)!;
        Assert.Equal(0x10UL, sent.KeywordMask);
        Assert.Equal(10, sent.TaskValue);
        Assert.Equal(7, sent.Fields.Count);
        Assert.Equal(4, sent.Fields[0].FixedWidth);
        Assert.Equal(FieldWidthKind.Fixed, sent.Fields[4].WidthKind);
        Assert.Equal(2, sent.Fields[4].FixedWidth);
    }

    [Fact(DisplayName = "R20: an unchanged layout keeps one schema fingerprint across formatting differences")]
    public void FingerprintIgnoresWhitespaceOnly()
    {
        string reformatted = SampleManifest.Replace("\n", "\n   ", StringComparison.Ordinal);

        Assert.Equal(ManifestParser.Fingerprint(SampleManifest), ManifestParser.Fingerprint(reformatted));
        Assert.NotEqual(
            ManifestParser.Fingerprint(SampleManifest),
            ManifestParser.Fingerprint(SampleManifest.Replace("win:UInt32", "win:UInt64", StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "R9: admitted fields compile to fixed offsets with a minimum body length")]
    public void CompilesFixedOffsets()
    {
        ProviderSchema schema = ManifestParser.Parse(SampleManifest);
        WindowsSourceDefinition definition = BuildDefinition(
        [
            new(10, 0, "sent", Mechanism.Tcp, ObservationKind.Send, Direction.Outbound,
            [
                new("PID", FieldRole.ProcessAttribution),
                new("size", FieldRole.ByteCount, MeasurementUnit.Bytes, ByteDomain.TransportObserved),
                new("sport", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderPort),
            ]),
        ]);

        SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(definition, schema, 0);

        AdmittedEventPlan descriptor = Assert.Single(plan.Events);
        Assert.Equal(3, descriptor.Slots.Count);
        Assert.Equal(0, descriptor.Slots[0].Offset);
        Assert.Equal(4, descriptor.Slots[1].Offset);
        Assert.Equal(18, descriptor.Slots[2].Offset);
        Assert.Equal(20, descriptor.MinimumBodyLength);
        Assert.All(descriptor.FieldReport, field => Assert.Equal(FieldAvailability.Present, field.Availability));
    }

    [Fact(DisplayName = "R21: a field behind a variable-length field is reported unknown, never guessed")]
    public void VariableLengthFieldBlocksLaterOffsets()
    {
        ProviderSchema schema = ManifestParser.Parse(SampleManifest);
        WindowsSourceDefinition definition = BuildDefinition(
        [
            new(20, 1, "named", Mechanism.ProcessLifecycle, ObservationKind.Create, Direction.DirectionNotApplicable,
            [
                new("ProcessID", FieldRole.ProcessAttribution),
                new("MissingField", FieldRole.Status),
            ]),
        ]);

        SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(definition, schema, 0);

        AdmittedEventPlan descriptor = Assert.Single(plan.Events);
        Assert.Empty(descriptor.Slots);
        FieldCapability blocked = descriptor.FieldReport.Single(field => field.Name == "ProcessID");
        Assert.Equal(FieldAvailability.SchemaUnknown, blocked.Availability);
        Assert.Contains("ImageName", blocked.Notes, StringComparison.Ordinal);
        FieldCapability missing = descriptor.FieldReport.Single(field => field.Name == "MissingField");
        Assert.Equal(FieldAvailability.NotExposed, missing.Availability);
    }

    [Fact(DisplayName = "R21: a descriptor the schema does not declare is refused with a reason")]
    public void UndeclaredDescriptorIsRefused()
    {
        ProviderSchema schema = ManifestParser.Parse(SampleManifest);
        WindowsSourceDefinition definition = BuildDefinition(
        [
            new(99, 0, "absent", Mechanism.Tcp, ObservationKind.Send, Direction.Outbound, []),
        ]);

        SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(definition, schema, 0);

        Assert.Empty(plan.Events);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Contains("not declared", StringComparison.Ordinal));
    }

    private static WindowsSourceDefinition BuildDefinition(IReadOnlyList<AdmittedEventIntent> events) => new()
    {
        SourceId = "etw/manifest/Sample-Provider",
        DisplayName = "Sample",
        Kind = SourceKind.ManifestProvider,
        ProviderName = "Sample-Provider",
        Mechanisms = [Mechanism.Tcp],
        RequiredPrivilege = PrivilegeRequirement.Administrator,
        Level = "win:Informational",
        MatchAnyKeyword = 0x10,
        RequestedKeywords = ["SAMPLE_KEYWORD_IPV4"],
        FilteringNotes = "test",
        StartupBehaviour = "test",
        ContractStatus = SourceContractStatus.Documented,
        AdmittedEvents = events,
    };
}
