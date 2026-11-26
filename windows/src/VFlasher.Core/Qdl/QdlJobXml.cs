using System.Xml.Linq;

namespace VFlasher.Core.Qdl;

/// <summary>
/// qdl job files (the format of Qualcomm's rawprogram*.xml), as Arduino's
/// flasher uses them. Unlike qdl's raw "read"/"write" commands they state the
/// sector size, so qdl does not have to detect it (and --dry-run works).
/// Same files as the Linux vflasher script writes.
/// </summary>
public static class QdlJobXml
{
    public const int SectorSize = 512;

    public sealed record Write(string Label, string File, long StartSector);

    static XDocument Doc(IEnumerable<XElement> items) => new(new XDeclaration("1.0", null, null), new XElement("data", items));

    static long Sectors(string path) => (new FileInfo(path).Length + SectorSize - 1) / SectorSize;

    /// <summary>Reads <paramref name="sectors"/> sectors at <paramref name="start"/> of LUN 0 into <paramref name="file"/>.</summary>
    public static void WriteReadJob(string path, string file, long start, long sectors) =>
        Doc([
            new XElement("read",
                new XAttribute("SECTOR_SIZE_IN_BYTES", SectorSize),
                new XAttribute("filename", file),
                new XAttribute("physical_partition_number", 0),
                new XAttribute("num_partition_sectors", sectors),
                new XAttribute("start_sector", start)),
        ]).Save(path);

    /// <summary>
    /// Writes each file (relative to the job's folder) at its start sector of
    /// LUN 0, in order.
    /// </summary>
    public static void WriteProgramJob(string path, IEnumerable<Write> writes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Doc(writes.Select(w => new XElement("program",
            new XAttribute("SECTOR_SIZE_IN_BYTES", SectorSize),
            new XAttribute("file_sector_offset", 0),
            new XAttribute("filename", w.File),
            new XAttribute("label", w.Label),
            new XAttribute("num_partition_sectors", Sectors(Path.Combine(dir, w.File))),
            new XAttribute("physical_partition_number", 0),
            new XAttribute("sparse", "false"),
            new XAttribute("start_sector", w.StartSector)))).Save(path);
    }
}
