# Fedora Workstation (GNOME) on the Qualcomm IQ-9075 EVK

| | |
|---|---|
| Board | Qualcomm Dragonwing IQ-9075 EVK (QCS9075, "lemans") |
| OS | Fedora Workstation 45, aarch64, GNOME desktop (kernel 7.2.11) |
| Image | `Fedora-Workstation-Disk-45-1.4.aarch64.raw.xz` (official Fedora build, 3.2 GB) |
| Installs to | On-board UFS, LUN 0, over USB in Qualcomm EDL mode |
| Display | The EVK's two **mini DisplayPort** connectors (EDP0 / EDP1) |
| Flash method | `qdl`, Linux only |
| Status | **Untested on hardware.** Tested against a simulated UFS LUN, see below |

The EVK boots from its on-board UFS storage; it does not boot from USB or SD.
VFlasher writes Fedora to UFS over USB in EDL mode with
[`qdl`](https://github.com/linux-msm/qdl), like Qualcomm's own
[QDL flashing instructions](https://dragonwingdocs.qualcomm.com/Ubuntu/devices/iq9075-evk/update-software/flash-using-qdl)
do for Ubuntu.

## Why the desktop can work

The upstream device tree for this board (`qcom/lemans-evk.dtb`, which GRUB is
set up to load) enables the display controller on both mini DP ports and the
Adreno 663 GPU. The image has the `msm` display/GPU driver and the GPU firmware
the device tree asks for (`qcom/sa8775p/a663_zap.mbn`, `a663_gmu.bin`,
`a660_sqe.fw`).

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
sectors:

- **EFI**: its FAT can't be mounted from a 4K device, so VFlasher rebuilds it
  with 4096-byte sectors and the same serial number (which `/etc/fstab` uses),
  with all files copied over. GRUB is set to load `qcom/lemans-evk.dtb`, the
  upstream device tree for this board, in place of the firmware's own.
- **/boot** (ext4, 4 KiB blocks) and **root** (btrfs, 4096-byte sectors) work
  as is. Root has `x-systemd.growfs`, so it grows to fill LUN 0 on first boot.

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
  `python3`, `mtools`, `dosfstools`, `xfsprogs` and `xz`.
- About **15 GB of free space** for the download and unpacking, in
  `~/.cache/vflasher`, or anywhere else with `--cache DIR`.

## Flashing

```bash
sudo ./vflasher qualcomm iq9075-evk fedora-workstation
```

1. VFlasher downloads and checks the image (3.2 GB) and the programmer, then
   rebuilds the EFI partition for 4K sectors.
2. It shows what it will replace and asks you to type `yes`.
3. It asks you to put the board in **EDL mode**: turn DIP switch **SW2-3 on**
   (up), connect the EVK's **USB0** (Type-C) port to the PC, and power on.
   `lsusb` then shows `05c6:9008 Qualcomm ... (QDL mode)`.
4. It reads LUN 0's partition table, then flashes LUN 0 (about 13 GB). If the
   board drops off USB, it waits for it to come back and retries, up to 3 times.
5. Turn SW2-3 **off** (down), connect a monitor to a **mini DisplayPort**
   port (an adapter to HDMI must be an active DP-to-HDMI one), and power-cycle.

On first boot GNOME's setup runs on the screen and creates your user account.

## Going back

Flash Qualcomm Linux or Ubuntu with Qualcomm's instructions. EDL mode is in the
chip's boot ROM, so the board can always be reflashed.

## Known caveats

- Not yet tried on a real board, including whether the display comes up. The
  whole flow was run with a stand-in for `qdl` against a simulated 64 GiB,
  4K-sector LUN 0 using the real image and the real programmer: the partition
  table checks clean with `sgdisk -v`, and all three partitions mount from a
  4K-sector device with the expected UUIDs and contents. CI repeats this on a
  small image (`tests/test-ufs-flash.sh`).
- If the screen stays dark, flash again with `--boot-log --root-password`:

  ```bash
  sudo ./vflasher qualcomm iq9075-evk fedora-workstation --boot-log --root-password
  ```

  The whole boot then shows on the main serial console (`ttyMSM0`, 115200 8N1;
  of the debug port's channels, the one that prints UEFI and then Linux, not
  the `sail_proc@home$` one), ending in a `login:` prompt. Log in as `root` and
  run `dmesg | grep -i -E 'msm|drm|dp|gpu|firmware'`.
- Only the two mini DP ports are enabled for display in the upstream device
  tree, so use one of those.
- Windows can't flash this board: rebuilding the EFI partition for 4K sectors
  needs Linux tools.
