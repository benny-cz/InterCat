using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class ProcessInstanceIndex
{
    private const int RecordBytes = 40;
    private const int AddressBytes = 32;

    /// <summary>The published files this derivation read, or null when one of them had no published identity.</summary>
    internal IReadOnlyCollection<StoreDependency>? FilesRead => evidence.Files;

    /// <summary>The capture and normalizer derivation of every record read; both null before any segment was read.</summary>
    internal (CaptureId? Capture, NormalizerContractVersion? Derivation) Provenance => (evidence.Capture, evidence.Derivation);

    /// <summary>
    /// Writes what this derivation read, which is everything its instances are built from, in canonical order
    /// (`contracts/derivation-checkpoint-v1.md` §3, <c>instances</c>).
    /// </summary>
    internal void WriteState(IndexFileWriter writer) => evidence.Write(writer);

    /// <summary>
    /// Rebuilds a derivation from what a checkpoint says it read. The instances are built from that evidence exactly as
    /// they are from the segments themselves, so they are the instances a derivation of the covered segments gives.
    /// </summary>
    internal static ProcessInstanceIndex ReadState(
        IndexFileReader reader,
        SourceClockDescriptor clock,
        CaptureId? capture,
        NormalizerContractVersion? derivation,
        IReadOnlyCollection<StoreDependency> files) =>
        Build(Evidence.Read(reader, clock.Id, capture, derivation, files), clock);

    private static int CompareAddresses(RecordAddress left, RecordAddress right)
    {
        int order = left.Stream.CompareTo(right.Stream);
        order = order != 0 ? order : left.Epoch.CompareTo(right.Epoch);
        order = order != 0 ? order : left.Ordinal.CompareTo(right.Ordinal);
        order = order != 0 ? order : left.FactKey.High.CompareTo(right.FactKey.High);
        return order != 0 ? order : left.FactKey.Low.CompareTo(right.FactKey.Low);
    }

    private static int CompareLifecycle(LifecycleRow left, LifecycleRow right)
    {
        int order = left.ProcessId.CompareTo(right.ProcessId);
        return order != 0 ? order : RecordKey.Compare(left.Record, right.Record);
    }

    private static int CompareSeen((RecordAddress Address, SourceField Field) left, (RecordAddress Address, SourceField Field) right)
    {
        int order = CompareAddresses(left.Address, right.Address);
        return order != 0 ? order : left.Field.CompareTo(right.Field);
    }

    private static void WriteAddress(IndexFileWriter writer, RecordAddress address)
    {
        writer.U32(address.Stream);
        writer.U32(address.Epoch);
        writer.U64(address.Ordinal);
        writer.U64(address.FactKey.High);
        writer.U64(address.FactKey.Low);
    }

    private static RecordAddress ReadAddress(IndexFileReader reader) =>
        new(reader.U32(), reader.U32(), reader.U64(), new FactKey(reader.U64(), reader.U64()));

    private sealed partial class Evidence
    {
        private const byte HasExitCode = 1;
        private const byte HasName = 2;

        public IReadOnlyCollection<StoreDependency>? Files => anonymous ? null : read;

        public CaptureId? Capture => captures.Count == 1 ? captures.Single() : null;

        public NormalizerContractVersion? Derivation => derivations.Count == 1 ? derivations.Single() : null;

        public void Write(IndexFileWriter writer)
        {
            writer.I64(WithoutOwner);
            LifecycleRow[] lifecycle = [.. Lifecycle];
            Array.Sort(lifecycle, CompareLifecycle);
            writer.Count(lifecycle.Length);
            foreach (LifecycleRow row in lifecycle)
            {
                writer.I32(row.ProcessId);
                writer.U8((byte)row.Kind);
                WriteRecord(writer, row.Record);
                writer.U8((byte)((row.ExitCode is null ? 0 : HasExitCode) | (row.Name is null ? 0 : HasName)));
                if (row.ExitCode is { } exitCode)
                {
                    writer.I64(exitCode);
                }

                if (row.Name is { } name)
                {
                    writer.Str32(name);
                }
            }

            KeyValuePair<RecordAddress, ProcessFields>[] fields = [.. Fields];
            Array.Sort(fields, static (left, right) => CompareAddresses(left.Key, right.Key));
            writer.Count(fields.Length);
            foreach ((RecordAddress address, ProcessFields values) in fields)
            {
                WriteAddress(writer, address);
                writer.U8((byte)((values.Sequence is null ? 0 : 1)
                    | (values.CreateFileTime is null ? 0 : 2)
                    | (values.ParentProcessId is null ? 0 : 4)
                    | (values.ParentSequence is null ? 0 : 8)
                    | (values.SessionId is null ? 0 : 16)
                    | (values.ExitFileTime is null ? 0 : 32)));
                if (values.Sequence is { } sequence)
                {
                    writer.U64(sequence);
                }

                if (values.CreateFileTime is { } created)
                {
                    writer.I64(created);
                }

                if (values.ParentProcessId is { } parent)
                {
                    writer.I32(parent);
                }

                if (values.ParentSequence is { } parentSequence)
                {
                    writer.U64(parentSequence);
                }

                if (values.SessionId is { } session)
                {
                    writer.U32(session);
                }

                if (values.ExitFileTime is { } exited)
                {
                    writer.I64(exited);
                }
            }

            (RecordAddress Address, SourceField Field)[] seen = [.. SeenFields];
            Array.Sort(seen, CompareSeen);
            writer.Count(seen.Length);
            foreach ((RecordAddress address, SourceField field) in seen)
            {
                WriteAddress(writer, address);
                writer.U16((ushort)field);
            }

            KeyValuePair<int, RecordKey>[] first = [.. FirstActivity];
            Array.Sort(first, static (left, right) => left.Key.CompareTo(right.Key));
            writer.Count(first.Length);
            foreach ((int processId, RecordKey record) in first)
            {
                writer.I32(processId);
                WriteRecord(writer, record);
            }
        }

        public static Evidence Read(
            IndexFileReader reader,
            ClockId clock,
            CaptureId? capture,
            NormalizerContractVersion? derivation,
            IReadOnlyCollection<StoreDependency> files)
        {
            long withoutOwner = reader.I64();
            if (withoutOwner < 0)
            {
                throw reader.Invalid("it counts a negative number of lifecycle records without an owner.");
            }

            int count = reader.Count(4 + 1 + RecordBytes + 1);
            var lifecycle = new List<LifecycleRow>(count);
            for (int index = 0; index < count; index++)
            {
                int processId = reader.I32();
                var kind = (ObservationKind)reader.U8();
                if (kind is not (ObservationKind.Create or ObservationKind.Exit or ObservationKind.Inventory))
                {
                    throw reader.Invalid($"a lifecycle record has kind {(int)kind}, which is not a lifecycle kind.");
                }

                RecordKey record = ReadRecord(reader, capture, derivation);
                byte flags = reader.U8();
                if ((flags & ~(HasExitCode | HasName)) != 0)
                {
                    throw reader.Invalid($"a lifecycle record has flags {flags}.");
                }

                long? exitCode = (flags & HasExitCode) != 0 ? reader.I64() : null;
                string? name = (flags & HasName) != 0 ? reader.Str32() : null;
                var row = new LifecycleRow(processId, kind, record, exitCode, name);
                if (lifecycle.Count > 0 && CompareLifecycle(lifecycle[^1], row) >= 0)
                {
                    throw reader.Invalid("its lifecycle records are not in canonical order.");
                }

                lifecycle.Add(row);
            }

            count = reader.Count(AddressBytes + 1);
            var fields = new Dictionary<RecordAddress, ProcessFields>(count);
            RecordAddress? previous = null;
            for (int index = 0; index < count; index++)
            {
                RecordAddress address = ReadAddress(reader);
                if (previous is { } before && CompareAddresses(before, address) >= 0)
                {
                    throw reader.Invalid("its process fields are not in canonical order.");
                }

                byte present = reader.U8();
                if (present is 0 or > 63)
                {
                    throw reader.Invalid($"a record's process fields are marked {present}.");
                }

                fields.Add(address, new ProcessFields(
                    (present & 1) != 0 ? reader.U64() : null,
                    (present & 2) != 0 ? reader.I64() : null,
                    (present & 4) != 0 ? reader.I32() : null,
                    (present & 8) != 0 ? reader.U64() : null,
                    (present & 16) != 0 ? reader.U32() : null,
                    (present & 32) != 0 ? reader.I64() : null));
                previous = address;
            }

            count = reader.Count(AddressBytes + 2);
            var seen = new HashSet<(RecordAddress Address, SourceField Field)>(count);
            (RecordAddress Address, SourceField Field)? last = null;
            for (int index = 0; index < count; index++)
            {
                (RecordAddress Address, SourceField Field) entry = (ReadAddress(reader), (SourceField)reader.U16());
                if (entry.Field is not (SourceField.ProcessStartSequence or SourceField.ProcessCreateTime
                    or SourceField.ParentProcessId or SourceField.ParentStartSequence
                    or SourceField.ProcessSessionId or SourceField.ProcessExitTime))
                {
                    throw reader.Invalid($"a record carried source field {(int)entry.Field}, which is not a process field.");
                }

                if (last is { } before && CompareSeen(before, entry) >= 0)
                {
                    throw reader.Invalid("the process fields records carried are not in canonical order.");
                }

                _ = seen.Add(entry);
                last = entry;
            }

            count = reader.Count(4 + RecordBytes);
            var first = new Dictionary<int, RecordKey>(count);
            int? previousProcess = null;
            for (int index = 0; index < count; index++)
            {
                int processId = reader.I32();
                if (previousProcess is { } before && before >= processId)
                {
                    throw reader.Invalid("the earliest records of each PID are not in PID order.");
                }

                first.Add(processId, ReadRecord(reader, capture, derivation));
                previousProcess = processId;
            }

            var evidence = new Evidence(clock)
            {
                Lifecycle = lifecycle,
                Fields = fields,
                SeenFields = seen,
                FirstActivity = first,
                WithoutOwner = withoutOwner,
            };
            evidence.read.UnionWith(files);
            if (capture is { } captured)
            {
                _ = evidence.captures.Add(captured);
            }

            if (derivation is { } derived)
            {
                _ = evidence.derivations.Add(derived);
            }

            return evidence;
        }

        /// <summary>A record's canonical position; the capture and derivation it shares with every record are stated once.</summary>
        private void WriteRecord(IndexFileWriter writer, RecordKey record)
        {
            if (record.Capture != Capture || record.Derivation != Derivation)
            {
                throw new InvalidOperationException(
                    "A record read belongs to another capture or normalizer derivation than the segments it was read with.");
            }

            writer.I64(record.NativeTicks);
            WriteAddress(writer, record.Address);
        }

        private static RecordKey ReadRecord(IndexFileReader reader, CaptureId? capture, NormalizerContractVersion? derivation)
        {
            if (capture is not { } captured || derivation is not { } derived)
            {
                throw reader.Invalid("it holds records but names no capture or normalizer derivation for them.");
            }

            long ticks = reader.I64();
            RecordAddress address = ReadAddress(reader);
            return new(ticks, address.Stream, address.Epoch, address.Ordinal, address.FactKey, captured, derived);
        }
    }
}
