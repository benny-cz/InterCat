using System.Globalization;
using System.Xml.Linq;

namespace InterCat.Capture.Windows;

/// <summary>
/// A binary field's fixed length, and its out type, as TDH reports them for one event descriptor: what the provider's own
/// manifest declares (<c>length="16" outType="win:IPv6"</c>) and a manifest rebuilt from TDH metadata can leave out.
/// </summary>
public sealed record ManifestFieldShape(int EventId, int Version, string Field, int Length, string? OutType);

/// <summary>
/// Restores fixed binary lengths to a manifest that lost them. TraceEvent rebuilds a registered provider's manifest from
/// TDH and writes a 16-byte IPv6 address as <c>win:Binary</c> with no length, which makes it and every later field of its
/// event unreachable to a bounded read. The lengths TDH itself reports are written back before the manifest is parsed,
/// so the schema fingerprint covers them. A manifest nothing is restored to is returned exactly as it was, so its
/// fingerprint does not change.
/// </summary>
public static class ManifestFieldShapes
{
    private static readonly XNamespace EventsNamespace = "http://schemas.microsoft.com/win/2004/08/events";

    public static string Apply(string manifestXml, IReadOnlyList<ManifestFieldShape> shapes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestXml);
        ArgumentNullException.ThrowIfNull(shapes);
        if (shapes.Count == 0)
        {
            return manifestXml;
        }

        XDocument document = XDocument.Parse(manifestXml, LoadOptions.PreserveWhitespace);
        var templates = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement template in document.Descendants(EventsNamespace + "template"))
        {
            if (template.Attribute("tid")?.Value is { } id)
            {
                templates[id] = template;
            }
        }

        // What each template's unsized binary fields would become, from every event that uses the template. A field two
        // events describe differently is left as it was, unreachable, rather than given either length.
        var restored = new Dictionary<(string Template, string Field), (int Length, string? OutType)?>();
        foreach (XElement element in document.Descendants(EventsNamespace + "event"))
        {
            if (!int.TryParse(element.Attribute("value")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int eventId)
                || element.Attribute("template")?.Value is not { } templateId
                || !templates.ContainsKey(templateId))
            {
                continue;
            }

            _ = int.TryParse(element.Attribute("version")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int version);
            foreach (ManifestFieldShape shape in shapes)
            {
                if (shape.EventId != eventId || shape.Version != version || shape.Length <= 0)
                {
                    continue;
                }

                (string, string) key = (templateId, shape.Field);
                (int, string?) value = (shape.Length, shape.OutType);
                restored[key] = restored.TryGetValue(key, out (int Length, string? OutType)? seen) && seen != value
                    ? null
                    : value;
            }
        }

        bool changed = false;
        foreach (((string template, string field), (int Length, string? OutType)? shape) in restored)
        {
            if (shape is not { } fixedShape)
            {
                continue;
            }

            foreach (XElement data in templates[template].Elements(EventsNamespace + "data"))
            {
                if (!string.Equals(data.Attribute("name")?.Value, field, StringComparison.Ordinal)
                    || !string.Equals(data.Attribute("inType")?.Value, "win:Binary", StringComparison.Ordinal)
                    || data.Attribute("length") is not null)
                {
                    continue;
                }

                data.SetAttributeValue("length", fixedShape.Length.ToString(CultureInfo.InvariantCulture));
                if (fixedShape.OutType is { } outType && data.Attribute("outType") is null)
                {
                    data.SetAttributeValue("outType", outType);
                }

                changed = true;
            }
        }

        return changed ? document.ToString(SaveOptions.DisableFormatting) : manifestXml;
    }
}
