using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using VFlasher.Core.Qdl;
using static VFlasher.Core.Tests.QdlFixtures;

namespace VFlasher.Core.Tests;

public class GptPlannerTests
{
    static readonly Guid BootGuid = new("11111111-2222-3333-4444-555555555555");

    static GptPlan PlanFor(byte[] gpt) =>
        GptPlanner.Plan(gpt, "efi", "system", 4096000 / 100, 16384, 64 * 2048, "lvm", () => BootGuid);

    [Fact]
    public void KeepsFirmwareAndSplitsSystem()
    {
        var gpt = BoardGpt(BoardLayout());
        var plan = PlanFor(gpt);
        Assert.Equal(16384, plan.EspStart);
        Assert.Equal(53248, plan.BootStart);
        Assert.Equal(0, plan.RootStart % 2048);
        Assert.Equal(DiskSectors - 34, plan.RootStart + plan.RootSectors - 1);
        Assert.Equal(DiskSectors, plan.DiskSectors);
        // Protective MBR and the firmware entries (first 5 x 128 bytes) are kept.
        Assert.Equal(gpt.AsSpan(0, S).ToArray(), plan.PrimaryGpt.AsSpan(0, S).ToArray());
        Assert.Equal(gpt.AsSpan(2 * S, 5 * 128).ToArray(), plan.PrimaryGpt.AsSpan(2 * S, 5 * 128).ToArray());
    }

    [Fact]
    public void ReflashGivesSameLayout()
    {
        var first = PlanFor(BoardGpt(BoardLayout()));
        var again = PlanFor(first.PrimaryGpt.Concat(new byte[34 * S - first.PrimaryGpt.Length]).ToArray());
        Assert.Equal(first.PrimaryGpt, again.PrimaryGpt);
        Assert.Equal(first.BackupGpt, again.BackupGpt);
    }

    [Theory]
    [InlineData("no-system", "expected exactly one 'system' partition on the board, found 0")]
    [InlineData("small-efi", "'efi' is too small (4096 < 16384 sectors)")]
    [InlineData("foreign-boot", "the board has a 'boot' partition that VFlasher did not create; not changing it")]
    public void RefusesUnexpectedBoards(string layout, string message)
    {
        var e = Assert.Throws<GptPlanException>(() => PlanFor(BoardGpt(BoardLayout(layout))));
        Assert.Equal(message, e.Message);
    }

    [Fact]
    public void RejectsCorruptTable()
    {
        var gpt = BoardGpt(BoardLayout());
        gpt[2 * S + 40] ^= 1;
        Assert.Throws<GptPlanException>(() => PlanFor(gpt));
    }

    /// <summary>The C# planner and the python one in the Linux script must produce identical tables.</summary>
    [Theory]
    [InlineData("normal")]
    [InlineData("reflash")]
    [InlineData("no-system")]
    [InlineData("small-efi")]
    [InlineData("foreign-boot")]
    public void MatchesLinuxScript(string layout)
    {
        if (!HasTool("python3"))
            return; // python3 is on every Linux CI runner; skipped elsewhere
        var dir = Directory.CreateTempSubdirectory("vflasher-parity-").FullName;
        try
        {
            var gpt = layout == "reflash"
                ? PlanFor(BoardGpt(BoardLayout())).PrimaryGpt.Concat(new byte[20 * S]).Take(34 * S).ToArray()
                : BoardGpt(BoardLayout(layout));
            var gptFile = Path.Combine(dir, "board.bin");
            File.WriteAllBytes(gptFile, gpt);

            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-I", "-c", PythonHelper(), "plan", gptFile, "efi", "system", $"{4096000 / 100}", "16384", $"{64 * 2048}", "lvm", dir })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                var e = Assert.Throws<GptPlanException>(() => PlanFor(gpt));
                Assert.Equal($"vflasher: error: {e.Message}", stderr.Trim());
                return;
            }

            var py = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(' ')).ToDictionary(a => a[0], a => long.Parse(a[1]));
            var pyMain = File.ReadAllBytes(Path.Combine(dir, "gpt-main.bin"));
            var pyBackup = File.ReadAllBytes(Path.Combine(dir, "gpt-backup.bin"));
            // Use python's random GUID for the new 'boot' partition so the bytes can match.
            var bootSlot = Enumerable.Range(0, 72).First(i =>
                new Guid(pyMain.AsSpan(2 * S + i * 128, 16)) == GptPlanner.XbootldrType);
            var pyGuid = new Guid(pyMain.AsSpan(2 * S + bootSlot * 128 + 16, 16));

            var cs = GptPlanner.Plan(gpt, "efi", "system", 4096000 / 100, 16384, 64 * 2048, "lvm", () => pyGuid);
            Assert.Equal(pyMain, cs.PrimaryGpt);
            Assert.Equal(pyBackup, cs.BackupGpt);
            Assert.Equal(py["ESP_START"], cs.EspStart);
            Assert.Equal(py["BOOT_START"], cs.BootStart);
            Assert.Equal(py["ROOT_START"], cs.RootStart);
            Assert.Equal(py["ROOT_SECTORS"], cs.RootSectors);
            Assert.Equal(py["BACKUP_START"], cs.BackupStart);
            Assert.Equal(py["DISK_SECTORS"], cs.DiskSectors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class QdlPieceTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("vflasher-qdl-").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void AddsDevicetreeToBothStubsOnce()
    {
        var path = Path.Combine(dir, "esp.img");
        File.WriteAllBytes(path, Esp());
        Assert.Equal(2, GrubStubEditor.AddDevicetree(path, "qcom/board.dtb").Count);
        Assert.Empty(GrubStubEditor.AddDevicetree(path, "qcom/board.dtb")); // already there

        var img = File.ReadAllBytes(path);
        foreach (var stub in new[] { @"\EFI\fedora\grub.cfg", @"\EFI\BOOT\grub.cfg" })
            Assert.Equal(
                "search --fs-uuid --set=root 1234\nset prefix=($root)/grub2\ndevicetree ($root)/dtb/qcom/board.dtb\nconfigfile ($root)/grub2/grub.cfg\n",
                ReadFatFile(img, stub));
    }

    [Fact]
    public void SplitsMbrImage()
    {
        var (image, esp, boot, root) = FedoraLikeImage();
        var parts = DiskImageSplitter.Split(new MemoryStream(image), dir);
        Assert.Equal(3, parts.Count);
        Assert.Equal(esp, File.ReadAllBytes(parts[0].Path));
        Assert.Equal(boot, File.ReadAllBytes(parts[1].Path));
        Assert.Equal(root, File.ReadAllBytes(parts[2].Path));
        Assert.Equal(boot.Length / S, parts[1].Sectors);
    }

    [Fact]
    public void SplitRejectsTruncatedImage()
    {
        var (image, _, _, _) = FedoraLikeImage();
        Assert.Throws<InvalidDataException>(() =>
            DiskImageSplitter.Split(new MemoryStream(image[..^4096]), dir));
    }

    [Fact]
    public async Task FetchesProgrammerFromArchive()
    {
        var prog = RandomNumberGenerator.GetBytes(900_000);
        var archive = ProgrammerArchive("./b/flash/prog_firehose_ddr.elf", prog);
        var m = Manifest(Convert.ToHexStringLower(SHA256.HashData(prog)));
        var http = new HttpClient(new StaticServer(archive));

        var file = await ProgrammerFetcher.GetAsync(http, m, dir);
        Assert.Equal(prog, File.ReadAllBytes(file));

        var bad = Manifest(new string('0', 64));
        File.Delete(file);
        await Assert.ThrowsAsync<InvalidDataException>(() => ProgrammerFetcher.GetAsync(http, bad, dir));
    }

    static ImageManifest Manifest(string programmerSha) => ImageManifest.Parse("t", "b", "o", $"""
        NAME=T
        IMAGE_URL=https://example.org/i.raw.zst
        IMAGE_FILE=i.raw.zst
        IMAGE_SHA256={new string('a', 64)}
        IMAGE_FORMAT=raw.zst
        FLASH_METHOD=qdl
        QDL_PROGRAMMER_URL=https://example.org/bundle.tar.zst
        QDL_PROGRAMMER_PATH=./b/flash/prog_firehose_ddr.elf
        QDL_PROGRAMMER_SHA256={programmerSha}
        """);

    internal sealed class StaticServer(params byte[][] bodies) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var body = bodies.Length == 1 ? bodies[0]
                : request.RequestUri!.AbsolutePath.EndsWith(".tar.zst") ? bodies[1] : bodies[0];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}

/// <summary>
/// The whole C# EDL flow against a fake eMMC file, through tests/fake-qdl (the
/// same stand-in the Linux script's tests use). Linux only.
/// </summary>
public class QdlFlashJobTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("vflasher-job-").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    /// <summary>Records qdl arguments (qdl.exe reads "C:\..." as an image id, so only bare names are allowed).</summary>
    sealed class RecordingQdl(IQdlRunner inner) : IQdlRunner
    {
        public List<string> Args { get; } = [];
        public Task<int> RunAsync(IReadOnlyList<string> args, string workingDirectory, Action<string> onOutput,
            CancellationToken ct)
        {
            Args.AddRange(args);
            return inner.RunAsync(args, workingDirectory, onOutput, ct);
        }
    }

    sealed class NoWaitEdl : IEdlDevice
    {
        public Task WaitForDeviceAsync(CancellationToken ct) => Task.CompletedTask;
        public Task WaitForReconnectAsync(CancellationToken ct) => Task.CompletedTask;
    }

    sealed class AutoYes : IQdlUserInterface
    {
        public List<string> Lines { get; } = [];
        public GptPlan? Plan;
        public Task<bool> AskForEdlModeAsync(string hint) => Task.FromResult(true);
        public Task<bool> ConfirmPlanAsync(ImageManifest m, GptPlan plan, string esp, string root)
        {
            Plan = plan;
            return Task.FromResult(true);
        }
        public void Log(string line) => Lines.Add(line);
    }

    [Fact]
    public async Task FlashesFakeBoard()
    {
        if (!OperatingSystem.IsLinux() || !HasTool("dd") || !HasTool("bash"))
            return;

        var (image, _, boot, root) = FedoraLikeImage();
        var imageZst = new ZstdSharp.Compressor().Wrap(image).ToArray();
        var prog = RandomNumberGenerator.GetBytes(900_000);
        var archive = ProgrammerArchive("./b/flash/prog_firehose_ddr.elf", prog);
        var m = ImageManifest.Parse("test", "board", "fedora", $"""
            NAME=Test Fedora
            IMAGE_URL=https://example.org/fedora.raw.zst
            IMAGE_FILE=fedora.raw.zst
            IMAGE_SHA256={Convert.ToHexStringLower(SHA256.HashData(imageZst))}
            IMAGE_FORMAT=raw.zst
            FLASH_METHOD=qdl
            QDL_ROOT_TYPE=lvm
            QDL_PROGRAMMER_URL=https://example.org/bundle.tar.zst
            QDL_PROGRAMMER_PATH=./b/flash/prog_firehose_ddr.elf
            QDL_PROGRAMMER_SHA256={Convert.ToHexStringLower(SHA256.HashData(prog))}
            DTB=qcom/test-board.dtb
            """);

        var emmc = Path.Combine(dir, "emmc.img");
        CreateBoard(emmc);
        byte[] Region(long sector, long count)
        {
            using var f = File.OpenRead(emmc);
            f.Position = sector * S;
            var b = new byte[count * S];
            f.ReadExactly(b);
            return b;
        }
        var firmwareBefore = Region(34, 16384 - 34);
        var persistBefore = Region(49152, 4096);

        var qdl = new RecordingQdl(new ProcessQdlRunner(Path.Combine(RepoRoot(), "tests", "fake-qdl"),
            new Dictionary<string, string> { ["FAKE_QDL_DISK"] = emmc }));
        var ui = new AutoYes();
        var http = new HttpClient(new QdlPieceTests.StaticServer(imageZst, archive));
        var job = new QdlFlashJob(m, http, Path.Combine(dir, "cache"), qdl, new NoWaitEdl(), ui);
        await job.RunAsync(new Progress<TransferProgress>(), CancellationToken.None);

        var plan = ui.Plan!;
        Assert.Equal(firmwareBefore, Region(34, 16384 - 34));
        Assert.Equal(persistBefore, Region(49152, 4096));
        Assert.Equal(plan.PrimaryGpt, Region(0, plan.PrimaryGpt.Length / S));
        Assert.Equal(plan.BackupGpt, Region(plan.BackupStart, plan.BackupGpt.Length / S));
        Assert.Equal(boot, Region(plan.BootStart, boot.Length / S));
        Assert.Equal(root, Region(plan.RootStart, root.Length / S)); // zero areas overwrite old data
        var esp = Region(plan.EspStart, (8 << 20) / S);
        Assert.Contains("devicetree ($root)/dtb/qcom/test-board.dtb", ReadFatFile(esp, @"\EFI\fedora\grub.cfg"));
        Assert.Contains(ui.Lines, l => l.StartsWith("Done."));
        Assert.All(qdl.Args, a => Assert.False(Path.IsPathRooted(a) || a.Contains(':'), $"qdl argument '{a}' is a full path"));
        Assert.Contains("prog_firehose_ddr.elf", qdl.Args);

        if (HasTool("sgdisk"))
        {
            var psi = new ProcessStartInfo("sgdisk", ["-v", emmc]) { RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Contains("No problems found", output);
        }

        // The work folder with the unpacked partitions is cleaned up.
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir, "cache")));
    }
}

/// <summary>
/// Downloads the real VENTUNO Q programmer from Arduino's archive. Needs the
/// network, so it only runs with VFLASHER_NETWORK_TESTS=1.
/// </summary>
public class RealProgrammerTests
{
    [Fact]
    public async Task FetchesVentunoQProgrammer()
    {
        if (Environment.GetEnvironmentVariable("VFLASHER_NETWORK_TESTS") != "1")
            return;
        var m = ManifestCatalog.LoadLocal(ManifestCatalog.FindLocalImagesDir(AppContext.BaseDirectory)!)
            .Single(x => x.Id == "arduino/ventuno-q/fedora");
        var dir = Directory.CreateTempSubdirectory("vflasher-net-").FullName;
        try
        {
            var counter = new CountingHandler();
            var sw = Stopwatch.StartNew();
            var file = await ProgrammerFetcher.GetAsync(new HttpClient(counter), m, dir);
            Assert.True(await ImageDownloader.HashMatchesAsync(file, m.Get("QDL_PROGRAMMER_SHA256")!));
            Console.WriteLine($"programmer OK after {counter.BytesRead / 1_000_000.0:0.0} MB in {sw.Elapsed.TotalSeconds:0.0}s");
            Assert.True(counter.BytesRead < 50_000_000, $"downloaded {counter.BytesRead} bytes");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    sealed class CountingHandler() : DelegatingHandler(new HttpClientHandler())
    {
        public long BytesRead;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = await base.SendAsync(request, ct);
            var inner = await resp.Content.ReadAsStreamAsync(ct);
            var counted = new CountingStream(inner, n => Interlocked.Add(ref BytesRead, n));
            var content = new StreamContent(counted);
            foreach (var h in resp.Content.Headers) content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            resp.Content = content;
            return resp;
        }
    }

    sealed class CountingStream(Stream inner, Action<int> onRead) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); onRead(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = await inner.ReadAsync(buffer, ct); onRead(n); return n; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

public class LayoutTests
{
    [Fact]
    public async Task WindowsRefusesWholeLunImages()
    {
        var images = ManifestCatalog.LoadLocal(ManifestCatalog.FindLocalImagesDir(AppContext.BaseDirectory)!);
        var iq9 = images.Single(m => m.Id == "qualcomm/iq9075-evk/fedora");
        var ventuno = images.Single(m => m.Id == "arduino/ventuno-q/fedora");
        Assert.False(QdlFlashJob.SupportsLayout(iq9));
        Assert.True(QdlFlashJob.SupportsLayout(ventuno));

        var job = new QdlFlashJob(iq9, new HttpClient(), Path.GetTempPath(), null!, null!, null!);
        var e = await Assert.ThrowsAsync<NotSupportedException>(() =>
            job.RunAsync(new Progress<TransferProgress>(), CancellationToken.None));
        Assert.Contains("Linux vflasher", e.Message);
    }
}
