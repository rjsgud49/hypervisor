#!/usr/bin/env python3
"""Web relay for HvScreen. Both PCs connect outbound; the room name is the key."""

import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MAX_FRAME = 8 * 1024 * 1024
TTL_SEC = 3.0

_lock = threading.Lock()
_rooms = {}


PAGE = """<!DOCTYPE html><html lang="ko"><head><meta charset="utf-8"/>
<title>HV 화면 모음</title><style>
body{margin:0;background:#0e1116;color:#e7edf5;font:15px/1.4 Segoe UI,sans-serif}
header{padding:22px 24px 8px}h1{margin:0 0 6px;font-size:22px}
p{margin:0;color:#93a0b4}
#wall{display:grid;grid-template-columns:repeat(auto-fit,minmax(420px,1fr));gap:16px;padding:16px 24px 32px}
.card{background:#171b22;border:1px solid #2a3140;border-radius:14px;overflow:hidden}
.card header{padding:10px 12px 0}img{width:100%;background:#000;display:block}
#empty{color:#93a0b4;padding:48px 24px}
</style></head><body><header><h1>실행 중인 화면</h1>
<p id="lead">화면 보내기가 켜진 PC가 여기 모두 표시됩니다.</p></header>
<div id="empty">아직 실행 중인 화면이 없습니다.</div><div id="wall"></div>
<script>
const wall=document.getElementById('wall');
const empty=document.getElementById('empty');
const cards={};
async function tick(){
let list=[];
try{list=await (await fetch('list',{cache:'no-store'})).json();}catch(e){return;}
const seen={};
empty.hidden=list.length>0;
for(const item of list){
seen[item.id]=true;
let card=cards[item.id];
if(!card){
card=document.createElement('section');
card.className='card';
card.innerHTML='<header></header><img alt="">';
wall.appendChild(card);
cards[item.id]=card;
}
card.querySelector('header').textContent=item.name;
card.querySelector('img').src='frame/'+encodeURIComponent(item.id)+'?t='+Date.now();
}
Object.keys(cards).forEach(function(id){
if(!seen[id]){cards[id].remove();delete cards[id];}
});
}
tick();setInterval(tick,400);
</script></body></html>
"""


def clean_token(raw):
    if not raw:
        return ""
    acc = []
    for ch in raw:
        ok = ("a" <= ch <= "z") or ("A" <= ch <= "Z") or ("0" <= ch <= "9") or ch in "-_"
        if ok:
            acc.append(ch)
        if len(acc) >= 48:
            break
    return "".join(acc)


def split_room(path):
    if path in ("/", "/list", "/frame") or path.startswith("/frame/"):
        return "live", path
    if not path.startswith("/r/"):
        return None
    rest = path[3:]
    slash = rest.find("/")
    raw = rest if slash < 0 else rest[:slash]
    room = clean_token(raw)
    if len(room) < 4 or room != raw:
        return None
    tail = "" if slash < 0 else rest[slash:]
    return room, tail


def list_json(room):
    now = time.time()
    items = []
    with _lock:
        slots = _rooms.get(room)
        if not slots:
            return []
        drop = []
        for sid, slot in slots.items():
            age = now - slot["seen"]
            if age > TTL_SEC or not slot["jpeg"]:
                drop.append(sid)
                continue
            items.append({
                "id": sid,
                "name": slot["name"],
                "age_ms": int(age * 1000),
            })
        for sid in drop:
            del slots[sid]
        if not slots:
            _rooms.pop(room, None)
    return items


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        return

    def _send(self, code, content_type, body):
        data = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        if data:
            self.wfile.write(data)

    def _route(self):
        path = self.path.split("?", 1)[0]
        parsed = split_room(path)
        if parsed is None:
            self._send(404, "text/plain; charset=utf-8", "missing")
            return None
        return parsed

    def do_GET(self):
        parsed = self._route()
        if parsed is None:
            return
        room, tail = parsed
        if tail == "":
            self.send_response(302)
            self.send_header("Location", "/r/%s/" % room)
            self.send_header("Content-Length", "0")
            self.send_header("Connection", "close")
            self.end_headers()
            return
        if tail == "/":
            self._send(200, "text/html; charset=utf-8", PAGE)
            return
        if tail == "/list":
            self._send(200, "application/json; charset=utf-8", json.dumps(list_json(room)))
            return
        if tail.startswith("/frame/"):
            sid = clean_token(tail[len("/frame/"):])
            jpeg = None
            with _lock:
                slots = _rooms.get(room) or {}
                slot = slots.get(sid)
                if slot and (time.time() - slot["seen"]) <= TTL_SEC:
                    jpeg = slot["jpeg"]
            if jpeg is None:
                self._send(404, "text/plain", b"missing")
            else:
                self._send(200, "image/jpeg", jpeg)
            return
        self._send(404, "text/plain", b"missing")

    def do_POST(self):
        parsed = self._route()
        if parsed is None:
            return
        room, tail = parsed
        length = int(self.headers.get("Content-Length", "0") or "0")
        if tail != "/frame" or length <= 0 or length > MAX_FRAME:
            self._send(400, "text/plain", b"bad")
            if 0 < length <= MAX_FRAME:
                self.rfile.read(length)
            return
        body = self.rfile.read(length)
        sid = clean_token(self.headers.get("X-Hv-Id", ""))
        name = self.headers.get("X-Hv-Name", "") or sid
        if not sid or not body:
            self._send(400, "text/plain", b"bad")
            return
        with _lock:
            slots = _rooms.setdefault(room, {})
            slots[sid] = {"name": name, "jpeg": body, "seen": time.time()}
        self._send(204, "text/plain", b"")


def main():
    parser = argparse.ArgumentParser(description="HvScreen web relay")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=19723)
    args = parser.parse_args()
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print("hvrelay http://%s:%d/r/<room>/" % (args.host, args.port))
    server.serve_forever()


if __name__ == "__main__":
    main()
