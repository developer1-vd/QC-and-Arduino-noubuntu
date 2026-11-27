#!/usr/bin/env bash
# End-to-end test of flashing a whole 4096-byte-sector UFS LUN (the IQ-9075
# EVK's layout), without hardware. Needs root (it mounts file systems).
#
# Builds a small Fedora-like image (FAT16 EFI with GRUB stubs, ext4 /boot,
# 512-byte-sector XFS root inside an "LVM" volume, fstab naming the LVM
# device) and a 4K-sector LUN 0 with a QLI-style GPT, runs vflasher through
# tests/fake-qdl, and checks the result as a 4K-sector device would see it.
#
# Needs: python3, sgdisk, mtools, dosfstools, xfsprogs, e2fsprogs, attr, xz, curl.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(dirname "$here")"
[ "$(id -u)" -eq 0 ] || { echo "run as root (it mounts file systems)" >&2; exit 1; }
t="$(mktemp -d)"
mnt="$t/mnt"
loops=()
cleanup() {
    mountpoint -q "$mnt" 2>/dev/null && umount "$mnt"
    for l in "${loops[@]}"; do losetup -d "$l" 2>/dev/null || true; done
    rm -rf --one-file-system "$t"
}
trap cleanup EXIT
mkdir -p "$mnt"
pass=0
ok() { printf 'ok   %s\n' "$*"; pass=$((pass + 1)); }
bad() { printf 'FAIL %s\n' "$*" >&2; exit 1; }
loop() { local l; l="$(losetup -f --show "$@")"; loops+=("$l"); echo "$l"; }

for tool in python3 sgdisk mformat mcopy mtype minfo mkfs.vfat mkfs.xfs mkfs.ext4 mkfs.btrfs setfattr getfattr xz curl; do
    command -v "$tool" >/dev/null || bad "missing tool: $tool"
done

ROOT_UUID=53b09c2f-0e29-49f3-869c-61199efd85e7
BOOT_UUID=bec41507-401e-44e4-b04a-7f31312dfb12

# ---------------------------------------------------------------- fixtures

mkdir -p "$t/src"
# EFI: FAT16, 512-byte sectors, with Fedora's two GRUB stubs.
mformat -i "$t/src/esp.img" -C -T 32768 -v EFI -N 8203472A ::
for d in ::/EFI ::/EFI/fedora ::/EFI/BOOT; do mmd -i "$t/src/esp.img" "$d"; done
# shellcheck disable=SC2016  # GRUB variables, not shell
printf 'search --fs-uuid --set=root %s\nset prefix=($root)/grub2\nconfigfile ($root)/grub2/grub.cfg\n' "$BOOT_UUID" > "$t/src/stub.cfg"
mcopy -i "$t/src/esp.img" "$t/src/stub.cfg" ::/EFI/fedora/grub.cfg
mcopy -i "$t/src/esp.img" "$t/src/stub.cfg" ::/EFI/BOOT/grub.cfg
printf 'BOOTAA64' > "$t/src/shim"; mcopy -i "$t/src/esp.img" "$t/src/shim" ::/EFI/BOOT/BOOTAA64.EFI

# /boot: ext4 with 4K blocks, a BLS entry and grub.cfg naming the LVM root.
mkdir -p "$t/bootdir/loader/entries" "$t/bootdir/grub2" "$t/bootdir/dtb-1.0/qcom"
ln -s dtb-1.0 "$t/bootdir/dtb"
echo dtb > "$t/bootdir/dtb-1.0/qcom/test-board.dtb"
printf 'title Fedora\nlinux /vmlinuz\noptions root=/dev/mapper/systemVG-LVRoot ro rd.lvm.lv=systemVG/LVRoot\n' > "$t/bootdir/loader/entries/a.conf"
# shellcheck disable=SC2016  # GRUB variables, not shell
printf 'if [ -z "${kernelopts}" ]; then\n  set kernelopts="root=/dev/mapper/systemVG-LVRoot ro  "\nfi\nset root=hd0\n' > "$t/bootdir/grub2/grub.cfg"
truncate -s 64M "$t/src/boot.img"
mkfs.ext4 -q -b 4096 -U "$BOOT_UUID" -L BOOT -d "$t/bootdir" "$t/src/boot.img"

# Root: XFS with 512-byte sectors, 1 MiB into an "LVM" volume.
truncate -s 320M "$t/src/xfs.img"
mkfs.xfs -q -f -s size=512 -m uuid="$ROOT_UUID" -L fedora "$t/src/xfs.img"
mount -o loop "$t/src/xfs.img" "$mnt"
mkdir -p "$mnt/etc/kernel" "$mnt/etc/lvm/devices" "$mnt/usr/bin" "$mnt/boot/efi"
printf '/dev/systemVG/LVRoot / xfs defaults 0 1\nUUID=%s /boot ext4 defaults 0 0\n' "$BOOT_UUID" > "$mnt/etc/fstab"
echo "root=/dev/mapper/systemVG-LVRoot ro" > "$mnt/etc/kernel/cmdline"
touch "$mnt/etc/lvm/devices/system.devices" "$mnt/etc/lvm/devices/auto-import-rootvg"
head -c 2000000 /dev/urandom > "$mnt/usr/bin/tool"; chmod 755 "$mnt/usr/bin/tool"
ln "$mnt/usr/bin/tool" "$mnt/usr/bin/tool-hardlink"
setfattr -n user.vflasher -v kept "$mnt/usr/bin/tool"
sha_tool="$(sha256sum < "$mnt/usr/bin/tool")"
umount "$mnt"
{ printf 'LABELONE'; head -c $((1024 * 1024 - 8)) /dev/zero; cat "$t/src/xfs.img"; } > "$t/src/root.img"

# Like Fedora Workstation: a btrfs root (4 KiB sectors) with a "root" subvolume.
truncate -s 160M "$t/src/btrfs.img"
mkfs.btrfs -q -s 4096 -U "$ROOT_UUID" -L fedora "$t/src/btrfs.img"
mount -o loop "$t/src/btrfs.img" "$mnt"
btrfs subvolume create "$mnt/root" > /dev/null
mkdir -p "$mnt/root/etc"
printf 'UUID=%s / btrfs compress=zstd:1,defaults,subvol=root 0 1\n' "$ROOT_UUID" > "$mnt/root/etc/fstab"
echo kept > "$mnt/root/etc/marker"
umount "$mnt"

python3 -I - "$t/src" root fedora.raw <<'PY'
import struct, sys
d, root_name, out = sys.argv[1:4]
S = 512
parts = [open(f"{d}/{n}.img", "rb").read() for n in ("esp", "boot", root_name)]
start, starts = 2048, []
for p in parts:
    starts.append(start)
    start += (len(p) + 1048575) // 1048576 * 2048
img = bytearray(start * S + (1 << 20))  # 1 MiB after the last partition, like real images
for i, (s, p) in enumerate(zip(starts, parts)):
    struct.pack_into("<B3xB3xII", img, 446 + 16 * i, 0, 0xEF if i == 0 else 0x83, s, len(p) // S)
    img[s * S: s * S + len(p)] = p
img[510:512] = b"\x55\xaa"
open(f"{d}/{out}", "wb").write(img)
PY
xz -T0 -k "$t/src/fedora.raw"
python3 -I - "$t/src" btrfs workstation.raw <<'PY'
import struct, sys
d, root_name, out = sys.argv[1:4]
S = 512
parts = [open(f"{d}/{n}.img", "rb").read() for n in ("esp", "boot", root_name)]
start, starts = 2048, []
for p in parts:
    starts.append(start)
    start += (len(p) + 1048575) // 1048576 * 2048
img = bytearray(start * S + (1 << 20))  # 1 MiB after the last partition, like real images
for i, (s, p) in enumerate(zip(starts, parts)):
    struct.pack_into("<B3xB3xII", img, 446 + 16 * i, 0, 0xEF if i == 0 else 0x83, s, len(p) // S)
    img[s * S: s * S + len(p)] = p
img[510:512] = b"\x55\xaa"
open(f"{d}/{out}", "wb").write(img)
PY
xz -T0 -k "$t/src/workstation.raw"

mkdir -p "$t/bundle/fw"
head -c 900000 /dev/urandom > "$t/bundle/fw/prog_firehose_ddr.elf"
tar -C "$t/bundle" -czf "$t/src/fw.tar.gz" fw

# The board's LUN 0: 1 GiB, 4096-byte sectors, QLI-style efi/persist/system.
make_lun() { # LAYOUT
    rm -f "$t/lun0.img"
    truncate -s 1G "$t/lun0.img"
    local l; l="$(loop -b 4096 "$t/lun0.img")"
    # (sgdisk -q exits 0 without writing to a blank 4K loop device, so no -q.)
    sgdisk -n 1:6:+512M -t 1:EF00 -c 1:efi -n 2:0:+30M -t 2:8300 -c 2:persist \
        -n 3:0:0 -t 3:8305 -c 3:system "$l" > /dev/null
    if [ "$1" = firmware ]; then sgdisk -d 2 -n 2:0:+4M -c 2:xbl_a "$l" > /dev/null; fi
    blockdev --flushbufs "$l"; sync
    losetup -d "$l"
}

mkdir -p "$t/images/test/ufs/fedora"
cat > "$t/images/test/ufs/fedora/image.conf" <<EOF
NAME=Test Fedora UFS
IMAGE_URL=file://$t/src/fedora.raw.xz
IMAGE_FILE=fedora.raw.xz
IMAGE_SHA256=$(sha256sum "$t/src/fedora.raw.xz" | cut -d' ' -f1)
IMAGE_FORMAT=raw.xz
FLASH_METHOD=qdl
QDL_STORAGE=ufs
QDL_LAYOUT=whole-lun
QDL_LUN=0
QDL_SECTOR_SIZE=4096
QDL_PROGRAMMER_URL=file://$t/src/fw.tar.gz
QDL_PROGRAMMER_FORMAT=tar.gz
QDL_PROGRAMMER_PATH=fw/prog_firehose_ddr.elf
QDL_PROGRAMMER_SHA256=$(sha256sum "$t/bundle/fw/prog_firehose_ddr.elf" | cut -d' ' -f1)
DTB=qcom/test-board.dtb
EOF

run_vflasher() {
    VFLASHER_TEST=1 VFLASHER_IMAGES="$t/images" VFLASHER_CACHE="$t/cache" \
        VFLASHER_QDL="$here/fake-qdl" FAKE_QDL_DISK="$t/lun0.img" FAKE_QDL_LOG="$t/qdl.log" \
        "$repo/vflasher" test ufs fedora --yes
}

# ------------------------------------------------------------------- tests

make_lun normal
run_vflasher > "$t/out.txt" 2>&1 || { cat "$t/out.txt"; bad "vflasher failed"; }
ok "vflasher flashed the fake 4K-sector UFS LUN"
[ -z "$(losetup -a | grep "$t/cache" || true)" ] || bad "loop devices left behind"
ok "no loop devices or mounts left behind"

L="$(loop -r -b 4096 "$t/lun0.img")"
sgdisk -v "$L" | grep -q 'No problems found' || bad "GPT invalid: $(sgdisk -v "$L")"
names="$(sgdisk -p "$L" | awk '/^ *[0-9]/ { print $7 }' | tr '\n' ' ')"
[ "$names" = "efi boot system " ] || bad "unexpected partitions: $names"
start() { sgdisk -i "$1" "$L" | awk -F': ' '/First sector/ { print $2 }' | awk '{ print $1 }'; }
last3="$(sgdisk -i 3 "$L" | awk -F': ' '/Last sector/ { print $2 }' | awk '{ print $1 }')"
[ "$last3" = $(( (1 << 30) / 4096 - 6 )) ] || bad "system does not reach the end of the LUN ($last3)"
ok "4K GPT is valid: efi, boot, system (to the end of the LUN)"

dd if="$t/lun0.img" of="$t/esp-out.img" bs=4096 skip="$(start 1)" count=8192 status=none
info="$(MTOOLS_SKIP_CHECK=1 minfo -i "$t/esp-out.img" ::)"
grep -q 'sector size: 4096 bytes' <<<"$info" || bad "EFI partition does not use 4096-byte sectors"
grep -q 'serial number: 8203472A' <<<"$info" || bad "EFI serial number not kept"
for f in ::/EFI/fedora/grub.cfg ::/EFI/BOOT/grub.cfg; do
    # shellcheck disable=SC2016  # GRUB variable, not shell
    MTOOLS_SKIP_CHECK=1 mtype -i "$t/esp-out.img" "$f" | grep -qx 'devicetree ($root)/dtb/qcom/test-board.dtb' \
        || bad "devicetree missing in $f"
done
[ "$(MTOOLS_SKIP_CHECK=1 mtype -i "$t/esp-out.img" ::/EFI/BOOT/BOOTAA64.EFI)" = BOOTAA64 ] || bad "EFI files not copied"
ok "EFI partition: 4096-byte sectors, serial kept, files copied, device tree set"

B="$(loop -r -b 4096 -o $(( $(start 2) * 4096 )) "$t/lun0.img")"
mount -o ro "$B" "$mnt"
grep -q "options root=UUID=$ROOT_UUID ro\$" "$mnt/loader/entries/a.conf" || bad "BLS entry: $(cat "$mnt/loader/entries/a.conf")"
grep -q "kernelopts=\"root=UUID=$ROOT_UUID ro  \"" "$mnt/grub2/grub.cfg" || bad "grub.cfg fallback not updated"
grep -q '^set root=hd0$' "$mnt/grub2/grub.cfg" || bad "grub.cfg 'set root' was changed"
umount "$mnt"
ok "/boot mounts from a 4K device; boot entries use root=UUID, rd.lvm.lv removed"

R="$(loop -r -b 4096 -o $(( $(start 3) * 4096 )) "$t/lun0.img")"
mount -o ro,nouuid "$R" "$mnt" || bad "root XFS does not mount from a 4K-sector device"
[ "$(blkid -p -o value -s UUID "$R")" = "$ROOT_UUID" ] || bad "root UUID changed"
grep -q "^UUID=$ROOT_UUID / xfs defaults,x-systemd.growfs 0 1\$" "$mnt/etc/fstab" || bad "fstab: $(cat "$mnt/etc/fstab")"
grep -q "^UUID=$BOOT_UUID /boot ext4 defaults 0 0\$" "$mnt/etc/fstab" || bad "fstab /boot line changed"
[ "$(cat "$mnt/etc/kernel/cmdline")" = "root=UUID=$ROOT_UUID ro" ] || bad "cmdline: $(cat "$mnt/etc/kernel/cmdline")"
[ ! -e "$mnt/etc/lvm/devices/system.devices" ] && [ ! -e "$mnt/etc/lvm/devices/auto-import-rootvg" ] || bad "LVM device files left"
[ "$(sha256sum < "$mnt/usr/bin/tool")" = "$sha_tool" ] || bad "file contents changed"
[ "$(stat -c %a "$mnt/usr/bin/tool")" = 755 ] || bad "mode not kept"
[ "$(stat -c %h "$mnt/usr/bin/tool")" = 2 ] || bad "hard link not kept"
[ "$(getfattr --only-values -n user.vflasher "$mnt/usr/bin/tool" 2>/dev/null)" = kept ] || bad "xattr not kept"
umount "$mnt"
ok "root XFS mounts from a 4K device; UUID, fstab, cmdline, contents, links and xattrs correct"

[ "$(head -1 "$t/qdl.log")" = "read 0/0+6 board-gpt.bin" ] && [ "$(tail -1 "$t/qdl.log")" = 'write 0/0 gpt-main.bin' ] \
    || bad "unexpected qdl commands: $(cat "$t/qdl.log")"
ok "qdl: 6 sectors read from LUN 0, primary GPT written last"
if [ -n "${REAL_QDL:-}" ]; then
    ok "every qdl job was also accepted by the real qdl ($REAL_QDL --dry-run)"
fi

# A btrfs root (Fedora Workstation) already works with 4K sectors: it is kept,
# and fstab gets x-systemd.growfs so it fills the LUN.
mkdir -p "$t/images/test/ufs/workstation"
sed -e "s#fedora.raw.xz#workstation.raw.xz#g" \
    -e "s#^IMAGE_SHA256=.*#IMAGE_SHA256=$(sha256sum "$t/src/workstation.raw.xz" | cut -d' ' -f1)#" \
    "$t/images/test/ufs/fedora/image.conf" > "$t/images/test/ufs/workstation/image.conf"
make_lun normal
VFLASHER_TEST=1 VFLASHER_IMAGES="$t/images" VFLASHER_CACHE="$t/cache" \
    VFLASHER_QDL="$here/fake-qdl" FAKE_QDL_DISK="$t/lun0.img" FAKE_QDL_LOG="$t/qdl.log" \
    "$repo/vflasher" test ufs workstation --yes > "$t/out-ws.txt" 2>&1 || { cat "$t/out-ws.txt"; bad "btrfs flash failed"; }
grep -q 'btrfs root file system already works with 4096-byte sectors' "$t/out-ws.txt" || bad "btrfs root was not kept"
L="$(loop -r -b 4096 "$t/lun0.img")"
R="$(loop -r -b 4096 -o $(( $(start 3) * 4096 )) "$t/lun0.img")"
mount -o ro,subvolid=5 "$R" "$mnt" || bad "btrfs root does not mount from a 4K-sector device"
[ "$(cat "$mnt/root/etc/marker")" = kept ] || bad "btrfs contents changed"
grep -q "^UUID=$ROOT_UUID / btrfs compress=zstd:1,defaults,x-systemd.growfs,subvol=root 0 1\$\|^UUID=$ROOT_UUID / btrfs compress=zstd:1,defaults,subvol=root,x-systemd.growfs 0 1\$" "$mnt/root/etc/fstab" \
    || bad "fstab: $(cat "$mnt/root/etc/fstab")"
umount "$mnt"
ok "btrfs root (Fedora Workstation) is kept, mounts from a 4K device, and grows on first boot"

# Too little space for unpacking: a clear error, nothing written, nothing left.
mkdir -p "$t/small"
mount -t tmpfs -o size=12M tmpfs "$t/small"
make_lun normal
cp "$t/lun0.img" "$t/lun0-before.img"
if VFLASHER_TEST=1 VFLASHER_IMAGES="$t/images" VFLASHER_QDL="$here/fake-qdl" FAKE_QDL_DISK="$t/lun0.img" \
    "$repo/vflasher" test ufs fedora --yes --cache "$t/small" > "$t/out-space.txt" 2>&1; then
    umount "$t/small"; bad "unpacking into 12 MB should fail"
fi
left="$(find "$t/small" -name 'work.*' | wc -l)"
umount "$t/small"
if ! grep -q 'ran out of disk space' "$t/out-space.txt" || ! grep -q 'use --cache DIR' "$t/out-space.txt"; then
    bad "unclear error: $(tail -4 "$t/out-space.txt")"
fi
if grep -q 'expected an image with' "$t/out-space.txt"; then bad "misleading partition-count error shown"; fi
[ "$left" = 0 ] || bad "work folder left behind"
cmp -s "$t/lun0.img" "$t/lun0-before.img" || bad "LUN changed although unpacking failed"
ok "out of disk space while unpacking: clear error, --cache suggested, nothing written or left behind"

make_lun firmware
cp "$t/lun0.img" "$t/lun0-before.img"
if run_vflasher > "$t/out2.txt" 2>&1; then bad "a LUN holding firmware must be refused"; fi
grep -q 'holds firmware partitions (xbl_a)' "$t/out2.txt" || bad "wrong error: $(tail -3 "$t/out2.txt")"
cmp -s "$t/lun0.img" "$t/lun0-before.img" || bad "LUN changed although flashing was refused"
ok "refuses a LUN that holds firmware partitions, without writing"

echo "all $pass checks passed"
