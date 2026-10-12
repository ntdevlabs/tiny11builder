namespace Tiny11Builder.Core;

public static class PathSafety
{
    public static string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Use an absolute local path.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
            throw new InvalidDataException("Network paths and alternate data streams are not supported.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static bool IsInside(string candidate, string parent) =>
        Full(candidate).StartsWith(Full(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static void RejectReparsePoints(string path)
    {
        for (var current = Full(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Reparse points are not supported for build paths: {current}");
    }

    public static void Validate(BuildRecipe recipe)
    {
        recipe.ValidateOptions();
        ValidateWorkspace(recipe.SourcePath, recipe.WorkspacePath);
        ValidateOutput(recipe);
    }

    /// <summary>
    /// Validation for resuming a saved job. The original media is not needed once the image is committed, so unlike
    /// <see cref="Validate"/> this does not require the source to exist; it still keeps workspace and output safe.
    /// </summary>
    public static void ValidateForResume(BuildRecipe recipe)
    {
        recipe.ValidateOptions();
        ValidateWorkspaceLocation(Full(recipe.SourcePath), Full(recipe.WorkspacePath));
        ValidateOutput(recipe);
    }

    private static void ValidateOutput(BuildRecipe recipe)
    {
        var source = Full(recipe.SourcePath);
        var output = Full(recipe.OutputPath);
        RejectReparsePoints(output);
        foreach (var protectedPath in ProtectedDirectories())
            if (output.Equals(protectedPath, StringComparison.OrdinalIgnoreCase) || IsInside(output, protectedPath))
                throw new InvalidDataException("Output cannot be inside a system directory.");
        if (!Path.GetExtension(output).Equals(".iso", StringComparison.OrdinalIgnoreCase) || File.Exists(output))
            throw new InvalidDataException("Output must be a new .iso file. Existing files are never overwritten.");
        if (source.Equals(output, StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(source) && IsInside(output, source)))
            throw new InvalidDataException("Keep source media and output separate.");
    }

    public static void ValidateWorkspace(string sourcePath, string workspacePath)
    {
        var source = Full(sourcePath);
        var work = Full(workspacePath);
        foreach (var path in new[] { source, work }) RejectReparsePoints(path);
        if (!File.Exists(source) && !Directory.Exists(source))
            throw new FileNotFoundException("The Windows media source does not exist.", source);
        if (File.Exists(source) && !Path.GetExtension(source).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a Windows ISO or an extracted installation-media folder.");
        ValidateWorkspaceLocation(source, work);
    }

    private static void ValidateWorkspaceLocation(string source, string work)
    {
        RejectReparsePoints(work);
        if (work.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(work)!), StringComparison.OrdinalIgnoreCase) ||
            work.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a dedicated workspace folder, not a drive or profile root.");
        foreach (var protectedPath in ProtectedDirectories())
            if (work.Equals(protectedPath, StringComparison.OrdinalIgnoreCase) || IsInside(work, protectedPath))
                throw new InvalidDataException("Workspace cannot be inside a system directory.");
        if (File.Exists(work)) throw new InvalidDataException("Workspace must be a directory, not an existing file.");
        if (work.Equals(source, StringComparison.OrdinalIgnoreCase) || (Directory.Exists(source) && IsInside(work, source)))
            throw new InvalidDataException("Keep source media and workspace separate.");
    }

    private static IEnumerable<string> ProtectedDirectories() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
    }.Where(p => !string.IsNullOrEmpty(p));

    public static void DeleteOwnedDirectory(string path, string jobDirectory)
    {
        var job = Full(jobDirectory);
        var leaf = Path.GetFileName(job);
        if (!leaf.StartsWith("tiny11-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(leaf[7..], "N", out _) ||
            !File.Exists(Path.Combine(job, "job.json")) || !IsInside(path, job))
            throw new InvalidDataException("Refusing to delete a directory not owned by this build.");
        RejectReparsePoints(path);
        if (!Directory.Exists(path)) return;
        ValidateTree(path);
        Directory.Delete(path, recursive: true);
    }

    private static void ValidateTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to follow a reparse point during workspace cleanup.");
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }
}
