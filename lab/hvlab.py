#!/usr/bin/env python3
"""
Joy Hypervisor Lab
- 실행: 서브 OS 기동 + 메인 채널 연결
- 창/CLI 종료: 채널만 끊고 서브 OS는 계속 유지
- 서브 끄기: GUI 버튼 / python hvlab.py --stop / stop.ps1
- 노트북 재부팅: restart:no 이라 서브는 자동으로 안 올라옴 (꺼진 상태)
"""

from __future__ import annotations

import argparse
import atexit
import sys
import threading
import time
from pathlib import Path

LAB_DIR = Path(__file__).resolve().parent
if str(LAB_DIR) not in sys.path:
    sys.path.insert(0, str(LAB_DIR))

from lifecycle import LabError, start_sub, stop_sub, sub_running  # noqa: E402
from session import ChannelSession  # noqa: E402

_session: ChannelSession | None = None
_disconnect_done = False
_disconnect_lock = threading.Lock()


def disconnect_channel(reason: str = "exit") -> None:
    """Close Main<->Sub session only. Sub OS keeps running."""
    global _disconnect_done, _session
    with _disconnect_lock:
        if _disconnect_done:
            return
        _disconnect_done = True
    print(f"\n[hvlab] disconnect channel ({reason})...", flush=True)
    if _session is not None:
        try:
            _session.close()
        except Exception:
            pass
        _session = None
    print(
        "[hvlab] channel closed. Sub OS still running "
        "(reboot or --stop / stop.ps1 to power off Sub).",
        flush=True,
    )


def shutdown_sub(reason: str = "stop") -> None:
    """Stop Sub OS container (explicit)."""
    disconnect_channel(reason=reason)
    print(f"[hvlab] stopping Sub OS ({reason})...", flush=True)
    try:
        stop_sub()
    except Exception as exc:
        print(f"[hvlab] stop warning: {exc}", flush=True)
    print("[hvlab] Sub OS stopped. Main Windows unchanged.", flush=True)


def _install_signals(kill_sub: bool) -> None:
    import signal

    def _handler(signum, _frame):
        if kill_sub:
            shutdown_sub(reason=f"signal {signum}")
        else:
            disconnect_channel(reason=f"signal {signum}")
        sys.exit(0)

    for name in ("SIGINT", "SIGTERM", "SIGBREAK"):
        sig = getattr(signal, name, None)
        if sig is not None:
            try:
                signal.signal(sig, _handler)
            except Exception:
                pass


def boot(build: bool = True) -> ChannelSession:
    global _session, _disconnect_done
    _disconnect_done = False
    print("[hvlab] starting Sub OS (stays up until reboot or --stop)...", flush=True)
    start_sub(build=build)
    print("[hvlab] connecting channel to Sub...", flush=True)
    sess = ChannelSession()
    sess.connect()
    sess.start_heartbeat()
    _session = sess
    rtt = sess.stats.last_rtt_ms
    try:
        rtt = sess.ping()
    except OSError:
        pass
    print(
        f"[hvlab] READY - Main <-> Sub linked"
        + (f" (rtt {rtt:.2f} ms)" if rtt is not None else ""),
        flush=True,
    )
    return sess


def run_gui(sess: ChannelSession) -> None:
    import tkinter as tk
    from tkinter import ttk

    root = tk.Tk()
    root.title("Joy Hypervisor Lab")
    root.geometry("440x260")
    root.minsize(380, 220)

    status = tk.StringVar(value="연결됨 - 메인 OS <-> 서브 OS")
    rtt_var = tk.StringVar(value="RTT: -")
    hint = tk.StringVar(
        value="창을 닫아도 서브 OS는 유지됩니다. 재부팅 시 자동으로 꺼집니다."
    )

    frm = ttk.Frame(root, padding=16)
    frm.pack(fill=tk.BOTH, expand=True)
    ttk.Label(frm, text="Joy Hypervisor Lab", font=("Segoe UI", 14, "bold")).pack(
        anchor=tk.W
    )
    ttk.Label(frm, textvariable=status).pack(anchor=tk.W, pady=(12, 4))
    ttk.Label(frm, textvariable=rtt_var).pack(anchor=tk.W)
    ttk.Label(frm, textvariable=hint, foreground="#555", wraplength=400).pack(
        anchor=tk.W, pady=(16, 8)
    )

    btn_row = ttk.Frame(frm)
    btn_row.pack(fill=tk.X, pady=(8, 0))

    def on_close_keep_sub() -> None:
        root.destroy()
        disconnect_channel(reason="window close")

    def on_kill_sub() -> None:
        root.destroy()
        shutdown_sub(reason="gui stop button")

    ttk.Button(btn_row, text="창 닫기 (서브 유지)", command=on_close_keep_sub).pack(
        side=tk.LEFT
    )
    ttk.Button(btn_row, text="서브 OS 끄기", command=on_kill_sub).pack(side=tk.RIGHT)

    def tick() -> None:
        with sess.stats.lock:
            ok = sess.stats.connected
            rtt = sess.stats.last_rtt_ms
            err = sess.stats.error
        if ok:
            status.set("연결됨 - 메인 OS <-> 서브 OS 통신 중")
        else:
            status.set("재연결 중..." + (f" ({err})" if err else ""))
        if rtt is not None:
            rtt_var.set(f"RTT: {rtt:.2f} ms")
        root.after(400, tick)

    root.protocol("WM_DELETE_WINDOW", on_close_keep_sub)
    root.after(200, tick)
    root.mainloop()


def main() -> int:
    parser = argparse.ArgumentParser(description="Joy Hypervisor Lab launcher")
    parser.add_argument("--cli", action="store_true", help="GUI 없이 콘솔만")
    parser.add_argument("--no-build", action="store_true", help="이미지 재빌드 생략")
    parser.add_argument(
        "--stop",
        action="store_true",
        help="서브 OS만 종료하고 끝 (재부팅과 동일한 끄기)",
    )
    parser.add_argument(
        "--status",
        action="store_true",
        help="서브 실행 여부만 출력",
    )
    args = parser.parse_args()

    if args.status:
        on = sub_running()
        print("Sub OS: RUNNING" if on else "Sub OS: STOPPED")
        return 0 if on else 1

    if args.stop:
        shutdown_sub(reason="--stop")
        return 0

    # Default: closing app does NOT stop Sub
    atexit.register(lambda: disconnect_channel(reason="atexit"))
    _install_signals(kill_sub=False)

    try:
        sess = boot(build=not args.no_build)
    except LabError as exc:
        print(f"[hvlab] ERROR: {exc}", file=sys.stderr)
        # boot 실패 시에만 잔여 컨테이너 정리 시도하지 않음(이미 떠 있을 수 있음)
        disconnect_channel(reason="boot failure")
        return 1
    except OSError as exc:
        print(f"[hvlab] channel ERROR: {exc}", file=sys.stderr)
        disconnect_channel(reason="channel failure")
        return 1

    try:
        if args.cli:
            print(
                "채널 유지 중. Enter=창만 종료(서브 유지). "
                "서브 끄기: python hvlab.py --stop",
                flush=True,
            )

            def _wait_enter():
                try:
                    sys.stdin.readline()
                except Exception:
                    pass

            t = threading.Thread(target=_wait_enter, daemon=True)
            t.start()
            while t.is_alive():
                with sess.stats.lock:
                    ok = sess.stats.connected
                    rtt = sess.stats.last_rtt_ms
                msg = f"\r  linked={'YES' if ok else 'NO'}"
                if rtt is not None:
                    msg += f"  rtt={rtt:.2f}ms   "
                print(msg, end="", flush=True)
                t.join(timeout=0.5)
            print()
        else:
            run_gui(sess)
    finally:
        disconnect_channel(reason="main return")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
