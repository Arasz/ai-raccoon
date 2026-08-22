"""Ingestion pipeline orchestration (moved verbatim from scripts/ingest-jsaa-docs.py).

Chunking is the production FileIngestor's job (via the memory_ingest_file MCP
tool), not this script's — see ADR-0042. This module only curates *which*
files to feed it and drives the batched writes/embeds/spot-checks.

Network + git — smoke-covered by the wrapper's --dry-run mode, not unit-tested.
"""

from __future__ import annotations

import logging
import re
import subprocess
import time
from pathlib import Path

import httpx

from jsaa_config import (
    BATCH_SIZE,
    CONTEXTS_TO_DELETE,
    JSAA_PINNED_COMMIT,
    JSAA_PINNED_COMMIT_ENV,
    JSAA_ROOT,
    JSAA_ROOT_ENV,
    PROJECT_ID,
    SPOT_CHECKS,
)
from mcp_client import AiRaccoonClient
from sources import classify_file, enumerate_files

log = logging.getLogger("ingest")


async def ingest_files_batched(
    client: AiRaccoonClient,
    http: httpx.AsyncClient,
    files: list[tuple[Path, str, str]],
    dry_run: bool = False,
) -> int:
    """Ingest files in batches of BATCH_SIZE via memory_ingest_file, embed after each batch.

    Returns number of files ingested (0-chunk files, e.g. binary/unsupported
    extensions, still count as attempted but contribute nothing).
    """
    total = len(files)
    ingested = 0

    for batch_start in range(0, total, BATCH_SIZE):
        batch = files[batch_start: batch_start + BATCH_SIZE]
        batch_num = batch_start // BATCH_SIZE + 1
        total_batches = (total + BATCH_SIZE - 1) // BATCH_SIZE

        if dry_run:
            log.info("[batch %d/%d] would ingest %d files (dry-run)", batch_num, total_batches, len(batch))
            continue

        for abs_path, rel, _type_key in batch:
            _, context = classify_file(rel)
            await client.memory_ingest_file(
                http,
                project_id=PROJECT_ID,
                path=str(abs_path),
                context=context,
            )
            ingested += 1

        embed_result = await client.memory_embed_pending(http, project_id=PROJECT_ID)
        processed = embed_result.get("processed", 0) if isinstance(embed_result, dict) else "?"
        log.info(
            "[batch %d/%d] ingested %d files, %s/%d embedded",
            batch_num,
            total_batches,
            len(batch),
            processed,
            ingested,
        )
        if isinstance(embed_result, dict) and embed_result.get("processed", 0) == 0 and embed_result.get("pending", 0) > 0:
            log.warning(
                "No embedding provider configured (%d rows stay pending). "
                "Fix with `ai-raccoon model set local` (single config channel) and re-run.",
                embed_result.get("pending", 0),
            )

    if not dry_run:
        final = await client.memory_embed_pending(http, project_id=PROJECT_ID)
        log.info("Final embed: %s", final)

    return ingested


async def run_spot_checks(client: AiRaccoonClient, http: httpx.AsyncClient) -> None:
    """Run verification spot-checks and log results."""
    log.info("━━━ SPOT-CHECKS ━━━")
    for query, expected in SPOT_CHECKS:
        result = await client.memory_search(http, PROJECT_ID, query, scope="project", limit=5, min_score=0.0)
        if isinstance(result, dict) and "results" in result:
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
    from a different jsaa commit would silently change what gets indexed.
    """
    if JSAA_ROOT is None:
        raise SystemExit(
            f"FATAL: {JSAA_ROOT_ENV} is not set. This CLI ingests a private checkout that "
            "lives outside this repository; point it at that tree and re-run."
        )
    if not JSAA_PINNED_COMMIT:
        raise SystemExit(
            f"FATAL: {JSAA_PINNED_COMMIT_ENV} is not set. Ingesting an unpinned tree would "
            "silently change what gets indexed; set the pin and re-run."
        )
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
    files = enumerate_files(JSAA_ROOT)
    log.info("Found %d files to ingest", len(files))

    type_counts: dict[str, int] = {}
    for _, rel, type_key in files:
        type_counts[type_key] = type_counts.get(type_key, 0) + 1
    for tk in sorted(type_counts):
        log.info("  %s: %d", tk, type_counts[tk])

    # chunk_only has no local meaning post-ADR-0042 (chunking moved server-side
    # into memory_ingest_file) — it now behaves like --dry-run: enumerate and stop.
    if dry_run or chunk_only:
        log.info("Enumeration complete: %d files. No local chunking to preview — "
                 "the production chunker runs server-side inside memory_ingest_file.", len(files))
        return

    # ── 2. MCP ingest ──
    log.info("━━━ PHASE 2: MCP ingest ━━━")
    log.info("Requires ingest scope configured first: "
             "ai-raccoon ingest scope add %s %s", PROJECT_ID, JSAA_ROOT)
    client = AiRaccoonClient()

    async with httpx.AsyncClient() as http:
        if reset:
            log.info("Resetting contexts...")
            await reset_contexts(client, http)

        ingested = await ingest_files_batched(client, http, files, dry_run=False)
        log.info("Ingested %d files total", ingested)

        stats = await client.memory_stats(http, PROJECT_ID)
        log.info("memory_stats → %s", stats)

        if verify:
            log.info("━━━ PHASE 3: Spot-checks ━━━")
            await run_spot_checks(client, http)

    elapsed = time.monotonic() - t0
    log.info("━━━ DONE (%.1fs) ━━━", elapsed)
    log.info("Files ingested: %d", ingested)
