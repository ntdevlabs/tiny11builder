using System.Globalization;

namespace Tiny11Builder.App;

// UI text is resolved through this one seam. The app installs the Windows resource lookup at startup;
// the tests install the English resources, so the same code runs in both and no English copy is
// duplicated in C#.
internal static class Loc
{
    private static Func<string, string?>? lookup;

    public static void Use(Func<string, string?> resolver) => lookup = resolver;

    public static string Get(string key) =>
        lookup?.Invoke(key) is { Length: > 0 } text ? text : key;

    public static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);
}
