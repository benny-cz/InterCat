using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterCat.Desktop.Theme;

namespace InterCat.Desktop.Settings;

/// <summary>
/// The application's own settings, as §26.3's per-user configuration file holds them (`contracts/app-settings-v1.md`).
/// </summary>
/// <param name="Theme">A theme the user chose, or null to follow the operating system's setting (§26.2).</param>
/// <param name="Notices">What reading the file found worth saying: an unknown key kept, a value not understood.</param>
public sealed record ApplicationSettings(ThemeMode? Theme, IReadOnlyList<string> Notices)
{
    public static ApplicationSettings Default { get; } = new(null, []);
}

/// <summary>
/// Reads and writes the application settings file. It is documented, versioned and hand-editable: a key this build does
/// not know is kept as written and reported, never dropped, and a value it cannot read is reported and left alone. The
/// file is written only when the user changes a setting, and then atomically; one that cannot be read is kept aside
/// rather than overwritten, and one a newer build wrote is not changed at all.
/// </summary>
public sealed class ApplicationSettingsStore
{
    public const string ContractName = "app-settings-v1";

    private const long MaximumBytes = 1024 * 1024;
    private const string ContractKey = "contract";
    private const string ThemeKey = "themeMode";
    private static readonly JsonSerializerOptions Written = new() { WriteIndented = true };

    public ApplicationSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = System.IO.Path.GetFullPath(path);
    }

    /// <summary>Where the settings are kept, which the settings menu states.</summary>
    public string FilePath { get; }

    /// <summary>The user's own settings file: `%APPDATA%\InterCat\settings.json`, which follows a roaming profile.</summary>
    public static string DefaultPath()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(folder)
            ? throw new InvalidOperationException("Windows did not provide a per-user application-data directory.")
            : System.IO.Path.Combine(folder, "InterCat", "settings.json");
    }

    /// <summary>What the file says, with the defaults where it says nothing understood. Reads only; never throws.</summary>
    public ApplicationSettings Load()
    {
        (JsonObject? settings, string? problem) = Read();
        if (settings is null)
        {
            return problem is null ? ApplicationSettings.Default : new(null, [problem]);
        }

        var notices = new List<string>();
        if (Contract(settings) is { } contract && contract != ContractName)
        {
            return new(null, [$"The settings file was written as {contract}, which this InterCat does not read, so its "
                + "settings are not applied and the file is left as it is."]);
        }

        ThemeMode? theme = null;
        if (settings[ThemeKey] is { } value)
        {
            if (value is JsonValue text && text.TryGetValue(out string? name) && ThemeFromName(name) is { } known)
            {
                theme = known.Mode;
            }
            else
            {
                notices.Add($"The theme \"{value.ToJsonString()}\" is not one of {string.Join(", ", ThemeNames)}, so the "
                    + "system's theme is followed.");
            }
        }

        foreach ((string key, JsonNode? _) in settings)
        {
            if (key is not (ContractKey or ThemeKey))
            {
                notices.Add($"The setting \"{key}\" is not one this InterCat knows; it is kept as written.");
            }
        }

        return new(theme, notices);
    }

    /// <summary>
    /// Records the theme, or null to follow the system, keeping every other key the file holds. A file that could not be
    /// read is kept beside it first; one written as another version is refused rather than overwritten.
    /// </summary>
    public ApplicationSettings SaveTheme(ThemeMode? theme)
    {
        (JsonObject? settings, string? problem) = Read();
        if (settings is not null && Contract(settings) is { } contract && contract != ContractName)
        {
            throw new InvalidOperationException(
                $"The settings file was written as {contract}; this InterCat leaves it unchanged rather than lose what it holds.");
        }

        if (settings is null && problem is not null && File.Exists(FilePath))
        {
            // Hand edits that no longer parse are the user's; they are kept aside, not overwritten.
            string aside = FilePath + string.Create(CultureInfo.InvariantCulture, $".unreadable-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");
            File.Move(FilePath, aside, overwrite: false);
        }

        settings ??= [];
        settings[ContractKey] = ContractName;
        settings[ThemeKey] = NameOf(theme);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        string staged = FilePath + ".tmp";
        File.WriteAllText(staged, settings.ToJsonString(Written));
        File.Move(staged, FilePath, overwrite: true);
        return Load();
    }

    /// <summary>Every theme the file can name, "system" first.</summary>
    public static IReadOnlyList<string> ThemeNames { get; } =
        ["system", "dark", "light", "high-contrast-dark", "high-contrast-light"];

    internal static string NameOf(ThemeMode? theme) => theme switch
    {
        null => "system",
        ThemeMode.Dark => "dark",
        ThemeMode.Light => "light",
        ThemeMode.HighContrastDark => "high-contrast-dark",
        ThemeMode.HighContrastLight => "high-contrast-light",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Not a theme this InterCat draws."),
    };

    private static (ThemeMode? Mode, bool Known)? ThemeFromName(string? name) => name switch
    {
        "system" => (null, true),
        "dark" => (ThemeMode.Dark, true),
        "light" => (ThemeMode.Light, true),
        "high-contrast-dark" => (ThemeMode.HighContrastDark, true),
        "high-contrast-light" => (ThemeMode.HighContrastLight, true),
        _ => null,
    };

    private static string? Contract(JsonObject settings) =>
        settings[ContractKey] is JsonValue value && value.TryGetValue(out string? contract) ? contract : null;

    private (JsonObject? Settings, string? Problem) Read()
    {
        try
        {
            var file = new FileInfo(FilePath);
            if (!file.Exists)
            {
                return (null, null);
            }

            if (file.Length > MaximumBytes)
            {
                return (null, $"The settings file is larger than {MaximumBytes / 1024:N0} KiB, so it is not read.");
            }

            return JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject settings
                ? (settings, null)
                : (null, "The settings file does not hold a JSON object, so the defaults are used.");
        }
        catch (JsonException exception)
        {
            return (null, $"The settings file could not be read as JSON ({exception.Message}), so the defaults are used.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, $"The settings file could not be read ({exception.Message}), so the defaults are used.");
        }
    }
}
