#!/usr/bin/env python3
"""EPOLISI assist — собственная программа удалённого доступа к компьютерам агентов.

Сервер (этот файл) делает три вещи:
  * книга адресов (как раньше);
  * релей WebSocket: программа агента (EPOLISI-assist.exe) держит соединение с сервером,
    оператор из браузера подключается к агенту по ID и паролю, сервер пересылает
    кадры экрана агенту→оператору и мышь/клавиатуру оператор→агенту;
  * раздача EPOLISI-assist.exe для установки агентам.
Без внешних зависимостей: только стандартная библиотека Python 3.
"""
import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import socket
import sqlite3
import struct
import threading
import time
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

BASE = os.path.dirname(os.path.abspath(__file__))
DB_PATH = os.path.join(BASE, "assist.db")
CFG_PATH = os.path.join(BASE, "config.json")
AGENT_EXE = os.path.join(BASE, "EPOLISI-assist.exe")
SESSION_TTL = 7 * 24 * 3600
MAX_FAILS, FAIL_WINDOW = 5, 15 * 60  # не больше 5 неверных паролей за 15 минут с одного IP
FAILS = {}
FIELDS = ("name", "program", "remote_id", "password", "phone", "grp", "note")
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
MAX_MSG = 16 * 1024 * 1024
VIEWER_TYPES = {"m", "b", "w", "k", "t", "q", "ack", "clip"}  # команды оператора агенту; плюс всё, что с "f" — файлы


PBKDF2_ITERATIONS = 200_000


def hash_pw(password, salt=None):
    salt = salt or secrets.token_hex(16)
    dk = hashlib.pbkdf2_hmac("sha256", password.encode(), bytes.fromhex(salt), PBKDF2_ITERATIONS)
    return f"pbkdf2${PBKDF2_ITERATIONS}${salt}${dk.hex()}"


def verify_pw(password, stored):
    try:
        algo, iters, salt, want = stored.split("$")
        if algo != "pbkdf2":
            return False
        dk = hashlib.pbkdf2_hmac("sha256", password.encode(), bytes.fromhex(salt), int(iters))
        return hmac.compare_digest(dk.hex(), want)
    except (ValueError, AttributeError):
        return False


def load_config():
    changed = False
    if not os.path.exists(CFG_PATH):
        pw = secrets.token_urlsafe(9)
        cfg = {"host": "0.0.0.0", "port": 8095, "users": {"owner": hash_pw(pw)}}
        changed = True
        print(f"Создан config.json. Пароль входа owner: {pw}")
    else:
        with open(CFG_PATH, encoding="utf-8") as f:
            cfg = json.load(f)
        if "users" not in cfg:
            # старый формат с одним общим паролем — переносим как учётку "owner"
            cfg["users"] = {"owner": cfg.pop("password", secrets.token_urlsafe(9))}
            changed = True
    if "cookie_secret" not in cfg:
        cfg["cookie_secret"] = secrets.token_hex(32)
        changed = True
    # пароли в открытом виде (старый формат или только что перенесённые) — хешируем
    for name, pw in list(cfg["users"].items()):
        if not pw.startswith("pbkdf2$"):
            cfg["users"][name] = hash_pw(pw)
            changed = True
    if changed:
        with open(CFG_PATH, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
        os.chmod(CFG_PATH, 0o600)
        print("config.json обновлён. Учётки входа:", ", ".join(cfg["users"]))
    return cfg


def save_config():
    with open(CFG_PATH, "w", encoding="utf-8") as f:
        json.dump(CFG, f, ensure_ascii=False, indent=2)
    os.chmod(CFG_PATH, 0o600)


CFG = load_config()
SECRET = hashlib.sha256(("epolisi-cookie:" + CFG["cookie_secret"]).encode()).digest()


def db():
    con = sqlite3.connect(DB_PATH)
    con.row_factory = sqlite3.Row
    return con


def visible_host_ids(con, user):
    """ID записей книги, которые видит этот помощник: свои + отдельные доступы + доступы по группе (филиалу)."""
    rows = con.execute(
        "SELECT id FROM hosts WHERE created_by=? "
        "UNION SELECT CAST(target AS INTEGER) FROM grants WHERE username=? AND kind='host' "
        "UNION SELECT h.id FROM hosts h JOIN grants g ON g.kind='group' AND g.target=h.grp "
        "WHERE g.username=? AND h.grp<>''", (user, user, user)).fetchall()
    return {r[0] for r in rows}


def visible_agent_ids(con, user):
    """ID агентов EPOLISI assist (только цифры), которые видит этот помощник."""
    ids = visible_host_ids(con, user)
    if not ids:
        return set()
    placeholders = ",".join("?" * len(ids))
    rows = con.execute(f"SELECT remote_id FROM hosts WHERE id IN ({placeholders})", tuple(ids)).fetchall()
    return {re.sub(r"\D", "", r[0]) for r in rows if r[0]}


def init_db():
    with db() as con:
        con.execute("""CREATE TABLE IF NOT EXISTS hosts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            program TEXT NOT NULL DEFAULT 'AnyDesk',
            remote_id TEXT NOT NULL DEFAULT '',
            password TEXT NOT NULL DEFAULT '',
            phone TEXT NOT NULL DEFAULT '',
            grp TEXT NOT NULL DEFAULT '',
            note TEXT NOT NULL DEFAULT '',
            updated_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
        )""")
        if "created_by" not in [r["name"] for r in con.execute("PRAGMA table_info(hosts)")]:
            con.execute("ALTER TABLE hosts ADD COLUMN created_by TEXT NOT NULL DEFAULT 'owner'")
        # ID агента закрепляется за его секретным ключом при первом подключении
        con.execute("""CREATE TABLE IF NOT EXISTS agents (
            id TEXT PRIMARY KEY,
            key_hash TEXT NOT NULL,
            name TEXT NOT NULL DEFAULT '',
            last_seen TEXT NOT NULL DEFAULT ''
        )""")
        # кому из помощников явно открыт доступ к записи книги (помимо владельца и автора записи)
        con.execute("""CREATE TABLE IF NOT EXISTS grants (
            username TEXT NOT NULL,
            kind TEXT NOT NULL,   -- 'host' (одна запись) или 'group' (весь филиал)
            target TEXT NOT NULL, -- id записи или название группы
            PRIMARY KEY (username, kind, target)
        )""")
        if "kind" not in [r["name"] for r in con.execute("PRAGMA table_info(grants)")]:
            # старый формат grants(username, host_id) — переносим как доступ к отдельным записям
            con.execute("ALTER TABLE grants RENAME TO grants_old")
            con.execute("""CREATE TABLE grants (
                username TEXT NOT NULL, kind TEXT NOT NULL, target TEXT NOT NULL,
                PRIMARY KEY (username, kind, target))""")
            con.execute("INSERT INTO grants (username, kind, target) "
                        "SELECT username, 'host', CAST(host_id AS TEXT) FROM grants_old")
            con.execute("DROP TABLE grants_old")
        # журнал подключений к чужим компьютерам — хранится 1 год
        con.execute("""CREATE TABLE IF NOT EXISTS logs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            start_ts TEXT NOT NULL DEFAULT (datetime('now','localtime')),
            end_ts TEXT,
            username TEXT NOT NULL,
            agent_id TEXT NOT NULL,
            agent_name TEXT NOT NULL DEFAULT '',
            ip TEXT NOT NULL DEFAULT '',
            inputs INTEGER NOT NULL DEFAULT 0
        )""")
        if "inputs" not in [r["name"] for r in con.execute("PRAGMA table_info(logs)")]:
            con.execute("ALTER TABLE logs ADD COLUMN inputs INTEGER NOT NULL DEFAULT 0")


USER_RE = re.compile(r"^[a-z0-9_]{2,32}$")


def make_token(user):
    exp = str(int(time.time()) + SESSION_TTL)
    payload = f"{user}.{exp}"
    sig = hmac.new(SECRET, payload.encode(), "sha256").hexdigest()
    return f"{payload}.{sig}"


def check_token(tok):
    """Возвращает имя вошедшего пользователя или None."""
    try:
        user, exp, sig = tok.split(".", 2)
        good = hmac.new(SECRET, f"{user}.{exp}".encode(), "sha256").hexdigest()
        if hmac.compare_digest(sig, good) and int(exp) > time.time():
            return user
        return None
    except (ValueError, AttributeError):
        return None


def is_owner(user):
    return user == "owner"


def too_many_fails(key):
    now = time.time()
    FAILS[key] = [t for t in FAILS.get(key, []) if now - t < FAIL_WINDOW]
    return len(FAILS[key]) >= MAX_FAILS


def add_fail(key):
    FAILS.setdefault(key, []).append(time.time())


LOG_RETENTION_DAYS = 366


def log_session_start(user, agent_id, agent_name, ip):
    with db() as con:
        con.execute("DELETE FROM logs WHERE start_ts < datetime('now','-%d days')" % LOG_RETENTION_DAYS)
        cur = con.execute("INSERT INTO logs (username, agent_id, agent_name, ip) VALUES (?, ?, ?, ?)",
                           (user, agent_id, agent_name, ip))
        return cur.lastrowid


def log_session_end(log_id, inputs=0):
    try:
        with db() as con:
            con.execute("UPDATE logs SET end_ts=datetime('now','localtime'), inputs=? WHERE id=?", (inputs, log_id))
    except sqlite3.Error:
        pass


class WebSocket:
    """Минимальный WebSocket (RFC 6455) поверх соединения BaseHTTPRequestHandler."""

    def __init__(self, handler):
        self.rfile, self.wfile, self.sock = handler.rfile, handler.wfile, handler.connection
        self.lock = threading.Lock()

    def _read(self, n):
        buf = self.rfile.read(n)
        if len(buf) < n:
            raise ConnectionError("closed")
        return buf

    def recv(self):
        """Следующее сообщение: (is_text, bytes) или None, если соединение закрыто."""
        parts, kind = [], None
        while True:
            b0, b1 = self._read(2)
            op, n = b0 & 0x0F, b1 & 0x7F
            if n == 126:
                n = struct.unpack(">H", self._read(2))[0]
            elif n == 127:
                n = struct.unpack(">Q", self._read(8))[0]
            if n > MAX_MSG:
                raise ConnectionError("message too big")
            mask = self._read(4) if b1 & 0x80 else None
            data = self._read(n) if n else b""
            if mask and n:
                key = (mask * (n // 4 + 1))[:n]
                data = (int.from_bytes(data, "big") ^ int.from_bytes(key, "big")).to_bytes(n, "big")
            if op == 8:
                return None
            if op == 9:
                self._send(10, data)
                continue
            if op == 10:
                continue
            if op in (1, 2):
                kind = op
            parts.append(data)
            if b0 & 0x80:
                return kind == 1, b"".join(parts)

    def _send(self, op, data):
        n = len(data)
        if n < 126:
            head = struct.pack(">BB", 0x80 | op, n)
        elif n < 65536:
            head = struct.pack(">BBH", 0x80 | op, 126, n)
        else:
            head = struct.pack(">BBQ", 0x80 | op, 127, n)
        with self.lock:
            self.wfile.write(head)
            self.wfile.write(data)

    def send_text(self, obj):
        self._send(1, json.dumps(obj, ensure_ascii=False).encode())

    def send_raw_text(self, data):
        self._send(1, data)

    def send_bin(self, data):
        self._send(2, data)

    def close(self):
        try:
            self._send(8, b"")
        except OSError:
            pass
        try:
            self.sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass


class Agent:
    def __init__(self, ws, aid, name, pw, akey):
        self.ws, self.id, self.name, self.pw, self.akey = ws, aid, name, pw, akey
        self.viewers = []  # несколько человек могут одновременно смотреть
        self.controller = None  # но управляет только один
        self.lock = threading.Lock()


def broadcast_role(agent):
    """Сообщить всем зрителям агента, кто сейчас управляет и сколько человек смотрит."""
    with agent.lock:
        vs, ctrl = list(agent.viewers), agent.controller
    n = len(vs)
    for v in vs:
        safe(v.send_text, {"type": "role", "controller": v is ctrl, "viewers": n})


AGENTS = {}
AGENTS_LOCK = threading.Lock()


def safe(fn, *args):
    try:
        fn(*args)
    except OSError:
        pass


class Handler(BaseHTTPRequestHandler):
    server_version = "EPOLISI-assist"
    protocol_version = "HTTP/1.1"  # нужен для ответа 101 Switching Protocols

    def log_message(self, fmt, *args):
        pass

    def client_ip(self):
        # за Cloudflare Tunnel (он подключается с localhost) реальный адрес приходит в заголовке
        ip = self.client_address[0]
        if ip in ("127.0.0.1", "::1"):
            return self.headers.get("CF-Connecting-IP") or ip
        return ip

    def is_https(self):
        return self.headers.get("X-Forwarded-Proto") == "https" or '"https"' in self.headers.get("CF-Visitor", "")

    def authed(self):
        """Имя вошедшего пользователя или None."""
        c = SimpleCookie(self.headers.get("Cookie", ""))
        return check_token(c["sess"].value) if "sess" in c else None

    def send(self, code, body, ctype="application/json; charset=utf-8", headers=None):
        data = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(data)

    def json(self, obj, code=200):
        self.send(code, json.dumps(obj, ensure_ascii=False))

    def read_body(self):
        n = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(n) if n else b"{}"
        try:
            return json.loads(raw)
        except json.JSONDecodeError:
            return {}

    # ---------- WebSocket ----------

    def ws_upgrade(self):
        key = self.headers.get("Sec-WebSocket-Key")
        if not key or "websocket" not in self.headers.get("Upgrade", "").lower():
            self.json({"error": "websocket expected"}, 400)
            return None
        accept = base64.b64encode(hashlib.sha1((key + WS_GUID).encode()).digest()).decode()
        self.send_response(101)
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", accept)
        self.end_headers()
        self.close_connection = True
        self.connection.settimeout(90)  # агент и оператор шлют ping чаще
        return WebSocket(self)

    def ws_agent(self):
        ws = self.ws_upgrade()
        if not ws:
            return
        agent = None
        try:
            first = ws.recv()
            hello = json.loads(first[1]) if first and first[0] else {}
            aid = re.sub(r"\D", "", str(hello.get("id", "")))
            key, pw, akey = str(hello.get("key", "")), str(hello.get("pw", "")), str(hello.get("akey", ""))
            if len(aid) != 9 or len(key) < 16 or len(pw) < 6:
                return ws.send_text({"type": "error", "code": "bad_hello"})
            key_hash = hashlib.sha256(key.encode()).hexdigest()
            name = str(hello.get("name", ""))[:100]
            with db() as con:
                row = con.execute("SELECT key_hash FROM agents WHERE id=?", (aid,)).fetchone()
                if row and not hmac.compare_digest(row["key_hash"], key_hash):
                    return ws.send_text({"type": "error", "code": "id_taken"})
                con.execute("INSERT INTO agents (id, key_hash, name, last_seen) "
                            "VALUES (?, ?, ?, datetime('now','localtime')) ON CONFLICT(id) DO UPDATE "
                            "SET name=excluded.name, last_seen=excluded.last_seen", (aid, key_hash, name))
            agent = Agent(ws, aid, name, pw, akey)
            with AGENTS_LOCK:
                old = AGENTS.get(aid)
                AGENTS[aid] = agent
            if old:
                old.ws.close()
            ws.send_text({"type": "ok"})
            while True:
                msg = ws.recv()
                if msg is None:
                    break
                is_text, data = msg
                with agent.lock:
                    viewers, controller = list(agent.viewers), agent.controller
                if not is_text:
                    for v in viewers:
                        safe(v.send_bin, data)
                    continue
                m = json.loads(data)
                t = m.get("type")
                if t == "pw":
                    agent.pw = str(m.get("pw", ""))
                elif t == "akey":
                    agent.akey = str(m.get("akey", ""))
                elif controller and (t == "info" or t == "clip" or (t and t.startswith("f"))):
                    safe(controller.send_raw_text, data)
                elif t == "end" and viewers:
                    for v in viewers:
                        safe(v.send_text, {"type": "error", "code": "ended", "msg": "Агент завершил сеанс"})
                        v.close()
        except (ConnectionError, OSError, ValueError, AttributeError):
            pass
        finally:
            if agent:
                with AGENTS_LOCK:
                    if AGENTS.get(agent.id) is agent:
                        del AGENTS[agent.id]
                with agent.lock:
                    viewers = list(agent.viewers)
                for v in viewers:
                    safe(v.send_text, {"type": "error", "code": "offline", "msg": "Агент отключился"})
                    v.close()
            ws.close()

    def ws_view(self, aid):
        origin = urlparse(self.headers.get("Origin", "")).netloc
        user = self.authed()
        if not user or origin != self.headers.get("Host"):
            return self.json({"error": "auth"}, 401)
        ws = self.ws_upgrade()
        if not ws:
            return
        agent, ip, log_id, input_count = None, self.client_ip(), None, 0
        try:
            first = ws.recv()
            auth = json.loads(first[1]) if first and first[0] else {}
            agent = AGENTS.get(aid)
            if not agent:
                return ws.send_text({"type": "error", "code": "offline", "msg": "Агент сейчас не в сети"})
            if too_many_fails("view:" + ip):
                agent = None
                return ws.send_text({"type": "error", "code": "locked",
                                     "msg": "Слишком много неверных паролей, подождите 15 минут"})
            pw_try = str(auth.get("pw", ""))
            ok = hmac.compare_digest(pw_try, agent.pw) or (
                len(agent.akey) >= 16 and hmac.compare_digest(pw_try, agent.akey))
            if not ok:
                add_fail("view:" + ip)
                agent = None
                return ws.send_text({"type": "error", "code": "bad_pw", "msg": "Неверный пароль агента"})
            is_controller = False
            with agent.lock:
                agent.viewers.append(ws)
                first_viewer = len(agent.viewers) == 1
                if agent.controller is None:
                    agent.controller = ws
                    is_controller = True
            if first_viewer:
                safe(agent.ws.send_text, {"type": "start", "ip": ip})
            ws.send_text({"type": "connected", "name": agent.name, "controller": is_controller, "user": user})
            broadcast_role(agent)
            log_id = log_session_start(user, aid, agent.name, ip)
            while True:
                msg = ws.recv()
                if msg is None:
                    break
                if not msg[0]:
                    continue
                m = json.loads(msg[1])
                mt = m.get("type") if isinstance(m, dict) else None
                if mt == "control":
                    with agent.lock:
                        agent.controller = ws
                    is_controller = True
                    broadcast_role(agent)
                    continue
                my_turn = agent.controller is ws
                if mt == "ack":
                    agent.ws.send_raw_text(msg[1])  # поток кадров общий на всех — подтверждает любой зритель
                    continue
                if mt in ("m", "b", "w", "k", "t"):
                    if not my_turn:
                        continue  # управляет не он — команда не уходит агенту
                    input_count += 1
                if my_turn and (mt in VIEWER_TYPES or (mt and mt.startswith("f"))):
                    agent.ws.send_raw_text(msg[1])
        except (ConnectionError, OSError, ValueError, AttributeError):
            pass
        finally:
            if log_id:
                log_session_end(log_id, input_count)
            if agent:
                last_viewer = False
                with agent.lock:
                    if ws in agent.viewers:
                        agent.viewers.remove(ws)
                    if agent.controller is ws:
                        agent.controller = agent.viewers[0] if agent.viewers else None
                    last_viewer = not agent.viewers
                if last_viewer:
                    safe(agent.ws.send_text, {"type": "stop"})
                else:
                    broadcast_role(agent)
            ws.close()

    # ---------- HTTP ----------

    def do_GET(self):
        url = urlparse(self.path)
        if url.path == "/":
            return self.send(200, PAGE, "text/html; charset=utf-8")
        if url.path == "/view":
            return self.send(200, VIEW_PAGE, "text/html; charset=utf-8")
        if url.path == "/download":
            if not os.path.exists(AGENT_EXE):
                return self.json({"error": "Файл ещё не загружен"}, 404)
            with open(AGENT_EXE, "rb") as f:
                return self.send(200, f.read(), "application/octet-stream",
                                 {"Content-Disposition": 'attachment; filename="EPOLISI-assist.exe"'})
        if url.path == "/ws/agent":
            return self.ws_agent()
        if url.path == "/ws/view":
            return self.ws_view(re.sub(r"\D", "", parse_qs(url.query).get("id", [""])[0]))
        user = self.authed()
        if not user:
            return self.json({"error": "auth"}, 401)
        owner = is_owner(user)
        if url.path == "/api/me":
            return self.json({"user": user, "owner": owner})
        if url.path == "/api/hosts":
            q = parse_qs(url.query).get("q", [""])[0].strip()
            with db() as con:
                visible_ids = None if owner else visible_host_ids(con, user)
                if q:
                    like = f"%{q}%"
                    rows = con.execute(
                        "SELECT * FROM hosts WHERE (name LIKE ? OR remote_id LIKE ? OR phone LIKE ? "
                        "OR grp LIKE ? OR note LIKE ?) ORDER BY grp, name COLLATE NOCASE",
                        (like,) * 5).fetchall()
                else:
                    rows = con.execute("SELECT * FROM hosts ORDER BY grp, name COLLATE NOCASE").fetchall()
            rows = [dict(r) for r in rows if owner or r["id"] in visible_ids]
            for r in rows:
                r["mine"] = owner or r["created_by"] == user
            return self.json(rows)
        if url.path == "/api/online":
            with db() as con:
                visible = None if owner else visible_agent_ids(con, user)
            with AGENTS_LOCK:
                online = [{"id": a.id, "name": a.name, "busy": len(a.viewers) > 0, "viewers": len(a.viewers)}
                          for a in AGENTS.values() if owner or a.id in visible]
            return self.json(sorted(online, key=lambda a: a["name"].lower()))
        if url.path == "/api/akey":
            # ключ для входа «в один клик»; требует права видеть этот компьютер (кроме владельца)
            aid = re.sub(r"\D", "", parse_qs(url.query).get("id", [""])[0])
            if not owner:
                with db() as con:
                    if aid not in visible_agent_ids(con, user):
                        return self.json({"error": "Нет доступа к этому компьютеру"}, 403)
            with AGENTS_LOCK:
                a = AGENTS.get(aid)
            if not a:
                return self.json({"error": "Агент не в сети"}, 404)
            if len(a.akey) < 16:
                return self.json({"error": "Агент старой версии, обновите программу у агента"}, 409)
            return self.json({"akey": a.akey})
        if not owner:
            return self.json({"error": "Доступно только владельцу"}, 403)
        if url.path == "/api/export":
            with db() as con:
                rows = [dict(r) for r in con.execute("SELECT * FROM hosts ORDER BY id")]
            fname = time.strftime("epolisi-assist-%Y%m%d.json")
            return self.send(200, json.dumps(rows, ensure_ascii=False, indent=2), "application/json; charset=utf-8",
                             {"Content-Disposition": f'attachment; filename="{fname}"'})
        if url.path == "/api/users":
            return self.json(sorted(u for u in CFG["users"] if u != "owner"))
        if url.path == "/api/groups":
            with db() as con:
                rows = con.execute(
                    "SELECT DISTINCT grp FROM hosts WHERE grp<>'' ORDER BY grp COLLATE NOCASE").fetchall()
            return self.json([r["grp"] for r in rows])
        if url.path == "/api/grants":
            with db() as con:
                rows = con.execute("SELECT kind, target, username FROM grants").fetchall()
            out = {"host": {}, "group": {}}
            for r in rows:
                out[r["kind"]].setdefault(r["target"], []).append(r["username"])
            return self.json(out)
        if url.path == "/api/logs":
            q = parse_qs(url.query).get("q", [""])[0].strip()
            with db() as con:
                if q:
                    like = f"%{q}%"
                    rows = con.execute(
                        "SELECT * FROM logs WHERE username LIKE ? OR agent_name LIKE ? OR agent_id LIKE ? "
                        "ORDER BY id DESC LIMIT 1000", (like, like, like)).fetchall()
                else:
                    rows = con.execute("SELECT * FROM logs ORDER BY id DESC LIMIT 1000").fetchall()
            return self.json([dict(r) for r in rows])
        self.json({"error": "not found"}, 404)

    def do_POST(self):
        path = urlparse(self.path).path
        body = self.read_body()  # читаем всегда, чтобы не сломать keep-alive соединение
        if path == "/api/login":
            ip = self.client_ip()
            if too_many_fails(ip):
                return self.json({"error": "Слишком много попыток, подождите 15 минут"}, 429)
            user = str(body.get("user", "")).strip().lower()
            pw_try = str(body.get("password", ""))
            real = CFG["users"].get(user, "")
            # сверяем всегда (даже для неизвестного логина), чтобы не выдавать время ответом
            ok = len(real) > 0 and verify_pw(pw_try, real)
            if ok:
                FAILS.pop(ip, None)
                secure = "; Secure" if self.is_https() else ""
                return self.send(200, "{}", headers={
                    "Set-Cookie": f"sess={make_token(user)}; HttpOnly; SameSite=Strict; Path=/; Max-Age={SESSION_TTL}{secure}"})
            add_fail(ip)
            time.sleep(1)
            return self.json({"error": "Неверный логин или пароль"}, 403)
        if path == "/api/logout":
            return self.send(200, "{}", headers={"Set-Cookie": "sess=; Path=/; Max-Age=0"})
        user = self.authed()
        if not user:
            return self.json({"error": "auth"}, 401)
        owner = is_owner(user)
        if path == "/api/hosts":
            vals = [str(body.get(f, "")).strip() for f in FIELDS]
            if not vals[0]:
                return self.json({"error": "Укажите название"}, 400)
            with db() as con:
                if body.get("id"):
                    hid = int(body["id"])
                    row = con.execute("SELECT created_by FROM hosts WHERE id=?", (hid,)).fetchone()
                    if not row or (not owner and row["created_by"] != user):
                        return self.json({"error": "Нет доступа к этой записи"}, 403)
                    con.execute(f"UPDATE hosts SET {', '.join(f + '=?' for f in FIELDS)}, "
                                "updated_at=datetime('now','localtime') WHERE id=?", vals + [hid])
                else:
                    con.execute(f"INSERT INTO hosts ({', '.join(FIELDS)}, created_by) VALUES "
                                f"({', '.join('?' * len(FIELDS))}, ?)", vals + [user])
            return self.json({"ok": True})
        if path == "/api/import":
            if not owner:
                return self.json({"error": "Доступно только владельцу"}, 403)
            if not isinstance(body, list):
                return self.json({"error": "Ожидается JSON-массив"}, 400)
            with db() as con:
                for r in body:
                    if isinstance(r, dict) and str(r.get("name", "")).strip():
                        con.execute(f"INSERT INTO hosts ({', '.join(FIELDS)}, created_by) VALUES "
                                    f"({', '.join('?' * len(FIELDS))}, 'owner')",
                                    [str(r.get(f, "")).strip() for f in FIELDS])
            return self.json({"ok": True})
        if not owner:
            return self.json({"error": "Доступно только владельцу"}, 403)
        if path == "/api/users":
            name = str(body.get("user", "")).strip().lower()
            pw = str(body.get("password", ""))
            if not USER_RE.match(name) or name == "owner":
                return self.json({"error": "Логин: латиница/цифры/подчёркивание, 2-32 символа, не 'owner'"}, 400)
            if len(pw) < 6:
                return self.json({"error": "Пароль — не меньше 6 символов"}, 400)
            CFG["users"][name] = hash_pw(pw)
            save_config()
            return self.json({"ok": True})
        if path == "/api/grants":
            kind = str(body.get("kind", "host"))
            target = str(body.get("target", ""))
            uname = str(body.get("user", ""))
            grant = bool(body.get("grant"))
            if kind not in ("host", "group") or not target or uname not in CFG["users"]:
                return self.json({"error": "bad request"}, 400)
            with db() as con:
                if grant:
                    con.execute("INSERT OR IGNORE INTO grants (username, kind, target) VALUES (?, ?, ?)",
                                (uname, kind, target))
                else:
                    con.execute("DELETE FROM grants WHERE username=? AND kind=? AND target=?",
                                (uname, kind, target))
            return self.json({"ok": True})
        self.json({"error": "not found"}, 404)

    def do_DELETE(self):
        path = urlparse(self.path).path
        user = self.authed()
        if not user:
            return self.json({"error": "auth"}, 401)
        owner = is_owner(user)
        if path.startswith("/api/hosts/"):
            hid = int(path.rsplit("/", 1)[1])
            with db() as con:
                row = con.execute("SELECT created_by FROM hosts WHERE id=?", (hid,)).fetchone()
                if not row or (not owner and row["created_by"] != user):
                    return self.json({"error": "Нет доступа к этой записи"}, 403)
                con.execute("DELETE FROM hosts WHERE id=?", (hid,))
                con.execute("DELETE FROM grants WHERE kind='host' AND target=?", (str(hid),))
            return self.json({"ok": True})
        if path.startswith("/api/users/"):
            if not owner:
                return self.json({"error": "Доступно только владельцу"}, 403)
            name = path.rsplit("/", 1)[1]
            if name == "owner":
                return self.json({"error": "Нельзя удалить владельца"}, 400)
            CFG["users"].pop(name, None)
            save_config()
            with db() as con:
                con.execute("DELETE FROM grants WHERE username=?", (name,))
            return self.json({"ok": True})
        self.json({"error": "not found"}, 404)


STYLE = r"""
:root{--bg:#f4f6fa;--card:#fff;--text:#1c2433;--muted:#6b7588;--line:#e2e6ee;--acc:#1f6feb;--acc2:#e8f0fe;--red:#d1242f;--green:#1a7f37}
@media (prefers-color-scheme:dark){:root{--bg:#10141b;--card:#181e28;--text:#e6eaf2;--muted:#8d97a8;--line:#2a3240;--acc:#4c8dff;--acc2:#1b2a44;--red:#ff6b6b;--green:#3fb950}}
*{box-sizing:border-box}body{margin:0;font:15px/1.4 system-ui,Segoe UI,Roboto,sans-serif;background:var(--bg);color:var(--text)}
input,select,textarea{font:inherit;color:var(--text);background:var(--bg);border:1px solid var(--line);border-radius:8px;padding:8px 10px;width:100%}
textarea{min-height:60px;resize:vertical}
button,.btn{font:inherit;border:1px solid var(--line);background:var(--card);color:var(--text);border-radius:8px;padding:7px 12px;cursor:pointer;white-space:nowrap;text-decoration:none;display:inline-block}
button:hover,.btn:hover{border-color:var(--acc)}button.pri,.btn.pri{background:var(--acc);border-color:var(--acc);color:#fff}button.del{color:var(--red)}
dialog{border:1px solid var(--line);border-radius:12px;background:var(--card);color:var(--text);width:min(520px,94vw);padding:20px}
dialog::backdrop{background:#0007}
#toast{position:fixed;bottom:20px;left:50%;transform:translateX(-50%);background:var(--text);color:var(--bg);padding:8px 16px;border-radius:8px;opacity:0;transition:.2s;pointer-events:none;z-index:9}
#toast.on{opacity:1}
"""

PAGE = r"""<!doctype html>
<html lang="ru"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>EPOLISI assist</title>
<style>""" + STYLE + r"""
header{display:flex;gap:10px;align-items:center;padding:14px 20px;background:var(--card);border-bottom:1px solid var(--line);flex-wrap:wrap}
header h1{font-size:18px;margin:0 12px 0 0;white-space:nowrap}header h1 b,#login h1 b{color:var(--acc)}
#q{flex:1;min-width:180px;max-width:360px}.sp{flex:1}
#qc{display:flex;gap:6px}#qc input{width:150px}
main{padding:16px 20px;max-width:1300px;margin:0 auto}
table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line);border-radius:10px;overflow:hidden}
th,td{text-align:left;padding:9px 10px;border-bottom:1px solid var(--line);vertical-align:middle}
th{font-size:12px;text-transform:uppercase;color:var(--muted);letter-spacing:.04em}
tr.g td{background:var(--acc2);font-weight:600;font-size:13px}
.id{font-family:Consolas,monospace;font-size:15px;cursor:pointer}.id:hover{color:var(--acc)}
.tag{display:inline-block;padding:2px 8px;border-radius:20px;font-size:12px;background:var(--acc2);color:var(--acc)}
.dot{display:inline-block;width:9px;height:9px;border-radius:50%;background:var(--red);margin-right:6px;vertical-align:middle}
.dot.on{background:var(--green);animation:pulse 1.3s infinite}
@keyframes pulse{0%,100%{box-shadow:0 0 0 0 rgba(40,180,90,.55)}50%{box-shadow:0 0 0 5px rgba(40,180,90,0)}}
.stat{font-size:12px;font-weight:600}.stat.on{color:var(--green)}.stat.off{color:var(--red)}
.muted{color:var(--muted)}.acts{display:flex;gap:6px;justify-content:flex-end}
.pw{font-family:Consolas,monospace;cursor:pointer}
.f{display:grid;grid-template-columns:1fr 1fr;gap:10px}.f label{font-size:12px;color:var(--muted);display:block;margin-bottom:3px}.f .w{grid-column:1/3}
#login{max-width:360px;margin:12vh auto;background:var(--card);border:1px solid var(--line);border-radius:12px;padding:28px}
#online{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:14px}
.chip{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:8px 10px;display:flex;gap:10px;align-items:center}
.chip small{color:var(--muted);font-family:Consolas,monospace}
h2{font-size:14px;color:var(--muted);text-transform:uppercase;letter-spacing:.04em;margin:4px 0 8px}
@media(max-width:760px){.hm{display:none}th,td{padding:8px 6px}}
</style></head><body>
<div id="login" hidden><h1 style="margin-top:0">EPOLISI <b>assist</b></h1>
<form onsubmit="login(event)"><input id="lu" placeholder="Логин" autocomplete="username" autofocus style="margin-bottom:8px">
<input id="lp" type="password" placeholder="Пароль" autocomplete="current-password">
<p id="le" style="color:var(--red);min-height:1em"></p><button class="pri" style="width:100%">Войти</button></form>
<p style="margin-bottom:0;text-align:center"><a class="muted" href="/download">Скачать программу для агента</a></p></div>
<div id="app" hidden>
<header><h1>EPOLISI <b>assist</b></h1><span id="whoami" class="muted" style="font-size:12px"></span>
<form id="qc" onsubmit="quick(event)"><input id="qid" placeholder="ID агента" inputmode="numeric"><button class="pri">Подключиться</button></form>
<input id="q" placeholder="Поиск: название, ID, телефон, группа…" oninput="load()">
<span class="sp"></span><button onclick="edit()">+ Добавить</button>
<a class="btn" href="/download" title="Программа, которую ставят на компьютер агента">Скачать для агента</a>
<button id="btnUsers" hidden onclick="loadUsers();usersDlg.showModal()">Помощники</button>
<button id="btnGroups" hidden onclick="openGroups()">Филиалы</button>
<button id="btnLogs" hidden onclick="loadLogs();logsDlg.showModal()">Журнал</button>
<button id="btnExport" hidden onclick="location='/api/export'" title="Скачать резервную копию">Экспорт</button>
<button id="btnImport" hidden onclick="imp.click()">Импорт</button><input id="imp" type="file" accept=".json" hidden onchange="doImport(this)">
<button onclick="logout()">Выход</button></header>
<main><div id="onl" hidden><h2>Сейчас в сети</h2><div id="online"></div></div>
<table><thead><tr><th>Название</th><th>Программа</th><th>ID / адрес</th><th class="hm">Пароль</th><th class="hm">Телефон</th><th class="hm">Заметка</th><th></th></tr></thead>
<tbody id="tb"></tbody></table><p id="empty" class="muted" hidden>Записей нет. Нажмите «+ Добавить».</p></main></div>
<dialog id="dlg"><form method="dialog" onsubmit="save(event)"><h3 id="dt" style="margin-top:0"></h3><div class="f">
<div class="w"><label>Название (агент / точка / компьютер)*</label><input id="f_name" required></div>
<div><label>Программа</label><select id="f_program"><option>EPOLISI assist</option><option>AnyDesk</option><option>Ammyy Admin</option><option>Supremo</option><option>RustDesk</option><option>TeamViewer</option><option>RDP</option><option>Другое</option></select></div>
<div><label>ID / адрес</label><input id="f_remote_id"></div>
<div><label>Пароль</label><input id="f_password"></div>
<div><label>Телефон</label><input id="f_phone"></div>
<div class="w"><label>Группа (филиал, город…)</label><input id="f_grp" list="grps"><datalist id="grps"></datalist></div>
<div class="w"><label>Заметка</label><textarea id="f_note"></textarea></div></div>
<p style="display:flex;gap:8px;justify-content:flex-end;margin-bottom:0"><button type="button" onclick="dlg.close()">Отмена</button><button class="pri">Сохранить</button></p></form></dialog>
<dialog id="usersDlg"><h3 style="margin-top:0">Помощники</h3>
<p class="muted" style="margin-top:0">У каждого свой логин и пароль. Видят только те компьютеры, которые добавили сами, или те, куда вы дали доступ (кнопка «Доступ» у записи).</p>
<div id="usersList"></div>
<form onsubmit="addUser(event)" style="margin-top:12px;display:flex;gap:6px">
<input id="nu_name" placeholder="Логин (латиницей)"><input id="nu_pass" placeholder="Пароль (от 6 символов)">
<button class="pri" style="white-space:nowrap">Добавить</button></form>
<p style="text-align:right;margin-bottom:0;margin-top:14px"><button type="button" onclick="usersDlg.close()">Закрыть</button></p></dialog>
<dialog id="grantsDlg"><h3 id="gd_title" style="margin-top:0">Доступ</h3>
<div id="gd_group"></div><div id="gd_list"></div>
<p style="text-align:right;margin-bottom:0;margin-top:14px"><button type="button" onclick="grantsDlg.close()">Закрыть</button></p></dialog>
<dialog id="groupsDlg"><h3 style="margin-top:0">Доступ по филиалам</h3>
<p class="muted" style="margin-top:0">Откройте доступ ко всему филиалу сразу — помощник увидит все компьютеры этой группы, включая те, что добавят позже.</p>
<select id="gr_sel" onchange="loadGroupGrants()" style="margin-bottom:10px"></select>
<div id="gr_list"></div>
<p style="text-align:right;margin-bottom:0;margin-top:14px"><button type="button" onclick="groupsDlg.close()">Закрыть</button></p></dialog>
<dialog id="logsDlg" style="width:min(820px,94vw)"><h3 style="margin-top:0">Журнал подключений</h3>
<p class="muted" style="margin-top:0">Кто из сотрудников и когда подключался к чужим компьютерам. Хранится 1 год.</p>
<input id="lg_q" placeholder="Поиск: кто, к кому…" oninput="loadLogs()" style="margin-bottom:10px">
<div style="max-height:55vh;overflow:auto"><table><thead><tr><th>Начало</th><th>Кто</th><th>Куда</th><th>Длительность</th><th title="Сколько раз двигали мышью, кликали или печатали">Клики/нажатия</th><th class="hm">IP</th></tr></thead><tbody id="lg_body"></tbody></table></div>
<p style="text-align:right;margin-bottom:0;margin-top:10px"><button type="button" onclick="logsDlg.close()">Закрыть</button></p></dialog>
<div id="toast"></div>
<script>
const $=s=>document.getElementById(s),F=["name","program","remote_id","password","phone","grp","note"];let rows=[],online=[],cur=null,ME={owner:false,user:""};
const esc=s=>String(s??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
const digits=s=>String(s||"").replace(/\D/g,""),fmt=s=>digits(s).replace(/(\d{3})(?=\d)/g,"$1 ");
function toast(t){const e=$("toast");e.textContent=t;e.classList.add("on");clearTimeout(e._t);e._t=setTimeout(()=>e.classList.remove("on"),1600)}
async function api(u,o={}){const r=await fetch(u,{headers:{"Content-Type":"application/json"},...o});if(r.status==401){show(false);throw 0}const j=await r.json();if(!r.ok){alert(j.error||"Ошибка");throw 0}return j}
function show(a){$("app").hidden=!a;$("login").hidden=a}
async function login(e){e.preventDefault();const r=await fetch("/api/login",{method:"POST",body:JSON.stringify({user:$("lu").value,password:$("lp").value})});
 if(r.ok){$("lp").value="";$("le").textContent="";show(true);await loadMe();load();loadOnline()}else $("le").textContent=(await r.json()).error||"Неверный логин или пароль"}
async function loadMe(){try{ME=await api("/api/me")}catch(e){return}
 $("whoami").textContent="вы: "+ME.user;
 for(const id of["btnUsers","btnGroups","btnLogs","btnExport","btnImport"])$(id).hidden=!ME.owner}
async function logout(){await fetch("/api/logout",{method:"POST"});show(false)}
function openEA(id,pw){id=digits(id);if(id.length!=9){alert("ID агента — 9 цифр");return}
 if(pw)sessionStorage.setItem("pw:"+id,pw);window.open("/view?id="+id,"ea"+id)}
// вход в один клик: берём ключ доступа агента и сразу открываем сеанс без ввода пароля
async function oneClick(id){id=digits(id);try{const r=await api("/api/akey?id="+id);openEA(id,r.akey)}catch(e){openEA(id)}}
function quick(e){e.preventDefault();oneClick($("qid").value);$("qid").value=""}
function link(r){const id=encodeURIComponent(r.remote_id.replace(/\s/g,""));if(!id)return"";
 return {"AnyDesk":"anydesk:"+id,"RustDesk":"rustdesk://"+id,"TeamViewer":"teamviewer10://control?device="+id}[r.program]||""}
const isOn=r=>r.program=="EPOLISI assist"&&online.some(a=>a.id==digits(r.remote_id));
async function load(){rows=await api("/api/hosts?q="+encodeURIComponent($("q").value));render()}
function render(){let h="",g=null;
 for(const r of rows){if(r.grp!==g){g=r.grp;h+=`<tr class="g"><td colspan="7">${esc(g||"Без группы")}</td></tr>`}
  const ea=r.program=="EPOLISI assist",l=link(r);
  const btn=ea?`<button class="pri" onclick="connRow(${r.id})">Подключить</button>`
   :l?`<a href="${esc(l)}"><button class="pri">Подключить</button></a>`:`<button onclick="cp('${r.id}','remote_id')">Копировать ID</button>`;
  h+=`<tr><td>${ea?`<span class="dot ${isOn(r)?"on":""}" title="${isOn(r)?"В сети":"Не в сети"}"></span><span class="stat ${isOn(r)?"on":"off"}">${isOn(r)?"Онлайн":"Не на связи"}</span><br>`:""}<b>${esc(r.name)}</b></td><td><span class="tag">${esc(r.program)}</span></td>
  <td><span class="id" onclick="cp('${r.id}','remote_id')" title="Копировать">${esc(ea?fmt(r.remote_id):r.remote_id)}</span></td>
  <td class="hm">${r.password?`<span class="pw" onclick="cp('${r.id}','password')" title="Копировать">••••••</span>`:""}</td>
  <td class="hm">${esc(r.phone)}</td><td class="hm muted">${esc(r.note)}</td>
  <td><div class="acts">${btn}${ME.owner?`<button onclick="openGrants(${r.id})" title="Кому из помощников видна эта запись">Доступ</button>`:""}
  ${ME.owner||r.mine?`<button onclick="edit(${r.id})">✎</button><button class="del" onclick="del(${r.id})">✕</button>`:""}</div></td></tr>`}
 $("tb").innerHTML=h;$("empty").hidden=rows.length>0;
 $("grps").innerHTML=[...new Set(rows.map(r=>r.grp).filter(Boolean))].map(g=>`<option value="${esc(g)}">`).join("")}
async function loadOnline(){if($("app").hidden)return;try{online=await api("/api/online")}catch(e){return}
 $("onl").hidden=!online.length;
 $("online").innerHTML=online.map(a=>{const known=rows.find(r=>r.program=="EPOLISI assist"&&digits(r.remote_id)==a.id);
  return `<div class="chip"><span class="dot on"></span><div><b>${esc(known?known.name:a.name)}</b><br><small>${fmt(a.id)}${a.busy?` · смотрят: ${a.viewers}`:""}</small></div>
  <button class="pri" onclick="${known?`connRow(${known.id})`:`oneClick('${a.id}')`}">Подключить</button>
  ${known?"":`<button onclick="addFrom('${a.id}','${esc(a.name)}')">В книгу</button>`}</div>`}).join("");render()}
function connRow(id){const r=rows.find(x=>x.id==id);if(r.password)openEA(r.remote_id,r.password);else oneClick(r.remote_id)}
function cp(id,f){const v=rows.find(x=>x.id==id)[f],t=document.createElement("textarea");t.value=v;document.body.append(t);t.select();
 const ok=document.execCommand("copy");t.remove();ok?toast("Скопировано"):prompt("Скопируйте:",v)}
function edit(id,preset){cur=rows.find(r=>r.id==id)||null;$("dt").textContent=cur?"Изменить запись":"Новая запись";
 for(const f of F)$("f_"+f).value=cur?cur[f]:(preset?.[f]??(f=="program"?"EPOLISI assist":""));dlg.showModal()}
async function addFrom(id,name){let akey="";try{akey=(await api("/api/akey?id="+digits(id))).akey}catch(e){}
 edit(null,{name,remote_id:fmt(id),password:akey,program:"EPOLISI assist"})}
async function save(e){e.preventDefault();const d={id:cur?.id};for(const f of F)d[f]=$("f_"+f).value;
 await api("/api/hosts",{method:"POST",body:JSON.stringify(d)});dlg.close();toast("Сохранено");load().then(loadOnline)}
async function del(id){const r=rows.find(x=>x.id==id);if(!confirm(`Удалить «${r.name}»?`))return;await api("/api/hosts/"+id,{method:"DELETE"});load()}
async function doImport(inp){const t=await inp.files[0].text();inp.value="";await api("/api/import",{method:"POST",body:t});toast("Импортировано");load()}
async function loadUsers(){const list=await api("/api/users");
 $("usersList").innerHTML=list.length?list.map(u=>`<div style="display:flex;justify-content:space-between;align-items:center;padding:6px 0;border-bottom:1px solid var(--line)">
 <b>${esc(u)}</b><button class="del" onclick="delUser('${esc(u)}')">Удалить</button></div>`).join(""):'<p class="muted">Помощников пока нет</p>'}
async function addUser(e){e.preventDefault();const user=$("nu_name").value.trim(),password=$("nu_pass").value;
 if(!user||!password)return;await api("/api/users",{method:"POST",body:JSON.stringify({user,password})});
 $("nu_name").value="";$("nu_pass").value="";toast(`Добавлено: ${user} / ${password}`);loadUsers()}
async function delUser(u){if(!confirm(`Удалить помощника «${u}»? Доступы, которые вы ему дали, тоже удалятся.`))return;
 await api("/api/users/"+u,{method:"DELETE"});loadUsers()}
async function openGrants(id){const [users,map]=await Promise.all([api("/api/users"),api("/api/grants")]);
 const r=rows.find(x=>x.id==id);$("gd_title").textContent="Доступ: "+r.name;
 const hostGranted=new Set(map.host[id]||[]);
 if(!users.length){$("gd_group").innerHTML="";$("gd_list").innerHTML='<p class="muted">Сначала добавьте помощника</p>';grantsDlg.showModal();return}
 if(r.grp){const grpGranted=new Set(map.group[r.grp]||[]);
  $("gd_group").innerHTML=`<p style="margin-bottom:6px"><b>Весь филиал «${esc(r.grp)}»:</b></p>`+users.map(u=>
  `<label style="display:flex;gap:8px;align-items:center;padding:4px 0">
  <input type="checkbox" ${grpGranted.has(u)?"checked":""} onchange="toggleGrant('group','${esc(r.grp)}','${esc(u)}',this.checked)"> ${esc(u)}</label>`).join("")
  +'<p style="margin:10px 0 6px"><b>Только этот компьютер:</b></p>'}
 else $("gd_group").innerHTML="";
 $("gd_list").innerHTML=users.map(u=>`<label style="display:flex;gap:8px;align-items:center;padding:4px 0">
 <input type="checkbox" ${hostGranted.has(u)?"checked":""} onchange="toggleGrant('host','${id}','${esc(u)}',this.checked)"> ${esc(u)}</label>`).join("");
 grantsDlg.showModal()}
async function toggleGrant(kind,target,u,on){await api("/api/grants",{method:"POST",body:JSON.stringify({kind,target,user:u,grant:on})})}
async function openGroups(){const groups=await api("/api/groups");
 if(!groups.length){alert("Пока нет ни одной группы — укажите «Группу» у записи в книге.");return}
 $("gr_sel").innerHTML=groups.map(g=>`<option value="${esc(g)}">${esc(g)}</option>`).join("");
 groupsDlg.showModal();loadGroupGrants()}
async function loadGroupGrants(){const [users,map]=await Promise.all([api("/api/users"),api("/api/grants")]);
 const grp=$("gr_sel").value;if(!users.length){$("gr_list").innerHTML='<p class="muted">Сначала добавьте помощника</p>';return}
 const granted=new Set(map.group[grp]||[]);
 $("gr_list").innerHTML=users.map(u=>`<label style="display:flex;gap:8px;align-items:center;padding:4px 0">
 <input type="checkbox" ${granted.has(u)?"checked":""} onchange="toggleGrant('group','${esc(grp)}','${esc(u)}',this.checked)"> ${esc(u)}</label>`).join("")}
async function loadLogs(){const list=await api("/api/logs?q="+encodeURIComponent($("lg_q").value));
 $("lg_body").innerHTML=list.length?list.map(r=>{const dur=r.end_ts?Math.max(0,Math.round(
 (new Date(r.end_ts.replace(" ","T"))-new Date(r.start_ts.replace(" ","T")))/60000))+" мин":"идёт сейчас";
 return `<tr><td>${esc(r.start_ts)}</td><td>${esc(r.username)}</td><td>${esc(r.agent_name||r.agent_id)}</td>
 <td>${dur}</td><td${r.inputs?"":' style="color:var(--red)" title="За весь сеанс ни одного клика или нажатия — управление не дошло до компьютера"'}>${r.inputs}</td>
 <td class="hm muted">${esc(r.ip)}</td></tr>`}).join(""):'<tr><td colspan="6" class="muted">Пусто</td></tr>'}
api("/api/hosts").then(async r=>{show(true);await loadMe();rows=r;render();loadOnline()}).catch(()=>{});
setInterval(loadOnline,10000);
</script></body></html>"""

VIEW_PAGE = r"""<!doctype html>
<html lang="ru"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>EPOLISI assist — сеанс</title>
<style>""" + STYLE + r"""
html,body{height:100%}body{display:flex;flex-direction:column;overflow:hidden}
#bar{display:flex;gap:8px;align-items:center;padding:8px 12px;background:var(--card);border-bottom:1px solid var(--line);flex-wrap:wrap}
#bar select{width:auto;padding:6px 8px}#bar button,#bar .btn{padding:6px 10px;font-size:14px}
#title{font-weight:600}#st{color:var(--muted);font-size:13px}#st.err{color:var(--red)}.sp{flex:1}
#wrap{flex:1;background:#000;position:relative;min-height:0}
#cv{width:100%;height:100%;object-fit:contain;display:block;outline:none;cursor:default}
#msg{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;color:#ccc;font-size:18px;text-align:center;padding:20px;flex-direction:column;gap:14px}
#files{position:absolute;top:0;right:0;bottom:0;width:min(340px,86vw);background:var(--card);border-left:1px solid var(--line);display:none;flex-direction:column;z-index:5}
#files.on{display:flex}#files header{padding:10px 12px;border-bottom:1px solid var(--line);display:flex;align-items:center;gap:8px}
#files header b{flex:1}#fpath{padding:6px 12px;font-family:Consolas,monospace;font-size:12px;color:var(--muted);word-break:break-all;border-bottom:1px solid var(--line)}
#flist{flex:1;overflow:auto}.frow{display:flex;gap:8px;align-items:center;padding:8px 12px;border-bottom:1px solid var(--line);cursor:pointer}
.frow:hover{background:var(--acc2)}.frow .nm{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.frow .sz{color:var(--muted);font-size:12px}
#ffoot{padding:10px 12px;border-top:1px solid var(--line);display:flex;gap:8px;align-items:center;flex-wrap:wrap}
#fprog{font-size:12px;color:var(--muted);width:100%}
</style></head><body>
<div id="bar"><a class="btn" href="/">← Книга</a><span id="title"></span><span id="st">Подключение…</span>
<span id="role" class="tag" hidden></span><button id="btnTake" hidden onclick="takeControl()" class="pri">Взять управление</button><span class="sp"></span>
<select id="qual" class="ctl-btn" title="Качество картинки"><option value="fast">Быстро</option><option value="normal" selected>Норма</option><option value="hq">Чётко</option></select>
<button class="ctl-btn" onclick="textDlg.showModal()" title="Напечатать текст на компьютере агента">Вставить текст</button>
<button class="ctl-btn" onclick="combo([[91,1]])">Win</button><button class="ctl-btn" onclick="combo([[18],[9]])">Alt+Tab</button>
<button class="ctl-btn" onclick="combo([[17],[16],[27]])" title="Ctrl+Shift+Esc">Диспетчер задач</button>
<button class="ctl-btn" onclick="sendClip()" title="Отправить ваш буфер обмена агенту">📋 Агенту</button>
<button class="ctl-btn" onclick="toggleFiles()" title="Передать или забрать файлы">Файлы</button>
<button onclick="full()" title="Во весь экран. Выход: Ctrl+Alt+Shift+Q">⛶</button><button class="del" onclick="bye()">Отключиться</button></div>
<div id="wrap"><canvas id="cv" tabindex="0"></canvas><div id="msg">Подключение…</div>
<div id="files"><header><b>Файлы агента</b><button onclick="toggleFiles()">✕</button></header>
<div id="fpath"></div><div id="flist"></div>
<div id="ffoot"><button onclick="fup()">↑ Вверх</button><button onclick="fhome()">Рабочий стол</button>
<button class="pri" onclick="fsend.click()">Отправить агенту</button><input id="fsend" type="file" multiple hidden onchange="pushFiles(this)">
<div id="fprog"></div></div></div></div>
<dialog id="pwd"><form onsubmit="setPw(event)"><h3 style="margin-top:0">Пароль агента</h3>
<p class="muted" style="margin-top:0">Пароль показан в окне программы EPOLISI assist на компьютере агента.</p>
<input id="pwi" autocomplete="off" inputmode="numeric"><p style="display:flex;gap:8px;justify-content:flex-end;margin-bottom:0">
<a class="btn" href="/">Отмена</a><button class="pri">Подключиться</button></p></form></dialog>
<dialog id="textDlg"><form onsubmit="sendText(event)"><h3 style="margin-top:0">Вставить текст</h3>
<textarea id="txt" placeholder="Текст будет напечатан там, где у агента стоит курсор"></textarea>
<p style="display:flex;gap:8px;justify-content:flex-end;margin-bottom:0"><button type="button" onclick="textDlg.close()">Отмена</button><button class="pri">Отправить</button></p></form></dialog>
<div id="toast"></div>
<script>
const $=s=>document.getElementById(s),ID=(new URLSearchParams(location.search).get("id")||"").replace(/\D/g,"");
const fmt=s=>s.replace(/(\d{3})(?=\d)/g,"$1 "),cv=$("cv"),ctx=cv.getContext("2d");
const EXT=new Set(["ArrowUp","ArrowDown","ArrowLeft","ArrowRight","Insert","Delete","Home","End","PageUp","PageDown",
 "ControlRight","AltRight","NumpadEnter","NumpadDivide","MetaLeft","MetaRight","ContextMenu"]);
const FIXVK={59:186,61:187,173:189,224:91};  // отличия Firefox
let ws,pw=sessionStorage.getItem("pw:"+ID),connected=false,pending=null,frames=0,bytes=0,res="",tick=0;const pressed=new Map();
document.title="EPOLISI assist — "+fmt(ID);$("title").textContent="Агент "+fmt(ID);
function toast(t,ms){const e=$("toast");e.textContent=t;e.classList.add("on");clearTimeout(e._t);e._t=setTimeout(()=>e.classList.remove("on"),ms||1600)}
function st(t,err){$("st").textContent=t;$("st").className=err?"err":""}
function msg(html){$("msg").innerHTML=html;$("msg").hidden=!html}
function send(o){if(ws&&ws.readyState==1)ws.send(JSON.stringify(o))}
function setPw(e){e.preventDefault();pw=$("pwi").value.trim();if(!pw)return;pwd.close();connect()}
function connect(){
 if(!pw){pwd.showModal();$("pwi").focus();return}
 msg("Подключение…");st("Подключение…");
 ws=new WebSocket((location.protocol=="https:"?"wss://":"ws://")+location.host+"/ws/view?id="+ID);ws.binaryType="blob";
 ws.onopen=()=>send({type:"auth",pw});
 ws.onmessage=async e=>{
  if(typeof e.data!="string"){const b=await createImageBitmap(e.data);
   if(cv.width!=b.width||cv.height!=b.height){cv.width=b.width;cv.height=b.height}
   ctx.drawImage(b,0,0);b.close();frames++;bytes+=e.data.size;msg("");send({type:"ack"});return}
  const m=JSON.parse(e.data);
  if(m.type=="connected"){connected=true;sessionStorage.setItem("pw:"+ID,pw);$("title").textContent=m.name+" · "+fmt(ID);
   st("Подключено");msg("Ожидание изображения…");setRole(m.controller,1);if(isController)send({type:"q",mode:$("qual").value});cv.focus()}
  else if(m.type=="role")setRole(m.controller,m.viewers);
  else if(m.type=="info")res=m.w+"×"+m.h;
  else if(m.type=="clip"){navigator.clipboard?.writeText(m.text).then(()=>toast("Буфер агента скопирован — можно вставить (Ctrl+V)")).catch(()=>toast("Буфер агента: "+m.text.slice(0,60)))}
  else if(m.type&&m.type[0]=="f")onFile(m);
  else if(m.type=="error"){connected=false;st(m.msg,1);
   if(m.code=="bad_pw"){sessionStorage.removeItem("pw:"+ID);pw=null;msg("");$("pwi").value="";pwd.showModal()}
   else msg(esc(m.msg)+'<button class="pri" onclick="connect()">Подключиться снова</button>')}
 };
 let answered=false;ws.addEventListener("message",()=>answered=true);
 ws.onclose=()=>{releaseAll();if(connected){connected=false;st("Соединение прервано",1);
  msg('Соединение прервано<button class="pri" onclick="connect()">Подключиться снова</button>')}
  else if(!answered){st("Нет доступа",1);msg('Не удалось подключиться. Войдите в EPOLISI assist<a class="btn pri" href="/">Войти</a>')}};
}
const esc=s=>String(s??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
let isController=true;
function setRole(ctrl,viewers){isController=!!ctrl;$("role").hidden=false;
 $("role").textContent=isController?"Вы управляете":`Вы смотрите${viewers>1?" ("+viewers+")":""}`;
 $("btnTake").hidden=isController;
 for(const b of document.querySelectorAll(".ctl-btn"))b.disabled=!isController;
 if(!isController&&$("files").classList.contains("on"))toggleFiles()}
function takeControl(){send({type:"control"});cv.focus()}
setInterval(()=>{if(!connected)return;st(`Подключено · ${res} · ${frames} кадр/с · ${bytes/1024|0} КБ/с`);frames=0;bytes=0;
 if(++tick%20==0)send({type:"ping"})},1000);
function pos(e){const r=cv.getBoundingClientRect(),s=Math.min(r.width/cv.width,r.height/cv.height),w=cv.width*s,h=cv.height*s,
 cl=v=>Math.min(1,Math.max(0,v));return{x:cl((e.clientX-r.left-(r.width-w)/2)/w),y:cl((e.clientY-r.top-(r.height-h)/2)/h)}}
cv.addEventListener("pointermove",e=>{if(!connected||!isController)return;const first=!pending;pending=pos(e);
 if(first)requestAnimationFrame(()=>{send({type:"m",...pending});pending=null})});
const BTN=["l","m","r"];let btnDown=new Set();
cv.addEventListener("pointerdown",e=>{if(!connected||!isController||!BTN[e.button])return;e.preventDefault();cv.focus();btnDown.add(e.button);
 send({type:"b",b:BTN[e.button],d:1,...pos(e)})});
addEventListener("pointerup",e=>{if(!isController||!btnDown.delete(e.button))return;send({type:"b",b:BTN[e.button],d:0,...pos(e)})});
cv.addEventListener("contextmenu",e=>e.preventDefault());
cv.addEventListener("wheel",e=>{if(!connected||!isController)return;e.preventDefault();send({type:"w",d:e.deltaY>0?-120:120})},{passive:false});
const FS_COMBO=e=>e.ctrlKey&&e.altKey&&e.shiftKey&&e.code=="KeyQ";
function keyEv(e,d){
 if(FS_COMBO(e)){e.preventDefault();if(d&&document.fullscreenElement)document.exitFullscreen();return}
 if(!connected||!isController||document.querySelector("dialog[open]")||document.activeElement==$("qual"))return;
 e.preventDefault();const vk=FIXVK[e.keyCode]||e.keyCode,x=EXT.has(e.code)?1:0;if(!vk)return;
 if(d)pressed.set(e.code,[vk,x]);else pressed.delete(e.code);send({type:"k",vk,d,x})}
addEventListener("keydown",e=>keyEv(e,1));addEventListener("keyup",e=>keyEv(e,0));
function releaseAll(){for(const[,[vk,x]]of pressed)send({type:"k",vk,d:0,x});pressed.clear();
 for(const b of btnDown)send({type:"b",b:BTN[b],d:0,x:.5,y:.5});btnDown.clear()}
addEventListener("blur",releaseAll);
function combo(keys){for(const[vk,x]of keys)send({type:"k",vk,d:1,x:x||0});for(const[vk,x]of[...keys].reverse())send({type:"k",vk,d:0,x:x||0});cv.focus()}
function sendText(e){e.preventDefault();const t=$("txt").value;if(t)send({type:"t",text:t});$("txt").value="";textDlg.close();cv.focus()}
async function sendClip(){try{const t=await navigator.clipboard.readText();if(t)send({type:"clip",text:t});toast("Отправлено агенту")}catch(e){toast("Браузер не дал прочитать буфер — разрешите доступ к буферу обмена")}}
// ---- файлы: забрать у агента (обзор папок) и отправить агенту ----
let fentries=[],curPath="",dl=null;
function toggleFiles(){const on=$("files").classList.toggle("on");if(on)fhome();else cv.focus()}
function fhome(){send({type:"fls",path:""})}
function fup(){send({type:"fls",path:curPath,up:1})}
function fopen(i){send({type:"fls",path:fentries[i].p})}
function fget(i){const e=fentries[i];$("fprog").textContent="Скачивание "+e.n+"…";dl={name:e.n,parts:[]};send({type:"fget",path:e.p})}
function b64bytes(b){const s=atob(b),u=new Uint8Array(s.length);for(let i=0;i<s.length;i++)u[i]=s.charCodeAt(i);return u}
function onFile(m){
 if(m.type=="flist"){curPath=m.path||"";fentries=m.entries||[];$("fpath").textContent=m.path||"Рабочий стол";
  $("flist").innerHTML=fentries.map((e,i)=>e.d
   ?`<div class="frow" onclick="fopen(${i})"><span>📁</span><span class="nm">${esc(e.n)}</span></div>`
   :`<div class="frow" onclick="fget(${i})"><span>📄</span><span class="nm">${esc(e.n)}</span><span class="sz">${e.s>0?(e.s/1024|0)+" КБ":""}</span></div>`
  ).join("")||'<div class="frow muted">Папка пуста</div>';}
 else if(m.type=="fgetmeta")dl={name:m.name,parts:[]};
 else if(m.type=="fdata"){if(dl)dl.parts.push(b64bytes(m.data))}
 else if(m.type=="fend"){if(dl){const a=document.createElement("a");a.href=URL.createObjectURL(new Blob(dl.parts));
  a.download=dl.name;a.click();setTimeout(()=>URL.revokeObjectURL(a.href),10000);$("fprog").textContent="Скачано: "+dl.name;dl=null}}
 else if(m.type=="fok"){$("fprog").textContent=m.msg||"Готово";if($("files").classList.contains("on"))send({type:"fls",path:curPath})}
 else if(m.type=="ferr")$("fprog").textContent=m.msg||"Ошибка";
}
async function pushFiles(inp){const files=[...inp.files];inp.value="";
 for(const f of files){$("fprog").textContent="Отправка "+f.name+"…";send({type:"fput",name:f.name,size:f.size});
  const buf=new Uint8Array(await f.arrayBuffer()),C=48000;
  for(let o=0;o<buf.length;o+=C){let s="";const p=buf.subarray(o,o+C);for(let i=0;i<p.length;i++)s+=String.fromCharCode(p[i]);send({type:"fdata",data:btoa(s)})}
  send({type:"fend"})}
 $("fprog").textContent="Отправлено. У агента на рабочем столе, папка «EPOLISI assist»."}
$("qual").onchange=()=>{send({type:"q",mode:$("qual").value});cv.focus()};
async function full(){toast("Полноэкранный режим. Для выхода нажмите Ctrl+Alt+Shift+Q",4000);
 try{await document.documentElement.requestFullscreen();await navigator.keyboard?.lock?.()}catch(e){}cv.focus()}
document.addEventListener("fullscreenchange",()=>{if(!document.fullscreenElement){navigator.keyboard?.unlock?.();cv.focus()}});
function bye(){connected=false;ws&&ws.close();location="/"}
connect();
</script></body></html>"""


if __name__ == "__main__":
    init_db()
    srv = ThreadingHTTPServer((CFG.get("host", "0.0.0.0"), int(CFG.get("port", 8095))), Handler)
    srv.daemon_threads = True
    print(f"EPOLISI assist: http://{CFG.get('host')}:{CFG.get('port')}")
    srv.serve_forever()
