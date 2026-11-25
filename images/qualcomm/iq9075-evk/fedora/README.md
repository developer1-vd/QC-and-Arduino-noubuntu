# Fedora on the Qualcomm IQ-9075 EVK

| | |
|---|---|
| Board | Qualcomm Dragonwing IQ-9075 EVK (QCS9075, "lemans") |
| OS | Fedora Server 45, aarch64 (kernel 7.2.11) |
| Image | `Fedora-Server-Host-Generic-45-1.4.aarch64.raw.xz` (official Fedora build) |
| SHA-256 | `c5e3339909ed326480990ec2c0934d19340ff7d5ac9256236966a4e8572752a8` |
| Flash method | `dd` to a USB/NVMe/SD boot drive |
| Status | **Untested on hardware** |

The image itself (~1.4 GB compressed) is far over GitHub's 100 MB file limit, so
it is not stored in git. [`image.conf`](image.conf) pins the exact official
Fedora download and its SHA-256; VFlasher downloads it, verifies the checksum,
and only then writes it.

## Flashing

```bash
sudo ./vflasher qualcomm iq9075-evk fedora
```

VFlasher lists the attached drives and asks which one to write. You can also
name it directly:

```bash
sudo ./vflasher qualcomm iq9075-evk fedora --device /dev/sdX
```

## Booting

1. Plug the flashed drive into the EVK.
2. Power on with a serial console attached (the EVK's debug UART, 115200 8N1).
3. In the board's UEFI boot menu, pick the USB/NVMe drive.
4. Fedora's first-boot setup (`initial-setup`) runs on the serial console.

## Known caveats

- The EVK must be running Qualcomm's UEFI boot firmware. Flash or update it
  from Qualcomm's release if the board does not offer a UEFI boot menu.
- Board support comes from the upstream kernel device tree
  `qcom/lemans-evk.dtb`, which this image ships at `/boot/dtb/qcom/lemans-evk.dtb`
  (checked by unpacking the image). If the firmware does not hand over a device
  tree, add `devicetree /dtb/qcom/lemans-evk.dtb` to the GRUB entry.
- Writing Fedora to the on-board UFS storage needs Qualcomm's EDL/QDL tooling
  and firehose programmer, which VFlasher does not support yet.
