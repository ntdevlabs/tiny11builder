using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Tiny11Builder.Core;

internal sealed record PendingScan(IReadOnlyList<string> Pending, int Packages, int Features);

internal static class PendingOperations
{
    private static readonly string[] MarkerFiles = [@"Windows\WinSxS\pending.xml", @"Windows\WinSxS\reboot.xml"];

    [SupportedOSPlatform("windows")]
    public static async Task<PendingScan> ScanAsync(DismClient dism, string mount, CancellationToken token)
    {
        var markers = MarkerFiles.Where(file => ImageFileAccess.Exists(Path.Combine(mount, file))).Select(file => file + " exists").ToArray();
        if (markers.Length > 0) return new(markers, 0, 0);
        var packages = Read(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Packages").ConfigureAwait(false),
            "Package Identity", ImageItemKind.Package);
        var features = Read(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false),
            "Feature Name", ImageItemKind.Feature);
        var pending = packages.Concat(features).Where(item => IsPending(item.Detail))
            .Select(item => $"{(item.Kind == ImageItemKind.Package ? "package" : "feature")} {item.Identity}: {item.Detail}").ToArray();
        return new(pending, packages.Count, features.Count);
    }

    public static bool IsPending(string state) =>
        Regex.Replace(state, @"\s+", " ").Trim().EndsWith("pending", StringComparison.OrdinalIgnoreCase);

    public static string Refusal(BuildRecipe recipe) => (recipe.Flavor == BuildFlavor.Core ?
        "Core stopped before trimming any component: the image has pending operations, and Core would delete the files needed to finish them at first boot. " :
        "Component payloads were not removed: the image has pending operations, and removing payloads would delete the files needed to finish them at first boot. ") +
        "Build without .NET 3.5, or build a serviceable Tiny11, which keeps those files. Pending actions are never reverted.";

    private static IReadOnlyList<ImageInventoryItem> Read(string output, string field, ImageItemKind kind)
    {
        var listed = Regex.Matches(output, $@"(?m)^[ \t]*{Regex.Escape(field)}[ \t]*:[ \t]*\S").Count;
        var items = ImageInventory.ParseIdentityStates(output, field, kind);
        if (items.Count == 0 || items.Count != listed)
            throw new InvalidDataException($"DISM returned an unreadable {(kind == ImageItemKind.Package ? "package" : "feature")} listing, so pending operations cannot be ruled out.");
        return items;
    }
}
