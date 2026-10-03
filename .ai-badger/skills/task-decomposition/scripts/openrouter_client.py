"""The memory-context pipeline's one network surface: JSON POSTs to OpenRouter under one deadline.

No ambient proxy, no redirect, verified TLS, and no host but loopback unless the session opts
in; a locked project relaxes only for the `https` hosts its `allowHosts` lists. An `https` POST
tunnels through the `http` proxy the passed env names, never for loopback; a proxy failure leaves
one closed token in `PROXY_FAULTS`, never the proxy value. The deadline covers
DNS, every connect attempt, the proxy CONNECT, the TLS handshake and the reply; the key comes from
the environment and appears only in one header, inside the tunnel.
"""
from __future__ import annotations

import http.client
import ipaddress
import json
import os
import re
import socket
import ssl
import stat
import threading
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any, Dict, FrozenSet, List, Mapping, NamedTuple, Optional, Tuple

PRODUCTION_BASE = "https://openrouter.ai"
TEST_BASE_ENV = "AI_BADGER_JEV_TEST_OPENROUTER_BASE"
TEST_KEY_PREFIX = "sk-test-"
ALLOW_ENV = "AI_BADGER_ALLOW_THIRD_PARTY"
HTTPS_PROXY_LOWER = "https_proxy"
HTTPS_PROXY_UPPER = "HTTPS_PROXY"
NO_PROXY_LOWER = "no_proxy"
NO_PROXY_UPPER = "NO_PROXY"
LOCAL_ONLY = "local-only"
LOOPBACK = "127.0.0.1"
LOOPBACK_HOSTS = ("127.0.0.1", "localhost", "::1")
BODY_MAX = 1024 * 1024
CONFIG_MAX = 1 << 20
READ_CHUNK = 64 * 1024
TIMEOUT = "timeout"
TRANSPORT = "transport"
EGRESS_REFUSED = "egress-refused"
# An allowHosts entry: ASCII LDH labels, at least one dot, a last label that starts with a letter
# (no dotted or `0x` IPv4 shorthand), an optional trailing dot. The schema carries the same
# string; the lookahead keeps `$` from accepting a trailing newline under jsonschema's `re.search`.
HOST_PATTERN = (r"^(?!.*\n)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+"
                r"[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.?$")
# A proxy host name: the same LDH labels, but a single label (`proxy`) is allowed.
PROXY_HOST_PATTERN = (r"(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*"
                      r"[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?")
MALFORMED = "malformed"
UNSUPPORTED = "unsupported"
TLS_VERIFY = "tls-verify"
TUNNEL_STATUS = re.compile(r"Tunnel connection failed: ([0-9]{3})(?![0-9])")
POLICY = "dataPolicy"
LIST_IGNORED = ": list ignored)"
UNRESOLVABLE = "unresolvable cwd"
INTERNAL_ERROR = "internal-error"
DNS_THREAD = "ai-badger-openrouter-dns"
WATCHDOG_THREAD = "ai-badger-openrouter-watchdog"


class Reply(NamedTuple):
    """Status, lower-cased headers and body; on failure status 0 and `error` `timeout`,
    `transport` or `egress-refused`."""

    status: int
    headers: Dict[str, str]
    body: bytes
    error: Optional[str] = None


class Lock(NamedTuple):
    """One locking config: its path (`None` when the cwd itself could not be walked), a fixed
    cause token, and its valid `allowHosts` (`None` when the list is absent or void)."""

    path: Optional[str]
    cause: str
    hosts: Optional[FrozenSet[str]]

    def reason(self) -> str:
        """`<path>: <cause>`, or the bare cause when there is no path."""
        return self.cause if self.path is None else f"{self.path}: {self.cause}"


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


def is_loopback(url: str) -> bool:
    """True only for an http(s) URL with no userinfo whose host is 127.0.0.1, localhost or ::1."""
    try:
        parts = urllib.parse.urlsplit(url)
        _ = parts.port
    except (ValueError, TypeError, AttributeError):
        return False
    return (parts.scheme in ("http", "https") and "@" not in parts.netloc
            and parts.username is None and parts.hostname in LOOPBACK_HOSTS)


def _config_lock(path: str) -> Tuple[Optional[str], Optional[FrozenSet[str]]]:
    """The cause token when the config at *path* locks, and its valid `allowHosts` (`None` when
    absent or void); `(None, None)` when it is absent or an object without `dataPolicy`. A config
    that cannot be read is `unreadable:<invalid-json|too-large|io|not-object>`; a FIFO or
    directory is never read."""
    try:
        os.lstat(path)
    except (FileNotFoundError, NotADirectoryError):
        return None, None
    except OSError:
        return "unreadable:io", None
    try:
        if not stat.S_ISREG(os.stat(path).st_mode):
            return "unreadable:io", None
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NONBLOCK", 0))
        with os.fdopen(descriptor, "rb") as handle:
            if not stat.S_ISREG(os.fstat(handle.fileno()).st_mode):
                return "unreadable:io", None
            raw = handle.read(CONFIG_MAX + 1)
    except OSError:
        return "unreadable:io", None
    if len(raw) > CONFIG_MAX:
        return "unreadable:too-large", None
    try:
        data = json.loads(raw.decode("utf-8"))
    except (ValueError, RecursionError):
        return "unreadable:invalid-json", None
    if not isinstance(data, dict):
        return "unreadable:not-object", None
    if POLICY not in data:
        return None, None
    return _policy_hosts(data[POLICY])


def _policy_hosts(policy: Any) -> Tuple[str, Optional[FrozenSet[str]]]:
    """The cause token for *policy* and its normalised `allowHosts`; a malformed object names
    what voided the list (by entry index, never by entry text) and allows nothing."""
    if not isinstance(policy, dict):
        return POLICY, None
    if set(policy) - {"mode", "allowHosts"}:
        return f"{POLICY} (unknown key)", None
    if policy.get("mode") != LOCAL_ONLY:
        return f"{POLICY} (mode invalid)", None
    if "allowHosts" not in policy:
        return f"{POLICY} (allowHosts missing{LIST_IGNORED}", None
    hosts = policy["allowHosts"]
    if not isinstance(hosts, list):
        return f"{POLICY} (allowHosts not a list{LIST_IGNORED}", None
    for index, host in enumerate(hosts):
        if not (isinstance(host, str) and re.fullmatch(HOST_PATTERN, host)):
            return f"{POLICY} (allowHosts invalid at entry #{index}{LIST_IGNORED}", None
    if len(set(hosts)) != len(hosts):
        return f"{POLICY} (allowHosts duplicate{LIST_IGNORED}", None
    return POLICY, frozenset(_normal_host(host) for host in hosts)


def _normal_host(host: str) -> str:
    host = host.lower()
    return host[:-1] if host.endswith(".") else host


def _starts(cwd: str) -> List[Path]:
    """*cwd* as given and resolved, plus the shell's logical `PWD` when it names the same place."""
    starts = [Path(os.path.abspath(cwd)), Path(cwd).resolve()]
    logical = os.environ.get("PWD")
    if logical and os.path.isabs(logical) and Path(logical).resolve() == starts[1]:
        starts.append(Path(logical))
    return starts


def _locks(cwd: str) -> List[Lock]:
    """Every locking config above *cwd* (as given, resolved, or through `PWD`), nearest first;
    a cwd that cannot be resolved, or any other failure, is itself one path-less lock."""
    try:
        starts = _starts(cwd)
    except (OSError, ValueError, RuntimeError):
        return [Lock(None, UNRESOLVABLE, None)]
    try:
        locks: List[Lock] = []
        seen = set()
        for start in starts:
            for folder in (start, *start.parents):
                if folder in seen:
                    continue
                seen.add(folder)
                path = os.path.join(folder, ".ai-badger", "config.json")
                cause, hosts = _config_lock(path)
                if cause is not None:
                    locks.append(Lock(path, cause, hosts))
        return locks
    except Exception:  # pylint: disable=broad-except
        return [Lock(None, INTERNAL_ERROR, None)]


def lock_reason(cwd: str) -> Optional[str]:
    """`<config path>: <cause>` for the nearest `.ai-badger/config.json` above *cwd* (as given,
    resolved, or through `PWD`) that locks the project, `unresolvable cwd`, `internal-error`, or
    `None`."""
    locks = _locks(cwd)
    return locks[0].reason() if locks else None


def project_locked(cwd: str) -> bool:
    """True when any `.ai-badger/config.json` above *cwd* locks the project; never raises."""
    return bool(_locks(cwd))


def allowed_hosts(cwd: str) -> FrozenSet[str]:
    """The hosts every locking config above *cwd* allowlists; a lock without a valid
    `allowHosts` contributes the empty set."""
    hosts: Optional[FrozenSet[str]] = None
    for lock in _locks(cwd):
        allowed = lock.hosts or frozenset()
        hosts = allowed if hosts is None else hosts & allowed
    return hosts or frozenset()


def denying_lock(url: str, cwd: str) -> Optional[Lock]:
    """The nearest lock above *cwd* whose `allowHosts` does not admit the host *url* dials (or
    that has no valid list); `None` when every lock admits it."""
    host = dialled_host(url)
    for lock in _locks(cwd):
        if host is None or lock.hosts is None or host not in lock.hosts:
            return lock
    return None


def opted_in(env: Mapping[str, str]) -> bool:
    """`AI_BADGER_ALLOW_THIRD_PARTY` is exactly `1`."""
    return env.get(ALLOW_ENV) == "1"


def third_party_allowed(env: Mapping[str, str], cwd: str) -> bool:
    """The session opted in and no config above *cwd* locks the project."""
    return opted_in(env) and not project_locked(cwd)


def _screened(url: str) -> bool:
    """True when *url* holds whitespace, a control or format character, or `\\`."""
    return any(ch.isspace() or ch == "\\" or unicodedata.category(ch) in ("Cc", "Cf")
               for ch in url)


def dialled_host(url: str) -> Optional[str]:
    """The host a request to *url* dials, lower-cased without port or trailing dot; `None` when
    the URL holds whitespace, a control character or `\\`, or `urlsplit` would name another."""
    if _screened(url):
        return None
    try:
        host = urllib.request.Request(url).host
        named = urllib.parse.urlsplit(url).hostname
    except (ValueError, TypeError, AttributeError):
        return None
    if not host:
        return None
    if host.rfind(":") > host.rfind("]"):
        host, _, port = host.rpartition(":")
        if not (port.isascii() and port.isdigit()):
            return None
    if host.startswith("[") and host.endswith("]"):
        host = host[1:-1]
    host = _normal_host(host)
    return host if host and named is not None and host == _normal_host(named) else None


def egress_allowed(url: str, env: Mapping[str, str], cwd: str) -> bool:
    """Whether a POST to *url* may leave: loopback always; otherwise `https` to a well-formed
    host on opt-in, and from a locked *cwd* only to a host every lock's `allowHosts` lists. A URL
    holding whitespace, a control or format character or `\\` is refused, loopback included."""
    if _screened(url):
        return False
    if is_loopback(url):
        return True
    host = dialled_host(url)
    if host is None or urllib.parse.urlsplit(url).scheme != "https" or not opted_in(env):
        return False
    return denying_lock(url, cwd) is None


PROXY_FAULTS: set = set()


def take_proxy_faults() -> set:
    """Return and clear the proxy fault tokens recorded since the last call: `malformed`,
    `unsupported`, `connect-<status>` or `tls-verify`."""
    taken = set(PROXY_FAULTS)
    PROXY_FAULTS.difference_update(taken)
    return taken


class _BadProxy(ValueError):
    """The https proxy variable is refused; `cause` is `malformed` or `unsupported` (credentials
    or a scheme other than http), and the value is never echoed."""

    def __init__(self, cause: str):
        super().__init__(f"{cause} proxy")
        self.cause = cause


def _present(lower: Optional[str], upper: Optional[str]) -> str:
    """The lowercase value when present (even blank), else the uppercase one, else `""`."""
    return (lower if lower is not None else upper) or ""


def _proxy_host(host: Optional[str]) -> Optional[str]:
    """*host* as it goes in `host:port` (IPv6 bracketed), or `None` unless it is an IP address
    or LDH host name."""
    if not host or "%" in host:
        return None
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        return host if re.fullmatch(PROXY_HOST_PATTERN, host) else None
    return f"[{host}]" if address.version == 6 else host


def _proxy_address(value: str) -> str:
    """`host:port` of an `http://host:port[/]` or bare `host:port` proxy value; raises
    `_BadProxy` for anything else."""
    if _screened(value) or "?" in value or "#" in value:
        raise _BadProxy(MALFORMED)
    try:
        parts = urllib.parse.urlsplit(value if "://" in value else "http://" + value)
        host = _proxy_host(parts.hostname)
    except ValueError:
        raise _BadProxy(MALFORMED) from None
    if not parts.scheme:
        raise _BadProxy(MALFORMED)
    if "@" in parts.netloc or parts.scheme != "http":
        raise _BadProxy(UNSUPPORTED)
    try:
        port = parts.port
    except ValueError:
        raise _BadProxy(MALFORMED) from None
    if host is None or not port or parts.path not in ("", "/"):
        raise _BadProxy(MALFORMED)
    return f"{host}:{port}"


def proxy_for(url: str, env: Mapping[str, str]) -> Optional[str]:
    """`host:port` of the `http` proxy an `https` *url* goes through, from *env* alone
    (`https_proxy` over `HTTPS_PROXY`, minus `no_proxy` over `NO_PROXY`); `None` for loopback,
    plain http, a blank value or a bypassed host. Raises `ValueError` (with a `cause`) for a
    refused proxy, even for a bypassed host."""
    if is_loopback(url) or urllib.parse.urlsplit(url).scheme != "https":
        return None
    value = _present(env.get(HTTPS_PROXY_LOWER), env.get(HTTPS_PROXY_UPPER))
    if not value.strip():
        return None
    address = _proxy_address(value)
    bypass = _present(env.get(NO_PROXY_LOWER), env.get(NO_PROXY_UPPER))
    host = dialled_host(url)
    if host is None or (bypass.strip() and urllib.request.proxy_bypass_environment(
            host, {"no": bypass})):
        return None
    return address


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
            if self._tunnel_host:
                self.sock = sock
                self._tunnel()
                sock.settimeout(self.call.left())
            self.sock = self.tls.wrap_socket(sock,
                                             server_hostname=self._tunnel_host or self.host)
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


def _proxy_fault(err: BaseException) -> Optional[str]:
    """The closed token for a proxied request's failure, or `None`; only the status digits of a
    refused CONNECT are read, never its reason phrase."""
    reason = getattr(err, "reason", err)
    if isinstance(reason, ssl.SSLCertVerificationError):
        return TLS_VERIFY
    match = TUNNEL_STATUS.match(str(reason)) if isinstance(reason, OSError) else None
    return f"connect-{match.group(1)}" if match else None


def _is_timeout(err: BaseException) -> bool:
    reason = getattr(err, "reason", None)
    return isinstance(err, (TimeoutError, socket.timeout)) or isinstance(
        reason, (TimeoutError, socket.timeout))


def post_json(url: str, body: Any, key: Optional[str], budget: Any, *,
              env: Optional[Mapping[str, str]] = None, cwd: Optional[str] = None) -> Reply:
    """POST *body* as JSON inside *budget*, bearer *key* when given; never raises, never follows.
    A non-loopback *url* is refused before any dial unless it is https and *env* opts in from a
    *cwd* that is unlocked or whose every lock lists its host in `allowHosts`."""
    if not egress_allowed(url, env if env is not None else {}, cwd if cwd is not None else "."):
        return _failure(EGRESS_REFUSED)
    try:
        proxy = proxy_for(url, env if env is not None else {})
    except _BadProxy as err:
        PROXY_FAULTS.add(err.cause)
        return _failure(TRANSPORT)
    if budget.remaining() <= 0:
        return _failure(TIMEOUT)
    if key is not None and _clean_key(key) != key:
        return _failure(TRANSPORT)
    call = Call(budget)
    try:
        data = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        request = urllib.request.Request(url, data=data, method="POST")
        request.add_header("Content-Type", "application/json")
        if key is not None:
            request.add_unredirected_header("Authorization", f"Bearer {key}")
        if proxy is not None:
            request.add_unredirected_header("Host", request.host)
            request.set_proxy(proxy, "https")
        opener = make_opener(call)
        call.arm()
        reply = _exchange(opener, request, call)
    except Exception as err:  # pylint: disable=broad-except
        if call.fired or budget.remaining() <= 0 or _is_timeout(err):
            return _failure(TIMEOUT)
        fault = _proxy_fault(err) if proxy is not None else None
        if fault is not None:
            PROXY_FAULTS.add(fault)
        return _failure(TRANSPORT)
    finally:
        call.close()
    return _failure(TIMEOUT) if call.fired else reply
