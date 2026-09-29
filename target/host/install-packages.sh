#!/usr/bin/env bash
# Install KVM/QEMU stack on Ubuntu management host
set -euo pipefail

if [[ "$(id -u)" -ne 0 ]]; then
  echo "run with sudo: sudo ./install-packages.sh"
  exit 1
fi

apt-get update
apt-get install -y \
  qemu-system-x86 \
  qemu-utils \
  qemu-kvm \
  libvirt-daemon-system \
  libvirt-clients \
  bridge-utils \
  cpu-checker \
  ovmf \
  cloud-image-utils \
  python3 \
  curl

# optional: add invoking user to kvm/libvirt
if [[ -n "${SUDO_USER:-}" ]]; then
  usermod -aG kvm,libvirt "$SUDO_USER" || true
  echo "Added $SUDO_USER to kvm,libvirt — re-login may be required"
fi

kvm-ok || true
echo "install-packages: OK"
