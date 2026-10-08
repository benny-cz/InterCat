using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class RpcPeerIndex
{
    /// <summary>How many bytes <see cref="WriteState"/> writes.</summary>
    internal long StateBytes() => 8 + 8 + (CollectedAlpc ? 5L * states.Length : 0);

    /// <summary>
    /// Writes the other ends as `contracts/operation-index-v1.md` §3 holds them: the ALPC records read, and when there
    /// were any, each call's state and other call, the calls in <paramref name="order"/> - the canonical order the calls
    /// are written in - and the other call by its place in that order.
    /// </summary>
    internal void WriteState(IndexFileWriter writer, int[] order)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(order);
        writer.I64(AlpcSends);
        writer.I64(AlpcReceives);
        if (!CollectedAlpc)
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
            writer.U8((byte)states[call]);
            writer.I32(peers[call] < 0 ? -1 : placeOf[peers[call]]);
        }
    }

    /// <summary>
    /// Reads the other ends <see cref="WriteState"/> wrote of <paramref name="calls"/>, whose calls read in canonical
    /// order are at <paramref name="placed"/>. What the peer rule could not have given is refused (operation-index-v1 §4):
    /// a client call is served exactly when it holds the server call that holds it, and it was completed when its other
    /// end is anything but unresolved for that; a server call is served or not reached.
    /// </summary>
    internal static RpcPeerIndex ReadState(IndexFileReader reader, RpcCallIndex calls, int[] placed)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(placed);
        long sends = reader.I64();
        long receives = reader.I64();
        if (sends < 0 || receives < 0)
        {
            throw reader.Invalid("it counts fewer than no ALPC records.");
        }

        int count = calls.Count;
        var states = new RpcPeerState[count];
        int[] peers = new int[count];
        Array.Fill(peers, -1);
        if (sends + receives == 0)
        {
            Array.Fill(states, RpcPeerState.NoAlpcEvidence);
            return new(calls, states, peers, 0, 0);
        }

        for (int place = 0; place < count; place++)
        {
            var state = (RpcPeerState)reader.U8();
            int other = reader.I32();
            if (!Enum.IsDefined(state) || state == RpcPeerState.NoAlpcEvidence || other < -1 || other >= count)
            {
                throw reader.Invalid($"call {place:N0}'s other end is not one the peer rule gives.");
            }

            states[placed[place]] = state;
            peers[placed[place]] = other < 0 ? -1 : placed[other];
        }

        for (int call = 0; call < count; call++)
        {
            RpcCallIndex.PeerFacts facts = calls.FactsOf(call);
            int other = peers[call];
            // A link is mutual and joins two sides; whether the other call's state says served is its own check's.
            bool linked = other >= 0 && peers[other] == call && calls.FactsOf(other).Side != facts.Side;
            bool valid = facts.Side == RpcCallSide.Client
                ? states[call] != RpcPeerState.NotReached
                    && (states[call] == RpcPeerState.Served ? linked : other < 0)
                    && (states[call] == RpcPeerState.NotCompleted) == (facts.State != RpcCallState.Completed)
                : states[call] == RpcPeerState.Served ? linked : states[call] == RpcPeerState.NotReached && other < 0;
            if (!valid)
            {
                throw reader.Invalid("a call's other end contradicts the call it names, or the call's own state.");
            }
        }

        return new(calls, states, peers, sends, receives);
    }
}
