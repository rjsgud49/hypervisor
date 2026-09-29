#!/usr/bin/env python3
"""
Guest-side ivshmem channel ping (Type-1 Phase 1 Step B/C userspace).
Run INSIDE Guest Main or Guest Sub after locating the ivshmem PCI resource.

This does NOT read the other guest's private RAM — only the shared HV-backed region.
"""

from __future__ import annotations

import argparse
import mmap
import os
import struct
import time
from pathlib import Path

MAGIC = b"HVC1"
HDR = struct.Struct("!4sB3xQQ")  # compatible with lab hvchan
TYPE_PING, TYPE_PONG = 1, 2
SLOT = 64  # bytes per direction slot


def find_ivshmem_resource() -> Path:
    """Best-effort: first PCI resource0 under a Red Hat ivshmem vendor if present."""
    pci = Path("/sys/bus/pci/devices")
    if not pci.exists():
        raise SystemExit("not a Linux guest sysfs")
    for dev in pci.iterdir():
        vendor = (dev / "vendor").read_text().strip()
        device = (dev / "device").read_text().strip()
        # ivshmem often appears as 1af4:1110 (virtio-like) or qemu ivshmem ids — probe resource0 size
        res = dev / "resource0"
        if res.exists():
            # Prefer devices with 'ivshmem' in uevent
            uevent = (dev / "uevent").read_text() if (dev / "uevent").exists() else ""
            if "ivshmem" in uevent.lower() or device in ("0x1110", "0x1111"):
                return res
    # fallback: largest resource0 under 1af4
    candidates = []
    for dev in pci.iterdir():
        res = dev / "resource0"
        if res.exists() and (dev / "vendor").read_text().strip() == "0x1af4":
            candidates.append(res)
    if candidates:
        return candidates[0]
    raise SystemExit(
        "ivshmem BAR not found. Check lspci -nn and pass correct --resource PATH"
    )


def open_map(resource: Path, size: int) -> mmap.mmap:
    fd = os.open(str(resource), os.O_RDWR | os.O_SYNC)
    return mmap.mmap(fd, size, mmap.MAP_SHARED, mmap.PROT_READ | mmap.PROT_WRITE)


def main() -> None:
    ap = argparse.ArgumentParser(description="Type-1 ivshmem HVC1 ping")
    ap.add_argument("--resource", type=Path, help="sysfs PCI resource0 path")
    ap.add_argument("--size", type=int, default=16 * 1024 * 1024)
    ap.add_argument("--role", choices=("main", "sub"), required=True)
    ap.add_argument("-n", type=int, default=20)
    args = ap.parse_args()

    res = args.resource or find_ivshmem_resource()
    mm = open_map(res, args.size)

    # layout: [0:SLOT)= main->sub mailbox, [SLOT:2*SLOT)= sub->main
    if args.role == "main":
        tx, rx = 0, SLOT
    else:
        tx, rx = SLOT, 0

    samples = []
    for i in range(args.n):
        t0 = time.time_ns()
        pkt = HDR.pack(MAGIC, TYPE_PING, i, t0)
        mm[tx : tx + len(pkt)] = pkt
        # naive poll for pong in rx (peer must run --serve)
        deadline = time.time() + 2.0
        while time.time() < deadline:
            raw = bytes(mm[rx : rx + HDR.size])
            if raw[:4] == MAGIC and raw[4] == TYPE_PONG:
                _m, typ, seq, _t = HDR.unpack(raw[: HDR.size])
                if seq == i:
                    samples.append((time.time_ns() - t0) / 1e6)
                    # clear
                    mm[rx : rx + HDR.size] = b"\0" * HDR.size
                    break
            time.sleep(0.0005)
        else:
            print(f"seq={i} TIMEOUT (is peer in --serve?)")
            continue
        print(f"seq={i} rtt_ms={samples[-1]:.3f}")

    if samples:
        samples.sort()
        p50 = samples[len(samples) // 2]
        p95 = samples[max(0, int(len(samples) * 0.95) - 1)]
        print(f"--- n={len(samples)} p50_ms={p50:.3f} p95_ms={p95:.3f}")
    mm.close()


def serve(role: str, resource: Path | None, size: int) -> None:
    res = resource or find_ivshmem_resource()
    mm = open_map(res, size)
    if role == "sub":
        rx, tx = 0, SLOT  # receive main->sub, reply sub->main
    else:
        rx, tx = SLOT, 0
    print(f"[serve] role={role} resource={res}", flush=True)
    while True:
        raw = bytes(mm[rx : rx + HDR.size])
        if raw[:4] == MAGIC and raw[4] == TYPE_PING:
            _m, typ, seq, t_send = HDR.unpack(raw[: HDR.size])
            mm[rx : rx + HDR.size] = b"\0" * HDR.size
            mm[tx : tx + HDR.size] = HDR.pack(MAGIC, TYPE_PONG, seq, t_send)
        time.sleep(0.0002)


if __name__ == "__main__":
    import sys

    if "--serve" in sys.argv:
        sys.argv.remove("--serve")
        ap = argparse.ArgumentParser()
        ap.add_argument("--role", choices=("main", "sub"), required=True)
        ap.add_argument("--resource", type=Path)
        ap.add_argument("--size", type=int, default=16 * 1024 * 1024)
        a = ap.parse_args()
        serve(a.role, a.resource, a.size)
    else:
        main()
