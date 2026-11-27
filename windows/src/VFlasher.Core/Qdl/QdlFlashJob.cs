using System.Diagnostics;

namespace VFlasher.Core.Qdl;

/// <summary>
/// Runs qdl with the given arguments in <c>workingDirectory</c>, passing each
/// output line to <c>onOutput</c>. File arguments should be bare names inside
/// that folder: qdl reads "C:\..." as a programmer image id ("C").
/// </summary>
public interface IQdlRunner
{
    Task<int> RunAsync(IReadOnlyList<string> args, string workingDirectory, Action<string> onOutput,
        CancellationToken ct);
}

/// <summary>Knows when a board in Qualcomm EDL mode (USB 05c6:9008) is connected.</summary>
public interface IEdlDevice
{
    Task WaitForDeviceAsync(CancellationToken ct);

    /// <summary>Waits for the board to come back after qdl reset it.</summary>
    Task WaitForReconnectAsync(CancellationToken ct);
}

/// <summary>The questions the flash job asks the user.</summary>
public interface IQdlUserInterface
{
    /// <summary>Ask the user to put the board in EDL mode. False cancels.</summary>
    Task<bool> AskForEdlModeAsync(string hint);

    /// <summary>Show what will be written. False cancels.</summary>
    Task<bool> ConfirmPlanAsync(ImageManifest manifest, GptPlan plan, string espName, string rootName);

    void Log(string line);
}

/// <summary>Runs qdl as a process.</summary>
public sealed class ProcessQdlRunner(string executable, IReadOnlyDictionary<string, string>? environment = null)
    : IQdlRunner
{
    public async Task<int> RunAsync(IReadOnlyList<string> args, string workingDirectory, Action<string> onOutput,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (environment != null)
            foreach (var (k, v) in environment)
                psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return p.ExitCode;
    }
}

/// <summary>
/// Flashes an image to a board's on-board storage over EDL with qdl, keeping
/// the board's firmware partitions. Same steps as the Linux vflasher script:
/// split the image, point GRUB at the board's device tree, read the board's
/// GPT, plan the new OS layout, then write the partitions and the GPT last.
/// </summary>
public sealed class QdlFlashJob(
    ImageManifest manifest, HttpClient http, string cacheDir, IQdlRunner qdl, IEdlDevice edl,
    IQdlUserInterface ui, Action<FileStream>? prepareSparseFile = null)
{
    public async Task RunAsync(IProgress<TransferProgress> progress, CancellationToken ct)
    {
        var m = manifest;
        if (m.FlashMethod != "qdl")
            throw new InvalidOperationException("not a qdl image");
        if (!SupportsLayout(m))
            throw new NotSupportedException(UnsupportedReason);
        var storage = m.Get("QDL_STORAGE") ?? "emmc";
        var espName = m.Get("QDL_ESP_PARTITION") ?? "efi";
        var rootName = m.Get("QDL_ROOT_PARTITION") ?? "system";
        var rootType = m.Get("QDL_ROOT_TYPE") ?? "linux";

        var image = await new ImageDownloader(http, cacheDir).GetImageAsync(m, progress, ct);
        ui.Log("Image checksum OK.");
        ui.Log("Getting the Qualcomm firehose programmer…");
        var programmer = await ProgrammerFetcher.GetAsync(http, m, cacheDir, ct);
        ui.Log("Programmer checksum OK.");

        var work = Directory.CreateDirectory(Path.Combine(cacheDir, $"work-{Guid.NewGuid():N}")).FullName;
        try
        {
            ui.Log("Unpacking the image's partitions…");
            List<ImagePartition> parts;
            using (var image_ = new ImageDecompressor(image, m.Format))
            {
                var total = image_.CompressedLength;
                parts = await Task.Run(() => DiskImageSplitter.Split(image_.Raw, work, prepareSparseFile,
                    _ => progress.Report(new TransferProgress("Unpacking", image_.CompressedPosition, total)), ct), ct);
            }
            if (parts.Count != 3)
                throw new InvalidDataException(
                    $"expected an image with 3 partitions (EFI, /boot, root), found {parts.Count}");

            if (m.Dtb is { Length: > 0 } dtb)
            {
                ui.Log($"Setting the device tree to {dtb}");
                GrubStubEditor.AddDevicetree(parts[0].Path, dtb);
            }

            var hint = m.Get("EDL_HINT") ?? "See the documentation of the board for how to enter EDL mode.";
            if (!await ui.AskForEdlModeAsync(hint))
                throw new OperationCanceledException();
            progress.Report(new TransferProgress("Waiting for the board in EDL mode", 0, null));
            await edl.WaitForDeviceAsync(ct);

            // qdl runs in the work folder with bare file names, like Arduino's flasher.
            File.Copy(programmer, Path.Combine(work, Programmer));
            ui.Log("Reading the board's partition table…");
            var boardGpt = Path.Combine(work, "board-gpt.bin");
            QdlJobXml.WriteReadJob(Path.Combine(work, "read-gpt.xml"), "board-gpt.bin", 0, 34);
            await RunQdlAsync(work, [.. Common(storage), "read-gpt.xml"],
                "qdl could not read the board (is it in EDL mode?)", ct);

            var plan = GptPlanner.Plan(await File.ReadAllBytesAsync(boardGpt, ct), espName, rootName,
                parts[1].Sectors, parts[0].Sectors, parts[2].Sectors, rootType);
            var main = Path.Combine(work, "gpt-main.bin");
            var backup = Path.Combine(work, "gpt-backup.bin");
            await File.WriteAllBytesAsync(main, plan.PrimaryGpt, ct);
            await File.WriteAllBytesAsync(backup, plan.BackupGpt, ct);

            if (!await ui.ConfirmPlanAsync(m, plan, espName, rootName))
                throw new OperationCanceledException();

            // qdl reset the board after the read; it comes back in EDL mode
            // while the EDL jumper is still fitted.
            progress.Report(new TransferProgress("Waiting for the board to reconnect", 0, null));
            await edl.WaitForReconnectAsync(ct);

            ui.Log("Flashing (this takes a while; do not disconnect the board)…");
            progress.Report(new TransferProgress("Flashing", 0, null));
            // The partitions first, the new partition table last.
            QdlJobXml.WriteProgramJob(Path.Combine(work, "rawprogram.xml"), [
                new(espName, Path.GetFileName(parts[0].Path), plan.EspStart),
                new("boot", Path.GetFileName(parts[1].Path), plan.BootStart),
                new(rootName, Path.GetFileName(parts[2].Path), plan.RootStart),
                new("BackupGPT", Path.GetFileName(backup), plan.BackupStart),
                new("PrimaryGPT", Path.GetFileName(main), 0),
            ]);
            // Retrying is safe: the job rewrites the same partitions and writes the
            // partition table last. A board can drop off USB during the handshake.
            for (var attempt = 1; ; attempt++)
            {
                var code = await qdl.RunAsync([.. Common(storage), "rawprogram.xml"], work, ui.Log, ct);
                if (code == 0)
                    break;
                if (attempt == MaxFlashAttempts)
                    throw new IOException(
                        $"qdl failed while flashing; try again, or restore the vendor image with its own flasher (qdl exit code {code})");
                ui.Log($"qdl lost the board (attempt {attempt} of {MaxFlashAttempts}); waiting for it to come back in EDL mode. " +
                       "If it does not, power-cycle the board with the EDL switch or jumper still set.");
                await edl.WaitForReconnectAsync(ct);
            }

            ui.Log($"Done. {m.Name} is on the board.");
            ui.Log("Remove the EDL jumper and power-cycle the board.");
            if (m.Get("FIRST_BOOT") is { Length: > 0 } firstBoot)
                ui.Log($"After the first login, run this to use the whole {storage}:  {firstBoot}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    const string Programmer = "prog_firehose_ddr.elf";
    public const int MaxFlashAttempts = 3;

    public const string UnsupportedReason =
        "This board's image has to be rebuilt for its storage's 4096-byte sectors, which needs Linux " +
        "file system tools. Flash it with the Linux vflasher script.";

    /// <summary>
    /// The Windows app does the keep-firmware layout (e.g. the VENTUNO Q's eMMC).
    /// Whole-LUN images with 4K sectors (the IQ-9075's UFS) need the Linux script.
    /// </summary>
    public static bool SupportsLayout(ImageManifest m) =>
        (m.Get("QDL_LAYOUT") ?? "keep-firmware") == "keep-firmware" && (m.Get("QDL_SECTOR_SIZE") ?? "512") == "512";

    static string[] Common(string storage) => ["--storage", storage, Programmer];

    async Task RunQdlAsync(string workDir, string[] args, string error, CancellationToken ct)
    {
        var code = await qdl.RunAsync(args, workDir, ui.Log, ct);
        if (code != 0)
            throw new IOException($"{error} (qdl exit code {code})");
    }
}
