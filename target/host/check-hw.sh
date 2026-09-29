#!/usr/bin/env bash
# Type-1 management host hardware check (run on Ubuntu/KVM host — NOT Windows)
set -euo pipefail

echo "=== CPU / virt ==="
lscpu | egrep 'Architecture|Model name|Virtualization|Hypervisor' || true
egrep -c '(vmx|svm)' /proc/cpuinfo || true

echo "=== /dev/kvm ==="
if [[ -e /dev/kvm ]]; then
  ls -l /dev/kvm
else
  echo "FAIL: /dev/kvm missing (install qemu-kvm / enable SVM in BIOS)"
fi

echo "=== modules ==="
lsmod | egrep 'kvm|kvm_amd|kvm_intel|vfio' || true

echo "=== IOMMU (dmesg) ==="
if dmesg 2>/dev/null | egrep -i 'iommu|AMD-Vi|DMAR' | tail -n 20; then
  :
else
  echo "(no iommu lines — try amd_iommu=on on kernel cmdline)"
fi

echo "=== cmdline ==="
cat /proc/cmdline

echo "=== memory ==="
free -h

echo "=== disk free ==="
df -h /

echo "Done. Fix FAILs before create-disks.sh / run-guests.sh"
