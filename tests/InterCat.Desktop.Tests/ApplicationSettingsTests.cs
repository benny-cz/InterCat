using System.Text.Json.Nodes;
using InterCat.Desktop.Settings;
using InterCat.Desktop.Theme;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §26.3's application settings file (`contracts/app-settings-v1.md`): hand-editable and versioned, an unknown key kept
/// and reported, never dropped, and nothing the user wrote overwritten unread.
/// </summary>
public sealed class ApplicationSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "InterCat.Desktop.Tests.Settings", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "§26.3: with no settings file the defaults stand, and a choice is written as a versioned, readable file")]
    public void AChoiceIsWrittenAsAVersionedFile()
    {
        var store = new ApplicationSettingsStore(SettingsPath);
        Assert.Equal(ApplicationSettings.Default, store.Load());
        Assert.False(File.Exists(SettingsPath));

        ApplicationSettings saved = store.SaveTheme(ThemeMode.Light);

        Assert.Equal(ThemeMode.Light, saved.Theme);
        Assert.Empty(saved.Notices);
        JsonObject written = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(SettingsPath)));
        Assert.Equal("app-settings-v1", (string?)written["contract"]);
        Assert.Equal("light", (string?)written["themeMode"]);
        Assert.Equal(ThemeMode.Light, new ApplicationSettingsStore(SettingsPath).Load().Theme);

        Assert.Null(store.SaveTheme(null).Theme);
        Assert.Equal("system", (string?)JsonNode.Parse(File.ReadAllText(SettingsPath))!["themeMode"]);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact(DisplayName = "§26.3: an unknown key is kept as written and reported, and a value not understood is reported and left")]
    public void UnknownKeysAreKeptAndReported()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, """
            {
              "contract": "app-settings-v1",
              "themeMode": "purple",
              "units": { "bytes": "binary" }
            }
            """);
        var store = new ApplicationSettingsStore(SettingsPath);

        ApplicationSettings loaded = store.Load();
        Assert.Null(loaded.Theme);
        Assert.Contains(loaded.Notices, notice => notice.Contains("\"purple\"", StringComparison.Ordinal)
            && notice.Contains("system, dark, light, high-contrast-dark, high-contrast-light", StringComparison.Ordinal));
        Assert.Contains(loaded.Notices, notice => notice.Contains("\"units\"", StringComparison.Ordinal)
            && notice.Contains("kept as written", StringComparison.Ordinal));

        // A choice replaces only its own key; the one this build does not know survives, still reported.
        ApplicationSettings saved = store.SaveTheme(ThemeMode.HighContrastDark);
        JsonObject written = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(SettingsPath)));
        Assert.Equal("high-contrast-dark", (string?)written["themeMode"]);
        Assert.Equal("binary", (string?)written["units"]!["bytes"]);
        Assert.Equal(ThemeMode.HighContrastDark, saved.Theme);
        Assert.Single(saved.Notices, notice => notice.Contains("\"units\"", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "§26.3: a settings file that cannot be read is kept aside when a choice is written, never overwritten")]
    public void AnUnreadableFileIsKeptAside()
    {
        Directory.CreateDirectory(directory);
        const string HandEdit = "{ \"themeMode\": \"dark\", ";
        File.WriteAllText(SettingsPath, HandEdit);
        var store = new ApplicationSettingsStore(SettingsPath);

        ApplicationSettings loaded = store.Load();
        Assert.Null(loaded.Theme);
        Assert.Contains("could not be read as JSON", Assert.Single(loaded.Notices), StringComparison.Ordinal);

        Assert.Equal(ThemeMode.Dark, store.SaveTheme(ThemeMode.Dark).Theme);
        string aside = Assert.Single(Directory.GetFiles(directory, "settings.json.unreadable-*"));
        Assert.Equal(HandEdit, File.ReadAllText(aside));

        File.WriteAllText(SettingsPath, "[1, 2]");
        Assert.Contains("does not hold a JSON object", Assert.Single(store.Load().Notices), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§26.3: a settings file another version wrote is neither applied nor overwritten")]
    public void AnotherVersionsFileIsLeftAlone()
    {
        Directory.CreateDirectory(directory);
        const string Newer = """{ "contract": "app-settings-v2", "themeMode": "light", "layout": "wide" }""";
        File.WriteAllText(SettingsPath, Newer);
        var store = new ApplicationSettingsStore(SettingsPath);

        ApplicationSettings loaded = store.Load();
        Assert.Null(loaded.Theme);
        Assert.Contains("app-settings-v2", Assert.Single(loaded.Notices), StringComparison.Ordinal);
        Assert.Contains("app-settings-v2", Assert.Throws<InvalidOperationException>(() => store.SaveTheme(ThemeMode.Light)).Message,
            StringComparison.Ordinal);
        Assert.Equal(Newer, File.ReadAllText(SettingsPath));
    }
}
