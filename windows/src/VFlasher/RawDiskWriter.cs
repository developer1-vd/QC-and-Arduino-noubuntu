using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VFlasher.Core;

namespace VFlasher;

/// <summary>
/// Writes a decompressed image to a whole physical drive: unmounts its volumes,
/// clears the old partition table, writes sector-aligned blocks, then asks
/// Windows to re-read the drive.
/// </summary>
internal static class RawDiskWriter
{
    const int BlockSize = 4 * 1024 * 1024;

    public static unsafe void Write(DiskInfo disk, ImageDecompressor image, IProgress<TransferProgress> progress,
        CancellationToken ct)
    {
        if (disk.IsSystem)
            throw new InvalidOperationException("Refusing to write to the drive Windows is running from.");

        var lockedVolumes = LockAndDismountVolumes(disk.Number);
        try
        {
            using var drive = Native.Open(disk.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH);

            var sector = SectorSize(drive);
            var length = Native.Ioctl(drive, Native.IOCTL_DISK_GET_LENGTH_INFO, null, 8) is { Length: 8 } l
                ? BitConverter.ToInt64(l)
                : disk.Size;

            // Without this, Windows may remount the old partitions mid-write.
            Native.Ioctl(drive, Native.IOCTL_DISK_DELETE_DRIVE_LAYOUT);

            // Unbuffered I/O needs a sector-aligned buffer.
            var mem = (byte*)NativeMemory.AlignedAlloc(BlockSize, 4096);
            try
            {
                var buffer = new Span<byte>(mem, BlockSize);
                long offset = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = image.ReadFull(buffer);
                    if (n == 0)
                        break;
                    var toWrite = (n + sector - 1) / sector * sector;
                    if (toWrite > n)
                        buffer[n..toWrite].Clear();
                    if (offset + toWrite > length)
                        throw new IOException(
                            $"The image is larger than the drive ({DiskInfo.FormatSize(length)}). Use a bigger drive.");

                    RandomAccess.Write(drive, buffer[..toWrite], offset);
                    offset += toWrite;
                    progress.Report(new TransferProgress("Writing", image.CompressedPosition, image.CompressedLength));
                    if (n < BlockSize)
                        break;
                }
            }
            finally
            {
                NativeMemory.AlignedFree(mem);
            }

            Native.FlushFileBuffers(drive);
            Native.Ioctl(drive, Native.IOCTL_DISK_UPDATE_PROPERTIES);
        }
        finally
        {
            foreach (var v in lockedVolumes)
            {
                Native.Ioctl(v, Native.FSCTL_UNLOCK_VOLUME);
                v.Dispose();
            }
        }
    }

    static int SectorSize(SafeFileHandle drive)
    {
        // DISK_GEOMETRY_EX starts with DISK_GEOMETRY; BytesPerSector is at offset 20.
        var g = Native.Ioctl(drive, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 256);
        var bps = g is { Length: >= 24 } ? BitConverter.ToInt32(g, 20) : 512;
        return bps is 512 or 4096 ? bps : 512;
    }

    /// <summary>
    /// Locks and dismounts every volume on the disk so Windows lets us overwrite
    /// it. The handles stay open (and locked) until the write finishes.
    /// </summary>
    static List<SafeFileHandle> LockAndDismountVolumes(int diskNumber)
    {
        var locked = new List<SafeFileHandle>();
        try
        {
            foreach (var volume in Native.ListVolumes())
            {
                var path = volume.TrimEnd('\\');
                var h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0,
                    IntPtr.Zero);
                if (h.IsInvalid)
                {
                    h.Dispose();
                    continue;
                }
                if (!Native.VolumeDiskNumbers(h).Contains(diskNumber))
                {
                    h.Dispose();
                    continue;
                }

                var ok = false;
                for (var attempt = 0; attempt < 10 && !ok; attempt++)
                {
                    ok = Native.Ioctl(h, Native.FSCTL_LOCK_VOLUME) is not null;
                    if (!ok)
                        Thread.Sleep(500);
                }
                if (!ok)
                {
                    var where = string.Join(" ", Native.VolumeMountPoints(volume));
                    h.Dispose();
                    throw new IOException(
                        $"Could not lock volume {(where.Length > 0 ? where : path)}. Close any program or window using that drive and try again.");
                }
                Native.IoctlOrThrow(h, Native.FSCTL_DISMOUNT_VOLUME, "Dismounting a volume");
                locked.Add(h);
            }
            return locked;
        }
        catch
        {
            foreach (var h in locked)
                h.Dispose();
            throw;
        }
    }
}
