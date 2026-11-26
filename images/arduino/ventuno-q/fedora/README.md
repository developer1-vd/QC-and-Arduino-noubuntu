# Fedora on the Arduino VENTUNO Q

| | |
|---|---|
| Board | Arduino VENTUNO Q (Qualcomm Dragonwing IQ-8275 / QCS8300 "Monaco") |
| OS | Fedora Server 45, aarch64 (kernel 7.2.11) |
| Image | `Fedora-Server-Host-Generic-45-1.4.aarch64.raw.xz` (official Fedora build) |
| Installs to | On-board 64 GB eMMC, over USB-C in Qualcomm EDL mode |
| Flash method | `qdl`, from Linux or Windows |
| Status | **Untested on hardware.** Tested against a simulated eMMC, see below |

The VENTUNO Q can't boot from its NVMe slot. Its USB-A ports sit behind a PCIe
switch that the board firmware doesn't drive, so the eMMC is the reliable boot
device. VFlasher writes the eMMC the same way Arduino's own flasher does, over
EDL with [`qdl`](https://github.com/linux-msm/qdl).

## What VFlasher changes

The eMMC starts with about 67 Qualcomm firmware partitions (bootloaders, UEFI,
TrustZone and so on), followed by the OS. VFlasher reads the board's partition
table, keeps every firmware partition as it is, and rearranges only the OS
area:

```
before (Arduino's Ubuntu):  [firmware ...] [efi 512M] [persist 30M] [system .............]
after  (Fedora):            [firmware ...] [efi 512M] [persist 30M] [boot 2G] [system ....]
```

- `efi` gets Fedora's EFI partition. Its GRUB stubs get one extra line,
  `devicetree ($root)/dtb/qcom/monaco-arduino-monza.dtb`, so Fedora's kernel
  gets the upstream device tree for this board instead of the firmware's own
  (which is built for Ubuntu's 6.8 kernel). `/boot/dtb` always points at the
  newest kernel, so the line keeps working after kernel updates.
- `boot` is a new partition holding Fedora's `/boot`.
- `system` holds Fedora's root (LVM) and runs to the end of the eMMC.
- `persist` is left alone.

The board's own table is read first, so this works whatever firmware version
the board has, as long as it uses Arduino's `efi`/`persist`/`system` layout.
Re-running VFlasher on a board it already flashed gives the same layout.

The Qualcomm firehose programmer that `qdl` needs comes from Arduino's official
Ubuntu image archive. VFlasher downloads only the first ~9 MB of that archive
and checks the programmer against the SHA-256 in [`image.conf`](image.conf).

## Flashing from Windows

Install VFlasher with the [setup wizard](../../../../windows/), which brings
`qdl` and the USB driver. Start it, pick **arduino/ventuno-q** and **Fedora**,
click **Flash**, and follow the prompts. The steps are the same as on Linux
below.

## Flashing from Linux

Needs a Linux PC with `qdl` (`sudo dnf install qdl` on Fedora, `sudo apt install
qdl` on Debian/Ubuntu), plus `python3`, `mtools`, `zstd` and `xz`.

```bash
sudo ./vflasher arduino ventuno-q fedora
```

1. VFlasher downloads and checks the Fedora image (1.4 GB) and the programmer.
2. It asks you to put the board in **EDL mode**. Fit the EDL jumper with the
   board unplugged, as described in Arduino's VENTUNO Q flashing guide, then
   connect the board's USB-C port to the PC. VFlasher waits for USB device
   `05c6:9008`.
3. It reads the board's partition table, shows what it will write, and asks you
   to type `yes`.
4. It flashes about 10 GB over USB, which takes a while.
5. Remove the jumper and power-cycle the board. Fedora's first-boot setup runs
   on the serial console (uart7, 115200 8N1) or the HDMI screen.

After the first login, let the root file system use the whole eMMC:

```bash
sudo pvresize /dev/disk/by-partlabel/system && sudo lvextend -r -l +100%FREE /dev/systemVG/LVRoot
```

## Going back to Ubuntu

EDL mode is in the chip's boot ROM and can't be overwritten, so the board can
always be restored. Flash Arduino's image with `arduino-flasher-cli` (or Arduino
App Lab) as usual.

## Known caveats

- Not yet tried on a real board. It was tested by running VFlasher, with a
  stand-in for `qdl`, against a 58 GB disk image carrying Arduino's real
  partition table, using the real Fedora image and the real programmer:
  - the partition table checks clean with `sgdisk -v`
  - the firmware partitions and `persist` are byte-for-byte unchanged
  - the Fedora partitions land where planned
- If the board starts an old Ubuntu boot entry, or drops into the UEFI menu
  instead of booting, use **Delete UEFI spec defined Boot Variables** in the
  UEFI menu. The firmware then finds Fedora's `\EFI\BOOT\BOOTAA64.EFI` on `efi`.
- Fedora's `systemd-repart` grows the root partition only for its own disk
  layouts; use the command above instead.

## Sources

- Board device tree: [`monaco-arduino-monza.dts`](https://lkml.iu.edu/2603.1/11375.html)
  (in Fedora 45's kernel as `qcom/monaco-arduino-monza.dtb`)
- Arduino's flasher: [arduino-flasher-cli](https://github.com/arduino/arduino-flasher-cli)
- [Ubuntu 24.04 for VENTUNO Q release notes](https://people.canonical.com/~platform/images/qualcomm-iot/ventunoq/ubuntu-desktop-24.04/20260729-248/Ubuntu_24.04_Arduino_Ventuno_Q_Release_Notes.pdf)
