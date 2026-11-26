using System.Buffers.Binary;

namespace VFlasher.Core.Qdl;

public sealed record ImagePartition(int Number, long Start, long Sectors, string Path);

/// <summary>
/// Splits a raw disk image (MBR or GPT) into one file per partition, in a
/// single pass over the decompressed stream. All-zero blocks are skipped
/// (left as holes), so the files stay small on file systems with sparse files.
/// </summary>
public static class DiskImageSplitter
{
    const int Sector = 512;
    const int Mib = 1 << 20;

    public static List<(long Start, long Sectors)> ReadPartitionTable(ReadOnlySpan<byte> head)
    {
        if (head.Length < 2 * Sector || head[510] != 0x55 || head[511] != 0xAA)
            throw new InvalidDataException("the image has no partition table");
        var mbr = new List<(byte Type, long Start, long Sectors)>();
        for (var i = 0; i < 4; i++)
        {
            var e = head.Slice(446 + 16 * i, 16);
            mbr.Add((e[4], BinaryPrimitives.ReadUInt32LittleEndian(e[8..]), BinaryPrimitives.ReadUInt32LittleEndian(e[12..])));
        }
        var parts = new List<(long, long)>();
        if (mbr.Any(p => p.Type == 0xEE))
        {
            var h = head.Slice(Sector, 92);
            if (!h[..8].SequenceEqual("EFI PART"u8))
                throw new InvalidDataException("bad GPT in image");
            var entLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[72..]);
            var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[80..]);
            var esz = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[84..]);
            if (entLba * Sector + (long)n * esz > head.Length)
                throw new InvalidDataException("the image's GPT is larger than expected");
            for (var i = 0; i < n; i++)
            {
                var e = head.Slice((int)(entLba * Sector) + i * esz, esz);
                if (e[..16].IndexOfAnyExcept((byte)0) < 0)
                    continue;
                var first = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[32..]);
                var last = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[40..]);
                parts.Add((first, last - first + 1));
            }
        }
        else
        {
            parts.AddRange(mbr.Where(p => p.Type != 0 && p.Sectors > 0).Select(p => (p.Start, p.Sectors)));
        }
        parts.Sort();
        return parts;
    }

    /// <param name="prepareFile">Called on each new output file, e.g. to mark it sparse on Windows.</param>
    /// <param name="onProgress">Called after each block with the number of image bytes read.</param>
    public static List<ImagePartition> Split(Stream raw, string outDir, Action<FileStream>? prepareFile = null,
        Action<long>? onProgress = null, CancellationToken ct = default)
    {
        var head = new byte[Mib];
        ReadExact(raw, head);
        long pos = head.Length;
        var table = ReadPartitionTable(head);

        var result = new List<ImagePartition>();
        var buffer = new byte[Mib];
        for (var i = 0; i < table.Count; i++)
        {
            var (start, sectors) = table[i];
            var startByte = start * Sector;
            if (startByte < pos)
                throw new InvalidDataException("overlapping partitions in image");
            while (pos < startByte)
            {
                ct.ThrowIfCancellationRequested();
                var n = (int)Math.Min(buffer.Length, startByte - pos);
                ReadExact(raw, buffer.AsSpan(0, n));
                pos += n;
                onProgress?.Invoke(pos);
            }

            var path = Path.Combine(outDir, $"p{i + 1}.img");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, Mib))
            {
                prepareFile?.Invoke(fs);
                var length = sectors * Sector;
                var left = length;
                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = (int)Math.Min(buffer.Length, left);
                    var block = buffer.AsSpan(0, n);
                    ReadExact(raw, block);
                    if (block.IndexOfAnyExcept((byte)0) < 0)
                        fs.Seek(n, SeekOrigin.Current); // leave a hole
                    else
                        fs.Write(block);
                    left -= n;
                    pos += n;
                    onProgress?.Invoke(pos);
                }
                fs.SetLength(length);
            }
            result.Add(new ImagePartition(i + 1, start, sectors, path));
        }
        return result;
    }

    static void ReadExact(Stream s, Span<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var n = s.Read(buffer);
            if (n == 0)
                throw new InvalidDataException("the image ended before its last partition");
            buffer = buffer[n..];
        }
    }
}
