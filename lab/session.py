"""Main-side channel session: HELLO + heartbeat with Sub."""

from __future__ import annotations

import socket
import threading
import time
from dataclasses import dataclass, field

from hvchan import (
    DEFAULT_HOST,
    DEFAULT_PORT,
    HDR_SIZE,
    TYPE_HB,
    TYPE_HB_ACK,
    TYPE_HELLO,
    TYPE_HELLO_ACK,
    TYPE_PING,
    TYPE_PONG,
    pack,
    unpack,
)


@dataclass
class SessionStats:
    connected: bool = False
    last_rtt_ms: float | None = None
    hello_ok: bool = False
    seq: int = 0
    error: str | None = None
    lock: threading.Lock = field(default_factory=threading.Lock)


class ChannelSession:
    def __init__(
        self,
        host: str = DEFAULT_HOST,
        port: int = DEFAULT_PORT,
        heartbeat_s: float = 1.0,
    ) -> None:
        self.host = host
        self.port = port
        self.heartbeat_s = heartbeat_s
        self.stats = SessionStats()
        self._sock: socket.socket | None = None
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def connect(self, timeout: float = 5.0) -> None:
        sock = socket.create_connection((self.host, self.port), timeout=timeout)
        sock.settimeout(5.0)
        self._sock = sock
        self._send(TYPE_HELLO, 0)
        typ, _seq, _t = self._recv()
        if typ != TYPE_HELLO_ACK:
            raise ConnectionError(f"expected HELLO_ACK, got type={typ}")
        with self.stats.lock:
            self.stats.connected = True
            self.stats.hello_ok = True
            self.stats.error = None

    def start_heartbeat(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._loop, name="hvchan-hb", daemon=True)
        self._thread.start()

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                self.ping()
            except OSError as exc:
                with self.stats.lock:
                    self.stats.connected = False
                    self.stats.error = str(exc)
                try:
                    self.close_socket_only()
                    time.sleep(0.5)
                    self.connect()
                except OSError as exc2:
                    with self.stats.lock:
                        self.stats.error = str(exc2)
                    self._stop.wait(1.0)
                    continue
            self._stop.wait(self.heartbeat_s)

    def ping(self) -> float:
        if not self._sock:
            raise OSError("not connected")
        with self.stats.lock:
            self.stats.seq += 1
            seq = self.stats.seq
        t0 = time.time_ns()
        self._send(TYPE_PING, seq, t0)
        typ, rseq, _ = self._recv()
        if typ != TYPE_PONG or rseq != seq:
            raise ConnectionError(f"bad pong typ={typ} seq={rseq}")
        rtt = (time.time_ns() - t0) / 1e6
        with self.stats.lock:
            self.stats.last_rtt_ms = rtt
            self.stats.connected = True
            self.stats.error = None
        return rtt

    def heartbeat(self) -> None:
        if not self._sock:
            raise OSError("not connected")
        with self.stats.lock:
            self.stats.seq += 1
            seq = self.stats.seq
        self._send(TYPE_HB, seq)
        typ, rseq, _ = self._recv()
        if typ != TYPE_HB_ACK or rseq != seq:
            raise ConnectionError(f"bad hb ack typ={typ} seq={rseq}")

    def _send(self, typ: int, seq: int, t_send_ns: int = 0) -> None:
        assert self._sock is not None
        self._sock.sendall(pack(typ, seq, t_send_ns))

    def _recv(self) -> tuple[int, int, int]:
        assert self._sock is not None
        data = b""
        while len(data) < HDR_SIZE:
            chunk = self._sock.recv(HDR_SIZE - len(data))
            if not chunk:
                raise ConnectionError("channel closed by peer")
            data += chunk
        magic, typ, seq, t_send = unpack(data)
        if magic != b"HVC1":
            raise ConnectionError("bad magic")
        return typ, seq, t_send

    def close_socket_only(self) -> None:
        if self._sock:
            try:
                self._sock.close()
            except OSError:
                pass
            self._sock = None
        with self.stats.lock:
            self.stats.connected = False

    def close(self) -> None:
        self._stop.set()
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=2.0)
        self.close_socket_only()
