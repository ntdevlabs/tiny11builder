using System.Text.RegularExpressions;

namespace Tiny11Builder.Core;

public static class ImageInventory
{
    public static async Task<IReadOnlyList<ImageInventoryItem>> ReadAsync(DismClient dism, IImageOperations native,
        string mount, CancellationToken token)
    {
        var items = new List<ImageInventoryItem>();
        var apps = DismClient.ParseApps(await dism.RunAsync(token, $"/Image:{mount}", "/Get-ProvisionedAppxPackages").ConfigureAwait(false));
        foreach (var app in apps)
            items.Add(new(ImageItemKind.ProvisionedApp, app.PackageName, app.DisplayName,
                !CleanupCatalog.Options.Any(o => CleanupCatalog.MatchesApp(o, app))));
        var features = DismClient.ParseFeatures(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false));
        foreach (var feature in features) items.Add(new(ImageItemKind.Feature, feature.Key, feature.Value, true));
        var capabilities = await dism.RunAsync(token, $"/Image:{mount}", "/Get-Capabilities").ConfigureAwait(false);
        items.AddRange(ParseIdentityStates(capabilities, "Capability Identity", ImageItemKind.Capability));
        var packages = await dism.RunAsync(token, $"/Image:{mount}", "/Get-Packages").ConfigureAwait(false);
        items.AddRange(ParseIdentityStates(packages, "Package Identity", ImageItemKind.Package));
        items.AddRange(native.ReadNativeInventory(mount));
        return items.OrderBy(i => i.Kind).ThenBy(i => i.Identity, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<ImageInventoryItem> ParseIdentityStates(string text, string field, ImageItemKind kind)
    {
        var items = new List<ImageInventoryItem>();
        string? name = null;
        foreach (var line in text.Split('\n'))
        {
            var identity = Regex.Match(line, $@"^[ \t]*{Regex.Escape(field)}[ \t]*:[ \t]*(.+?)[ \t\r]*$");
            if (identity.Success) name = identity.Groups[1].Value;
            var state = Regex.Match(line, @"^[ \t]*State[ \t]*:[ \t]*(.+?)[ \t\r]*$");
            if (!state.Success || name is null) continue;
            items.Add(new(kind, name, state.Groups[1].Value, true));
            name = null;
        }
        return items;
    }

    public static void ValidateActions(IReadOnlyList<ExpertAction> actions, IReadOnlyList<ImageInventoryItem> items)
    {
        foreach (var action in actions)
            if (!items.Any(i => i.Kind == action.Kind && i.Identity.Equals(action.Identity, StringComparison.Ordinal)))
                throw new InvalidDataException($"The custom action is not in the selected image's current inventory: {action.Kind} / {action.Identity}");
    }
}
