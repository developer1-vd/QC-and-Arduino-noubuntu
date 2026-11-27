# VFlasher for Windows

A Windows GUI for VFlasher. It uses the same [`images/`](../images/) manifests as
the Linux `vflasher` script. It downloads the image from its official source,
checks the SHA-256, and writes it to the drive you pick.

## Install

Download `vflasher-setup-<version>-x64.exe`, or `-arm64` for Windows on Arm,
from the **Build** workflow's artifacts on GitHub Actions, then run it. The
setup wizard:

- installs VFlasher to `Program Files\VFlasher`, along with the image list
- adds a Start menu entry, plus an optional desktop icon
- adds VFlasher to `PATH` if you tick the box, so `vflasher` works in any
  terminal (the uninstaller removes it again)
- installs `qdl` and the USB driver for boards flashed in Qualcomm EDL mode
  (see below)

A standalone `vflasher.exe` is in the same artifact if you don't want to install
anything. Keep the `qdl` and `drivers` folders next to it if you want to flash
EDL boards.

## Use

1. Start VFlasher. Windows asks for administrator rights, which are needed to
   write to a whole drive.
2. Pick the board and the OS.
3. Plug in the USB, NVMe or SD drive and pick it under **Drive**. Only external
   drives are listed. Tick **Show internal drives too** to see the others. The
   drive Windows runs from is never listed.
4. Click **Flash** and confirm.

### Boards flashed over USB-C (Arduino VENTUNO Q)

Some boards boot only from their on-board storage, which is written over USB
in Qualcomm EDL mode. For these, no drive is picked. Click **Flash**, then:

1. VFlasher downloads and checks the OS image, plus the Qualcomm "firehose"
   programmer for the board. It downloads only the first few MB of the vendor's
   image archive and checks the programmer against a pinned SHA-256.
2. It asks you to put the board in EDL mode and connect it over USB-C, then
   waits for USB device `05C6:9008`.
3. It reads the board's partition table and shows what it will write. All
   firmware partitions are kept; only the OS area is replaced.
4. It flashes with `qdl`, which takes a while. Then remove the EDL jumper and
   power-cycle the board.

This is the same process as the Linux `vflasher` script, and the same code
plans the partition table: a test checks that both produce identical bytes.

The **Qualcomm IQ-9075 EVK** is listed but can't be flashed from Windows. Its
UFS uses 4096-byte sectors, and rebuilding Fedora's file systems for that needs
Linux tools, so use the Linux `vflasher` script for it.

You can also preselect the image from a terminal, like on Linux:

```
vflasher qualcomm iq9075-evk fedora
```

Downloads are cached in `%LOCALAPPDATA%\VFlasher\cache`, and a dropped download
resumes where it stopped.

## EDL tools and driver

The installer ships these alongside `vflasher.exe`, downloaded at build time
by [`installer/fetch-edl-tools.ps1`](installer/fetch-edl-tools.ps1) and checked
against pinned SHA-256s:

| Folder | What | From |
|---|---|---|
| `qdl\` | `qdl.exe` v2.4 (BSD-3-Clause), the EDL flashing tool | [linux-msm/qdl](https://github.com/linux-msm/qdl), Windows build from [arduino/qdl-packing](https://github.com/arduino/qdl-packing) |
| `drivers\` | WinUSB driver for USB `05C6:9008`, signed by Qualcomm (BSD-3-Clause) | [arduino/arduino-flasher-cli](https://github.com/arduino/arduino-flasher-cli) |

These are the same versions Arduino's own flasher uses. The setup wizard
installs the driver, and VFlasher installs it again (harmlessly) before each
EDL flash.

**If qdl can't open the board:** another driver (for example Qualcomm's
QDLoader serial driver) may have claimed it. In Device Manager, open the device
whose hardware ID is `USB\VID_05C6&PID_9008`, choose **Update driver → Browse
my computer → Let me pick**, and select the WinUSB entry from Qualcomm (under
"libusb-win32 devices").

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). It builds on
Windows, Linux or macOS.

```bash
dotnet test windows/tests/VFlasher.Core.Tests
```

```bash
dotnet publish windows/src/VFlasher -c Release -r win-x64 -o windows/publish/win-x64
```

The installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php) on Windows:

```
iscc /DArch=x64 windows\installer\vflasher.iss
```

| Folder | What |
|---|---|
| `src/VFlasher.Core` | Manifests, downloads, checksums, decompression, and the EDL flash job: image splitting, GPT planning, GRUB device-tree setup, programmer download (cross-platform, tested) |
| `src/VFlasher` | WinForms GUI, raw drive writing, EDL device detection and driver install (Windows only) |
| `tests/VFlasher.Core.Tests` | Unit tests for the core |
| `installer/` | Inno Setup script for the setup wizard |
