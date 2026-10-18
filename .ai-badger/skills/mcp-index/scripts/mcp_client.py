#!/usr/bin/env python3
"""A stdlib-only MCP client that asks one server for its tools: `list_tools(spec, timeout)`.

`spec` is a server entry as Claude Code's config files write it — `{command, args, env}` for
stdio, `{type: "http", url, headers}` for streamable HTTP. Both run initialize, then
`notifications/initialized`, then `tools/list` following `nextCursor`, under one deadline. Every
failure is an `McpClientError` whose message is one line a human can act on. No OAuth: a server
that answers 401/403 raises `Unauthorized`.
"""

from __future__ import annotations

import json
import os
import queue
import re
import signal
import subprocess
import threading
import time
import urllib.error
import urllib.request
from collections import deque
from typing import Any, Callable, Dict, List, Mapping, Optional

PROTOCOL_VERSION = "2025-06-18"
CLIENT_INFO = {"name": "ai-badger-mcp-index", "version": "1"}
# A server whose cursor never ends would otherwise page forever.
PAGE_CAP = 50
# Seconds a stdio server gets to exit after stdin closes, and again after terminate().
GRACE_SECONDS = 1.0
# One stdio line, HTTP body or SSE event larger than this is refused, not buffered.
MAX_MESSAGE_BYTES = 16 * 1024 * 1024
POSIX = os.name == "posix"

Tool = Dict[str, str]
Request = Callable[[str, Dict[str, Any]], Dict[str, Any]]
Notify = Callable[[str], None]


class McpClientError(RuntimeError):
    """The server could not be enumerated; the message is one line naming why."""


class Timeout(McpClientError):
    """The deadline passed before the last tools/list page arrived."""


class Unauthorized(McpClientError):
    """The server refused the request without credentials this client does not hold."""


def _too_large() -> McpClientError:
    return McpClientError(f"response too large (over {MAX_MESSAGE_BYTES} bytes)")


_TOO_LARGE = b"\0too-large"


# ── ${VAR} expansion ─────────────────────────────────────────────────────────

_VAR = re.compile(r"\$\{(?P<name>[A-Za-z_][A-Za-z0-9_]*)(?::-(?P<default>[^}]*))?\}")


def expand(value: str, env: Mapping[str, str]) -> str:
    """Expand `${VAR}` and `${VAR:-default}`; an unset variable with no default becomes ''."""
    def one(match):
        found = env.get(match.group("name"))
        if found:
            return found
        return match.group("default") or ""
    return _VAR.sub(one, value)


def _one_line(text: str, limit: int = 200) -> str:
    """Collapse whitespace so a reason never spans lines."""
    flat = " ".join(str(text).split())
    return flat if len(flat) <= limit else flat[:limit - 1] + "…"


# ── the protocol, independent of transport ───────────────────────────────────

def _initialize_params() -> Dict[str, Any]:
    return {"protocolVersion": PROTOCOL_VERSION, "capabilities": {}, "clientInfo": CLIENT_INFO}


def _enumerate(request: Request, notify: Notify) -> List[Tool]:
    """initialize → initialized → every tools/list page. Any older negotiated version is fine."""
    request("initialize", _initialize_params())
    notify("notifications/initialized")
    tools: List[Tool] = []
    cursor: Optional[str] = None
    for _ in range(PAGE_CAP):
        result = request("tools/list", {"cursor": cursor} if cursor else {})
        for tool in result.get("tools") or []:
            if isinstance(tool, dict) and tool.get("name"):
                tools.append({"name": str(tool["name"]),
                              "description": str(tool.get("description") or "")})
        cursor = result.get("nextCursor")
        if not cursor:
            return tools
    raise McpClientError(f"tools/list still paginating after {PAGE_CAP} pages")


def _result_of(method: str, message: Dict[str, Any]) -> Dict[str, Any]:
    """The `result` of a response, or the JSON-RPC error as an McpClientError."""
    if "error" in message:
        error = message["error"] if isinstance(message["error"], dict) else {}
        raise McpClientError(f"{method} failed: {_one_line(error.get('message', error))}")
    result = message.get("result")
    return result if isinstance(result, dict) else {}


def _reply_to_server_request(message: Dict[str, Any]) -> Dict[str, Any]:
    """`ping` gets `{}`; anything else the server asks gets -32601 — this client offers nothing."""
    if message.get("method") == "ping":
        return {"jsonrpc": "2.0", "id": message["id"], "result": {}}
    return {"jsonrpc": "2.0", "id": message["id"],
            "error": {"code": -32601, "message": "method not supported by this client"}}


# ── stdio ────────────────────────────────────────────────────────────────────

class _Stdio:
    """One spawned server: newline-delimited JSON-RPC on stdin/stdout, stderr only kept."""

    def __init__(self, spec: Dict[str, Any], deadline: float, cwd: Optional[str]):
        env = dict(os.environ)
        env.update({k: expand(str(v), os.environ) for k, v in (spec.get("env") or {}).items()})
        command = expand(str(spec.get("command") or ""), env)
        argv = [command] + [expand(str(a), env) for a in spec.get("args") or []]
        self.deadline = deadline
        self.next_id = 0
        self.stdout: "queue.Queue[Optional[bytes]]" = queue.Queue()
        self.stderr: deque = deque(maxlen=20)
        self.limit = MAX_MESSAGE_BYTES
        # Its own process group, so close() reaches what an npx/uvx launcher forks.
        group = ({"start_new_session": True} if POSIX
                 else {"creationflags": getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)})
        try:
            self.proc = subprocess.Popen(  # pylint: disable=consider-using-with
                argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                env=env, cwd=cwd or None, **group)
        except FileNotFoundError as exc:
            raise McpClientError(f"command not found: {command}") from exc
        except OSError as exc:
            raise McpClientError(f"could not start {command}: {_one_line(exc)}") from exc
        threading.Thread(target=self._pump_stdout, daemon=True).start()
        threading.Thread(target=self._pump_stderr, daemon=True).start()

    def _pump_stdout(self) -> None:
        while True:
            line = self.proc.stdout.readline(self.limit + 1)
            if not line:
                break
            if len(line) > self.limit:
                self.stdout.put(_TOO_LARGE)
                break
            self.stdout.put(line)
        self.stdout.put(None)

    def _pump_stderr(self) -> None:
        for line in iter(self.proc.stderr.readline, b""):
            text = line.decode("utf-8", "replace").strip()
            if text:
                self.stderr.append(text)

    def _send(self, message: Dict[str, Any]) -> None:
        try:
            self.proc.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
            self.proc.stdin.flush()
        except (BrokenPipeError, OSError, ValueError):
            pass  # the read loop reports the exit with its stderr reason

    def _exited(self) -> McpClientError:
        try:
            code = self.proc.wait(timeout=max(0.0, min(GRACE_SECONDS,
                                                       self.deadline - time.monotonic())))
        except subprocess.TimeoutExpired:
            code = None
        time.sleep(0.05)  # let the stderr pump catch the last line
        last = self.stderr[-1] if self.stderr else "no stderr"
        return McpClientError(f"server exited (exit {code}) before answering: {_one_line(last)}")

    def notify(self, method: str) -> None:
        """Send a notification; nothing comes back."""
        self._send({"jsonrpc": "2.0", "method": method})

    def request(self, method: str, params: Dict[str, Any]) -> Dict[str, Any]:
        """Send one request and wait for the response with its id, serving the server meanwhile."""
        self.next_id += 1
        wanted = self.next_id
        self._send({"jsonrpc": "2.0", "id": wanted, "method": method, "params": params})
        while True:
            remaining = self.deadline - time.monotonic()
            if remaining <= 0:
                raise Timeout(f"timed out waiting for {method}")
            try:
                line = self.stdout.get(timeout=remaining)
            except queue.Empty as exc:
                raise Timeout(f"timed out waiting for {method}") from exc
            if line is None:
                raise self._exited()
            if line is _TOO_LARGE:
                raise _too_large()
            try:
                message = json.loads(line)
            except ValueError:
                continue  # a banner or log line on stdout is not protocol
            if not isinstance(message, dict):
                continue
            if "method" in message:
                if "id" in message:
                    self._send(_reply_to_server_request(message))
                continue  # a notification
            if message.get("id") == wanted:
                return _result_of(method, message)

    def _signal(self, sig: int) -> None:
        """Signal the whole process group on POSIX; the direct child elsewhere."""
        try:
            if POSIX:
                os.killpg(self.proc.pid, sig)
            elif sig == getattr(signal, "SIGKILL", None):
                self.proc.kill()
            else:
                self.proc.terminate()
        except OSError:
            pass  # already gone

    def close(self) -> None:
        """Close stdin, then terminate, then kill the group — nothing outlives the call."""
        try:
            self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(timeout=GRACE_SECONDS)
        except subprocess.TimeoutExpired:
            self._signal(signal.SIGTERM)
            try:
                self.proc.wait(timeout=GRACE_SECONDS)
            except subprocess.TimeoutExpired:
                pass
        # Grandchildren may outlive a child that exited cleanly; the group kill reaches them.
        self._signal(getattr(signal, "SIGKILL", signal.SIGTERM))
        try:
            self.proc.wait(timeout=GRACE_SECONDS)
        except subprocess.TimeoutExpired:
            pass


# ── streamable HTTP ──────────────────────────────────────────────────────────

class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """Config headers belong to the config's own URL; a redirect is refused, never followed."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):  # pylint: disable=too-many-arguments,too-many-positional-arguments
        raise McpClientError(f"HTTP {code} redirect to {newurl}; not followed")


class _Http:
    """Streamable HTTP: one POST per message, JSON or SSE replies, session id carried."""

    def __init__(self, spec: Dict[str, Any], deadline: float):
        self.url = expand(str(spec.get("url") or ""), os.environ)
        self.headers = {str(k): expand(str(v), os.environ)
                        for k, v in (spec.get("headers") or {}).items()}
        self.deadline = deadline
        self.next_id = 0
        self.session: Optional[str] = None
        self.version: Optional[str] = None
        self.opener = urllib.request.build_opener(_NoRedirect)

    def _remaining(self, method: str) -> float:
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise Timeout(f"timed out waiting for {method}")
        return remaining

    def _headers(self) -> Dict[str, str]:
        headers = dict(self.headers)
        headers.update({"Content-Type": "application/json",
                        "Accept": "application/json, text/event-stream"})
        if self.session:
            headers["Mcp-Session-Id"] = self.session
        if self.version:
            headers["MCP-Protocol-Version"] = self.version
        return headers

    def _open(self, method: str, http_method: str, body: Optional[bytes]):
        req = urllib.request.Request(self.url, data=body, headers=self._headers(),
                                     method=http_method)
        try:
            return self.opener.open(req, timeout=self._remaining(method))
        except urllib.error.HTTPError as exc:
            if exc.code in (401, 403):
                raise Unauthorized(f"needs authorization (HTTP {exc.code})") from exc
            raise McpClientError(f"{method}: HTTP {exc.code} {_one_line(exc.reason)}") from exc
        except TimeoutError as exc:
            raise Timeout(f"timed out waiting for {method}") from exc
        except urllib.error.URLError as exc:
            if isinstance(exc.reason, TimeoutError):
                raise Timeout(f"timed out waiting for {method}") from exc
            raise McpClientError(f"{method}: {_one_line(exc.reason)}") from exc

    def _post(self, label: str, message: Dict[str, Any]) -> None:
        """POST a message that expects no reply (a notification or a response); 202 back."""
        with self._open(label, "POST", json.dumps(message).encode("utf-8")):
            pass

    def notify(self, method: str) -> None:
        """POST a notification; the server answers 202 with no body."""
        self._post(method, {"jsonrpc": "2.0", "method": method})

    def request(self, method: str, params: Dict[str, Any]) -> Dict[str, Any]:
        """POST one request; read the JSON body or the SSE stream until the matching id."""
        self.next_id += 1
        wanted = self.next_id
        body = json.dumps({"jsonrpc": "2.0", "id": wanted, "method": method,
                           "params": params}).encode("utf-8")
        with self._open(method, "POST", body) as response:
            self.session = response.headers.get("Mcp-Session-Id") or self.session
            ctype = response.headers.get("Content-Type", "")
            try:
                if "text/event-stream" in ctype:
                    message = self._read_sse(method, response, wanted)
                else:
                    raw = response.read(MAX_MESSAGE_BYTES + 1)
                    if len(raw) > MAX_MESSAGE_BYTES:
                        raise _too_large()
                    message = json.loads(raw or b"{}")
            except TimeoutError as exc:
                raise Timeout(f"timed out waiting for {method}") from exc
            except ValueError as exc:
                raise McpClientError(f"{method}: unreadable reply ({_one_line(exc)})") from exc
        if isinstance(message, list):
            message = next((m for m in message if isinstance(m, dict)
                            and m.get("id") == wanted), {})
        result = _result_of(method, message if isinstance(message, dict) else {})
        if method == "initialize":
            self.version = str(result.get("protocolVersion") or PROTOCOL_VERSION)
        return result

    def _read_sse(self, method: str, response, wanted: int) -> Dict[str, Any]:
        """Events until the reply with `wanted`; server requests on the stream are answered."""
        data: List[str] = []
        size = 0
        while True:
            self._remaining(method)
            raw = response.readline(MAX_MESSAGE_BYTES + 1)
            size += len(raw)
            if size > MAX_MESSAGE_BYTES:
                raise _too_large()
            line = raw.decode("utf-8", "replace").rstrip("\r\n")
            if line.startswith("data:"):
                data.append(line[5:].lstrip())
            elif not line and data:  # a blank line, or the end of the stream, ends an event
                message = self._event(method, "\n".join(data), wanted)
                data, size = [], 0
                if message is not None:
                    return message
            if not raw:
                break
        raise McpClientError(f"{method}: event stream ended without a reply")

    def _event(self, method: str, text: str, wanted: int) -> Optional[Dict[str, Any]]:
        """The reply if this event is it; a server request is answered, anything else skipped."""
        try:
            message = json.loads(text)
        except ValueError:
            return None
        if not isinstance(message, dict):
            return None
        if "method" in message:
            if "id" in message:
                self._post(method, _reply_to_server_request(message))
            return None
        return message if message.get("id") == wanted else None

    def close(self) -> None:
        """Best-effort DELETE of the session; a refusal or a dead server is not an error."""
        if not self.session:
            return
        req = urllib.request.Request(self.url, headers=self._headers(), method="DELETE")
        try:
            with self.opener.open(req, timeout=2):
                pass
        except (McpClientError, OSError, ValueError):
            pass


# ── entry point ──────────────────────────────────────────────────────────────

def transport_of(spec: Dict[str, Any]) -> str:
    """`stdio`, `http` or whatever else the config names (`sse`, ...), lower-cased."""
    kind = str(spec.get("type") or "").lower()
    if kind in ("", "stdio"):
        return "http" if spec.get("url") and not spec.get("command") else "stdio"
    return "http" if kind in ("http", "streamable-http", "streamable_http") else kind


def list_tools(spec: Dict[str, Any], timeout: float,
               cwd: Optional[str] = None) -> List[Tool]:
    """Every tool the server reports, as `{name, description}`; raises McpClientError."""
    deadline = time.monotonic() + timeout
    kind = transport_of(spec)
    if kind == "stdio":
        session: Any = _Stdio(spec, deadline, cwd)
    elif kind == "http":
        session = _Http(spec, deadline)
    else:
        raise McpClientError(f"transport {kind!r} is not supported (stdio and http only)")
    try:
        return _enumerate(session.request, session.notify)
    finally:
        session.close()
