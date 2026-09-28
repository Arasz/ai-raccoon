"""The memory-context pipeline's one network surface: JSON POSTs to OpenRouter under one deadline.

No proxy, no redirect, verified TLS. The deadline covers DNS, every connect attempt, the TLS
handshake and the reply; the key comes from the environment and appears only in one header.
"""
from __future__ import annotations

import http.client
import ipaddress
import json
import socket
import ssl
import threading
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Dict, List, Mapping, NamedTuple, Optional, Tuple

PRODUCTION_BASE = "https://openrouter.ai"
TEST_BASE_ENV = "AI_BADGER_MEMORY_CONTEXT_TEST_OPENROUTER_BASE"
TEST_KEY_PREFIX = "sk-test-"
LOOPBACK = "127.0.0.1"
BODY_MAX = 1024 * 1024
READ_CHUNK = 64 * 1024
TIMEOUT = "timeout"
TRANSPORT = "transport"
DNS_THREAD = "ai-badger-openrouter-dns"
WATCHDOG_THREAD = "ai-badger-openrouter-watchdog"


class Reply(NamedTuple):
    """Status, lower-cased headers and body; on failure status 0 and `error` `timeout`/`transport`."""

    status: int
    headers: Dict[str, str]
    body: bytes
    error: Optional[str] = None


def _failure(kind: str) -> Reply:
    return Reply(0, {}, b"", kind)


def _clean_key(raw: Optional[str]) -> Optional[str]:
    key = (raw or "").strip()
    if not key or any(not "!" <= ch <= "~" for ch in key):
        return None
    return key


def api_key(env: Mapping[str, str]) -> Optional[str]:
    """`OPENROUTER_API_KEY` stripped; `None` if blank, spaced or not printable ASCII."""
    return _clean_key(env.get("OPENROUTER_API_KEY"))


def api_base(env: Mapping[str, str], key: Optional[str]) -> Optional[str]:
    """The OpenRouter base URL; the test override only as `http://127.0.0.1:<port>` with an
    `sk-test-` key, else `None` (never a silent switch to production)."""
    if TEST_BASE_ENV not in env:
        return PRODUCTION_BASE
    raw = env[TEST_BASE_ENV]
    if not (key or "").startswith(TEST_KEY_PREFIX):
        return None
    try:
        parts = urllib.parse.urlsplit(raw)
        port = parts.port
    except ValueError:
        return None
    base = f"http://{LOOPBACK}:{port}"
    return base if port is not None and raw == base else None


class _Expired(TimeoutError):
    """The call's share ran out before a phase could start."""


class Call:
    """One request's deadline: the watchdog timer and the socket it shuts when the share ends."""

    def __init__(self, budget: Any):
        self.budget = budget
        self.fired = False
        self.lock = threading.Lock()
        self.watched: Optional[socket.socket] = None
        self.timer: Optional[threading.Timer] = None

    def left(self) -> float:
        """Seconds left; raises `_Expired` when none are."""
        left = self.budget.remaining()
        if left <= 0:
            raise _Expired("share spent")
        return left

    def arm(self) -> None:
        """Start the watchdog for the rest of the share."""
        self.timer = threading.Timer(max(0.0, self.budget.remaining()), self._fire)
        self.timer.name = WATCHDOG_THREAD
        self.timer.daemon = True
        self.timer.start()

    def _fire(self) -> None:
        with self.lock:
            self.fired = True
            watched = self.watched
        if watched is not None:
            _shut(watched)

    def attach(self, sock: socket.socket) -> None:
        """Watch a `dup()` of *sock*: it survives `wrap_socket` detaching the original."""
        duplicate = sock.dup()
        with self.lock:
            self.watched = duplicate
            fired = self.fired
        if fired:
            _shut(duplicate)

    def close(self) -> None:
        """Cancel and join the watchdog, then close the watched socket."""
        if self.timer is not None:
            self.timer.cancel()
            self.timer.join()
        if self.watched is not None:
            self.watched.close()

    def open_socket(self, host: str, port: int) -> socket.socket:
        """Resolve and connect under the share; the connected socket is watched."""
        addresses = _resolve(host, port, self)
        last: Optional[OSError] = None
        for family, kind, proto, _, address in addresses:
            left = self.left()
            sock = socket.socket(family, kind, proto)
            try:
                sock.settimeout(left)
                sock.connect(address)
            except OSError as err:
                sock.close()
                last = err
                continue
            self.attach(sock)
            return sock
        raise last if last is not None else OSError("no address")


def _shut(sock: socket.socket) -> None:
    try:
        socket.socket.shutdown(sock, socket.SHUT_RDWR)
    except OSError:
        pass


class _Lookup:
    """One host-name resolution on its own thread; writes only its result slot."""

    def __init__(self, host: str, port: int):
        self.host = host
        self.port = port
        self.result: Optional[List[Tuple]] = None

    def run(self) -> None:
        """Resolve, then drop out of the live-resolver table."""
        try:
            self.result = socket.getaddrinfo(self.host, self.port, type=socket.SOCK_STREAM)
        except (OSError, UnicodeError):
            self.result = []
        finally:
            with _RESOLVING_LOCK:
                if _RESOLVING.get(self.host) is self:
                    del _RESOLVING[self.host]


_RESOLVING: Dict[str, _Lookup] = {}
_RESOLVING_LOCK = threading.Lock()


def _resolve(host: str, port: int, call: Call) -> List[Tuple]:
    try:
        ipaddress.ip_address(host)
    except ValueError:
        pass
    else:
        return socket.getaddrinfo(host, port, type=socket.SOCK_STREAM,
                                  flags=socket.AI_NUMERICHOST)
    left = call.left()
    with _RESOLVING_LOCK:
        if host in _RESOLVING:
            raise _Expired("a resolver for this host is still running")
        lookup = _Lookup(host, port)
        _RESOLVING[host] = lookup
        thread = threading.Thread(target=lookup.run, name=DNS_THREAD, daemon=True)
        thread.start()
    thread.join(timeout=left)
    if thread.is_alive():
        raise _Expired("resolver still running")
    if not lookup.result:
        raise OSError("host did not resolve")
    return lookup.result


class _DeadlineHTTPConnection(http.client.HTTPConnection):
    def __init__(self, host: str, port: Optional[int] = None, *, call: Call, **kwargs: Any):
        super().__init__(host, port, **kwargs)
        self.call = call

    def connect(self) -> None:
        self.sock = self.call.open_socket(self.host, self.port)


class _DeadlineHTTPSConnection(http.client.HTTPSConnection):
    def __init__(self, host: str, port: Optional[int] = None, *, call: Call,
                 tls: ssl.SSLContext, **kwargs: Any):
        super().__init__(host, port, context=tls, **kwargs)
        self.call = call
        self.tls = tls

    def connect(self) -> None:
        sock = self.call.open_socket(self.host, self.port)
        try:
            sock.settimeout(self.call.left())
            self.sock = self.tls.wrap_socket(sock, server_hostname=self.host)
        except Exception:
            sock.close()
            raise


class DeadlineHTTPHandler(urllib.request.HTTPHandler):
    """Plain HTTP under the call's deadline (the loopback test base only)."""

    def __init__(self, call: Call):
        super().__init__()
        self.call = call

    def http_open(self, req: urllib.request.Request) -> http.client.HTTPResponse:
        return self.do_open(self._connection, req)

    def _connection(self, host: str, **kwargs: Any) -> _DeadlineHTTPConnection:
        return _DeadlineHTTPConnection(host, call=self.call, **kwargs)


class DeadlineHTTPSHandler(urllib.request.HTTPSHandler):
    """Verified HTTPS under the call's deadline."""

    def __init__(self, context: ssl.SSLContext, call: Call):
        super().__init__(context=context)
        self.tls = context
        self.call = call

    def https_open(self, req: urllib.request.Request) -> http.client.HTTPResponse:
        return self.do_open(self._connection, req)

    def _connection(self, host: str, **kwargs: Any) -> _DeadlineHTTPSConnection:
        return _DeadlineHTTPSConnection(host, call=self.call, tls=self.tls, **kwargs)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Refuse every redirect, so a 3xx surfaces as its own status."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):  # pylint: disable=too-many-arguments
        return None


def make_opener(call: Call) -> urllib.request.OpenerDirector:
    """No proxy, no redirect, default-context TLS, both schemes under *call*'s deadline."""
    return urllib.request.build_opener(
        urllib.request.ProxyHandler({}), NoRedirect(),
        DeadlineHTTPSHandler(context=ssl.create_default_context(), call=call),
        DeadlineHTTPHandler(call=call))


def _read_capped(response: Any) -> Optional[bytes]:
    chunks: List[bytes] = []
    size = 0
    while True:
        chunk = response.read(READ_CHUNK)
        if not chunk:
            return b"".join(chunks)
        size += len(chunk)
        if size > BODY_MAX:
            return None
        chunks.append(chunk)


def _exchange(opener: urllib.request.OpenerDirector, request: urllib.request.Request,
              call: Call) -> Reply:
    try:
        response = opener.open(request, timeout=call.left())
        status = response.status
    except urllib.error.HTTPError as err:
        response, status = err, err.code
    with response:
        headers = {name.lower(): value for name, value in (response.headers or {}).items()}
        body = _read_capped(response)
    length = headers.get("content-length", "")
    if body is None or (length.isdigit() and int(length) != len(body)):
        return _failure(TRANSPORT)
    return Reply(status, headers, body)


def _is_timeout(err: BaseException) -> bool:
    reason = getattr(err, "reason", None)
    return isinstance(err, (TimeoutError, socket.timeout)) or isinstance(
        reason, (TimeoutError, socket.timeout))


def post_json(url: str, body: Any, key: str, budget: Any) -> Reply:
    """POST *body* as JSON with a bearer *key* inside *budget*; never raises, never follows."""
    if budget.remaining() <= 0:
        return _failure(TIMEOUT)
    clean = _clean_key(key)
    if clean is None or clean != key:
        return _failure(TRANSPORT)
    call = Call(budget)
    try:
        data = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        request = urllib.request.Request(url, data=data, method="POST")
        request.add_header("Content-Type", "application/json")
        request.add_unredirected_header("Authorization", f"Bearer {key}")
        opener = make_opener(call)
        call.arm()
        reply = _exchange(opener, request, call)
    except Exception as err:  # pylint: disable=broad-except
        if call.fired or budget.remaining() <= 0 or _is_timeout(err):
            return _failure(TIMEOUT)
        return _failure(TRANSPORT)
    finally:
        call.close()
    return _failure(TIMEOUT) if call.fired else reply
