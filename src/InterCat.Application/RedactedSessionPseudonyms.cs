using System.Security.Cryptography;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>Which pseudonym family a resource name belongs to, from the mechanism of the row that carries it.</summary>
internal enum RedactedNameKind
{
    /// <summary>A process image path or image file name. Grouped case-insensitively, as executable grouping is.</summary>
    Executable = 1,

    Pipe = 2,

    Resource = 3,
}

/// <summary>
/// The pseudonyms of one redacted session package (`contracts/redacted-session-v1.md`). Every namespace is a bijection on
/// the values the source holds: equal values share one pseudonym and distinct values get distinct ones, so every
/// relationship an analysis reads by equality survives. A pseudonym is random, is never one of the source's own values,
/// and nothing that maps it back is kept. A few values mean the same thing on every Windows machine and are kept as
/// fixed points, because replacing them would change what an analysis concludes: an unspecified endpoint (0) stays
/// incomplete rather than pairing, and loopback stays loopback.
/// </summary>
internal sealed class RedactedSessionPseudonyms
{
    /// <summary>
    /// The public Microsoft providers InterCat's source catalog admits. Their identity is the same on every machine, so
    /// keeping it discloses nothing about the machine and keeps records readable; every other provider is pseudonymized.
    /// Their schema fingerprints, which can identify a Windows build, are pseudonymized all the same.
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> PublicProviders { get; } = new Dictionary<Guid, string>
    {
        [Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88")] = "Microsoft-Windows-Kernel-Network",
        [Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716")] = "Microsoft-Windows-Kernel-Process",
        [Guid.Parse("6ad52b32-d609-4be9-ae07-ce8dae937e39")] = "Microsoft-Windows-RPC",
        [Guid.Parse("2f07e2ee-15db-40f1-90ef-9d7ba282188a")] = "Microsoft-Windows-TCPIP",
        [Guid.Parse("edd08927-9cc4-4e65-b970-c2560fb5c289")] = "Microsoft-Windows-Kernel-File",
        [Guid.Parse("d1d93ef7-e1f2-4f45-9943-03d245fe6c00")] = "Microsoft-Windows-Kernel-Memory",
    };

    public const string ExecutablePrefix = "executable-";
    public const string PipePrefix = "pipe-";
    public const string ResourcePrefix = "resource-";
    public const string FolderPrefix = "folder-";
    public const string ProviderPrefix = "provider-";
    public const string SchemaPrefix = "redacted-schema-";

    /// <summary>Extensions an executable pseudonym keeps; any other is dropped with the name.</summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.Ordinal) { ".exe", ".com", ".scr" };

    private static readonly char[] Separators = ['\\', '/'];

    // Source values, collected by the inspection pass so no pseudonym is ever one of them.
    private readonly HashSet<int> sourceNumbers = [];
    private readonly HashSet<uint> sourceAddresses = [];
    private readonly HashSet<ushort> sourcePorts = [];
    private readonly HashSet<Guid> sourceIdentifiers = [];
    private readonly HashSet<ulong> sourceSequences = [];
    private readonly HashSet<long> sourcePointers = [];
    private readonly HashSet<Guid> sourceProviders = [];
    private readonly HashSet<string> sourceFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> sourceNames = new(StringComparer.Ordinal);

    private readonly Dictionary<int, int> numbers = [];
    private readonly Dictionary<uint, uint> addresses = [];
    private readonly Dictionary<ushort, ushort> ports = [];
    private readonly Dictionary<Guid, Guid> identifiers = [];
    private readonly Dictionary<ulong, ulong> sequences = [];
    private readonly Dictionary<long, long> pointers = [];
    private readonly Dictionary<Guid, Guid> providers = [];
    private readonly Dictionary<string, string> fingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<(RedactedNameKind Kind, bool Folder, string Key), string> names = [];

    private readonly HashSet<int> issuedNumbers = [];
    private readonly HashSet<uint> issuedAddresses = [];
    private readonly HashSet<ushort> issuedPorts = [];
    private readonly HashSet<Guid> issuedIdentifiers = [];
    private readonly HashSet<ulong> issuedSequences = [];
    private readonly HashSet<long> issuedPointers = [];
    private readonly HashSet<Guid> issuedProviders = [];
    private readonly HashSet<string> issuedTexts = new(StringComparer.Ordinal);
    private readonly HashSet<string> issuedFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> issuedResourceNames = new(StringComparer.Ordinal);
    private Queue<ushort>? freePorts;

    /// <summary>Every distinct source name, folder and file name, for the leak scan.</summary>
    public IReadOnlyCollection<string> SourceNames => sourceNames;

    /// <summary>The providers that were pseudonymized, for the leak scan.</summary>
    public IEnumerable<Guid> PseudonymizedSourceProviders => sourceProviders.Where(id => !PublicProviders.ContainsKey(id));

    public IReadOnlyCollection<string> SourceFingerprints => sourceFingerprints;

    public int NameCount => names.Count;
    public int NumberCount => numbers.Count;
    public int AddressCount => addresses.Count;
    public int PortCount => ports.Count;
    public int IdentifierCount => identifiers.Count;
    public int ProviderCount => providers.Count(entry => entry.Key != entry.Value);
    public int PublicProviderCount => providers.Count(entry => entry.Key == entry.Value && entry.Key != Guid.Empty);
    public int SchemaCount => fingerprints.Count;
    public int SequenceCount => sequences.Count;
    public int PointerCount => pointers.Count;

    public static RedactedNameKind NameKindOf(Mechanism mechanism) => mechanism switch
    {
        Mechanism.ProcessLifecycle => RedactedNameKind.Executable,
        Mechanism.NamedPipe or Mechanism.AnonymousPipe => RedactedNameKind.Pipe,
        _ => RedactedNameKind.Resource,
    };

    // ---- The inspection pass records what the source holds. ----

    public void SeeNumber(int value) => sourceNumbers.Add(value);

    public void SeeAddress(uint? value)
    {
        if (value is { } address) sourceAddresses.Add(address);
    }

    public void SeePort(ushort? value)
    {
        if (value is { } port) sourcePorts.Add(port);
    }

    public void SeeIdentifier(Guid? value)
    {
        if (value is { } identifier) sourceIdentifiers.Add(identifier);
    }

    public void SeeSequence(ulong value) => sourceSequences.Add(value);

    public void SeePointer(long value) => sourcePointers.Add(value);

    public void SeeProvider(Guid value) => sourceProviders.Add(value);

    public void SeeFingerprint(string value) => sourceFingerprints.Add(value);

    public void SeeName(string value)
    {
        sourceNames.Add(value);
        int split = value.LastIndexOfAny(Separators);
        if (split >= 0 && split < value.Length - 1)
        {
            if (split > 0) sourceNames.Add(value[..split]);
            sourceNames.Add(value[(split + 1)..]);
        }
    }

    // ---- Mapping. ----

    /// <summary>A process or thread id. 0 (idle), 4 (System) and -1 (no process) mean the same on every machine.</summary>
    public int Number(int value)
    {
        if (IsFixedNumber(value)) return value;
        if (numbers.TryGetValue(value, out int mapped)) return mapped;
        do
        {
            // Multiples of four in [1,000, 100,000,000): shaped like Windows ids, and unlike any this source holds.
            mapped = 4 * RandomNumberGenerator.GetInt32(250, 25_000_000);
        }
        while (sourceNumbers.Contains(mapped) || !issuedNumbers.Add(mapped));
        numbers[value] = mapped;
        return mapped;
    }

    public int? Number(int? value) => value is { } number ? Number(number) : null;

    public static bool IsFixedNumber(int value) => value is 0 or 4 or -1;

    public bool IsIssuedOrFixedNumber(int value) => IsFixedNumber(value) || issuedNumbers.Contains(value);

    /// <summary>
    /// An IPv4 address. The unspecified address, loopback and limited broadcast are the same on every machine and keep
    /// their meaning; every other address becomes one in 240.0.0.0/4, reserved and never routed.
    /// </summary>
    public uint? Address(uint? value)
    {
        if (value is not { } address) return null;
        if (IsFixedAddress(address)) return address;
        if (addresses.TryGetValue(address, out uint mapped)) return mapped;
        do
        {
            mapped = 0xF000_0000u + (uint)RandomNumberGenerator.GetInt32(0, 0x0FFF_FFFF);
        }
        while (sourceAddresses.Contains(mapped) || !issuedAddresses.Add(mapped));
        addresses[address] = mapped;
        return mapped;
    }

    public static bool IsFixedAddress(uint address) => address is 0 or uint.MaxValue || address >> 24 == 127;

    public bool IsIssuedOrFixedAddress(uint address) => IsFixedAddress(address) || issuedAddresses.Contains(address);

    /// <summary>
    /// A port. Port 0 stays 0, because a relation treats it as an incomplete endpoint. Every other port becomes one of
    /// 1024-65535 the source does not use, so no pseudonym looks like a well-known service.
    /// </summary>
    public ushort? Port(ushort? value)
    {
        if (value is not { } port) return null;
        if (port == 0) return port;
        if (ports.TryGetValue(port, out ushort mapped)) return mapped;
        freePorts ??= FreePorts();
        if (!freePorts.TryDequeue(out mapped))
        {
            throw new InvalidOperationException(
                $"This session uses {sourcePorts.Count:N0} distinct ports, too many to give each a distinct pseudonym "
                + "outside the ports it already uses. The package refuses rather than reuse a source port.");
        }

        issuedPorts.Add(mapped);
        ports[port] = mapped;
        return mapped;
    }

    public bool IsIssuedOrFixedPort(ushort port) => port == 0 || issuedPorts.Contains(port);

    private Queue<ushort> FreePorts()
    {
        ushort[] free = [.. Enumerable.Range(1024, 65536 - 1024).Select(port => (ushort)port)
            .Where(port => !sourcePorts.Contains(port))];
        RandomNumberGenerator.Shuffle(free.AsSpan());
        return new(free);
    }

    /// <summary>An activity, related activity or source identifier. The empty identifier means none and stays.</summary>
    public Guid? Identifier(Guid? value)
    {
        if (value is not { } identifier) return null;
        if (identifier == Guid.Empty) return identifier;
        if (identifiers.TryGetValue(identifier, out Guid mapped)) return mapped;
        do
        {
            mapped = Guid.NewGuid();
        }
        while (sourceIdentifiers.Contains(mapped) || !issuedIdentifiers.Add(mapped));
        identifiers[identifier] = mapped;
        return mapped;
    }

    public bool IsIssuedOrFixedIdentifier(Guid identifier) =>
        identifier == Guid.Empty || issuedIdentifiers.Contains(identifier);

    /// <summary>A process start sequence number. Compared only for equality; its magnitude would reveal uptime.</summary>
    public ulong Sequence(ulong value)
    {
        if (value == 0) return value;
        if (sequences.TryGetValue(value, out ulong mapped)) return mapped;
        do
        {
            mapped = 1 + (BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) % ((1UL << 40) - 1));
        }
        while (sourceSequences.Contains(mapped) || !issuedSequences.Add(mapped));
        sequences[value] = mapped;
        return mapped;
    }

    public bool IsIssuedOrFixedSequence(ulong value) => value == 0 || issuedSequences.Contains(value);

    /// <summary>A kernel object value: a connection id, request packet, file object or file key. Zero means none.</summary>
    public long Pointer(long value)
    {
        if (value == 0) return value;
        if (pointers.TryGetValue(value, out long mapped)) return mapped;
        do
        {
            // Sixteen-byte aligned values below 2^47: shaped like an address, and none the source holds.
            mapped = (long)(0x10000UL + ((BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) % (1UL << 43)) << 4));
        }
        while (sourcePointers.Contains(mapped) || !issuedPointers.Add(mapped));
        pointers[value] = mapped;
        return mapped;
    }

    public bool IsIssuedOrFixedPointer(long value) => value == 0 || issuedPointers.Contains(value);

    /// <summary>A provider: kept when it is one of the public providers, pseudonymized otherwise.</summary>
    public Guid Provider(Guid value)
    {
        if (value == Guid.Empty || PublicProviders.ContainsKey(value))
        {
            providers.TryAdd(value, value);
            return value;
        }

        if (providers.TryGetValue(value, out Guid mapped)) return mapped;
        do
        {
            mapped = Guid.NewGuid();
        }
        while (sourceProviders.Contains(mapped) || PublicProviders.ContainsKey(mapped)
            || sourceNames.Contains(PseudonymousProviderName(mapped))
            || !issuedTexts.Add(PseudonymousProviderName(mapped)) || !issuedProviders.Add(mapped));
        providers[value] = mapped;
        return mapped;
    }

    /// <summary>
    /// The name a pseudonymous provider goes by: <c>provider-</c> and the first eight hexadecimal digits of its
    /// pseudonymous identifier, unique in its package. A reader derives it from a row's provider identifier, so a
    /// record and the coverage ledger name one provider alike.
    /// </summary>
    public static string PseudonymousProviderName(Guid pseudonym) => ProviderPrefix + pseudonym.ToString("N")[..8];

    public bool IsIssuedOrPublicProvider(Guid value) =>
        value == Guid.Empty || PublicProviders.ContainsKey(value) || issuedProviders.Contains(value);

    /// <summary>The name a coverage ledger shows for a provider: its public name, or a pseudonym.</summary>
    public string ProviderName(Guid original)
    {
        Guid mapped = Provider(original);
        return PublicProviders.TryGetValue(mapped, out string? known) ? known : PseudonymousProviderName(mapped);
    }

    public bool IsIssuedOrPublicProviderName(string name) =>
        PublicProviders.Values.Contains(name, StringComparer.Ordinal)
        || issuedProviders.Any(provider => PseudonymousProviderName(provider) == name);

    /// <summary>
    /// A schema fingerprint. Equal fingerprints share a pseudonym, so records of one descriptor still share a layout;
    /// the fingerprint itself can identify a Windows build and is never kept.
    /// </summary>
    public string Fingerprint(string value)
    {
        if (fingerprints.TryGetValue(value, out string? mapped)) return mapped;
        mapped = Token(SchemaPrefix, hexCharacters: 16);
        fingerprints[value] = mapped;
        issuedFingerprints.Add(mapped);
        return mapped;
    }

    public bool IsIssuedFingerprint(string value) => issuedFingerprints.Contains(value);

    /// <summary>
    /// A resource name. A path is pseudonymized a component at a time: its folder and its file name each map to one
    /// token, joined by the separator the source used. That keeps what an analysis reads by equality: the file name of
    /// a pseudonymized image path is the pseudonym of the source's file name, which is also what an exit record's bare
    /// image name maps to, and paths that group together - case-insensitively, for executables - still do.
    /// </summary>
    public string Name(RedactedNameKind kind, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        // The split mirrors how an image name is read from a path: no separator, or one at the very end, means the
        // whole value is the name; a separator first means a rooted name with no folder to pseudonymize.
        int split = value.LastIndexOfAny(Separators);
        string pseudonym = split < 0 || split == value.Length - 1
            ? File(kind, value)
            : (split == 0 ? string.Empty : Folder(kind, value[..split])) + value[split] + File(kind, value[(split + 1)..]);
        issuedResourceNames.Add(pseudonym);
        return pseudonym;
    }

    public bool IsIssuedName(string value) => issuedResourceNames.Contains(value);

    private string Folder(RedactedNameKind kind, string folder)
    {
        string key = kind == RedactedNameKind.Executable ? folder.ToUpperInvariant() : folder;
        if (names.TryGetValue((kind, true, key), out string? token)) return token;
        token = Token(FolderPrefix);
        names[(kind, true, key)] = token;
        return token;
    }

    private string File(RedactedNameKind kind, string file)
    {
        string key = kind == RedactedNameKind.Executable ? file.ToUpperInvariant() : file;
        if (names.TryGetValue((kind, false, key), out string? token)) return token;
        string? extension = null;
        if (kind == RedactedNameKind.Executable)
        {
            int dot = key.LastIndexOf('.');
            string candidate = dot > 0 ? key[dot..].ToLowerInvariant() : string.Empty;
            extension = ExecutableExtensions.Contains(candidate) ? candidate : null;
        }

        token = Token(kind switch
        {
            RedactedNameKind.Executable => ExecutablePrefix,
            RedactedNameKind.Pipe => PipePrefix,
            _ => ResourcePrefix,
        }, suffix: extension);
        names[(kind, false, key)] = token;
        return token;
    }

    /// <summary>A random token with a readable prefix, unique in this package and unlike any source name.</summary>
    private string Token(string prefix, int hexCharacters = 8, string? suffix = null)
    {
        string token;
        do
        {
            token = prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(hexCharacters / 2)) + suffix;
        }
        while (sourceNames.Contains(token) || sourceFingerprints.Contains(token) || !issuedTexts.Add(token));
        return token;
    }
}
