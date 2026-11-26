using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Automation;
using VFlasher.Core;
using VFlasher.Core.Qdl;
using Xunit.Abstractions;
using static VFlasher.Core.Tests.QdlFixtures;

namespace VFlasher.Windows.Tests;

/// <summary>
/// A virtual disk (VHD) attached with diskpart, so the real drive-writing code
/// can be tested on \\.\PhysicalDriveN without touching a real drive.
/// </summary>
sealed class VirtualDisk : IDisposable
{
    public const long Size = 64L << 20;
    readonly string file;
    public DiskInfo Disk { get; }

    public VirtualDisk(bool formatted)
    {
        file = Path.Combine(Path.GetTempPath(), $"vflasher-test-{Guid.NewGuid():N}.vhd");
        var script = $"""
            create vdisk file="{file}" maximum=64 type=fixed
            select vdisk file="{file}"
            attach vdisk
            """;
        if (formatted)
            script += "\nconvert mbr\ncreate partition primary\nformat fs=fat32 quick label=VFTEST\nassign\n";
        Diskpart(script);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        DiskInfo? disk = null;
        while (disk == null && DateTime.UtcNow < deadline)
        {
            disk = DiskInfo.Enumerate().SingleOrDefault(d => d.Size == Size);
            if (disk == null || formatted && disk.MountPoints.Count == 0)
            {
                disk = null;
                Thread.Sleep(500);
            }
        }
        Disk = disk ?? throw new InvalidOperationException("the attached VHD did not show up as a disk");
    }

    public static string Diskpart(string script)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, script);
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe", ["/s", path])
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"diskpart failed ({p.ExitCode}):\n{output}");
            return output;
        }
        finally
        {
            File.Delete(path);
        }
    }

    public byte[] Read(long offset, int length)
    {
        using var h = File.OpenHandle(Disk.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[length];
        var done = 0;
        while (done < length)
            done += RandomAccess.Read(h, buf.AsSpan(done), offset + done);
        return buf;
    }

    public void Dispose()
    {
        try { Diskpart($"select vdisk file=\"{file}\"\ndetach vdisk\n"); } catch (InvalidOperationException) { }
        try { File.Delete(file); } catch (IOException) { }
    }
}

public class DriveWritingTests(ITestOutputHelper output)
{
    static string WriteGz(byte[] raw)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vflasher-img-{Guid.NewGuid():N}.raw.gz");
        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            gz.Write(raw);
        return path;
    }

    static void Flash(DiskInfo disk, string image)
    {
        using var dec = new ImageDecompressor(image, "raw.gz");
        RawDiskWriter.Write(disk, dec, new Progress<TransferProgress>(), CancellationToken.None);
    }

    [Theory]
    [InlineData(false, 8 << 20)]
    [InlineData(true, 8 << 20)]          // a mounted FAT32 volume must be locked and dismounted first
    [InlineData(false, (8 << 20) + 100)] // a partial last sector is padded with zeros
    public void WritesImageToDisk(bool formatted, int size)
    {
        using var vd = new VirtualDisk(formatted);
        output.WriteLine($"{vd.Disk}");
        Assert.False(vd.Disk.IsSystem);
        if (formatted)
            Assert.NotEmpty(vd.Disk.MountPoints);

        var raw = RandomNumberGenerator.GetBytes(size);
        var image = WriteGz(raw);
        try
        {
            Flash(vd.Disk, image);
            var padded = (size + 511) / 512 * 512;
            var back = vd.Read(0, padded);
            Assert.Equal(raw, back[..size]);
            Assert.All(back[size..], b => Assert.Equal(0, b));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void RefusesImageLargerThanDisk()
    {
        using var vd = new VirtualDisk(false);
        var image = WriteGz(new byte[VirtualDisk.Size + (4 << 20)]);
        try
        {
            var e = Assert.Throws<IOException>(() => Flash(vd.Disk, image));
            Assert.Contains("larger than the drive", e.Message);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void RefusesSystemDisk()
    {
        // A made-up disk number: the check must happen before anything is opened.
        var fake = new DiskInfo(9999, "System", 1L << 40, "NVMe", false, true, []);
        var image = WriteGz(new byte[4096]);
        try
        {
            Assert.Throws<InvalidOperationException>(() => Flash(fake, image));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void ListsDisksAndMarksSystemDisk()
    {
        var disks = DiskInfo.Enumerate();
        foreach (var d in disks)
            output.WriteLine($"{d}  system={d.IsSystem} external={d.LooksExternal}");
        Assert.Contains(disks, d => d.IsSystem);
    }
}

public class EdlTests(ITestOutputHelper output)
{
    /// <summary>Folder with qdl\ and drivers\ from fetch-edl-tools.ps1 (set in CI).</summary>
    static string? Tools => Environment.GetEnvironmentVariable("VFLASHER_EDL_TOOLS");

    [Fact]
    public void DetectsNoBoardWithoutCrashing() => Assert.False(WindowsEdlDevice.IsPresent());

    [Fact]
    public async Task InstallsEdlDriver()
    {
        if (Tools == null)
            return;
        var dest = Path.Combine(AppContext.BaseDirectory, "drivers");
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(Path.Combine(Tools, "drivers")))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);

        var message = await EdlDriver.InstallAsync(CancellationToken.None);
        output.WriteLine(message);

        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), ["/enum-drivers"])
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var drivers = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Contains("qcserlib.inf", drivers, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shipped qdl.exe runs (a 32-bit x86 build, emulated on Arm) and accepts
    /// the exact read and write commands QdlFlashJob sends, in --dry-run mode.
    /// </summary>
    [Fact]
    public async Task QdlAcceptsVFlasherCommands()
    {
        if (Tools == null)
            return;
        var qdl = new ProcessQdlRunner(Path.Combine(Tools, "qdl", "qdl.exe"));
        var dir = Directory.CreateTempSubdirectory("vflasher-qdl-").FullName;
        var prog = Path.Combine(dir, "prog_firehose_ddr.elf");
        File.WriteAllBytes(prog, RandomNumberGenerator.GetBytes(4096));
        foreach (var f in new[] { "p1.img", "p2.img", "p3.img", "gpt-backup.bin", "gpt-main.bin" })
            File.WriteAllBytes(Path.Combine(dir, f), new byte[64 * 512]);
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Exactly what QdlFlashJob runs: bare names inside the work folder.
        string[] Common() => ["--dry-run", "--storage", "emmc", "prog_firehose_ddr.elf"];
        var read = await qdl.RunAsync([.. Common(), "read", "0/0+34", "board-gpt.bin"], dir, lines.Add, cts.Token);
        var write = await qdl.RunAsync([
            .. Common(),
            "write", "0/6031360", "p1.img",
            "write", "0/7141376", "p2.img",
            "write", "0/11237376", "p3.img",
            "write", "0/122142701", "gpt-backup.bin",
            "write", "0/0", "gpt-main.bin",
        ], dir, lines.Add, cts.Token);
        foreach (var l in lines)
            output.WriteLine(l);
        Directory.Delete(dir, true);
        Assert.Equal(0, read);
        Assert.Equal(0, write);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSizeW(string path, out uint high);

    [Fact]
    public void UnpacksImageIntoSparseFiles()
    {
        var (image, esp, boot, root) = FedoraLikeImage();
        var dir = Directory.CreateTempSubdirectory("vflasher-sparse-").FullName;
        try
        {
            var parts = DiskImageSplitter.Split(new MemoryStream(image), dir, SparseFiles.MarkSparse);
            Assert.Equal(esp, File.ReadAllBytes(parts[0].Path));
            Assert.Equal(boot, File.ReadAllBytes(parts[1].Path));
            Assert.Equal(root, File.ReadAllBytes(parts[2].Path));
            var low = GetCompressedFileSizeW(parts[2].Path, out var high);
            var onDisk = ((long)high << 32) | low;
            output.WriteLine($"root: {root.Length} bytes, {onDisk} on disk");
            Assert.True(onDisk <= root.Length - (16 << 20), "the 16 MiB zero block should be a hole");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>Starts the published vflasher.exe and checks its window with UI Automation.</summary>
public class AppTests(ITestOutputHelper output)
{
    static string? Exe => Environment.GetEnvironmentVariable("VFLASHER_EXE");

    [Fact]
    public void PreselectsVentunoQAndOffersEdlFlashing()
    {
        if (Exe == null)
            return;
        using var p = Process.Start(new ProcessStartInfo(Exe, "arduino ventuno-q fedora") { UseShellExecute = true })!;
        try
        {
            AutomationElement? window = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                p.Refresh();
                Assert.False(p.HasExited, $"vflasher.exe exited with code {(p.HasExited ? p.ExitCode : 0)}");
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    window = AutomationElement.FromHandle(p.MainWindowHandle);
                    var texts = Texts(window);
                    if (texts.Any(t => t.Contains("EDL mode")))
                        break;
                }
                Thread.Sleep(500);
            }
            Assert.NotNull(window);
            Assert.Equal("VFlasher", window!.Current.Name);
            foreach (var t in Texts(window))
                output.WriteLine($"text: {t}");

            Assert.Contains(Texts(window), t => t.Contains("Fedora Server 45 for Arduino VENTUNO Q"));
            Assert.Contains(Texts(window), t => t.Contains("flashed over USB-C in EDL mode"));
            Assert.DoesNotContain(Texts(window), t => t.Contains("qdl.exe was not found"));

            var combos = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)).Cast<AutomationElement>().ToList();
            var selected = combos.Select(Selected).ToList();
            output.WriteLine("combo boxes: " + string.Join(" | ", selected));
            Assert.Contains("arduino/ventuno-q", selected);
            // The drive picker is not used for EDL boards.
            Assert.Contains(combos, c => !c.Current.IsEnabled);

            var flash = window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Flash")));
            Assert.NotNull(flash);
            Assert.True(flash.Current.IsEnabled, "Flash should be enabled when qdl.exe is installed");
        }
        finally
        {
            if (!p.HasExited)
                p.Kill();
        }
    }

    static List<string> Texts(AutomationElement window) =>
        window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(e => e.Current.Name).ToList();

    static string Selected(AutomationElement combo)
    {
        if (combo.TryGetCurrentPattern(SelectionPattern.Pattern, out var sp))
        {
            var sel = ((SelectionPattern)sp).Current.GetSelection();
            if (sel.Length > 0)
                return sel[0].Current.Name;
        }
        if (combo.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            return ((ValuePattern)vp).Current.Value;
        return "";
    }
}
