using System.Security.Cryptography;

namespace Tiny11Builder.Core;

public sealed record UpdatePackageResult(string Name, long Bytes, string Sha256, string Outcome);

public static class WindowsUpdates
{
    public static long ValidateSources(BuildRecipe recipe)
    {
        recipe.ValidateOptions();
        long bytes = 0;
        foreach (var package in recipe.UpdatePackages)
        {
            var path = PathSafety.Full(package);
            PathSafety.RejectReparsePoints(path);
            if (!File.Exists(path)) throw new FileNotFoundException("A selected Windows update package is missing.", path);
            var length = new FileInfo(path).Length;
            if (length == 0) throw new InvalidDataException($"The Windows update package is empty: {Path.GetFileName(path)}");
            bytes = checked(bytes + length);
        }
        return bytes;
    }

    internal static async Task<IReadOnlyList<UpdatePackageResult>> StageAsync(BuildRecipe recipe, string job,
        Action<string> log, CancellationToken token)
    {
        ValidateSources(recipe);
        var directory = Path.Combine(job, "updates");
        Directory.CreateDirectory(directory);
        var result = new List<UpdatePackageResult>();
        for (var i = 0; i < recipe.UpdatePackages.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var source = PathSafety.Full(recipe.UpdatePackages[i]);
            PathSafety.RejectReparsePoints(source);
            var target = Path.Combine(directory, i.ToString("D3"), Path.GetFileName(source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // DISM sees only this selected package, not other packages in the user's Downloads folder.
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous);
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            input.Position = 0;
            var expected = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            await using var staged = File.OpenRead(target);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(staged, token).ConfigureAwait(false));
            if (actual != expected) throw new IOException("The staged Windows update package failed its SHA-256 copy check.");
            result.Add(new(Path.GetFileName(source), input.Length, actual, "Staged"));
            log($"Staged Windows update: {Path.GetFileName(source)}; SHA-256 {actual}");
        }
        return result;
    }

    internal static string StagedPath(string job, int index, string name) =>
        Path.Combine(job, "updates", index.ToString("D3"), name);
}
