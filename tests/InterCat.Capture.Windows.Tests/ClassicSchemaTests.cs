using System.Text;
using InterCat.Capture.Windows;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>ADR-035's sixth decision: a classic kernel event's layout comes from the machine's own registration.</summary>
public sealed class ClassicSchemaTests
{
    private static readonly Guid AlpcClass = Guid.Parse("45d8cccd-539f-4b72-a8b7-5c683142609a");

    [Fact(DisplayName = "§18.3: TDH describes ALPC's classic events from a header alone: send, receive and wait for reply carry a 32-bit message id")]
    public void AlpcLayoutComesFromTheMachine()
    {
        if (!OperatingSystem.IsWindows()) return;

        ProviderSchema schema = Assert.IsType<ProviderSchema>(
            TdhClassicSchemaReader.Read(AlpcClass, "ALPC", 2, [33, 34, 35, 36, 37]));

        Assert.Equal(AlpcClass, schema.ProviderGuid);
        Assert.StartsWith("sha256:", schema.SchemaFingerprint, StringComparison.Ordinal);
        foreach (int opcode in new[] { 33, 34, 35 })
        {
            ProviderSchemaEvent described = schema.Events.Single(item => item.OpcodeValue == opcode);
            Assert.Equal(0, described.EventId);
            Assert.Equal(2, described.Version);
            ProviderSchemaField field = Assert.Single(described.Fields);
            Assert.Equal("MessageID", field.Name);
            Assert.Equal("win:UInt32", field.InType);
            Assert.Equal((FieldWidthKind.Fixed, 4), (field.WidthKind, field.FixedWidth));
        }

        // Unwait carries a status, not a message id; a wait for a new message ends in a variable port name.
        Assert.Equal("Status", Assert.Single(schema.Events.Single(item => item.OpcodeValue == 37).Fields).Name);
        ProviderSchemaEvent waiting = schema.Events.Single(item => item.OpcodeValue == 36);
        Assert.Equal(FieldWidthKind.Variable, waiting.Fields[^1].WidthKind);

        // Read again, the same layout gives the same fingerprint; a version the machine does not register gives nothing.
        Assert.Equal(schema.SchemaFingerprint, TdhClassicSchemaReader.Read(AlpcClass, "ALPC", 2, [37, 36, 35, 34, 33])!.SchemaFingerprint);
        Assert.Null(TdhClassicSchemaReader.Read(Guid.NewGuid(), "none", 2, [33]));
    }

    [Fact(DisplayName = "§18.3: a classic event's fields are read in order from TDH's description, and a field another sizes is variable")]
    public void AClassicDescriptionParsesInOrder()
    {
        byte[] info = Description(("MessageID", 8, 0, 1, 0), ("Name", 1, 0x2, 1, 0), ("Blob", 14, 0x8, 1, 12));

        ProviderSchemaEvent described = Assert.IsType<ProviderSchemaEvent>(TdhClassicSchemaReader.Parse(info, 33, 2));
        Assert.Equal(33, described.OpcodeValue);
        Assert.Equal("ALPC", described.TaskName);
        Assert.Equal("SendMessage", described.OpcodeName);
        Assert.Equal(["MessageID", "Name", "Blob"], described.Fields.Select(field => field.Name));
        Assert.Equal((FieldWidthKind.Fixed, 4), (described.Fields[0].WidthKind, described.Fields[0].FixedWidth));
        Assert.Equal(FieldWidthKind.Variable, described.Fields[1].WidthKind);
        Assert.Equal((FieldWidthKind.Fixed, 12), (described.Fields[2].WidthKind, described.Fields[2].FixedWidth));

        // A manifest's decoding, or a buffer too short to hold its properties, is not a classic description.
        info[48] = 0;
        Assert.Null(TdhClassicSchemaReader.Parse(info, 33, 2));
        Assert.Null(TdhClassicSchemaReader.Parse(info.AsSpan(0, 100), 33, 2));
    }

    [Fact(DisplayName = "§18.3: a classic class's fingerprint follows its layout, not the order its opcodes were asked in")]
    public void AClassicFingerprintFollowsItsLayout()
    {
        ProviderSchemaEvent send = Event(33, ProviderSchemaField.Create("MessageID", "win:UInt32"));
        ProviderSchemaEvent receive = Event(34, ProviderSchemaField.Create("MessageID", "win:UInt32"));
        ProviderSchemaEvent wider = Event(34, ProviderSchemaField.Create("MessageID", "win:UInt64"));

        string fingerprint = TdhClassicSchemaReader.Fingerprint(AlpcClass, 2, 8, [send, receive]);
        Assert.Equal(fingerprint, TdhClassicSchemaReader.Fingerprint(AlpcClass, 2, 8, [receive, send]));
        Assert.NotEqual(fingerprint, TdhClassicSchemaReader.Fingerprint(AlpcClass, 2, 8, [send, wider]));
        Assert.NotEqual(fingerprint, TdhClassicSchemaReader.Fingerprint(AlpcClass, 2, 4, [send, receive]));
    }

    private static ProviderSchemaEvent Event(int opcode, params ProviderSchemaField[] fields) =>
        new(0, 2, null, "ALPC", null, null, opcode, null, [], 0, null, fields);

    /// <summary>A TRACE_EVENT_INFO a WMI class's decoding would give: names after the property array.</summary>
    private static byte[] Description(params (string Name, ushort InType, int Flags, ushort Count, ushort Length)[] properties)
    {
        int names = 112 + (properties.Length * 24);
        var buffer = new List<byte>(new byte[names]);
        int Append(string text)
        {
            int at = buffer.Count;
            buffer.AddRange(Encoding.Unicode.GetBytes(text + "\0"));
            return at;
        }

        int task = Append("ALPC");
        int opcode = Append("SendMessage");
        int[] nameOffsets = [.. properties.Select(property => Append(property.Name))];
        byte[] info = [.. buffer];
        BitConverter.GetBytes(1).CopyTo(info, 48);
        BitConverter.GetBytes(task).CopyTo(info, 68);
        BitConverter.GetBytes(opcode).CopyTo(info, 72);
        BitConverter.GetBytes(properties.Length).CopyTo(info, 100);
        BitConverter.GetBytes(properties.Length).CopyTo(info, 104);
        for (int index = 0; index < properties.Length; index++)
        {
            int at = 112 + (index * 24);
            BitConverter.GetBytes(properties[index].Flags).CopyTo(info, at);
            BitConverter.GetBytes(nameOffsets[index]).CopyTo(info, at + 4);
            BitConverter.GetBytes(properties[index].InType).CopyTo(info, at + 8);
            BitConverter.GetBytes(properties[index].Count).CopyTo(info, at + 16);
            BitConverter.GetBytes(properties[index].Length).CopyTo(info, at + 18);
        }

        return info;
    }
}
