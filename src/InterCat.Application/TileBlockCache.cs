using System.Collections.Concurrent;

namespace InterCat.Application;

/// <summary>The parts of a tile index block a query reads, each kept apart (<see cref="TileBlockCache"/>).</summary>
internal enum TilePart
{
    /// <summary>The block's tiles, decoded and checked (<see cref="TileBlock"/>).</summary>
    Tiles,

    /// <summary>Its tiles' owner tallies, as read and checked.</summary>
    OwnerTallies,

    /// <summary>Its tiles' channel tallies, as read and checked.</summary>
    ChannelTallies,

    /// <summary>Its tiles' byte sums, as read and checked.</summary>
    Sums,

    /// <summary>Its tiles' byte tallies, as read and checked.</summary>
    ByteTallies,

    /// <summary>Its finest tiles' records, each tile's followed by its checksum, which every read of it checks.</summary>
    Records,
}

/// <summary>
/// What queries read of tile index blocks, kept for later queries up to <see cref="Capacity"/> bytes across every tile
/// index file that keeps its blocks here: a block's tiles decoded, and its tallies, byte sums and records as read, each
/// charged the bytes it holds. A generation's tile index keeps its own, and the tiles of every chunk a live session's
/// generations name share one, so what they keep is bounded whatever the session's size and however many chunks it names
/// (§12.1 S2, §19.3). Past the budget what a query reads takes the place of what was kept at random, never of all of it: a
/// query that reads more than fits still finds much of it again, where clearing it all, or giving up what was read least
/// recently, would find none of it each time a query read it in the same order again.
/// </summary>
internal sealed class TileBlockCache
{
    /// <summary>What keeping a part costs beyond its bytes: its key, its place among those kept and an array's header.</summary>
    internal const int EntryBytes = 192;

    // Read without a lock, as every query reads it for each block it walks; written under the gate alone.
    private readonly ConcurrentDictionary<(TileSection Section, TilePart Part, int Level, int Block), (object Value, long Charge)>
        kept = new();

    // What is kept, by place: the choice of a part to give up, made under the gate.
    private readonly List<(TileSection Section, TilePart Part, int Level, int Block)> keys = [];
    private readonly Lock gate = new();
    private long charged;

    // The last place given up, as xorshift32 steps from it: cheap, and the same choices on every run.
    private uint chosen = 0x2545F491;

    public TileBlockCache(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>The most bytes it keeps.</summary>
    public long Capacity { get; }

    /// <summary>The bytes what it keeps is charged.</summary>
    public long Charged
    {
        get
        {
            lock (gate)
            {
                return charged;
            }
        }
    }

    /// <summary>How many parts it keeps.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return keys.Count;
            }
        }
    }

    /// <summary>
    /// The tiles kept of block <paramref name="block"/> of level <paramref name="level"/> of <paramref name="section"/>, if
    /// they are.
    /// </summary>
    public TileBlock? Find(TileSection section, int level, int block) =>
        (TileBlock?)Find((section, TilePart.Tiles, level, block));

    /// <summary>
    /// The bytes kept of <paramref name="part"/> of block <paramref name="block"/> of level <paramref name="level"/> of
    /// <paramref name="section"/>, if they are.
    /// </summary>
    public byte[]? Find(TileSection section, TilePart part, int level, int block)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(part, TilePart.Tiles);
        return (byte[]?)Find((section, part, level, block));
    }

    /// <summary>
    /// Keeps <paramref name="decoded"/> as the tiles of block <paramref name="block"/> of level <paramref name="level"/> of
    /// <paramref name="section"/>, charged <see cref="TileBlock.Bytes"/>. Returns the tiles kept: another query's where it
    /// kept the same block first.
    /// </summary>
    public TileBlock Keep(TileSection section, int level, int block, TileBlock decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        return (TileBlock)Keep((section, TilePart.Tiles, level, block), decoded, decoded.Bytes);
    }

    /// <summary>
    /// Keeps <paramref name="bytes"/> as <paramref name="part"/> of block <paramref name="block"/> of level
    /// <paramref name="level"/> of <paramref name="section"/>, charged their length. Returns the bytes kept: another query's
    /// where it kept the same part first.
    /// </summary>
    public byte[] Keep(TileSection section, TilePart part, int level, int block, byte[] bytes)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(part, TilePart.Tiles);
        ArgumentNullException.ThrowIfNull(bytes);
        return (byte[])Keep((section, part, level, block), bytes, bytes.Length);
    }

    private object? Find((TileSection Section, TilePart Part, int Level, int Block) key)
    {
        ArgumentNullException.ThrowIfNull(key.Section);
        return kept.TryGetValue(key, out (object Value, long Charge) found) ? found.Value : null;
    }

    /// <summary>
    /// Keeps <paramref name="value"/> under <paramref name="key"/>, giving up parts chosen at random until it fits; one
    /// that could never fit is returned unkept, for the query that read it alone.
    /// </summary>
    private object Keep((TileSection Section, TilePart Part, int Level, int Block) key, object value, long bytes)
    {
        ArgumentNullException.ThrowIfNull(key.Section);
        long charge = bytes + EntryBytes;
        lock (gate)
        {
            if (kept.TryGetValue(key, out (object Value, long Charge) known))
            {
                return known.Value;
            }

            if (charge > Capacity)
            {
                return value;
            }

            while (charged + charge > Capacity)
            {
                GiveUpOne();
            }

            kept[key] = (value, charge);
            keys.Add(key);
            charged += charge;
            return value;
        }
    }

    /// <summary>Gives up one part chosen at random, moving the last kept into its place.</summary>
    private void GiveUpOne()
    {
        chosen ^= chosen << 13;
        chosen ^= chosen >> 17;
        chosen ^= chosen << 5;
        int place = (int)(chosen % (uint)keys.Count);
        _ = kept.TryRemove(keys[place], out (object Value, long Charge) given);
        charged -= given.Charge;
        keys[place] = keys[^1];
        keys.RemoveAt(keys.Count - 1);
    }
}
