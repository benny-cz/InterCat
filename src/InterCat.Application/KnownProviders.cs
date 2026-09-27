namespace InterCat.Application;

/// <summary>
/// The public Microsoft providers InterCat's source catalog admits, by the identity every machine gives them. A record
/// names its provider by that identity; a reader is told the name beside it, which discloses nothing about the machine.
/// </summary>
public static class KnownProviders
{
    public static IReadOnlyDictionary<Guid, string> Public { get; } = new Dictionary<Guid, string>
    {
        [Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88")] = "Microsoft-Windows-Kernel-Network",
        [Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716")] = "Microsoft-Windows-Kernel-Process",
        [Guid.Parse("6ad52b32-d609-4be9-ae07-ce8dae937e39")] = "Microsoft-Windows-RPC",
        [Guid.Parse("2f07e2ee-15db-40f1-90ef-9d7ba282188a")] = "Microsoft-Windows-TCPIP",
        [Guid.Parse("edd08927-9cc4-4e65-b970-c2560fb5c289")] = "Microsoft-Windows-Kernel-File",
        [Guid.Parse("d1d93ef7-e1f2-4f45-9943-03d245fe6c00")] = "Microsoft-Windows-Kernel-Memory",
    };

    /// <summary>A provider's public name, or null for one the catalog does not name.</summary>
    public static string? NameOf(Guid provider) => Public.GetValueOrDefault(provider);
}
