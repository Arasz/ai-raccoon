"""MCP client for the AiRaccoon memory server.

Two transports behind one duck-typed surface:

- ``stdio`` (default): spawns the ``ai-raccoon`` binary as a child process
  (MCP over stdio). Since ADR-0020 that child is a proxy that relays to an
  ``ai-raccoon serve`` backend, starting one if none is listening. The data
  root comes ONLY from the ``--data-root`` flag (pass it via ``binary_args``)
  — no data-root environment variable is read by the CLI.
- ``http``: connects to a running server's Streamable HTTP endpoint
  (default ``http://127.0.0.1:7721/mcp``). ``ai-raccoon serve`` gates ``/mcp``
  behind a loopback token (``McpTokenGate``); this transport reads
  ``token_file`` (default ``~/.ai-raccoon/mcp-token``) at connect and sends it
  as ``X-AiRaccoon-Token`` — only when ``url`` is loopback, so the secret
  never ships to a remote host a caller configures.

The official ``mcp`` SDK is imported lazily inside ``connect()`` so code
that only constructs or fakes these clients (unit tests) never needs it.
A background asyncio loop owns the session — for stdio the child process
must NOT be re-spawned per call, so the session is persistent.
"""

from __future__ import annotations

import asyncio
import json
import logging
import threading
from pathlib import Path
from typing import Any, Dict, Optional
from urllib.parse import urlsplit

logger = logging.getLogger(__name__)

DEFAULT_HTTP_URL = "http://127.0.0.1:7721/mcp"
DEFAULT_TOKEN_FILE = "~/.ai-raccoon/mcp-token"
TOKEN_HEADER = "X-AiRaccoon-Token"
_LOOPBACK_HOSTS = frozenset({"127.0.0.1", "::1", "localhost"})
CONNECT_TIMEOUT_S = 30.0
CALL_TIMEOUT_S = 15.0
CLOSE_TIMEOUT_S = 10.0
# Matches the host runtime's own httpx.AsyncClient defaults for the streamable-http
# transport (~/.hermes/hermes-agent/tools/mcp_tool.py:3079-3095): connect at
# CONNECT_TIMEOUT_S, read generously since a tool call can run long.
_HTTP_READ_TIMEOUT_S = 300.0


def _is_loopback_url(url: str) -> bool:
    """R3: the loopback token must never ship to a host the caller configured."""
    return urlsplit(url).hostname in _LOOPBACK_HOSTS


class AiRaccoonError(RuntimeError):
    """A server call failed at the transport, protocol, or tool level."""


class _MCPClient:
    """Sync facade over a persistent asyncio MCP session."""

    def __init__(self) -> None:
        self._loop = None
        self._session = None
        self._ctx = None
        self._thread: Optional[threading.Thread] = None
        self._closed = False

    # -- transport hooks ----------------------------------------------------

    async def _open(self):  # pragma: no cover - transport-specific
        raise NotImplementedError

    async def _close(self):  # pragma: no cover - transport-specific
        raise NotImplementedError

    # -- lifecycle ----------------------------------------------------------

    def _describe(self) -> str:  # pragma: no cover - overridden per transport
        """What this client was trying to reach, for a failure message."""
        return type(self).__name__

    def connect(self) -> None:
        """Start the loop thread and establish the MCP session. Raises AiRaccoonError on failure."""
        self._loop = asyncio.new_event_loop()
        self._thread = threading.Thread(target=self._loop.run_forever, daemon=True)
        self._thread.start()
        future = asyncio.run_coroutine_threadsafe(self._open(), self._loop)
        try:
            future.result(timeout=CONNECT_TIMEOUT_S)
        # CancelledError explicitly: anyio cancels the caller when a sibling task fails, so the real
        # cause arrives outside Exception with an empty str(). Not bare BaseException — a Ctrl-C
        # during the 30 s wait belongs to the operator's shell, not to this error message.
        except (Exception, asyncio.CancelledError) as e:
            # Never leave the pending _open task behind: a stdio spawn that
            # completes after the timeout would leak the child process.
            future.cancel()
            self.close()
            reason = str(e) or type(e).__name__
            raise AiRaccoonError(f"ai-raccoon connect to {self._describe()} failed: {reason}") from e

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        if self._loop is not None and self._loop.is_running():
            try:
                asyncio.run_coroutine_threadsafe(self._close(), self._loop).result(timeout=CLOSE_TIMEOUT_S)
            # Same widening as connect(): .result() surfaces a cancelled teardown as
            # CancelledError, and letting it escape skips the loop stop and thread join below.
            except (Exception, asyncio.CancelledError) as e:  # pragma: no cover - best-effort
                logger.debug("ai-raccoon client teardown failed: %s", e)
            self._loop.call_soon_threadsafe(self._loop.stop)
        if self._thread is not None:
            self._thread.join(timeout=5.0)
        self._session = None

    # -- tool calls ---------------------------------------------------------

    def _call(self, name: str, arguments: Dict[str, Any]) -> Any:
        if self._session is None or self._loop is None or not self._loop.is_running():
            raise AiRaccoonError("ai-raccoon provider is not connected")
        try:
            future = asyncio.run_coroutine_threadsafe(
                self._session.call_tool(name, arguments=arguments), self._loop
            )
            result = future.result(timeout=CALL_TIMEOUT_S)
        except Exception as e:
            raise AiRaccoonError(f"ai-raccoon call {name} failed: {e}") from e
        if getattr(result, "isError", False):
            raise AiRaccoonError(f"ai-raccoon {name} returned an error: {_text(result)}")
        return _unwrap(_text(result))

    # -- duck-typed surface (snake_case; camelCase over the wire) -----------

    def search(self, project_id: str, query: str, scope: str = "all",
               limit: int = 5, min_score: float = 0.5,
               context_label: Optional[str] = None) -> Any:
        args: Dict[str, Any] = {"projectId": project_id, "query": query,
                                "scope": scope, "limit": limit, "minScore": min_score}
        if context_label:
            args["contextLabel"] = context_label
        return self._call("memory_search", args)

    def write(self, project_id: str, content: str, workspace_id: Optional[str] = None,
              agent_id: Optional[str] = None, context: Optional[str] = None,
              source_file: Optional[str] = None, section: Optional[str] = None) -> Any:
        args: Dict[str, Any] = {"projectId": project_id, "content": content}
        if workspace_id:
            args["workspaceId"] = workspace_id
        if agent_id:
            args["agentId"] = agent_id
        if context:
            args["context"] = context
        if source_file:
            args["sourceFile"] = source_file
        if section:
            args["section"] = section
        return self._call("memory_write", args)

    def stats(self, project_id: str) -> Any:
        return self._call("memory_stats", {"projectId": project_id})

    def share(self, project_id: str, hash_: str) -> Any:
        return self._call("memory_share", {"projectId": project_id, "hash": hash_})


class StdioClient(_MCPClient):
    """Spawns ``ai-raccoon`` as a child process and speaks MCP over stdio."""

    def __init__(self, binary: str = "ai-raccoon", args: Optional[list] = None) -> None:
        super().__init__()
        self._binary = binary
        self._args = list(args or [])

    def _describe(self) -> str:
        return " ".join([self._binary, *self._args])

    async def _open(self) -> None:
        from mcp import ClientSession
        from mcp.client.stdio import StdioServerParameters, stdio_client

        self._ctx = stdio_client(StdioServerParameters(command=self._binary, args=self._args))
        read, write = await self._ctx.__aenter__()
        session = ClientSession(read, write)
        self._session = await session.__aenter__()
        await self._session.initialize()

    async def _close(self) -> None:
        if self._session is not None:
            await self._session.__aexit__(None, None, None)
            self._session = None
        if self._ctx is not None:
            await self._ctx.__aexit__(None, None, None)
            self._ctx = None


class HttpClient(_MCPClient):
    """Connects to a running server's Streamable HTTP MCP endpoint.

    ``token_file`` (default ``~/.ai-raccoon/mcp-token``) is read at connect time and, when
    ``url`` targets loopback, sent as ``X-AiRaccoon-Token`` — the header ``ai-raccoon serve``
    (unlike the ungated ``--transport http``) requires. A missing, unreadable, empty or
    whitespace-only file just means no header: the ungated case must keep working (D2).
    """

    def __init__(self, url: str = DEFAULT_HTTP_URL, token_file: str = DEFAULT_TOKEN_FILE) -> None:
        super().__init__()
        self._url = url
        self._token_file = token_file
        self._http_client = None
        self._header_sent: Optional[bool] = None

    def _describe(self) -> str:
        # What _open actually sent, when it got that far — re-probing would let the message
        # describe a file that appeared or vanished after the attempt.
        sent = self._auth_headers() if self._header_sent is None else self._header_sent
        return f"{self._url} ({TOKEN_HEADER} {'sent' if sent else 'not sent'}; token file {self._token_file})"

    def _auth_headers(self) -> Dict[str, str]:
        if not self._token_file or not _is_loopback_url(self._url):
            return {}
        try:
            token = Path(self._token_file).expanduser().read_text(encoding="utf-8").strip()
        except (OSError, ValueError, RuntimeError):
            # RuntimeError: expanduser() with no resolvable home. ValueError: UnicodeDecodeError on
            # a clobbered file. Both reach _describe() inside the connect-failure handler.
            return {}
        if not token:
            return {}
        return {TOKEN_HEADER: token}

    async def _open(self) -> None:
        import httpx
        from mcp import ClientSession
        from mcp.client.streamable_http import streamable_http_client

        headers = self._auth_headers()
        self._header_sent = bool(headers)
        self._http_client = httpx.AsyncClient(
            # False: httpx strips Authorization across origins but keeps a custom header, so a 3xx
            # would hand the loopback token to whatever host it names. /mcp never redirects.
            follow_redirects=False,
            timeout=httpx.Timeout(CONNECT_TIMEOUT_S, read=_HTTP_READ_TIMEOUT_S),
            headers=headers or None,
        )
        self._ctx = streamable_http_client(self._url, http_client=self._http_client)
        read, write, _ = await self._ctx.__aenter__()
        session = ClientSession(read, write)
        self._session = await session.__aenter__()
        await self._session.initialize()

    async def _close(self) -> None:
        # try/finally: session/ctx teardown can itself raise (observed: anyio
        # "cancel scope in a different task", since _open and _close each run as
        # a separate run_coroutine_threadsafe task on the same loop) and
        # streamable_http_client does not close a client it did not create
        # (mcp.client.streamable_http.streamable_http_client) — either way, the
        # httpx client must still be closed or it leaks a connection pool.
        try:
            if self._session is not None:
                await self._session.__aexit__(None, None, None)
                self._session = None
            if self._ctx is not None:
                await self._ctx.__aexit__(None, None, None)
                self._ctx = None
        finally:
            if self._http_client is not None:
                await self._http_client.aclose()


def create_client(config: dict) -> _MCPClient:
    """Build a client for the plugin config (transport: stdio | http).

    stdio spawns carry ``--quiet`` by default, which the spawned child passes
    on to the ``serve`` backend: every backend log level, including warnings,
    goes to a file beside its bank instead of stdout/stderr (the provider
    emits its own status cues). The proxy in the middle has no quiet
    destination, so it still writes ``Warning``-and-above — and any
    backend-unavailable line — to stderr. Set ``quiet: false`` in the plugin
    config for full backend logs.
    """
    transport = config.get("transport", "stdio")
    if transport == "http":
        return HttpClient(
            config.get("url", DEFAULT_HTTP_URL),
            config.get("token_file", DEFAULT_TOKEN_FILE),
        )
    args = list(config.get("binary_args") or [])
    if config.get("quiet", True):
        args.append("--quiet")
    return StdioClient(config.get("binary", "ai-raccoon"), args)


def _text(result: Any) -> Any:
    """Extract the first text content of a CallToolResult and parse JSON."""
    for content in getattr(result, "content", None) or []:
        if getattr(content, "type", None) == "text":
            text = getattr(content, "text", "")
            try:
                return json.loads(text)
            except json.JSONDecodeError:
                return text
    raise AiRaccoonError("ai-raccoon tool result contained no text content")


def _unwrap(payload: Any) -> Any:
    """Strip the server's ApiEnvelope: every tool answers {data, meta}.

    Both keys are required so a payload with its own ``data`` field survives.
    """
    if isinstance(payload, dict) and "data" in payload and "meta" in payload:
        return payload["data"]
    return payload
