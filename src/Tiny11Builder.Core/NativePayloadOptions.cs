namespace Tiny11Builder.Core;

internal static class NativePayloadOptions
{
    private static readonly IReadOnlyDictionary<string, string[]> Paths = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["remove-wow64"] = [@"Windows\SysWOW64", @"Windows\SysArm32", @"Program Files (x86)\Common Files"],
        ["remove-desktop-netfx"] = [@"Windows\Microsoft.NET", @"Windows\assembly", @"Program Files\Reference Assemblies",
            @"Program Files (x86)\Reference Assemblies"],
        ["remove-ime-files"] = [@"Windows\IME", @"Windows\System32\IME", @"Windows\System32\InputMethod"],
        ["remove-speech-files"] = [@"Windows\Speech", @"Windows\Speech_OneCore", @"Windows\OCR"],
        ["remove-print-drivers"] = [],
        ["remove-extra-media"] = [@"Windows\Web", @"Windows\Media", @"Windows\Help", @"Windows\Cursors"]
    };

    public static CleanupOption Option(string id, string title, string description) =>
        new(id, title, description, "Advanced / original builder", OptionKind.File, false, false, Paths[id]) { CustomOnly = true };

    public static bool IsSelected(string id) => Paths.ContainsKey(id);

    public static IReadOnlyList<string> Resolve(string mount, IReadOnlyList<string> selected)
    {
        var result = selected.Where(Paths.ContainsKey).SelectMany(id => Paths[id]).ToList();
        if (selected.Contains("remove-print-drivers"))
        {
            var root = Path.Combine(mount, @"Windows\System32\DriverStore\FileRepository");
            if (Directory.Exists(root))
            {
                PathSafety.RejectReparsePoints(root);
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(directory);
                    if (new[] { "prn", "scan", "mfd" }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        result.Add(Path.GetRelativePath(mount, directory));
                }
            }
        }
        return result;
    }
}
