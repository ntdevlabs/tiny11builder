using System.Text;

namespace Tiny11Builder.Core;

public static class SetupInstaller
{
    public static bool RequiresLegacy(BuildRecipe recipe) => recipe.Flavor == BuildFlavor.Core || recipe.SelectedOptions.Any(NativePayloadOptions.IsSelected) ||
        recipe.ExpertActions.Any(action => action.Kind is ImageItemKind.ComponentPayload or ImageItemKind.FilePayload && action.Mode == ExpertActionMode.Remove);

    internal static bool RequiresBootEdit(BuildRecipe recipe) =>
        recipe.ShrinkBootImage || recipe.SelectedOptions.Contains("hardware-bypass") || RequiresLegacy(recipe);

    internal static async Task ExportSmallerBootImageAsync(DismClient dism, string source, WindowsImage setup,
        string destination, CancellationToken token)
    {
        PathSafety.RejectReparsePoints(source);
        PathSafety.RejectReparsePoints(destination);
        if (File.Exists(destination)) throw new IOException("The reduced boot image destination already exists.");
        await dism.RunAsync(token, "/Export-Image", $"/SourceImageFile:{source}", $"/SourceIndex:{setup.Index}",
            $"/DestinationImageFile:{destination}", "/Compress:max", "/Bootable", "/CheckIntegrity").ConfigureAwait(false);
        Digest.RequireCompleteWim(destination, "The rebuilt stock Setup image");
        var images = await dism.GetImagesAsync(destination, CancellationToken.None).ConfigureAwait(false);
        if (images.Count != 1 || !WindowsImageIdentity.Matches(images[0], setup))
            throw new InvalidDataException("The rebuilt stock Setup image changed version, architecture or identity.");
        using var stream = File.OpenRead(destination);
        stream.Position = 120;
        var bootIndex = new byte[4];
        stream.ReadExactly(bootIndex);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bootIndex) != 1)
            throw new InvalidDataException("The rebuilt stock boot.wim is not marked bootable at index 1.");
    }

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
