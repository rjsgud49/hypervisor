#!/usr/bin/env bash
# Boot Guest Main + Guest Sub with shared ivshmem channel (Type-1 / KVM host)
# Run on Linux management host only.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DATA="${HV_DATA:-$ROOT/data}"
MAIN_DISK="$DATA/main.qcow2"
SUB_DISK="$DATA/sub.qcow2"
SHM="$DATA/hvchan.shm"

MAIN_CPUS="${HV_MAIN_CPUS:-4}"
SUB_CPUS="${HV_SUB_CPUS:-2}"
MAIN_RAM="${HV_MAIN_RAM:-4096}"
SUB_RAM="${HV_SUB_RAM:-2048}"
INSTALL_ISO="${HV_INSTALL_ISO:-}"

if [[ ! -e /dev/kvm ]]; then
  echo "ERROR: /dev/kvm not found. Use Ubuntu management host, not Windows Lab."
  exit 1
fi
if [[ ! -f "$MAIN_DISK" || ! -f "$SUB_DISK" || ! -f "$SHM" ]]; then
  echo "ERROR: missing disks/shm. Run ./create-disks.sh first."
  exit 1
fi

common_ivshmem=(
  -object "memory-backend-file,id=hvchanmem,share=on,mem-path=${SHM},size=$(( ${HV_CHAN_MB:-16} * 1024 * 1024 ))"
  -device ivshmem-plain,memdev=hvchanmem,master=off
)

iso_args=()
if [[ -n "$INSTALL_ISO" ]]; then
  iso_args=(-cdrom "$INSTALL_ISO" -boot d)
fi

echo "Starting Guest Main (serial stdio in this terminal pair via tmux recommended)..."
echo "  Main disk: $MAIN_DISK"
echo "  Sub  disk: $SUB_DISK"
echo "  Channel:   $SHM"
echo ""

# Main: graphical none, serial multiplex — user attaches via unix sockets
MAIN_SERIAL="$DATA/main.serial"
SUB_SERIAL="$DATA/sub.serial"
MAIN_QMP="$DATA/main.qmp"
rm -f "$MAIN_SERIAL" "$SUB_SERIAL" "$MAIN_QMP"

qemu-system-x86_64 \
  -enable-kvm -machine q35,accel=kvm -cpu host \
  -smp "$MAIN_CPUS" -m "$MAIN_RAM" \
  -drive "file=${MAIN_DISK},if=virtio,format=qcow2" \
  "${common_ivshmem[@]}" \
  -display none \
  -qmp "unix:${MAIN_QMP},server=on,wait=off" \
  -serial "unix:${MAIN_SERIAL},server=on,wait=off" \
  -name hv-main \
  "${iso_args[@]}" \
  -daemonize \
  -pidfile "$DATA/main.pid"

qemu-system-x86_64 \
  -enable-kvm -machine q35,accel=kvm -cpu host \
  -smp "$SUB_CPUS" -m "$SUB_RAM" \
  -drive "file=${SUB_DISK},if=virtio,format=qcow2" \
  "${common_ivshmem[@]}" \
  -display none \
  -serial "unix:${SUB_SERIAL},server=on,wait=off" \
  -name hv-sub \
  -daemonize \
  -pidfile "$DATA/sub.pid"

echo "Guests daemonized."
echo "  console Main:  socat UNIX-CONNECT:${MAIN_SERIAL} STDIO,raw,echo=0"
echo "  console Sub:   socat UNIX-CONNECT:${SUB_SERIAL} STDIO,raw,echo=0"
echo "  screen:        python3 ${ROOT}/channel/fb_view.py --shm ${SHM} --qmp ${MAIN_QMP} --pull auto"
echo "  stop:          ./stop-guests.sh"
echo "Install socat if needed: sudo apt-get install -y socat"
