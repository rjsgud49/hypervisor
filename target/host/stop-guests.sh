#!/usr/bin/env bash
# Stop Type-1 guests started by run-guests.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DATA="${HV_DATA:-$ROOT/data}"

for role in main sub; do
  pidfile="$DATA/${role}.pid"
  if [[ -f "$pidfile" ]]; then
    pid="$(cat "$pidfile")"
    if kill -0 "$pid" 2>/dev/null; then
      echo "stopping hv-${role} pid=$pid"
      kill "$pid" || true
      sleep 1
      kill -9 "$pid" 2>/dev/null || true
    fi
    rm -f "$pidfile"
  fi
done

# fallback by name
pkill -f 'qemu-system-x86_64.*hv-main' 2>/dev/null || true
pkill -f 'qemu-system-x86_64.*hv-sub' 2>/dev/null || true
rm -f "$DATA/main.qmp"
echo "guests stopped (management host still up; reboot to Windows to leave Type-1)"
