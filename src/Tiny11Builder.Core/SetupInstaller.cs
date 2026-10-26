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

        // Start the boot image's own setup bootstrap, and let it find the media, instead of starting a fixed path
        // inside the boot RAM disk.
        //
        // boot.wim is loaded to X:, but the installation media is not there. From a USB stick it is another drive;
        // under Ventoy it is not a drive at all until vtoyjump.exe has mounted the ISO as a virtual one, and Ventoy
        // runs that hook from the same WinPE launch chain this file configures. Setup started as
        // X:\sources\setup.exe takes the directory it was loaded from as the media root, finds no install.wim beside
        // it, and stops with "A media driver your computer needs is missing" - which reads like a missing storage
        // driver but only means Setup never saw its media. setup.exe at the root of the boot image is the stock
        // bootstrap: it locates the media and then runs that media's matching sources\setup.exe, which is the classic
        // installer this method exists to select. Only when the image ships no bootstrap does the fixed path remain.
        var launch = "%SYSTEMDRIVE%\\" + executable;
        var bootstrap = Path.Combine(mount, "setup.exe");
        if (File.Exists(bootstrap))
        {
            ImageFileAccess.ValidateFile(bootstrap);
            if (new FileInfo(bootstrap).Length == 0)
                throw new InvalidDataException("The Setup bootstrap in the Windows Setup image is empty.");
            launch = @"%SYSTEMDRIVE%\setup.exe";
        }
        var path = Path.Combine(mount, @"Windows\System32\winpeshl.ini");
        PathSafety.RejectReparsePoints(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            using var privileges = new ImageOperations.PrivilegeScope();
            ImageFileAccess.DeleteEntry(path);
        }
        var content = "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\wpeinit.exe\r\n" + launch + "\r\n";
        File.WriteAllText(path, content, Encoding.ASCII);
        if (File.ReadAllText(path) != content) throw new IOException("The legacy Windows Setup startup configuration could not be verified.");
        log($"Legacy installer enabled: {launch}. Required by Core/component-store removals.");
    }
}
