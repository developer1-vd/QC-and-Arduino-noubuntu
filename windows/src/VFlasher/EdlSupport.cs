using System.Diagnostics;
using System.Runtime.InteropServices;
using VFlasher.Core.Qdl;

namespace VFlasher;

/// <summary>Finds a Qualcomm board in EDL mode (USB 05C6:9008) with the Windows device manager API.</summary>
internal sealed partial class WindowsEdlDevice : IEdlDevice
{
    const string HardwareId = @"USB\VID_05C6&PID_9008";
    const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
    const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List(string filter, [Out] char[] buffer, uint length, uint flags);

    public static bool IsPresent()
    {
        const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        if (CM_Get_Device_ID_List_Size(out var len, "USB", flags) != 0 || len == 0)
            return false;
        var buf = new char[len];
        if (CM_Get_Device_ID_List("USB", buf, len, flags) != 0)
            return false;
        return new string(buf).Split('\0')
            .Any(id => id.StartsWith(HardwareId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task WaitForDeviceAsync(CancellationToken ct)
    {
        var timeout = DateTime.UtcNow.AddMinutes(5);
        while (!IsPresent())
        {
            if (DateTime.UtcNow > timeout)
                throw new TimeoutException("No board in EDL mode (USB 05C6:9008) was found within 5 minutes.");
            await Task.Delay(500, ct);
        }
    }

    public async Task WaitForReconnectAsync(CancellationToken ct)
    {
        // qdl resets the board after reading; give it a moment to drop off the bus.
        var gone = DateTime.UtcNow.AddSeconds(10);
        while (IsPresent() && DateTime.UtcNow < gone)
            await Task.Delay(250, ct);
        await WaitForDeviceAsync(ct);
        await Task.Delay(1000, ct); // let Windows finish binding the driver
    }
}

/// <summary>
/// Installs the WinUSB driver that qdl needs for the EDL device. The driver
/// package (qcserlib.inf/.cat, signed by Qualcomm) ships in the drivers folder;
/// installing it again is harmless.
/// </summary>
internal static class EdlDriver
{
    public static string? InfPath =>
        new[] { Path.Combine(AppContext.BaseDirectory, "drivers", "qcserlib.inf") }.FirstOrDefault(File.Exists);

    /// <returns>A message for the log.</returns>
    public static async Task<string> InstallAsync(CancellationToken ct)
    {
        var inf = InfPath ?? throw new FileNotFoundException(
            "The EDL USB driver (drivers\\qcserlib.inf) is missing. Install VFlasher with the setup wizard.");
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            ArgumentList = { "/add-driver", inf, "/install" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        // See https://learn.microsoft.com/windows-hardware/drivers/devtest/pnputil-return-values
        return p.ExitCode switch
        {
            0 => "EDL USB driver is installed.",
            259 => "EDL USB driver is installed (it will be used when the board is connected).",
            3010 => throw new InvalidOperationException(
                "Windows needs a restart to finish installing the EDL USB driver. Restart, then flash again."),
            _ => throw new InvalidOperationException($"Installing the EDL USB driver failed (pnputil exit code {p.ExitCode}):\n{output}"),
        };
    }
}

/// <summary>Locates qdl.exe, shipped in the qdl folder next to vflasher.exe.</summary>
internal static class QdlTool
{
    public static string? Path => new[]
    {
        System.IO.Path.Combine(AppContext.BaseDirectory, "qdl", "qdl.exe"),
        System.IO.Path.Combine(AppContext.BaseDirectory, "qdl.exe"),
    }.FirstOrDefault(File.Exists);
}

/// <summary>Marks a file sparse on NTFS, so skipped zero blocks take no disk space.</summary>
internal static partial class SparseFiles
{
    const uint FSCTL_SET_SPARSE = 0x000900C4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code,
        IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    public static void MarkSparse(FileStream fs) =>
        DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
}
