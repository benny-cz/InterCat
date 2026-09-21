using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterCat.Cli;

/// <summary>
/// One serializer for every machine-readable artifact. Output is locale independent, enums are written by
/// name, and large integers stay numeric only while they are consumer safe (section 20.5).
/// </summary>
internal static class JsonContracts
{
    public static JsonSerializerOptions Indented { get; } = Create(writeIndented: true);

    public static JsonSerializerOptions Compact { get; } = Create(writeIndented: false);

    private static JsonSerializerOptions Create(bool writeIndented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
