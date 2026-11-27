# Fedora Workstation (GNOME) on the Qualcomm IQ-9075 EVK

| | |
|---|---|
| Board | Qualcomm Dragonwing IQ-9075 EVK (QCS9075, "lemans") |
| OS | Fedora Workstation 45, aarch64, GNOME desktop (kernel 7.2.11) |
| Image | `Fedora-Workstation-Disk-45-1.4.aarch64.raw.xz` (official Fedora build, 3.2 GB) |
| Installs to | On-board UFS, LUN 0, over USB in Qualcomm EDL mode |
| Display | The EVK's two **mini DisplayPort** connectors (EDP0 / EDP1) |
| Flash method | `qdl`, Linux only |
| Status | **Untested on hardware.** Tested against a simulated UFS LUN |

This is the desktop version of [Fedora Server for the IQ-9075 EVK](../fedora/).
It flashes the same way: only UFS LUN 0 is replaced, and the firmware on
LUNs 1-4 and SPI-NOR is left alone. See that page for the details and for
what the board needs before you start.

## Why it can work

The upstream device tree for this board (`qcom/lemans-evk.dtb`, which GRUB is
set up to load) enables the display controller on both mini DP ports and the
Adreno 663 GPU. The image has the `msm` display/GPU driver and the GPU firmware
the device tree asks for (`qcom/sa8775p/a663_zap.mbn`, `a663_gmu.bin`,
`a660_sqe.fw`).

The image's root is btrfs, which already uses 4096-byte sectors, so unlike
Fedora Server only the EFI partition has to be rebuilt for UFS.

## Flashing

```bash
sudo ./vflasher qualcomm iq9075-evk fedora-workstation
```

Same steps as for Fedora Server: SW2-3 on, USB0 to the PC, power on, confirm,
wait. Then turn SW2-3 off, connect a monitor to a **mini DisplayPort** port
(an adapter to HDMI works if it is an active DP-to-HDMI one), and power-cycle.

On first boot GNOME's setup runs on the screen and creates your user account.
The root file system grows to fill LUN 0 by itself.

## Known caveats

- Not yet tried on a real board, including whether the display comes up. If
  the screen stays dark, log in on the serial console (115200 8N1) and send
  `journalctl -b -k | grep -i -E 'msm|drm|dp'`.
- Only the two mini DP ports are enabled for display in the upstream device
  tree, so use one of those.
