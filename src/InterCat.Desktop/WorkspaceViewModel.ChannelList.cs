using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A channel's rung across live publications (§6.4, R7): the calls an RPC channel's rung, or the exchanges a process's HTTP
/// exchanges' rung, listed in one publication stay listed in the next until it has read its own, rather than the list
/// going empty every couple of seconds, and the call or exchange chosen among them stays chosen.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>
    /// The calls or exchanges this rung lists for the scope its rows count - its own pages read, or an earlier
    /// publication's it still lists - for the next publication to list until it has read its own; none at any other rung,
    /// or while the rows listed are another scope's.
    /// </summary>
    private ChannelListCarry? CarryChannelList()
    {
        if (evidenceSource is not { } source)
        {
            return null;
        }

        TimeRange? scope = CountedScope;
        if (IsRpcChannelRung && rpcCalls is { Calls.Count: > 0 } calls && calls.Scope == scope
            && (!calls.ReplaceOnFirstPage || calls.Adopted))
        {
            return new(source.SessionId, calls.ChannelKey, scope, calls.Channel, [.. calls.Calls], null, []);
        }

        if (IsHttpChannelRung && httpExchanges is { Exchanges.Count: > 0 } exchanges && exchanges.Scope == scope
            && (!exchanges.ReplaceOnFirstPage || exchanges.Adopted))
        {
            return new(source.SessionId, exchanges.ChannelKey, scope, null, [], exchanges.Channel, [.. exchanges.Exchanges]);
        }

        return null;
    }

    /// <summary>
    /// Lists what an earlier publication of this session listed on the same channel's rung over the same scope while this
    /// generation reads its own first page, which replaces them; a read that fails lets them go with the failure said.
    /// </summary>
    private void AdoptChannelList(ChannelListCarry? carry, Guid session)
    {
        if (carry is not { } earlier || earlier.SessionId != session)
        {
            return;
        }

        if (rpcCalls is { Loading: true, Calls.Count: 0 } calls && calls.ChannelKey == earlier.ChannelKey
            && calls.Scope == earlier.Scope && earlier.Calls.Count > 0)
        {
            calls.Channel ??= earlier.RpcChannel;
            calls.Calls.AddRange(earlier.Calls);
            calls.ReplaceOnFirstPage = true;
            calls.Adopted = true;
            RebuildRpcRows();
        }

        if (httpExchanges is { Loading: true, Exchanges.Count: 0 } exchanges && exchanges.ChannelKey == earlier.ChannelKey
            && exchanges.Scope == earlier.Scope && earlier.Exchanges.Count > 0)
        {
            exchanges.Channel ??= earlier.HttpChannel;
            exchanges.Exchanges.AddRange(earlier.Exchanges);
            exchanges.ReplaceOnFirstPage = true;
            exchanges.Adopted = true;
            RebuildHttpRows();
        }
    }
}
