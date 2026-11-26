# VFlasher images

Each supported image lives at:

```
images/<manufacturer>/<board>/<os>/image.conf
```

which maps directly onto the VFlasher command line:

```bash
sudo ./vflasher <manufacturer> <board> <os>
```

OS images are hundreds of MB to several GB, so they are not committed to git.
Each `image.conf` instead pins an upstream download URL and its SHA-256.
VFlasher downloads the image, checks it, and refuses to flash on a mismatch.

## Available images

| Manufacturer | Board | OS | Version |
|---|---|---|---|
| qualcomm | [iq9075-evk](qualcomm/iq9075-evk/fedora/) | fedora | 45 |
| arduino | [ventuno-q](arduino/ventuno-q/fedora/) | fedora | 45 |

Every image also needs a line in [`index.txt`](index.txt), which the Windows app
uses to list images online. A test checks that the two stay in sync.

## `image.conf` keys

| Key | Required | Meaning |
|---|---|---|
| `NAME` | yes | Human-readable name |
| `IMAGE_URL` | yes | Download URL |
| `IMAGE_FILE` | yes | File name to cache the download as |
| `IMAGE_SHA256` | yes | SHA-256 of the downloaded file |
| `IMAGE_FORMAT` | yes | `raw`, `raw.xz`, `raw.gz` or `raw.zst` |
| `FLASH_METHOD` | yes | `dd` (write the image to a USB/NVMe/SD drive) or `qdl` (write it to on-board storage over USB in Qualcomm EDL mode) |
| `IMAGE_SIZE` | no | Download size in bytes |
| `DTB` | no | Device tree for the board. With `qdl`, GRUB is set up to load it |
| `OS_VERSION`, `ARCH` | no | Informational |

Extra keys for `FLASH_METHOD=qdl`:

| Key | Required | Meaning |
|---|---|---|
| `QDL_PROGRAMMER_URL` | yes | Where to get Qualcomm's firehose programmer for the board |
| `QDL_PROGRAMMER_FORMAT` | no | `tar.zst` (a file inside an archive; default) or `elf` |
| `QDL_PROGRAMMER_PATH` | for `tar.zst` | Path of the programmer inside the archive |
| `QDL_PROGRAMMER_SHA256` | yes | SHA-256 of the programmer itself |
| `QDL_STORAGE` | no | `emmc` (default) or `ufs` |
| `QDL_ESP_PARTITION` | no | Board partition that receives the EFI partition (default `efi`) |
| `QDL_ROOT_PARTITION` | no | Last board partition, split into `boot` + root (default `system`) |
| `QDL_ROOT_TYPE` | no | Partition type for root: `linux` (default) or `lvm` |
| `EDL_HINT` | no | How to put the board in EDL mode, shown to the user |
| `FIRST_BOOT` | no | Command to suggest after the first boot |

Lines are plain `KEY=value`; nothing in the file is executed.
