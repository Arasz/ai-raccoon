"""Async MCP HTTP client for AiRaccoon (moved verbatim from scripts/ingest-jsaa-docs.py).

httpx is the only third-party import in the ingest pipeline (ambient dependency, unchanged).
"""

from __future__ import annotations

import json
import logging
import time
from typing import Optional

import httpx

from jsaa_config import MCP_BASE

log = logging.getLogger("ingest")


class AiRaccoonClient:
    """Async HTTP client for the AiRaccoon MCP server (Streamable HTTP transport)."""

    def __init__(self, base_url: str = MCP_BASE) -> None:
        self._base = base_url.rstrip("/")
        self._req_id = 0

    def _next_id(self) -> int:
        self._req_id += 1
        return self._req_id

    def _rpc_body(self, method: str, params: dict) -> dict:
        return {
            "jsonrpc": "2.0",
            "id": self._next_id(),
            "method": method,
            "params": params,
        }

    async def _call(self, client: httpx.AsyncClient, method: str, params: dict) -> dict:
        body = self._rpc_body(method, params)
        t0 = time.monotonic()
        log.debug("MCP call %s params=%s", method, {k: str(v)[:80] for k, v in params.items()})
        try:
            resp = await client.post(
                self._base,
                json=body,
                headers={"Content-Type": "application/json"},
                timeout=httpx.Timeout(60.0),
            )
            resp.raise_for_status()
            elapsed = time.monotonic() - t0
            # AiRaccoon MCP Streamable HTTP returns SSE: "event: message\\ndata: {...}\\n\\n"
            text = resp.text
            if "event:" in text and "data:" in text:
                data_line = next((line.removeprefix("data: ").strip()
                                  for line in text.splitlines()
                                  if line.startswith("data: ")), "{}")
                result = json.loads(data_line)
            else:
                result = resp.json()
            log.debug("MCP %s → ok (%.2fs)", method, elapsed)
            return result
        except httpx.HTTPStatusError as exc:
            elapsed = time.monotonic() - t0
            log.error("MCP %s → HTTP %d (%.2fs): %s", method, exc.response.status_code, elapsed, exc.response.text[:500])
            raise
        except Exception:
            elapsed = time.monotonic() - t0
            log.exception("MCP %s → error (%.2fs)", method, elapsed)
            raise

    async def memory_ingest_file(
        self,
        client: httpx.AsyncClient,
        project_id: str,
        path: str,
        context: Optional[str] = None,
    ) -> dict:
        """Index one file from disk through the production FileIngestor/chunker.

        Requires the path to lie inside the project's configured ingest scope
        (`ai-raccoon ingest scope add <project_id> <path>`) — an unscoped
        project refuses every ingest.
        """
        args: dict = {"projectId": project_id, "path": path}
        if context:
            args["context"] = context
        result = await self._call(
            client, "tools/call", {"name": "memory_ingest_file", "arguments": args}
        )
        return _unwrap(result)

    async def memory_embed_pending(
        self, client: httpx.AsyncClient, project_id: str, limit: Optional[int] = None
    ) -> dict:
        args: dict = {"projectId": project_id}
        if limit is not None:
            args["limit"] = limit
        result = await self._call(
            client, "tools/call", {"name": "memory_embed_pending", "arguments": args}
        )
        return _unwrap(result)

    async def memory_stats(self, client: httpx.AsyncClient, project_id: str) -> dict:
        result = await self._call(
            client,
            "tools/call",
            {"name": "memory_stats", "arguments": {"projectId": project_id}},
        )
        return _unwrap(result)

    async def memory_search(
        self,
        client: httpx.AsyncClient,
        project_id: str,
        query: str,
        scope: str = "project",
        limit: int = 5,
        min_score: float = 0.0,
    ) -> dict:
        result = await self._call(
            client,
            "tools/call",
            {
                "name": "memory_search",
                "arguments": {
                    "projectId": project_id,
                    "query": query,
                    "scope": scope,
                    "limit": limit,
                    "minScore": min_score,
                },
            },
        )
        return _unwrap(result)

    async def memory_delete_context(
        self, client: httpx.AsyncClient, project_id: str, context: str
    ) -> dict:
        result = await self._call(
            client,
            "tools/call",
            {
                "name": "memory_delete_context",
                "arguments": {"projectId": project_id, "context": context},
            },
        )
        return _unwrap(result)


def _unwrap(result: dict) -> dict:
    """Extract content from MCP JSON-RPC response.

    AiRaccoon MCP returns: {"jsonrpc": "2.0", "id": N, "result": {"content": [{"type": "text", "text": "..."}]}}
    We parse the inner text as JSON and return it.
    """
    try:
        content_list = result["result"]["content"]
        for item in content_list:
            if item.get("type") == "text":
                return json.loads(item["text"])
    except (KeyError, IndexError, TypeError, json.JSONDecodeError):
        pass
    # Fallback: return result as-is if it doesn't match MCP shape
    return result
