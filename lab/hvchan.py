"""Shared Lab channel protocol (Main <-> Sub)."""

from __future__ import annotations

import struct

MAGIC = b"HVC1"
HDR = struct.Struct("!4sB3xQQ")  # magic, type, pad3, seq, t_send_ns

TYPE_PING = 1
TYPE_PONG = 2
TYPE_HELLO = 3
TYPE_HELLO_ACK = 4
TYPE_HB = 5
TYPE_HB_ACK = 6

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 19800


def pack(msg_type: int, seq: int, t_send_ns: int = 0) -> bytes:
    return HDR.pack(MAGIC, msg_type, seq, t_send_ns)


def unpack(data: bytes) -> tuple[bytes, int, int, int]:
    return HDR.unpack(data)


HDR_SIZE = HDR.size
