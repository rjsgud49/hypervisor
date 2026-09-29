#!/usr/bin/env python3
"""Lab channel server (Sub) — threaded accepts, HELLO / PING / HEARTBEAT."""

from __future__ import annotations

import os
import socket
import struct
import threading

MAGIC = os.environ.get("HVCHAN_MAGIC", "HVC1").encode("ascii")[:4].ljust(4, b"\0")
PORT = int(os.environ.get("HVCHAN_PORT", "19800"))
HDR = struct.Struct("!4sB3xQQ")

TYPE_PING = 1
TYPE_PONG = 2
TYPE_HELLO = 3
TYPE_HELLO_ACK = 4
TYPE_HB = 5
TYPE_HB_ACK = 6


def handle(conn: socket.socket, addr) -> None:
    conn.settimeout(60.0)
    buf = b""
    try:
        while True:
            chunk = conn.recv(4096)
            if not chunk:
                return
            buf += chunk
            while len(buf) >= HDR.size:
                magic, typ, seq, t_send = HDR.unpack_from(buf, 0)
                buf = buf[HDR.size :]
                if magic != MAGIC:
                    return
                if typ == TYPE_PING:
                    conn.sendall(HDR.pack(MAGIC, TYPE_PONG, seq, t_send))
                elif typ == TYPE_HELLO:
                    conn.sendall(HDR.pack(MAGIC, TYPE_HELLO_ACK, seq, 0))
                elif typ == TYPE_HB:
                    conn.sendall(HDR.pack(MAGIC, TYPE_HB_ACK, seq, 0))
    except (OSError, socket.timeout) as exc:
        print(f"[sub] session {addr} end: {exc}", flush=True)
    finally:
        try:
            conn.close()
        except OSError:
            pass


def main() -> None:
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", PORT))
    srv.listen(32)
    print(f"[sub] hvchan listening on 0.0.0.0:{PORT}", flush=True)
    while True:
        conn, addr = srv.accept()
        print(f"[sub] peer {addr}", flush=True)
        threading.Thread(
            target=handle, args=(conn, addr), name=f"hv-{addr[1]}", daemon=True
        ).start()


if __name__ == "__main__":
    main()
