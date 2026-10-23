using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Tiny11Builder.App;
using Xunit;

namespace Tiny11Builder.Core.Tests;

internal static class LocalizationTestHost
{
    [ModuleInitializer]
    internal static void Initialize() => Loc.Use(EnglishResources.Get);
}

internal static class EnglishResources
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Strings = new(() => Read("en-US"));

    public static string? Get(string key) => Strings.Value.TryGetValue(key, out var text) ? text : null;

    public static IReadOnlyDictionary<string, string> Read(string language)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Strings", language, "Resources.resw");
        return XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")?.Value ?? "");
    }

    public static IEnumerable<string> Languages =>
        Directory.EnumerateDirectories(Path.Combine(AppContext.BaseDirectory, "Strings"))
            .Select(Path.GetFileName).OfType<string>();
}

public sealed class LocalizationTests
{
    [Fact]
    public void Every_language_has_the_same_nonempty_keys_and_placeholders()
    {
        var english = EnglishResources.Read("en-US");
        Assert.NotEmpty(english);
        Assert.All(english, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));

        foreach (var language in EnglishResources.Languages.Where(language => language != "en-US"))
        {
            var translated = EnglishResources.Read(language);
            Assert.Equal(english.Keys.OrderBy(key => key), translated.Keys.OrderBy(key => key));
            foreach (var (key, value) in translated)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{language}: {key}");
                Assert.Equal(Placeholders(english[key]), Placeholders(value));
            }
        }
    }

    [Fact]
    public void Every_key_used_by_xaml_exists()
    {
        var english = EnglishResources.Read("en-US");
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Deployment", "MainWindow.xaml"));
        var keys = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{loc:Tr Key=([^}]+)\}")
            .Select(match => match.Groups[1].Value).Distinct().ToArray();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.True(english.ContainsKey(key), key));
    }

    [Fact]
    public void Language_tags_normalize_to_a_shipped_resource_folder()
    {
        Assert.Equal("", LanguageCatalog.Normalize(null));
        Assert.Equal("", LanguageCatalog.Normalize("system"));
        Assert.Equal("zh-Hant", LanguageCatalog.Normalize("zh-TW"));
        Assert.Equal("zh-Hant", LanguageCatalog.Normalize("zh-HK"));
        Assert.Equal("zh-Hans", LanguageCatalog.Normalize("zh-CN"));
        Assert.Equal("en-US", LanguageCatalog.Normalize("en"));
        Assert.All(LanguageCatalog.All.Where(language => language.Tag.Length > 0),
            language => Assert.True(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Strings", language.Tag)), language.Tag));
    }

    private static string Placeholders(string text) =>
        string.Join(",", System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}")
            .Select(match => match.Value).OrderBy(value => value));
}
