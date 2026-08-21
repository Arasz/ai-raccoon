#!/usr/bin/env python3
"""WP3 / plan §5.4 — deterministic generator for corpora/eval-set-100.json.

Reads the memory-db COPY (read-only) and the docs/adr listing, resolves every
anchor (expectedSource / expectedHash / targetProjectId / targetScope) from the
copy, and emits exactly 100 queries:

- 75 file-targeted queries: 25 ADR files (explicit allowlist below) x 3
  paraphrased queries, one per section family (context / decision /
  consequences; 0053 and 0067 use decision + 2 content queries because they
  lack one of the standard sections).
- 25 non-file queries: hermes transcripts (project_id='hermes-default',
  chunk_index=-1) and shared-tier entries without a source_file. Every one
  carries expectedHash (entry hash) and expectedSource=null.

The 4 RESERVED files belong to the 10-query test set and are never targeted
(plan §5.2 holdout discipline). The 25-file allowlist is explicit and fixed;
the seed constant documents the determinism contract (selection is by
allowlist, not by RNG, so regeneration is byte-identical).

Run:  python scripts/retrieval_tuning/build_eval_corpus.py
      (--copy /tmp/continue-testing-algorithm/datasets/memory-copy.db default;
       --output scripts/retrieval_tuning/corpora/eval-set-100.json default)
"""
from __future__ import annotations

import argparse
import json
import re
import sqlite3
from pathlib import Path

SEED = 42  # determinism contract: same inputs -> byte-identical JSON

DEFAULT_COPY = Path("/tmp/continue-testing-algorithm/datasets/memory-copy.db")
DEFAULT_OUTPUT = (
    Path(__file__).resolve().parents[2]
    / "scripts" / "retrieval_tuning" / "corpora" / "eval-set-100.json"
)

# ADR files RESERVED for the 10-query test set (plan §5.2) — never targeted here.
RESERVED_TEST_FILES = {
    "0006-rrf-parameter-optimization.md",
    "0056-a-retrieval-gate-measured-off-its-tuning-set.md",
    "0070-maintenance-is-a-list-of-jobs-with-a-ledger.md",
    "0078-the-no-fusion-regression-rule-is-an-order-and-ships-default-off.md",
}

# The explicit 25-file allowlist (plan §5.4): spread across the numbering range,
# favouring table-bearing and multi-chunk ADRs; disjoint from RESERVED_TEST_FILES.
ADR_ALLOWLIST = [
    "0004-dual-vector-structure-signal.md",
    "0008-live-pid-discovery-for-monitoring.md",
    "0011-schema-versioning.md",
    "0013-extension-host-hook-surface.md",
    "0014-settings-never-sync.md",
    "0017-tensorprimitives-in-core.md",
    "0020-always-on-http-stdio-proxy.md",
    "0022-authenticated-loopback-restart.md",
    "0025-the-sweep-reaper.md",
    "0035-memory-get-and-query-relevant-snippets.md",
    "0036-engine-aware-chunk-token-budget.md",
    "0039-noise-learning-substrate-and-shadow-mode.md",
    "0044-section-fts-weight.md",
    "0046-project-membership-has-one-definition.md",
    "0048-a-chunk-is-a-well-formed-markdown-fragment.md",
    "0053-rating-is-computed-where-it-is-stored.md",
    "0060-an-unrecognised-verb-must-not-launch-anything.md",
    "0064-memory-write-chunks-like-everything-else.md",
    "0067-naming-shared-asks-for-promotion.md",
    "0068-ctx-is-a-vec0-metadata-column-not-a-partition-key.md",
    "0071-a-query-is-trimmed-deliberately-and-said-so.md",
    "0072-a-term-budget-for-long-queries-is-not-adjudicable.md",
    "0075-only-the-server-writes-to-the-bank.md",
    "0080-the-phases-close-against-search-total-not-the-tool-total.md",
    "0083-search-parameters-unified-source.md",
]

# Per-file query specs: (family, category, difficulty, query text).
# The target chunk is resolved from the copy as the FIRST chunk (by chunk_index)
# whose section matches the family. Families: context | decision | consequences
# | the-measurement | what-was-rejected (the latter two for ADRs lacking one of
# the standard sections). Queries are paraphrased as a user would search; they
# never quote the ADR text (the 3 exact-query items live in the test set).
ADR_QUERY_SPECS: dict[str, list[tuple[str, str, str, str]]] = {
    "0004-dual-vector-structure-signal.md": [
        ("context", "ADR (Context)", "medium",
         "Why could plain content-only vector search not answer section-targeted questions like \"what does ADR-0011 decide\"?"),
        ("decision", "ADR (Decision)", "medium",
         "How does the dual-vector structure signal work — a second embedding of the heading path fused with the content embedding at a fixed alpha?"),
        ("consequences", "ADR (Consequences)", "medium",
         "What did shipping the structure vector cost in storage, and which section-targeted queries became answerable?"),
    ],
    "0008-live-pid-discovery-for-monitoring.md": [
        ("context", "ADR (Context)", "easy",
         "How do I find the server's process id to use with dotnet-counters and dotnet-trace?"),
        ("decision", "ADR (Decision)", "medium",
         "Does the running ai-raccoon server expose its own PID over an HTTP endpoint instead of persisting a pid file?"),
        ("consequences", "ADR (Consequences)", "hard",
         "Is exposing the server PID on loopback a security concern, and can polling the observability endpoint keep an idle server alive forever?"),
    ],
    "0011-schema-versioning.md": [
        ("context", "ADR (Context)", "medium",
         "How does the bank schema migrate today — does EnsureAsync use a version marker or per-feature existence probes?"),
        ("decision", "ADR (Decision)", "medium",
         "Should the bank adopt PRAGMA user_version as its schema version with an ordered migration ladder?"),
        ("consequences", "ADR (Consequences)", "hard",
         "What changes for existing banks now that the schema gap is recorded but the migration ladder is deferred?"),
    ],
    "0013-extension-host-hook-surface.md": [
        ("context", "ADR (Context)", "medium",
         "Which memory extension hooks were specified but never wired up in the extension host?"),
        ("decision", "ADR (Decision)", "easy",
         "Were the OnSweepAsync and OnConsolidateAsync hooks removed from the IMemoryExtension interface?"),
        ("consequences", "ADR (Consequences)", "hard",
         "How can an extension still observe sweep or consolidation deletions now that their dedicated hooks are gone?"),
    ],
    "0014-settings-never-sync.md": [
        ("context", "ADR (Context)", "easy",
         "Can cloud credentials stored in the settings table leak out through the bank snapshot sync?"),
        ("decision", "ADR (Decision)", "medium",
         "Are settings stripped from every pushed snapshot and completely ignored on pull?"),
        ("consequences", "ADR (Consequences)", "hard",
         "What is lost by never syncing settings — can a shared project-wide default like an embedding endpoint ride along with entries?"),
    ],
    "0017-tensorprimitives-in-core.md": [
        ("context", "ADR (Context)", "medium",
         "Why does AiRaccoon.Core need the System.Numerics.Tensors package — what does the mean-pool-and-normalize kernel do?"),
        ("decision", "ADR (Decision)", "easy",
         "Was the embedding math kernel vectorized with TensorPrimitives inside the domain layer?"),
        ("consequences", "ADR (Consequences)", "medium",
         "How much faster did the pooling kernel get at the decisive case, and what did Core gain in third-party packages?"),
    ],
    "0020-always-on-http-stdio-proxy.md": [
        ("context", "ADR (Context)", "medium",
         "Why did the stdio entry point become a proxy — what went wrong when many processes opened the same bank directly?"),
        ("decision", "ADR (Decision)", "medium",
         "Does bare ai-raccoon now proxy to a single always-on HTTP server, and does a loopback token guard /mcp?"),
        ("consequences", "ADR (Consequences)", "hard",
         "What happens when the proxy retries a request after a connection failure — can a tool call be executed twice?"),
    ],
    "0022-authenticated-loopback-restart.md": [
        ("context", "ADR (Context)", "easy",
         "How do I restart the running ai-raccoon server after a tool update when serve just attaches to the old binary?"),
        ("decision", "ADR (Decision)", "medium",
         "Does serve --restart ask the running server to stop over an authenticated loopback endpoint, then serve in its place?"),
        ("consequences", "ADR (Consequences)", "hard",
         "What can go wrong with serve --restart, and which exit codes distinguish the failure reasons?"),
    ],
    "0025-the-sweep-reaper.md": [
        ("context", "ADR (Context)", "hard",
         "Why was shipping the unattended sweep reaper on by default safe, given that it deletes entries?"),
        ("decision", "ADR (Decision)", "easy",
         "Are the sweep kill switch and threshold global settings, or can they be set per project?"),
        ("consequences", "ADR (Consequences)", "medium",
         "Does the sweep reaper honour a project's access mode — can it delete entries from a project in read-only mode?"),
    ],
    "0035-memory-get-and-query-relevant-snippets.md": [
        ("context", "ADR (Context)", "easy",
         "Is there an MCP tool that returns the full content of a memory entry by hash?"),
        ("decision", "ADR (Decision)", "hard",
         "How does the snippet for a vector-only hit get centered on query terms, and how are promoted shared copies deduped by content?"),
        ("consequences", "ADR (Consequences)", "medium",
         "What happens to scope=all results when a project row and its promoted shared copy of the same text both exist?"),
    ],
    "0036-engine-aware-chunk-token-budget.md": [
        ("context", "ADR (Context)", "medium",
         "Why did chunks counted with the o200k token budget get silently truncated by the local embedding model?"),
        ("decision", "ADR (Decision)", "hard",
         "How is the chunk budget now counted with the real BERT tokenizer, and what is the guaranteed split floor?"),
        ("consequences", "ADR (Consequences)", "very-hard",
         "Which event ids report embed-time truncation, and what known gap with newline-joined hash lists remains?"),
    ],
    "0039-noise-learning-substrate-and-shadow-mode.md": [
        ("context", "ADR (Context)", "medium",
         "How well does the embedding space separate tool output from deliberate memory writes?"),
        ("decision", "ADR (Decision)", "medium",
         "Does the noise-learning subsystem ship a detector now, or only the substrate and a shadow mode?"),
        ("consequences", "ADR (Consequences)", "hard",
         "What did the noise-store cleanup remove, and what remains as the training-data source?"),
    ],
    "0044-section-fts-weight.md": [
        ("context", "ADR (Context)", "hard",
         "Why was the section column's FTS bm25 weight 16, and was that weight ever actually exercised on a real bank?"),
        ("decision", "ADR (Decision)", "easy",
         "What weight does the section column carry in FTS ranking now?"),
        ("consequences", "ADR (Consequences)", "very-hard",
         "Which ranking gates moved when the section weight dropped, and what is wrong with the A1 relevance label?"),
    ],
    "0046-project-membership-has-one-definition.md": [
        ("context", "ADR (Context)", "medium",
         "Why did memory_set_ttl and memory_share answer unknown-hash for context-labelled entries?"),
        ("decision", "ADR (Decision)", "medium",
         "Where does the single definition of which rows belong to a project now live?"),
        ("consequences", "ADR (Consequences)", "easy",
         "Do context-labelled rows now appear in shares, TTLs, sweeps and project listings?"),
    ],
    "0048-a-chunk-is-a-well-formed-markdown-fragment.md": [
        ("context", "ADR (Context)", "hard",
         "Why does the heading parser see shell comments inside a code fence as level-1 headings?"),
        ("decision", "ADR (Decision)", "medium",
         "Is every chunk now guaranteed to open and close its fence state within itself?"),
        ("consequences", "ADR (Consequences)", "hard",
         "What happens to chunk hashes when an over-budget fence gets re-fenced with repeated delimiters?"),
    ],
    "0053-rating-is-computed-where-it-is-stored.md": [
        ("the-measurement", "ADR (Content)", "medium",
         "How can the stored rating and access count drift apart under concurrent search hits?"),
        ("decision", "ADR (Decision)", "easy",
         "Is the rating now computed in the same UPDATE statement that increments the access count?"),
        ("consequences", "ADR (Consequences)", "hard",
         "What did the rating fix delete from the codebase, and why must the SQLite build provide the pow function?"),
    ],
    "0060-an-unrecognised-verb-must-not-launch-anything.md": [
        ("context", "ADR (Context)", "medium",
         "What happened when a mistyped CLI verb fell through to the proxy and reached the production install?"),
        ("decision", "ADR (Decision)", "easy",
         "Which exit code does an unrecognised CLI verb now produce instead of launching the proxy?"),
        ("consequences", "ADR (Consequences)", "hard",
         "Why does a known verb with a wrong argument keep exit code 15 instead of the parse-failure code?"),
    ],
    "0064-memory-write-chunks-like-everything-else.md": [
        ("context", "ADR (Context)", "easy",
         "Why were long memory_write bodies stored as one row with most of the tokens never embedded?"),
        ("decision", "ADR (Decision)", "medium",
         "Does memory_write now route through the same chunker and budget resolution as file ingest?"),
        ("consequences", "ADR (Consequences)", "hard",
         "What hash does the caller get back after writing a long body that was split into multiple chunks?"),
    ],
    "0067-naming-shared-asks-for-promotion.md": [
        ("context", "ADR (Context)", "medium",
         "Why was writing with context shared able to put rows straight into the shared tier without review?"),
        ("decision", "ADR (Decision)", "medium",
         "What does memory_write(context: shared) do now — write to the project scope and queue a promotion request?"),
        ("what-was-rejected", "ADR (Content)", "easy",
         "Can a new shared-scope row still be created by a write?"),
    ],
    "0068-ctx-is-a-vec0-metadata-column-not-a-partition-key.md": [
        ("context", "ADR (Context)", "hard",
         "Why were the vec0 chunks wasting roughly 43 MB of fixed-capacity allocation?"),
        ("decision", "ADR (Decision)", "easy",
         "Is ctx still a partition key in the vec0 table, or just a filterable metadata column?"),
        ("consequences", "ADR (Consequences)", "medium",
         "What is the measured trade-off between the vec0 size win and the per-KNN latency cost?"),
    ],
    "0071-a-query-is-trimmed-deliberately-and-said-so.md": [
        ("context", "ADR (Context)", "hard",
         "Why did the embed-time truncation warning fire for a search query when no stored entry was over the window?"),
        ("decision", "ADR (Decision)", "medium",
         "Are long queries now trimmed before the embedding generator and reported as their own log event?"),
        ("consequences", "ADR (Consequences)", "easy",
         "Which event id means a stored entry was truncated and which one means a query was cut?"),
    ],
    "0072-a-term-budget-for-long-queries-is-not-adjudicable.md": [
        ("context", "ADR (Context)", "medium",
         "What is the project's stance on supporting very long pasted queries?"),
        ("decision", "ADR (Decision)", "easy",
         "Did a term budget or deduplication ship for the FTS query path?"),
        ("consequences", "ADR (Consequences)", "hard",
         "Why can the existing baseline gates not adjudicate a term-budget change?"),
    ],
    "0075-only-the-server-writes-to-the-bank.md": [
        ("context", "ADR (Context)", "hard",
         "Why was every bank open so expensive — what did the profiling trace find about schema ensure and settings reads?"),
        ("decision", "ADR (Decision)", "easy",
         "Is the MCP server now the only process allowed to write to the bank?"),
        ("consequences", "ADR (Consequences)", "medium",
         "How do CLI settings commands reach the server, and which command stays a direct bank writer?"),
    ],
    "0080-the-phases-close-against-search-total-not-the-tool-total.md": [
        ("context", "ADR (Context)", "hard",
         "Why can the six search phases never be made to sum to the memory_search tool total?"),
        ("decision", "ADR (Decision)", "medium",
         "Which total do the search phase timings close against now?"),
        ("consequences", "ADR (Consequences)", "very-hard",
         "What stays uninstrumented outside SearchAsync, and why does search.total not join PhaseNames?"),
    ],
    "0083-search-parameters-unified-source.md": [
        ("context", "ADR (Context)", "medium",
         "How were the retrieval knobs configured before SearchParameters — a mix of per-call args, settings rows and hardcoded constants?"),
        ("decision", "ADR (Decision)", "medium",
         "Is there now one resolved record per search with precedence query over settings over constants?"),
        ("consequences", "ADR (Consequences)", "easy",
         "What does every search now pay for the batched settings reads?"),
    ],
}

# Non-file targets: (project_id, scope, chunk_index or None, unique content
# marker, category, difficulty, query). The entry row is resolved from the copy
# by the marker (asserted unique within the bucket); hash comes from the row.
NON_FILE_SPECS: list[tuple[str, str, int | None, str, str, str, str]] = [
    # --- hermes transcripts (project hermes-default, chunk_index=-1) ---
    ("hermes-default", "project", -1,
     "ServerProbe`**: Refactored to execute probe attempts via Polly",
     "Non-file (hermes transcript)", "medium",
     "Did we refactor ServerProbe and AssetDownloader to use Polly retry pipelines with exponential backoff and jitter?"),
    ("hermes-default", "project", -1,
     "proc_d7cf45a51e69 was the group-6 serve run",
     "Non-file (hermes transcript)", "medium",
     "Is the group-6 serve run still current, or was it superseded by the final full-suite run?"),
    ("hermes-default", "project", -1,
     "func-jobsearch-prod still needs a restart to load the rotated OpenRouter key",
     "Non-file (hermes transcript)", "easy",
     "Which production function still needs a restart to load the rotated OpenRouter key?"),
    ("hermes-default", "project", -1,
     "installation instructions, metadata, and setup guides for `code-review-graph`",
     "Non-file (hermes transcript)", "easy",
     "Where are the installation instructions and setup guides for the code-review-graph MCP framework?"),
    ("hermes-default", "project", -1,
     "rollout-111-done.marker",
     "Non-file (hermes transcript)", "easy",
     "What version of ai-raccoon is installed after the 1.1.1 rollout, and is auto-promotion enabled?"),
    ("hermes-default", "project", -1,
     "untracked editor/tool backup files from entering git tracking",
     "Non-file (hermes transcript)", "easy",
     "Did we clean up tracked .bak backup files and add gitignore rules for them?"),
    ("hermes-default", "project", -1,
     "manualtest-tar2",
     "Non-file (hermes transcript)", "medium",
     "Which project id was used for the manual 25-tool MCP round-trip test?"),
    ("hermes-default", "project", -1,
     "SECURITY DEFECT** note (cloud sync",
     "Non-file (hermes transcript)", "medium",
     "What did the memory sweep of the ai-raccoon project store — which ADR chunks and defect notes?"),
    ("hermes-default", "project", -1,
     "RED: lint test suite (10 rules + wiring + real corpus)",
     "Non-file (hermes transcript)", "medium",
     "Which commits landed for the ai-badger skills-lint release 0.87.0?"),
    ("hermes-default", "project", -1,
     "preventing API and EasyAuth calls hitting SWA routes",
     "Non-file (hermes transcript)", "medium",
     "How did we stop SWA navigation fallback from rewriting API and EasyAuth requests to index.html?"),
    ("hermes-default", "project", -1,
     "PeachPDF generation",
     "Non-file (hermes transcript)", "easy",
     "What did PR 824 add to the PDF renderer — cancellation checks and async behaviour tests?"),
    ("hermes-default", "project", -1,
     "all phases PASS, no FAILs, ready to merge once CI is green",
     "Non-file (hermes transcript)", "easy",
     "What was the review verdict posted on ai-raccoon PR 254?"),
    ("hermes-default", "project", -1,
     "**Commit**: [`de15ca9f`](https://github.com/Arasz/ai-raccoon/commit/de15ca9f)",
     "Non-file (hermes transcript)", "easy",
     "Which commit re-scaffolded the ai-raccoon project files with ai-badger 0.116.6?"),
    ("hermes-default", "project", -1,
     "IMPORTANT / INTERRUPT: STOP IMMEDIATELY. Pause or cancel any running",
     "Non-file (hermes transcript)", "easy",
     "What does the important! interrupt prompt marker tell an agent to do?"),
    # --- shared-tier entries (scope=shared, no source_file) ---
    ("ai-raccoon", "shared", None,
     "one bank per install",
     "Non-file (shared tier)", "easy",
     "Where does the ai-raccoon memory bank live, and which installed global tool does the Hermes MCP bridge run?"),
    ("ai-raccoon", "shared", None,
     "streamable_http_client",
     "Non-file (shared tier)", "hard",
     "What are the traps in the Python MCP client around streamable_http_client headers and failed connects?"),
    ("ai-raccoon", "shared", None,
     "FRAMING CORRECTION THAT DRIVES EVERYTHING",
     "Non-file (shared tier)", "medium",
     "Why is a stdio ai-raccoon process not a client of ai-raccoon serve?"),
    ("ai-raccoon", "shared", None,
     "DefaultOptions.Transport from Stdio to Proxy",
     "Non-file (shared tier)", "hard",
     "How did the proxy transport decision reach already-installed MCP clients without editing their config?"),
    ("ai-raccoon", "shared", None,
     "JSON-RPC ERROR CODES ARE LOST THROUGH THE PROXY",
     "Non-file (shared tier)", "medium",
     "Are JSON-RPC error codes preserved through the ai-raccoon proxy?"),
    ("ai-raccoon", "shared", None,
     "TEST-GATE TRAP",
     "Non-file (shared tier)", "medium",
     "Why does redirecting stdout with StringWriter in a test miss output from a spawned child process?"),
    ("ai-raccoon", "shared", None,
     "malformed OTEL_EXPORTER_OTLP_ENDPOINT",
     "Non-file (shared tier)", "medium",
     "Can a malformed OTEL_EXPORTER_OTLP_ENDPOINT environment variable kill the server at boot?"),
    ("ai-raccoon", "shared", None,
     "dotnet-monitor: Prometheus yes, OTLP no",
     "Non-file (shared tier)", "easy",
     "Does dotnet-monitor support OTLP export or only Prometheus metrics?"),
    ("ai-raccoon", "shared", None,
     "asyncio.CancelledError",
     "Non-file (shared tier)", "hard",
     "Why does a failed MCP connect surface as an asyncio.CancelledError with an empty message?"),
    ("ai-badger", "shared", None,
     "imports jsonschema unguarded at module scope",
     "Non-file (shared tier)", "medium",
     "Why is importing badger_lib slow — which module-level import dominates its cost?"),
    ("ai-badger", "shared", None,
     "documented reason engine/framework_copies.py exists as a separate stdlib-only module",
     "Non-file (shared tier)", "medium",
     "Why does engine/framework_copies.py exist as a stdlib-only module separate from badger_lib?"),
]


def slugify(section: str) -> str:
    """'Decision 2 — the kill switch' -> 'decision-2-the-kill-switch'."""
    return re.sub(r"[^a-z0-9]+", "-", section.lower()).strip("-")


def _section_matches(section: str, family: str) -> bool:
    if family == "context":
        return section == "Context"
    if family == "decision":
        return section == "Decision" or section.startswith("Decision")
    if family == "consequences":
        return section == "Consequences"
    if family == "the-measurement":
        return section.startswith("The measurement")
    if family == "what-was-rejected":
        return section.startswith("What was rejected")
    raise ValueError(f"unknown family: {family}")


def _answer_span(value: str, limit: int = 120) -> str:
    collapsed = re.sub(r"\s+", " ", value).strip()
    return collapsed[:limit]


def _resolve_adr_target(conn: sqlite3.Connection, filename: str, family: str) -> sqlite3.Row:
    rows = conn.execute(
        "SELECT hash, source_file, section, project_id, scope, value, chunk_index "
        "FROM entries WHERE source_file LIKE ? ORDER BY chunk_index",
        (f"%/docs/adr/{filename}",),
    ).fetchall()
    for row in rows:
        if _section_matches(row["section"] or "", family):
            return row
    raise RuntimeError(f"no chunk of {filename} matches family {family!r}")


def _resolve_non_file_target(
    conn: sqlite3.Connection, project_id: str, scope: str, chunk_index: int | None, marker: str
) -> sqlite3.Row:
    q = "SELECT hash, source_file, section, project_id, scope, value, chunk_index FROM entries WHERE value LIKE ?"
    args: list = [f"%{marker}%"]
    if project_id:
        q += " AND project_id=?"
        args.append(project_id)
    if scope:
        q += " AND scope=?"
        args.append(scope)
    if chunk_index is not None:
        q += " AND chunk_index=?"
        args.append(chunk_index)
    rows = conn.execute(q, args).fetchall()
    if len(rows) != 1:
        raise RuntimeError(
            f"non-file marker {marker[:60]!r} resolved to {len(rows)} rows "
            f"(project={project_id}, scope={scope}, chunk_index={chunk_index})"
        )
    return rows[0]


def _check_hash_unique(conn: sqlite3.Connection, hash_value: str, label: str) -> None:
    n = conn.execute("SELECT count(*) FROM entries WHERE hash=?", (hash_value,)).fetchone()[0]
    if n != 1:
        raise RuntimeError(f"{label}: hash {hash_value[:16]}... is not unique in the copy ({n} rows)")


def generate(copy_path: Path, output_path: Path, docs_dir: Path | None = None) -> list[dict]:
    """Resolve all anchors from the copy and write the 100-query corpus JSON."""
    copy_path = Path(copy_path)
    output_path = Path(output_path)
    assert copy_path.exists(), f"memory-db copy not found: {copy_path}"

    # Plan §5.4: the generator reads the docs/adr listing to validate the allowlist.
    if docs_dir is None:
        docs_dir = Path(__file__).resolve().parents[2] / "docs" / "adr"
    docs_dir = Path(docs_dir)
    missing = [f for f in ADR_ALLOWLIST if not (docs_dir / f).exists()]
    assert not missing, f"allowlist files missing from docs/adr: {missing}"
    assert not (set(ADR_ALLOWLIST) & RESERVED_TEST_FILES), (
        "allowlist overlaps the reserved test-set files"
    )

    conn = sqlite3.connect(f"file:{copy_path}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    queries: list[dict] = []

    try:
        # 75 ADR file-targeted queries, in allowlist order (deterministic).
        for filename in ADR_ALLOWLIST:
            for family, category, difficulty, query_text in ADR_QUERY_SPECS[filename]:
                row = _resolve_adr_target(conn, filename, family)
                _check_hash_unique(conn, row["hash"], f"{filename}@{row['chunk_index']}")
                section = row["section"] or ""
                queries.append({
                    "id": f"E{len(queries) + 1:03d}",
                    "category": category,
                    "query": query_text,
                    "expectedSource": f"docs:adr:{filename}#{slugify(section)}",
                    "expectedHash": row["hash"],
                    "answerSpan": _answer_span(row["value"]),
                    "targetProjectId": row["project_id"],
                    "targetScope": row["scope"],
                    "searchLimit": 5,
                    "relevanceGrade": 5,
                    "negativeTest": False,
                    "difficulty": difficulty,
                    "nonFileTarget": False,
                })

        # 25 non-file queries (hermes transcripts + shared-tier entries).
        for project_id, scope, chunk_index, marker, category, difficulty, query_text in NON_FILE_SPECS:
            row = _resolve_non_file_target(conn, project_id, scope, chunk_index, marker)
            assert row["project_id"] == project_id and row["scope"] == scope, (
                f"marker {marker[:40]!r} resolved to unexpected bucket "
                f"{row['project_id']}/{row['scope']}"
            )
            _check_hash_unique(conn, row["hash"], f"non-file {marker[:40]!r}")
            queries.append({
                "id": f"E{len(queries) + 1:03d}",
                "category": category,
                "query": query_text,
                "expectedSource": None,
                "expectedHash": row["hash"],
                "answerSpan": _answer_span(row["value"]),
                "targetProjectId": row["project_id"],
                "targetScope": row["scope"],
                "searchLimit": 5,
                "relevanceGrade": 5,
                "negativeTest": False,
                "difficulty": difficulty,
                "nonFileTarget": True,
            })
    finally:
        conn.close()

    assert len(queries) == 100, f"expected 100 queries, built {len(queries)}"
    assert len({q["query"] for q in queries}) == 100, "duplicate query text"

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8") as fh:
        json.dump(queries, fh, indent=2, sort_keys=True, ensure_ascii=False)
        fh.write("\n")
    return queries


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--copy", type=Path, default=DEFAULT_COPY, help="memory-db copy (read-only)")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT, help="output JSON path")
    parser.add_argument("--docs-dir", type=Path, default=None, help="docs/adr directory")
    args = parser.parse_args()
    queries = generate(args.copy, args.output, args.docs_dir)
    print(f"wrote {len(queries)} queries to {args.output}")


if __name__ == "__main__":
    main()
