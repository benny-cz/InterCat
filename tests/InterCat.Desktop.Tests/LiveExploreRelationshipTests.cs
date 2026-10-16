using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Capture.Journal.Tests;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The window's live view of an Explore capture, end to end on records shaped as Windows delivers them (§12, R22): admission
/// plans compiled through the source catalog from the providers' manifests, as the broker compiles them from what TDH
/// reports; record bodies laid out as those manifests declare, read slot by slot as the ETW admitter reads them; the
/// capture-state rundown of the machine's processes; loopback TCP over IPv4 and IPv6, owned by their payload's PID while
/// a receive's header names the sender, beside a connection to another host; evidence published chunk by chunk while the
/// window's derivation follows it. Its graph must relate both loopback connections and say where the other went.
/// </summary>
public sealed class LiveExploreRelationshipTests
{
    private static readonly Guid Network = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");
    private static readonly Guid Process = Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");

    // Microsoft-Windows-Kernel-Network's transfer templates as its manifest declares them: addresses and ports in network
    // order, the remote endpoint before the owner's own.
    private const string NetworkManifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Microsoft-Windows-Kernel-Network" guid="{7dd42a49-5329-4832-8dfd-43d979153a88}">
           <templates>
            <template tid="SendV4">
             <data name="PID" inType="win:UInt32" />
             <data name="size" inType="win:UInt32" />
             <data name="daddr" inType="win:UInt32" outType="win:IPv4" />
             <data name="saddr" inType="win:UInt32" outType="win:IPv4" />
             <data name="dport" inType="win:UInt16" outType="win:Port" />
             <data name="sport" inType="win:UInt16" outType="win:Port" />
             <data name="startime" inType="win:UInt32" />
             <data name="endtime" inType="win:UInt32" />
             <data name="seqnum" inType="win:UInt32" />
             <data name="connid" inType="win:UInt32" />
            </template>
            <template tid="ReceiveV4">
             <data name="PID" inType="win:UInt32" />
             <data name="size" inType="win:UInt32" />
             <data name="daddr" inType="win:UInt32" outType="win:IPv4" />
             <data name="saddr" inType="win:UInt32" outType="win:IPv4" />
             <data name="dport" inType="win:UInt16" outType="win:Port" />
             <data name="sport" inType="win:UInt16" outType="win:Port" />
             <data name="seqnum" inType="win:UInt32" />
             <data name="connid" inType="win:UInt32" />
            </template>
            <template tid="TransferV6">
             <data name="PID" inType="win:UInt32" />
             <data name="size" inType="win:UInt32" />
             <data name="daddr" inType="win:Binary" outType="win:IPv6" length="16" />
             <data name="saddr" inType="win:Binary" outType="win:IPv6" length="16" />
             <data name="dport" inType="win:UInt16" outType="win:Port" />
             <data name="sport" inType="win:UInt16" outType="win:Port" />
             <data name="seqnum" inType="win:UInt32" />
             <data name="connid" inType="win:UInt32" />
            </template>
           </templates>
           <events>
            <event value="10" version="0" template="SendV4" />
            <event value="11" version="0" template="ReceiveV4" />
            <event value="26" version="0" template="TransferV6" />
            <event value="27" version="0" template="TransferV6" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

    // Microsoft-Windows-Kernel-Process's start and rundown template: the image name follows a variable-length SID.
    private const string ProcessManifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Microsoft-Windows-Kernel-Process" guid="{22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716}">
           <templates>
            <template tid="Start">
             <data name="ProcessID" inType="win:UInt32" />
             <data name="ProcessSequenceNumber" inType="win:UInt64" />
             <data name="CreateTime" inType="win:FILETIME" />
             <data name="ParentProcessID" inType="win:UInt32" />
             <data name="ParentProcessSequenceNumber" inType="win:UInt64" />
             <data name="SessionID" inType="win:UInt32" />
             <data name="Flags" inType="win:UInt32" />
             <data name="ProcessTokenElevationType" inType="win:UInt32" />
             <data name="ProcessTokenIsElevated" inType="win:UInt32" />
             <data name="MandatoryLabel" inType="win:SID" />
             <data name="ImageName" inType="win:UnicodeString" />
             <data name="ImageChecksum" inType="win:UInt32" />
             <data name="TimeDateStamp" inType="win:UInt32" />
             <data name="PackageFullName" inType="win:UnicodeString" />
             <data name="PackageRelativeAppId" inType="win:UnicodeString" />
            </template>
           </templates>
           <events>
            <event value="1" version="4" template="Start" />
            <event value="15" version="2" template="Start" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

    [Fact(DisplayName = "R22: the window's live view of an Explore capture shaped as Windows delivers it relates its loopback connections over IPv4 and IPv6")]
    public void ALiveExploreCaptureRelatesItsLoopbackConnections() => SingleThreadedContext.Run(async () =>
    {
        SourceAdmissionPlan processes = AdmissionPlanCompiler.Compile(
            WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelProcessSourceId)!, ManifestParser.Parse(ProcessManifest), 0);
        SourceAdmissionPlan network = AdmissionPlanCompiler.Compile(
            WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelNetworkSourceId)!, ManifestParser.Parse(NetworkManifest), 1);
        var table = new EventAdmissionTable([processes, network]);

        using var root = new TemporaryDirectory();
        string evidencePath = Path.Combine(root.Path, "evidence");
        Directory.CreateDirectory(evidencePath);
        SessionStore evidence = SessionStore.Open(LocalOwnedDirectory.Open(evidencePath), Guid.NewGuid(), "live-explore");
        var host = new ScriptedHost();
        long qpc = Stopwatch.GetTimestamp();
        long ordinal = 0;
        void Deliver(Guid provider, int eventId, int version, int header, byte[] body) =>
            Admit(host, table, provider, eventId, version, qpc += 3, ++ordinal, header, body);

        // The capture-state rundown names the machine's processes before any traffic: the client, the server, a browser
        // and twenty service hosts, each with its start key and image.
        long created = DateTime.UtcNow.AddHours(-1).ToFileTimeUtc();
        (int Pid, string Image)[] running =
        [
            (100, @"C:\Tools\client.exe"), (200, @"C:\Tools\server.exe"), (300, @"C:\Apps\browser.exe"),
            .. Enumerable.Range(0, 20).Select(index => (1_000 + (index * 4), @"C:\Windows\System32\svchost.exe")),
        ];
        foreach ((int pid, string image) in running)
        {
            Deliver(Process, 15, 2, pid, ProcessBody(pid, (ulong)(10_000 + pid), created, image));
        }

        // Each burst lands in a chunk of its own, as the broker publishes while the window follows.
        const uint Loopback = 0x7F00_0001;
        for (int burst = 0; burst < 3; burst++)
        {
            int published = burst + 1;
            host.PauseUntil(
                () => evidence.Current is { } current
                    && current.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal) >= published,
                TimeSpan.FromSeconds(30));
            for (int round = 0; round < 4; round++)
            {
                // client.exe at 127.0.0.1:50000 and server.exe at 127.0.0.1:8080: a receive is raised in the sender's
                // context, so its header names the sender while its payload names the owner.
                Deliver(Network, 10, 0, 100, V4(100, 64, (Loopback, 50000), (Loopback, 8080), startTimes: true));
                Deliver(Network, 11, 0, 100, V4(200, 64, (Loopback, 8080), (Loopback, 50000), startTimes: false));
                Deliver(Network, 10, 0, 200, V4(200, 16, (Loopback, 8080), (Loopback, 50000), startTimes: true));
                Deliver(Network, 11, 0, 200, V4(100, 16, (Loopback, 50000), (Loopback, 8080), startTimes: false));

                // browser.exe at [::1]:51000 and server.exe at [::1]:9090, from the second burst on.
                if (burst > 0)
                {
                    Deliver(Network, 26, 0, 300, V6(300, 32, (UInt128.One, 51000), (UInt128.One, 9090)));
                    Deliver(Network, 27, 0, 300, V6(200, 32, (UInt128.One, 9090), (UInt128.One, 51000)));
                }

                // browser.exe to another host, whose other end no record of the capture can hold.
                Deliver(Network, 10, 0, 300, V4(300, 1_200, (0xC0A8_010A, 52000), (0x8EFA_0101, 443), startTimes: true));
                Deliver(Network, 11, 0, 4, V4(300, 1_400, (0xC0A8_010A, 52000), (0x8EFA_0101, 443), startTimes: false));
            }
        }

        var owned = new OwnedSessionPlan
        {
            Identity = CaptureSessionIdentity.Create("live-explore", 4242),
            Providers = ProviderEnablementCompiler.Compile([processes, network]),
            Sources = [processes, network],
        };
        Task<LiveRecordingResult> recording = LiveSessionRecorder.RecordAsync(
            owned, host, evidence, _ => host.Delivered.Task, DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(50), output: LiveRecordingOutput.EvidenceOnly,
            calibration: EvidenceRecordings.Calibration());

        // The window's derivation follows the evidence while it is published, projecting each generation it derives.
        using var derivation = new LiveDerivation(evidencePath, Path.Combine(root.Path, "session"), Stopwatch.StartNew(),
            new SessionStoreRegistry(capacity: 4));
        var drawn = new List<int>();
        SessionOverviewBundle? last = null;
        while (true)
        {
            LiveDerivationStep step = await derivation.StepAsync(CancellationToken.None);
            Assert.Null(step.OverviewProblem);
            if (step.Overview is { } overview)
            {
                drawn.Add(overview.Edges.Count);
                last = overview;
            }

            if (recording.IsCompleted && step.Follow is { Finished: true } && step.Overview is null)
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.True((await recording).Start.Started);
        evidence.ReleaseSegmentReaders();

        // Once the server's IPv4 end is in, every generation relates it; by the end both loopback connections are drawn.
        Assert.Equal(2, drawn[^1]);
        Assert.Equal(drawn.Order(), drawn);
        SessionOverviewBundle final = last!;
        Assert.All(final.Edges, edge => Assert.Equal(Mechanism.Tcp, edge.Mechanism));
        int[] ends = [.. final.Edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId })
            .Select(id => final.Nodes.Single(node => node.Id == id).ProcessId).Order()];
        Assert.Equal([100, 200, 200, 300], ends);
        Assert.Equal((2, 1, 1), (final.Census!.Tcp.Relationships, final.Census.Tcp.OtherHostConnections, final.Census.Tcp.OtherHosts));

        // The browser's connection to another host, whose receive the System process's context raised, is its one host link.
        HostLink reach = Assert.Single(final.HostLinks);
        Assert.Equal((300, Mechanism.Tcp, 1, 1), (final.Nodes.Single(node => node.Id == reach.Process).ProcessId, reach.Mechanism,
            reach.Connections, reach.Hosts));

        // The window draws them, and the browser's connection to Other hosts, and says so in its graph's summary.
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(final), final.GraphIdentity);
        await workspace.LayoutReady;
        Assert.StartsWith(running.Length.ToString(System.Globalization.CultureInfo.CurrentCulture) + " processes · 2 relationships",
            workspace.GraphSummary, StringComparison.Ordinal);
        Assert.EndsWith(" · 1 with connections to other hosts", workspace.GraphSummary, StringComparison.Ordinal);
        Assert.Equal(1, workspace.GraphDisplay.OtherHosts?.ConnectedProcesses);
        Assert.Null(workspace.GraphEmptyExplanation);
        derivation.Store!.ReleaseSegmentReaders();
    });

    /// <summary>
    /// Admits a record as the ETW admitter does: its descriptor's plan by provider, id and version; its body checked
    /// against the plan's minimum length; then each slot read at its offset - a number of its width, little-endian as
    /// delivered, an IPv6 address big-endian into the next address slot, an image name past its SID.
    /// </summary>
    private static void Admit(ScriptedHost host, EventAdmissionTable table, Guid provider, int eventId, int version, long qpc,
        long ordinal, int header, byte[] body)
    {
        AdmittedEventPlan plan = table.Find(provider, eventId, version)
            ?? throw new InvalidOperationException($"No admission plan for event {eventId} v{version}.");
        Assert.True(body.Length >= plan.MinimumBodyLength);
        AdmittedEvent admitted = default;
        admitted.PointerSize = 8;
        admitted.SourceIndex = plan.SourceIndex;
        admitted.EventId = eventId;
        admitted.Version = version;
        admitted.TimestampQpc = qpc;
        admitted.TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        admitted.HeaderProcessId = header;
        admitted.HeaderThreadId = header + 1;
        admitted.RecordOrdinal = ordinal;
        int address = 0;
        for (int index = 0; index < plan.Slots.Count; index++)
        {
            AdmittedSlotPlan slot = plan.Slots[index];
            switch (slot.Kind)
            {
                case AdmittedSlotKind.ResourceNameAfterSid:
                    admitted.SetName(Utf16At(body, slot.Offset + 8 + (4 * body[slot.Offset + 1])), truncated: false);
                    break;
                case AdmittedSlotKind.Address128:
                    admitted.SetAddress(address++, BinaryPrimitives.ReadUInt128BigEndian(body.AsSpan(slot.Offset, 16)));
                    break;
                case AdmittedSlotKind.Numeric:
                    admitted.SetSlot(index, slot.Width switch
                    {
                        2 => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(slot.Offset)),
                        4 => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(slot.Offset)),
                        8 => BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(slot.Offset)),
                        _ => throw new InvalidOperationException($"No {slot.Width}-byte slot is laid out here."),
                    });
                    break;
                default:
                    throw new InvalidOperationException($"No {slot.Kind} slot is laid out here.");
            }
        }

        admitted.BeginExtendedData(ExtendedDataAvailability.NotRequested, 0);
        host.Admit(admitted, new DeliveredRecord(provider, eventId, version, qpc));
    }

    private static string Utf16At(byte[] body, int offset)
    {
        int end = offset;
        while (body[end] != 0 || body[end + 1] != 0)
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(body, offset, end - offset);
    }

    /// <summary>A start or rundown body: the high mandatory label's SID, then the image, as the manifest lays them out.</summary>
    private static byte[] ProcessBody(int pid, ulong sequence, long created, string image)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((uint)pid));
        body.AddRange(BitConverter.GetBytes(sequence));
        body.AddRange(BitConverter.GetBytes(created));
        body.AddRange(BitConverter.GetBytes(4u));
        body.AddRange(BitConverter.GetBytes(1UL));
        body.AddRange(BitConverter.GetBytes(1u));
        body.AddRange(new byte[12]);
        body.AddRange([1, 1, 0, 0, 0, 0, 0, 16, 0, 0x30, 0, 0]);
        body.AddRange(Encoding.Unicode.GetBytes(image + "\0"));
        body.AddRange(new byte[12]);
        return [.. body];
    }

    /// <summary>An IPv4 transfer body: the owner, the size, then the remote endpoint before the owner's own, in network order.</summary>
    private static byte[] V4(int owner, uint size, (uint Address, ushort Port) own, (uint Address, ushort Port) remote, bool startTimes)
    {
        byte[] body = new byte[startTimes ? 36 : 28];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), (uint)owner);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), size);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8), remote.Address);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12), own.Address);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(16), remote.Port);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(18), own.Port);
        return body;
    }

    /// <summary>An IPv6 transfer body: each address its 16 bytes in network order.</summary>
    private static byte[] V6(int owner, uint size, (UInt128 Address, ushort Port) own, (UInt128 Address, ushort Port) remote)
    {
        byte[] body = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), (uint)owner);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), size);
        BinaryPrimitives.WriteUInt128BigEndian(body.AsSpan(8), remote.Address);
        BinaryPrimitives.WriteUInt128BigEndian(body.AsSpan(24), own.Address);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(40), remote.Port);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(42), own.Port);
        return body;
    }
}
