"""Per-session memory broker: one `ai-raccoon` proxy per session instead of one per search.

A SessionStart hook starts `memory_broker.py serve` detached; it holds one proxy session open and
answers `memory_search` calls from the session's other hooks over a unix socket in an owner-only
directory under `~/.ai-badger/memory-broker/`, keyed by the sanitized session id. Concurrent
requests are multiplexed onto the one proxy stdio stream under distinct JSON-RPC ids and routed
back by id, so none waits behind another and a client that gives up costs the others nothing.

The client half (`call`, `is_live`, `shutdown`, `ensure_started`) is stdlib-only and raises
`Unavailable` when there is no broker, so callers fall back to a per-call proxy spawn. The
broker exits on the SessionEnd hook, after `IDLE_SECONDS` without a request, or on SIGTERM, and
removes its socket. POSIX only; Windows keeps the per-call path.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import selectors
import signal
import socket
import stat
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

try:
    import fcntl
except ImportError:  # Windows: no broker, callers keep the per-call path
    fcntl = None  # pylint: disable=invalid-name

DIR_PARTS = (".ai-badger", "memory-broker")
IDLE_SECONDS = 1800.0
OPEN_SECONDS = 5.0
CONNECT_SECONDS = 0.5
REQUEST_READ_SECONDS = 5.0
MAX_REQUEST_SECONDS = 120.0
REQUEST_MAX_BYTES = 64 * 1024
REPLY_MAX_BYTES = 4 * 1024 * 1024
LINE_MAX_BYTES = 1024 * 1024
# AF_UNIX paths are capped near 104 bytes on macOS and 108 on Linux.
SOCKET_PATH_MAX = 100
ALLOWED_METHOD = "tools/call"
ALLOWED_TOOL = "memory_search"
CONTEXT_STEM = "memory_context"


class Unavailable(Exception):
    """No broker answers for this session: callers fall back to a per-call proxy."""


# ------------------------------------------------------------------ paths


def safe_session(session_id: Optional[str]) -> str:
    """Session id made filesystem-safe: keeps [A-Za-z0-9._-], `_` for the rest, dot-only
    segments neutralized. Mirrors `memory_first_gate._safe_session` (pinned by a test)."""
    if not session_id:
        return ""
    sanitized = "".join(
        ch if ch.isascii() and (ch.isalnum() or ch in "._-") else "_" for ch in str(session_id))
    if sanitized == ".":
        return "_"
    if sanitized == "..":
        return "__"
    return sanitized


def broker_dir(home: Optional[str] = None) -> Path:
    """The broker's owner-only directory under *home* (default: the user's home)."""
    return Path(home or Path.home()).joinpath(*DIR_PARTS)


def socket_path(session_id: Optional[str], home: Optional[str] = None) -> Path:
    """The session's socket path; a digest of the id when the readable name would not fit."""
    directory = broker_dir(home)
    safe = safe_session(session_id) or "_"
    path = directory / f"{safe}.sock"
    if len(os.fsencode(str(path))) >= SOCKET_PATH_MAX:
        path = directory / (hashlib.sha256(safe.encode("utf-8")).hexdigest()[:16] + ".sock")
    return path


# ------------------------------------------------------------------ client


def _remove_if_same(path: Path, before: os.stat_result) -> None:
    """Remove the stale socket at *path* unless something replaced it since *before*."""
    try:
        now = path.lstat()
        if stat.S_ISSOCK(now.st_mode) and now.st_ino == before.st_ino:
            path.unlink()
    except OSError:
        pass


def open_socket(session_id: Optional[str], home: Optional[str] = None) -> socket.socket:
    """A connected socket to the session's broker, or `Unavailable`; a socket file nobody
    listens on is stale and is removed."""
    if sys.platform == "win32":
        raise Unavailable("no unix sockets")
    path = socket_path(session_id, home)
    try:
        before = path.lstat()
    except OSError as err:
        raise Unavailable("no socket") from err
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        sock.settimeout(CONNECT_SECONDS)
        sock.connect(str(path))
    except ConnectionRefusedError as err:
        sock.close()
        _remove_if_same(path, before)
        raise Unavailable("stale socket") from err
    except OSError as err:
        sock.close()
        raise Unavailable(str(err)) from err
    return sock


def _read_line(sock: socket.socket, limit: int) -> Optional[bytes]:
    """One newline-terminated line from *sock* (timeout set by the caller), or None."""
    data = b""
    while b"\n" not in data:
        chunk = sock.recv(65536)
        if not chunk:
            return None
        data += chunk
        if len(data) > limit:
            return None
    return data.split(b"\n", 1)[0]


def _exchange(sock: socket.socket, request: Dict[str, Any], seconds: float,
              limit: int) -> Optional[Dict[str, Any]]:
    """Send *request* and read the one-line JSON response within *seconds*; None on a
    timeout, a bad line or a closed peer; `Unavailable` when the broker cannot be written to."""
    sock.settimeout(max(seconds, 0.001))
    try:
        sock.sendall(json.dumps(request).encode("utf-8") + b"\n")
    except (BrokenPipeError, ConnectionResetError) as err:
        raise Unavailable("broker went away") from err
    except OSError:
        return None
    try:
        line = _read_line(sock, limit)
    except OSError:
        return None
    if line is None:
        return None
    try:
        response = json.loads(line)
    except ValueError:
        return None
    return response if isinstance(response, dict) else None


def call(session_id: Optional[str], method: str, params: Dict[str, Any], seconds: float,
         home: Optional[str] = None) -> Optional[Dict[str, Any]]:
    """The proxy's raw JSON-RPC reply to one request, or None when the broker could not
    answer within *seconds*; `Unavailable` when no broker answers for the session."""
    sock = open_socket(session_id, home)
    try:
        response = _exchange(sock, {"op": "call", "method": method, "params": params,
                                    "seconds": seconds}, seconds, REPLY_MAX_BYTES)
    finally:
        sock.close()
    if response is None or response.get("ok") is not True:
        return None
    reply = response.get("reply")
    return reply if isinstance(reply, dict) else None


def is_live(session_id: Optional[str], home: Optional[str] = None) -> bool:
    """True when a broker listens for the session."""
    try:
        open_socket(session_id, home).close()
    except Unavailable:
        return False
    return True


def shutdown(session_id: Optional[str], home: Optional[str] = None) -> bool:
    """Ask the session's broker to exit; True when one acknowledged."""
    try:
        sock = open_socket(session_id, home)
    except Unavailable:
        return False
    try:
        response = _exchange(sock, {"op": "shutdown"}, 1.0, REQUEST_MAX_BYTES)
    except Unavailable:
        return False
    finally:
        sock.close()
    return bool(response and response.get("ok") is True)


def ensure_started(session_id: Optional[str], env: Optional[Dict[str, str]] = None) -> bool:
    """Start a detached broker for the session unless one is live; never waits for it.

    Two racing starts are harmless: the second broker finds the first one's lock and exits.
    """
    if sys.platform == "win32" or not safe_session(session_id):
        return False
    env = dict(os.environ if env is None else env)
    if is_live(session_id, env.get("HOME")):
        return True
    try:
        subprocess.Popen(  # pylint: disable=consider-using-with
            [sys.executable, str(Path(__file__).resolve()), "serve", "--session", str(session_id)],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            close_fds=True, start_new_session=True, env=env)
    except (OSError, ValueError):
        return False
    return True


# ------------------------------------------------------------------ multiplexer


class Deadline:
    """A wall-clock deadline with the two methods a proxy session needs of a budget."""

    def __init__(self, seconds: float):
        self.end = time.monotonic() + seconds

    def remaining(self) -> float:
        """Seconds left, never negative."""
        return max(0.0, self.end - time.monotonic())

    def expired(self) -> bool:
        """True once the deadline has passed."""
        return self.remaining() <= 0


class _Waiter:
    """One in-flight request: set by the reader thread when its reply arrives."""

    def __init__(self, session: Any):
        self.session = session
        self.event = threading.Event()
        self.reply: Optional[Dict[str, Any]] = None


class Multiplexer:
    """One proxy session shared by many threads: requests go out under distinct ids and a
    reader thread hands each reply to the thread waiting on its id. A dead or poisoned proxy
    is dropped and reopened by the next request."""

    def __init__(self, open_session: Callable[[Deadline], Any]):
        self._open_session = open_session
        self._state = threading.Lock()
        self._open_lock = threading.Lock()
        self._write_lock = threading.Lock()
        self._session: Any = None
        self._waiters: Dict[int, _Waiter] = {}
        self._next_id = 1
        self._readers: List[threading.Thread] = []
        self._closing = False

    @staticmethod
    def _usable(session: Any) -> bool:
        return (session is not None and not session.poisoned and not session.closed
                and session.process.poll() is None)

    def warm(self) -> bool:
        """Open the proxy now rather than at the first request."""
        return self._ensure(Deadline(OPEN_SECONDS)) is not None

    def _ensure(self, deadline: Deadline) -> Any:
        with self._state:
            session = self._session
        if self._usable(session):
            return session
        if not self._open_lock.acquire(timeout=max(deadline.remaining(), 0.001)):
            return None
        try:
            with self._state:
                session = self._session
                if self._usable(session):
                    return session
                if self._closing:
                    return None
            if session is not None:
                self._retire(session)
            # The open serves every waiter, so it gets its own budget, not this caller's.
            fresh = self._open_session(Deadline(OPEN_SECONDS))
            if fresh is None:
                return None
            reader = threading.Thread(target=self._read_loop, args=(fresh,), daemon=True)
            with self._state:
                self._session = fresh
                self._next_id = max(self._next_id, fresh.next_id)
                self._readers.append(reader)
            reader.start()
            return fresh
        finally:
            self._open_lock.release()

    def request(self, method: str, params: Dict[str, Any], seconds: float) -> Optional[Dict[str, Any]]:
        """The raw reply to one request within *seconds*, or None; never raises."""
        try:
            deadline = Deadline(seconds)
            session = self._ensure(deadline)
            if session is None or deadline.expired():
                return None
            waiter = _Waiter(session)
            with self._state:
                msg_id = self._next_id
                self._next_id += 1
                self._waiters[msg_id] = waiter
            try:
                line = (json.dumps({"jsonrpc": "2.0", "id": msg_id, "method": method,
                                    "params": params}, ensure_ascii=False) + "\n").encode("utf-8")
                if not self._write_lock.acquire(timeout=max(deadline.remaining(), 0.001)):
                    return None
                try:
                    wrote = session._write(line, deadline)  # pylint: disable=protected-access
                finally:
                    self._write_lock.release()
                if not wrote:
                    self._retire(session)
                    return None
                waiter.event.wait(deadline.remaining())
                return waiter.reply
            finally:
                with self._state:
                    self._waiters.pop(msg_id, None)
        except Exception:  # pylint: disable=broad-exception-caught
            return None

    def _retire(self, session: Any) -> None:
        """Drop *session*: no new request uses it, its waiters wake empty, its child dies and
        its reader thread (the owner of the pipes) closes it."""
        with self._state:
            if self._session is session:
                self._session = None
            doomed = [w for w in self._waiters.values() if w.session is session]
        session.poisoned = True
        for waiter in doomed:
            waiter.event.set()
        try:
            session.process.kill()
        except OSError:
            pass

    def _dispatch(self, raw: bytes, session: Any) -> None:
        try:
            msg = json.loads(raw)
        except ValueError:
            return
        msg_id = msg.get("id") if isinstance(msg, dict) else None
        if isinstance(msg_id, bool) or not isinstance(msg_id, int):
            return
        with self._state:
            waiter = self._waiters.get(msg_id)
        if waiter is not None and waiter.session is session:
            waiter.reply = msg
            waiter.event.set()

    def _read_loop(self, session: Any) -> None:
        """Frame the proxy's stdout by newline and dispatch each reply by id until EOF."""
        fd = session.process.stdout.fileno()
        buffer, session.buffer = session.buffer, b""
        try:
            with selectors.DefaultSelector() as selector:
                selector.register(fd, selectors.EVENT_READ)
                while True:
                    while b"\n" in buffer:
                        raw, buffer = buffer.split(b"\n", 1)
                        self._dispatch(raw, session)
                    if len(buffer) > LINE_MAX_BYTES or session.poisoned:
                        break
                    if not selector.select(1.0):
                        if session is not self._session:
                            break
                        continue
                    try:
                        chunk = os.read(fd, 65536)
                    except BlockingIOError:
                        continue
                    except OSError:
                        chunk = b""
                    if not chunk:
                        break
                    buffer += chunk
        except (OSError, ValueError):
            pass
        finally:
            self._retire(session)
            session.close(Deadline(1.0))

    def close(self) -> None:
        """Stop serving: the proxy is closed and the reader threads are joined briefly."""
        with self._state:
            self._closing = True
            session = self._session
            readers = list(self._readers)
        if session is not None:
            self._retire(session)
        for reader in readers:
            reader.join(2.0)


# ------------------------------------------------------------------ broker


def _allowed_call(params: Any, seconds: Any) -> bool:
    """True for a `memory_search` call with a sane time budget; the broker forwards nothing else."""
    if not isinstance(params, dict) or params.get("name") != ALLOWED_TOOL:
        return False
    if isinstance(seconds, bool) or not isinstance(seconds, (int, float)):
        return False
    return 0 < seconds <= MAX_REQUEST_SECONDS


class Broker:
    """The unix-socket front of a `Multiplexer`: one thread per client connection."""

    def __init__(self, session_id: str, home: Optional[str], open_session: Callable[[Deadline], Any],
                 *, idle_seconds: float = IDLE_SECONDS, clock: Callable[[], float] = time.monotonic):
        self.path = socket_path(session_id, home)
        self.mux = Multiplexer(open_session)
        self.idle_seconds = idle_seconds
        self.clock = clock
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._active = 0
        self._last = clock()

    def stop(self) -> None:
        """Ask `serve_forever` to return."""
        self._stop.set()

    def _acquire_lock(self) -> Optional[int]:
        """The per-session lock fd, or None when another broker holds it."""
        if fcntl is None:
            return None
        directory = self.path.parent
        directory.mkdir(parents=True, mode=0o700, exist_ok=True)
        info = directory.stat()
        if info.st_uid != os.getuid():
            return None
        if stat.S_IMODE(info.st_mode) != 0o700:
            directory.chmod(0o700)
        fd = os.open(str(self.path.with_suffix(".lock")), os.O_RDWR | os.O_CREAT, 0o600)
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            os.close(fd)
            return None
        return fd

    def _bind(self) -> socket.socket:
        """A listening socket at the session path (0600); the lock makes any file there stale."""
        try:
            self.path.unlink()
        except FileNotFoundError:
            pass
        server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        old = os.umask(0o177)
        try:
            server.bind(str(self.path))
        finally:
            os.umask(old)
        os.chmod(self.path, 0o600)
        server.listen(64)
        server.settimeout(0.2)
        return server

    def serve_forever(self) -> None:
        """Serve until stopped, idle or told to shut down; a second broker for the session
        returns at once. The socket and lock files are removed on the way out."""
        try:
            lock_fd = self._acquire_lock()
        except OSError:
            return
        if lock_fd is None:
            return
        server = None
        try:
            server = self._bind()
            threading.Thread(target=self.mux.warm, daemon=True).start()
            self._accept_loop(server)
        except OSError:
            pass
        finally:
            if server is not None:
                server.close()
            for leftover in (self.path, self.path.with_suffix(".lock")):
                try:
                    leftover.unlink()
                except OSError:
                    pass
            self.mux.close()
            os.close(lock_fd)

    def _idle(self) -> bool:
        with self._lock:
            return self._active == 0 and self.clock() - self._last > self.idle_seconds

    def _accept_loop(self, server: socket.socket) -> None:
        while not self._stop.is_set() and not self._idle():
            try:
                conn, _ = server.accept()
            except socket.timeout:
                continue
            threading.Thread(target=self._handle, args=(conn,), daemon=True).start()

    def _touch(self, delta: int) -> None:
        with self._lock:
            self._active += delta
            self._last = self.clock()

    def _handle(self, conn: socket.socket) -> None:
        """Serve one connection: read a request line, answer with one response line."""
        self._touch(1)
        try:
            with conn:
                conn.settimeout(REQUEST_READ_SECONDS)
                try:
                    line = _read_line(conn, REQUEST_MAX_BYTES)
                    request = json.loads(line) if line else None
                except (OSError, ValueError):
                    return
                if not isinstance(request, dict):
                    return
                response = self._answer(request)
                try:
                    conn.settimeout(REQUEST_READ_SECONDS)
                    conn.sendall(json.dumps(response, ensure_ascii=False).encode("utf-8") + b"\n")
                except OSError:
                    pass
        finally:
            self._touch(-1)

    def _answer(self, request: Dict[str, Any]) -> Dict[str, Any]:
        op = request.get("op")
        if op == "ping":
            return {"ok": True}
        if op == "shutdown":
            self.stop()
            return {"ok": True}
        if op != "call":
            return {"ok": False, "error": "unknown op"}
        params, seconds = request.get("params"), request.get("seconds")
        if request.get("method") != ALLOWED_METHOD or not _allowed_call(params, seconds):
            return {"ok": False, "error": "refused"}
        reply = self.mux.request(ALLOWED_METHOD, params, float(seconds))
        return {"ok": reply is not None, "reply": reply}


# ------------------------------------------------------------------ entry point


def _load_context() -> Optional[Any]:
    """The sibling `memory_context` (proxy spawn and handshake), or None."""
    path = Path(__file__).resolve().parent / f"{CONTEXT_STEM}.py"
    if not path.is_file():
        return None
    spec = importlib.util.spec_from_file_location("ai_badger_broker_" + CONTEXT_STEM, path)
    if spec is None or spec.loader is None:
        return None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    try:
        spec.loader.exec_module(module)
    except Exception:  # pylint: disable=broad-exception-caught
        return None
    return module


def serve(session_id: str, idle_seconds: float) -> int:
    """Run the broker for *session_id* in this process; 0 always (a hook-side child)."""
    context = _load_context()
    if context is None or sys.platform == "win32" or not safe_session(session_id):
        return 0
    env = os.environ
    if env.get(context.KILL_SWITCH) == "0":
        return 0
    exe = context.find_executable(env, env.get("HOME"))
    if exe is None:
        return 0

    def open_session(budget: Any) -> Any:
        return context.RaccoonSession.open(exe, "", session_id, budget, env)

    instance = Broker(session_id, env.get("HOME"), open_session, idle_seconds=idle_seconds)
    signal.signal(signal.SIGTERM, lambda *_: instance.stop())
    instance.serve_forever()
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    """`serve --session ID [--idle SECONDS]`."""
    parser = argparse.ArgumentParser(description="Per-session ai-raccoon memory broker")
    commands = parser.add_subparsers(dest="command", required=True)
    run = commands.add_parser("serve")
    run.add_argument("--session", required=True)
    run.add_argument("--idle", type=float, default=IDLE_SECONDS)
    args = parser.parse_args(argv)
    return serve(args.session, args.idle)


if __name__ == "__main__":
    sys.exit(main())
