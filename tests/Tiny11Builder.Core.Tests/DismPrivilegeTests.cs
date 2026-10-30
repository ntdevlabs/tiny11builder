using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[CollectionDefinition("Servicing privilege isolation", DisableParallelization = true)]
public sealed class ServicingPrivilegeCollection;

[SupportedOSPlatform("windows")]
[Collection("Servicing privilege isolation")]
public sealed class DismPrivilegeTests
{
    private static readonly string[] Required = ["SeBackupPrivilege", "SeRestorePrivilege", "SeSecurityPrivilege"];

    [Fact]
    public async Task DISM_child_inherits_enabled_backup_restore_and_security_privileges_and_parent_rolls_back()
    {
        using var disabled = new DisabledPrivileges();
        using var files = new IssueFixTestDirectory();
        var executable = CreateProbe(files);
        var before = Snapshot();
        var result = await new ProcessRunner().RunAsync(executable, ["/priv", "/fo", "csv", "/nh"], null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var enabled = RecordState(result.Output, "SeChangeNotifyPrivilege");
        Assert.NotEqual("\"\"", enabled);
        foreach (var privilege in Required) Assert.Equal(enabled, RecordState(result.Output, privilege));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Failed_DISM_process_creation_restores_all_parent_privileges()
    {
        using var disabled = new DisabledPrivileges();
        using var files = new IssueFixTestDirectory();
        var before = Snapshot();
        await Assert.ThrowsAsync<Win32Exception>(() => new ProcessRunner().RunAsync(Path.Combine(files.Root, "dism.exe"), [], null, CancellationToken.None));
        Assert.Equal(before, Snapshot());
        using var usable = new ImageOperations.PrivilegeScope();
    }

    [Fact]
    public async Task A_non_DISM_child_does_not_get_servicing_privileges_enabled()
    {
        using var disabled = new DisabledPrivileges();
        var before = Snapshot();
        var result = await new ProcessRunner().RunAsync(Path.Combine(Environment.SystemDirectory, "whoami.exe"),
            ["/priv", "/fo", "csv", "/nh"], null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var enabled = RecordState(result.Output, "SeChangeNotifyPrivilege");
        foreach (var privilege in Required) Assert.NotEqual(enabled, RecordState(result.Output, privilege));
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Output_and_false_finish_cancellation_callbacks_do_not_hold_the_process_privilege_monitor(bool finishOnCancellation)
    {
        using var disabled = new DisabledPrivileges();
        using var files = new IssueFixTestDirectory();
        using var cancellation = new CancellationTokenSource();
        var before = Snapshot();
        var callbackCount = 0;
        ExceptionDispatchInfo? workerFailure = null;
        using var entered = new ManualResetEventSlim();
        Thread? worker = null;
        var run = new ProcessRunner().RunAsync(CreateProbe(files), ["/priv", "/fo", "csv", "/nh"], _ =>
        {
            if (Interlocked.Increment(ref callbackCount) != 1) return;
            Assert.Equal(before, Snapshot());
            worker = new Thread(() =>
            {
                try { using var scope = new ImageOperations.PrivilegeScope(); }
                catch (Exception error) { workerFailure = ExceptionDispatchInfo.Capture(error); }
                finally { entered.Set(); }
            }) { IsBackground = true };
            worker.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "An output callback was blocked by the process privilege monitor.");
            workerFailure?.Throw();
            if (!finishOnCancellation) cancellation.Cancel();
        }, cancellation.Token, finishOnCancellation);
        if (finishOnCancellation) Assert.Equal(0, (await run).ExitCode);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(callbackCount > 0);
        Assert.True(worker!.Join(TimeSpan.FromSeconds(5)));
        workerFailure?.Throw();
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Process_privilege_scopes_are_serialized_across_threads()
    {
        using var disabled = new DisabledPrivileges();
        var before = Snapshot();
        using var started = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        ExceptionDispatchInfo? failure = null;
        var worker = new Thread(() =>
        {
            started.Set();
            try
            {
                using var scope = new ImageOperations.PrivilegeScope("SeBackupPrivilege");
                entered.Set();
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        using (var scope = new ImageOperations.PrivilegeScope("SeBackupPrivilege"))
        {
            worker.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(entered.Wait(TimeSpan.FromMilliseconds(100)));
        }
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        failure?.Throw();
        Assert.True(entered.IsSet);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Invalid_scope_arguments_fail_before_taking_the_process_wide_monitor()
    {
        Assert.Throws<ArgumentNullException>(() => new ImageOperations.PrivilegeScope(null!));
        using var usable = new ImageOperations.PrivilegeScope();
    }

    [Fact]
    public void Partial_privilege_enable_failure_rolls_back_and_releases_the_monitor_without_granting_a_missing_right()
    {
        using var disabled = new DisabledPrivileges();
        var before = Snapshot();
        var present = Attributes();
        var missing = new[] { "SeTcbPrivilege", "SeCreateTokenPrivilege", "SeTrustedCredManAccessPrivilege" }
            .First(name => !present.ContainsKey(LuidOf(name)));
        var error = Assert.Throws<Win32Exception>(() => new ImageOperations.PrivilegeScope("SeBackupPrivilege", missing));
        Assert.Equal(1300, error.NativeErrorCode);
        Assert.Contains(missing, error.Message);
        Assert.Contains("cannot grant", error.Message);
        Assert.Equal(before, Snapshot());
        using (var usable = new ImageOperations.PrivilegeScope("SeBackupPrivilege"))
            Assert.True((Attributes()[LuidOf("SeBackupPrivilege")] & 2) != 0);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Nested_and_duplicate_privilege_scopes_preserve_enabled_state_and_dispose_idempotently()
    {
        using var disabled = new DisabledPrivileges();
        var before = Snapshot();
        var outer = new ImageOperations.PrivilegeScope("SeBackupPrivilege", "SeBackupPrivilege");
        try
        {
            var enabled = Snapshot();
            using (var nested = new ImageOperations.PrivilegeScope("SeBackupPrivilege")) Assert.Equal(enabled, Snapshot());
            Assert.Equal(enabled, Snapshot());
        }
        finally { outer.Dispose(); }
        outer.Dispose();
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Synthetic_WIM_capture_mount_commit_and_readonly_remount_succeed_without_an_OS_image()
    {
        using var disabled = new DisabledPrivileges();
        using var files = new IssueFixTestDirectory();
        var source = Path.Combine(files.Root, "capture");
        var mount = Path.Combine(files.Root, "mount");
        var scratch = Path.Combine(files.Root, "scratch");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(mount);
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(source, "source.txt"), "synthetic privilege fixture");
        var image = Path.Combine(files.Root, "synthetic.wim");
        var before = Snapshot();
        var dism = new DismClient(ToolLocator.FindDism(), new ProcessRunner(), null,
            new(scratch, Path.Combine(files.Root, "dism.log"), false));
        await dism.RunAsync(CancellationToken.None, "/Capture-Image", $"/ImageFile:{image}", $"/CaptureDir:{source}",
            "/Name:Synthetic privilege fixture", "/Compress:fast", "/CheckIntegrity");
        var mounted = false;
        try
        {
            await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{image}", "/Index:1", $"/MountDir:{mount}", "/CheckIntegrity");
            mounted = true;
            Assert.Equal("synthetic privilege fixture", File.ReadAllText(Path.Combine(mount, "source.txt")));
            File.WriteAllText(Path.Combine(mount, "committed.txt"), "committed with scoped child privileges");
            await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity");
            mounted = false;
            await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{image}", "/Index:1", $"/MountDir:{mount}", "/ReadOnly", "/CheckIntegrity");
            mounted = true;
            Assert.Equal("committed with scoped child privileges", File.ReadAllText(Path.Combine(mount, "committed.txt")));
            Assert.Equal(before, Snapshot());
        }
        finally
        {
            if (mounted) await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard");
        }
        Assert.Equal(before, Snapshot());
    }

    private static string CreateProbe(IssueFixTestDirectory files)
    {
        var executable = Path.Combine(files.Root, "dism.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), executable);
        foreach (var resource in Directory.EnumerateDirectories(Environment.SystemDirectory)
                     .Select(directory => Path.Combine(directory, "whoami.exe.mui")).Where(File.Exists))
        {
            var language = Path.GetFileName(Path.GetDirectoryName(resource)!);
            Directory.CreateDirectory(Path.Combine(files.Root, language));
            File.Copy(resource, Path.Combine(files.Root, language, "dism.exe.mui"));
        }
        return executable;
    }

    private static string RecordState(string output, string privilege) =>
        Assert.Single(output.Split('\n'), line => line.StartsWith('"' + privilege + '"', StringComparison.Ordinal)).Trim().Split(',')[^1];

    private static string Snapshot()
    {
        var attributes = Attributes();
        return string.Join(",", Required.Select(name => $"{name}:{attributes[LuidOf(name)] & 2}"));
    }

    private static (uint Low, int High) LuidOf(string name)
    {
        if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (luid.Low, luid.High);
    }

    private static Dictionary<(uint Low, int High), uint> Attributes()
    {
        using var process = Process.GetCurrentProcess();
        if (!OpenProcessToken(process.Handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using (token)
        {
            if (GetTokenInformation(token, 3, IntPtr.Zero, 0, out var length) || Marshal.GetLastPInvokeError() != 122)
                throw new InvalidOperationException("Could not size the process token privilege list.");
            var buffer = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                if (!GetTokenInformation(token, 3, buffer, length, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                var result = new Dictionary<(uint, int), uint>();
                var count = Marshal.ReadInt32(buffer);
                for (var index = 0; index < count; index++)
                {
                    var offset = 4 + index * 12;
                    result.Add((unchecked((uint)Marshal.ReadInt32(buffer, offset)), Marshal.ReadInt32(buffer, offset + 4)),
                        unchecked((uint)Marshal.ReadInt32(buffer, offset + 8)));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private sealed class DisabledPrivileges : IDisposable
    {
        private readonly SafeAccessTokenHandle token;
        private readonly List<TokenPrivilege> previous = [];
        private bool disposed;
        public DisabledPrivileges()
        {
            using var process = Process.GetCurrentProcess();
            if (!OpenProcessToken(process.Handle, 0x28, out token)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                foreach (var name in Required)
                {
                    if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    var state = new TokenPrivilege { Count = 1, Luid = luid };
                    Marshal.SetLastPInvokeError(0);
                    var adjusted = AdjustTokenPrivileges(token, false, ref state, Marshal.SizeOf<TokenPrivilege>(), out var old, out _);
                    var error = Marshal.GetLastPInvokeError();
                    if (old.Count != 0) previous.Add(old);
                    if (!adjusted || error != 0) throw new Win32Exception(error);
                }
            }
            catch (Win32Exception) { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            List<Exception> failures = [];
            try
            {
                foreach (var old in previous.AsEnumerable().Reverse())
                {
                    var state = old;
                    Marshal.SetLastPInvokeError(0);
                    var adjusted = AdjustTokenPrivileges(token, false, ref state, Marshal.SizeOf<TokenPrivilege>(), out _, out _);
                    var error = Marshal.GetLastPInvokeError();
                    if (!adjusted || error != 0) failures.Add(new Win32Exception(error));
                }
            }
            finally { token.Dispose(); }
            if (failures.Count > 0) throw new AggregateException("Failed to restore test-process privileges.", failures);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool all, ref TokenPrivilege state,
        int size, out TokenPrivilege previous, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr buffer, uint length, out uint returned);
}

internal sealed class IssueFixTestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "issue-fix-fixtures", Guid.NewGuid().ToString("N"));
    public IssueFixTestDirectory() => Directory.CreateDirectory(Root);
    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
