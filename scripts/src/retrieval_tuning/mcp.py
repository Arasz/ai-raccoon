"""Minimal streamable-HTTP MCP client for the retrieval-tuning harness (plan §8).

Pattern from scripts/src/mcp_client.py + /tmp/checklist-1-27-2/mcp_call.py: the
server answers with SSE frames ('event: message' then 'data: {...}') on
streamable HTTP. This client deliberately imports NO project config — the
bearer token comes from <data-root>/mcp-token via read_token().
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Optional

import httpx

PROTOCOL_VERSION = "2025-06-18"
CLIENT_INFO = {"name": "retrieval-tuning-harness", "version": "0.1.0"}


class McpError(RuntimeError):
    """An MCP-level failure: JSON-RPC error, HTTP error, or an unparseable response."""


def parse_sse_body(text: str) -> dict:
    """Parse a streamable-HTTP response: the LAST 'data:' frame wins, plain JSON falls back."""
    data_lines = [line[5:].strip() for line in text.strip().splitlines() if line.startswith("data:")]
    if data_lines:
        return json.loads(data_lines[-1])
    return json.loads(text)


def read_token(data_root) -> str:
    """The bearer token the serve process mints at <data-root>/mcp-token."""
    return (Path(data_root) / "mcp-token").read_text().strip()


class MCPClient:
    """Blocking JSON-RPC client for one AiRaccoon MCP endpoint (/mcp)."""

    def __init__(self, base_url: str, token: Optional[str] = None, timeout: float = 120.0) -> None:
        base = base_url.rstrip("/")
        if not base.endswith("/mcp"):
            base += "/mcp"
        self._base = base
        self._token = token
        self._timeout = timeout
        self._req_id = 0
        self._initialized = False

    def _headers(self) -> dict:
        headers = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        }
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        return headers

    def _next_id(self) -> int:
        self._req_id += 1
        return self._req_id

    def _rpc(self, method: str, params: dict, notify: bool = False) -> dict:
        body = {"jsonrpc": "2.0", "method": method, "params": params}
        if not notify:
            body["id"] = self._next_id()
        try:
            with httpx.Client(timeout=self._timeout) as client:
                resp = client.post(self._base, headers=self._headers(), json=body)
        except httpx.HTTPError as exc:
            raise McpError(f"MCP request {method} failed: {exc}") from exc
        if resp.status_code >= 400:
            raise McpError(f"MCP request {method} failed with HTTP {resp.status_code}: {resp.text[:300]}")
        if not resp.text.strip():
            return {}  # notifications answer 202 with an empty body
        try:
            parsed = parse_sse_body(resp.text)
        except json.JSONDecodeError as exc:
            raise McpError(f"MCP request {method} returned non-JSON: {resp.text[:300]}") from exc
        if "error" in parsed:
            error = parsed["error"]
            if notify:
                return {}  # best-effort notification: the handshake already succeeded
            raise McpError(f"MCP {method} error {error.get('code')}: {error.get('message')}")
        return parsed.get("result", parsed)

    def initialize(self) -> dict:
        """The initialize handshake, then notifications/initialized. Returns the server info."""
        result = self._rpc(
            "initialize",
            {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": CLIENT_INFO,
            },
        )
        self._rpc("notifications/initialized", {}, notify=True)
        self._initialized = True
        return result

    def tools_list(self) -> list[dict]:
        """tools/list — used to verify the tool surface of a scratch server."""
        return self._rpc("tools/list", {}).get("tools", [])

    def _call_tool(self, name: str, arguments: dict) -> Any:
        result = self._rpc("tools/call", {"name": name, "arguments": arguments})
        content = result.get("content", [])
        for item in content:
            if item.get("type") == "text":
                return json.loads(item["text"])
        return result

    @staticmethod
    def _extract_results(parsed: Any, kind: Optional[str] = None) -> list[dict]:
        """Dig the ranked result list out of the tool text payload.

        `kind="code"` reads the 'code' key (the code-corpus section, plan
        docs/work/2026-08-21-code-search-implementation-plan.md §3.6); any other
        kind (including None/"memory"/"both") reads the 'results' key — the
        harness only ever scores one section per call, so kind="both" reads the
        memory leg, matching every existing corpus entry's (non-code) scoring.
        """
        key = "code" if kind == "code" else "results"
        if isinstance(parsed, list):
            return parsed
        if isinstance(parsed, dict):
            value = parsed.get(key)
            if isinstance(value, list):
                return value
            if key != "data":
                fallback = parsed.get("data")
                if isinstance(fallback, list):
                    return fallback
                if isinstance(fallback, dict) and isinstance(fallback.get(key), list):
                    return fallback[key]
        raise McpError(f"memory_search response carries no '{key}' list: {str(parsed)[:300]}")

    def memory_search(
        self,
        project_id: str,
        query: str,
        scope: str = "all",
        limit: int = 5,
        min_relative_score: float = 0.0,
        kind: Optional[str] = None,
    ) -> list[dict]:
        """Settings-driven search: NO tuning args — the bank settings table drives the knobs.

        `kind` (plan §12.2 H8) is omitted from the wire entirely when None, so the
        default memory-only call is byte-identical to before this parameter existed;
        pass "code" to search the code corpus (see docs/features/code-corpus/) or
        "both" to search both (the memory leg is what gets scored).
        """
        arguments = {
            "projectId": project_id,
            "query": query,
            "scope": scope,
            "limit": limit,
            "minRelativeScore": min_relative_score,
        }
        if kind is not None:
            arguments["kind"] = kind
        return self._extract_results(self._call_tool("memory_search", arguments), kind=kind)

    def memory_search_with_knobs(
        self,
        project_id: str,
        query: str,
        scope: str = "all",
        limit: int = 5,
        min_relative_score: float = 0.0,
        rrf_k: Optional[int] = None,
        fts_weight: Optional[int] = None,
        vector_weight: Optional[int] = None,
        source_lambda: Optional[float] = None,
        consolidation_threshold: Optional[float] = None,
        doc_score_formula: Optional[str] = None,
        candidate_window: Optional[str] = None,
    ) -> list[dict]:
        """Verification path only: per-call tuning args mirroring the MCP tool surface."""
        arguments = {
            "projectId": project_id,
            "query": query,
            "scope": scope,
            "limit": limit,
            "minRelativeScore": min_relative_score,
        }
        per_call = {
            "rrfK": rrf_k,
            "ftsWeight": fts_weight,
            "vectorWeight": vector_weight,
            "sourceLambda": source_lambda,
            "consolidationThreshold": consolidation_threshold,
            "docScoreFormula": doc_score_formula,
            "candidateWindow": candidate_window,
        }
        arguments.update({key: value for key, value in per_call.items() if value is not None})
        return self._extract_results(self._call_tool("memory_search", arguments))
