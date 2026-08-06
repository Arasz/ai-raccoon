"""Content-type chunking for the JSAA docs ingestion pipeline (moved verbatim from scripts/ingest-jsaa-docs.py)."""

from __future__ import annotations

import logging
import re
from dataclasses import dataclass

log = logging.getLogger("ingest")


@dataclass
class Chunk:
    """A single retrievable unit of knowledge."""

    structured_path: str   # e.g. "docs:adr/0011-frontend-chassis-stack.md#decision"
    content: str           # markdown body (no embedded provenance — Wave 2 plan C §3 2d)
    context: str           # typed context label e.g. "docs:adr"
    source_file: str       # original relative path, e.g. "docs/adr/0011-frontend-chassis-stack.md"


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
        return [Chunk(structured_path=path, content=text.strip(), context=context, source_file=rel)]

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
        chunks.append(Chunk(structured_path=path, content=body, context=context, source_file=rel))

    return chunks


def chunk_heading(rel: str, text: str, _type_key: str, context: str) -> list[Chunk]:
    """Split by ## headers. Fallback to whole file."""
    sections = _split_by_h2(text)

    if len(sections) == 1 and sections[0][0] == "body":
        # No ## sections: whole file as one chunk
        path = _chunk_path(rel, "")
        return [Chunk(structured_path=path, content=text.strip(), context=context, source_file=rel)]

    chunks: list[Chunk] = []
    for sec_name, sec_body in sections:
        if not sec_body.strip():
            continue
        # Lowercase BEFORE the regex — [^a-z0-9-] would otherwise eat leading
        # capitals ("## Framework" -> "#ramework").
        section_slug = re.sub(r"[^a-z0-9-]+", "-", sec_name.strip().lower()).strip("-")
        path = _chunk_path(rel, section_slug)
        # Wave 2 (plan C §3 2d): no H1 title in section chunks — the title is file-level
        # provenance; repeating it in every chunk pollutes BM25. The section heading stays.
        body = f"## {sec_name}\n\n{sec_body}"
        chunks.append(Chunk(structured_path=path, content=body, context=context, source_file=rel))

    return chunks


def chunk_atomic(rel: str, text: str, _type_key: str, context: str) -> list[Chunk]:
    """One file = one chunk. No splitting."""
    path = _chunk_path(rel, "")
    return [Chunk(structured_path=path, content=text.strip(), context=context, source_file=rel)]


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
        chunks.append(Chunk(structured_path=path, content=preamble, context=context, source_file=rel))

    # Subsequent parts: header, content pairs
    i = 1
    while i < len(parts):
        header_line = parts[i].strip()
        body = parts[i + 1].strip() if i + 1 < len(parts) else ""
        if body:
            # Extract date slug from header
            date_match = re.search(r"(\d{4}-\d{2}-\d{2})", header_line)
            section_slug = date_match.group(1) if date_match else re.sub(r"[^a-z0-9-]+", "-", header_line.strip("#").strip().lower()).strip("-")
            path = _chunk_path(rel, section_slug)
            chunks.append(Chunk(structured_path=path, content=f"{header_line}\n\n{body}", context=context, source_file=rel))
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
        chunks.append(Chunk(structured_path=path, content=body, context=context, source_file=rel))

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
