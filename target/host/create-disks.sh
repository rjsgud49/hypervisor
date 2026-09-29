#!/usr/bin/env bash
# Create guest disks + shared ivshmem backing file
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DATA="${HV_DATA:-$ROOT/data}"
mkdir -p "$DATA"

MAIN_DISK="$DATA/main.qcow2"
SUB_DISK="$DATA/sub.qcow2"
SHM="$DATA/hvchan.shm"
SIZE_MAIN="${HV_MAIN_DISK:-40G}"
SIZE_SUB="${HV_SUB_DISK:-20G}"
SHM_SIZE_MB="${HV_CHAN_MB:-16}"

if [[ ! -f "$MAIN_DISK" ]]; then
  qemu-img create -f qcow2 "$MAIN_DISK" "$SIZE_MAIN"
  echo "created $MAIN_DISK"
else
  echo "exists $MAIN_DISK"
fi

if [[ ! -f "$SUB_DISK" ]]; then
  qemu-img create -f qcow2 "$SUB_DISK" "$SIZE_SUB"
  echo "created $SUB_DISK"
else
  echo "exists $SUB_DISK"
fi

# POSIX shm-style file for ivshmem-plain (both VMs map this)
truncate -s "${SHM_SIZE_MB}M" "$SHM"
chmod 666 "$SHM" || true
echo "ivshmem file: $SHM (${SHM_SIZE_MB}M)"

echo ""
echo "Next: install a guest OS into the qcow2 disks, e.g. Ubuntu cloud image:"
echo "  https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img"
echo "  qemu-img convert -O qcow2 noble-server-cloudimg-amd64.img $MAIN_DISK"
echo "  (then clone/copy for sub, or create-disks again with separate images)"
echo ""
echo "Or attach ISO on first boot via HV_INSTALL_ISO=/path/to.iso ./run-guests.sh"
