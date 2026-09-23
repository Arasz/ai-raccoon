#!/usr/bin/env python3
"""Code-corpus retrieval eval runner (plan §E3):

fresh scratch data root (or a copy of --reuse-bank with its watches rows
deleted, no re-ingest/drain) -> `serve --port 0 --idle-timeout 0` -> `model
code set default` -> `settings ingest scope add '*' <corpus-root>` -> MCP
`memory_ingest_directory` -> poll `code_entries.embed_state='pending'` until 0
-> run every query (kind=code, limit 10) -> write results-<arm>.json/.md.

Usage:
    python3 run_code_eval.py --binary <path> --corpus-root <dir> \\
        --queries <queries.json> --arm <name> [--settings k=v ...] \\
        [--out <dir>] [--reuse-bank <dir>] [--allow-busy]

Refuses to start (BusyProcessError) when `ps` shows a competing
embedding-heavy process (ai-raccoon serve, aspire, dotnet test) other than the
runner itself; --allow-busy overrides. Never points at the live bank/port —
retrieval_tuning.server's safety asserts (C1/C2) still apply to every scratch
server this module starts.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import sqlite3
import subprocess
import sys
import time
from pathlib import Path
from typing import Callable, Optional

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from retrieval_tuning import scoring, settings as settings_mod  # noqa: E402
from retrieval_tuning.server import start_server  # noqa: E402

DEFAULT_PROJECT_ID = "code-eval"
DEFAULT_IDLE_TIMEOUT = "0"
DRAIN_POLL_SECONDS = 2.0
DRAIN_TIMEOUT_SECONDS = 1800.0

def _word(term: str) -> "re.Pattern":
    """A whole-word match for `term` — 'serve' must not fire on 'observe'/'preserve'."""
    return re.compile(r"\b" + re.escape(term) + r"\b")


_RE_SERVE = _word("serve")
_RE_TEST = _word("test")
_RE_DOTNET = _word("dotnet")

BUSY_PATTERNS: tuple[tuple[str, Callable[[str], bool]], ...] = (
    ("ai-raccoon serve", lambda cmd: "ai-raccoon" in cmd and _RE_SERVE.search(cmd) is not None),
    ("aspire", lambda cmd: "aspire" in cmd),
    ("dotnet test", lambda cmd: _RE_DOTNET.search(cmd) is not None and _RE_TEST.search(cmd) is not None),
)


class BusyProcessError(RuntimeError):
    """A competing embedding-heavy process is running; refuse to start (override: --allow-busy)."""


class CliError(RuntimeError):
    """One `ai-raccoon` CLI subprocess exited non-zero."""


class DrainTimeoutError(RuntimeError):
    """code_entries.embed_state stayed 'pending' past the drain timeout."""


# ---------------------------------------------------------------------------
# Busy-process refusal (ps injected for testing; real ps at the CLI boundary).
# ---------------------------------------------------------------------------

def is_busy_command(cmdline: str) -> Optional[str]:
    lowered = cmdline.lower()
    for label, predicate in BUSY_PATTERNS:
        if predicate(lowered):
            return label
    return None


def find_busy_processes(ps_output: str, exclude_pids: frozenset = frozenset()) -> list[dict]:
    """Parse `ps -Ao pid,command` text; return busy rows, excluding given pids."""
    lines = ps_output.strip("\n").splitlines()
    if len(lines) <= 1:
        return []
    found = []
    for line in lines[1:]:
        line = line.strip()
        if not line:
            continue
        parts = line.split(None, 1)
        if len(parts) < 2:
            continue
        pid_str, command = parts
        try:
            pid = int(pid_str)
        except ValueError:
            continue
        if pid in exclude_pids:
            continue
        label = is_busy_command(command)
        if label:
            found.append({"pid": pid, "command": command, "label": label})
    return found


def _run_ps() -> str:
    proc = subprocess.run(["ps", "-Ao", "pid,command"], capture_output=True, text=True, timeout=10)
    return proc.stdout


def check_not_busy(*, allow_busy: bool = False, own_pid: Optional[int] = None, ps_fn=None) -> list[dict]:
    """Raise BusyProcessError unless allow_busy, or nothing competing is running."""
    if allow_busy:
        return []
    import os  # noqa: PLC0415 — only needed for the default own_pid

    own_pid = own_pid if own_pid is not None else os.getpid()
    ps_fn = ps_fn or _run_ps
    busy = find_busy_processes(ps_fn(), exclude_pids=frozenset({own_pid}))
    if busy:
        described = ", ".join(f"pid {p['pid']} ({p['label']}): {p['command']}" for p in busy)
        raise BusyProcessError(
            f"refusing to start: competing embedding-heavy process(es) running: {described} "
            "— pass --allow-busy to override"
        )
    return busy


# ---------------------------------------------------------------------------
# Data-root prep: fresh empty root, or a --reuse-bank copy with watches wiped.
# ---------------------------------------------------------------------------

def delete_watches(db_path) -> int:
    """Delete every row from watches (+ watch_files) in a bank copy.

    A copy inherits live watch registrations; left alone the scratch server
    re-scans and re-ingests real project directories mid-eval. watches is the
    table checked (and asserted on); watch_files (per-path fingerprints) is
    cleared too when present, for the same reason, tolerated if absent."""
    conn = sqlite3.connect(str(db_path))
    try:
        removed = conn.execute("DELETE FROM watches").rowcount
        try:
            conn.execute("DELETE FROM watch_files")
        except sqlite3.OperationalError:
            pass
        conn.commit()
        return int(removed)
    finally:
        conn.close()


def prepare_reused_bank(reuse_bank, data_root) -> Path:
    """Copy a whole bank directory (already ingested + drained) into data_root,
    with its watches rows deleted. No re-ingest/re-drain follows this."""
    reuse_bank, data_root = Path(reuse_bank), Path(data_root)
    if data_root.exists():
        shutil.rmtree(data_root)
    shutil.copytree(reuse_bank, data_root)
    delete_watches(data_root / "memory.db")
    return data_root


def prepare_fresh_data_root(data_root) -> Path:
    """An empty scratch data root; `ai-raccoon serve` initializes the bank on first run."""
    data_root = Path(data_root)
    if data_root.exists():
        shutil.rmtree(data_root)
    data_root.mkdir(parents=True, exist_ok=True)
    return data_root


# ---------------------------------------------------------------------------
# CLI-driven bank setup: engine + ingest scope (verified against --help, plan §E3).
# ---------------------------------------------------------------------------

def _run_cli(argv: list[str], timeout: float = 600.0) -> str:
    try:
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired as exc:
        raise CliError(f"CLI command timed out: {' '.join(argv)}") from exc
    if proc.returncode != 0:
        raise CliError(
            f"CLI command failed (exit {proc.returncode}): {' '.join(argv)}\n{proc.stderr.strip()[:800]}"
        )
    return proc.stdout


def set_code_engine_default(binary: str, data_root, port: int) -> None:
    """`model code set default` — downloads (first run) and activates the code engine."""
    _run_cli([binary, "--data-root", str(data_root), "--port", str(port), "model", "code", "set", "default"])


def add_ingest_scope(binary: str, data_root, port: int, corpus_root, target: str = "*") -> None:
    """`settings ingest scope add <target> <path>` — the allowlist memory_ingest_directory needs."""
    _run_cli(
        [binary, "--data-root", str(data_root), "--port", str(port),
         "settings", "ingest", "scope", "add", target, str(corpus_root)]
    )


# ---------------------------------------------------------------------------
# Drain: poll code_entries.embed_state until nothing is pending.
# ---------------------------------------------------------------------------

def pending_code_count(db_path) -> int:
    conn = sqlite3.connect(f"file:{Path(db_path).resolve()}?mode=ro", uri=True)
    try:
        return int(conn.execute("SELECT count(*) FROM code_entries WHERE embed_state = 'pending'").fetchone()[0])
    finally:
        conn.close()


def drain_code_embeddings(
    db_path,
    *,
    poll_interval: float = DRAIN_POLL_SECONDS,
    timeout: float = DRAIN_TIMEOUT_SECONDS,
    clock=time.monotonic,
    sleep=time.sleep,
    count_fn=pending_code_count,
) -> float:
    """Poll until code_entries has zero pending rows; returns elapsed seconds."""
    start = clock()
    deadline = start + timeout
    while True:
        pending = count_fn(db_path)
        if pending == 0:
            return clock() - start
        if clock() >= deadline:
            raise DrainTimeoutError(f"drain timed out after {timeout}s with {pending} rows still pending")
        sleep(poll_interval)


# ---------------------------------------------------------------------------
# Manifest join: chunk counts per language/sizeBand.
# ---------------------------------------------------------------------------

def load_manifest(path) -> list[dict]:
    data = json.loads(Path(path).read_text())
    if isinstance(data, list):
        return data
    return list(data.get("files") or data.get("manifest") or [])


def _normalize_path(path: str) -> str:
    return path.replace("\\", "/")


def match_manifest_row(entry_path: str, manifest_rows: list[dict]) -> Optional[dict]:
    """The manifest row whose vendoredPath/path is the LONGEST suffix of entry_path."""
    normalized = _normalize_path(entry_path)
    best, best_len = None, -1
    for row in manifest_rows:
        candidate = _normalize_path(row.get("vendoredPath") or row.get("path") or "")
        if candidate and normalized.endswith(candidate) and len(candidate) > best_len:
            best, best_len = row, len(candidate)
    return best


def chunk_counts_by_language_band(db_path, manifest_rows: list[dict]) -> dict:
    """{'language:sizeBand': count} over code_entries joined to the manifest."""
    conn = sqlite3.connect(f"file:{Path(db_path).resolve()}?mode=ro", uri=True)
    try:
        paths = [row[0] for row in conn.execute("SELECT path FROM code_entries")]
    finally:
        conn.close()
    counts: dict[str, int] = {}
    for path in paths:
        row = match_manifest_row(path, manifest_rows)
        if row is None:
            continue
        key = f"{row.get('language', 'unknown')}:{row.get('sizeBand', 'unknown')}"
        counts[key] = counts.get(key, 0) + 1
    return counts


# ---------------------------------------------------------------------------
# Misc small helpers.
# ---------------------------------------------------------------------------

def sha256_file(path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _coerce_value(raw: str):
    if raw.lower() in ("true", "false"):
        return raw.lower() == "true"
    try:
        return int(raw)
    except ValueError:
        pass
    try:
        return float(raw)
    except ValueError:
        pass
    return raw


def parse_settings_kv(pairs: list[str]) -> dict:
    parsed: dict = {}
    for pair in pairs:
        if "=" not in pair:
            raise ValueError(f"--settings expects k=v, got {pair!r}")
        key, raw_value = pair.split("=", 1)
        parsed[key] = _coerce_value(raw_value)
    return parsed


def load_queries(path) -> list[dict]:
    data = json.loads(Path(path).read_text())
    if isinstance(data, dict):
        return list(data.get("queries") or [])
    return list(data)


def render_markdown(arm: str, report: dict) -> str:
    metrics = report["metrics"]
    lines = [
        f"# Code eval — arm `{arm}`",
        "",
        f"- binary sha256: `{report['binarySha256']}`",
        f"- corpus root: `{report['corpusRoot']}`",
        f"- reused bank: `{report['reusedBank']}`",
        f"- drain seconds: {report['drainSeconds']}",
        f"- settings: {report['settings']}",
        "",
        "## Overall",
        "",
        "| metric | value |",
        "| --- | --- |",
        f"| nDCG@5 | {metrics['mean_ndcg5']:.4f} |",
        f"| nDCG@10 | {metrics['mean_ndcg10']:.4f} |",
        f"| MRR@10 | {metrics['mean_mrr10']:.4f} |",
        f"| hit@1 | {metrics['hit1_rate']:.4f} |",
        f"| hit@5 | {metrics['hit5_rate']:.4f} |",
        f"| span-hit@5 | {metrics['spanHit5Rate']:.4f} |",
        "",
    ]
    for label, groups in (
        ("split", metrics["per_split"]),
        ("language", metrics["per_language"]),
        ("size band", metrics["per_size_band"]),
        ("category", metrics["per_category"]),
    ):
        if not groups:
            continue
        lines += [f"## By {label}", "", "| name | n | nDCG@5 | hit@1 | hit@5 |", "| --- | --- | --- | --- | --- |"]
        for name, group in sorted(groups.items()):
            lines.append(
                f"| {name} | {group['count']} | {group['mean_ndcg5']:.4f} | "
                f"{group['hit1_rate']:.4f} | {group['hit5_rate']:.4f} |"
            )
        lines.append("")
    if report.get("chunkCounts"):
        lines += ["## Chunk counts (language:sizeBand)", ""]
        for key, count in sorted(report["chunkCounts"].items()):
            lines.append(f"- {key}: {count}")
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------------------
# The end-to-end run.
# ---------------------------------------------------------------------------

def run(args) -> dict:
    check_not_busy(allow_busy=args.allow_busy)

    binary_sha = sha256_file(args.binary)
    settings_dict = parse_settings_kv(args.settings)
    queries = load_queries(args.queries)

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    data_root = out_dir / f"data-root-{args.arm}"

    reused = args.reuse_bank is not None
    if reused:
        prepare_reused_bank(args.reuse_bank, data_root)
    else:
        prepare_fresh_data_root(data_root)

    drain_seconds = 0.0
    with start_server(data_root, binary=args.binary, idle_timeout=DEFAULT_IDLE_TIMEOUT) as server:
        if settings_dict:
            settings_mod.apply_settings(server.data_root, settings_dict, port=server.port, binary=args.binary)

        if not reused:
            set_code_engine_default(args.binary, server.data_root, server.port)
            add_ingest_scope(args.binary, server.data_root, server.port, args.corpus_root)
            server.client.ingest_directory(DEFAULT_PROJECT_ID, str(args.corpus_root))
            drain_seconds = drain_code_embeddings(data_root / "memory.db")

        query_scores = []
        for entry in queries:
            results = server.client.memory_search(
                project_id=entry.get("targetProjectId") or DEFAULT_PROJECT_ID,
                query=entry["query"],
                scope=entry.get("targetScope") or "all",
                limit=10,
                min_relative_score=0.0,
                kind="code",
            )
            query_scores.append(scoring.score_query(results, entry))
        metrics = scoring.summarize(query_scores, config=dict(settings_dict))

        chunk_counts: dict = {}
        if args.manifest is not None:
            chunk_counts = chunk_counts_by_language_band(data_root / "memory.db", load_manifest(args.manifest))

    report = {
        "arm": args.arm,
        "binary": str(args.binary),
        "binarySha256": binary_sha,
        "corpusRoot": str(args.corpus_root),
        "queriesPath": str(args.queries),
        "settings": settings_dict,
        "reusedBank": str(args.reuse_bank) if reused else None,
        "drainSeconds": drain_seconds,
        "chunkCounts": chunk_counts,
        "metrics": metrics.as_dict(),
    }
    return report


def parse_args(argv: Optional[list[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--binary", required=True, help="ai-raccoon binary/apphost path — never the live install")
    parser.add_argument("--corpus-root", required=True, type=Path, dest="corpus_root")
    parser.add_argument("--queries", required=True, type=Path)
    parser.add_argument("--arm", required=True)
    parser.add_argument("--settings", action="append", default=[], metavar="k=v")
    parser.add_argument("--out", type=Path, default=Path("."))
    parser.add_argument("--reuse-bank", type=Path, default=None, dest="reuse_bank")
    parser.add_argument("--manifest", type=Path, default=None,
                        help="MANIFEST.json for the chunk-counts-by-language/band report")
    parser.add_argument("--allow-busy", action="store_true")
    return parser.parse_args(argv)


def main(argv: Optional[list[str]] = None) -> int:
    args = parse_args(argv)
    try:
        report = run(args)
    except (BusyProcessError, CliError, DrainTimeoutError) as exc:
        print(f"FAIL: {exc}")
        return 1

    out_dir = Path(args.out)
    json_path = out_dir / f"results-{args.arm}.json"
    md_path = out_dir / f"results-{args.arm}.md"
    json_path.write_text(json.dumps(report, indent=2) + "\n")
    md_path.write_text(render_markdown(args.arm, report))

    metrics = report["metrics"]
    print(
        f"arm={args.arm} n={len(metrics['per_query'])} drain={report['drainSeconds']:.1f}s "
        f"nDCG@5={metrics['mean_ndcg5']:.4f} nDCG@10={metrics['mean_ndcg10']:.4f} "
        f"hit@1={metrics['hit1_rate']:.4f} hit@5={metrics['hit5_rate']:.4f} "
        f"spanHit@5={metrics['spanHit5Rate']:.4f}"
    )
    print(f"results: {json_path}")
    print(f"report:  {md_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
