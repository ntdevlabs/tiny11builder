using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tiny11Builder.App;

internal sealed record LanguageInfo(string Id, string Name);
internal sealed record LanguageFileProblem(string FileName, string Message);

internal sealed class LanguageCatalog
{
    private static readonly Regex LanguageId = new("^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, string> strings;
    private readonly LanguageCatalog? english;

    private LanguageCatalog(LanguageInfo info, Dictionary<string, string> strings, LanguageCatalog? english)
    {
        Info = info;
        this.strings = new ReadOnlyDictionary<string, string>(strings);
        this.english = english;
        MissingKeys = english is null ? [] : english.Keys.Where(key => !strings.ContainsKey(key)).ToArray();
    }

    public LanguageInfo Info { get; }
    public IEnumerable<string> Keys => strings.Keys;
    public IReadOnlyList<string> MissingKeys { get; }
    public bool Contains(string key) => strings.ContainsKey(key);
    public string Get(string key) => strings.TryGetValue(key, out var text) ? text : english?.Get(key) ?? $"[{key}]";
    public string GetOrEnglish(string key, string fallback) => strings.TryGetValue(key, out var text) ? text :
        english?.Contains(key) == true ? english.Get(key) : fallback;
    public string Format(string key, params object?[] arguments) => string.Format(Culture, Get(key), arguments);
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Info.Id);

    public static LanguageCatalog Parse(string json, LanguageCatalog? english = null)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A language file must be a JSON object.");
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!properties.Add(property.Name) || property.Name is not ("language" or "name" or "strings"))
                throw new InvalidDataException($"Unknown or duplicate language property: {property.Name}");
        var id = RequiredText(root, "language");
        var name = RequiredText(root, "name");
        if (id.Length > 80 || !LanguageId.IsMatch(id) || name.Length > 100) throw new InvalidDataException("Invalid language identifier or display name.");
        try { _ = CultureInfo.GetCultureInfo(id); }
        catch (CultureNotFoundException error) { throw new InvalidDataException("Unsupported language identifier.", error); }
        if (english is null && id != "en") throw new InvalidDataException("The baseline language must be English (en).");
        if (!root.TryGetProperty("strings", out var entries) || entries.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A language file needs a strings object.");
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.Value.GetString()))
                throw new InvalidDataException($"A translation must not be empty: {entry.Name}");
            var text = entry.Value.GetString()!;
            if (entry.Name.Length is 0 or > 180 || text.Length > 16000 || !strings.TryAdd(entry.Name, text))
                throw new InvalidDataException($"Invalid or duplicate translation key: {entry.Name}");
            if (english is not null && !english.Contains(entry.Name))
                throw new InvalidDataException($"Unknown translation key: {entry.Name}");
            try
            {
                var signature = Placeholders(text);
                if (english is not null && signature != Placeholders(english.Get(entry.Name)))
                    throw new InvalidDataException($"Format placeholders differ from English: {entry.Name}");
            }
            catch (FormatException error) { throw new InvalidDataException($"Invalid format placeholders: {entry.Name}", error); }
        }
        if (strings.Count == 0) throw new InvalidDataException("The language file has no strings.");
        return new(new(id, name), strings, english);
    }

    private static string RequiredText(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
            throw new InvalidDataException($"Missing language property: {property}");

    private static string Placeholders(string text)
    {
        _ = CompositeFormat.Parse(text);
        var placeholders = new List<string>();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '{') continue;
            if (index + 1 < text.Length && text[index + 1] == '{') { index++; continue; }
            var end = text.IndexOf('}', index + 1);
            if (end < 0) throw new FormatException("Unclosed placeholder.");
            // Keep argument numbers, format specifiers and alignment unchanged; translators may reorder them.
            placeholders.Add(text[(index + 1)..end]);
            index = end;
        }
        placeholders.Sort(StringComparer.Ordinal);
        return string.Join("\n", placeholders);
    }
}

internal sealed class LanguageCatalogs
{
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private readonly Dictionary<string, LanguageCatalog> catalogs;
    public static LanguageCatalog English { get; } = ReadEnglish();

    private LanguageCatalogs(Dictionary<string, LanguageCatalog> catalogs, IReadOnlyList<LanguageFileProblem> problems)
    {
        this.catalogs = catalogs;
        Problems = problems;
        Available = catalogs.Values.OrderBy(catalog => catalog.Info.Id == "en" ? 0 : 1)
            .ThenBy(catalog => catalog.Info.Name, StringComparer.Ordinal).Select(catalog => catalog.Info).ToArray();
    }

    public IReadOnlyList<LanguageInfo> Available { get; }
    public IReadOnlyList<LanguageFileProblem> Problems { get; }

    public static LanguageCatalogs Load(string directory)
    {
        var catalogs = new Dictionary<string, LanguageCatalog>(StringComparer.OrdinalIgnoreCase) { ["en"] = English };
        var problems = new List<LanguageFileProblem>();
        try
        {
            if (!Directory.Exists(directory)) return new(catalogs, problems);
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                try
                {
                    if (new FileInfo(file).Length > MaximumFileBytes) throw new InvalidDataException("The language file is too large.");
                    var catalog = LanguageCatalog.Parse(File.ReadAllText(file, Encoding.UTF8), English);
                    if (!Path.GetFileNameWithoutExtension(file).Equals(catalog.Info.Id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The file name must match its language identifier.");
                    if (catalogs.ContainsKey(catalog.Info.Id) && catalog.Info.Id != "en")
                        throw new InvalidDataException("Duplicate language identifier.");
                    catalogs[catalog.Info.Id] = catalog;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                { problems.Add(new(Path.GetFileName(file), error.Message)); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { problems.Add(new(Path.GetFileName(directory), error.Message)); }
        return new(catalogs, problems);
    }

    public LanguageCatalog Resolve(string? preference, CultureInfo systemCulture)
    {
        if (preference is not (null or "system") && catalogs.TryGetValue(preference, out var chosen)) return chosen;
        for (var culture = systemCulture; culture.Name.Length > 0; culture = culture.Parent)
            if (catalogs.TryGetValue(culture.Name, out var found)) return found;
        return catalogs.Values.OrderBy(catalog => catalog.Info.Id, StringComparer.Ordinal)
            .FirstOrDefault(catalog => catalog.Culture.TwoLetterISOLanguageName == systemCulture.TwoLetterISOLanguageName) ?? catalogs["en"];
    }

    public bool IsAvailable(string preference) => preference == "system" || catalogs.ContainsKey(preference);

    private static LanguageCatalog ReadEnglish()
    {
        using var input = typeof(LanguageCatalogs).Assembly.GetManifestResourceStream("Tiny11Builder.App.Languages.en.json")
            ?? throw new InvalidDataException("The built-in English language catalog is missing.");
        using var reader = new StreamReader(input, Encoding.UTF8);
        return LanguageCatalog.Parse(reader.ReadToEnd());
    }
}

internal sealed record LanguagePreference(string Language)
{
    public static LanguagePreference Read(string path)
    {
        if (!File.Exists(path)) return new("system");
        var saved = JsonSerializer.Deserialize<LanguagePreference>(File.ReadAllText(path));
        return saved is { Language.Length: > 0 and <= 80 } ? saved : throw new InvalidDataException("Invalid saved interface language.");
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this), new UTF8Encoding(false));
    }
}

internal static class UiText
{
    private static LanguageCatalog current = LanguageCatalogs.English;
    private static readonly IReadOnlyDictionary<string, string> EnglishKeys = LanguageCatalogs.English.Keys
        .GroupBy(key => LanguageCatalogs.English.Get(key), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    public static event Action? Changed;
    public static LanguageCatalog Current => current;
    public static string Get(string key) => current.Get(key);
    public static string GetOrEnglish(string key, string fallback) => current.GetOrEnglish(key, fallback);
    public static string Format(string key, params object?[] arguments) => current.Format(key, arguments);
    public static string FromEnglish(string text) => EnglishKeys.TryGetValue(text, out var key) ? current.Get(key) : text;
    public static string Relocalize(LanguageCatalog previous, string text)
    {
        var key = LanguageCatalogs.English.Keys.FirstOrDefault(candidate => previous.Get(candidate) == text);
        return key is null ? FromEnglish(text) : current.Get(key);
    }

    public static void Select(LanguageCatalog catalog)
    {
        current = catalog;
        Changed?.Invoke();
    }
}
