using System.Runtime.InteropServices;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// What one process announced of the SDK it speaks (`contracts/sdk-v1.md` §1): the schema's major - its record's event
/// version - and minor, the SDK's library, and the frequency of the clock its readings are in. A process announces it to
/// each session enabling the provider, so a process's equal announcements are one.
/// </summary>
public sealed record CooperativeAnnouncement
{
    public required int ProcessId { get; init; }

    public required ProcessBinding Process { get; init; }

    /// <summary>The schema's major it raises: its announcement's event version.</summary>
    public required int Major { get; init; }

    /// <summary>The schema's minor it raises; null where the capture did not keep it.</summary>
    public int? Minor { get; init; }

    /// <summary>The SDK library's version, as it announces it; null where the capture did not keep it.</summary>
    public string? Library { get; init; }

    /// <summary>The frequency of the clock its readings are in; null where the capture did not keep it.</summary>
    public long? TicksPerSecond { get; init; }
}

/// <summary>
/// The SDK's announcements of one generation (ADR-111): each process's schema, library and clock, read from the SDK's own
/// provider's records as a capture admits them, a process's equal announcements once. What the processes announce, beside
/// what the capture admitted, says which of their events never reached it. It changes no observation (R1).
/// </summary>
public static class CooperativeAnnouncements
{
    /// <summary>Every distinct announcement the generation holds, in the order first recorded.</summary>
    public static IReadOnlyList<CooperativeAnnouncement> Read(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(processes);
        Dictionary<RecordAddress, (long? Minor, long? Frequency)> carried = ReadFields(fieldSegments, cancellationToken);
        var announced = new List<CooperativeAnnouncement>();
        var seen = new HashSet<(int, ProcessBinding, int, int?, string?, long?)>();
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            bool opened = false;
            SegmentColumnSlice ticks = default, streams = default, epochs = default, ordinals = default, factHigh = default,
                factLow = default;
            var owners = new RecordOwnerColumns(segment);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.ApplicationSdk)
                {
                    continue;
                }

                // A record is the SDK's by its provider: the content fixture's records share the mechanism and number from 1 too.
                (Guid provider, ushort eventId, byte version, _) = segment.SchemaOf(row);
                if (provider != CooperativeSdk.ProviderId || eventId != CooperativeSdk.SdkStarted
                    || owners.At(row, Mechanism.ApplicationSdk) is not { } processId)
                {
                    continue;
                }

                if (!opened)
                {
                    ticks = segment.Slice(SegmentColumnId.NativeTicks);
                    streams = segment.Slice(SegmentColumnId.RawStreamId);
                    epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                    ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                    factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
                    factLow = segment.Slice(SegmentColumnId.FactKeyLow);
                    opened = true;
                }

                var address = new RecordAddress(
                    (uint)streams.UnsignedAt(row)!.Value,
                    (uint)epochs.UnsignedAt(row)!.Value,
                    ordinals.UnsignedAt(row)!.Value,
                    new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
                (long? minor, long? frequency) = carried.GetValueOrDefault(address);
                var announcement = new CooperativeAnnouncement
                {
                    ProcessId = processId,
                    Process = processes.Bind(processId, ticks.SignedAt(row)!.Value, isLifecycleRecord: false),
                    Major = version,
                    Minor = minor is >= 0 and <= int.MaxValue ? (int)minor : null,
                    Library = segment.TextValue(SegmentColumnId.ResourceName, row),
                    TicksPerSecond = frequency,
                };

                // A process tells each session enabling the provider; its equal announcements are one.
                if (seen.Add((announcement.ProcessId, announcement.Process, announcement.Major, announcement.Minor, announcement.Library,
                    announcement.TicksPerSecond)))
                {
                    announced.Add(announcement);
                }
            }
        }

        return announced;
    }

    private static Dictionary<RecordAddress, (long? Minor, long? Frequency)> ReadFields(
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        CancellationToken cancellationToken)
    {
        var carried = new Dictionary<RecordAddress, (long? Minor, long? Frequency)>();
        foreach (SegmentReaderV1 segment in fieldSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Table != SegmentTableId.SourceFieldsV1)
            {
                throw new ArgumentException("A source-field segment must hold source-fields-v1.", nameof(fieldSegments));
            }

            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            bool opened = false;
            SegmentColumnSlice streams = default, epochs = default, ordinals = default, factHigh = default, factLow = default,
                values = default;
            for (int row = 0; row < segment.RowCount; row++)
            {
                var code = (SourceField)(ushort)codes.UnsignedAt(row)!.Value;
                if (code is not (SourceField.SdkSchemaMinor or SourceField.SourceClockFrequency))
                {
                    continue;
                }

                if (!opened)
                {
                    streams = segment.Slice(SegmentColumnId.RawStreamId);
                    epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                    ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                    factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
                    factLow = segment.Slice(SegmentColumnId.FactKeyLow);
                    values = segment.Slice(SegmentColumnId.FieldValue);
                    opened = true;
                }

                var address = new RecordAddress(
                    (uint)streams.UnsignedAt(row)!.Value,
                    (uint)epochs.UnsignedAt(row)!.Value,
                    ordinals.UnsignedAt(row)!.Value,
                    new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
                ref (long? Minor, long? Frequency) fields = ref CollectionsMarshal.GetValueRefOrAddDefault(carried, address, out _);
                if (code == SourceField.SdkSchemaMinor)
                {
                    fields.Minor = values.SignedAt(row);
                }
                else
                {
                    fields.Frequency = values.SignedAt(row);
                }
            }
        }

        return carried;
    }

    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey);
}
