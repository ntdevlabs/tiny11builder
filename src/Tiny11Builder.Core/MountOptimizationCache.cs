using System.Diagnostics;
using System.Text.Json;

namespace Tiny11Builder.Core;

internal static class MountOptimizationCache
{
    private sealed record Entry(string Tool, string Version, int ExitCode);
    private static string CacheFile(string workspace) => Path.Combine(PathSafety.Full(workspace), "mount-optimization.json");

    public static bool IsUnavailable(string workspace, string executable, Action<string> log)
    {
        if (!File.Exists(executable)) return false;
        var file = CacheFile(workspace);
        if (!File.Exists(file)) return false;
        Entry entry;
        try
        {
            PathSafety.RejectReparsePoints(file);
            entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file), JsonDefaults.Options) ??
                throw new InvalidDataException("The mount optimization cache is empty.");
            if (string.IsNullOrWhiteSpace(entry.Tool) || string.IsNullOrWhiteSpace(entry.Version) ||
                entry.ExitCode is not (87 or 4350))
                throw new InvalidDataException("The mount optimization cache has missing or unsupported fields.");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Advisory data only: it must never block or orphan a job. Fail closed (no lazy-mount probe), say so, and leave the file alone.
            log($"The mount-optimization advisory cache could not be used ({error.GetType().Name}: {error.Message}) at {file}. " +
                "Optimized mounting is disabled for this run; the file was not changed.");
            return true;
        }
        var unavailable = entry.Tool.Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) &&
            entry.Version == VersionOf(executable);
        if (unavailable) log($"Optimized mounting disabled for this known-incompatible tool version (observed exit {entry.ExitCode}).");
        return unavailable;
    }

    public static void RecordUnavailable(string workspace, string executable, int exitCode)
    {
        if (!File.Exists(executable)) return;
        if (exitCode is not (87 or 4350)) throw new ArgumentOutOfRangeException(nameof(exitCode));
        var file = CacheFile(workspace);
        PathSafety.RejectReparsePoints(file);
        var entry = new Entry(Path.GetFullPath(executable), VersionOf(executable), exitCode);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry, JsonDefaults.Options));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Best-effort recorder for the recovery path: an unwritable advisory cache must not fail an otherwise good build.</summary>
    public static bool TryRecordUnavailable(string workspace, string executable, int exitCode, Action<string> log)
    {
        if (exitCode is not (87 or 4350)) throw new ArgumentOutOfRangeException(nameof(exitCode));
        try
        {
            RecordUnavailable(workspace, executable, exitCode);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log($"The mount-optimization advisory cache could not be updated ({error.GetType().Name}: {error.Message}). " +
                "The build continues with a normal mount; a later opt-in run may probe again.");
            return false;
        }
    }

    private static string VersionOf(string executable)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var files = new[] { executable, Path.Combine(folder, "wimgapi.dll"), Path.Combine(folder, "DismApi.dll"),
            Path.Combine(folder, @"Dism\dismcore.dll") };
        return string.Join("|", files.Where(File.Exists).Select(file =>
            Path.GetFileName(file) + ":" + FileVersionInfo.GetVersionInfo(file).FileVersion));
    }
}
