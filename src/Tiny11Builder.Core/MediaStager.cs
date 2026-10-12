namespace Tiny11Builder.Core;

public sealed record MediaStagingResult(int CopiedFiles, long CopiedBytes, int OmittedFiles, long OmittedBytes);

internal static class MediaStager
{
    private sealed record FileEntry(string Source, string Relative, long Size);

    public static async Task<MediaStagingResult> CopyAsync(string source, string target, int workers, bool omitNetFx3,
        Action<string> log, CancellationToken token)
    {
        if (workers is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(workers));
        var files = SafeFiles(source).Select(file => new FileEntry(file, Path.GetRelativePath(source, file), new FileInfo(file).Length))
            .Where(file => !file.Relative.Equals(@"sources\install.wim", StringComparison.OrdinalIgnoreCase) &&
                           !file.Relative.Equals(@"sources\install.esd", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Any(file => file.Relative.StartsWith(@"sources\install", StringComparison.OrdinalIgnoreCase) &&
                              Path.GetExtension(file.Relative).Equals(".swm", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Split SWM source images are not supported.");
        var omitted = files.Where(file => omitNetFx3 && file.Relative.StartsWith(@"sources\sxs\", StringComparison.OrdinalIgnoreCase)).ToArray();
        var omittedPaths = omitted.Select(file => file.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = files.Where(file => !omittedPaths.Contains(file.Relative)).OrderByDescending(file => file.Size).ToArray();
        var completed = 0;
        await Parallel.ForEachAsync(selected, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token },
            async (file, cancellation) =>
            {
                var destination = Path.Combine(target, file.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
                var count = Interlocked.Increment(ref completed);
                if (count % 50 == 0 || count == selected.Length) log($"Copied setup file {count} of {selected.Length}");
            }).ConfigureAwait(false);
        if (omitted.Length > 0) log($"Omitted .NET 3.5 setup payload: {omitted.Length} files, {omitted.Sum(file => file.Size)} bytes.");
        return new(selected.Length, selected.Sum(file => file.Size), omitted.Length, omitted.Sum(file => file.Size));
    }

    private static IEnumerable<string> SafeFiles(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Source media contains a reparse point.");
            if (Directory.Exists(entry))
            {
                foreach (var file in SafeFiles(entry)) yield return file;
            }
            else yield return entry;
        }
    }
}
