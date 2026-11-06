using System.Runtime.InteropServices;
using System.Text;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// A process's package admitted from its lifecycle records (ADR-094): Kernel-Process's start and rundown compile the package
/// as a second text after the image past the mandatory label's SID, and the callback reads it bounded by the record - a
/// package's name, an empty one known as none, and one the record ends before or one longer than any package's name unknown.
/// </summary>
public sealed class ProcessPackageAdmissionTests
{
    // Kernel-Process's start and rundown template as this build registers it (bench/results/process-provider-inspection-20261106T120609Z).
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
             <data name="SecurityMitigations" inType="win:UInt32" />
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

    private const string Package = "Microsoft.DesktopAppInstaller_1.29.250.0_x64__8wekyb3d8bbwe";

    [Fact(DisplayName = "R9: Kernel-Process's start and rundown admit the package after the image as a second text - its slot at the mandatory label's SID, the image's checksum and time stamp's eight bytes past the image's terminator - their eighth field")]
    public void ThePackageCompilesAfterTheImage()
    {
        SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(
            WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelProcessSourceId)!, ManifestParser.Parse(ProcessManifest), 0);
        Assert.Equal([1, 15], plan.Events.Select(static descriptor => descriptor.EventId).Order());
        foreach (AdmittedEventPlan descriptor in plan.Events)
        {
            AdmittedSlotPlan[] slots = [.. descriptor.Slots];
            int name = Array.FindIndex(slots, static slot => slot.Kind == AdmittedSlotKind.ResourceNameAfterSid);
            int package = Array.FindIndex(slots, static slot => slot.Kind == AdmittedSlotKind.TextAfterResourceName);
            Assert.True(name >= 0 && package > name, "the package follows the image it is reached past");
            Assert.Equal(("PackageFullName", (SourceField?)SourceField.ProcessPackageFullName, slots[name].Offset, 8),
                (slots[package].FieldName, slots[package].SourceField, slots[package].Offset, slots[package].Width));
            Assert.Equal(AdmissionPlanCompiler.MaximumSlots, slots.Length);
            Assert.Contains(descriptor.FieldReport, static field => field.Name == "PackageFullName" && field.Availability == FieldAvailability.Present
                && field.Notes!.Contains("past the SID 'MandatoryLabel' by 8 bytes of fixed fields", StringComparison.Ordinal));
        }

        // Without the image before it the package has nothing to be reached past, and is not admitted.
        WindowsSourceDefinition definition = WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelProcessSourceId)!;
        WindowsSourceDefinition packageAlone = definition with
        {
            AdmittedEvents = [.. definition.AdmittedEvents.Select(static intent => intent with
            {
                Fields = [.. intent.Fields.Where(static field => field.FieldName != "ImageName")],
            })],
        };
        SourceAdmissionPlan alone = AdmissionPlanCompiler.Compile(packageAlone, ManifestParser.Parse(ProcessManifest), 0);
        Assert.All(alone.Events.Where(static descriptor => descriptor.EventId == 1),
            static descriptor => Assert.DoesNotContain(descriptor.Slots, static slot => slot.Kind == AdmittedSlotKind.TextAfterResourceName));
    }

    [Fact(DisplayName = "R9: the callback reads the package after the image bounded by the record - a package's name, an empty one known as none, and as unknown one the record ends before, one unterminated or one longer than any package's name - and finds no end of an image that runs past the record")]
    public void TheCallbackReadsThePackageBounded()
    {
        Assert.Equal(Package, Read(Body(Package)).Text);
        Assert.Equal((true, 0), (Read(Body(string.Empty)).HasText, Read(Body(string.Empty)).Length));

        // A record that ends before the package, or whose package has no terminator in it, carries none it can be read as.
        byte[] whole = Body(Package);
        int packageAt = whole.Length - (2 * (Package.Length + 1)) - (2 * ("App".Length + 1));
        Assert.False(Read(whole[..packageAt]).HasText);
        Assert.False(Read(whole[..(packageAt + 10)]).HasText);

        // A package's full name is at most 127 characters: a longer text is no package's, and is left unknown, never cut.
        Assert.Equal(AdmittedEvent.MaximumTextLength, Read(Body(new string('p', AdmittedEvent.MaximumTextLength))).Length);
        Assert.False(Read(Body(new string('p', AdmittedEvent.MaximumTextLength + 1))).HasText);

        // An image whose terminator the record never reaches has no end the package could follow.
        byte[] cut = Body(Package)[..(12 + 6)];
        GCHandle pinned = GCHandle.Alloc(cut, GCHandleType.Pinned);
        try
        {
            Assert.True(TraceEventRecordAdmitter.TryOffsetAfterSid(pinned.AddrOfPinnedObject(), 0, cut.Length, out int image));
            Assert.Equal(12, image);
            Assert.False(TraceEventRecordAdmitter.TryOffsetAfterText(pinned.AddrOfPinnedObject(), image, cut.Length, out _));
        }
        finally
        {
            pinned.Free();
        }
    }

    // The mandatory label's SID (a high integrity level), the image, its checksum and time stamp, the package and its
    // application's ID, as the start and the rundown lay them out from the label on.
    private static byte[] Body(string package)
    {
        var body = new List<byte>([1, 1, 0, 0, 0, 0, 0, 16, 0x00, 0x30, 0, 0]);
        body.AddRange(Encoding.Unicode.GetBytes(@"\Device\HarddiskVolume3\Program Files\WindowsApps\winget.exe" + "\0"));
        body.AddRange(BitConverter.GetBytes(0x1234_5678u));
        body.AddRange(BitConverter.GetBytes(0x6000_0000u));
        body.AddRange(Encoding.Unicode.GetBytes(package + "\0"));
        body.AddRange(Encoding.Unicode.GetBytes("App\0"));
        return [.. body];
    }

    // The package as the callback reads it from a body whose SID begins it.
    private static (bool HasText, int Length, string Text) Read(byte[] body)
    {
        GCHandle pinned = GCHandle.Alloc(body, GCHandleType.Pinned);
        try
        {
            IntPtr at = pinned.AddrOfPinnedObject();
            AdmittedEvent admitted = default;
            Assert.True(TraceEventRecordAdmitter.TryOffsetAfterSid(at, 0, body.Length, out int image));
            Assert.True(TraceEventRecordAdmitter.TryOffsetAfterText(at, image, body.Length, out int afterImage));
            TraceEventRecordAdmitter.ReadBoundedText(at, afterImage + 8, body.Length, ref admitted);
            (bool has, string text) = (admitted.HasText, new string(admitted.Text));

            // A detached record carries its text no more: the buffer goes back, and the record says it has none.
            if (admitted.DetachText() is { } rented)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }

            Assert.False(admitted.HasText);
            return (has, text.Length, text);
        }
        finally
        {
            pinned.Free();
        }
    }
}
