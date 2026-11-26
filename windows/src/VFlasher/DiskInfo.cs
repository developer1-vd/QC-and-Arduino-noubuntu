using System.Text;

namespace VFlasher;

/// <summary>A physical drive (\\.\PhysicalDriveN) that could be flashed.</summary>
internal sealed record DiskInfo(int Number, string Model, long Size, string Bus, bool Removable, bool IsSystem,
    IReadOnlyList<string> MountPoints)
{
    public string Path => $@"\\.\PhysicalDrive{Number}";

    /// <summary>USB sticks, SD/MMC cards and anything the drive itself reports as removable.</summary>
    public bool LooksExternal => Removable || Bus is "USB" or "SD" or "MMC";

    public override string ToString()
    {
        var letters = MountPoints.Count > 0 ? "  [" + string.Join(" ", MountPoints) + "]" : "";
        return $"Disk {Number}: {Model} ({FormatSize(Size)}, {Bus}){letters}";
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return $"{v:0.#} {units[u]}";
    }

    static readonly Dictionary<int, string> BusNames = new()
    {
        [1] = "SCSI", [2] = "ATAPI", [3] = "ATA", [4] = "1394", [5] = "SSA", [6] = "Fibre", [7] = "USB",
        [8] = "RAID", [9] = "iSCSI", [10] = "SAS", [11] = "SATA", [12] = "SD", [13] = "MMC", [14] = "Virtual",
        [15] = "File-backed", [16] = "Spaces", [17] = "NVMe", [18] = "SCM", [19] = "UFS",
    };

    /// <summary>Lists physical drives 0-63, marking the one Windows runs from.</summary>
    public static List<DiskInfo> Enumerate()
    {
        var systemDisks = SystemDiskNumbers();
        var mounts = MountPointsByDisk();
        var disks = new List<DiskInfo>();
        for (var n = 0; n < 64; n++)
        {
            var h = Native.CreateFile($@"\\.\PhysicalDrive{n}", 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            using (h)
            {
                if (h.IsInvalid)
                    continue;
                var size = DiskSize(h);
                var (model, bus, removable) = QueryDevice(h);
                disks.Add(new DiskInfo(n, model, size, bus, removable, systemDisks.Contains(n),
                    mounts.GetValueOrDefault(n) ?? []));
            }
        }
        return disks;
    }

    /// <summary>
    /// Size in bytes. GET_LENGTH_INFO needs read access, which this handle does
    /// not have; GET_DRIVE_GEOMETRY_EX works without it (DiskSize is at offset 24).
    /// </summary>
    static long DiskSize(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        if (Native.Ioctl(h, Native.IOCTL_DISK_GET_LENGTH_INFO, null, 8) is { Length: 8 } len)
            return BitConverter.ToInt64(len);
        if (Native.Ioctl(h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 256) is { Length: >= 32 } geo)
            return BitConverter.ToInt64(geo, 24);
        return 0;
    }

    static (string Model, string Bus, bool Removable) QueryDevice(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        // STORAGE_PROPERTY_QUERY { StorageDeviceProperty = 0, PropertyStandardQuery = 0 }
        var query = new byte[12];
        var d = Native.Ioctl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, query, 1024);
        if (d is null || d.Length < 32)
            return ("Unknown drive", "?", false);
        // STORAGE_DEVICE_DESCRIPTOR
        var removable = d[10] != 0;
        string Str(int offsetField)
        {
            var off = BitConverter.ToInt32(d, offsetField);
            if (off <= 0 || off >= d.Length)
                return "";
            var end = Array.IndexOf(d, (byte)0, off);
            return Encoding.ASCII.GetString(d, off, (end < 0 ? d.Length : end) - off).Trim();
        }
        var model = $"{Str(12)} {Str(16)}".Trim();
        var bus = BusNames.GetValueOrDefault(BitConverter.ToInt32(d, 28), "Other");
        return (model.Length > 0 ? model : "Unknown drive", bus, removable);
    }

    /// <summary>Disks holding the Windows volume (normally just one).</summary>
    static HashSet<int> SystemDiskNumbers()
    {
        var set = new HashSet<int>();
        var sysDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var h = Native.CreateFile($@"\\.\{sysDrive}", 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        using (h)
        {
            if (h.IsInvalid)
                return set;
            foreach (var disk in Native.VolumeDiskNumbers(h))
                set.Add(disk);
        }
        return set;
    }

    /// <summary>Map of disk number to the drive letters / mount folders on it.</summary>
    public static Dictionary<int, List<string>> MountPointsByDisk()
    {
        var map = new Dictionary<int, List<string>>();
        foreach (var volume in Native.ListVolumes())
        {
            var h = Native.CreateFile(volume.TrimEnd('\\'), 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            using (h)
            {
                if (h.IsInvalid)
                    continue;
                var points = Native.VolumeMountPoints(volume);
                foreach (var disk in Native.VolumeDiskNumbers(h))
                {
                    if (!map.TryGetValue(disk, out var list))
                        map[disk] = list = [];
                    list.AddRange(points);
                }
            }
        }
        return map;
    }
}
