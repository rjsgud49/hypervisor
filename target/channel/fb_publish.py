#!/usr/bin/env python3
"""Publish the local framebuffer into the Type-1 HV slot.

Run inside Guest Main (ivshmem BAR) or on the management host (backing file).
This writes the shared slot only. It does not read the other guest.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from fbmem import SHM_SIZE, map_file, map_resource
from fb_source import run_loop


def main() -> None:
    here = Path(__file__).resolve().parent
    ap = argparse.ArgumentParser(description="Publish a framebuffer into the HV slot")
    ap.add_argument("--shm", type=Path, default=here.parent / "data" / "hvchan.shm")
    ap.add_argument("--resource", type=Path, default=None, help="guest ivshmem resource0")
    ap.add_argument("--source", choices=("fb", "demo"), default="fb")
    ap.add_argument("--fps", type=int, default=10)
    ap.add_argument("--size", type=int, default=SHM_SIZE)
    ap.add_argument("--width", type=int, default=960)
    ap.add_argument("--height", type=int, default=540)
    args = ap.parse_args()

    if args.resource is not None:
        fd, mm = map_resource(args.resource, args.size)
    else:
        fd, mm = map_file(args.shm, args.size)

    print(f"publishing source={args.source} into {'resource' if args.resource else args.shm}", flush=True)
    try:
        run_loop(
            mm,
            args.source,
            args.fps,
            preview_size=(args.width, args.height),
            on_error=lambda msg: print(msg, file=sys.stderr, flush=True) if msg else None,
        )
    except KeyboardInterrupt:
        pass
    finally:
        mm.close()
        os_close(fd)


def os_close(fd: int) -> None:
    import os

    try:
        os.close(fd)
    except OSError:
        pass


if __name__ == "__main__":
    main()
