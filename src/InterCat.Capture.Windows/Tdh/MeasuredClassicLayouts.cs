using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace InterCat.Capture.Windows;

/// <summary>
/// A classic kernel class's layout at one version, measured on one build for the opcodes named (ADR-091): every field by its
/// name and type in the order the records carry them, and the committed reports that measured it against a truth.
/// </summary>
public sealed record MeasuredClassicLayout(
    string BuildId,
    Guid ClassGuid,
    string TaskName,
    int Version,
    IReadOnlyList<(int Opcode, string Name)> Opcodes,
    IReadOnlyList<ProviderSchemaField> Fields,
    IReadOnlyList<string> Evidence);

/// <summary>
/// The layouts measured for classic kernel records a build raises at a version the machine registers no layout for (ADR-091).
/// A capture admits a classic record by its class's registration, which TDH resolves from a header alone (ADR-035); where TDH
/// describes none of a class's opcodes at the version a source is planned for, the layout a fixture measured for the build
/// reads them instead, and the capture says so. Each is carried here as the type numbers measured for a build are (ADR-082's
/// fourth addendum), never read from a delivered record, and its schema fingerprint is of a scheme no registration's is, so
/// a journal tells a measured interpretation from a registered one. A build none measured has none, and its source is refused.
/// </summary>
public static class MeasuredClassicLayouts
{
    /// <summary>The scheme a measured layout's schema fingerprint is computed under, which no registered layout's is.</summary>
    public const string FingerprintScheme = "intercat-measured-classic-schema-v1";

    private static readonly MeasuredClassicLayout[] Layouts =
    [
        // The kernel's file view flag on 10.0.26220.0-x64 (ADR-089): a view's mapping and unmapping, measured on FX-SECT-001's
        // views, and the rundowns at a session's start and stop, measured on the probe's own views and images (its fourth
        // addendum) - all at version 3, 44 bytes, the registered version 2's five fields with eight page-aligned bytes between
        // the size and the process.
        new(
            "10.0.26220.0-x64",
            WindowsSourceCatalog.FileIoEventClass,
            "FileIo",
            3,
            [(37, "MapFile"), (38, "UnmapFile"), (39, "MapFileDCStart"), (40, "MapFileDCEnd")],
            [
                ProviderSchemaField.Create("ViewBase", "win:Pointer"),
                ProviderSchemaField.Create("FileObject", "win:Pointer"),
                ProviderSchemaField.Create("MiscInfo", "win:UInt64"),
                ProviderSchemaField.Create("ViewSize", "win:UInt64"),
                ProviderSchemaField.Create("Offset", "win:UInt64"),
                ProviderSchemaField.Create("ProcessId", "win:UInt32"),
            ],
            [
                "bench/results/section-measurement-20261106T030404Z/report.json",
                "bench/results/section-measurement-20261106T030410Z/report.json",
                "bench/results/section-measurement-20261106T030417Z/report.json",
                "bench/results/section-rundowns-20261106T054519Z/report.json",
                "bench/results/section-rundowns-20261106T054527Z/report.json",
                "bench/results/section-rundowns-20261106T054533Z/report.json",
            ]),
    ];

    /// <summary>Every layout measured, on every build.</summary>
    public static IReadOnlyList<MeasuredClassicLayout> All => Layouts;

    /// <summary>
    /// The class's layout measured on <paramref name="buildId"/> at <paramref name="version"/>, as a schema of the opcodes asked
    /// for that it measured; null where it measured none of them, or none on that build.
    /// </summary>
    public static ProviderSchema? Find(string buildId, Guid classGuid, string className, int version, IReadOnlyList<int> opcodes, int pointerSize = 8)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentNullException.ThrowIfNull(opcodes);
        MeasuredClassicLayout? layout = Layouts.FirstOrDefault(candidate => string.Equals(candidate.BuildId, buildId, StringComparison.Ordinal)
            && candidate.ClassGuid == classGuid && candidate.Version == version);
        if (layout is null)
        {
            return null;
        }

        ProviderSchemaEvent[] events =
        [
            .. layout.Opcodes.Where(opcode => opcodes.Contains(opcode.Opcode)).OrderBy(static opcode => opcode.Opcode)
                .Select(opcode => new ProviderSchemaEvent(0, version, null, layout.TaskName, null, opcode.Name, opcode.Opcode, null, [], 0, null, layout.Fields)),
        ];
        return events.Length == 0
            ? null
            : new ProviderSchema(className, classGuid, Fingerprint(buildId, classGuid, version, pointerSize, events), new Dictionary<string, ulong>(), events);
    }

    /// <summary>What a capture whose source is read by a measured layout says of it, in its plan's warnings.</summary>
    public static string Statement(string buildId, int version) =>
        string.Create(CultureInfo.InvariantCulture,
            $"The machine registers no layout for this classic event class at version {version}, the version this build raises it at; ")
        + $"its records are read by the layout measured for build {buildId} (ADR-091), not by a registered one.";

    /// <summary>
    /// A measured layout's schema fingerprint: of its scheme, which no registered layout's is, the build it was measured on, the
    /// class, version and pointer size, and every opcode's fields in order, as a registered layout's covers its own.
    /// </summary>
    public static string Fingerprint(string buildId, Guid classGuid, int version, int pointerSize, IReadOnlyList<ProviderSchemaEvent> events)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentNullException.ThrowIfNull(events);
        var canonical = new StringBuilder(256);
        canonical.Append(FingerprintScheme).Append('\n')
            .Append(buildId).Append('\n')
            .Append(classGuid.ToString("N", CultureInfo.InvariantCulture)).Append('\n')
            .Append(version.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(pointerSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (ProviderSchemaEvent described in events.OrderBy(static item => item.OpcodeValue))
        {
            canonical.Append("opcode:").Append(described.OpcodeValue?.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (ProviderSchemaField field in described.Fields)
            {
                canonical.Append(field.Name).Append('|').Append(field.InType).Append('|')
                    .Append(((int)field.WidthKind).ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(field.FixedWidth.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
