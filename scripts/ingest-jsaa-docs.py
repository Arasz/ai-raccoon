#!/usr/bin/env python3
"""Ingest job-search-ai-assistant documentation into AiRaccoon's memory.db.

Walks the JSAA project tree, chunks files by content type, writes chunks to
AiRaccoon via its MCP HTTP transport, and exports a structured-path → SHA256
hash map for the test runner's isExpectedSource matching.

Usage:
  python3 scripts/ingest-jsaa-docs.py --dry-run       # enumerate only
  python3 scripts/ingest-jsaa-docs.py --chunk-only     # enumerate + chunk, no writes
  python3 scripts/ingest-jsaa-docs.py --ingest-only    # full ingest, no spot-checks
  python3 scripts/ingest-jsaa-docs.py --verify         # full ingest + spot-checks
  python3 scripts/ingest-jsaa-docs.py --reset          # delete all contexts then re-ingest
"""

from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import logging
import re
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

import httpx

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

JSAA_ROOT = Path("/Users/arasz/RiderProjects/job-search-ai-assistant")
MCP_BASE = "http://localhost:5000/mcp"
PROJECT_ID = "job-search-ai-assistant"
HASH_MAP_PATH = Path(__file__).resolve().parent / "chunk-hash-map.json"
BATCH_SIZE = 50

log = logging.getLogger("ingest")

# ---------------------------------------------------------------------------
# Include / exclude rules (design §3.3)
# ---------------------------------------------------------------------------

INCLUDE_GLOBS: list[str] = [
    "docs/adr/*.md",
    "docs/*.md",
    "docs/explanation/*.md",
    "docs/how-to/*.md",
    "docs/reference/*.md",
    "docs/rules/*.md",
    "docs/tutorials/*.md",
    "docs/legacy/*.md",
    "docs/meta/baseline.json",
    "docs/meta/trust-debt.md",
    "docs/meta/trust-index.json",
    ".ai-badger/invariants/*.md",
    ".ai-badger/skills/*/SKILL.md",
    ".ai-badger/agents/*.md",
    ".ai-badger/instructions/*.md",
    ".ai-badger/config.json",
    ".ai-badger/delegation.md",
    ".ai-badger/copilot-instructions.md",
    ".ai-badger/agent-instructions/*",
    ".remember/recent.md",
    ".remember/archive.md",
    "README.md",
    "CLAUDE.md",
    "HERMES.md",
    "REVIEW.md",
    "infra/README.md",
]

EXCLUDE_GLOBS: list[str] = [
    ".ai-badger/state.json",
    ".ai-badger/status-notes.json",
    ".ai-badger/status-history.json",
    ".ai-badger/task-tracking/",
    ".ai-badger/hooks/",
    ".ai-badger/worktrees/",
    ".ai-badger/prompt-markers/",
    ".ai-badger/skills-data/",
    ".ai-badger/mcp-tools.json",
    ".ai-badger/manifest.json",
    ".ai-badger/stack-ignore.json",
    ".ai-badger/mcp-tools.yaml.migrated",
    ".remember/now.md",
    ".remember/today-*.md",
    ".remember/tmp/",
    ".remember/logs/",
    "docs/work/",
    "docs/brand/",
    ".github/",
    "node_modules/",
    ".git/",
]

# ---------------------------------------------------------------------------
# Data model
# ---------------------------------------------------------------------------


@dataclass
class Chunk:
    """A single retrievable unit of knowledge."""

    structured_path: str   # e.g. "docs:adr/0011-frontend-chassis-stack.md#decision"
    content: str           # markdown body with embedded ## Source: header
    context: str           # typed context label e.g. "docs:adr"


# ---------------------------------------------------------------------------
# MCP HTTP client
# ---------------------------------------------------------------------------


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

    async def memory_configure(
        self, client: httpx.AsyncClient, project_id: str, provider: str
    ) -> dict:
        result = await self._call(
            client,
            "tools/call",
            {
                "name": "memory_configure",
                "arguments": {"projectId": project_id, "provider": provider},
            },
        )
        return _unwrap(result)

    async def memory_write(
        self,
        client: httpx.AsyncClient,
        project_id: str,
        content: str,
        context: Optional[str] = None,
        agent_id: Optional[str] = None,
    ) -> dict:
        args: dict = {"projectId": project_id, "content": content}
        if context:
            args["context"] = context
        if agent_id:
            args["agentId"] = agent_id
        result = await self._call(
            client, "tools/call", {"name": "memory_write", "arguments": args}
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


CONTEXTS_TO_DELETE = [
    "docs:adr",
    "docs:architecture",
    "docs:explanation",
    "docs:how-to",
    "docs:reference",
    "docs:rules",
    "ai-badger:invariants",
    "ai-badger:skills",
    "ai-badger:agents",
    "ai-badger:instructions",
    "remember:operational",
]


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


# ---------------------------------------------------------------------------
# File enumeration
# ---------------------------------------------------------------------------


def _matches_exclude(rel: str) -> bool:
    """Check if a relative path matches any exclusion rule."""
    for pattern in EXCLUDE_GLOBS:
        if pattern.endswith("/"):
            # Directory prefix exclusion
            if rel.startswith(pattern) or rel + "/" == pattern:
                return True
        elif pattern.endswith("/*"):
            # Glob-style: directory/* (but not subdirs)
            prefix = pattern[:-1]  # e.g. ".remember/today-*" → we keep the *
            if "*" in prefix:
                # Wildcard match
                import fnmatch
                if fnmatch.fnmatch(rel, pattern):
                    return True
            else:
                dir_prefix = pattern[:-2]  # e.g. ".ai-badger/task-tracking/"
                if rel.startswith(dir_prefix):
                    return True
        else:
            # Exact file match
            if rel == pattern:
                return True
    return False


def classify_file(rel: str) -> tuple[str, str]:
    """Return (type_key, context_label) for a relative path.

    Type keys: adr, architecture, explanation, howto, reference, rules,
               invariants, skills, agents, instructions, config, agent-model,
               remember, tutorials, legacy, meta, root-md, infra
    """
    # ADRs
    if rel.startswith("docs/adr/"):
        return ("adr", "docs:adr")

    # Architecture docs (top-level docs/*.md except root entries)
    architecture_docs = {
        "docs/architecture.md",
        "docs/data-model.md",
        "docs/flows.md",
        "docs/requirements.md",
        "docs/CHANGELOG.md",
    }
    if rel in architecture_docs:
        return ("architecture", "docs:architecture")

    # Explanation
    if rel.startswith("docs/explanation/"):
        return ("explanation", "docs:explanation")

    # How-to
    if rel.startswith("docs/how-to/"):
        return ("howto", "docs:how-to")

    # Reference
    if rel.startswith("docs/reference/"):
        return ("reference", "docs:reference")

    # Rules
    if rel.startswith("docs/rules/"):
        return ("rules", "docs:rules")

    # Tutorials
    if rel.startswith("docs/tutorials/"):
        return ("tutorials", "docs:tutorials")

    # Legacy
    if rel.startswith("docs/legacy/"):
        return ("legacy", "docs:legacy")

    # Meta
    if rel.startswith("docs/meta/"):
        return ("meta", "docs:meta")

    # Top-level docs/ (catch-all for architecture-like docs: README.md etc.)
    if rel.startswith("docs/") and rel != "docs/README.md":
        return ("architecture", "docs:architecture")

    # Invariants
    if rel.startswith(".ai-badger/invariants/"):
        return ("invariants", "ai-badger:invariants")

    # Skills (SKILL.md files and their reference files)
    if rel.startswith(".ai-badger/skills/"):
        return ("skills", "ai-badger:skills")

    # Agents
    if rel.startswith(".ai-badger/agents/"):
        return ("agents", "ai-badger:agents")

    # Instructions
    if rel.startswith(".ai-badger/instructions/"):
        return ("instructions", "ai-badger:instructions")

    # Agent-instructions (model)
    if rel.startswith(".ai-badger/agent-instructions/"):
        return ("agent-model", "ai-badger:instructions")

    # Config / delegation / copilot-instructions
    config_files = {
        ".ai-badger/config.json",
        ".ai-badger/delegation.md",
        ".ai-badger/copilot-instructions.md",
    }
    if rel in config_files:
        return ("config", "ai-badger:instructions")

    # Remember
    if rel.startswith(".remember/"):
        return ("remember", "remember:operational")

    # Root markdown (also catches docs/README.md)
    root_md = {"README.md", "CLAUDE.md", "HERMES.md", "REVIEW.md", "docs/README.md"}
    if rel in root_md:
        return ("root-md", "docs:architecture")

    # Infra
    if rel.startswith("infra/"):
        return ("infra", "docs:architecture")

    # Default fallback
    return ("unknown", "docs:architecture")


def enumerate_files() -> list[tuple[Path, str, str]]:
    """Walk JSAA_ROOT and return [(absolute_path, relative_path, type_key), ...].

    Sorted for determinism.
    """
    # Collect all include matches
    included: dict[str, Path] = {}  # rel_path → abs_path

    for pattern in INCLUDE_GLOBS:
        if "*" in pattern:
            # Glob expansion
            for p in JSAA_ROOT.glob(pattern):
                if p.is_file():
                    rel = str(p.relative_to(JSAA_ROOT))
                    if not _matches_exclude(rel):
                        included[rel] = p
        elif pattern.endswith("/*"):
            # Directory/* : all files in dir (non-recursive)
            dir_glob = JSAA_ROOT / pattern
            for p in dir_glob.parent.glob(dir_glob.name):
                if p.is_file():
                    rel = str(p.relative_to(JSAA_ROOT))
                    if not _matches_exclude(rel):
                        included[rel] = p
        else:
            # Exact file path
            p = JSAA_ROOT / pattern
            if p.is_file():
                rel = str(p.relative_to(JSAA_ROOT))
                if not _matches_exclude(rel):
                    included[rel] = p

    # Also include skill reference files
    for skill_dir in (JSAA_ROOT / ".ai-badger/skills").iterdir():
        if not skill_dir.is_dir():
            continue
        refs_dir = skill_dir / "references"
        if refs_dir.is_dir():
            for ref_file in refs_dir.rglob("*"):
                if ref_file.is_file():
                    rel = str(ref_file.relative_to(JSAA_ROOT))
                    if not _matches_exclude(rel):
                        included[rel] = ref_file

    results: list[tuple[Path, str, str]] = []
    for rel in sorted(included):
        type_key, _ = classify_file(rel)
        results.append((included[rel], rel, type_key))

    return results


# ---------------------------------------------------------------------------
# Content-type chunking (design §3.4)
# ---------------------------------------------------------------------------


def read_file(path: Path) -> str:
    """Read file content, return '' on any error."""
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        log.warning("Cannot read: %s", path)
        return ""


def _make_chunk_content(body: str, structured_path: str) -> str:
    """Embed the structured path as a ## Source header in the content."""
    return f"## Source: {structured_path}\n\n{body}"


def _chunk_path(rel: str, section: str) -> str:
    """Build the structured path for a chunk."""
    source_prefix = _source_prefix(rel)
    # Strip directory prefix already captured in source_prefix (e.g. "docs/adr/..." → "0011-...")
    short_rel = _short_rel(rel)
    if section:
        return f"{source_prefix}:{short_rel}#{section}"
    return f"{source_prefix}:{short_rel}"


def _short_rel(rel: str) -> str:
    """Strip leading directories already captured in source_prefix."""
    if rel.startswith("docs/"):
        # Strip "docs/<sub>/" to get the filename — source_prefix already has docs:<sub>
        parts = rel.split("/", 2)
        if len(parts) >= 3:
            return parts[2]  # After "docs/adr/"
        if len(parts) == 2:
            return parts[1]  # After "docs/" (top-level docs)
        return rel
    if rel.startswith(".ai-badger/"):
        return rel[len(".ai-badger/"):]
    if rel.startswith(".remember/"):
        return rel[len(".remember/"):]
    return rel


def _source_prefix(rel: str) -> str:
    """Map a relative path to its source prefix matching design doc format."""
    if rel.startswith("docs/"):
        # Sub-category prefix: docs:adr, docs:architecture, etc.
        sub = rel.split("/")[1] if "/" in rel else ""
        if sub in ("adr",):
            return "docs:adr"
        if sub in ("explanation",):
            return "docs:explanation"
        if sub in ("how-to",):
            return "docs:how-to"
        if sub in ("reference",):
            return "docs:reference"
        if sub in ("rules",):
            return "docs:rules"
        if sub in ("tutorials",):
            return "docs:tutorials"
        if sub in ("legacy",):
            return "docs:legacy"
        if sub in ("meta",):
            return "docs:meta"
        # Top-level docs (architecture.md, flows.md, etc.)
        return "docs:architecture"
    if rel.startswith(".ai-badger/"):
        return "ai-badger"
    if rel.startswith(".remember/"):
        return "remember"
    if rel.startswith("infra/"):
        return "infra"
    if rel in ("README.md", "CLAUDE.md", "HERMES.md", "REVIEW.md"):
        return "docs"
    return "unknown"


def _extract_title(text: str) -> str:
    """Extract the H1 title from markdown text."""
    m = re.search(r"^#\s+(.+)$", text, re.MULTILINE)
    return m.group(1).strip() if m else ""


def _split_by_h2(text: str) -> list[tuple[str, str]]:
    """Split markdown text by ## headers.

    Returns [(section_name, section_body), ...] where section_name is the heading
    text (without ##). Any content before the first ## is returned as ("preamble", ...).
    """
    parts = re.split(r"^##\s+", text, flags=re.MULTILINE)
    if len(parts) <= 1:
        return [("body", parts[0].strip())]

    chunks: list[tuple[str, str]] = []
    # First part is text before any ##
    preamble = parts[0].strip()
    if preamble:
        chunks.append(("preamble", preamble))

    for i in range(1, len(parts)):
        section = parts[i]
        # Split on first newline to get heading name
        nl = section.find("\n")
        if nl == -1:
            name = section.strip()
            body = ""
        else:
            name = section[:nl].strip()
            body = section[nl + 1:].strip()
        chunks.append((name, body))

    return chunks


def chunk_adr(rel: str, text: str, _type_key: str, context: str) -> list[Chunk]:
    """Parse Nygard ADR sections. Combine short ADRs (<1000 chars) into one chunk."""
    total_len = len(text)
    title = _extract_title(text)

    if total_len < 1000:
        # Short ADR: single chunk
        path = _chunk_path(rel, "")
        return [Chunk(structured_path=path, content=_make_chunk_content(text.strip(), path), context=context)]

    # Split by Nygard sections
    sections = _split_by_h2(text)
    chunks: list[Chunk] = []

    for sec_name, sec_body in sections:
        if not sec_body.strip():
            continue
        # Normalize section name: strip trailing parens like "(optional)"
        clean_name = re.sub(r"\s*\(.*?\)\s*$", "", sec_name).strip().lower()
        # Map to known Nygard names
        if clean_name in ("context", "decision", "consequences", "alternatives", "status"):
            section_slug = clean_name
        elif sec_name == "preamble":
            section_slug = "header"
        else:
            section_slug = re.sub(r"[^a-z0-9-]+", "-", clean_name).strip("-")

        path = _chunk_path(rel, section_slug)
        # For non-preamble sections, include the H1 title context
        body = f"# {title}\n\n## {sec_name}\n\n{sec_body}" if title else sec_body
        chunks.append(Chunk(structured_path=path, content=_make_chunk_content(body, path), context=context))

    return chunks


def chunk_heading(rel: str, text: str, _type_key: str, context: str) -> list[Chunk]:
    """Split by ## headers, prepend H1 title. Fallback to whole file."""
    title = _extract_title(text)
    sections = _split_by_h2(text)

    if len(sections) == 1 and sections[0][0] == "body":
        # No ## sections: whole file as one chunk
        path = _chunk_path(rel, "")
        return [Chunk(structured_path=path, content=_make_chunk_content(text.strip(), path), context=context)]

    chunks: list[Chunk] = []
    for sec_name, sec_body in sections:
        if not sec_body.strip():
            continue
        section_slug = re.sub(r"[^a-z0-9-]+", "-", sec_name.strip()).strip("-").lower()
        path = _chunk_path(rel, section_slug)
        body = f"# {title}\n\n## {sec_name}\n\n{sec_body}" if title else f"## {sec_name}\n\n{sec_body}"
        chunks.append(Chunk(structured_path=path, content=_make_chunk_content(body, path), context=context))

    return chunks


def chunk_atomic(rel: str, text: str, _type_key: str, context: str) -> list[Chunk]:
    """One file = one chunk. No splitting."""
    path = _chunk_path(rel, "")
    return [Chunk(structured_path=path, content=_make_chunk_content(text.strip(), path), context=context)]


def chunk_skill(rel: str, text: str, type_key: str, context: str) -> list[Chunk]:
    """One chunk per SKILL.md. Reference files get separate chunks."""
    # Determine skill name from path
    path_parts = rel.split("/")
    skill_name = ""
    for i, part in enumerate(path_parts):
        if part == "skills" and i + 1 < len(path_parts):
            skill_name = path_parts[i + 1]
            break

    if "/references/" in rel:
        # This is a reference file — classified by the caller
        ctx = f"{context}:{skill_name}:references" if skill_name else context
        return chunk_atomic(rel, text, type_key, ctx)

    # SKILL.md itself
    return chunk_atomic(rel, text, type_key, context)


def chunk_remember(rel: str, text: str, _type_key: str, context: str) -> list[Chunk]:
    """Split by ## Week of or ## date headers."""
    # Use a more careful split that captures the header
    parts = re.split(r"^(##\s+(?:Week\s+of\s+\d{4}-\d{2}-\d{2}|\d{4}-\d{2}-\d{2}).*)$", text, flags=re.MULTILINE)

    if len(parts) <= 1:
        return chunk_atomic(rel, text, _type_key, context)

    chunks: list[Chunk] = []
    current_header = ""

    # First part is text before any temporal header
    preamble = parts[0].strip()
    if preamble:
        path = _chunk_path(rel, "preamble")
        chunks.append(Chunk(structured_path=path, content=_make_chunk_content(preamble, path), context=context))

    # Subsequent parts: header, content pairs
    i = 1
    while i < len(parts):
        header_line = parts[i].strip()
        body = parts[i + 1].strip() if i + 1 < len(parts) else ""
        if body:
            # Extract date slug from header
            date_match = re.search(r"(\d{4}-\d{2}-\d{2})", header_line)
            section_slug = date_match.group(1) if date_match else re.sub(r"[^a-z0-9-]+", "-", header_line.strip("#").strip()).strip("-").lower()
            path = _chunk_path(rel, section_slug)
            chunks.append(Chunk(structured_path=path, content=_make_chunk_content(f"{header_line}\n\n{body}", path), context=context))
        i += 2

    return chunks if chunks else chunk_atomic(rel, text, _type_key, context)


def chunk_rules(rel: str, text: str, _type_key: str, context: str) -> list[Chunk]:
    """Parse markdown tables: each rule row becomes a chunk with preamble."""
    # Find the preamble (everything before the first table)
    table_start = text.find("|")
    if table_start == -1:
        return chunk_heading(rel, text, _type_key, context)

    preamble = text[:table_start].strip()
    table_section = text[table_start:]

    # Parse the table rows
    lines = table_section.strip().split("\n")
    headers: list[str] = []
    rows: list[str] = []
    in_header = True

    for line in lines:
        line = line.strip()
        if not line.startswith("|"):
            continue
        if "---" in line and "|" in line:
            in_header = False
            continue
        if in_header:
            headers.append(line)
        else:
            rows.append(line)

    if not rows:
        return chunk_heading(rel, text, _type_key, context)

    chunks: list[Chunk] = []
    for i, row in enumerate(rows):
        # Extract rule ID from first column
        cols = [c.strip() for c in row.strip("|").split("|")]
        rule_id = cols[0] if cols else f"row-{i + 1}"
        rule_id_slug = re.sub(r"[^a-zA-Z0-9-]+", "-", rule_id).strip("-")

        path = _chunk_path(rel, rule_id_slug)
        body = f"{preamble}\n\nRow {i + 1}: {row}" if preamble else row
        chunks.append(Chunk(structured_path=path, content=_make_chunk_content(body, path), context=context))

    return chunks


# Map type_key → chunker function
CHUNKER_MAP = {
    "adr": chunk_adr,
    "architecture": chunk_heading,
    "explanation": chunk_heading,
    "howto": chunk_heading,
    "reference": chunk_heading,
    "rules": chunk_rules,
    "tutorials": chunk_heading,
    "legacy": chunk_heading,
    "meta": chunk_atomic,
    "invariants": chunk_atomic,
    "skills": chunk_skill,
    "agents": chunk_atomic,
    "instructions": chunk_atomic,
    "config": chunk_atomic,
    "agent-model": chunk_atomic,
    "remember": chunk_remember,
    "root-md": chunk_heading,
    "infra": chunk_heading,
}


def chunk_file(rel: str, text: str, type_key: str, context: str) -> list[Chunk]:
    """Dispatch to the appropriate chunker based on type_key."""
    if not text.strip():
        log.warning("Empty file, 0 chunks: %s", rel)
        return []

    chunker = CHUNKER_MAP.get(type_key, chunk_atomic)
    chunks = chunker(rel, text, type_key, context)

    # Frontmatter-only detection: if content is just YAML frontmatter, emit single chunk with warning
    stripped = text.strip()
    if stripped.startswith("---") and len(chunks) == 0:
        log.warning("Frontmatter-only file, emitting as single chunk: %s", rel)
        return chunk_atomic(rel, text, type_key, context)

    return chunks


# ---------------------------------------------------------------------------
# Hash map computation
# ---------------------------------------------------------------------------


def compute_expected_hash(content: str) -> str:
    """Compute AiRaccoon's content hash.

    AiRaccoon computes:
      assigned_path = SHA256(content).hex() + ".md"
      expected_hash = SHA256(UTF8(assigned_path) + UTF8(content)).hex()
    """
    content_hash = hashlib.sha256(content.encode("utf-8")).hexdigest()
    assigned_path = content_hash + ".md"
    return hashlib.sha256(assigned_path.encode("utf-8") + content.encode("utf-8")).hexdigest()


def chunk_written_content(chunk: Chunk) -> str:
    """The actual content sent to AiRaccoon, with metadata prefix."""
    return f"[{chunk.context}] {chunk.structured_path}\n\n{chunk.content}"


def build_hash_map(chunks: list[Chunk]) -> dict[str, str]:
    """Build {structured_path: expected_hash} for all chunks, using written content."""
    return {chunk.structured_path: compute_expected_hash(chunk_written_content(chunk)) for chunk in chunks}


# ---------------------------------------------------------------------------
# MCP batch-write pipeline
# ---------------------------------------------------------------------------


async def write_chunks_batched(
    client: AiRaccoonClient,
    http: httpx.AsyncClient,
    chunks: list[Chunk],
    dry_run: bool = False,
) -> int:
    """Write chunks in batches of BATCH_SIZE, embed after each batch.

    Returns number of chunks written.
    """
    total = len(chunks)
    written = 0

    for batch_start in range(0, total, BATCH_SIZE):
        batch = chunks[batch_start : batch_start + BATCH_SIZE]
        batch_num = batch_start // BATCH_SIZE + 1
        total_batches = (total + BATCH_SIZE - 1) // BATCH_SIZE

        if dry_run:
            log.info("[batch %d/%d] would write %d chunks (dry-run)", batch_num, total_batches, len(batch))
            continue

        # Write each chunk in the batch
        for chunk in batch:
            # Embed context label and structured path in content — AiRaccoon's memory_write
            # does NOT have path/context params (passing context sets scope='custom', invisible
            # to project-scoped search and stats). The metadata becomes part of content.
            content_with_meta = f"[{chunk.context}] {chunk.structured_path}\n\n{chunk.content}"
            await client.memory_write(
                http,
                project_id=PROJECT_ID,
                content=content_with_meta,
                agent_id=chunk.structured_path,
            )
            written += 1

        # Embed pending after batch
        embed_result = await client.memory_embed_pending(http, project_id=PROJECT_ID)
        processed = embed_result.get("processed", 0) if isinstance(embed_result, dict) else "?"
        log.info(
            "[batch %d/%d] wrote %d chunks, %s/%d embedded",
            batch_num,
            total_batches,
            len(batch),
            processed,
            written,
        )

    # Final embed (omit limit = all pending)
    if not dry_run:
        final = await client.memory_embed_pending(http, project_id=PROJECT_ID)
        log.info("Final embed: %s", final)

    return written


# ---------------------------------------------------------------------------
# Verification spot-checks
# ---------------------------------------------------------------------------


SPOT_CHECKS = [
    ("what is the frontend component library?", "ADR-0011"),
    ("TDD policy", "tdd-mandatory"),
    ("Cosmos DB partition key strategy", "partition-by-userid"),
    ("channel monitoring architecture", "ADR-0024 or ADR-0061"),
]


async def run_spot_checks(client: AiRaccoonClient, http: httpx.AsyncClient) -> None:
    """Run verification spot-checks and log results."""
    log.info("━━━ SPOT-CHECKS ━━━")
    for query, expected in SPOT_CHECKS:
        result = await client.memory_search(http, PROJECT_ID, query, scope="project", limit=5, min_score=0.0)
        if isinstance(result, dict) and "results" in result:
            top3_paths = [r.get("path", "?") for r in result["results"][:3]]
            found = any(expected.lower() in (p or "").lower() for p in top3_paths)
            status = "✓" if found else "✗"
            log.info("  %s query=%r  expected=%s  top3=%s", status, query, expected, top3_paths)
        else:
            log.warning("  ? query=%r  unexpected response: %s", query, str(result)[:200])


# ---------------------------------------------------------------------------
# Reset (delete all contexts)
# ---------------------------------------------------------------------------


async def reset_contexts(client: AiRaccoonClient, http: httpx.AsyncClient) -> None:
    """Delete all known contexts. Requires full access mode."""
    for ctx in CONTEXTS_TO_DELETE:
        try:
            result = await client.memory_delete_context(http, PROJECT_ID, ctx)
            log.info("Deleted context %s: %s", ctx, result)
        except httpx.HTTPStatusError:
            log.warning("Failed to delete context %s (may not exist)", ctx)


# ---------------------------------------------------------------------------
# Main pipeline
# ---------------------------------------------------------------------------


async def run_pipeline(
    dry_run: bool = False,
    chunk_only: bool = False,
    ingest_only: bool = False,
    verify: bool = False,
    reset: bool = False,
) -> None:
    """Execute the full ingestion pipeline."""
    t0 = time.monotonic()

    # ── 1. Enumerate ──
    log.info("━━━ PHASE 1: File enumeration ━━━")
    files = enumerate_files()
    log.info("Found %d files to ingest", len(files))

    # Breakdown by type
    type_counts: dict[str, int] = {}
    for _, rel, type_key in files:
        type_counts[type_key] = type_counts.get(type_key, 0) + 1
    for tk in sorted(type_counts):
        log.info("  %s: %d", tk, type_counts[tk])

    if dry_run and not chunk_only:
        log.info("Dry-run complete: %d files enumerated.", len(files))
        return

    # ── 2. Chunk ──
    log.info("━━━ PHASE 2: Chunking ━━━")
    chunks: list[Chunk] = []
    warnings: list[str] = []

    for abs_path, rel, type_key in files:
        _, context = classify_file(rel)
        text = read_file(abs_path)
        file_chunks = chunk_file(rel, text, type_key, context)
        chunks.extend(file_chunks)

        if len(file_chunks) == 0 and text.strip():
            warnings.append(rel)

    log.info("Produced %d chunks from %d files", len(chunks), len(files))
    if warnings:
        log.warning("  %d files produced 0 chunks: %s", len(warnings), warnings[:10])

    # Context breakdown
    ctx_counts: dict[str, int] = {}
    for c in chunks:
        ctx_counts[c.context] = ctx_counts.get(c.context, 0) + 1
    for ctx in sorted(ctx_counts):
        log.info("  context %s: %d chunks", ctx, ctx_counts[ctx])

    # ── 3. Hash map ──
    log.info("━━━ PHASE 3: Hash map ━━━")
    hash_map = build_hash_map(chunks)
    HASH_MAP_PATH.write_text(json.dumps(hash_map, indent=2, sort_keys=True))
    log.info("Wrote %d entries to %s", len(hash_map), HASH_MAP_PATH)

    if chunk_only:
        log.info("Chunk-only complete: %d chunks, hash map written.", len(chunks))
        return

    # ── 4. MCP writes ──
    log.info("━━━ PHASE 4: MCP writes ━━━")
    client = AiRaccoonClient()

    async with httpx.AsyncClient() as http:
        # Reset if requested
        if reset:
            log.info("Resetting contexts...")
            await reset_contexts(client, http)

        # Configure
        cfg = await client.memory_configure(http, PROJECT_ID, "local")
        log.info("memory_configure → %s", cfg)

        # Write batches
        written = await write_chunks_batched(client, http, chunks, dry_run=False)
        log.info("Wrote %d chunks total", written)

        # Stats
        stats = await client.memory_stats(http, PROJECT_ID)
        log.info("memory_stats → %s", stats)

        # Verify spot-checks
        if verify:
            log.info("━━━ PHASE 5: Spot-checks ━━━")
            await run_spot_checks(client, http)

    elapsed = time.monotonic() - t0
    log.info("━━━ DONE (%.1fs) ━━━", elapsed)
    log.info("Chunks: %d  |  Hash map entries: %d", len(chunks), len(hash_map))


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Ingest job-search-ai-assistant documentation into AiRaccoon memory."
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Enumerate files only, no chunking or MCP writes.",
    )
    parser.add_argument(
        "--chunk-only",
        action="store_true",
        help="Enumerate + chunk + hash map, no MCP writes.",
    )
    parser.add_argument(
        "--ingest-only",
        action="store_true",
        help="Full ingest without spot-checks.",
    )
    parser.add_argument(
        "--verify",
        action="store_true",
        help="Full ingest with verification spot-checks.",
    )
    parser.add_argument(
        "--reset",
        action="store_true",
        help="Delete all contexts before re-ingesting (requires full access mode).",
    )

    args = parser.parse_args()

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )

    # Determine mode from flags (default: --ingest-only behaviour)
    dry_run = args.dry_run
    chunk_only = args.chunk_only
    ingest_only = args.ingest_only
    verify = args.verify
    reset = args.reset

    # If no flag passed, default to ingest-only
    if not any([dry_run, chunk_only, ingest_only, verify, reset]):
        ingest_only = True

    # --verify implies ingest
    if verify:
        ingest_only = True

    asyncio.run(run_pipeline(
        dry_run=dry_run,
        chunk_only=chunk_only,
        ingest_only=ingest_only,
        verify=verify,
        reset=reset,
    ))


if __name__ == "__main__":
    main()
