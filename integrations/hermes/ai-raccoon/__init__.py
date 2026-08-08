"""ai-raccoon — Hermes memory provider plugin backed by the AiRaccoon MCP memory server.

Registers as a MemoryProvider plugin (agent/memory_provider.py ABC),
giving the agent project-scoped persistent memory over the AiRaccoon
bank: per-turn prefetch (memory_search), background sync
(memory_write), the curated tool surface (memory_search / memory_write
/ memory_stats / memory_share), and mirroring of built-in memory
writes.

Transport: stdio by default (spawns the installed ``ai-raccoon`` binary
as a child process); ``transport: http`` connects to a running server's
Streamable HTTP endpoint instead. Protocol on both is MCP via the
official mcp SDK.

Config in $HERMES_HOME/config.yaml (profile-scoped), under plugins.ai-raccoon:
  transport: stdio | http
  url: http://127.0.0.1:7721/mcp        # http mode
  binary: ai-raccoon                     # stdio mode (PATH lookup)
  project_id: ""                         # empty -> derived hermes-<profile>
  search_limit: 5
  min_score: 0.5
  scope: all
"""

from __future__ import annotations

import json
import logging
import os
import shutil
import sys
import threading
import time
from agent.memory_provider import MemoryProvider
from typing import Any, Dict, List, Optional

try:
    from .status import MemoryOperationLog, status_word
except ImportError:  # spec-loaded without a package (tests)
    from status import MemoryOperationLog, status_word  # type: ignore[no-redef]

logger = logging.getLogger(__name__)

DEFAULT_TRANSPORT = "stdio"
DEFAULT_BINARY = "ai-raccoon"

# Must be >= client.CALL_TIMEOUT_S so an in-flight write drains before the
# session is torn down (shutdown joins with this bound).
_SYNC_JOIN_TIMEOUT_S = 15.0

# -- Tool schemas (OpenAI function-calling format; projectId injected by the
#    provider, so it is deliberately absent from the model-facing schemas).
#    Descriptions mirror the server's tool descriptions (MCP stays thin).

TOOL_SCHEMAS: List[Dict[str, Any]] = [
    {
        "name": "memory_search",
        "description": (
            "Hybrid semantic search over the AiRaccoon memory bank. "
            "scope=all (default) searches shared + project (+ workspace when named); "
            "scope=project searches the project only; scope=shared searches the shared "
            "promotion tier only."
        ),
        "parameters": {
            "type": "object",
            "properties": {
                "query": {"type": "string", "description": "The search query."},
                "scope": {"type": "string", "enum": ["all", "project", "shared"], "default": "all"},
                "limit": {"type": "integer", "description": "Maximum results (default 20).", "default": 20},
                "minScore": {"type": "number", "description": "Minimum ranking threshold 0..1 (default 0.7).", "default": 0.7},
                "contextLabel": {"type": "string", "description": "When set, the project scope also searches custom-scoped rows under this context label."},
            },
            "required": ["query"],
        },
    },
    {
        "name": "memory_write",
        "description": (
            "Writes content into the AiRaccoon memory bank. Writes land in the project's "
            "committed context by default; naming a workspace_id routes them into that "
            "isolated workspace. Returns the stored entry."
        ),
        "parameters": {
            "type": "object",
            "properties": {
                "content": {"type": "string", "description": "The content to remember."},
                "workspaceId": {"type": "string", "description": "When set, the write lands in this workspace's isolated context instead of the project context."},
                "context": {"type": "string", "description": "Optional custom context label instead of the default project/workspace context."},
                "sourceFile": {"type": "string", "description": "Optional original file path the content came from; chunks of one file share it."},
                "section": {"type": "string", "description": "Optional section slug within the source file (e.g. 'decision'); indexed as a weighted FTS column."},
            },
            "required": ["content"],
        },
    },
    {
        "name": "memory_stats",
        "description": "Reports entry count, pending (deferred-embedding) count, and the bank's committed contexts.",
        "parameters": {"type": "object", "properties": {}},
    },
    {
        "name": "memory_share",
        "description": (
            "Promotes an existing project entry into the flat shared context — the curated, "
            "cross-project, sweep-exempt tier. Nothing is shared without this explicit promotion."
        ),
        "parameters": {
            "type": "object",
            "properties": {
                "hash": {"type": "string", "description": "The content hash to promote."},
            },
            "required": ["hash"],
        },
    },
]

# MCP tool name -> (client method, arg mapping)
_TOOL_DISPATCH = {
    "memory_search": ("search", {
        "query": "query",
        "scope": "scope",
        "limit": "limit",
        "minScore": "min_score",
        "contextLabel": "context_label",
    }),
    "memory_write": ("write", {
        "content": "content",
        "sourceFile": "source_file",
        "section": "section",
        "workspaceId": "workspace_id",
        "context": "context",
    }),
    "memory_stats": ("stats", {}),
    "memory_share": ("share", {"hash": "hash_"}),
}


def _load_plugin_config() -> dict:
    """Read the plugins.ai-raccoon block from the profile's config.yaml."""
    try:
        from hermes_cli.config import cfg_get, load_config_readonly
        all_config = load_config_readonly()
        return cfg_get(all_config, "plugins", "ai-raccoon", default={}) or {}
    except Exception:
        return {}


class AiRaccoonMemoryProvider(MemoryProvider):
    """Project-scoped memory over the AiRaccoon MCP server."""

    def __init__(self, config: Optional[dict] = None, client_factory=None) -> None:
        self._config = config or _load_plugin_config()
        self._client_factory = client_factory
        self._client = None
        self._session_id = None
        self._agent_identity = "default"
        self._agent_context = None
        self._project_id = None
        self._sync_threads: List[threading.Thread] = []
        self._status_words = bool(self._config.get("status_words", True))
        self._op_log: Optional[MemoryOperationLog] = None

    # -- identity -----------------------------------------------------------

    @property
    def name(self) -> str:
        return "ai-raccoon"

    def is_available(self) -> bool:
        transport = self._config.get("transport", DEFAULT_TRANSPORT)
        if transport == "http":
            return bool(self._config.get("url"))
        if transport == "stdio":
            return shutil.which(self._config.get("binary", DEFAULT_BINARY)) is not None
        return False

    @property
    def project_id(self) -> Optional[str]:
        return self._project_id

    # -- lifecycle ----------------------------------------------------------

    def initialize(self, session_id: str, **kwargs) -> None:
        # Re-init must not leak the previous session's client/child.
        if self._client is not None:
            try:
                self._client.close()
            except Exception as e:
                logger.debug("ai-raccoon provider re-init close failed: %s", e)
            self._client = None
        self._session_id = session_id
        self._agent_identity = kwargs.get("agent_identity") or "default"
        self._agent_context = kwargs.get("agent_context")
        workspace = kwargs.get("agent_workspace") or "hermes"
        self._project_id = self._config.get("project_id") or f"{workspace}-{self._agent_identity}"
        log_path = os.environ.get("AIRACCOON_MEMORY_LOG") or None  # blank = unset
        self._op_log = MemoryOperationLog(log_path) if log_path else None
        factory = self._client_factory
        if factory is None:
            from .client import create_client
            factory = create_client
        try:
            client = factory(self._config)
            client.connect()
            self._client = client
        except Exception as e:
            logger.warning("ai-raccoon provider connect failed: %s", e)
            self._client = None

    def shutdown(self) -> None:
        self._join_sync_threads()
        client, self._client = self._client, None
        if client is not None:
            try:
                client.close()
            except Exception as e:
                logger.debug("ai-raccoon provider shutdown failed: %s", e)

    # -- caller-side observability (the server cannot attribute calls) ------

    def _emit_word(self, tool: str) -> None:
        if self._status_words:
            print(status_word(tool), file=sys.stderr, flush=True)

    def _log(self, tool: str, status: str, duration_ms: float,
             error_type: Optional[str] = None) -> None:
        log = self._op_log
        if log is None or self._project_id is None:
            return
        log.write(tool, self._project_id, status, duration_ms, error_type=error_type,
                  agent_id=f"hermes-{self._agent_identity}", session_id=self._session_id)

    # -- context ------------------------------------------------------------

    def system_prompt_block(self) -> str:
        return (
            "# AiRaccoon Memory\n"
            "Active. Search memory FIRST — before web search or asking — with "
            "memory_search (2-3 query formulations) and cite what you find. "
            "Write durable facts back with memory_write (include sourceFile/section). "
            "Use memory_stats to check the bank, memory_share to promote cross-project facts."
        )

    def prefetch(self, query: str, *, session_id: str = "") -> str:
        if self._client is None or self._project_id is None or not query:
            return ""
        start = time.monotonic()
        self._emit_word("memory_search")
        try:
            payload = self._client.search(
                self._project_id,
                query,
                scope=self._config.get("scope", "all"),
                limit=int(self._config.get("search_limit", 5)),
                min_score=float(self._config.get("min_score", 0.5)),
            )
        except Exception as e:
            self._log("memory_search", "error", (time.monotonic() - start) * 1000,
                      error_type=type(e).__name__)
            logger.debug("ai-raccoon prefetch failed: %s", e)
            return ""
        self._log("memory_search", "ok", (time.monotonic() - start) * 1000)
        results = payload.get("results", []) if isinstance(payload, dict) else []
        if not results:
            return ""
        lines = [f"- [{r.get('ranking', 0.0):.2f}] {r.get('snippet', '')}" for r in results]
        return "## AiRaccoon Memory\n" + "\n".join(lines)

    def sync_turn(self, user_content: str, assistant_content: str, *,
                  session_id: str = "", messages: Optional[List[Dict[str, Any]]] = None) -> None:
        client = self._client
        pid = self._project_id
        if client is None or pid is None:
            return
        if self._agent_context not in (None, "primary"):
            return  # cron/subagent contexts must not pollute the bank
        target_session = session_id or self._session_id or "unknown"

        def _sync() -> None:
            start = time.monotonic()
            self._emit_word("memory_write")
            try:
                client.write(
                    pid,
                    content=assistant_content,
                    agent_id=f"hermes-{self._agent_identity}",
                    source_file=f"hermes/{target_session}",
                    section="turn",
                )
                self._log("memory_write", "ok", (time.monotonic() - start) * 1000)
            except Exception as e:
                self._log("memory_write", "error", (time.monotonic() - start) * 1000,
                          error_type=type(e).__name__)
                logger.warning("ai-raccoon sync_turn failed: %s", e)

        thread = threading.Thread(target=_sync, daemon=True)
        thread.start()
        # Reap finished threads so a long-lived agent does not accumulate
        # thousands of dead references (shutdown would join them all).
        self._sync_threads = [t for t in self._sync_threads if t.is_alive()]
        self._sync_threads.append(thread)

    def _join_sync_threads(self) -> None:
        for thread in self._sync_threads:
            thread.join(timeout=_SYNC_JOIN_TIMEOUT_S)
        self._sync_threads = []

    # -- tool surface -------------------------------------------------------

    def get_tool_schemas(self) -> List[Dict[str, Any]]:
        return list(TOOL_SCHEMAS)

    def handle_tool_call(self, tool_name: str, args: Dict[str, Any], **kwargs) -> str:
        if self._client is None or self._project_id is None:
            return json.dumps({"error": "ai-raccoon provider is not connected"})
        entry = _TOOL_DISPATCH.get(tool_name)
        if entry is None:
            return json.dumps({"error": f"Unknown tool: {tool_name}"})
        method_name, mapping = entry
        method = getattr(self._client, method_name)
        start = time.monotonic()
        self._emit_word(tool_name)
        try:
            call_args = {target: args[key] for key, target in mapping.items() if key in args}
            if method_name == "write" and "agent_id" not in call_args:
                call_args["agent_id"] = f"hermes-{self._agent_identity}"
            result = method(self._project_id, **call_args)
        except KeyError as exc:
            # Defensive: the arg mapping is `in`-guarded, so this only fires if a
            # required schema arg is missing from the model's call.
            self._log(tool_name, "error", (time.monotonic() - start) * 1000,
                      error_type="KeyError")
            return json.dumps({"error": f"Missing required argument: {exc}"})
        except Exception as exc:
            self._log(tool_name, "error", (time.monotonic() - start) * 1000,
                      error_type=type(exc).__name__)
            return json.dumps({"error": str(exc)})
        self._log(tool_name, "ok", (time.monotonic() - start) * 1000)
        return json.dumps(result)

    # -- hooks --------------------------------------------------------------

    def on_memory_write(self, action: str, target: str, content: str,
                        metadata: Optional[Dict[str, Any]] = None) -> None:
        client = self._client
        pid = self._project_id
        if action != "add" or client is None or pid is None:
            return
        start = time.monotonic()
        self._emit_word("memory_write")
        try:
            client.write(
                pid,
                content=content,
                agent_id=f"hermes-{self._agent_identity}",
                source_file="hermes-memory",
                section=target,
            )
            self._log("memory_write", "ok", (time.monotonic() - start) * 1000)
        except Exception as e:
            self._log("memory_write", "error", (time.monotonic() - start) * 1000,
                      error_type=type(e).__name__)
            logger.debug("ai-raccoon on_memory_write mirror failed: %s", e)

    def on_session_end(self, messages: List[Dict[str, Any]]) -> None:
        pass  # v1: no session-end summary (design decision D4b)

    # -- config -------------------------------------------------------------

    def get_config_schema(self) -> List[Dict[str, Any]]:
        return [
            {"key": "transport", "description": "stdio spawns the ai-raccoon binary; http connects to a running server", "default": "stdio", "choices": ["stdio", "http"]},
            {"key": "url", "description": "Streamable HTTP endpoint (http mode)", "default": "http://127.0.0.1:7721/mcp"},
            {"key": "binary", "description": "ai-raccoon binary to spawn (stdio mode)", "default": "ai-raccoon"},
            {"key": "binary_args", "description": "Extra args for the spawned binary, e.g. ['--data-root', '/tmp/bank'] (stdio mode)", "default": ""},
            {"key": "quiet", "description": "Spawn the server with --quiet (no info logs; the provider emits status cues)", "default": True, "choices": ["true", "false"]},
            {"key": "status_words", "description": "Print one-word status cues to stderr per call", "default": True, "choices": ["true", "false"]},
            {"key": "project_id", "description": "AiRaccoon project id (empty = derived hermes-<profile>)"},
            {"key": "search_limit", "description": "Prefetch result limit", "default": 5, "type": "integer"},
            {"key": "min_score", "description": "Prefetch minimum ranking threshold 0..1", "default": 0.5, "type": "number"},
            {"key": "scope", "description": "Prefetch search scope", "default": "all", "choices": ["all", "project", "shared"]},
        ]

    def save_config(self, values: Dict[str, Any], hermes_home: str) -> None:
        """Write non-secret config to config.yaml under plugins.ai-raccoon."""
        from pathlib import Path

        try:
            import yaml
            from hermes_cli.config import read_user_config_raw

            config_path = Path(hermes_home) / "config.yaml"
            existing = read_user_config_raw(config_path)
            existing.setdefault("plugins", {})
            existing["plugins"]["ai-raccoon"] = values
            with open(config_path, "w", encoding="utf-8") as f:
                yaml.dump(existing, f, default_flow_style=False)
        except Exception as e:
            logger.warning("ai-raccoon save_config failed: %s", e)


def register(ctx) -> None:
    """Register the ai-raccoon memory provider with the plugin system."""
    config = _load_plugin_config()
    provider = AiRaccoonMemoryProvider(config=config)
    ctx.register_memory_provider(provider)
