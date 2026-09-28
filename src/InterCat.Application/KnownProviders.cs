namespace InterCat.Application;

/// <summary>
/// The public providers InterCat's source catalog admits, by the identity every machine gives them: Microsoft's, and
/// InterCat's own content fixture, whose identity is derived from its name. A record names its provider by that identity;
/// a reader is told the name beside it, which discloses nothing about the machine.
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
        [Guid.Parse("a70ff94f-570b-4979-ba5c-e59c9feab61b")] = "Microsoft-Windows-WinINet-Capture",
        [System.Diagnostics.Tracing.EventSource.GetGuid(typeof(InterCat.Domain.ContentFixtureEventSource))] =
            InterCat.Domain.ContentFixtureEventSource.ProviderName,
    };

    /// <summary>A provider's public name, or null for one the catalog does not name.</summary>
    public static string? NameOf(Guid provider) => Public.GetValueOrDefault(provider);
}
