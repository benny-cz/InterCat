using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace InterCat.Capture.Windows;

/// <summary>
/// Parses the instrumentation manifest TDH reports for a registered provider into a schema the
/// adapter can reason about. Parsing is pure so its tests need neither Windows nor a live provider.
/// </summary>
public static class ManifestParser
{
    private static readonly XNamespace EventsNamespace = "http://schemas.microsoft.com/win/2004/08/events";

    public static ProviderSchema Parse(string manifestXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestXml);

        XDocument document = XDocument.Parse(manifestXml, LoadOptions.None);
        XElement provider = document.Descendants(EventsNamespace + "provider").FirstOrDefault()
            ?? throw new InvalidDataException("The manifest declares no provider element.");

        string name = provider.Attribute("name")?.Value ?? string.Empty;
        Guid guid = ParseGuid(provider.Attribute("guid")?.Value);

        Dictionary<string, ulong> keywords = new(StringComparer.Ordinal);
        foreach (XElement keyword in provider.Descendants(EventsNamespace + "keyword"))
        {
            string? keywordName = keyword.Attribute("name")?.Value;
            if (keywordName is not null && TryParseMask(keyword.Attribute("mask")?.Value, out ulong mask))
            {
                keywords[keywordName] = mask;
            }
        }

        Dictionary<string, int> taskValues = new(StringComparer.Ordinal);
        Dictionary<string, int> opcodeValues = new(StringComparer.Ordinal);
        foreach (XElement task in provider.Descendants(EventsNamespace + "task"))
        {
            string? taskName = task.Attribute("name")?.Value;
            if (taskName is not null && TryParseInt(task.Attribute("value")?.Value, out int taskValue))
            {
                taskValues[taskName] = taskValue;
            }
        }

        foreach (XElement opcode in provider.Descendants(EventsNamespace + "opcode"))
        {
            string? opcodeName = opcode.Attribute("name")?.Value;
            if (opcodeName is not null && TryParseInt(opcode.Attribute("value")?.Value, out int opcodeValue))
            {
                opcodeValues[opcodeName] = opcodeValue;
            }
        }

        Dictionary<string, IReadOnlyList<ProviderSchemaField>> templates = new(StringComparer.Ordinal);
        foreach (XElement template in provider.Descendants(EventsNamespace + "template"))
        {
            string? templateId = template.Attribute("tid")?.Value;
            if (templateId is null)
            {
                continue;
            }

            List<ProviderSchemaField> fields = [];
            foreach (XElement data in template.Elements(EventsNamespace + "data"))
            {
                string? fieldName = data.Attribute("name")?.Value;
                string? inType = data.Attribute("inType")?.Value;
                if (fieldName is not null && inType is not null)
                {
                    fields.Add(ProviderSchemaField.Create(fieldName, inType));
                }
            }

            templates[templateId] = fields;
        }

        List<ProviderSchemaEvent> events = [];
        foreach (XElement element in provider.Descendants(EventsNamespace + "event"))
        {
            if (!TryParseInt(element.Attribute("value")?.Value, out int eventId))
            {
                continue;
            }

            _ = TryParseInt(element.Attribute("version")?.Value, out int version);
            string? taskName = element.Attribute("task")?.Value;
            string? opcodeName = element.Attribute("opcode")?.Value;
            string? templateId = element.Attribute("template")?.Value;
            string[] keywordNames = (element.Attribute("keywords")?.Value ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            ulong keywordMask = 0;
            foreach (string keywordName in keywordNames)
            {
                if (keywords.TryGetValue(keywordName, out ulong mask))
                {
                    keywordMask |= mask;
                }
            }

            IReadOnlyList<ProviderSchemaField> fields = templateId is not null
                && templates.TryGetValue(templateId, out IReadOnlyList<ProviderSchemaField>? templateFields)
                    ? templateFields
                    : [];

            events.Add(new(
                eventId,
                version,
                element.Attribute("symbol")?.Value,
                taskName,
                taskName is not null && taskValues.TryGetValue(taskName, out int taskValue) ? taskValue : null,
                opcodeName,
                opcodeName is not null && opcodeValues.TryGetValue(opcodeName, out int opcodeValue) ? opcodeValue : null,
                element.Attribute("level")?.Value,
                keywordNames,
                keywordMask,
                templateId,
                fields));
        }

        events.Sort(static (left, right) => left.EventId != right.EventId
            ? left.EventId.CompareTo(right.EventId)
            : left.Version.CompareTo(right.Version));

        return new(name, guid, Fingerprint(manifestXml), keywords, events);
    }

    /// <summary>
    /// A content fingerprint of the exact manifest bytes the schema was built from. Whitespace is
    /// normalized so that an unchanged layout keeps one identity across formatting differences.
    /// </summary>
    public static string Fingerprint(string manifestXml)
    {
        ArgumentNullException.ThrowIfNull(manifestXml);

        var normalized = new StringBuilder(manifestXml.Length);
        bool pendingSpace = false;
        foreach (char character in manifestXml)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }

            normalized.Append(character);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Guid ParseGuid(string? value) =>
        value is not null && Guid.TryParse(value.Trim('{', '}'), out Guid parsed) ? parsed : Guid.Empty;

    private static bool TryParseInt(string? value, out int parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = 0;
            return false;
        }

        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryParseMask(string? value, out ulong parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = 0;
            return false;
        }

        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)
            : ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }
}
