using System.Diagnostics;
using VFlasher.Core;
using VFlasher.Core.Qdl;

namespace VFlasher;

internal sealed class MainForm : Form, IQdlUserInterface
{
    readonly ComboBox boardBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox osBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Label imageInfo = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 4) };
    readonly ComboBox driveBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Button refreshButton = new() { Text = "Refresh", AutoSize = true };
    readonly CheckBox showAll = new() { Text = "Show internal drives too", AutoSize = true };
    readonly Button flashButton = new() { Text = "Flash", AutoSize = true, Enabled = false };
    readonly Button cancelButton = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, Text = "Loading images…" };
    readonly TextBox log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9),
    };

    readonly string[] args;
    readonly HttpClient http;
    List<ImageManifest> manifests = [];
    CancellationTokenSource? cts;

    public MainForm(string[] args)
    {
        this.args = args;
        http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"VFlasher/{Application.ProductVersion}");

        Text = "VFlasher";
        MinimumSize = new Size(640, 480);
        Size = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Row(string label, Control c, Control? extra = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) });
            grid.Controls.Add(c);
            if (extra != null) grid.Controls.Add(extra); else grid.Controls.Add(new Label());
        }

        Row("Board", boardBox);
        Row("OS", osBox);
        Row("", imageInfo);
        Row("Drive", driveBox, refreshButton);
        Row("", showAll);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(flashButton);
        grid.Controls.Add(new Label());
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);

        Row("Progress", progressBar);
        Row("", status);
        grid.Controls.Add(log);
        grid.SetColumnSpan(log, 3);
        grid.RowStyles.Clear();
        for (var i = 0; i < 9; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(grid);

        boardBox.SelectedIndexChanged += (_, _) => FillOsBox();
        osBox.SelectedIndexChanged += (_, _) => ShowImageInfo();
        driveBox.SelectedIndexChanged += (_, _) => UpdateButtons();
        refreshButton.Click += (_, _) => RefreshDrives();
        showAll.CheckedChanged += (_, _) => RefreshDrives();
        flashButton.Click += async (_, _) => await FlashAsync();
        cancelButton.Click += (_, _) => cts?.Cancel();
        FormClosing += (_, e) =>
        {
            if (cts != null && MessageBox.Show(this, "Flashing is in progress. Stop it and quit? The drive will be left unusable until it is flashed again.",
                    "VFlasher", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                e.Cancel = true;
            else
                cts?.Cancel();
        };
        Load += async (_, _) => await LoadManifestsAsync();
    }

    ImageManifest? SelectedManifest => osBox.SelectedItem as ImageManifest;
    DiskInfo? SelectedDisk => driveBox.SelectedItem as DiskInfo;

    public void Log(string line)
    {
        // qdl output arrives on background threads.
        if (InvokeRequired)
            BeginInvoke(() => Log(line));
        else
            log.AppendText(line + Environment.NewLine);
    }

    bool IsEdl => SelectedManifest?.FlashMethod == "qdl";

    async Task LoadManifestsAsync()
    {
        try
        {
            var local = ManifestCatalog.FindLocalImagesDir(AppContext.BaseDirectory);
            if (local != null)
            {
                manifests = ManifestCatalog.LoadLocal(local);
                Log($"Loaded {manifests.Count} image(s) from {local}");
            }
            else
            {
                manifests = await ManifestCatalog.LoadRemoteAsync(http);
                Log($"Loaded {manifests.Count} image(s) from GitHub");
            }
        }
        catch (Exception e)
        {
            status.Text = "Could not load the image list.";
            Log("Error loading images: " + e.Message);
            return;
        }

        foreach (var board in manifests.Select(m => $"{m.Manufacturer}/{m.Board}").Distinct())
            boardBox.Items.Add(board);

        // vflasher.exe <manufacturer> <board> <os> preselects, like the Linux command.
        if (args.Length >= 2)
        {
            var wanted = $"{args[0].ToLowerInvariant()}/{args[1].ToLowerInvariant()}";
            var idx = boardBox.Items.IndexOf(wanted);
            if (idx >= 0) boardBox.SelectedIndex = idx;
            else Log($"No image for board '{wanted}'.");
        }
        if (boardBox.SelectedIndex < 0 && boardBox.Items.Count > 0)
            boardBox.SelectedIndex = 0;
        if (args.Length >= 3)
        {
            var os = args[2].ToLowerInvariant();
            foreach (ImageManifest m in osBox.Items)
                if (m.Os == os) osBox.SelectedItem = m;
        }

        status.Text = "Ready.";
        RefreshDrives();
    }

    void FillOsBox()
    {
        osBox.Items.Clear();
        var board = boardBox.SelectedItem as string;
        foreach (var m in manifests.Where(m => $"{m.Manufacturer}/{m.Board}" == board))
            osBox.Items.Add(m);
        if (osBox.Items.Count > 0) osBox.SelectedIndex = 0;
    }

    void ShowImageInfo()
    {
        var m = SelectedManifest;
        if (m == null) { imageInfo.Text = ""; UpdateButtons(); return; }
        var size = m.ImageSize is { } s ? $", {DiskInfo.FormatSize(s)} download" : "";
        imageInfo.Text = $"{m.Name}{size}\nSource: {m.ImageUrl.Host}";
        if (m.FlashMethod == "qdl")
        {
            imageInfo.Text += "\nThis board is flashed over USB-C in EDL mode: no drive is needed. " +
                              "Its firmware partitions are kept; the OS on the board is replaced.";
            if (QdlTool.Path == null)
                imageInfo.Text += "\nqdl.exe was not found next to VFlasher. Install VFlasher with the setup wizard.";
        }
        else if (m.FlashMethod != "dd")
        {
            imageInfo.Text += $"\nThis image uses the '{m.FlashMethod}' flash method, which this version does not support.";
        }
        UpdateButtons();
    }

    void RefreshDrives()
    {
        driveBox.Items.Clear();
        try
        {
            foreach (var d in DiskInfo.Enumerate())
                if (!d.IsSystem && (showAll.Checked || d.LooksExternal) && d.Size > 0)
                    driveBox.Items.Add(d);
        }
        catch (Exception e)
        {
            Log("Could not list drives: " + e.Message);
        }
        if (driveBox.Items.Count > 0) driveBox.SelectedIndex = 0;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var busy = cts != null;
        flashButton.Enabled = !busy && SelectedManifest switch
        {
            { FlashMethod: "dd" } => SelectedDisk != null,
            { FlashMethod: "qdl" } => QdlTool.Path != null,
            _ => false,
        };
        cancelButton.Enabled = busy;
        boardBox.Enabled = osBox.Enabled = !busy;
        driveBox.Enabled = refreshButton.Enabled = showAll.Enabled = !busy && !IsEdl;
    }

    async Task FlashAsync()
    {
        if (IsEdl)
            await FlashEdlAsync();
        else
            await FlashDriveAsync();
    }

    Progress<TransferProgress> NewProgress() => new(p =>
    {
        if (p.Total is > 0)
        {
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = (int)Math.Clamp(p.Done * 1000 / p.Total.Value, 0, 1000);
            status.Text = $"{p.Stage}: {DiskInfo.FormatSize(p.Done)} / {DiskInfo.FormatSize(p.Total.Value)}";
        }
        else
        {
            progressBar.Style = ProgressBarStyle.Marquee;
            status.Text = p.Done > 0 ? $"{p.Stage}: {DiskInfo.FormatSize(p.Done)}" : p.Stage + "…";
        }
    });

    async Task FlashEdlAsync()
    {
        var m = SelectedManifest!;
        var confirm = MessageBox.Show(this,
            $"{m.Name} will be written to the board's on-board storage over USB-C.\n\n" +
            "The operating system currently on the board will be ERASED. Its firmware is kept, and the " +
            "vendor's own flasher can always restore the original image.\n\nContinue?",
            "Confirm flashing", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        cts = new CancellationTokenSource();
        UpdateButtons();
        var sw = Stopwatch.StartNew();
        try
        {
            Log(await EdlDriver.InstallAsync(cts.Token));
            var qdl = new ProcessQdlRunner(QdlTool.Path!);
            var job = new QdlFlashJob(m, http, ImageDownloader.DefaultCacheDir(), qdl, new WindowsEdlDevice(), this,
                SparseFiles.MarkSparse);
            await job.RunAsync(NewProgress(), cts.Token);

            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = 1000;
            status.Text = "Done.";
            Log($"Finished in {sw.Elapsed:hh\\:mm\\:ss}.");
            MessageBox.Show(this, $"{m.Name} is on the board.\n\nRemove the EDL jumper and power-cycle the board.",
                "VFlasher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            status.Text = "Cancelled.";
            Log("Cancelled. If flashing had started, flash again or restore the vendor image.");
        }
        catch (Exception e)
        {
            status.Text = "Failed.";
            Log("Error: " + e.Message);
            MessageBox.Show(this, e.Message, "VFlasher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            progressBar.Style = ProgressBarStyle.Blocks;
            cts.Dispose();
            cts = null;
            UpdateButtons();
        }
    }

    public Task<bool> AskForEdlModeAsync(string hint)
    {
        var r = MessageBox.Show(this,
            "Put the board in EDL mode and connect it to this computer over USB-C.\n\n" + hint +
            "\n\nClick OK when it is connected. VFlasher then waits for it.",
            "Connect the board", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        return Task.FromResult(r == DialogResult.OK);
    }

    public Task<bool> ConfirmPlanAsync(ImageManifest m, GptPlan plan, string espName, string rootName)
    {
        static string Gib(long sectors) => $"{sectors * 512.0 / (1L << 30):0.#} GiB";
        var text =
            $"The board's storage was read. VFlasher will write:\n\n" +
            $"  {espName}: EFI system partition, at sector {plan.EspStart}\n" +
            $"  boot: /boot (new partition), at sector {plan.BootStart}\n" +
            $"  {rootName}: root, {Gib(plan.RootSectors)}, at sector {plan.RootStart}\n\n" +
            "Firmware partitions are kept. The current OS on the board will be ERASED.\n\nFlash now?";
        Log(text.Replace("\n\n", "\n"));
        var r = MessageBox.Show(this, text, "Confirm flashing", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return Task.FromResult(r == DialogResult.Yes);
    }

    async Task FlashDriveAsync()
    {
        var m = SelectedManifest!;
        var disk = SelectedDisk!;
        var confirm = MessageBox.Show(this,
            $"ALL DATA on this drive will be destroyed:\n\n{disk}\n\nWrite {m.Name} to it?",
            "Confirm flashing", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        cts = new CancellationTokenSource();
        UpdateButtons();
        var progress = NewProgress();
        var sw = Stopwatch.StartNew();
        try
        {
            var downloader = new ImageDownloader(http, ImageDownloader.DefaultCacheDir());
            Log($"Getting {m.ImageFile} (cache: {downloader.CacheDir})");
            var file = await downloader.GetImageAsync(m, progress, cts.Token);
            Log("Image checksum OK.");

            Log($"Writing to {disk.Path}…");
            var token = cts.Token;
            await Task.Run(() =>
            {
                using var image = new ImageDecompressor(file, m.Format);
                RawDiskWriter.Write(disk, image, progress, token);
            }, token);

            progressBar.Value = 1000;
            status.Text = "Done.";
            Log($"Done in {sw.Elapsed:mm\\:ss}. You can remove the drive and boot the board from it.");
            MessageBox.Show(this, $"{m.Name} was written to {disk}.", "VFlasher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            status.Text = "Cancelled.";
            Log("Cancelled. If writing had started, the drive is not usable until it is flashed again.");
        }
        catch (Exception e)
        {
            status.Text = "Failed.";
            Log("Error: " + e.Message);
            MessageBox.Show(this, e.Message, "VFlasher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cts.Dispose();
            cts = null;
            RefreshDrives();
        }
    }
}
