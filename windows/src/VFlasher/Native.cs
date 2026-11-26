using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VFlasher;

/// <summary>Win32 calls used to find drives and write to them directly.</summary>
internal static partial class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_DISK_DELETE_DRIVE_LAYOUT = 0x0007C100;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

    public const int ERROR_NO_MORE_FILES = 18;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? inBuffer, int inSize,
        byte[]? outBuffer, int outSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(SafeFileHandle file);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    public static unsafe partial IntPtr FindFirstVolume(char* name, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FindNextVolume(IntPtr find, char* name, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindVolumeClose(IntPtr find);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetVolumePathNamesForVolumeName(string volume, char* names, int length,
        out int returned);

    public static SafeFileHandle Open(string path, uint access, uint flags = 0)
    {
        var h = CreateFile(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, flags,
            IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"Cannot open {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        return h;
    }

    /// <summary>DeviceIoControl that returns the output bytes, or null on failure.</summary>
    public static byte[]? Ioctl(SafeFileHandle h, uint code, byte[]? input = null, int outSize = 0)
    {
        var output = outSize > 0 ? new byte[outSize] : null;
        if (!DeviceIoControl(h, code, input, input?.Length ?? 0, output, outSize, out var returned, IntPtr.Zero))
            return null;
        return output is null ? [] : output.AsSpan(0, returned).ToArray();
    }

    public static void IoctlOrThrow(SafeFileHandle h, uint code, string what)
    {
        if (Ioctl(h, code) is null)
            throw new IOException($"{what} failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
    }

    /// <summary>Every volume GUID path on the system, e.g. \\?\Volume{...}\</summary>
    public static unsafe List<string> ListVolumes()
    {
        var result = new List<string>();
        const int len = 260;
        char* buf = stackalloc char[len];
        var find = FindFirstVolume(buf, len);
        if (find == new IntPtr(-1))
            return result;
        try
        {
            do
                result.Add(new string(buf));
            while (FindNextVolume(find, buf, len));
        }
        finally
        {
            FindVolumeClose(find);
        }
        return result;
    }

    /// <summary>Mount points (drive letters / folders) of a volume.</summary>
    public static unsafe List<string> VolumeMountPoints(string volume)
    {
        const int len = 1024;
        char* buf = stackalloc char[len];
        if (!GetVolumePathNamesForVolumeName(volume, buf, len, out _))
            return [];
        var list = new List<string>();
        for (var p = buf; *p != '\0';)
        {
            var s = new string(p);
            list.Add(s);
            p += s.Length + 1;
        }
        return list;
    }

    /// <summary>Physical disk numbers a volume lives on.</summary>
    public static List<int> VolumeDiskNumbers(SafeFileHandle volume)
    {
        var outBuf = Ioctl(volume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 8 + 24 * 32);
        var disks = new List<int>();
        if (outBuf is null || outBuf.Length < 8)
            return disks;
        // VOLUME_DISK_EXTENTS: DWORD count; (pad); DISK_EXTENT[count] { DWORD disk; (pad); LONGLONG start; LONGLONG len }
        var count = BitConverter.ToInt32(outBuf, 0);
        for (var i = 0; i < count && 8 + 24 * i + 4 <= outBuf.Length; i++)
            disks.Add(BitConverter.ToInt32(outBuf, 8 + 24 * i));
        return disks;
    }
}
