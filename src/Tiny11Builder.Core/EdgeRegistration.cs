using System.Runtime.Versioning;

namespace Tiny11Builder.Core;

[SupportedOSPlatform("windows")]
internal static class EdgeRegistration
{
    internal static void RemoveAfterFiles(string mount, Action<string> log)
    {
        ImageOperations.RequireOwnedMount(mount);
        foreach (var root in new[] { "Program Files", "Program Files (x86)" })
            if (ImageFileAccess.Exists(Path.Combine(mount, root, @"Microsoft\Edge")))
                throw new InvalidDataException("Edge files remain in the offline image. Its installed-program entry was not removed.");
        using var privileges = new ImageOperations.PrivilegeScope();
        using var software = OfflineRegistry.Load(Path.Combine(mount, @"Windows\System32\config\SOFTWARE"));
        (string Key, string Directory, string[] DisplayNames)[] candidates =
        [
            ("Microsoft Edge", "Edge", ["Microsoft Edge"]),
            ("Microsoft Edge Update", "EdgeUpdate", ["Microsoft Edge Update"]),
            ("Microsoft EdgeWebView", "EdgeWebView", ["Microsoft Edge WebView", "Microsoft Edge WebView2 Runtime"]),
            ("Microsoft Edge WebView", "EdgeWebView", ["Microsoft Edge WebView", "Microsoft Edge WebView2 Runtime"]),
            ("Microsoft Edge WebView2 Runtime", "EdgeWebView", ["Microsoft Edge WebView2 Runtime"])
        ];
        foreach (var path in new[]
        {
            @"Microsoft\Windows\CurrentVersion\Uninstall",
            @"WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        })
        {
            using var inventory = software.Root.OpenSubKey(path);
            if (inventory is null) continue;
            foreach (var candidate in candidates)
            {
                if (!inventory.GetSubKeyNames().Contains(candidate.Key, StringComparer.OrdinalIgnoreCase)) continue;
                var remains = new[] { "Program Files", "Program Files (x86)" }.Any(root =>
                    ImageFileAccess.Exists(Path.Combine(mount, root, "Microsoft", candidate.Directory)));
                if (candidate.Directory == "EdgeWebView")
                    remains |= ImageFileAccess.Exists(Path.Combine(mount, @"Windows\System32\Microsoft-Edge-Webview"));
                if (remains)
                {
                    log($"Retained offline uninstall entry because matching files remain: {path}\\{candidate.Key}");
                    continue;
                }
                using (var entry = inventory.OpenSubKey(candidate.Key)!)
                {
                    if (!candidate.DisplayNames.Contains(entry.GetValue("DisplayName") as string, StringComparer.OrdinalIgnoreCase) ||
                        !string.Equals(entry.GetValue("Publisher") as string, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                    {
                        log($"Retained offline uninstall entry with an unexpected identity: {path}\\{candidate.Key}");
                        continue;
                    }
                }
                using var parent = OfflineRegistry.OpenWriteKey(software.Root, path);
                OfflineRegistry.DeleteKeyTree(parent, candidate.Key);
                if (parent.GetSubKeyNames().Contains(candidate.Key, StringComparer.OrdinalIgnoreCase))
                    throw new IOException("The offline installed-program entry could not be removed: " + candidate.Key);
                log($"Removed offline Edge uninstall entry: {path}\\{candidate.Key}");
            }
        }
    }
}
