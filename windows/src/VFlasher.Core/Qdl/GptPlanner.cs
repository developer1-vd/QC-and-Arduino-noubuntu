using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace VFlasher.Core.Qdl;

/// <summary>Where the image's partitions go on the board, and the new partition table.</summary>
public sealed record GptPlan(
    long EspStart, long BootStart, long RootStart, long RootSectors, long BackupStart, long DiskSectors,
    byte[] PrimaryGpt, byte[] BackupGpt);

/// <summary>
/// Rewrites the OS area at the end of a Qualcomm board's GPT, keeping every
/// firmware partition. Same rules as the planner in the Linux vflasher script:
///
///   before:  [firmware ...] [efi] [persist] [system ..................]
///   after:   [firmware ...] [efi] [persist] [boot] [system ............]
///
/// A board flashed this way before ([boot][system] at the end) is planned
/// again from the start of 'boot', so re-flashing gives the same layout.
/// </summary>
public static class GptPlanner
{
    public const int Sector = 512;
    const int Align = 1024 * 1024 / Sector;

    public static readonly Guid EspType = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid XbootldrType = new("bc13c2ff-59e6-4262-a352-b275fd6f7172");
    public static readonly Guid LvmType = new("e6d6d379-f507-44c2-a23c-238f2a3df928");
    public static readonly Guid LinuxType = new("0fc63daf-8483-4772-8e79-3d69d8477de4");

    sealed class Entry
    {
        public Guid Type;
        public Guid Unique;
        public long First, Last;
        public ulong Attributes;
        public byte[] Name = new byte[72];

        public bool Used => Type != Guid.Empty;
        public string NameText => Encoding.Unicode.GetString(Name).TrimEnd('\0');

        public static Entry Read(ReadOnlySpan<byte> b) => new()
        {
            Type = new Guid(b[..16]),
            Unique = new Guid(b[16..32]),
            First = (long)BinaryPrimitives.ReadUInt64LittleEndian(b[32..]),
            Last = (long)BinaryPrimitives.ReadUInt64LittleEndian(b[40..]),
            Attributes = BinaryPrimitives.ReadUInt64LittleEndian(b[48..]),
            Name = b[56..128].ToArray(),
        };

        public void Write(Span<byte> b)
        {
            Type.TryWriteBytes(b[..16]);
            Unique.TryWriteBytes(b[16..32]);
            BinaryPrimitives.WriteUInt64LittleEndian(b[32..], (ulong)First);
            BinaryPrimitives.WriteUInt64LittleEndian(b[40..], (ulong)Last);
            BinaryPrimitives.WriteUInt64LittleEndian(b[48..], Attributes);
            Name.CopyTo(b[56..128]);
        }
    }

    static uint Crc(ReadOnlySpan<byte> b) => Crc32.HashToUInt32(b);

    static GptPlanException Fail(string msg) => new(msg);

    /// <param name="boardGpt">The first 34 sectors of the board's storage.</param>
    /// <param name="newGuid">Unique GUID for the new 'boot' partition (injectable for tests).</param>
    public static GptPlan Plan(byte[] boardGpt, string espName, string rootName, long bootSectors, long espMin,
        long rootMin, string rootType, Func<Guid>? newGuid = null)
    {
        if (boardGpt.Length < 34 * Sector)
            throw Fail("the partition table read from the board is too short");
        var hdr = boardGpt.AsSpan(Sector, 92).ToArray();
        var sig = Encoding.ASCII.GetString(hdr, 0, 8);
        var hsize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12));
        var hcrc = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(16));
        var myLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(24));
        var altLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(32));
        var last = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(48));
        var entLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(72));
        var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(80));
        var esz = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(84));
        var ecrc = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(88));

        if (sig != "EFI PART" || hsize != 92 || myLba != 1 || entLba != 2 || esz != 128)
            throw Fail("unexpected partition table on the board (not a standard GPT)");
        var check = (byte[])hdr.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(check.AsSpan(16), 0);
        if (Crc(check) != hcrc)
            throw Fail("the board's GPT header checksum is wrong");
        var entSectors = (n * esz + Sector - 1) / Sector;
        if (2 + entSectors > 34)
            throw Fail("the board's GPT has more partition entries than expected");
        var raw = boardGpt.AsSpan((int)entLba * Sector, n * esz).ToArray();
        if (Crc(raw) != ecrc)
            throw Fail("the board's GPT entry checksum is wrong");
        if (altLba <= last)
            throw Fail("the board's GPT does not describe the whole eMMC");

        var entries = Enumerable.Range(0, n).Select(i => Entry.Read(raw.AsSpan(i * esz, esz))).ToArray();
        var used = Enumerable.Range(0, n).Where(i => entries[i].Used).ToList();
        int One(string name)
        {
            var idx = used.Where(i => entries[i].NameText == name).ToList();
            if (idx.Count != 1)
                throw Fail($"expected exactly one '{name}' partition on the board, found {idx.Count}");
            return idx[0];
        }

        int esp = One(espName), root = One(rootName);
        var rstart = entries[root].First;
        if (used.Any(i => entries[i].NameText == "boot"))
        {
            var b = One("boot");
            var later = used.Any(i => i != b && i != root && entries[i].Last >= entries[b].First);
            if (entries[b].Type != XbootldrType || entries[b].Last >= rstart || later)
                throw Fail("the board has a 'boot' partition that VFlasher did not create; not changing it");
            rstart = entries[b].First;
            entries[b] = new Entry();
            used.Remove(b);
        }
        if (entries[esp].Type != EspType)
            throw Fail($"'{espName}' is not an EFI system partition");
        var espSize = entries[esp].Last - entries[esp].First + 1;
        if (espSize < espMin)
            throw Fail($"'{espName}' is too small ({espSize} < {espMin} sectors)");
        foreach (var i in used)
            if (i != root && entries[i].Last >= rstart)
                throw Fail($"'{rootName}' is not the last partition on the board");

        var bootStart = (rstart + Align - 1) / Align * Align;
        var rootStart = (bootStart + bootSectors + Align - 1) / Align * Align;
        if (last - rootStart + 1 < rootMin)
            throw Fail("the eMMC is too small for this image");
        var free = Enumerable.Range(0, n).FirstOrDefault(i => !entries[i].Used, -1);
        if (free < 0)
            throw Fail("no free partition slot in the board's GPT");

        entries[root].Type = rootType switch
        {
            "lvm" => LvmType,
            "linux" => LinuxType,
            _ => throw Fail($"unknown QDL_ROOT_TYPE '{rootType}'"),
        };
        entries[root].First = rootStart;
        entries[root].Last = last;
        var name = new byte[72];
        Encoding.Unicode.GetBytes("boot").CopyTo(name, 0);
        entries[free] = new Entry
        {
            Type = XbootldrType, Unique = (newGuid ?? Guid.NewGuid)(), First = bootStart,
            Last = bootStart + bootSectors - 1, Name = name,
        };

        var newRaw = new byte[entSectors * Sector];
        for (var i = 0; i < n; i++)
            entries[i].Write(newRaw.AsSpan(i * esz, esz));
        var newCrc = Crc(newRaw.AsSpan(0, n * esz));

        byte[] Header(long my, long alt, long entriesLba)
        {
            var h = new byte[Sector];
            hdr.CopyTo(h, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(24), (ulong)my);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), (ulong)alt);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(72), (ulong)entriesLba);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), newCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc(h.AsSpan(0, 92)));
            return h;
        }

        var primary = boardGpt.AsSpan(0, Sector).ToArray()
            .Concat(Header(1, altLba, 2)).Concat(newRaw).ToArray();
        var backup = newRaw.Concat(Header(altLba, 1, altLba - entSectors)).ToArray();

        return new GptPlan(entries[esp].First, bootStart, rootStart, last - rootStart + 1, altLba - entSectors,
            altLba + 1, primary, backup);
    }
}

public sealed class GptPlanException(string message) : Exception(message);
