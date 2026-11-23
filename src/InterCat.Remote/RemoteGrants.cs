namespace InterCat.Remote;

/// <summary>
/// What an enrolled controller may ask an agent for, each granted apart (`remote-agent-v1` §5). The agent's own local
/// policy is authoritative over every grant: a grant it forbids is refused as if never given.
/// </summary>
[Flags]
public enum RemoteGrants : ushort
{
    None = 0,

    /// <summary>Prepare, start, follow and stop a capture of metadata on the agent's host.</summary>
    MetadataCapture = 1,

    /// <summary>A capture that also keeps contents, under the agent's content policy; never implied by metadata capture.</summary>
    Contents = 2,

    /// <summary>Download a finished session the agent's host holds.</summary>
    Download = 4,

    /// <summary>Install an optional collector: an administrative workflow of its own, refused by every version 1 agent.</summary>
    Collectors = 8,
}

/// <summary>The grants a token can carry and a version 1 agent can honor.</summary>
public static class RemoteGrantsExtensions
{
    /// <summary>Every grant version 1 defines.</summary>
    public const RemoteGrants Defined = RemoteGrants.MetadataCapture | RemoteGrants.Contents | RemoteGrants.Download | RemoteGrants.Collectors;

    /// <summary>The grants as their contract names, in order, for a listing or a document.</summary>
    public static IReadOnlyList<string> Names(this RemoteGrants grants)
    {
        var names = new List<string>(4);
        if (grants.HasFlag(RemoteGrants.MetadataCapture)) names.Add("metadata-capture");
        if (grants.HasFlag(RemoteGrants.Contents)) names.Add("contents");
        if (grants.HasFlag(RemoteGrants.Download)) names.Add("download");
        if (grants.HasFlag(RemoteGrants.Collectors)) names.Add("collectors");
        return names;
    }

    /// <summary>Reads grants from their contract names; false where one is not a name version 1 defines.</summary>
    public static bool TryParse(IEnumerable<string> names, out RemoteGrants grants)
    {
        ArgumentNullException.ThrowIfNull(names);
        grants = RemoteGrants.None;
        foreach (string name in names)
        {
            RemoteGrants? grant = name switch
            {
                "metadata-capture" => RemoteGrants.MetadataCapture,
                "contents" => RemoteGrants.Contents,
                "download" => RemoteGrants.Download,
                "collectors" => RemoteGrants.Collectors,
                _ => null,
            };
            if (grant is not { } known)
            {
                grants = RemoteGrants.None;
                return false;
            }

            grants |= known;
        }

        return true;
    }
}
