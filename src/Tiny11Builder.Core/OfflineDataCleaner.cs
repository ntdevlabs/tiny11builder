using System.Runtime.Versioning;
using System.ComponentModel;

namespace Tiny11Builder.Core;

public enum PendingState { Clear, Pending, Unknown }
public sealed record PendingAssessment(PendingState State, string Reason);
public sealed record ImageCleanupResult(string Path, BuildOperationState State, int Files, long LogicalBytes, string Detail,
    PendingState? Pending = null);

[SupportedOSPlatform("windows")]
public sealed class OfflineDataCleaner(Func<string, PendingAssessment>? assessPending = null)
{
    public OfflineDataCleaner(Func<string, bool> hasPendingRegistryOperations)
        : this(mount => new PendingAssessment(hasPendingRegistryOperations(mount) ? PendingState.Pending : PendingState.Clear,
            "Registry assessment")) { }

    private sealed record Target(string Path, bool ServicingSensitive = false, bool ChildrenOnly = true,
        string? Prefix = null, string[]? Extensions = null);

    private static readonly Target[] Targets =
    [
        new(@"Windows\SoftwareDistribution\Download", ServicingSensitive: true),
        new(@"Windows\Temp", ServicingSensitive: true),
        new(@"Users\Default\AppData\Local\Temp", ServicingSensitive: true),
        new(@"Windows\Logs\CBS", Prefix: "CbsPersist_", Extensions: [".cab", ".log"]),
        new(@"Windows\Logs\DISM", Extensions: [".bak"]),
        new(@"Windows\MEMORY.DMP", ChildrenOnly: false),
        new(@"Windows\Minidump", Extensions: [".dmp"]),
        new(@"ProgramData\Microsoft\Windows\WER\ReportArchive"),
        new(@"ProgramData\Microsoft\Windows\WER\ReportQueue")
    ];

    public IReadOnlyList<ImageCleanupResult> Clean(string mount, Action<string> log, CancellationToken token)
    {
        ImageOperations.RequireOwnedMount(mount);
        using var privileges = new ImageOperations.PrivilegeScope("SeBackupPrivilege", "SeRestorePrivilege");
        token.ThrowIfCancellationRequested();
        var marker = new[] { @"Windows\WinSxS\pending.xml", @"Windows\WinSxS\reboot.xml" }
            .FirstOrDefault(path => ImageFileAccess.Exists(Path.Combine(mount, path)));
        var pending = marker is not null ? new(PendingState.Pending, marker) :
            (assessPending ?? AssessRegistryPending)(mount);
        if (pending.State == PendingState.Unknown)
            log($"Warning: pending servicing state could not be verified: {pending.Reason}. Temporary/update payloads are retained.");
        var results = new List<ImageCleanupResult>();
        foreach (var target in Targets)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(mount, target.Path);
            if (pending.State != PendingState.Clear && target.ServicingSensitive)
            {
                var reason = pending.State == PendingState.Pending ? "Retained because this image has pending servicing operations." :
                    $"Retained because pending servicing state could not be verified: {pending.Reason}";
                log($"Skipped cleanup: {target.Path}: {reason}");
                results.Add(new(target.Path, BuildOperationState.Skipped, 0, 0, reason, pending.State));
                continue;
            }
            if (!ImageFileAccess.Exists(path))
            {
                log($"Skipped cleanup: {target.Path}: Not present.");
                results.Add(new(target.Path, BuildOperationState.Skipped, 0, 0, "Not present."));
                continue;
            }
            PathSafety.RejectReparsePoints(Path.GetDirectoryName(path)!);
            ImageFileAccess.ValidateFile(path);
            var entries = target.ChildrenOnly && Directory.Exists(path) ?
                Directory.EnumerateFileSystemEntries(path).Where(entry =>
                    (target.Prefix is null || Path.GetFileName(entry).StartsWith(target.Prefix, StringComparison.OrdinalIgnoreCase)) &&
                    (target.Extensions is null || File.Exists(entry) &&
                        target.Extensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase))).ToArray() :
                target.ChildrenOnly ? throw new InvalidDataException($"Expected an offline cleanup directory: {target.Path}") : [path];
            long bytes = 0;
            var files = 0;
            foreach (var entry in entries) Measure(entry, ref files, ref bytes, token);
            if (files == 0 && entries.Length == 0)
            {
                results.Add(new(target.Path, BuildOperationState.Skipped, 0, 0, "No redundant files."));
                log($"Skipped cleanup: {target.Path}: No redundant files.");
                continue;
            }
            log($"Plan payload: {target.Path}");
            log($"Removing payload: {target.Path}");
            foreach (var entry in entries) ImageOperations.DeleteTree(entry, turboMode: true, token);
            results.Add(new(target.Path, BuildOperationState.Completed, files, bytes, "Removed cache or diagnostic files; root directory retained."));
            log($"Payload removed: {target.Path}");
            log($"Cleanup removed {files} files and {bytes} logical bytes from {target.Path}.");
        }
        return results;
    }

    internal static PendingAssessment AssessRegistryPending(string mount)
    {
        var source = Path.Combine(mount, @"Windows\System32\config\SOFTWARE");
        if (!ImageFileAccess.Exists(source)) return new(PendingState.Unknown, "The SOFTWARE hive is missing.");
        var directory = Path.Combine(Path.GetDirectoryName(mount)!, "pending-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "SOFTWARE");
        try
        {
            // Loading a hive can replay transaction logs even when its registry handle is read-only.
            foreach (var suffix in new[] { "", ".LOG1", ".LOG2" })
                if (ImageFileAccess.Exists(source + suffix))
                {
                    ImageFileAccess.ValidateFile(source + suffix);
                    File.Copy(source + suffix, copy + suffix);
                    File.SetAttributes(copy + suffix, File.GetAttributes(copy + suffix) & ~FileAttributes.ReadOnly);
                }
            using var hive = OfflineRegistry.Load(copy, writable: false);
            const string cbsPath = @"Microsoft\Windows\CurrentVersion\Component Based Servicing";
            using var cbs = hive.Root.OpenSubKey(cbsPath);
            if (cbs is null) return new(PendingState.Unknown, "The SOFTWARE hive has no servicing inventory.");
            foreach (var subkey in new[]
            {
                cbsPath + @"\RebootPending", cbsPath + @"\PackagesPending", cbsPath + @"\RebootInProgress",
                @"Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"
            })
            {
                using var key = hive.Root.OpenSubKey(subkey);
                if (key is not null) return new(PendingState.Pending, subkey);
            }
            return new(PendingState.Clear, "No pending servicing markers.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 1009 or 1017)
        {
            return new(PendingState.Unknown, $"The SOFTWARE hive is unreadable (registry error {error.NativeErrorCode}).");
        }
        finally
        {
            PathSafety.DeleteOwnedDirectory(directory, Path.GetDirectoryName(mount)!);
        }
    }

    private static void Measure(string path, ref int files, ref long bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ImageFileAccess.ValidateFile(path);
        if (Directory.Exists(path))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Measure(entry, ref files, ref bytes, token);
        }
        else
        {
            files++;
            bytes = checked(bytes + new FileInfo(path).Length);
        }
    }
}
