using System.Runtime.Versioning;

namespace Tiny11Builder.Core;

public static class FilePayloadCatalog
{
    private static readonly string[] Folders =
    [
        @"Windows\SysWOW64", @"Windows\Microsoft.NET", @"Windows\assembly", @"Windows\IME",
        @"Windows\Speech", @"Windows\Speech_OneCore", @"Windows\OCR", @"Windows\Web",
        @"Windows\Help", @"Windows\Media", @"Windows\Cursors", @"Windows\System32\WindowsPowerShell",
        @"Windows\System32\spool", @"Windows\System32\Recovery", @"Windows\System32\InputMethod",
        @"Program Files (x86)\Common Files", @"Program Files\Common Files"
    ];

    public static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('/') || path.Contains(':') || Path.IsPathRooted(path))
            return false;
        var parts = path.Split('\\');
        if (parts.Length < 2 || parts.Any(part => part is "" or "." or ".." || part != part.TrimEnd(' ', '.') ||
            part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return false;
        if (!new[] { "Windows", "Program Files", "Program Files (x86)", "ProgramData" }
                .Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return false;
        // The mount ownership marker and offline registry edits must remain valid throughout the build.
        return !path.Equals(@"Windows\System32", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals(@"Windows\System32\config", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(@"Windows\System32\config\", StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<ImageInventoryItem> Read(string mount)
    {
        var result = new List<ImageInventoryItem>();
        foreach (var relative in Folders)
            if (Directory.Exists(Path.Combine(mount, relative)))
                result.Add(new(ImageItemKind.FilePayload, relative,
                    "Folder and all children. Direct removal bypasses servicing and can break Windows, input or applications.", true));
        foreach (var (relative, directories) in new[]
        {
            (@"Windows\System32", false), (@"Windows\System32\drivers", false),
            (@"Windows\Fonts", false), (@"Windows\SystemResources", false),
            (@"Windows\System32\DriverStore\FileRepository", true),
            (@"Windows\assembly", true), (@"Windows\WinSxS\Manifests", false)
        })
        {
            var folder = Path.Combine(mount, relative);
            if (!Directory.Exists(folder)) continue;
            PathSafety.RejectReparsePoints(folder);
            var entries = directories ? Directory.EnumerateDirectories(folder) : Directory.EnumerateFiles(folder);
            foreach (var file in entries)
            {
                var path = Path.GetRelativePath(mount, file);
                if (!IsValidPath(path)) continue;
                ImageFileAccess.ValidateFile(file);
                var detail = directories ? "Folder and all children." :
                    $"Logical file size: {new FileInfo(file).Length:N0} bytes; hard links may share data.";
                result.Add(new(ImageItemKind.FilePayload, path,
                    detail + " Direct removal can break boot, drivers, rendering, languages or applications.", true));
            }
        }
        foreach (var relative in new[] { @"Windows\SystemApps", @"Program Files\WindowsApps" })
        {
            var root = Path.Combine(mount, relative);
            if (!Directory.Exists(root)) continue;
            PathSafety.RejectReparsePoints(root);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    var path = Path.GetRelativePath(mount, entry);
                    if (!IsValidPath(path)) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        pending.Push(entry);
                        result.Add(new(ImageItemKind.FilePayload, path, "Folder and all children; removing packaged resources or frameworks can break the shell and applications.", true));
                    }
                    else
                    {
                        ImageFileAccess.ValidateFile(entry);
                        result.Add(new(ImageItemKind.FilePayload, path,
                            $"Logical file size: {new FileInfo(entry).Length:N0} bytes; package/framework dependency, not automatically verified.", true));
                    }
                }
        }
        return result.DistinctBy(item => item.Identity, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
