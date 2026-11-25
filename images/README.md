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

## `image.conf` keys

| Key | Required | Meaning |
|---|---|---|
| `NAME` | yes | Human-readable name |
| `IMAGE_URL` | yes | Download URL |
| `IMAGE_FILE` | yes | File name to cache the download as |
| `IMAGE_SHA256` | yes | SHA-256 of the downloaded file |
| `IMAGE_FORMAT` | yes | `raw`, `raw.xz`, `raw.gz` or `raw.zst` |
| `FLASH_METHOD` | yes | `dd` (only method so far) |
| `IMAGE_SIZE` | no | Download size in bytes |
| `OS_VERSION`, `ARCH`, `DTB` | no | Informational |

Lines are plain `KEY=value`; nothing in the file is executed.
