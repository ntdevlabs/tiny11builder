using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Tiny11Builder.Core;

public interface IMediaSource : IDisposable { string RootPath { get; } }
public interface IMediaSourceFactory { IMediaSource Open(string path); }

[SupportedOSPlatform("windows")]
public sealed class MediaSourceFactory : IMediaSourceFactory
{
    public IMediaSource Open(string path) => Directory.Exists(path) ? new FolderMedia(path) : new IsoMedia(path);
}

public sealed class FolderMedia(string path) : IMediaSource
{
    public string RootPath { get; } = path;
    public void Dispose() { }
}

public static class InstallationMedia
{
    public static string WorkingCompression(string imageFile)
    {
        using var input = File.OpenRead(imageFile);
        Span<byte> header = stackalloc byte[24];
        input.ReadExactly(header);
        if (!header[..8].SequenceEqual("MSWIM\0\0\0"u8))
            throw new InvalidDataException("The installation image has no valid WIM/ESD header.");
        var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        return (flags & 0x000E0000) switch
        {
            0x00040000 => "/Compress:max",
            0x00020000 => "/Compress:fast",
            0x00080000 => "/Compress:fast",
            0 when (flags & 2) == 0 => "/Compress:none",
            _ => throw new InvalidDataException("The installation image uses unsupported compression.")
        };
    }

    public static string ImageFile(string root)
    {
        if (!File.Exists(Path.Combine(root, @"sources\boot.wim")))
            throw new InvalidDataException("The source is not complete Windows installation media: boot.wim is missing.");
        foreach (var name in new[] { "install.wim", "install.esd" })
        {
            var file = Path.Combine(root, "sources", name);
            if (File.Exists(file)) return file;
        }
        throw new InvalidDataException("install.wim or install.esd is required. Split SWM media is not supported.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class IsoMedia : IMediaSource
{
    private readonly SafeFileHandle handle;
    public string RootPath { get; }

    public IsoMedia(string path)
    {
        var storage = new VirtualStorageType { DeviceId = 1, VendorId = new("EC984AEC-A0F9-47E9-901F-71415A66345B") };
        Check(OpenVirtualDisk(ref storage, path, 0xD0000, 0, IntPtr.Zero, out handle), "Open ISO");
        try
        {
            uint length = 2048;
            var physical = new StringBuilder(1024);
            if (GetVirtualDiskPhysicalPath(handle, ref length, physical) != 0)
            {
                var attach = new AttachParameters { Version = 1 };
                Check(AttachVirtualDisk(handle, IntPtr.Zero, 1, 0, ref attach, IntPtr.Zero), "Attach ISO read-only");
            }
            for (var attempt = 0; attempt < 50; attempt++)
            {
                length = 2048;
                physical.Clear();
                if (GetVirtualDiskPhysicalPath(handle, ref length, physical) == 0)
                {
                    var device = @"\Device\" + physical.ToString()[4..];
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        var target = new StringBuilder(1024);
                        if (QueryDosDevice(drive.Name[..2], target, target.Capacity) != 0 &&
                            target.ToString().Equals(device, StringComparison.OrdinalIgnoreCase) && drive.IsReady)
                        {
                            RootPath = drive.RootDirectory.FullName;
                            return;
                        }
                    }
                }
                Thread.Sleep(100);
            }
            throw new IOException("The ISO attached but Windows did not assign a readable drive letter.");
        }
        catch { handle.Dispose(); throw; }
    }

    // Non-permanent attachments created by this handle detach when it closes. Existing mounts are left alone.
    public void Dispose() => handle.Dispose();
    private static void Check(uint code, string operation)
    {
        if (code != 0) throw new Win32Exception((int)code, operation);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType { public uint DeviceId; public Guid VendorId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttachParameters { public uint Version; public uint Reserved; }
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(ref VirtualStorageType type, string path, uint access,
        uint flags, IntPtr parameters, out SafeFileHandle handle);
    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(SafeFileHandle handle, IntPtr security, uint flags,
        uint providerFlags, ref AttachParameters parameters, IntPtr overlapped);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref uint size, StringBuilder path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int max);
}
