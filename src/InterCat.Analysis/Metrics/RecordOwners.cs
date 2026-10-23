using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// The bytes a set of records measured in the transport-observed domain, by accounting side: sent, received and any other
/// side's, each with how many of the records stated a size and how many stated none - what a byte view measures of the
/// same records (<see cref="ByteDomainRows"/>, `metrics-v1` §5). A sum past what one holds is refused, as the view refuses
/// it: <see cref="Refused"/>, and the view then reads the rows, which say why.
/// </summary>
public sealed class TransportByteSums
{
    public long SentBytes { get; private set; }

    public long SentMeasured { get; private set; }

    public long SentUnmeasured { get; private set; }

    public long ReceivedBytes { get; private set; }

    public long ReceivedMeasured { get; private set; }

    public long ReceivedUnmeasured { get; private set; }

    public long OtherBytes { get; private set; }

    public long OtherMeasured { get; private set; }

    public long OtherUnmeasured { get; private set; }

    /// <summary>Whether a side's bytes passed what a sum holds, so the sums stand for nothing and are not given.</summary>
    public bool Refused { get; private set; }

    /// <summary>Whether no record measured anything here.</summary>
    public bool IsEmpty => SentMeasured + SentUnmeasured + ReceivedMeasured + ReceivedUnmeasured + OtherMeasured + OtherUnmeasured == 0;

    /// <summary>Adds one record's measurement at its side: the bytes it measured, or null for a size it did not record.</summary>
    internal void Add(AccountingSide side, long? value)
    {
        switch (side)
        {
            case AccountingSide.SendSide:
                if (value is { } sent) { SentBytes = Sum(SentBytes, sent); SentMeasured++; } else { SentUnmeasured++; }
                break;
            case AccountingSide.ReceiveSide:
                if (value is { } received) { ReceivedBytes = Sum(ReceivedBytes, received); ReceivedMeasured++; } else { ReceivedUnmeasured++; }
                break;
            default:
                if (value is { } other) { OtherBytes = Sum(OtherBytes, other); OtherMeasured++; } else { OtherUnmeasured++; }
                break;
        }
    }

    /// <summary>Adds what <paramref name="other"/> holds, refused if it was or if a sum passes what one holds.</summary>
    internal void Add(TransportByteSums other)
    {
        SentBytes = Sum(SentBytes, other.SentBytes);
        SentMeasured += other.SentMeasured;
        SentUnmeasured += other.SentUnmeasured;
        ReceivedBytes = Sum(ReceivedBytes, other.ReceivedBytes);
        ReceivedMeasured += other.ReceivedMeasured;
        ReceivedUnmeasured += other.ReceivedUnmeasured;
        OtherBytes = Sum(OtherBytes, other.OtherBytes);
        OtherMeasured += other.OtherMeasured;
        OtherUnmeasured += other.OtherUnmeasured;
        Refused |= other.Refused;
    }

    internal TransportByteSums Copy()
    {
        var copy = new TransportByteSums();
        copy.Add(this);
        return copy;
    }

    /// <summary>
    /// Writes the sums (`contracts/derivation-checkpoint-v1.md` §3, <c>bytes</c>, format 1.5): whether a sum was refused,
    /// then each side's bytes, measured and unmeasured contributions. A refused sum's bytes are written as none, since
    /// where it stopped depends on the order the records were read in.
    /// </summary>
    internal void Write(IndexFileWriter writer)
    {
        writer.Flag(Refused);
        foreach ((long bytes, long measured, long unmeasured) in Sides())
        {
            writer.I64(Refused ? 0 : bytes);
            writer.I64(measured);
            writer.I64(unmeasured);
        }
    }

    /// <summary>Reads what <see cref="Write"/> wrote, refusing counts below none or bytes beside a refused sum.</summary>
    internal static TransportByteSums Read(IndexFileReader reader)
    {
        bool refused = reader.Flag();
        long[] values = [.. Enumerable.Range(0, 9).Select(_ => reader.I64())];
        if (values.Any(value => value < 0) || (refused && (values[0] != 0 || values[3] != 0 || values[6] != 0)))
        {
            throw reader.Invalid("a sum of transport bytes is below none, or states bytes beside a refusal.");
        }

        return new()
        {
            SentBytes = values[0], SentMeasured = values[1], SentUnmeasured = values[2],
            ReceivedBytes = values[3], ReceivedMeasured = values[4], ReceivedUnmeasured = values[5],
            OtherBytes = values[6], OtherMeasured = values[7], OtherUnmeasured = values[8],
            Refused = refused,
        };
    }

    private IEnumerable<(long Bytes, long Measured, long Unmeasured)> Sides()
    {
        yield return (SentBytes, SentMeasured, SentUnmeasured);
        yield return (ReceivedBytes, ReceivedMeasured, ReceivedUnmeasured);
        yield return (OtherBytes, OtherMeasured, OtherUnmeasured);
    }

    /// <summary>Adds bytes to a sum as a byte view adds them; past what a sum holds the sums are refused, as the view refuses them.</summary>
    private long Sum(long total, long bytes)
    {
        long sum = unchecked(total + bytes);
        if (((total ^ sum) & (bytes ^ sum)) < 0)
        {
            Refused = true;
            return total;
        }

        return sum;
    }
}

/// <summary>
/// How many records bind to one owner at one strength - or to none, at <see cref="Instance"/> -1 - and the transport bytes
/// they measured: what a pass binding each of the same rows by its owner counts (`relations-v1` §5, revision 737).
/// </summary>
/// <param name="Instance">The instance's position in the process index the counts were made under, or -1 for none.</param>
/// <param name="Strength">How strongly they bind to it; meaningless for none.</param>
/// <param name="Records">How many records, but a process's or a thread's lifecycle records, bind so.</param>
/// <param name="Bytes">The transport bytes the records binding so measured, lifecycle records among them.</param>
public sealed record RecordOwners(int Instance, RelationStrength Strength, long Records, TransportByteSums Bytes);

/// <summary>One record as an owner tally takes it: whether it is counted among the records, and what bytes it measured.</summary>
internal readonly record struct OwnedRecord(bool Counted, ByteDomainContribution Contribution, AccountingSide Side, long? Value)
{
    /// <summary>Reads a row's part: counted unless a lifecycle record, and its transport-observed bytes.</summary>
    public static OwnedRecord Of(in ByteDomainRows bytes, int row, Mechanism mechanism)
    {
        ByteDomainContribution contribution = bytes.At(row, out AccountingSide side, out long? value);
        return new(Counts(mechanism), contribution, side, value);
    }

    /// <summary>Whether a record of <paramref name="mechanism"/> is counted among the records: unless a process's or a thread's lifecycle record.</summary>
    public static bool Counts(Mechanism mechanism) => mechanism is not (Mechanism.ProcessLifecycle or Mechanism.ThreadLifecycle);
}

/// <summary>
/// Records counted by the owner each binds to and how strongly, with the bytes they measured, and those binding to none
/// apart: the tally an incarnation and a process activity keep alike.
/// </summary>
internal sealed class OwnerTally
{
    private readonly List<(int Instance, RelationStrength Strength, long Records, TransportByteSums Bytes)> owners = [];
    private long unownedRecords;
    private TransportByteSums? unownedBytes;

    /// <summary>Whether a record of the tally was refused by a byte view: no side, an undefined one, no unit or another unit.</summary>
    public bool BytesRefused { get; private set; }

    /// <summary>How many records it counts, those binding to none among them.</summary>
    public long Records => owners.Sum(owner => owner.Records) + unownedRecords;

    /// <summary>Whether it counts a record binding to an instance.</summary>
    public bool HasOwners => owners.Count > 0;

    /// <summary>Whether every instance it counts records of is <paramref name="instance"/>.</summary>
    public bool OwnedOnlyBy(int instance) => owners.All(owner => owner.Instance == instance);

    /// <summary>How many records it counts binding to an instance at <paramref name="strength"/>.</summary>
    public long RecordsAt(RelationStrength strength) => owners.Where(owner => owner.Strength == strength).Sum(owner => owner.Records);

    /// <summary>
    /// Whether every record it counts, and every byte, binds to an instance at <paramref name="first"/> or
    /// <paramref name="second"/>: none binds to no instance or at another strength.
    /// </summary>
    public bool BindsOnlyAt(RelationStrength first, RelationStrength second) =>
        unownedRecords == 0 && unownedBytes is null && owners.All(owner => owner.Strength == first || owner.Strength == second);

    public void Add(ProcessBinding binding, OwnedRecord record)
    {
        TransportByteSums? bytes = null;
        if (record.Contribution == ByteDomainContribution.Refused)
        {
            BytesRefused = true;
        }

        if (binding.IsBound)
        {
            int at = owners.FindIndex(owner => owner.Instance == binding.Instance && owner.Strength == binding.Strength);
            if (at < 0)
            {
                owners.Add((binding.Instance, binding.Strength, 0, new()));
                at = owners.Count - 1;
            }

            (int instance, RelationStrength strength, long records, TransportByteSums sums) = owners[at];
            owners[at] = (instance, strength, record.Counted ? records + 1 : records, sums);
            bytes = sums;
        }
        else
        {
            if (record.Counted)
            {
                unownedRecords++;
            }

            if (record.Contribution == ByteDomainContribution.Measured)
            {
                bytes = unownedBytes ??= new();
            }
        }

        if (record.Contribution == ByteDomainContribution.Measured)
        {
            bytes!.Add(record.Side, record.Value);
        }
    }

    /// <summary>A copy sharing nothing, its owners at the positions <paramref name="mapped"/> gives them; null where one has none.</summary>
    public OwnerTally? Copy(IReadOnlyDictionary<int, int>? mapped)
    {
        var copy = new OwnerTally
        {
            unownedRecords = unownedRecords,
            unownedBytes = unownedBytes?.Copy(),
            BytesRefused = BytesRefused,
        };
        foreach ((int instance, RelationStrength strength, long records, TransportByteSums bytes) in owners)
        {
            int position = instance;
            if (mapped is not null && !mapped.TryGetValue(instance, out position))
            {
                return null;
            }

            copy.owners.Add((position, strength, records, bytes.Copy()));
        }

        return copy;
    }

    /// <summary>
    /// Writes the tally in canonical order (`contracts/derivation-checkpoint-v1.md` §3, <c>tally</c>, format 1.5): whether a
    /// byte view refused a record of it, each owner by position and strength with its records and bytes, then the records
    /// binding to none and their bytes, if any measured.
    /// </summary>
    public void Write(IndexFileWriter writer)
    {
        writer.Flag(BytesRefused);
        (int Instance, RelationStrength Strength, long Records, TransportByteSums Bytes)[] ordered =
            [.. owners.OrderBy(owner => owner.Instance).ThenBy(owner => owner.Strength)];
        writer.Count(ordered.Length);
        foreach ((int instance, RelationStrength strength, long records, TransportByteSums bytes) in ordered)
        {
            writer.I32(instance);
            writer.U8((byte)strength);
            writer.I64(records);
            bytes.Write(writer);
        }

        writer.I64(unownedRecords);
        writer.Flag(unownedBytes is not null);
        unownedBytes?.Write(writer);
    }

    /// <summary>
    /// Reads what <see cref="Write"/> wrote over <paramref name="instances"/> instances, refusing an owner that names no
    /// instance or an undefined strength, records below none, and owners out of order or held twice.
    /// </summary>
    public static OwnerTally Read(IndexFileReader reader, int instances)
    {
        var tally = new OwnerTally { BytesRefused = reader.Flag() };
        int count = reader.Count(4 + 1 + 8 + 1 + (9 * 8));
        (int Instance, int Strength) last = (-1, -1);
        for (int index = 0; index < count; index++)
        {
            int instance = reader.I32();
            var strength = (RelationStrength)reader.U8();
            long records = reader.I64();
            TransportByteSums bytes = TransportByteSums.Read(reader);
            if (instance < 0 || instance >= instances || !Enum.IsDefined(strength) || records < 0
                || (instance, (int)strength).CompareTo(last) <= 0)
            {
                throw reader.Invalid("an owner of the records counted names no instance or an undefined strength, counts "
                    + "fewer records than none, or is out of order.");
            }

            tally.owners.Add((instance, strength, records, bytes));
            last = (instance, (int)strength);
        }

        tally.unownedRecords = reader.I64();
        if (tally.unownedRecords < 0)
        {
            throw reader.Invalid("it counts fewer records binding to no instance than none.");
        }

        tally.unownedBytes = reader.Flag() ? TransportByteSums.Read(reader) : null;
        return tally;
    }

    /// <summary>What the tally counted: each owner at each strength, then the records binding to none, where there are any.</summary>
    public IReadOnlyList<RecordOwners> Snapshot()
    {
        var snapshot = new List<RecordOwners>(owners.Count + 1);
        foreach ((int instance, RelationStrength strength, long records, TransportByteSums bytes) in owners)
        {
            snapshot.Add(new(instance, strength, records, bytes));
        }

        if (unownedRecords > 0 || unownedBytes is not null)
        {
            snapshot.Add(new(-1, default, unownedRecords, unownedBytes ?? new()));
        }

        return snapshot;
    }
}
