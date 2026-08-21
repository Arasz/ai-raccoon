"""Scratch-server lifecycle for the retrieval-tuning harness (plan §8).

`ai-raccoon --data-root <root> serve --port 0` is backgrounded; the bound port
is parsed back from the serve log; the bearer token is read from
<root>/mcp-token; readiness is proven by the MCP initialize handshake; teardown
is SIGTERM + wait. SAFETY ASSERTS (unit-tested): the bound port is never 7721
and the data-root is never the live bank root (~/.ai-raccoon) — neither equal,
nor inside it, nor an ancestor of it.
"""

from __future__ import annotations

import re
import subprocess
import time
from pathlib import Path
from typing import Optional

from .mcp import MCPClient, read_token

LIVE_DATA_ROOT = Path.home() / ".ai-raccoon"
FORBIDDEN_PORT = 7721

_PORT_URL_RE = re.compile(r"http://[^:/]*:(\d+)/mcp")
_PORT_LISTENING_RE = re.compile(r"Now listening on: http://[^:/]*:(\d+)")
_BIND_TIMEOUT_SECONDS = 90.0
_STOP_TIMEOUT_SECONDS = 15.0


class SafetyViolation(RuntimeError):
    """The harness refused an action that could touch the live bank or the live server."""


class PortNotFoundError(RuntimeError):
    """The serve log does not yet carry a bound-port line."""


class ServerStartError(RuntimeError):
    """The scratch server failed to start, or failed its readiness checks."""


def assert_port_not_7721(port: int) -> None:
    """C2: port 7721 (the user's live server) must never be bound or dialed by the harness."""
    if int(port) == FORBIDDEN_PORT:
        raise SafetyViolation(
            f"refusing port {FORBIDDEN_PORT}: that is the live server's port (PID 5537) — "
            f"scratch servers must use --port 0 ephemeral binds"
        )


def assert_safe_data_root(data_root) -> None:
    """C1: the data-root must not be, sit inside, or contain the live bank root."""
    root = Path(data_root).resolve()
    live = LIVE_DATA_ROOT.resolve()
    if root == live:
        raise SafetyViolation(f"data-root {root} is the live bank root — refusing to touch it")
    if root == live / "memory.db":
        raise SafetyViolation(f"data-root {root} is the live bank database file — refusing")
    if live in root.parents:
        raise SafetyViolation(f"data-root {root} is inside the live bank root {live} — refusing")
    if root in live.parents:
        raise SafetyViolation(f"data-root {root} is an ancestor of the live bank root {live} — refusing")


def parse_bound_port(log_text: str) -> int:
    """The bound port from the serve log; the LAST bound-url line wins. Raises PortNotFoundError."""
    for pattern in (_PORT_URL_RE, _PORT_LISTENING_RE):
        matches = list(pattern.finditer(log_text))
        if matches:
            return int(matches[-1].group(1))
    raise PortNotFoundError("serve log carries no bound-port line yet")


class ScratchServer:
    """A running scratch server handle. Context-manager friendly; stop() = SIGTERM + wait."""

    def __init__(self, data_root: Path, port: int, token: str, proc, log_path: Path,
                 binary: str, client: MCPClient) -> None:
        self.data_root = Path(data_root)
        self.port = int(port)
        self.token = token
        self.proc = proc
        self.log_path = Path(log_path)
        self.binary = binary
        self.client = client

    @property
    def base_url(self) -> str:
        return f"http://127.0.0.1:{self.port}/mcp"

    def search(self, corpus_entry: dict) -> list[dict]:
        """One memory_search for a corpus entry — settings-driven (no tuning args).

        `kind` (plan §12.2 H8) is read from the entry and forwarded only when
        present; entries with no 'kind' key pass no kind kwarg at all, so an
        ordinary memory eval-set's call shape is byte-for-byte unchanged.
        """
        kwargs: dict = {
            "project_id": corpus_entry.get("targetProjectId") or "ai-raccoon",
            "query": corpus_entry["query"],
            "scope": corpus_entry.get("targetScope") or "all",
            "limit": int(corpus_entry.get("searchLimit") or 5),
            "min_relative_score": 0.0,
        }
        if corpus_entry.get("kind") is not None:
            kwargs["kind"] = corpus_entry["kind"]
        return self.client.memory_search(**kwargs)

    def stop(self) -> None:
        if self.proc is None or self.proc.poll() is not None:
            return
        self.proc.terminate()  # SIGTERM
        try:
            self.proc.wait(timeout=_STOP_TIMEOUT_SECONDS)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait(timeout=_STOP_TIMEOUT_SECONDS)

    def __enter__(self) -> "ScratchServer":
        return self

    def __exit__(self, exc_type, exc, tb) -> bool:
        self.stop()
        return False


def start_server(
    data_root,
    binary: str = "ai-raccoon",
    log_path: Optional[Path] = None,
    skip_health_check: bool = False,
    start_timeout: float = _BIND_TIMEOUT_SECONDS,
) -> ScratchServer:
    """Start one scratch server (--port 0), read its port + token, prove readiness."""
    root = Path(data_root)
    assert_safe_data_root(root)  # raises BEFORE anything is spawned
    root.mkdir(parents=True, exist_ok=True)
    log_path = Path(log_path) if log_path is not None else root / "serve.log"

    argv = [binary, "--data-root", str(root), "serve", "--port", "0"]
    with open(log_path, "wb") as log_file:
        proc = subprocess.Popen(argv, stdout=log_file, stderr=subprocess.STDOUT)

    deadline = time.monotonic() + start_timeout
    port: Optional[int] = None
    log_text = ""
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            tail = log_path.read_text(errors="replace")[-2000:]
            raise ServerStartError(
                f"serve exited early (code {proc.returncode}):\n{tail}"
            )
        log_text = log_path.read_text(errors="replace")
        try:
            port = parse_bound_port(log_text)
            break
        except PortNotFoundError:
            time.sleep(0.25)

    if port is None:
        proc.terminate()
        raise ServerStartError(
            f"serve did not report a bound port within {start_timeout:.0f}s:\n{log_text[-2000:]}"
        )
    assert_port_not_7721(port)

    token = _read_token_with_retry(root, deadline)
    client = MCPClient(f"http://127.0.0.1:{port}/mcp", token=token)
    if not skip_health_check:
        try:
            client.initialize()
        except Exception as exc:  # noqa: BLE001 — any handshake failure means not ready
            proc.terminate()
            try:
                proc.wait(timeout=_STOP_TIMEOUT_SECONDS)
            except subprocess.TimeoutExpired:
                proc.kill()
            raise ServerStartError(
                f"MCP initialize handshake failed on port {port}: {exc}"
            ) from exc
    return ScratchServer(root, port, token, proc, log_path, binary, client)


def _read_token_with_retry(root: Path, deadline: float) -> str:
    """The token file appears with the bind; give it a moment before giving up."""
    while True:
        try:
            return read_token(root)
        except FileNotFoundError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.25)
