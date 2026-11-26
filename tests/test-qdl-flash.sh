#!/usr/bin/env bash
# End-to-end test of VFlasher's EDL/qdl flashing, without hardware.
#
# Builds a fake board eMMC (firmware partitions + efi/persist/system, like the
# Arduino VENTUNO Q), a small Fedora-like disk image and a fake programmer
# archive, runs vflasher with tests/fake-qdl against the eMMC file, and checks
# the result with sgdisk and by comparing bytes.
#
# Needs: python3, sgdisk (gdisk), mtools, xz, zstd, curl.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(dirname "$here")"
t="$(mktemp -d)"
trap 'rm -rf "$t"' EXIT
pass=0
ok() { printf 'ok   %s\n' "$*"; pass=$((pass + 1)); }
bad() { printf 'FAIL %s\n' "$*" >&2; exit 1; }

for tool in python3 sgdisk mformat mcopy mtype xz zstd curl; do
    command -v "$tool" >/dev/null || bad "missing tool: $tool"
done

# ---------------------------------------------------------------- fixtures

# A Fedora-like image: MBR with EFI (FAT, GRUB stubs), /boot and root.
mkdir -p "$t/src"
mformat -i "$t/src/esp.img" -C -T 16384 -v EFI ::
for d in ::/EFI ::/EFI/fedora ::/EFI/BOOT; do mmd -i "$t/src/esp.img" "$d"; done
# shellcheck disable=SC2016  # GRUB variables, not shell
printf 'search --fs-uuid --set=root 1234\nset prefix=($root)/grub2\nconfigfile ($root)/grub2/grub.cfg\n' > "$t/src/stub.cfg"
mcopy -i "$t/src/esp.img" "$t/src/stub.cfg" ::/EFI/fedora/grub.cfg
mcopy -i "$t/src/esp.img" "$t/src/stub.cfg" ::/EFI/BOOT/grub.cfg

python3 -I - "$t/src" <<'PY'
import os, struct, sys
d = sys.argv[1]
S = 512
esp = open(f"{d}/esp.img", "rb").read()
boot = (b"BOOT" * 128) * (24 * 2048)          # 24 MiB
root = bytearray((b"ROOT" * 128) * (64 * 2048))  # 64 MiB, with a zero hole
root[8 << 20: 24 << 20] = bytes(16 << 20)
parts = [(2048, esp), (2048 + len(esp) // S, boot), (2048 + (len(esp) + len(boot)) // S, bytes(root))]
total = parts[-1][0] * S + len(root)
img = bytearray(total)
for i, (start, data) in enumerate(parts):
    t = 0xEF if i == 0 else 0x83
    struct.pack_into("<B3xB3xII", img, 446 + 16 * i, 0, t, start, len(data) // S)
    img[start * S: start * S + len(data)] = data
img[510:512] = b"\x55\xaa"
open(f"{d}/fedora.raw", "wb").write(img)
open(f"{d}/p2.img", "wb").write(boot)
open(f"{d}/p3.img", "wb").write(root)
PY
xz -T0 -k "$t/src/fedora.raw"

# A fake programmer inside a tar.zst, after a filler file, like a vendor archive.
mkdir -p "$t/bundle/b/flash"
head -c 3000000 /dev/urandom > "$t/bundle/b/flash/aaa-filler.bin"
head -c 900000 /dev/urandom > "$t/bundle/b/flash/prog_firehose_ddr.elf"
tar -C "$t/bundle" -cf - ./b | zstd -q -o "$t/src/bundle.tar.zst"

# The board: 512 MiB eMMC with firmware partitions and efi/persist/system.
make_board() {
    python3 -I - "$1" "$2" <<'PY'
import struct, sys, uuid, zlib
path, layout = sys.argv[1], sys.argv[2]
S, N = 512, 1048576
ESP = uuid.UUID("c12a7328-f81f-11d2-ba4b-00a0c93ec93b")
DATA = uuid.UUID("0fc63daf-8483-4772-8e79-3d69d8477de4")
ROOT = uuid.UUID("b921b045-1df0-41c3-af44-4c6f280d3fae")
parts = [("xbl", DATA, 2048, 4095), ("uefi", DATA, 4096, 8191), ("tz", DATA, 8192, 12287)]
efi_end = 16384 + (32768 if layout != "small-efi" else 4096) - 1
parts += [("efi", ESP, 16384, efi_end), ("persist", DATA, 49152, 53247)]
if layout != "no-system":
    parts.append(("system", ROOT, 53248, N - 34))
ents = bytearray(128 * 128)
for i, (name, typ, s, e) in enumerate(parts):
    struct.pack_into("<16s16sQQQ72s", ents, i * 128, typ.bytes_le, uuid.uuid4().bytes_le, s, e, 0,
                     name.encode("utf-16-le"))
disk_guid = uuid.uuid4().bytes_le
def hdr(my, alt, elba):
    h = bytearray(struct.pack("<8sIIIIQQQQ16sQIII", b"EFI PART", 0x10000, 92, 0, 0, my, alt, 34, N - 34,
                              disk_guid, elba, 128, 128, zlib.crc32(ents)))
    struct.pack_into("<I", h, 16, zlib.crc32(h))
    return bytes(h) + bytes(S - 92)
with open(path, "r+b") as f:
    pmbr = bytearray(S)
    struct.pack_into("<B3xB3xII", pmbr, 446, 0, 0xEE, 1, N - 1)
    pmbr[510:512] = b"\x55\xaa"
    f.write(pmbr + hdr(1, N - 1, 2) + ents)
    for name, typ, s, e in parts:
        f.seek(s * S)
        f.write((name.upper().encode() + b"-OLD-") * ((e - s + 1) * S // (len(name) + 5)))
    f.seek((N - 33) * S)
    f.write(ents + hdr(N - 1, 1, N - 33))
PY
}

new_board() {
    rm -f "$t/emmc.img"
    truncate -s 512M "$t/emmc.img"
    make_board "$t/emmc.img" "${1:-normal}"
}

mkdir -p "$t/images/test/board/fedora"
cat > "$t/images/test/board/fedora/image.conf" <<EOF
NAME=Test Fedora
IMAGE_URL=file://$t/src/fedora.raw.xz
IMAGE_FILE=fedora.raw.xz
IMAGE_SHA256=$(sha256sum "$t/src/fedora.raw.xz" | cut -d' ' -f1)
IMAGE_FORMAT=raw.xz
FLASH_METHOD=qdl
QDL_STORAGE=emmc
QDL_ESP_PARTITION=efi
QDL_ROOT_PARTITION=system
QDL_ROOT_TYPE=lvm
QDL_PROGRAMMER_URL=file://$t/src/bundle.tar.zst
QDL_PROGRAMMER_FORMAT=tar.zst
QDL_PROGRAMMER_PATH=./b/flash/prog_firehose_ddr.elf
QDL_PROGRAMMER_SHA256=$(sha256sum "$t/bundle/b/flash/prog_firehose_ddr.elf" | cut -d' ' -f1)
DTB=qcom/test-board.dtb
FIRST_BOOT=grow-root
EOF

run_vflasher() {
    VFLASHER_TEST=1 VFLASHER_IMAGES="$t/images" VFLASHER_CACHE="$t/cache" \
        VFLASHER_QDL="$here/fake-qdl" FAKE_QDL_DISK="$t/emmc.img" FAKE_QDL_LOG="$t/qdl.log" \
        "$repo/vflasher" test board fedora --yes
}

region() { dd if="$t/emmc.img" bs=512 skip="$1" count="$2" status=none; }
part() { sgdisk -i "$1" "$t/emmc.img" | awk -v k="$2" -F': ' '$1 == k { print $2 }' | awk '{ print $1 }'; }
partnum() { sgdisk -p "$t/emmc.img" | awk -v n="$1" '$7 == n { print $1 }'; }

# ------------------------------------------------------------------- tests

new_board
sgdisk -v "$t/emmc.img" | grep -q 'No problems found' || bad "fixture board GPT is invalid: $(sgdisk -v "$t/emmc.img")"
fw_before="$(region 0 16384 | tail -c +$((34 * 512 + 1)) | sha256sum)"
persist_before="$(region 49152 4096 | sha256sum)"

run_vflasher > "$t/out.txt" 2>&1 || { cat "$t/out.txt"; bad "vflasher failed"; }
ok "vflasher flashed the fake board"

sgdisk -v "$t/emmc.img" | grep -q 'No problems found' || bad "GPT invalid after flashing: $(sgdisk -v "$t/emmc.img")"
ok "primary and backup GPT are valid (sgdisk -v)"

[ "$(region 0 16384 | tail -c +$((34 * 512 + 1)) | sha256sum)" = "$fw_before" ] || bad "firmware partitions changed"
ok "firmware partitions are untouched"
[ "$(region 49152 4096 | sha256sum)" = "$persist_before" ] || bad "persist changed"
ok "persist is untouched"

for name in xbl uefi tz efi persist boot system; do
    [ -n "$(partnum "$name")" ] || bad "partition $name missing"
done
[ "$(sgdisk -p "$t/emmc.img" | grep -c '^ *[0-9]')" -eq 7 ] || bad "unexpected partition count"
ok "partitions: xbl uefi tz efi persist boot system"

boot_n="$(partnum boot)" sys_n="$(partnum system)"
boot_start="$(part "$boot_n" 'First sector')" sys_start="$(part "$sys_n" 'First sector')"
sys_end="$(part "$sys_n" 'Last sector')"
[ "$boot_start" = 53248 ] || bad "boot starts at $boot_start"
[ "$((sys_start % 2048))" = 0 ] || bad "system not 1 MiB aligned"
[ "$sys_end" = $((1048576 - 34)) ] || bad "system does not reach the end of the disk ($sys_end)"
[ "$(part "$boot_n" 'Partition GUID code')" = BC13C2FF-59E6-4262-A352-B275FD6F7172 ] || bad "boot type"
[ "$(part "$sys_n" 'Partition GUID code')" = E6D6D379-F507-44C2-A23C-238F2A3DF928 ] || bad "system type"
ok "boot/system placement, alignment and types"

cmp -s <(region "$boot_start" $((24 * 2048))) "$t/src/p2.img" || bad "/boot contents differ"
cmp -s <(region "$sys_start" $((64 * 2048))) "$t/src/p3.img" || bad "root contents differ (zero areas must overwrite old data)"
ok "/boot and root contents match the image byte for byte"

region 16384 16384 > "$t/esp-out.img"
for f in ::/EFI/fedora/grub.cfg ::/EFI/BOOT/grub.cfg; do
    MTOOLS_SKIP_CHECK=1 mtype -i "$t/esp-out.img" "$f" > "$t/stub.out"
    # shellcheck disable=SC2016  # GRUB variable, not shell
    [ "$(sed -n 3p "$t/stub.out")" = 'devicetree ($root)/dtb/qcom/test-board.dtb' ] || bad "devicetree line missing in $f: $(cat "$t/stub.out")"
done
ok "GRUB stubs load the board's device tree"

grep -q 'grow-root' "$t/out.txt" || bad "first-boot hint not shown"
[ "$(grep -c '^read' "$t/qdl.log")" = 1 ] && [ "$(tail -1 "$t/qdl.log")" = 'write 0/0 gpt-main.bin' ] \
    || bad "unexpected qdl commands: $(cat "$t/qdl.log")"
ok "qdl: one table read, primary GPT written last"

# Flashing again over the result (re-install) gives the same layout.
layout_before="$(sgdisk -p "$t/emmc.img" | awk '/^ *[0-9]/ { print $2, $3, $6, $7 }')"
run_vflasher > "$t/out2.txt" 2>&1 || { cat "$t/out2.txt"; bad "re-flashing failed"; }
sgdisk -v "$t/emmc.img" | grep -q 'No problems found' || bad "GPT invalid after re-flashing"
[ "$(sgdisk -p "$t/emmc.img" | awk '/^ *[0-9]/ { print $2, $3, $6, $7 }')" = "$layout_before" ] \
    || bad "layout changed on re-flash"
[ "$(region 0 16384 | tail -c +$((34 * 512 + 1)) | sha256sum)" = "$fw_before" ] || bad "firmware changed on re-flash"
ok "re-flashing a VFlasher-flashed board gives the same layout"

for layout in no-system small-efi; do
    new_board "$layout"
    cp "$t/emmc.img" "$t/emmc-before.img"
    if run_vflasher > "$t/out3.txt" 2>&1; then bad "$layout board should be refused"; fi
    cmp -s "$t/emmc.img" "$t/emmc-before.img" || bad "disk changed on refused $layout board"
    ok "refuses a $layout board without writing ($(grep -o 'error: .*' "$t/out3.txt" | head -1))"
done

new_board
sed -i 's/^QDL_PROGRAMMER_SHA256=.*/QDL_PROGRAMMER_SHA256=0000000000000000000000000000000000000000000000000000000000000000/' \
    "$t/images/test/board/fedora/image.conf"
rm -rf "$t/cache"/firehose-*
if run_vflasher > "$t/out4.txt" 2>&1; then bad "bad programmer checksum should be refused"; fi
grep -q 'expected checksum' "$t/out4.txt" || bad "wrong error: $(cat "$t/out4.txt")"
ok "refuses a programmer with the wrong checksum"

echo "all $pass checks passed"
