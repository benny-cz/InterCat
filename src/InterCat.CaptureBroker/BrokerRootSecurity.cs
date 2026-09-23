using System.Globalization;

namespace InterCat.CaptureBroker;

/// <summary>Windows mandatory integrity RIDs the broker names explicitly rather than by number.</summary>
public static class BrokerIntegrityLevel
{
    public const int Low = 0x1000;
    public const int Medium = 0x2000;
    public const int High = 0x3000;
    public const int System = 0x4000;

    /// <summary>The label SID for a level, written out so the descriptor never relies on an alias.</summary>
    public static string ToSid(int level) => level switch
    {
        Low => "S-1-16-4096",
        Medium => "S-1-16-8192",
        High => "S-1-16-12288",
        System => "S-1-16-16384",
        _ => throw new ArgumentOutOfRangeException(
            nameof(level),
            level,
            "The broker labels a root Low, Medium, High or System; no other level is written."),
    };

    public static bool IsKnown(int level) =>
        level is Low or Medium or High or System;
}

/// <summary>One parsed access-control entry. Masks are numeric so an abbreviation can never widen one.</summary>
public sealed record BrokerSecurityAce(
    string AceType,
    IReadOnlyList<string> Flags,
    uint Mask,
    string Sid)
{
    public bool SameAs(BrokerSecurityAce other) =>
        other is not null
        && AceType.Equals(other.AceType, StringComparison.OrdinalIgnoreCase)
        && Mask == other.Mask
        && Sid.Equals(other.Sid, StringComparison.OrdinalIgnoreCase)
        && Flags.Count == other.Flags.Count
        && Flags.Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(other.Flags.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    public override string ToString() =>
        $"({AceType};{string.Concat(Flags.Order(StringComparer.Ordinal))};0x{Mask:x};;;{Sid})";
}

/// <summary>
/// What a security descriptor actually says, parsed from its SDDL form. The broker compares facts
/// rather than strings: Windows is free to reorder inheritance flags and to render a well-known SID
/// as an alias, and neither difference changes what the descriptor grants.
/// </summary>
public sealed record BrokerSecurityDescriptorFacts(
    string? OwnerSid,
    bool DiscretionaryAclPresent,
    bool DiscretionaryAclProtected,
    IReadOnlyList<BrokerSecurityAce> DiscretionaryAces,
    IReadOnlyList<BrokerSecurityAce> SystemAces)
{
    private static readonly Dictionary<string, string> SidAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AN"] = "S-1-5-7",
        ["AU"] = "S-1-5-11",
        ["BA"] = "S-1-5-32-544",
        ["BU"] = "S-1-5-32-545",
        ["CO"] = "S-1-3-0",
        ["HI"] = "S-1-16-12288",
        ["IU"] = "S-1-5-4",
        ["LS"] = "S-1-5-19",
        ["LW"] = "S-1-16-4096",
        ["ME"] = "S-1-16-8192",
        ["MP"] = "S-1-16-8448",
        ["NS"] = "S-1-5-20",
        ["SI"] = "S-1-16-16384",
        ["SY"] = "S-1-5-18",
        ["WD"] = "S-1-1-0",
    };

    private static readonly Dictionary<string, uint> AccessRights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GA"] = 0x1000_0000,
        ["GX"] = 0x2000_0000,
        ["GW"] = 0x4000_0000,
        ["GR"] = 0x8000_0000,
        ["SD"] = 0x0001_0000,
        ["RC"] = 0x0002_0000,
        ["WD"] = 0x0004_0000,
        ["WO"] = 0x0008_0000,
        ["FA"] = 0x001F_01FF,
        ["FR"] = 0x0012_0089,
        ["FW"] = 0x0012_0116,
        ["FX"] = 0x0012_00A0,
    };

    private static readonly Dictionary<string, uint> LabelRights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NW"] = 0x0000_0001,
        ["NR"] = 0x0000_0002,
        ["NX"] = 0x0000_0004,
    };

    private static readonly string[] AceFlagTokens = ["CI", "OI", "IO", "NP", "ID", "SA", "FA"];

    public static BrokerSecurityDescriptorFacts Parse(string sddl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sddl);
        Dictionary<char, string> components = SplitComponents(sddl);
        string? owner = components.TryGetValue('O', out string? ownerValue)
            ? CanonicalizeSid(ownerValue)
            : null;
        (bool daclPresent, bool daclProtected, IReadOnlyList<BrokerSecurityAce> daclAces) = ParseAcl(components, 'D');
        (_, _, IReadOnlyList<BrokerSecurityAce> saclAces) = ParseAcl(components, 'S');
        return new(owner, daclPresent, daclProtected, daclAces, saclAces);
    }

    /// <summary>The one mandatory label, or null when the descriptor carries none.</summary>
    public BrokerSecurityAce? MandatoryLabel =>
        SystemAces.SingleOrDefault(ace => ace.AceType.Equals("ML", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<char, string> SplitComponents(string sddl)
    {
        Dictionary<char, string> components = [];
        int depth = 0;
        char current = '\0';
        int start = 0;
        for (int index = 0; index < sddl.Length; index++)
        {
            char character = sddl[index];
            if (character == '(')
            {
                depth++;
                continue;
            }

            if (character == ')')
            {
                depth--;
                if (depth < 0)
                {
                    throw new InvalidDataException("The security descriptor has an unbalanced ')'.");
                }

                continue;
            }

            bool marker = depth == 0
                && index + 1 < sddl.Length
                && sddl[index + 1] == ':'
                && character is 'O' or 'G' or 'D' or 'S';
            if (!marker)
            {
                continue;
            }

            if (current != '\0')
            {
                Add(components, current, sddl[start..index]);
            }

            current = character;
            start = index + 2;
            index++;
        }

        if (depth != 0)
        {
            throw new InvalidDataException("The security descriptor has an unbalanced '('.");
        }

        if (current == '\0')
        {
            throw new InvalidDataException("The security descriptor names no owner, group, DACL or SACL.");
        }

        Add(components, current, sddl[start..]);
        return components;

        static void Add(Dictionary<char, string> components, char key, string value)
        {
            if (!components.TryAdd(key, value))
            {
                throw new InvalidDataException($"The security descriptor repeats its '{key}:' component.");
            }
        }
    }

    private static (bool Present, bool Protected, IReadOnlyList<BrokerSecurityAce> Aces) ParseAcl(
        Dictionary<char, string> components,
        char key)
    {
        if (!components.TryGetValue(key, out string? value))
        {
            return (false, false, []);
        }

        int cursor = 0;
        bool isProtected = false;
        while (cursor < value.Length && value[cursor] != '(')
        {
            if (value[cursor] == 'P')
            {
                isProtected = true;
                cursor++;
                continue;
            }

            if (cursor + 1 < value.Length && value.AsSpan(cursor, 2) is "AI" or "AR")
            {
                cursor += 2;
                continue;
            }

            throw new InvalidDataException(
                $"The '{key}:' component carries an unsupported control flag at position {cursor}.");
        }

        List<BrokerSecurityAce> aces = [];
        bool label = key == 'S';
        while (cursor < value.Length)
        {
            if (value[cursor] != '(')
            {
                throw new InvalidDataException($"The '{key}:' component has text outside an ACE.");
            }

            int end = value.IndexOf(')', cursor);
            if (end < 0)
            {
                throw new InvalidDataException($"The '{key}:' component has an unterminated ACE.");
            }

            aces.Add(ParseAce(value[(cursor + 1)..end], label));
            cursor = end + 1;
        }

        return (true, isProtected, aces);
    }

    private static BrokerSecurityAce ParseAce(string ace, bool label)
    {
        string[] fields = ace.Split(';');
        if (fields.Length != 6)
        {
            throw new InvalidDataException(
                $"ACE '({ace})' has {fields.Length} fields; the broker reads only plain six-field ACEs.");
        }

        if (fields[3].Length != 0 || fields[4].Length != 0)
        {
            throw new InvalidDataException($"ACE '({ace})' names an object type the broker never applies.");
        }

        if (fields[0].Length == 0 || fields[5].Length == 0)
        {
            throw new InvalidDataException($"ACE '({ace})' has an empty type or trustee.");
        }

        return new(
            fields[0].ToUpperInvariant(),
            ParseFlags(fields[1], ace),
            ParseMask(fields[2], ace, label),
            CanonicalizeSid(fields[5]));
    }

    private static List<string> ParseFlags(string flags, string ace)
    {
        List<string> parsed = [];
        for (int index = 0; index < flags.Length; index += 2)
        {
            if (index + 2 > flags.Length)
            {
                throw new InvalidDataException($"ACE '({ace})' has a truncated inheritance flag.");
            }

            string token = flags.Substring(index, 2).ToUpperInvariant();
            if (!AceFlagTokens.Contains(token, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"ACE '({ace})' carries unsupported inheritance flag '{token}'.");
            }

            if (parsed.Contains(token, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"ACE '({ace})' repeats inheritance flag '{token}'.");
            }

            parsed.Add(token);
        }

        return parsed;
    }

    private static uint ParseMask(string mask, string ace, bool label)
    {
        if (mask.Length == 0)
        {
            throw new InvalidDataException($"ACE '({ace})' has an empty access mask.");
        }

        if (mask.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(mask.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
                ? value
                : throw new InvalidDataException($"ACE '({ace})' has an unreadable hexadecimal access mask.");
        }

        Dictionary<string, uint> rights = label ? LabelRights : AccessRights;
        uint accumulated = 0;
        for (int index = 0; index < mask.Length; index += 2)
        {
            if (index + 2 > mask.Length || !rights.TryGetValue(mask.Substring(index, 2), out uint right))
            {
                throw new InvalidDataException(
                    $"ACE '({ace})' uses an access abbreviation the broker does not resolve; "
                    + "an unresolved right is unknown rather than none.");
            }

            accumulated |= right;
        }

        return accumulated;
    }

    private static string CanonicalizeSid(string sid)
    {
        string trimmed = sid.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidDataException("The security descriptor names an empty trustee.");
        }

        if (SidAliases.TryGetValue(trimmed, out string? resolved))
        {
            return resolved;
        }

        return trimmed.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
            ? trimmed.ToUpperInvariant()
            : throw new InvalidDataException(
                $"The security descriptor names trustee '{trimmed}', which is neither a SID nor a resolved alias.");
    }
}

/// <summary>
/// The security a broker root must carry. It is declared once, applied when the root is created and
/// checked again from the open handle, so drift becomes a refusal with a named difference rather than
/// a directory that merely looks right.
/// </summary>
public sealed record BrokerRootSecurityPolicy
{
    /// <summary>FILE_ALL_ACCESS: what the broker identity needs to own its root.</summary>
    public const uint FullControlMask = 0x001F_01FF;

    /// <summary>FILE_GENERIC_READ | FILE_TRAVERSE: enough for an ordinary viewer, never enough to write.</summary>
    public const uint ViewerReadMask = 0x0012_00A9;

    /// <summary>SYSTEM_MANDATORY_LABEL_NO_WRITE_UP.</summary>
    public const uint NoWriteUpMask = 0x0000_0001;

    public const string LocalSystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";

    private static readonly string[] InheritedFlags = ["OI", "CI"];

    public required IReadOnlyList<string> BrokerPrincipalSids { get; init; }

    public required IReadOnlyList<string> TrustedOwnerSids { get; init; }

    /// <summary>
    /// The owner written into the descriptor a new root or capture directory is created with, or null for the
    /// creating token's default owner. Production names Administrators: an elevated administrator's default owner is
    /// the user themself under Windows' default "object creator" setting, so relying on the token default made the
    /// broker refuse the root it had just created (found by the first elevated qualification run).
    /// </summary>
    public string? CreationOwnerSid { get; init; }

    public required int MandatoryIntegrityLevel { get; init; }

    /// <summary>Production requires the broker's own token to be elevated before it provisions a root.</summary>
    public required bool RequireElevatedBroker { get; init; }

    /// <summary>
    /// Production requires the root's parent to be owned by a trusted principal. Holding the root
    /// handle stops the root itself being renamed; only a trusted parent stops an ancestor being
    /// renamed around it afterwards.
    /// </summary>
    public required bool RequireTrustedParentOwner { get; init; }

    public static BrokerRootSecurityPolicy Production { get; } = new()
    {
        BrokerPrincipalSids = [LocalSystemSid, AdministratorsSid],
        TrustedOwnerSids = [LocalSystemSid, AdministratorsSid],
        CreationOwnerSid = AdministratorsSid,
        MandatoryIntegrityLevel = BrokerIntegrityLevel.High,
        RequireElevatedBroker = true,
        RequireTrustedParentOwner = true,
    };

    public string? Validate()
    {
        if (BrokerPrincipalSids.Count is 0 or > 8 || TrustedOwnerSids.Count is 0 or > 8)
        {
            return "A broker root policy names between one and eight broker principals and trusted owners.";
        }

        if (!BrokerIntegrityLevel.IsKnown(MandatoryIntegrityLevel))
        {
            return "A broker root policy names a Low, Medium, High or System mandatory label.";
        }

        foreach (string sid in BrokerPrincipalSids.Concat(TrustedOwnerSids))
        {
            if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            {
                return $"'{sid}' is not a SID literal; a broker root policy never names a trustee by alias.";
            }
        }

        if (CreationOwnerSid is not null && !IsTrustedOwner(CreationOwnerSid))
        {
            return $"The creation owner {CreationOwnerSid} must be one of the policy's trusted owners.";
        }

        return BrokerPrincipalSids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != BrokerPrincipalSids.Count
            ? "A broker root policy names each broker principal once."
            : null;
    }

    /// <summary>The exact descriptor applied when the root is created, as SDDL.</summary>
    public string BuildSecurityDescriptorSddl(string capturingUserSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturingUserSid);
        string? problem = Validate();
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }

        IEnumerable<string> aces = ExpectedDiscretionaryAces(capturingUserSid)
            .Select(ace => $"(A;OICI;0x{ace.Mask:x};;;{ace.Sid})");
        string label = BrokerIntegrityLevel.ToSid(MandatoryIntegrityLevel);
        string owner = CreationOwnerSid is null ? string.Empty : $"O:{CreationOwnerSid.ToUpperInvariant()}";
        return $"{owner}D:P{string.Concat(aces)}S:(ML;OICI;0x{NoWriteUpMask:x};;;{label})";
    }

    /// <summary>
    /// The ACEs the root must carry and no others. A capturing user who is already a broker principal
    /// keeps that one full-control entry: the mandatory label, not a second weaker ACE, is what refuses
    /// their ordinary-integrity writes.
    /// </summary>
    public IReadOnlyList<BrokerSecurityAce> ExpectedDiscretionaryAces(string capturingUserSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturingUserSid);
        List<BrokerSecurityAce> aces =
        [
            .. BrokerPrincipalSids.Select(sid =>
                new BrokerSecurityAce("A", InheritedFlags, FullControlMask, sid.ToUpperInvariant())),
        ];
        string user = capturingUserSid.ToUpperInvariant();
        if (!aces.Any(ace => ace.Sid.Equals(user, StringComparison.OrdinalIgnoreCase)))
        {
            aces.Add(new("A", InheritedFlags, ViewerReadMask, user));
        }

        return aces;
    }

    /// <summary>Returns the reason an observed descriptor is not this policy's, or null when it is.</summary>
    public string? Approve(BrokerSecurityDescriptorFacts observed, string capturingUserSid)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (!observed.DiscretionaryAclPresent)
        {
            return "The broker root has no discretionary ACL, so every principal would be granted access.";
        }

        if (!observed.DiscretionaryAclProtected)
        {
            return "The broker root's ACL is not protected, so a parent's inherited entries still apply.";
        }

        IReadOnlyList<BrokerSecurityAce> expected = ExpectedDiscretionaryAces(capturingUserSid);
        foreach (BrokerSecurityAce ace in observed.DiscretionaryAces)
        {
            if (!expected.Any(candidate => candidate.SameAs(ace)))
            {
                return $"The broker root carries unexpected access-control entry {ace}.";
            }
        }

        foreach (BrokerSecurityAce ace in expected)
        {
            if (!observed.DiscretionaryAces.Any(candidate => candidate.SameAs(ace)))
            {
                return $"The broker root is missing required access-control entry {ace}.";
            }
        }

        BrokerSecurityAce? label = observed.MandatoryLabel;
        if (label is null)
        {
            return "The broker root carries no mandatory label, so an ordinary-integrity write is not refused.";
        }

        string expectedLabelSid = BrokerIntegrityLevel.ToSid(MandatoryIntegrityLevel);
        if (!label.Sid.Equals(expectedLabelSid, StringComparison.OrdinalIgnoreCase))
        {
            return $"The broker root is labelled {label.Sid}; this policy requires {expectedLabelSid}.";
        }

        if ((label.Mask & NoWriteUpMask) == 0)
        {
            return "The broker root's mandatory label does not refuse write-up.";
        }

        return observed.OwnerSid is not null && !IsTrustedOwner(observed.OwnerSid)
            ? $"The broker root is owned by {observed.OwnerSid}, which is not a trusted owner."
            : null;
    }

    public bool IsTrustedOwner(string sid) =>
        !string.IsNullOrWhiteSpace(sid)
        && TrustedOwnerSids.Contains(sid, StringComparer.OrdinalIgnoreCase);
}
