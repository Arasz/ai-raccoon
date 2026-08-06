"""Ingestion pipeline orchestration (moved verbatim from scripts/ingest-jsaa-docs.py).

Network + git — smoke-covered by the wrapper's --chunk-only mode, not unit-tested.
"""

from __future__ import annotations

import json
import logging
import re
import subprocess
import time
from pathlib import Path

import httpx

from chunking import Chunk, chunk_file
from hash_map import build_hash_map
from jsaa_config import (
    BATCH_SIZE,
    CONTEXTS_TO_DELETE,
    HASH_MAP_PATH,
    JSAA_PINNED_COMMIT,
    JSAA_ROOT,
    PROJECT_ID,
    SPOT_CHECKS,
)
from mcp_client import AiRaccoonClient
from sources import classify_file, enumerate_files

log = logging.getLogger("ingest")


def read_file(path: Path) -> str:
    """Read file content, return '' on any error."""
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        log.warning("Cannot read: %s", path)
        return ""


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

        # Write each chunk in the batch. Provenance travels in the source_file/section
        # parameters (Wave 2, plan C §3 2d) — the content itself carries no [context]
        # prefix or ## Source: header, so BM25 and the embeddings see clean body text.
        for chunk in batch:
            section = chunk.structured_path.split("#", 1)[1] if "#" in chunk.structured_path else None
            await client.memory_write(
                http,
                project_id=PROJECT_ID,
                content=chunk.content,
                source_file=chunk.source_file,
                section=section,
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
        if isinstance(embed_result, dict) and embed_result.get("processed", 0) == 0 and embed_result.get("pending", 0) > 0:
            log.warning(
                "No embedding provider configured (%d rows stay pending). "
                "Fix with `ai-raccoon model set local` (single config channel) and re-run.",
                embed_result.get("pending", 0),
            )

    # Final embed (omit limit = all pending)
    if not dry_run:
        final = await client.memory_embed_pending(http, project_id=PROJECT_ID)
        log.info("Final embed: %s", final)

    return written


async def run_spot_checks(client: AiRaccoonClient, http: httpx.AsyncClient) -> None:
    """Run verification spot-checks and log results."""
    log.info("━━━ SPOT-CHECKS ━━━")
    for query, expected in SPOT_CHECKS:
        result = await client.memory_search(http, PROJECT_ID, query, scope="project", limit=5, min_score=0.0)
        if isinstance(result, dict) and "results" in result:
            # `path` is the hash-derived filename (WritePathFor), so the structured path
            # lives in sourceFile (Wave 2) and the snippet — match expected against all
            # three (hash-map contract: match by path prefix, not exact section).
            top3_paths = [r.get("path", "?") for r in result["results"][:3]]
            top3_snips = [(r.get("snippet", "") or "") for r in result["results"][:3]]
            top3_sources = [(r.get("sourceFile") or "") for r in result["results"][:3]]

            def _matches_expected(p: str, s: str, src: str) -> bool:
                e = expected.lower()
                if e in (p or "").lower() or e in s.lower() or e in src.lower():
                    return True
                # ADR literals use "ADR-0011" but stored paths use "docs:adr:0011-…"
                m = re.search(r"adr-(\d{3,4})", e)
                return bool(m and (m.group(1) in s.lower() or m.group(1) in src.lower()))

            found = any(_matches_expected(p, s, src) for p, s, src in zip(top3_paths, top3_snips, top3_sources))
            status = "✓" if found else "✗"
            log.info("  %s query=%r  expected=%s  top3=%s", status, query, expected, top3_sources or top3_paths)
        else:
            log.warning("  ? query=%r  unexpected response: %s", query, str(result)[:200])


async def reset_contexts(client: AiRaccoonClient, http: httpx.AsyncClient) -> None:
    """Delete the project's committed rows (project:<id> scope). Requires full access mode."""
    for ctx in CONTEXTS_TO_DELETE:
        try:
            result = await client.memory_delete_context(http, PROJECT_ID, ctx)
            log.info("Deleted context %s: %s", ctx, result)
        except httpx.HTTPStatusError:
            log.warning("Failed to delete context %s (may not exist)", ctx)


def verify_jsaa_pin() -> None:
    """Abort unless the jsaa tree is at JSAA_PINNED_COMMIT.

    The canonical corpus must be reproducible on a clean checkout; ingesting
    from a different jsaa commit would silently change every hash.
    """
    try:
        head = subprocess.run(
            ["git", "-C", str(JSAA_ROOT), "rev-parse", "HEAD"],
            capture_output=True,
            text=True,
            check=True,
            timeout=30,
        ).stdout.strip()
    except (subprocess.CalledProcessError, FileNotFoundError) as exc:
        raise SystemExit(f"FATAL: cannot read jsaa HEAD at {JSAA_ROOT}: {exc}")
    if head != JSAA_PINNED_COMMIT:
        raise SystemExit(
            f"FATAL: jsaa HEAD {head} != pinned {JSAA_PINNED_COMMIT}. "
            f"Check out the pinned commit in {JSAA_ROOT} and re-run."
        )
    log.info("jsaa HEAD verified: %s (matches pin)", head)


async def run_pipeline(
    dry_run: bool = False,
    chunk_only: bool = False,
    ingest_only: bool = False,
    verify: bool = False,
    reset: bool = False,
) -> None:
    """Execute the full ingestion pipeline."""
    t0 = time.monotonic()

    # ── 0. Pin check (plan C Wave 0: reproducible corpus) ──
    verify_jsaa_pin()

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
