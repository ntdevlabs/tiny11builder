using System.Runtime.Versioning;

namespace Tiny11Builder.Core;

public interface IBuildHost
{
    void ValidateTools(BuildRecipe recipe);
    void ValidateImage(BuildRecipe recipe, WindowsImage image);

    /// <summary>
    /// Checks a host before a saved build is resumed. The default keeps older hosts working; <see cref="BuildHost"/>
    /// overrides it so a resume is not held to the free-space bar of a brand-new build.
    /// </summary>
    void ValidateResume(BuildRecipe recipe, WindowsImage image, long requiredWorkspaceBytes) => ValidateImage(recipe, image);
}

[SupportedOSPlatform("windows")]
public sealed class BuildHost : IBuildHost
{
    public void ValidateTools(BuildRecipe recipe)
    {
        if (!ToolLocator.IsAdministrator) throw new UnauthorizedAccessException("Image servicing requires administrator privileges.");
        ToolLocator.Validate(recipe.DismPath, "dism.exe");
        if (!string.IsNullOrEmpty(recipe.OscdimgPath)) ToolLocator.Validate(recipe.OscdimgPath, "oscdimg.exe");
    }

    public void ValidateImage(BuildRecipe recipe, WindowsImage image)
    {
        image.Validate();
        ToolLocator.EnsureServicingVersion(recipe.DismPath, image);
        var drive = new DriveInfo(Path.GetPathRoot(recipe.WorkspacePath)!);
        RequireNtfs(drive);
        var required = Math.Max(40L * 1024 * 1024 * 1024, image.Size * 3 + 10L * 1024 * 1024 * 1024);
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"The workspace needs at least {required / (1024 * 1024 * 1024)} GiB free for this image.");
        ValidateOutputDrive(recipe, drive);
    }

    public void ValidateResume(BuildRecipe recipe, WindowsImage image, long requiredWorkspaceBytes)
    {
        image.Validate();
        ToolLocator.EnsureServicingVersion(recipe.DismPath, image);
        var drive = new DriveInfo(Path.GetPathRoot(recipe.WorkspacePath)!);
        RequireNtfs(drive);
        if (drive.AvailableFreeSpace < requiredWorkspaceBytes)
            throw new IOException($"The workspace needs at least {(requiredWorkspaceBytes + (1L << 30) - 1) >> 30} GiB of free space to finish this build.");
        ValidateOutputDrive(recipe, drive);
    }

    private static void RequireNtfs(DriveInfo drive)
    {
        if (!drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The workspace must be on NTFS. WIM mounting on ReFS, FAT32 and network drives is not supported.");
    }

    private static void ValidateOutputDrive(BuildRecipe recipe, DriveInfo workspace)
    {
        if (string.IsNullOrEmpty(recipe.OutputPath)) return;
        var output = new DriveInfo(Path.GetPathRoot(recipe.OutputPath)!);
        if (output.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The output drive cannot be FAT32; Windows ISOs commonly exceed its 4 GiB limit.");
        if (!output.Name.Equals(workspace.Name, StringComparison.OrdinalIgnoreCase) &&
            output.AvailableFreeSpace < 12L * 1024 * 1024 * 1024)
            throw new IOException("The output drive needs at least 12 GiB free.");
    }
}
