using System.Text;

namespace Tiny11Builder.Core;

public static class SetupInstaller
{
    public static bool RequiresLegacy(BuildRecipe recipe) => recipe.Flavor == BuildFlavor.Core ||
        recipe.ExpertActions.Any(action => action.Kind == ImageItemKind.ComponentPayload && action.Mode == ExpertActionMode.Remove);

    internal static bool RequiresBootEdit(BuildRecipe recipe) =>
        recipe.SelectedOptions.Contains("hardware-bypass") || RequiresLegacy(recipe);

    internal static void ApplyLegacy(string mount, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Setup customization requires Windows.");
        ImageOperations.RequireOwnedMount(mount);
        // The matching classic installer is still sources\setup.exe in 24H2+ media;
        // SetupHost.exe is the newer boot workflow. Do not borrow binaries from another release.
        var executable = new[] { @"sources\setup_old.exe", @"sources\setup.exe" }
            .FirstOrDefault(relative => File.Exists(Path.Combine(mount, relative))) ??
            throw new FileNotFoundException("This Windows Setup image has no legacy installer. Core/component-store removals require media containing sources\\setup.exe.");
        var program = Path.Combine(mount, executable);
        PathSafety.RejectReparsePoints(Path.GetDirectoryName(program)!);
        ImageFileAccess.ValidateFile(program);
        if (new FileInfo(program).Length == 0)
            throw new InvalidDataException("The legacy installer in the Windows Setup image is empty.");
        var path = Path.Combine(mount, @"Windows\System32\winpeshl.ini");
        PathSafety.RejectReparsePoints(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            using var privileges = new ImageOperations.PrivilegeScope();
            ImageFileAccess.DeleteEntry(path);
        }
        var content = "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\wpeinit.exe\r\n%SYSTEMDRIVE%\\" + executable + "\r\n";
        File.WriteAllText(path, content, Encoding.ASCII);
        if (File.ReadAllText(path) != content) throw new IOException("The legacy Windows Setup startup configuration could not be verified.");
        log($"Legacy installer enabled: %SYSTEMDRIVE%\\{executable}. Required by Core/component-store removals.");
    }
}
