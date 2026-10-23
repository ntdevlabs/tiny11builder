using Microsoft.Windows.ApplicationModel.Resources;

namespace Tiny11Builder.App;

internal static class ResourceProvider
{
    // ResourceManager reads resources.pri beside the executable. A missing key returns null so Loc
    // can show the key itself, which is visible during development and caught by the resource tests.
    public static void Install()
    {
        var loader = new ResourceLoader();
        Loc.Use(key =>
        {
            try { return loader.GetString(key); }
            catch (Exception) { return null; }
        });
    }
}
