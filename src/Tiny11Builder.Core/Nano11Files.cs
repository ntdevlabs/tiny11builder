namespace Tiny11Builder.Core;

/// <summary>
/// The Nano11 file half: payloads, driver-store folders, fonts, scheduled tasks and service keys. It is separate from
/// the engine stage so it runs against a test mount, exactly like the Core and payload paths it mirrors.
/// </summary>
internal static class Nano11Files
{
    public static void Apply(string mount, Action<string> log, bool turboMode = false)
    {
        foreach (var relative in Nano11Plan.Payloads) RemovePayload(mount, relative, log, turboMode);
        // The recovery image is replaced by an empty placeholder, as nano11.ps1 does: a missing file makes WinRE look
        // broken to Setup and Windows, while an empty one reports that recovery is unavailable.
        var recovery = Path.Combine(mount, @"Windows\System32\Recovery\winre.wim");
        if (!ImageFileAccess.Exists(recovery))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
                using var placeholder = File.Create(recovery);
                log(@"Nano11: created an empty Recovery\winre.wim placeholder so recovery reports as unavailable.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log($@"Nano11 could not create the empty Recovery\winre.wim placeholder: {error.Message}");
            }
        }
        RemoveMatching(mount, @"Windows\System32\DriverStore\FileRepository", "driver package", log, turboMode,
            name => Nano11Plan.DriverPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                    Nano11Plan.DriverNames.Any(driver => name.StartsWith(driver, StringComparison.OrdinalIgnoreCase)));
        RemoveMatching(mount, @"Windows\assembly", "precompiled .NET assembly", log, turboMode,
            name => name.StartsWith("NativeImages_", StringComparison.OrdinalIgnoreCase));
        var fonts = Path.Combine(mount, @"Windows\Fonts");
        if (!Directory.Exists(fonts)) log(@"Nano11: no Windows\Fonts folder in this image; fonts were left as they are.");
        else
            foreach (var file in Directory.EnumerateFiles(fonts).ToArray())
            {
                var name = Path.GetFileName(file);
                if (Nano11Plan.KeptFontPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
                RemovePayload(mount, Path.GetRelativePath(mount, file), log, turboMode);
            }
        var tasks = Path.Combine(mount, @"Windows\System32\Tasks");
        foreach (var relative in Nano11Plan.TaskFiles)
        {
            var path = Path.GetFullPath(Path.Combine(tasks, relative));
            if (!PathSafety.IsInside(path, mount)) throw new InvalidDataException("A Nano11 scheduled-task path escaped the offline mount.");
            RemovePayload(mount, Path.GetRelativePath(mount, path), log, turboMode);
        }
        log("Nano11: payload, font, driver-store and scheduled-task removals finished.");
    }

    /// <summary>Deletes the named services from every control set. Audio services are never in the plan.</summary>
    public static void RemoveServices(string mount, Action<string> log)
    {
        using var privileges = new ImageOperations.PrivilegeScope();
        using var hive = OfflineRegistry.Load(Path.Combine(mount, @"Windows\System32\config\SYSTEM"));
        var root = hive.Root;
        var removed = 0;
        foreach (var control in root.GetSubKeyNames().Where(key => System.Text.RegularExpressions.Regex.IsMatch(key, @"^ControlSet\d{3}$")))
        {
            using var services = OfflineRegistry.OpenWriteKey(root, control + @"\Services");
            foreach (var name in Nano11Plan.RemovedServices)
            {
                if (!services.GetSubKeyNames().Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    log($@"Nano11: not present in {control}: service {name}");
                    continue;
                }
                OfflineRegistry.DeleteKeyTree(services, name);
                removed++;
                log($@"Nano11: removed service {control}\{name}");
            }
        }
        log(removed == 0 ? "Nano11: no named service existed in this image to remove." :
            $"Nano11: {removed} service definition(s) removed. Audio services are kept: they are a boot dependency.");
    }

    private static void RemoveMatching(string mount, string relativeRoot, string what, Action<string> log, bool turboMode,
        Func<string, bool> match)
    {
        var root = Path.Combine(mount, relativeRoot);
        if (!Directory.Exists(root)) { log($@"Nano11: no {relativeRoot} in this image; no {what} was removed."); return; }
        foreach (var path in Directory.EnumerateDirectories(root).ToArray())
            if (match(Path.GetFileName(path))) RemovePayload(mount, Path.GetRelativePath(mount, path), log, turboMode);
    }

    private static void RemovePayload(string mount, string relative, Action<string> log, bool turboMode)
    {
        var path = Path.GetFullPath(Path.Combine(mount, relative));
        if (!PathSafety.IsInside(path, mount)) throw new InvalidDataException("A Nano11 payload path escaped the offline mount.");
        if (!ImageFileAccess.Exists(path)) { log($@"Nano11: not present: {relative}"); return; }
        PathSafety.RejectReparsePoints(Path.GetDirectoryName(path)!);
        try
        {
            ImageOperations.DeleteTree(path, turboMode);
            log($@"Nano11: removed {relative}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A payload held by the offline servicing stack is reported, never claimed as removed.
            log($"Nano11 could not remove {relative}: {error.Message}");
        }
    }
}
