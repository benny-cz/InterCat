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

    [Theory(DisplayName = "§26.3: every theme is written by its name and read back as itself")]
    [InlineData(null, "system")]
    [InlineData(ThemeMode.Dark, "dark")]
    [InlineData(ThemeMode.Light, "light")]
    [InlineData(ThemeMode.HighContrastDark, "high-contrast-dark")]
    [InlineData(ThemeMode.HighContrastLight, "high-contrast-light")]
    public void EveryThemeIsWrittenAndReadByItsName(ThemeMode? theme, string name)
    {
        Assert.Equal(theme, new ApplicationSettingsStore(SettingsPath).SaveTheme(theme).Theme);
        Assert.Equal(name, (string?)JsonNode.Parse(File.ReadAllText(SettingsPath))!["themeMode"]);
        ApplicationSettings loaded = new ApplicationSettingsStore(SettingsPath).Load();
        Assert.Equal(theme, loaded.Theme);
        Assert.Empty(loaded.Notices);
    }

    [Fact(DisplayName = "§26.3: a settings file larger than its bound is not read, the defaults stand, and it is said why")]
    public void ASettingsFilePastItsBoundIsNotRead()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, "{ \"contract\": \"app-settings-v1\", \"themeMode\": \"dark\", \"padding\": \""
            + new string(' ', 1024 * 1024) + "\" }");

        ApplicationSettings loaded = new ApplicationSettingsStore(SettingsPath).Load();
        Assert.Null(loaded.Theme);
        Assert.Contains(loaded.Notices, notice => notice == string.Create(System.Globalization.CultureInfo.CurrentCulture,
            $"The settings file is larger than {1024:N0} KiB, so it is not read."));
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
        // It says where the hand edit stopped being JSON, never the parser's own words.
        string notice = Assert.Single(loaded.Notices);
        Assert.Equal("The settings file could not be read: it is not valid JSON at line 1, character 24, so the defaults are used.", notice);

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

    [Fact(DisplayName = "§26.3: where the window closed is kept beside the theme and every other key, and read back")]
    public void WhereTheWindowClosedIsKept()
    {
        var store = new ApplicationSettingsStore(SettingsPath);
        Assert.Null(store.Load().Placement);
        var placement = new WindowPlacement(-1_200, 40, 1_366.5, 768, Maximized: true);

        Assert.True(store.SavePlacement(placement));
        JsonObject written = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(SettingsPath)));
        Assert.Equal("app-settings-v1", (string?)written["contract"]);
        Assert.Equal(-1_200, (int?)written["window"]!["x"]);
        Assert.Equal(1_366.5, (double?)written["window"]!["width"]);
        Assert.Equal(true, (bool?)written["window"]!["maximized"]);
        ApplicationSettings loaded = new ApplicationSettingsStore(SettingsPath).Load();
        Assert.Equal(placement, loaded.Placement);
        Assert.Empty(loaded.Notices);

        // A theme chosen keeps the window's place, and a place kept keeps the theme and a key this build does not know.
        Assert.Equal(placement, store.SaveTheme(ThemeMode.Light).Placement);
        written = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(SettingsPath)));
        written["units"] = "binary";
        File.WriteAllText(SettingsPath, written.ToJsonString());
        Assert.True(store.SavePlacement(placement with { X = 80, Maximized = false }));
        loaded = store.Load();
        Assert.Equal((ThemeMode.Light, placement with { X = 80, Maximized = false }), (loaded.Theme, loaded.Placement));
        Assert.Equal("binary", (string?)JsonNode.Parse(File.ReadAllText(SettingsPath))!["units"]);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory(DisplayName = "§26.3: a window's place the file states in a way this build does not read is reported, and the window opens where the system places it")]
    [InlineData("\"left\"")]
    [InlineData("{ \"x\": 10, \"y\": 20, \"width\": 800, \"height\": 600 }")]
    [InlineData("{ \"x\": 10.5, \"y\": 20, \"width\": 800, \"height\": 600, \"maximized\": false }")]
    [InlineData("{ \"x\": 10, \"y\": \"20\", \"width\": 800, \"height\": 600, \"maximized\": false }")]
    [InlineData("{ \"x\": 10, \"y\": 20, \"width\": 0, \"height\": 600, \"maximized\": false }")]
    [InlineData("{ \"x\": 10, \"y\": 20, \"width\": 800, \"height\": -1, \"maximized\": false }")]
    [InlineData("{ \"x\": 10, \"y\": 20, \"width\": 800, \"height\": 600, \"maximized\": \"no\" }")]
    [InlineData("{ \"x\": 10, \"y\": 20, \"width\": 800, \"height\": 600, \"maximized\": false, \"monitor\": 2 }")]
    public void AWindowPlaceNotReadIsReported(string window)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath,
            $$"""{ "contract": "app-settings-v1", "themeMode": "dark", "window": {{window}} }""");
        var store = new ApplicationSettingsStore(SettingsPath);

        ApplicationSettings loaded = store.Load();
        Assert.Null(loaded.Placement);
        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.StartsWith("The window's place ", Assert.Single(loaded.Notices), StringComparison.Ordinal);

        // Written again as the window closes, the theme beside it kept.
        var closedAt = new WindowPlacement(10, 20, 800, 600, Maximized: false);
        Assert.True(store.SavePlacement(closedAt));
        ApplicationSettings rewritten = store.Load();
        Assert.Equal((ThemeMode.Dark, closedAt), (rewritten.Theme, rewritten.Placement));
        Assert.Empty(rewritten.Notices);
    }

    [Fact(DisplayName = "§26.3: where the window closed is never written over a settings file that cannot be read, nor one another version wrote")]
    public void WhereTheWindowClosedLeavesOthersFilesAlone()
    {
        Directory.CreateDirectory(directory);
        var store = new ApplicationSettingsStore(SettingsPath);
        var placement = new WindowPlacement(10, 20, 1_200, 800, Maximized: false);
        string[] files = ["{ \"themeMode\": \"dark\", ", """{ "contract": "app-settings-v2", "layout": "wide" }"""];
        foreach (string file in files)
        {
            File.WriteAllText(SettingsPath, file);
            Assert.False(store.SavePlacement(placement));
            Assert.Equal(file, File.ReadAllText(SettingsPath));
            Assert.Single(Directory.GetFiles(directory));
        }
    }
}
