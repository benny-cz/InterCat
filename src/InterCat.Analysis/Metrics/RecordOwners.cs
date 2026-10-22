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
        return new(mechanism is not (Mechanism.ProcessLifecycle or Mechanism.ThreadLifecycle), contribution, side, value);
    }
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
