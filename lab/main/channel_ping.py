#!/usr/bin/env python3
"""Lab HV channel client (Main / Windows). Reuses one TCP session for RTT samples."""

from __future__ import annotations

import argparse
import socket
import statistics
import struct
import time

MAGIC = b"HVC1"
HDR = struct.Struct("!4sB3xQQ")


def main() -> None:
    ap = argparse.ArgumentParser(description="Lab channel ping (Main -> Sub)")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=19800)
    ap.add_argument("-n", type=int, default=20)
    ap.add_argument("--timeout", type=float, default=2.0)
    args = ap.parse_args()

    samples: list[float] = []
    with socket.create_connection((args.host, args.port), timeout=args.timeout) as sock:
        sock.settimeout(args.timeout)
        for i in range(args.n):
            t_send = time.time_ns()
            sock.sendall(HDR.pack(MAGIC, 1, i, t_send))
            data = b""
            while len(data) < HDR.size:
                chunk = sock.recv(HDR.size - len(data))
                if not chunk:
                    raise ConnectionError("closed before pong")
                data += chunk
            magic, typ, rseq, _ = HDR.unpack(data)
            if magic != MAGIC or typ != 2 or rseq != i:
                raise RuntimeError(f"bad pong magic={magic!r} typ={typ} seq={rseq}")
            ms = (time.time_ns() - t_send) / 1e6
            samples.append(ms)
            print(f"seq={i} rtt_ms={ms:.3f}")

    samples_sorted = sorted(samples)
    p50 = statistics.median(samples_sorted)
    idx95 = min(len(samples_sorted) - 1, max(0, int(round(0.95 * (len(samples_sorted) - 1)))))
    p95 = samples_sorted[idx95]
    print(f"--- n={len(samples)} p50_ms={p50:.3f} p95_ms={p95:.3f} max_ms={max(samples):.3f}")


if __name__ == "__main__":
    main()
