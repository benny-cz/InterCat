using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterCat.Application;

/// <summary>
/// Why a JSON file a person may edit by hand could not be read, in words that say where to look: a line and a character,
/// the fields it lacks, or the ones it holds that are not known; never a parser's or a type's internals (§26.3: a format
/// that is documented, versioned and hand-editable).
/// </summary>
public static class JsonProblems
{
    /// <summary>Where text stops being JSON: its line and character, each counted from 1.</summary>
    public static string Syntax(JsonException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.LineNumber is { } line
            ? string.Create(CultureInfo.CurrentCulture,
                $"it is not valid JSON at line {line + 1:N0}, character {(exception.BytePositionInLine ?? 0) + 1:N0}")
            : "it is not valid JSON";
    }

    /// <summary>
    /// What of a JSON document's fields <typeparamref name="T"/> could not take: the required ones it lacks, the ones it
    /// holds that <typeparamref name="T"/> does not know, or else the value at the path that did not fit.
    /// </summary>
    public static string Fields<T>(JsonElement root, JsonException exception, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(options);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "it holds no object of fields";
        }

        var known = typeof(T).GetProperties()
            .Where(property => !property.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
            .Select(property => (Property: property,
                Name: property.GetCustomAttributes(typeof(JsonPropertyNameAttribute), inherit: true)
                    .OfType<JsonPropertyNameAttribute>().FirstOrDefault()?.Name
                    ?? options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name))
            .ToArray();
        StringComparer names = options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string[] held = [.. root.EnumerateObject().Select(field => field.Name)];
        string[] missing =
        [
            .. known.Where(member => member.Property.IsDefined(typeof(RequiredMemberAttribute), inherit: true)
                    && !held.Contains(member.Name, names))
                .Select(member => member.Name),
        ];
        if (missing.Length > 0)
        {
            return $"it lacks {(missing.Length == 1 ? "the field" : "the fields")} {string.Join(", ", missing)}";
        }

        string[] unknown = [.. held.Where(name => !known.Any(member => names.Equals(member.Name, name)))];
        if (unknown.Length > 0)
        {
            return $"it holds {(unknown.Length == 1 ? "a field" : "fields")} this version does not know: {string.Join(", ", unknown)}";
        }

        return exception.Path is { Length: > 1 } path
            ? $"its value at {path} is not of the kind that field holds"
            : "its fields are not of the kinds they hold";
    }
}
