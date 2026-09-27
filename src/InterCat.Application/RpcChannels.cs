using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// The documented names of well-known RPC interfaces, from the Windows protocol specifications that define them. A name
/// is shown beside its UUID, never instead of it: an interface is identified by its UUID alone (R22).
/// </summary>
public static class RpcInterfaceNames
{
    private static readonly Dictionary<Guid, (string Name, string Description)> Known = new()
    {
        [Guid.Parse("367abb81-9844-35f1-ad32-98f038001003")] = ("svcctl", "Service Control Manager"),
        [Guid.Parse("12345778-1234-abcd-ef00-0123456789ab")] = ("lsarpc", "Local Security Authority"),
        [Guid.Parse("12345778-1234-abcd-ef00-0123456789ac")] = ("samr", "Security Account Manager"),
        [Guid.Parse("e1af8308-5d1f-11c9-91a4-08002b14a0fa")] = ("epmapper", "RPC Endpoint Mapper"),
        [Guid.Parse("4b324fc8-1670-01d3-1278-5a47bf6ee188")] = ("srvsvc", "Server Service"),
        [Guid.Parse("6bffd098-a112-3610-9833-46c3f87e345a")] = ("wkssvc", "Workstation Service"),
        [Guid.Parse("12345678-1234-abcd-ef00-0123456789ab")] = ("spoolss", "Print Spooler"),
        [Guid.Parse("338cd001-2244-31f1-aaaa-900038001003")] = ("winreg", "Remote Registry"),
        [Guid.Parse("12345678-1234-abcd-ef00-01234567cffb")] = ("netlogon", "Netlogon"),
        [Guid.Parse("e3514235-4b06-11d1-ab04-00c04fc2dcd2")] = ("drsuapi", "Directory Replication Service"),
        [Guid.Parse("82273fdc-e32a-18c3-3f78-827929dc23ea")] = ("eventlog", "Event Log"),
        [Guid.Parse("f6beaff7-1e19-4fbb-9f8f-b89e2018337c")] = ("even6", "Event Log"),
        [Guid.Parse("c681d488-d850-11d0-8c52-00c04fd90f7e")] = ("efsrpc", "Encrypting File System"),
        [Guid.Parse("86d35949-83c9-4044-b424-db363231fd0c")] = ("ITaskSchedulerService", "Task Scheduler"),
        [Guid.Parse("1ff70682-0a51-30e8-076d-740be8cee98b")] = ("atsvc", "Task Scheduler"),
        [Guid.Parse("50abc2a4-574d-40b3-9d66-ee4fd5fba076")] = ("dnsserver", "DNS Server"),
    };

    /// <summary>The interface's documented protocol name and what it is, or null for one no specification here names.</summary>
    public static (string Name, string Description)? Of(Guid rpcInterface) =>
        Known.TryGetValue(rpcInterface, out (string Name, string Description) known) ? known : null;

    /// <summary>
    /// How an interface reads in a row: its documented name and what it is, or its UUID when none is documented here;
    /// "an interface no start named" for the calls whose start was not seen.
    /// </summary>
    public static string Describe(Guid? rpcInterface) => rpcInterface is not { } uuid
        ? "an interface no start named"
        : Of(uuid) is { } known ? $"{known.Name} ({known.Description})" : uuid.ToString();
}

/// <summary>
/// Keys for an RPC channel - one process instance's calls on one side to one interface - and for one call on it. They
/// name instances and record identities, never positions, so a key reads the same channel and call in every
/// generation that still holds them (`contracts/operations-v1.md` §5).
/// </summary>
public static class RpcChannelKeys
{
    public const string Prefix = "rpc:";
    private const string NoInterface = "none";

    /// <summary>The key of one instance's calls on one side to one interface.</summary>
    public static string Channel(ProcessInstanceId instance, RpcCallSide side, Guid? rpcInterface) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{instance.Value:N}:{Side(side)}:{(rpcInterface is { } uuid ? uuid.ToString("N") : NoInterface)}");

    /// <summary>The key of one call on a channel, by its first record's raw locator and fact key.</summary>
    public static string Call(string channelKey, uint stream, uint epoch, ulong ordinal, FactKey factKey) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{channelKey}/{stream}.{epoch}.{ordinal}.{factKey.High:x16}{factKey.Low:x16}");

    /// <summary>Whether a key names an RPC channel or a call on one.</summary>
    public static bool IsRpc(string? key) => key is not null && key.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The channel a channel key names; false for any other key.</summary>
    public static bool TryParseChannel(string? key, out ProcessInstanceId instance, out RpcCallSide side, out Guid? rpcInterface)
    {
        instance = default;
        side = default;
        rpcInterface = null;
        if (!IsRpc(key) || key!.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = key[Prefix.Length..].Split(':');
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "N", out Guid id) || id == Guid.Empty
            || parts[1] switch { "client" => false, "server" => false, _ => true })
        {
            return false;
        }

        Guid? parsedInterface = null;
        if (parts[2] != NoInterface)
        {
            if (!Guid.TryParseExact(parts[2], "N", out Guid uuid))
            {
                return false;
            }

            parsedInterface = uuid;
        }

        instance = new(id);
        side = parts[1] == "client" ? RpcCallSide.Client : RpcCallSide.Server;
        rpcInterface = parsedInterface;
        return true;
    }

    /// <summary>The channel and the first record a call key names; false for any other key.</summary>
    public static bool TryParseCall(
        string? key,
        out string channelKey,
        out (uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey) first)
    {
        channelKey = string.Empty;
        first = default;
        int slash = key?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        if (slash < 0 || !TryParseChannel(key![..slash], out _, out _, out _))
        {
            return false;
        }

        string[] parts = key[(slash + 1)..].Split('.');
        if (parts.Length != 4
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint stream)
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint epoch)
            || !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong ordinal)
            || parts[3].Length != 32
            || !ulong.TryParse(parts[3].AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong high)
            || !ulong.TryParse(parts[3].AsSpan(16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong low))
        {
            return false;
        }

        channelKey = key[..slash];
        first = (stream, epoch, ordinal, new FactKey(high, low));
        return true;
    }

    private static string Side(RpcCallSide side) => side switch
    {
        RpcCallSide.Client => "client",
        RpcCallSide.Server => "server",
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "An RPC call has a client or a server side."),
    };
}
