using Microsoft.Windows.ApplicationModel.Resources;
using Tiny11Builder.Core;
using Windows.Globalization;
using Windows.Storage;

namespace Tiny11Builder.App;

/// <summary>
/// Bridges the WinRT resource system to the WinRT-free <see cref="L10n"/> provider used across the
/// app and the shared logic, and remembers the user's language choice so it survives restarts.
/// </summary>
internal static class Localization
{
    private const string SettingsKey = "PreferredLanguage";

    private static readonly ResourceLoader Loader = new();

    /// <summary>The language choices offered in the sidebar, in display order.</summary>
    public static string[] Choices { get; } = ["System", "en-US", "zh-CN"];

    /// <summary>Currently active language override, or empty string for the system default.</summary>
    public static string Current
    {
        get => ApplicationLanguages.PrimaryLanguageOverride;
        set
        {
            ApplicationLanguages.PrimaryLanguageOverride = value;
            try
            {
                if (ApplicationData.Current is { } data)
                    data.LocalSettings.Values[SettingsKey] = value;
            }
            catch (Exception)
            {
                // Settings may be unavailable in some contexts; the override still applies for this run.
            }
        }
    }

    /// <summary>Call once at startup, before any window is created, so XAML x:Uid lookups use the saved language.</summary>
    public static void Initialize()
    {
        ApplySaved();
        L10n.Provider = (key, english) =>
        {
            try
            {
                var value = Loader.GetString(key);
                return string.IsNullOrWhiteSpace(value) ? english : value;
            }
            catch (Exception)
            {
                return english;
            }
        };
    }

    private static void ApplySaved()
    {
        try
        {
            if (ApplicationData.Current is not { } data) return;
            if (data.LocalSettings.Values.TryGetValue(SettingsKey, out var stored) &&
                stored is string language && language.Length > 0)
            {
                ApplicationLanguages.PrimaryLanguageOverride = language;
            }
        }
        catch (Exception)
        {
            // Ignore: fall back to the system language for this run.
        }
    }
}
