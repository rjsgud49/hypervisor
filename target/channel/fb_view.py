#!/usr/bin/env python3
"""Show the Type-1 framebuffer slot as a screen.

The page reads only the shared HV slot (ivshmem backing file). It does not
open the other guest's private RAM.

  python fb_view.py
  python fb_view.py --pull qmp --shm ../data/hvchan.shm --qmp ../data/main.qmp
  python fb_view.py --pull watch          # guest fb_publish.py is the writer
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse

from fbmem import SHM_SIZE, map_file, read_frame
from fb_source import run_loop

PAGE = """<!DOCTYPE html>
<html lang="ko">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>HV 화면 메모리</title>
<style>
  :root {
    color-scheme: dark;
    --bg: #0e1116;
    --panel: #171b22;
    --line: #2a3140;
    --text: #e7edf5;
    --muted: #93a0b4;
    --accent: #7eb6ff;
    --live: #3ddc97;
    --wait: #e2b657;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    min-height: 100vh;
    background: radial-gradient(1200px 500px at 50% -10%, #1c2433, var(--bg));
    color: var(--text);
    font: 15px/1.45 "Segoe UI", sans-serif;
  }
  main {
    max-width: 1180px;
    margin: 0 auto;
    padding: 28px 20px 40px;
  }
  h1 { font-size: 22px; font-weight: 650; margin: 0 0 6px; }
  .lead { color: var(--muted); margin: 0 0 22px; }
  .layout {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 260px;
    gap: 18px;
    align-items: start;
  }
  @media (max-width: 860px) {
    .layout { grid-template-columns: 1fr; }
  }
  .monitor {
    background: #0a0c10;
    border: 1px solid #000;
    border-radius: 18px;
    padding: 16px 16px 22px;
    box-shadow: 0 18px 50px rgba(0,0,0,.35);
  }
  .bezel {
    background: #050608;
    border-radius: 8px;
    overflow: hidden;
    min-height: 240px;
    display: grid;
    place-items: center;
  }
  canvas { width: 100%; height: auto; image-rendering: auto; background: #000; display: block; }
  .waiting { color: var(--muted); padding: 72px 16px; text-align: center; }
  .chin {
    display: flex;
    justify-content: center;
    padding-top: 12px;
  }
  .cam {
    width: 8px; height: 8px; border-radius: 50%;
    background: #2a3038;
  }
  .side {
    background: var(--panel);
    border: 1px solid var(--line);
    border-radius: 14px;
    padding: 14px 14px 8px;
  }
  .side h2 { font-size: 13px; letter-spacing: .04em; color: var(--muted); margin: 0 0 8px; font-weight: 600; }
  .row { display: flex; justify-content: space-between; gap: 12px; padding: 8px 0; border-top: 1px solid var(--line); }
  .row span:last-child { text-align: right; }
  .badge {
    display: inline-flex; align-items: center; gap: 6px;
    border-radius: 999px; padding: 3px 8px; font-size: 12px;
    background: #222836;
  }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--wait); }
  .badge.live .dot { background: var(--live); }
  .note { color: var(--muted); font-size: 13px; margin: 10px 0 6px; }
</style>
</head>
<body>
<main>
  <h1>HV 화면 메모리</h1>
  <p class="lead">Type-1 공유 슬롯에 들어온 프레임만 읽어서 화면 레이아웃으로 표시합니다.</p>
  <div class="layout">
    <section class="monitor">
      <div class="bezel" id="bezel">
        <div class="waiting" id="waiting">프레임 대기 중</div>
        <canvas id="screen" hidden></canvas>
      </div>
      <div class="chin"><div class="cam"></div></div>
    </section>
    <aside class="side">
      <h2>슬롯</h2>
      <div class="row"><span>상태</span><span id="state"><span class="badge"><i class="dot"></i>대기</span></span></div>
      <div class="row"><span>소스</span><span id="source">-</span></div>
      <div class="row"><span>해상도</span><span id="size">-</span></div>
      <div class="row"><span>프레임</span><span id="seq">-</span></div>
      <div class="row"><span>경과</span><span id="age">-</span></div>
      <div class="row"><span>오프셋</span><span>0x1000</span></div>
      <p class="note" id="note">게스트 개인 RAM은 읽지 않습니다.</p>
    </aside>
  </div>
</main>
<script>
const canvas = document.getElementById("screen");
const ctx = canvas.getContext("2d", { alpha: false });
const waiting = document.getElementById("waiting");
let lastSeq = -1;

function badge(live, label) {
  return `<span class="badge ${live ? "live" : ""}"><i class="dot"></i>${label}</span>`;
}

async function tick() {
  let meta;
  try {
    meta = await (await fetch("/meta", { cache: "no-store" })).json();
  } catch (err) {
    document.getElementById("state").innerHTML = badge(false, "끊김");
    return;
  }
  const sourceLabel = { demo: "미리보기", qmp: "게스트 VGA", fb: "메인 프레임버퍼", shm: "공유 슬롯" };
  document.getElementById("source").textContent = sourceLabel[meta.source] || meta.source || "-";
  document.getElementById("note").textContent = meta.error
    ? meta.error
    : (meta.source === "demo"
      ? "Type-1 게스트 화면이 없어 같은 슬롯에 미리보기를 넣고 있습니다."
      : "게스트 개인 RAM은 읽지 않습니다.");
  if (!meta.ready) {
    document.getElementById("state").innerHTML = badge(false, "대기");
    document.getElementById("size").textContent = "-";
    document.getElementById("seq").textContent = "-";
    document.getElementById("age").textContent = "-";
    return;
  }
  document.getElementById("state").innerHTML = badge(true, "수신");
  document.getElementById("size").textContent = meta.width + "×" + meta.height;
  document.getElementById("seq").textContent = String(meta.seq);
  document.getElementById("age").textContent = meta.age_ms + " ms";
  if (meta.seq === lastSeq) return;
  const buf = await (await fetch("/pixels?seq=" + meta.seq, { cache: "no-store" })).arrayBuffer();
  if (buf.byteLength !== meta.width * meta.height * 4) return;
  canvas.width = meta.width;
  canvas.height = meta.height;
  canvas.hidden = false;
  waiting.hidden = true;
  ctx.putImageData(new ImageData(new Uint8ClampedArray(buf), meta.width, meta.height), 0, 0);
  lastSeq = meta.seq;
}
tick();
setInterval(tick, 100);
</script>
</body>
</html>
"""

SOURCE_LABEL = {
    "demo": "미리보기",
    "qmp": "게스트 VGA",
    "fb": "메인 프레임버퍼",
    "shm": "공유 슬롯",
    "watch": "공유 슬롯",
}


class Hub:
    def __init__(self, fd: int, mm, pull: str):
        self.fd = fd
        self.mm = mm
        self.pull = pull
        self.error = ""
        self._stop = threading.Event()

    def close(self) -> None:
        self._stop.set()
        try:
            self.mm.close()
        except Exception:
            pass
        try:
            os.close(self.fd)
        except OSError:
            pass


HUB: Hub | None = None


def choose_pull(requested: str, qmp_path: Path) -> str:
    if requested != "auto":
        return requested
    if qmp_path.exists() and hasattr(socket, "AF_UNIX"):
        return "qmp"
    if Path("/dev/fb0").exists():
        return "fb"
    return "demo"


def make_handler(hub: Hub):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt: str, *args) -> None:
            return

        def _send(self, code: int, body: bytes, content_type: str) -> None:
            self.send_response(code)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self) -> None:  # noqa: N802
            path = urlparse(self.path).path
            if path == "/":
                self._send(200, PAGE.encode(), "text/html; charset=utf-8")
                return
            if path == "/meta":
                frame = read_frame(hub.mm)
                if frame is None:
                    payload = {
                        "ready": False,
                        "source": hub.pull,
                        "error": hub.error,
                    }
                else:
                    age = max(0, (time.time_ns() - frame["timestamp_ns"]) // 1_000_000)
                    payload = {
                        "ready": True,
                        "width": frame["width"],
                        "height": frame["height"],
                        "seq": frame["seq"],
                        "flags": frame["flags"],
                        "source": frame["source"],
                        "age_ms": age,
                        "error": hub.error,
                    }
                self._send(200, json.dumps(payload).encode(), "application/json")
                return
            if path == "/pixels":
                frame = read_frame(hub.mm)
                if frame is None:
                    self._send(204, b"", "application/octet-stream")
                    return
                self._send(200, frame["pixels"], "application/octet-stream")
                return
            self._send(404, b"not found", "text/plain; charset=utf-8")

    return Handler


def main() -> None:
    global HUB
    here = Path(__file__).resolve().parent
    default_shm = here.parent / "data" / "hvchan.shm"
    ap = argparse.ArgumentParser(description="Type-1 framebuffer screen view")
    ap.add_argument("--shm", type=Path, default=default_shm)
    ap.add_argument("--qmp", type=Path, default=None)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=19721)
    ap.add_argument("--fps", type=int, default=10)
    ap.add_argument("--pull", choices=("auto", "qmp", "fb", "demo", "watch"), default="auto")
    ap.add_argument("--width", type=int, default=960)
    ap.add_argument("--height", type=int, default=540)
    args = ap.parse_args()

    qmp_path = args.qmp or (args.shm.parent / "main.qmp")
    pull = choose_pull(args.pull, qmp_path)
    fd, mm = map_file(args.shm, SHM_SIZE)
    hub = Hub(fd, mm, pull)
    HUB = hub

    if pull != "watch":
        worker = threading.Thread(
            target=run_loop,
            kwargs={
                "mm": mm,
                "source": pull,
                "fps": args.fps,
                "qmp_path": qmp_path,
                "ppm_path": args.shm.parent / "main-screen.ppm",
                "preview_size": (args.width, args.height),
                "stop": hub._stop.is_set,
                "on_error": lambda msg: setattr(hub, "error", msg),
            },
            daemon=True,
        )
        worker.start()

    server = ThreadingHTTPServer((args.host, args.port), make_handler(hub))
    url = f"http://{args.host}:{args.port}/"
    label = SOURCE_LABEL.get(pull, pull)
    print(f"screen {url}  pull={pull} ({label})  shm={args.shm}", flush=True)
    if pull == "demo":
        print("Type-1 게스트 VGA/QMP가 없어 미리보기 프레임을 같은 슬롯에 기록합니다.", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        hub.close()


if __name__ == "__main__":
    main()
