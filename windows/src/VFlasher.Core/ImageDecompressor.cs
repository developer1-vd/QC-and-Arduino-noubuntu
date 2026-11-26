using System.IO.Compression;
using SharpCompress.Compressors.Xz;
using ZstdSharp;

namespace VFlasher.Core;

/// <summary>
/// Opens a downloaded image as a stream of raw disk bytes. Progress is measured on
/// the compressed file, since the unpacked size is not known in advance.
/// </summary>
public sealed class ImageDecompressor : IDisposable
{
    readonly FileStream file;

    public ImageDecompressor(string path, string format)
    {
        file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        Raw = format switch
        {
            "raw" => file,
            "raw.xz" => new XZStream(file),
            "raw.gz" => new GZipStream(file, CompressionMode.Decompress),
            "raw.zst" => new DecompressionStream(file),
            _ => throw new NotSupportedException($"unsupported IMAGE_FORMAT '{format}'"),
        };
    }

    /// <summary>The decompressed disk image.</summary>
    public Stream Raw { get; }

    public long CompressedLength => file.Length;
    public long CompressedPosition => file.Position;

    /// <summary>Reads until <paramref name="buffer"/> is full or the image ends.</summary>
    public int ReadFull(Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = Raw.Read(buffer[total..]);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(Raw, file))
            Raw.Dispose();
        file.Dispose();
    }
}
