namespace Tiny11Builder.Core;

/// <summary>
/// WinRT-free localization helper. The app supplies a provider that resolves a key through the
/// platform resource loader; when none is set (unit tests, or before startup) the English fallback
/// is returned so a string is never blank. Keeping this type free of Windows Runtime dependencies
/// lets the same source compile and run under the unit-test project.
/// </summary>
public static class L10n
{
    /// <summary>
    /// Resolves <paramref name="key"/> to the active language, or <paramref name="english"/> when no
    /// provider is registered or the key is not translated. Set once at app startup.
    /// </summary>
    public static Func<string, string, string>? Provider;

    public static string T(string key, string english) =>
        Provider is null ? english : Provider(key, english) ?? english;
}
