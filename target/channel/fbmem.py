"""Type-1 HV framebuffer slot inside the ivshmem backing file.

Ping mailboxes stay at offset 0. Screen bytes live in a separate slot so a
guest never reads the other guest's private RAM — only this shared region.

Seqlock: odd seq means a write is in progress. Readers copy only even seq
values and retry if the seq changes.
"""

from __future__ import annotations

import mmap
import os
import struct
import time
from pathlib import Path

MAGIC = b"HVC1"
TYPE_FRAME = 7
FRAME_OFF = 0x1000
HDR_SIZE = 64
FORMAT_RGBA = 1

FLAG_DEMO = 1
FLAG_QMP = 2
FLAG_FB = 4

SHM_SIZE = 16 * 1024 * 1024
SEQ_OFF = 8  # magic(4) + type(1) + pad(3)

# magic, type, seq, timestamp_ns, width, height, format, flags, stride, payload_len, reserved
HDR = struct.Struct("<4sB3xQQHHHHIII20x")
assert HDR.size == HDR_SIZE

SOURCE_NAME = {
    FLAG_DEMO: "demo",
    FLAG_QMP: "qmp",
    FLAG_FB: "fb",
}


def max_payload(shm_size: int = SHM_SIZE) -> int:
    return shm_size - FRAME_OFF - HDR_SIZE


def map_file(path: Path, size: int = SHM_SIZE) -> tuple[int, mmap.mmap]:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(os.fspath(path), os.O_RDWR | os.O_CREAT, 0o666)
    current = os.fstat(fd).st_size
    if current < size:
        os.ftruncate(fd, size)
    if os.name == "nt":
        mm = mmap.mmap(fd, size, access=mmap.ACCESS_WRITE)
    else:
        mm = mmap.mmap(fd, size, mmap.MAP_SHARED, mmap.PROT_READ | mmap.PROT_WRITE)
    return fd, mm


def map_resource(path: Path, size: int) -> tuple[int, mmap.mmap]:
    fd = os.open(os.fspath(path), os.O_RDWR | os.O_SYNC)
    if os.name == "nt":
        mm = mmap.mmap(fd, size, access=mmap.ACCESS_WRITE)
    else:
        mm = mmap.mmap(fd, size, mmap.MAP_SHARED, mmap.PROT_READ | mmap.PROT_WRITE)
    return fd, mm


def publish_frame(
    mm: mmap.mmap,
    rgba: bytes | bytearray,
    width: int,
    height: int,
    flags: int,
    timestamp_ns: int | None = None,
) -> int:
    """Write one RGBA frame. Returns the stable (even) seq."""
    payload = width * height * 4
    if len(rgba) != payload:
        raise ValueError(f"rgba length {len(rgba)} != {width}x{height}x4")
    if payload > max_payload(len(mm)):
        raise ValueError("frame does not fit in the HV slot")

    ts = time.time_ns() if timestamp_ns is None else timestamp_ns
    seq = struct.unpack_from("<Q", mm, FRAME_OFF + SEQ_OFF)[0]
    if seq & 1:
        seq += 1
    writing = seq + 1
    struct.pack_into("<Q", mm, FRAME_OFF + SEQ_OFF, writing)

    pix_off = FRAME_OFF + HDR_SIZE
    mm[pix_off : pix_off + payload] = rgba
    mm[FRAME_OFF : FRAME_OFF + HDR_SIZE] = HDR.pack(
        MAGIC,
        TYPE_FRAME,
        writing,
        ts,
        width,
        height,
        FORMAT_RGBA,
        flags,
        width * 4,
        payload,
        0,
    )
    stable = writing + 1
    struct.pack_into("<Q", mm, FRAME_OFF + SEQ_OFF, stable)
    return stable


def read_frame(mm: mmap.mmap, attempts: int = 8) -> dict | None:
    """Return a stable frame snapshot, or None if the slot is empty."""
    pix_off = FRAME_OFF + HDR_SIZE
    for _ in range(attempts):
        raw = bytes(mm[FRAME_OFF : FRAME_OFF + HDR_SIZE])
        magic, typ, seq, ts, width, height, fmt, flags, stride, payload, _reserved = HDR.unpack(
            raw
        )
        if magic != MAGIC or typ != TYPE_FRAME or seq == 0 or (seq & 1):
            if magic != MAGIC:
                return None
            continue
        if fmt != FORMAT_RGBA or width <= 0 or height <= 0:
            return None
        if payload != width * height * 4 or stride != width * 4:
            return None
        if pix_off + payload > len(mm):
            return None
        pixels = bytes(mm[pix_off : pix_off + payload])
        seq2 = struct.unpack_from("<Q", mm, FRAME_OFF + SEQ_OFF)[0]
        if seq2 != seq:
            continue
        return {
            "seq": seq,
            "timestamp_ns": ts,
            "width": width,
            "height": height,
            "format": fmt,
            "flags": flags,
            "source": SOURCE_NAME.get(flags, "shm"),
            "pixels": pixels,
        }
    return None
