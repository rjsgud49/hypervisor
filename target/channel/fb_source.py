"""Fill the Type-1 framebuffer slot from the guest display, QEMU VGA, or a preview."""

from __future__ import annotations

import json
import mmap
import os
import socket
import time
from pathlib import Path

from fbmem import FLAG_DEMO, FLAG_FB, FLAG_QMP, max_payload, publish_frame

# 5x7 glyphs, rows of 0/1. Enough for the preview desktop labels.
_GLYPHS = {
    " ": ["00000"] * 7,
    "-": ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
    ":": ["00000", "00100", "00100", "00000", "00100", "00100", "00000"],
    "/": ["00001", "00010", "00010", "00100", "01000", "01000", "10000"],
    ".": ["00000", "00000", "00000", "00000", "00000", "01100", "01100"],
    "0": ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
    "1": ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
    "2": ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
    "3": ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
    "4": ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
    "5": ["11111", "10000", "10000", "11110", "00001", "00001", "11110"],
    "6": ["01110", "10000", "10000", "11110", "10001", "10001", "01110"],
    "7": ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
    "8": ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
    "9": ["01110", "10001", "10001", "01111", "00001", "00001", "01110"],
    "A": ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
    "B": ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
    "C": ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
    "D": ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
    "E": ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
    "F": ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
    "G": ["01110", "10001", "10000", "10111", "10001", "10001", "01111"],
    "H": ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
    "I": ["01110", "00100", "00100", "00100", "00100", "00100", "01110"],
    "J": ["00111", "00010", "00010", "00010", "00010", "10010", "01100"],
    "K": ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
    "L": ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
    "M": ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
    "N": ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
    "O": ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    "P": ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
    "R": ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
    "S": ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
    "T": ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    "U": ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
    "V": ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
    "W": ["10001", "10001", "10001", "10101", "10101", "10101", "01010"],
    "X": ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
    "Y": ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
}


def fill_rect(buf: bytearray, width: int, x: int, y: int, rw: int, rh: int, rgba: bytes) -> None:
    if rw <= 0 or rh <= 0:
        return
    x0 = max(0, x)
    y0 = max(0, y)
    x1 = min(width, x + rw)
    y1 = min(len(buf) // (width * 4), y + rh)
    if x0 >= x1 or y0 >= y1:
        return
    row = rgba * (x1 - x0)
    stride = width * 4
    for yy in range(y0, y1):
        off = yy * stride + x0 * 4
        buf[off : off + len(row)] = row


def draw_text(
    buf: bytearray,
    width: int,
    x: int,
    y: int,
    text: str,
    rgba: bytes,
    scale: int = 2,
) -> None:
    cursor = x
    for ch in text.upper():
        glyph = _GLYPHS.get(ch)
        if glyph is None:
            cursor += 6 * scale
            continue
        for row_i, row in enumerate(glyph):
            for col_i, bit in enumerate(row):
                if bit == "1":
                    fill_rect(
                        buf,
                        width,
                        cursor + col_i * scale,
                        y + row_i * scale,
                        scale,
                        scale,
                        rgba,
                    )
        cursor += 6 * scale


def render_preview(width: int, height: int, tick: int) -> bytes:
    """Desktop-shaped preview stored in the same slot as a real frame."""
    buf = bytearray(width * height * 4)
    fill_rect(buf, width, 0, 0, width, height, b"\x1b\x1f\x2a\xff")

    fill_rect(buf, width, 36, 28, 560, 300, b"\x2c\x33\x44\xff")
    fill_rect(buf, width, 36, 28, 560, 28, b"\x3d\x7e\xee\xff")
    draw_text(buf, width, 48, 36, "MAIN GUEST", b"\xff\xff\xff\xff", 2)
    fill_rect(buf, width, 52, 76, 220, 120, b"\x8f\xb8\xff\xff")
    fill_rect(buf, width, 288, 76, 280, 72, b"\xf2\xc1\x4e\xff")
    fill_rect(buf, width, 288, 160, 280, 120, b"\x46\xc4\x8a\xff")
    draw_text(buf, width, 64, 214, "FRAME SLOT", b"\xe8\xee\xf7\xff", 2)

    fill_rect(buf, width, 620, 48, 300, 200, b"\x24\x2a\x38\xff")
    fill_rect(buf, width, 620, 48, 300, 24, b"\x5b\x4b\x8a\xff")
    draw_text(buf, width, 632, 54, "HV CHANNEL", b"\xff\xff\xff\xff", 2)
    bar_x = 640 + (tick % 240)
    fill_rect(buf, width, 640, 110, 260, 16, b"\x12\x16\x22\xff")
    fill_rect(buf, width, bar_x, 110, 40, 16, b"\x7d\xd3\xfc\xff")
    draw_text(buf, width, 640, 160, f"SEQ {tick:05d}", b"\xd5\xdc\xe8\xff", 2)

    bar_h = 40
    fill_rect(buf, width, 0, height - bar_h, width, bar_h, b"\x12\x14\x1c\xff")
    fill_rect(buf, width, 16, height - 30, 92, 20, b"\x3d\x7e\xee\xff")
    draw_text(buf, width, 28, height - 26, "SCREEN", b"\xff\xff\xff\xff", 2)
    draw_text(buf, width, 130, height - 26, f"{width}X{height}", b"\xb7\xc0\xd4\xff", 2)
    return bytes(buf)


def rgb_to_rgba(rgb: bytes) -> bytes:
    n = len(rgb) // 3
    out = bytearray(n * 4)
    out[0::4] = rgb[0::3]
    out[1::4] = rgb[1::3]
    out[2::4] = rgb[2::3]
    out[3::4] = b"\xff" * n
    return bytes(out)


def bgra_to_rgba(bgra: bytes, width: int, height: int, stride: int) -> bytes:
    out = bytearray(width * height * 4)
    for y in range(height):
        row = bgra[y * stride : y * stride + width * 4]
        base = y * width * 4
        span = width * 4
        out[base : base + span : 4] = row[2::4]
        out[base + 1 : base + span : 4] = row[1::4]
        out[base + 2 : base + span : 4] = row[0::4]
        out[base + 3 : base + span : 4] = b"\xff" * width
    return bytes(out)


def fit_rgba(rgba: bytes, width: int, height: int, limit: int) -> tuple[int, int, bytes]:
    scale = 1
    while (width // scale) * (height // scale) * 4 > limit and scale < width and scale < height:
        scale += 1
    if scale == 1:
        return width, height, rgba
    nw, nh = width // scale, height // scale
    out = bytearray(nw * nh * 4)
    for y in range(nh):
        sy = y * scale
        for x in range(nw):
            si = (sy * width + x * scale) * 4
            di = (y * nw + x) * 4
            out[di : di + 4] = rgba[si : si + 4]
    return nw, nh, bytes(out)


def parse_p6(data: bytes) -> tuple[int, int, bytes]:
    if not data.startswith(b"P6"):
        raise ValueError("screendump is not a P6 PPM")
    i = 2

    def token() -> bytes:
        nonlocal i
        while True:
            while i < len(data) and data[i] in b" \t\r\n":
                i += 1
            if i < len(data) and data[i] == ord("#"):
                while i < len(data) and data[i] != ord("\n"):
                    i += 1
                continue
            break
        start = i
        while i < len(data) and data[i] not in b" \t\r\n":
            i += 1
        return data[start:i]

    width = int(token())
    height = int(token())
    token()  # maxval
    if i < len(data) and data[i] in b" \t\r\n":
        i += 1
    need = width * height * 3
    raw = data[i : i + need]
    if len(raw) != need:
        raise ValueError("truncated PPM")
    return width, height, raw


class QmpClient:
    def __init__(self, path: Path):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.connect(os.fspath(path))
        self.sock.settimeout(8)
        self.buf = b""
        self._read_obj()
        self.command("qmp_capabilities")

    def close(self) -> None:
        self.sock.close()

    def _read_obj(self) -> dict:
        while b"\n" not in self.buf:
            chunk = self.sock.recv(1 << 20)
            if not chunk:
                raise EOFError("QMP closed")
            self.buf += chunk
        line, self.buf = self.buf.split(b"\n", 1)
        return json.loads(line.decode())

    def command(self, execute: str, arguments: dict | None = None) -> dict:
        req: dict = {"execute": execute}
        if arguments:
            req["arguments"] = arguments
        self.sock.sendall(json.dumps(req).encode() + b"\n")
        while True:
            obj = self._read_obj()
            if "return" in obj or "error" in obj:
                return obj


def read_fb0() -> tuple[int, int, bytes]:
    sysfs = Path("/sys/class/graphics/fb0")
    w_s, h_s = (sysfs / "virtual_size").read_text().strip().split(",")
    width, height = int(w_s), int(h_s)
    bpp = int((sysfs / "bits_per_pixel").read_text())
    stride = int((sysfs / "stride").read_text())
    fd = os.open("/dev/fb0", os.O_RDONLY)
    try:
        mm = mmap.mmap(fd, stride * height, mmap.MAP_SHARED, mmap.PROT_READ)
        try:
            raw = bytes(mm)
        finally:
            mm.close()
    finally:
        os.close(fd)
    if bpp == 32:
        return width, height, bgra_to_rgba(raw, width, height, stride)
    if bpp == 24:
        rows = bytearray()
        for y in range(height):
            rows += raw[y * stride : y * stride + width * 3]
        return width, height, rgb_to_rgba(bytes(rows))
    raise RuntimeError(f"unsupported fb0 bpp={bpp}")


def pull_qmp_once(client: QmpClient, ppm_path: Path, mm: mmap.mmap) -> None:
    ppm_path.parent.mkdir(parents=True, exist_ok=True)
    result = client.command("screendump", {"filename": os.fspath(ppm_path)})
    if "error" in result:
        desc = result["error"].get("desc", "screendump failed")
        raise RuntimeError(desc)
    width, height, rgb = parse_p6(ppm_path.read_bytes())
    rgba = rgb_to_rgba(rgb)
    width, height, rgba = fit_rgba(rgba, width, height, max_payload(len(mm)))
    publish_frame(mm, rgba, width, height, FLAG_QMP)


def publish_fb0_once(mm: mmap.mmap) -> None:
    width, height, rgba = read_fb0()
    width, height, rgba = fit_rgba(rgba, width, height, max_payload(len(mm)))
    publish_frame(mm, rgba, width, height, FLAG_FB)


def publish_preview_once(mm: mmap.mmap, width: int, height: int, tick: int) -> None:
    publish_frame(mm, render_preview(width, height, tick), width, height, FLAG_DEMO)


def run_loop(
    mm: mmap.mmap,
    source: str,
    fps: int,
    qmp_path: Path | None = None,
    ppm_path: Path | None = None,
    preview_size: tuple[int, int] = (960, 540),
    stop: callable | None = None,
    on_error: callable | None = None,
) -> None:
    interval = 1.0 / max(1, fps)
    client: QmpClient | None = None
    tick = 0
    try:
        while stop is None or not stop():
            started = time.monotonic()
            try:
                if source == "qmp":
                    if client is None:
                        if qmp_path is None:
                            raise RuntimeError("QMP socket path missing")
                        client = QmpClient(qmp_path)
                    if ppm_path is None:
                        raise RuntimeError("PPM path missing")
                    pull_qmp_once(client, ppm_path, mm)
                elif source == "fb":
                    publish_fb0_once(mm)
                elif source == "demo":
                    publish_preview_once(mm, preview_size[0], preview_size[1], tick)
                    tick += 1
                else:
                    raise RuntimeError(f"unknown source {source}")
                if on_error:
                    on_error("")
            except Exception as exc:  # noqa: BLE001 — keep the viewer alive
                if on_error:
                    on_error(str(exc))
                client = None
            elapsed = time.monotonic() - started
            time.sleep(max(0.0, interval - elapsed))
    finally:
        if client is not None:
            client.close()
