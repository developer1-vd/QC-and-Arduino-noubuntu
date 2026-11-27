# Fedora on the Qualcomm IQ-9075 EVK

| | |
|---|---|
| Board | Qualcomm Dragonwing IQ-9075 EVK (QCS9075, "lemans") |
| OS | Fedora Server 45, aarch64 (kernel 7.2.11) |
| Image | `Fedora-Server-Host-Generic-45-1.4.aarch64.raw.xz` (official Fedora build) |
| Installs to | On-board UFS, LUN 0, over USB in Qualcomm EDL mode |
| Flash method | `qdl`, Linux only (the image must be rebuilt for 4K sectors; see below) |
| Status | **Untested on hardware.** Tested against a simulated UFS LUN, see below |

The EVK boots from its on-board UFS storage; it does not boot from USB or SD.
VFlasher writes Fedora to UFS over USB in EDL mode with
[`qdl`](https://github.com/linux-msm/qdl), like Qualcomm's own
[QDL flashing instructions](https://dragonwingdocs.qualcomm.com/Ubuntu/devices/iq9075-evk/update-software/flash-using-qdl)
do for Ubuntu.

## What VFlasher changes

The EVK's UFS is split into LUNs. LUN 0 holds only the OS (`efi`, `persist`,
`system`); the firmware (XBL, UEFI, TrustZone, CDT, ...) is on LUNs 1-4 and on
SPI-NOR. VFlasher replaces **only LUN 0** and leaves the firmware and the boot
LUN setting alone:

```
LUN 0 before:  [efi] [persist] [system ..........................]
LUN 0 after:   [efi] [boot] [system ............................]   (4096-byte sectors)
LUNs 1-4, SPI-NOR: unchanged
```

UFS LUN 0 uses **4096-byte sectors**, and Fedora's image is built for 512-byte
sectors: its FAT EFI partition and XFS root cannot be mounted from a 4K device.
So VFlasher rebuilds them first, on your PC:

- **EFI**: a new FAT with 4096-byte sectors and the same serial number
  (`8203-472A`, which `/etc/fstab` uses), with all files copied over. GRUB is
  set to load `qcom/lemans-evk.dtb`, the upstream device tree for this board, in
  place of the firmware's own.
- **/boot**: Fedora's ext4 uses 4 KiB blocks and works as is.
- **root**: a new XFS with 4096-byte sectors and the same UUID, with every file
  copied over (permissions, owners, hard links, xattrs and SELinux labels kept).
  Fedora's image keeps root on LVM; the rebuilt root is a plain partition, so
  VFlasher points `/etc/fstab`, `/etc/kernel/cmdline` and the boot entries at
  `root=UUID=…`. With `x-systemd.growfs`, root grows to fill LUN 0 on first boot.

VFlasher refuses to touch LUN 0 if it holds any firmware partition, or if it
has no valid partition table.

The Qualcomm firehose programmer that `qdl` needs comes from Qualcomm's
[QCS9100 boot firmware bundle](https://artifacts.codelinaro.org/artifactory/qli-ci/flashable-binaries/ubuntu-fw/QCS9100/QLI.1.8-Ver.1.1/QLI.1.8-Ver.1.1-ubuntu-QCS9100-nhlos-bins.tar.gz)
(9 MB), the one the official instructions use, and is checked against the
SHA-256 in [`image.conf`](image.conf).

## Before you start

- The board must already have Qualcomm's firmware and provisioned UFS, as it
  does from the factory or after any official Qualcomm Linux or Ubuntu flash.
  VFlasher does not provision UFS or flash firmware. If you aren't sure, flash
  Ubuntu once with Qualcomm's instructions, then use VFlasher.
- A Linux PC with `qdl` (`sudo dnf install qdl` or `sudo apt install qdl`),
  `python3`, `mtools`, `dosfstools`, `xfsprogs`, `xz` and about 12 GB of free
  space in `~/.cache/vflasher`.

## Flashing

```bash
sudo ./vflasher qualcomm iq9075-evk fedora
```

1. VFlasher downloads and checks the Fedora image (1.4 GB) and the programmer,
   then rebuilds the image for 4K sectors (a minute or two).
2. It asks you to put the board in **EDL mode**: turn DIP switch **SW2-3 on**
   (up), connect the EVK's **USB0** (Type-C) port to the PC, and power on.
   `lsusb` then shows `05c6:9008 Qualcomm ... (QDL mode)`.
3. It reads LUN 0's partition table, shows what it will write, and asks you to
   type `yes`.
4. It flashes LUN 0 (about 10 GB).
5. Turn SW2-3 **off** (down) and power-cycle the board. Fedora's first-boot
   setup runs on the serial console (115200 8N1).

## Going back

Flash Qualcomm Linux or Ubuntu with Qualcomm's instructions. EDL mode is in the
chip's boot ROM, so the board can always be reflashed.

## Known caveats

- Not yet tried on a real board. The whole flow, including the rebuild, was run
  with a stand-in for `qdl` against a simulated 64 GiB, 4K-sector LUN 0 using
  the real Fedora image and the real programmer. The resulting partition table
  checks clean with `sgdisk -v`, and all three partitions mount from a 4K-sector
  device with the expected UUIDs and contents. CI repeats this on a small image
  (`tests/test-ufs-flash.sh`).
- Windows can't flash this board: rebuilding XFS and FAT needs Linux tools.
- An earlier version of this page said the EVK boots Fedora from a USB drive.
  That was wrong; it boots only from UFS.
