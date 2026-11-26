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

A standalone `vflasher.exe` is in the same artifact if you don't want to install
anything.

## Use

1. Start VFlasher. Windows asks for administrator rights, which are needed to
   write to a whole drive.
2. Pick the board and the OS.
3. Plug in the USB, NVMe or SD drive and pick it under **Drive**. Only external
   drives are listed. Tick **Show internal drives too** to see the others. The
   drive Windows runs from is never listed.
4. Click **Flash** and confirm.

You can also preselect the image from a terminal, like on Linux:

```
vflasher qualcomm iq9075-evk fedora
```

Downloads are cached in `%LOCALAPPDATA%\VFlasher\cache`, and a dropped download
resumes where it stopped.

## Limits

- Only images with `FLASH_METHOD=dd` (write to a drive) are supported. Boards
  that must be flashed over USB in Qualcomm EDL mode (`FLASH_METHOD=qdl`), such
  as the Arduino VENTUNO Q, are listed but need the Linux `vflasher` for now.

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
| `src/VFlasher.Core` | Manifests, downloads, checksums, decompression (cross-platform, tested) |
| `src/VFlasher` | WinForms GUI and raw drive writing (Windows only) |
| `tests/VFlasher.Core.Tests` | Unit tests for the core |
| `installer/` | Inno Setup script for the setup wizard |
