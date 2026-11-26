using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Hashing;
using System.Text;
using DiscUtils;
using DiscUtils.Fat;
using VFlasher.Core.Qdl;

namespace VFlasher.Core.Tests;

/// <summary>Builds fake boards and images for the qdl tests.</summary>
internal static class QdlFixtures
{
    public const int S = 512;
    public const long DiskSectors = 1048576; // 512 MiB
    static readonly Guid DataType = new("0fc63daf-8483-4772-8e79-3d69d8477de4");
    static readonly Guid RootArm64 = new("b921b045-1df0-41c3-af44-4c6f280d3fae");

    public record Part(string Name, Guid Type, long First, long Last);

    public static List<Part> BoardLayout(string layout = "normal")
    {
        var parts = new List<Part>
        {
            new("xbl", DataType, 2048, 4095),
            new("uefi", DataType, 4096, 8191),
            new("tz", DataType, 8192, 12287),
            new("efi", GptPlanner.EspType, 16384, 16384 + (layout == "small-efi" ? 4096 : 32768) - 1),
            new("persist", DataType, 49152, 53247),
        };
        if (layout != "no-system")
            parts.Add(new("system", RootArm64, 53248, DiskSectors - 34));
        if (layout == "foreign-boot")
            parts.Insert(3, new("boot", GptPlanner.XbootldrType, 14336, 16383));
        return parts;
    }

    /// <summary>The first 34 sectors of a board, with <paramref name="entries"/> entry slots.</summary>
    public static byte[] BoardGpt(List<Part> parts, int entries = 72, int seed = 1)
    {
        var rnd = new Random(seed);
        Guid G() { var b = new byte[16]; rnd.NextBytes(b); return new Guid(b); }
        var ents = new byte[entries * 128];
        for (var i = 0; i < parts.Count; i++)
        {
            var e = ents.AsSpan(i * 128, 128);
            parts[i].Type.TryWriteBytes(e[..16]);
            G().TryWriteBytes(e[16..32]);
            BinaryPrimitives.WriteUInt64LittleEndian(e[32..], (ulong)parts[i].First);
            BinaryPrimitives.WriteUInt64LittleEndian(e[40..], (ulong)parts[i].Last);
            Encoding.Unicode.GetBytes(parts[i].Name).CopyTo(e[56..]);
        }
        var d = new byte[34 * S];
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(446 + 8), 1);
        d[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(446 + 12), uint.MaxValue);
        d[510] = 0x55; d[511] = 0xAA;
        var h = d.AsSpan(S, 92);
        "EFI PART"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], 92);
        BinaryPrimitives.WriteUInt64LittleEndian(h[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(h[32..], (ulong)DiskSectors - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(h[40..], 34);
        BinaryPrimitives.WriteUInt64LittleEndian(h[48..], (ulong)DiskSectors - 34);
        G().TryWriteBytes(h[56..72]);
        BinaryPrimitives.WriteUInt64LittleEndian(h[72..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(h[80..], (uint)entries);
        BinaryPrimitives.WriteUInt32LittleEndian(h[84..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(h[88..], Crc32.HashToUInt32(ents));
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], Crc32.HashToUInt32(h));
        ents.CopyTo(d, 2 * S);
        return d;
    }

    /// <summary>A whole fake eMMC file: GPT (primary + backup) and marked partitions.</summary>
    public static void CreateBoard(string path, string layout = "normal")
    {
        var parts = BoardLayout(layout);
        var gpt = BoardGpt(parts, entries: 128);
        using var f = new FileStream(path, FileMode.Create);
        f.SetLength(DiskSectors * S);
        f.Write(gpt);
        foreach (var p in parts)
        {
            f.Position = p.First * S;
            var mark = Encoding.ASCII.GetBytes($"{p.Name.ToUpperInvariant()}-OLD-");
            var block = new byte[64 * 1024];
            for (var i = 0; i < block.Length; i++) block[i] = mark[i % mark.Length];
            var len = Math.Min((p.Last - p.First + 1) * S, 4L << 20);
            for (long w = 0; w < len; w += block.Length) f.Write(block);
        }
        // Backup GPT: entries then header at the last sector.
        var hdr = gpt.AsSpan(S, 92).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(24), (ulong)DiskSectors - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(72), (ulong)DiskSectors - 33);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16), Crc32.HashToUInt32(hdr));
        f.Position = (DiskSectors - 33) * S;
        f.Write(gpt.AsSpan(2 * S, 32 * S));
        f.Write(hdr);
    }

    /// <summary>An 8 MiB FAT EFI partition with Fedora-style GRUB stubs.</summary>
    public static byte[] Esp()
    {
        var ms = new MemoryStream(new byte[8 << 20]);
        using (var fat = FatFileSystem.FormatPartition(ms, "EFI", Geometry.FromCapacity(8 << 20), 0, 16384, 0))
        {
            fat.CreateDirectory(@"\EFI\fedora");
            fat.CreateDirectory(@"\EFI\BOOT");
            var stub = "search --fs-uuid --set=root 1234\nset prefix=($root)/grub2\nconfigfile ($root)/grub2/grub.cfg\n"u8.ToArray();
            foreach (var p in new[] { @"\EFI\fedora\grub.cfg", @"\EFI\BOOT\grub.cfg" })
                using (var f = fat.OpenFile(p, FileMode.Create, FileAccess.Write))
                    f.Write(stub);
        }
        return ms.ToArray();
    }

    public static string ReadFatFile(byte[] fatImage, string path)
    {
        using var fat = new FatFileSystem(new MemoryStream(fatImage));
        using var f = fat.OpenFile(path, FileMode.Open, FileAccess.Read);
        return new StreamReader(f).ReadToEnd();
    }

    /// <summary>A Fedora-like MBR image: EFI (FAT), /boot, root with an all-zero middle.</summary>
    public static (byte[] Image, byte[] Esp, byte[] Boot, byte[] Root) FedoraLikeImage()
    {
        var esp = Esp();
        var boot = new byte[24 << 20];
        var root = new byte[64 << 20];
        for (var i = 0; i < boot.Length; i++) boot[i] = (byte)"BOOT"[i % 4];
        for (var i = 0; i < root.Length; i++) root[i] = i >= 8 << 20 && i < 24 << 20 ? (byte)0 : (byte)"ROOT"[i % 4];
        long s1 = 2048, s2 = s1 + esp.Length / S, s3 = s2 + boot.Length / S;
        var img = new byte[s3 * S + root.Length];
        void Put(int idx, byte type, long start, byte[] data)
        {
            var e = img.AsSpan(446 + 16 * idx, 16);
            e[4] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)start);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)(data.Length / S));
            data.CopyTo(img, start * S);
        }
        Put(0, 0xEF, s1, esp);
        Put(1, 0x83, s2, boot);
        Put(2, 0x83, s3, root);
        img[510] = 0x55; img[511] = 0xAA;
        return (img, esp, boot, root);
    }

    /// <summary>A tar.zst with a filler entry, then the programmer at <paramref name="member"/>.</summary>
    public static byte[] ProgrammerArchive(string member, byte[] programmer)
    {
        var ms = new MemoryStream();
        using (var zst = new ZstdSharp.CompressionStream(ms, leaveOpen: true))
        using (var tar = new TarWriter(zst, TarEntryFormat.Gnu, leaveOpen: true))
        {
            var filler = new byte[3_000_000];
            new Random(5).NextBytes(filler);
            tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "./b/flash/aaa-filler.bin") { DataStream = new MemoryStream(filler) });
            tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, member) { DataStream = new MemoryStream(programmer) });
        }
        return ms.ToArray();
    }

    public static string RepoRoot()
    {
        var dir = ManifestCatalog.FindLocalImagesDir(AppContext.BaseDirectory)
                  ?? throw new InvalidOperationException("repository not found");
        return Path.GetDirectoryName(dir)!;
    }

    /// <summary>The python helper embedded in the Linux vflasher script.</summary>
    public static string PythonHelper()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "vflasher"));
        var marker = script.IndexOf("read -r -d '' PY_HELPER <<'PY'", StringComparison.Ordinal);
        var start = script.IndexOf('\n', marker) + 1;
        var end = script.IndexOf("\nPY\n", start, StringComparison.Ordinal);
        return script[start..end];
    }

    public static bool HasTool(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Any(d => File.Exists(Path.Combine(d, name)));
}
