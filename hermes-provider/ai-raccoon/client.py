"""MCP client for the AiRaccoon memory server.

Two transports behind one duck-typed surface:

- ``stdio`` (default): spawns the ``ai-raccoon`` binary as a child process
  (MCP over stdio; the child inherits the parent env, so a data-root
  override such as ``AIRACCOON_DATA_ROOT`` flows through).
- ``http``: connects to a running server's Streamable HTTP endpoint
  (default ``http://127.0.0.1:7721/mcp``).

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
from typing import Any, Dict, Optional

logger = logging.getLogger(__name__)

DEFAULT_HTTP_URL = "http://127.0.0.1:7721/mcp"
CONNECT_TIMEOUT_S = 30.0
CALL_TIMEOUT_S = 15.0
CLOSE_TIMEOUT_S = 10.0


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

    def connect(self) -> None:
        """Start the loop thread and establish the MCP session. Raises on failure."""
        self._loop = asyncio.new_event_loop()
        self._thread = threading.Thread(target=self._loop.run_forever, daemon=True)
        self._thread.start()
        future = asyncio.run_coroutine_threadsafe(self._open(), self._loop)
        try:
            future.result(timeout=CONNECT_TIMEOUT_S)
        except Exception:
            # Never leave the pending _open task behind: a stdio spawn that
            # completes after the timeout would leak the child process.
            future.cancel()
            self.close()
            raise

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        if self._loop is not None and self._loop.is_running():
            try:
                asyncio.run_coroutine_threadsafe(self._close(), self._loop).result(timeout=CLOSE_TIMEOUT_S)
            except Exception as e:  # pragma: no cover - teardown best-effort
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
        return _text(result)

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
    """Connects to a running server's Streamable HTTP MCP endpoint."""

    def __init__(self, url: str = DEFAULT_HTTP_URL) -> None:
        super().__init__()
        self._url = url

    async def _open(self) -> None:
        from mcp import ClientSession
        from mcp.client.streamable_http import streamable_http_client

        self._ctx = streamable_http_client(self._url)
        read, write, _ = await self._ctx.__aenter__()
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


def create_client(config: dict) -> _MCPClient:
    """Build a client for the plugin config (transport: stdio | http).

    stdio spawns carry ``--status-words`` by default so the child prints
    one-word progress cues instead of log noise; set ``status_words:
    false`` in the plugin config for full logs.
    """
    transport = config.get("transport", "stdio")
    if transport == "http":
        return HttpClient(config.get("url", DEFAULT_HTTP_URL))
    args = list(config.get("binary_args") or [])
    if config.get("status_words", True):
        args.append("--status-words")
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
