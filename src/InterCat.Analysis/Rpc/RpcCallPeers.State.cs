using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class RpcPeerIndex
{
    /// <summary>How many bytes <see cref="WriteNoneRead"/> writes.</summary>
    internal const long NoneReadBytes = 8 + 8 + 1;

    /// <summary>The bit of a call's stored state that says it is linked through the COM call it carries (minor 2).</summary>
    private const byte ThroughComBit = 0x80;

    /// <summary>How many bytes <see cref="WriteState"/> writes.</summary>
    internal long StateBytes() => 8 + 8 + 1 + (CollectedAlpc || HoldsComCalls ? 5L * states.Length : 0);

    /// <summary>
    /// Writes the other ends of calls whose segments hold no ALPC send or receive and no COM call record, as
    /// <see cref="WriteState"/> writes them: nothing read to link a call by, so no call's own state follows.
    /// </summary>
    internal static void WriteNoneRead(IndexFileWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.I64(0);
        writer.I64(0);
        writer.U8(0);
    }

    /// <summary>
    /// Writes the other ends as `contracts/operation-index-v1.md` §3 holds them: the ALPC records read and whether the
    /// generation holds COM call records, and when it held either, each call's state - marked when the call is linked
    /// through the COM call it carries - and other call, the calls in <paramref name="order"/> - the canonical order the
    /// calls are written in - and the other call by its place in that order.
    /// </summary>
    internal void WriteState(IndexFileWriter writer, int[] order)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(order);
        writer.I64(AlpcSends);
        writer.I64(AlpcReceives);
        writer.U8(HoldsComCalls ? (byte)1 : (byte)0);
        if (!CollectedAlpc && !HoldsComCalls)
        {
            return;
        }

        int[] placeOf = new int[order.Length];
        for (int place = 0; place < order.Length; place++)
        {
            placeOf[order[place]] = place;
        }

        foreach (int call in order)
        {
            writer.U8((byte)((byte)states[call] | (LinkedThroughComAt(call) ? ThroughComBit : 0)));
            writer.I32(peers[call] < 0 ? -1 : placeOf[peers[call]]);
        }
    }

    /// <summary>
    /// Reads the other ends <see cref="WriteState"/> wrote of <paramref name="calls"/>, whose calls read in canonical
    /// order are at <paramref name="placed"/>. What the peer rule could not have given is refused (operation-index-v1 §4):
    /// a client call is served exactly when it holds the server call that holds it, and it was completed when its other
    /// end is anything but unresolved for that; a server call is served or not reached; only a served call is linked
    /// through a COM call, and only in a generation holding COM call records.
    /// </summary>
    internal static RpcPeerIndex ReadState(IndexFileReader reader, RpcCallIndex calls, int[] placed)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(placed);
        (long sends, long receives, bool comCalls) = ReadCounts(reader);
        int count = calls.Count;
        var states = new RpcPeerState[count];
        int[] peers = new int[count];
        Array.Fill(peers, -1);
        if (sends + receives == 0 && !comCalls)
        {
            Array.Fill(states, RpcPeerState.NoAlpcEvidence);
            return new(calls, states, peers, 0, 0);
        }

        bool[]? throughCom = comCalls ? new bool[count] : null;
        for (int place = 0; place < count; place++)
        {
            (RpcPeerState state, int other, bool viaCom) = ReadEnd(reader, place, count, sends + receives > 0, comCalls);
            states[placed[place]] = state;
            peers[placed[place]] = other < 0 ? -1 : placed[other];
            if (viaCom)
            {
                throughCom![placed[place]] = true;
            }
        }

        for (int call = 0; call < count; call++)
        {
            RpcCallIndex.PeerFacts facts = calls.FactsOf(call);
            int other = peers[call];
            // A link is mutual and joins two sides; whether the other call's state says served is its own check's.
            bool linked = other >= 0 && peers[other] == call && calls.FactsOf(other).Side != facts.Side;
            if (!IsEnd(facts.Side, facts.State, states[call], other, linked, sends + receives > 0)
                || (throughCom is not null && linked && throughCom[call] != throughCom[other]))
            {
                throw reader.Invalid("a call's other end contradicts the call it names, or the call's own state.");
            }
        }

        return new(calls, states, peers, sends, receives, comCalls: comCalls, throughCom: throughCom);
    }

    /// <summary>
    /// Checks the other ends <see cref="WriteState"/> wrote as <see cref="ReadState"/> reads them, every refusal the same,
    /// of calls whose sides and states <paramref name="facts"/> holds by their places in the canonical order
    /// (<see cref="RpcCallIndex.VerifyState"/>): what a call's other end is, by place, rather than the calls themselves.
    /// </summary>
    internal static void VerifyState(IndexFileReader reader, byte[] facts)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(facts);
        (long sends, long receives, bool comCalls) = ReadCounts(reader);
        if (sends + receives == 0 && !comCalls)
        {
            return;
        }

        int count = facts.Length;
        var states = new RpcPeerState[count];
        int[] peers = new int[count];
        bool[] throughCom = new bool[count];
        for (int place = 0; place < count; place++)
        {
            (states[place], peers[place], throughCom[place]) = ReadEnd(reader, place, count, sends + receives > 0, comCalls);
        }

        for (int place = 0; place < count; place++)
        {
            RpcCallSide side = RpcCallIndex.SideOf(facts[place]);
            int other = peers[place];
            bool linked = other >= 0 && peers[other] == place && RpcCallIndex.SideOf(facts[other]) != side;
            if (!IsEnd(side, RpcCallIndex.StateOf(facts[place]), states[place], other, linked, sends + receives > 0)
                || (linked && throughCom[place] != throughCom[other]))
            {
                throw reader.Invalid("a call's other end contradicts the call it names, or the call's own state.");
            }
        }
    }

    private static (long Sends, long Receives, bool ComCalls) ReadCounts(IndexFileReader reader)
    {
        long sends = reader.I64();
        long receives = reader.I64();
        if (sends < 0 || receives < 0)
        {
            throw reader.Invalid("it counts fewer than no ALPC records.");
        }

        byte com = reader.U8();
        if (com > 1)
        {
            throw reader.Invalid("it says neither that the generation holds COM call records nor that it holds none.");
        }

        return (sends, receives, com == 1);
    }

    // One call's stored other end: its state, the call it names and whether it is linked through a COM call. A state saying
    // no ALPC record was read is one only a generation without ALPC records gives, and the mark is a served call's alone,
    // in a generation holding COM call records.
    private static (RpcPeerState State, int Other, bool ViaCom) ReadEnd(IndexFileReader reader, int place, int count, bool alpc, bool comCalls)
    {
        byte raw = reader.U8();
        bool viaCom = (raw & ThroughComBit) != 0;
        var state = (RpcPeerState)(raw & ~ThroughComBit);
        int other = reader.I32();
        if (!Enum.IsDefined(state) || (alpc && state == RpcPeerState.NoAlpcEvidence) || other < -1 || other >= count
            || (viaCom && (!comCalls || state != RpcPeerState.Served)))
        {
            throw reader.Invalid($"call {place:N0}'s other end is not one the peer rule gives.");
        }

        return (state, other, viaCom);
    }

    /// <summary>
    /// Whether a call's other end is one the peer rule gives: a client call is served exactly when it holds the server
    /// call that holds it, and - where ALPC records were read - was completed exactly when its other end is anything but
    /// unresolved for that, while without them it is served through a COM call or unresolved for want of ALPC; a server
    /// call is served when linked, and otherwise not reached.
    /// </summary>
    private static bool IsEnd(RpcCallSide side, RpcCallState call, RpcPeerState state, int other, bool linked, bool alpc) =>
        side == RpcCallSide.Client
            ? state != RpcPeerState.NotReached
                && (state == RpcPeerState.Served ? linked && (alpc || call == RpcCallState.Completed) : other < 0)
                && (alpc
                    ? (state == RpcPeerState.NotCompleted) == (call != RpcCallState.Completed)
                    : state is RpcPeerState.NoAlpcEvidence or RpcPeerState.Served)
            : state == RpcPeerState.Served ? linked : state == RpcPeerState.NotReached && other < 0;
}
