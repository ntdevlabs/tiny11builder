namespace Tiny11Builder.App;

internal sealed record AppLanguage(string Tag, string NativeName)
{
    // The system choice has no native name of its own; it is the one label that follows the UI language.
    public string DisplayName => Tag.Length == 0 ? Loc.Get("Language__System") : NativeName;
}

internal static class LanguageCatalog
{
    // Tags match the Strings\<tag>\Resources.resw folders. English is the fallback and must stay first.
    public static IReadOnlyList<AppLanguage> All { get; } =
    [
        new("", ""),
        new("en-US", "English"),
        new("zh-Hant", "繁體中文"),
        new("zh-Hans", "简体中文"),
    ];

    public static string Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var exact = All.FirstOrDefault(language => language.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Tag;
        return tag.ToLowerInvariant() switch
        {
            "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" => "zh-Hant",
            "zh-cn" or "zh-sg" or "zh-hans" => "zh-Hans",
            "en" => "en-US",
            _ => ""
        };
    }

    public static AppLanguage For(string? tag)
    {
        var normalized = Normalize(tag);
        return All.First(language => language.Tag == normalized);
    }
}
