"""Docker lifecycle for Lab Sub OS — start on demand, stop on exit."""

from __future__ import annotations

import shutil
import subprocess
import time
from pathlib import Path

LAB_DIR = Path(__file__).resolve().parent
COMPOSE_FILE = LAB_DIR / "docker-compose.yml"
HOST = "127.0.0.1"
PORT = 19800


class LabError(RuntimeError):
    pass


def _docker() -> str:
    exe = shutil.which("docker")
    if not exe:
        raise LabError("Docker가 없습니다. Docker Desktop을 설치하세요.")
    return exe


def docker_ready(timeout_s: float = 2.0) -> bool:
    try:
        r = subprocess.run(
            [_docker(), "info"],
            capture_output=True,
            timeout=timeout_s,
            check=False,
        )
        return r.returncode == 0
    except (LabError, subprocess.TimeoutExpired, OSError):
        return False


def _compose(*args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    cmd = [_docker(), "compose", "-f", str(COMPOSE_FILE), *args]
    return subprocess.run(
        cmd,
        cwd=str(LAB_DIR),
        capture_output=True,
        text=True,
        check=check,
    )


def port_open(host: str = HOST, port: int = PORT, timeout_s: float = 0.4) -> bool:
    import socket

    try:
        with socket.create_connection((host, port), timeout=timeout_s):
            return True
    except OSError:
        return False


def wait_channel(wait_s: float = 60.0) -> None:
    """Wait until HELLO handshake succeeds (not just TCP accept)."""
    import socket
    import struct

    magic = b"HVC1"
    hdr = struct.Struct("!4sB3xQQ")
    deadline = time.time() + wait_s
    last_err = "timeout"
    while time.time() < deadline:
        try:
            with socket.create_connection((HOST, PORT), timeout=1.0) as sock:
                sock.settimeout(2.0)
                sock.sendall(hdr.pack(magic, 3, 0, 0))  # HELLO
                data = b""
                while len(data) < hdr.size:
                    chunk = sock.recv(hdr.size - len(data))
                    if not chunk:
                        raise ConnectionError("closed")
                    data += chunk
                _m, typ, _s, _t = hdr.unpack(data)
                if typ == 4:  # HELLO_ACK
                    return
                last_err = f"bad type {typ}"
        except OSError as exc:
            last_err = str(exc)
        time.sleep(0.4)
    logs = _compose("logs", "--tail", "40", check=False)
    raise LabError(
        f"채널 HELLO 실패 ({last_err}).\n{logs.stdout}\n{logs.stderr}"
    )


def start_sub(build: bool = True, wait_s: float = 60.0) -> None:
    if not docker_ready(timeout_s=5.0):
        raise LabError(
            "Docker Desktop이 실행 중이 아닙니다. 켠 뒤 프로그램을 다시 실행하세요."
        )
    args = ["up", "-d", "--remove-orphans"]
    if build:
        args.insert(1, "--build")
    r = _compose(*args, check=False)
    if r.returncode != 0:
        raise LabError(f"서브 OS 기동 실패:\n{r.stderr or r.stdout}")
    wait_channel(wait_s=wait_s)


def stop_sub() -> None:
    try:
        _compose("down", "--remove-orphans", check=False)
    except LabError:
        pass


def sub_running() -> bool:
    r = _compose("ps", "--status", "running", "-q", check=False)
    return bool((r.stdout or "").strip())
