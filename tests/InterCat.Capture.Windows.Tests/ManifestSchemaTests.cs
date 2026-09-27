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

    private const string ProcessManifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Sample-Process" guid="{22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716}">
           <templates>
            <template tid="Start">
             <data name="ProcessID" inType="win:UInt32" />
             <data name="ProcessSequenceNumber" inType="win:UInt64" />
             <data name="CreateTime" inType="win:FILETIME" />
             <data name="ParentProcessID" inType="win:UInt32" />
             <data name="ParentProcessSequenceNumber" inType="win:UInt64" />
             <data name="SessionID" inType="win:UInt32" />
             <data name="UserSID" inType="win:SID" />
             <data name="ImageName" inType="win:UnicodeString" />
            </template>
            <template tid="Stop">
             <data name="ProcessID" inType="win:UInt32" />
             <data name="ProcessSequenceNumber" inType="win:UInt64" />
             <data name="CreateTime" inType="win:FILETIME" />
             <data name="ExitTime" inType="win:FILETIME" />
             <data name="ExitCode" inType="win:UInt32" />
             <data name="ImageName" inType="win:AnsiString" />
            </template>
           </templates>
           <events>
            <event value="1" version="4" template="Start" />
            <event value="2" version="2" template="Stop" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

    // The TCPv6 send template of Microsoft-Windows-Kernel-Network on 10.0.26220 (event 26), a binary whose length another
    // field states, and a 16-byte binary that does not say it is an address.
    private const string Ipv6Manifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Sample-Network" guid="{7dd42a49-5329-4832-8dfd-43d979153a88}">
           <templates>
            <template tid="SentV6">
             <data name="PID" inType="win:UInt32" outType="xs:unsignedInt" />
             <data name="size" inType="win:UInt32" outType="xs:unsignedInt" />
             <data name="daddr" inType="win:Binary" outType="win:IPv6" length="16" />
             <data name="saddr" inType="win:Binary" outType="win:IPv6" length="16" />
             <data name="dport" inType="win:UInt16" outType="win:Port" />
             <data name="sport" inType="win:UInt16" outType="win:Port" />
             <data name="seqnum" inType="win:UInt32" outType="xs:unsignedInt" />
             <data name="connid" inType="win:UInt32" outType="xs:unsignedInt" />
            </template>
            <template tid="Blob">
             <data name="length" inType="win:UInt16" />
             <data name="payload" inType="win:Binary" length="length" />
             <data name="after" inType="win:UInt32" />
            </template>
            <template tid="Unlabelled">
             <data name="blob" inType="win:Binary" length="16" />
             <data name="port" inType="win:UInt16" />
            </template>
           </templates>
           <events>
            <event value="26" version="0" template="SentV6" />
            <event value="30" version="0" template="Blob" />
            <event value="31" version="0" template="Unlabelled" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

    [Fact(DisplayName = "R9: a 16-byte IPv6 address compiles to an address slot at a fixed offset, and the fields after it stay reachable")]
    public void AnIpv6AddressCompilesToAnAddressSlot()
    {
        ProviderSchema schema = ManifestParser.Parse(Ipv6Manifest);
        ProviderSchemaField daddr = schema.FindEvent(26)!.Fields[2];
        Assert.Equal((FieldWidthKind.Fixed, 16, "win:IPv6"), (daddr.WidthKind, daddr.FixedWidth, daddr.OutType));

        AdmittedEventPlan descriptor = Assert.Single(AdmissionPlanCompiler.Compile(BuildDefinition([Ipv6Send()]), schema, 0).Events);

        Assert.Equal(
            [("PID", 0, 4, AdmittedSlotKind.Numeric), ("size", 4, 4, AdmittedSlotKind.Numeric),
                ("saddr", 24, 16, AdmittedSlotKind.Address128), ("sport", 42, 2, AdmittedSlotKind.Numeric),
                ("daddr", 8, 16, AdmittedSlotKind.Address128), ("dport", 40, 2, AdmittedSlotKind.Numeric),
                ("connid", 48, 4, AdmittedSlotKind.Numeric)],
            descriptor.Slots.Select(slot => (slot.FieldName, slot.Offset, slot.Width, slot.Kind)));
        Assert.Equal(52, descriptor.MinimumBodyLength);
        Assert.All(descriptor.FieldReport, field => Assert.Equal(FieldAvailability.Present, field.Availability));

        // The record's two address slots are numbered in the order the plan lists them.
        Assert.Equal([-1, -1, 0, -1, 1, -1, -1],
            Enumerable.Range(0, descriptor.Slots.Count).Select(slot => AdmittedEvent.AddressOrdinal(descriptor.Slots, slot)));
    }

    [Fact(DisplayName = "R21: a binary field is fixed only by a stated length, and no bytes are read as an address unless the manifest says so")]
    public void OnlyADeclaredIpv6AddressIsReadAsOne()
    {
        ProviderSchema schema = ManifestParser.Parse(Ipv6Manifest);

        // A length another field states varies per record, so the field after it has no knowable offset.
        AdmittedEventPlan blob = Assert.Single(AdmissionPlanCompiler.Compile(BuildDefinition(
        [
            new(30, 0, "blob", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound,
                [new("after", FieldRole.Status)]),
        ]), schema, 0).Events);
        Assert.Equal(FieldAvailability.SchemaUnknown, Assert.Single(blob.FieldReport).Availability);

        // Sixteen bytes the manifest does not call IPv6, and an IPv6 address read as IPv4, are refused, never guessed.
        AdmittedEventPlan unlabelled = Assert.Single(AdmissionPlanCompiler.Compile(BuildDefinition(
        [
            new(31, 0, "unlabelled", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound,
                [new("blob", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderIpv6Address)]),
        ]), schema, 0).Events);
        Assert.Equal(FieldAvailability.SchemaUnknown, Assert.Single(unlabelled.FieldReport).Availability);
        Assert.Empty(unlabelled.Slots);
        AdmittedEventPlan narrowed = Assert.Single(AdmissionPlanCompiler.Compile(BuildDefinition(
        [
            new(26, 0, "narrowed", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound,
                [new("saddr", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address)]),
        ]), schema, 0).Events);
        Assert.Equal(FieldAvailability.SchemaUnknown, Assert.Single(narrowed.FieldReport).Availability);

        // A record holds two addresses; a third is refused with the reason.
        AdmittedEventPlan crowded = Assert.Single(AdmissionPlanCompiler.Compile(BuildDefinition(
        [
            new(26, 0, "crowded", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound,
            [
                new("saddr", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderIpv6Address),
                new("daddr", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderIpv6Address),
                new("saddr", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderIpv6Address),
            ]),
        ]), schema, 0).Events);
        Assert.Equal(2, crowded.Slots.Count);
        Assert.Contains("at most 2 addresses", crowded.FieldReport[2].Notes, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: a fixed binary length TDH reports is restored to a rebuilt manifest, and nothing else is changed")]
    public void FixedBinaryLengthsAreRestoredFromTdh()
    {
        // As TraceEvent rebuilds the provider from TDH: binaries with neither length nor out type, one template two events use.
        string rebuilt = Ipv6Manifest
            .Replace(" outType=\"win:IPv6\" length=\"16\"", string.Empty, StringComparison.Ordinal)
            .Replace("<event value=\"31\" version=\"0\" template=\"Unlabelled\" />",
                "<event value=\"31\" version=\"0\" template=\"Unlabelled\" /><event value=\"59\" version=\"0\" template=\"SentV6\" />",
                StringComparison.Ordinal);
        Assert.Equal(FieldWidthKind.Variable, ManifestParser.Parse(rebuilt).FindEvent(26)!.Fields[2].WidthKind);

        ManifestFieldShape[] tdh =
        [
            new(26, 0, "daddr", 16, "win:IPv6"), new(26, 0, "saddr", 16, "win:IPv6"),
            new(59, 0, "daddr", 16, "win:IPv6"), new(59, 0, "saddr", 16, "win:IPv6"),
            new(31, 0, "port", 16, null),
        ];
        ProviderSchema restored = ManifestParser.Parse(ManifestFieldShapes.Apply(rebuilt, tdh));
        Assert.All(restored.FindEvent(26)!.Fields.Where(field => field.InType == "win:Binary"), field =>
            Assert.Equal((FieldWidthKind.Fixed, 16, "win:IPv6"), (field.WidthKind, field.FixedWidth, field.OutType)));
        Assert.Equal(52, Assert.Single(AdmissionPlanCompiler.Compile(BuildDefinition([Ipv6Send()]), restored, 0).Events).MinimumBodyLength);
        Assert.NotEqual(ManifestParser.Parse(rebuilt).SchemaFingerprint, restored.SchemaFingerprint);

        // Only an unsized binary is given a length: a UInt16 is not, and a field no shape names keeps what it had.
        Assert.Equal(FieldWidthKind.Fixed, restored.FindEvent(31)!.Fields[1].WidthKind);
        Assert.Equal(2, restored.FindEvent(31)!.Fields[1].FixedWidth);

        // Two events that describe one template's field differently leave it unsized, and a manifest nothing is restored
        // to is the same text, so its fingerprint is unchanged.
        ManifestFieldShape[] disagreeing = [new(26, 0, "daddr", 16, "win:IPv6"), new(59, 0, "daddr", 4, null)];
        Assert.Equal(FieldWidthKind.Variable,
            ManifestParser.Parse(ManifestFieldShapes.Apply(rebuilt, disagreeing)).FindEvent(26)!.Fields[2].WidthKind);
        Assert.Same(rebuilt, ManifestFieldShapes.Apply(rebuilt, disagreeing));
        Assert.Same(SampleManifest, ManifestFieldShapes.Apply(SampleManifest, tdh));
        Assert.Same(SampleManifest, ManifestFieldShapes.Apply(SampleManifest, []));
    }

    /// <summary>The admission a TCPv6 send is compiled from: the IPv4 send's fields, with 16-byte addresses.</summary>
    private static AdmittedEventIntent Ipv6Send() =>
        new(26, 0, "TCPv6 data sent", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound,
        [
            new("PID", FieldRole.ProcessAttribution),
            new("size", FieldRole.ByteCount, MeasurementUnit.Bytes, ByteDomain.TransportObserved),
            new("saddr", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderIpv6Address),
            new("sport", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderPort),
            new("daddr", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderIpv6Address),
            new("dport", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderPort),
            new("connid", FieldRole.CorrelationKey, SourceField: SourceField.ConnectionId),
        ]);

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
            new(10, 0, "sent", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound,
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
            new(20, 1, "named", Mechanism.ProcessLifecycle, ObservationLayer.Lifecycle, ObservationKind.Create, Direction.DirectionNotApplicable,
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
            new(99, 0, "absent", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound, []),
        ]);

        SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(definition, schema, 0);

        Assert.Empty(plan.Events);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Contains("not declared", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R9: a process image after a bounded SID and an ANSI stop image are admitted without guessing offsets")]
    public void ProcessImageShapesAreCompiled()
    {
        ProviderSchema schema = ManifestParser.Parse(ProcessManifest);
        WindowsSourceDefinition definition = BuildDefinition(
        [
            new(1, 4, "start", Mechanism.ProcessLifecycle, ObservationLayer.Lifecycle, ObservationKind.Create,
                Direction.DirectionNotApplicable,
                [
                    new("ProcessID", FieldRole.ProcessAttribution),
                    new("ProcessSequenceNumber", FieldRole.CorrelationKey, SourceField: SourceField.ProcessStartSequence),
                    new("ImageName", FieldRole.ResourceName),
                ]),
            new(2, 2, "stop", Mechanism.ProcessLifecycle, ObservationLayer.Lifecycle, ObservationKind.Exit,
                Direction.DirectionNotApplicable,
                [
                    new("ProcessID", FieldRole.ProcessAttribution),
                    new("ImageName", FieldRole.ResourceName),
                ]),
        ]);

        SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(definition, schema, 0);
        AdmittedEventPlan start = plan.Events.Single(item => item.EventId == 1);
        AdmittedEventPlan stop = plan.Events.Single(item => item.EventId == 2);
        Assert.Equal(AdmittedSlotKind.ResourceNameAfterSid, start.Slots.Single(item => item.Role == FieldRole.ResourceName).Kind);
        Assert.Equal(36, start.Slots.Single(item => item.Role == FieldRole.ResourceName).Offset);
        Assert.Equal(SourceField.ProcessStartSequence, start.Slots.Single(item => item.SourceField is not null).SourceField);
        Assert.Equal(AdmittedSlotKind.AnsiResourceName, stop.Slots.Single(item => item.Role == FieldRole.ResourceName).Kind);
        Assert.Equal(32, stop.Slots.Single(item => item.Role == FieldRole.ResourceName).Offset);
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
