#!/usr/bin/env python3
"""P3 runner — sequential two-arm threshold eval over the MCP stdio surface (plan §P3).

Committed, clean-room version of the proven PoC harness pattern: one AiRaccoon server
per arm (`dotnet <dll> --data-root <root> --transport stdio`), newline-delimited
JSON-RPC, each arm's stderr redirected to `arm-<name>.stderr`, arms strictly
sequential (off first, then threshold — never concurrent). Every `memory_search`
call passes the query's own `projectId` (per-project scoping is load-bearing:
cross-project queries silently serve 0/8 overlap), and holdout / fold-divergent
entries from P2's corpus are refused at load.

Protocol trap (verified in P1): writing every request and only then closing stdin
loses responses — the stdio transport starts shutdown on stdin EOF and races the
pending writes. The client therefore writes ONE request, reads its response, then
writes the next; stdin stays open until every response is in hand.

Marker discipline (review C4): up to TWO `[mmr-poc]` lines per search are expected —
`SearchResultMerge` and `AdjustMergedResults` both call `Merge`, and the threshold
filter is idempotent. Hard failures only: the threshold arm's stderr with ZERO
`[mmr-poc]` lines (the diversifier did not engage) or the off arm's stderr with ANY
`[mmr-poc]` line (the gate leaks). Never assert an exact count.

Metrics — per query per arm (arm-<name>.json): hash, ranking, path, sourceFile,
chunkIndex, snippet (<=600 chars), queryText (review S6: P4/P5 read it from the
metrics payload, not from the corpus); both the memory and the code sections are
recorded as served. Per pair (metrics.json), computed on the top-8 MEMORY list —
the fused, post-hook list in served order, exactly the eval precedent's
"ours-off final top-8" (the code section is a separate surface with its own score
scale, never merged into the top-8): top-8 SET overlap over 8 (review C2: set
overlap, not RBO — reorder-only queries are controls), RBO p=0.9 truncated as
`s += p**d * |A_d ∩ B_d| / d` for d = 1..min(len), result ×(1−p) — identical
8-lists pin to 0.5126, the eval report's ceiling; drop list (served by off, not
by threshold) and backfill list (the reverse), each entry with its sourceFile;
`expectedHashInTop8` auto-grade per arm (H2 flag).

P2's corpus is consumed by contract (JSON schema), never by importing P2's module.

Usage:
    python scripts/retrieval_tuning/run_threshold_eval.py \
        --dll <path-to-AiRaccoon.dll> \
        --corpus scripts/retrieval_tuning/corpora/project-corpus-100.json \
        --copy <memory.db copy> \
        [--data-root <ready root>] [--output-dir docs/work/threshold-committee-eval]
"""
from __future__ import annotations

import argparse
import json
import os
import select
import subprocess
import tempfile
import time
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Self

ARMS: dict[str, dict[str, str]] = {
    "off": {"MMR_DISABLE": "1"},
    "threshold": {"MMR_MODE": "threshold", "MMR_TAU": "0.95"},
}
MARKER = "[mmr-poc]"
RBO_P = 0.9
SNIPPET_MAX = 600
SEARCH_LIMIT = 8
SESSION_ID = "threshold-eval"
RESPONSE_TIMEOUT_S = 180.0  # per response; the first search loads the embedding model
PROTOCOL_VERSION = "2024-11-05"
DEFAULT_OUTPUT_DIR = (Path(__file__).resolve().parents[2]
                      / "docs" / "work" / "threshold-committee-eval")

# Review M2: the search gate folds these raw aliases to other canonical ids
# (aib -> ai-badger, job-search-ai-assistant -> jsaa), so a query scoped to the raw
# spelling could never be served. P2 excludes them; this runner refuses them too.
FOLD_DIVERGENT_PROJECT_IDS = frozenset({"aib", "job-search-ai-assistant"})


class CorpusContractError(ValueError):
    """P2 corpus JSON violates the runner's contract (shape, holdout, fold divergence)."""


class MarkerDisciplineError(RuntimeError):
    """An arm's stderr violates marker discipline (threshold silent, or off leaky)."""


@dataclass(frozen=True)
class EvalQuery:
    """One eval query from P2's corpus (id, query text, own projectId, anchor|null)."""

    id: str
    query: str
    project_id: str
    expected_hash: str | None


# ------------------------------------------------------------------ corpus contract


def load_corpus(path: Path) -> list[EvalQuery]:
    """Load P2's corpus JSON by contract: a top-level list of query entries, or an
    object carrying them under `queries` (a header is tolerated and ignored here).

    Each entry must carry `id`, query text (`query` or `queryText`), `projectId`, and
    optional `expectedHash` (null for content-targeted queries). Refusals: holdout
    entries (P2 reserves them — they are never queried), fold-divergent projectIds
    (review M2), duplicate ids, missing text or projectId.
    """
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    entries = raw if isinstance(raw, list) else (raw.get("queries") or raw.get("entries"))
    if not isinstance(entries, list) or not entries:
        raise CorpusContractError(
            f"corpus {path}: expected a non-empty list of query entries")
    queries: list[EvalQuery] = []
    seen: set[str] = set()
    for index, entry in enumerate(entries):
        query = _parse_entry(entry, index)
        if query.id in seen:
            raise CorpusContractError(f"corpus {path}: duplicate query id {query.id!r}")
        seen.add(query.id)
        queries.append(query)
    return queries


def _parse_entry(entry: Any, index: int) -> EvalQuery:
    if not isinstance(entry, dict):
        raise CorpusContractError(f"corpus entry #{index}: not an object")
    qid = entry.get("id")
    text = entry.get("query") or entry.get("queryText")
    project_id = entry.get("projectId")
    if not isinstance(qid, str) or not qid:
        raise CorpusContractError(f"corpus entry #{index}: missing `id`")
    if not isinstance(text, str) or not text.strip():
        raise CorpusContractError(f"{qid}: missing query text (`query`)")
    if not isinstance(project_id, str) or not project_id:
        raise CorpusContractError(
            f"{qid}: missing `projectId` (every query carries its own — the scoping trap)")
    if entry.get("holdout"):
        raise CorpusContractError(
            f"{qid}: holdout entries are never queried (P2 reserves them; plan §P2-2b)")
    if project_id in FOLD_DIVERGENT_PROJECT_IDS:
        raise CorpusContractError(
            f"{qid}: fold-divergent projectId {project_id!r} refused (review M2 — the "
            f"gate folds it, so the raw-spelled anchor could never be served)")
    expected = entry.get("expectedHash")
    if expected is not None and not isinstance(expected, str):
        raise CorpusContractError(f"{qid}: `expectedHash` must be a hash string or null")
    return EvalQuery(id=qid, query=text, project_id=project_id, expected_hash=expected)


# ------------------------------------------------------------------ arm config


def arm_env(arm: str, base: Mapping[str, str] | None = None) -> dict[str, str]:
    """Exact env for an arm over a clean base: every stray `MMR_*` variable from the
    invoking shell is stripped first, so a leftover MMR_MODE can never flip an arm."""
    if arm not in ARMS:
        raise ValueError(f"unknown arm {arm!r} — arms are exactly {sorted(ARMS)}")
    env = {key: value for key, value in (os.environ if base is None else base).items()
           if not key.startswith("MMR_")}
    env.update(ARMS[arm])
    return env


def build_search_calls(queries: Sequence[EvalQuery], limit: int = SEARCH_LIMIT,
                       session_id: str = SESSION_ID) -> list[dict[str, Any]]:
    """The single place `memory_search` calls are shaped: every call carries the
    query's own projectId (per-project scoping is load-bearing)."""
    return [{"name": "memory_search",
             "arguments": {"projectId": query.project_id, "query": query.query,
                           "limit": limit, "sessionId": session_id}}
            for query in queries]


# ------------------------------------------------------------------ stdio MCP client


class StdioMcpClient:
    """One MCP stdio server process driven interactively over newline JSON-RPC.

    The interactive discipline is load-bearing (protocol trap): each request is
    written with stdin kept OPEN and its response read before the next request is
    written. Writing the whole batch and only then closing stdin loses responses —
    the server starts shutdown on stdin EOF and races the pending writes. Reads are
    select-bounded per response, so a wedged server raises instead of hanging.
    """

    def __init__(self, dll: Path, env: Mapping[str, str], stderr_path: Path,
                 data_root: Path | None = None,
                 response_timeout_s: float = RESPONSE_TIMEOUT_S) -> None:
        command = ["dotnet", str(dll)]
        if data_root is not None:
            command += ["--data-root", str(data_root)]
        command += ["--transport", "stdio"]
        stderr_path.parent.mkdir(parents=True, exist_ok=True)
        self._stderr_file = stderr_path.open("wb")
        self._proc = subprocess.Popen(command, stdin=subprocess.PIPE,
                                      stdout=subprocess.PIPE, stderr=self._stderr_file,
                                      env=dict(env))
        self._buffer = bytearray()
        self._next_id = 0
        self._timeout_s = response_timeout_s

    def __enter__(self) -> Self:
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.close()

    def _send(self, message: dict[str, Any]) -> None:
        assert self._proc.stdin is not None
        self._proc.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
        self._proc.stdin.flush()

    def _wait_for_response(self, want_id: int) -> dict[str, Any]:
        """Collect stdout until the response carrying `want_id` arrives; notifications
        and non-matching ids are skipped; EOF or a lapsed deadline raises."""
        assert self._proc.stdout is not None
        fd = self._proc.stdout.fileno()
        deadline = time.monotonic() + self._timeout_s
        while True:
            newline = self._buffer.find(b"\n")
            while newline != -1:
                line = bytes(self._buffer[:newline])
                del self._buffer[:newline + 1]
                try:
                    message = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if isinstance(message, dict) and message.get("id") == want_id:
                    return message
                newline = self._buffer.find(b"\n")
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(
                    f"no response to id {want_id} within {self._timeout_s:.0f}s")
            ready, _, _ = select.select([fd], [], [], min(remaining, 5.0))
            if not ready:
                continue
            chunk = os.read(fd, 65536)
            if not chunk:
                raise EOFError(f"server closed stdout before responding to id {want_id}")
            self._buffer += chunk

    def rpc(self, method: str, params: dict[str, Any] | None = None,
            notify: bool = False) -> dict[str, Any] | None:
        """One JSON-RPC exchange: write the request, then (unless a notification)
        block on its response before returning — the send→read→next discipline."""
        self._next_id += 1
        message: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        if not notify:
            message["id"] = self._next_id
        self._send(message)
        return None if notify else self._wait_for_response(self._next_id)

    def initialize(self) -> None:
        """MCP handshake: initialize request, then the initialized notification."""
        self.rpc("initialize", {
            "protocolVersion": PROTOCOL_VERSION, "capabilities": {},
            "clientInfo": {"name": "threshold-eval-runner", "version": "0"}})
        self.rpc("notifications/initialized", {}, notify=True)

    def close(self) -> None:
        """All responses are in hand by contract — only now may stdin close (protocol
        trap), letting the server exit; a wedged server is killed after a grace wait."""
        try:
            if self._proc.stdin is not None and not self._proc.stdin.closed:
                self._proc.stdin.close()
            try:
                self._proc.wait(timeout=60)
            except subprocess.TimeoutExpired:
                self._proc.kill()
                self._proc.wait(timeout=30)
        finally:
            self._stderr_file.close()


# ------------------------------------------------------------------ result extraction


def extract_results(response: Mapping[str, Any]) -> dict[str, list[dict[str, Any]]]:
    """JSON-RPC response -> {memory: [chunk...], code: [chunk...]} on the deterministic
    fields (timings stripped by construction); snippets capped at SNIPPET_MAX."""
    error = response.get("error")
    if error:
        raise RuntimeError(f"memory_search JSON-RPC error: {json.dumps(error)[:300]}")
    try:
        text = response["result"]["content"][0]["text"]
    except (KeyError, IndexError, TypeError) as exc:
        raise RuntimeError(
            f"unexpected memory_search response shape: {str(dict(response))[:200]}") from exc
    try:
        data = json.loads(text)["data"]
    except (json.JSONDecodeError, KeyError) as exc:
        raise RuntimeError(f"unexpected memory_search payload: {text[:200]}") from exc
    out: dict[str, list[dict[str, Any]]] = {"memory": [], "code": []}
    for section, key in (("results", "memory"), ("code", "code")):
        for hit in data.get(section) or []:
            out[key].append(_chunk_fields(hit))
    return out


def _chunk_fields(hit: Mapping[str, Any]) -> dict[str, Any]:
    return {"hash": hit["hash"], "ranking": hit["ranking"], "path": hit.get("path"),
            "sourceFile": hit.get("sourceFile"), "chunkIndex": hit.get("chunkIndex"),
            "snippet": (hit.get("snippet") or "")[:SNIPPET_MAX]}


# ------------------------------------------------------------------ metrics


def top8_hits(query_result: Mapping[str, Any]) -> list[dict[str, Any]]:
    """The query's top-8 list: the memory section in served order, capped at
    SEARCH_LIMIT. The server's memory section IS the fused, post-hook list in final
    rank order (`ranking` is a float score, and the code section carries its own
    independent score scale — the two are never merged into one top-8)."""
    return list(query_result.get("memory") or [])[:SEARCH_LIMIT]


def rbo(left: Sequence[str], right: Sequence[str], p: float = RBO_P) -> float:
    """Finite truncated RBO at persistence p: `s += p**d * |A_d ∩ B_d| / d` for
    d = 1..min(len), result ×(1−p). Identical 8-lists pin to 0.5126 — the eval
    report's ceiling; deliberately the p**d form, not the textbook p**(d−1) form."""
    seen_left: set[str] = set()
    seen_right: set[str] = set()
    score = 0.0
    for depth in range(1, min(len(left), len(right)) + 1):
        seen_left.add(left[depth - 1])
        seen_right.add(right[depth - 1])
        score += p ** depth * len(seen_left & seen_right) / depth
    return (1 - p) * score


def _hash_set(hits: Sequence[Mapping[str, Any]]) -> set[str]:
    return {hit["hash"] for hit in hits}


def set_overlap(off_hits: Sequence[Mapping[str, Any]],
                threshold_hits: Sequence[Mapping[str, Any]]) -> tuple[int, float]:
    """Top-8 SET overlap: (shared hashes, fraction over the SEARCH_LIMIT denominator).
    Set overlap, not RBO, so reorder-only queries read as controls (review C2)."""
    shared = len(_hash_set(off_hits) & _hash_set(threshold_hits))
    return shared, shared / SEARCH_LIMIT


def drop_backfill(off_hits: Sequence[Mapping[str, Any]],
                  threshold_hits: Sequence[Mapping[str, Any]],
                  ) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """drop = served by off, not by threshold; backfill = the reverse. Each entry
    carries the sourceFile from the arm where the hash was served."""
    threshold_hashes = _hash_set(threshold_hits)
    off_hashes = _hash_set(off_hits)
    drop = [{"hash": hit["hash"], "sourceFile": hit.get("sourceFile")}
            for hit in off_hits if hit["hash"] not in threshold_hashes]
    backfill = [{"hash": hit["hash"], "sourceFile": hit.get("sourceFile")}
                for hit in threshold_hits if hit["hash"] not in off_hashes]
    return drop, backfill


def expected_hash_in_top8(expected_hash: str | None,
                          hits: Sequence[Mapping[str, Any]]) -> bool | None:
    """H2 auto-grade: did the corpus anchor land in this arm's top-8? null when the
    query carries no anchor (content-targeted)."""
    if expected_hash is None:
        return None
    return any(hit["hash"] == expected_hash for hit in hits)


def compute_metrics(off_arm: Mapping[str, Any], threshold_arm: Mapping[str, Any],
                    queries: Sequence[EvalQuery]) -> dict[str, Any]:
    """Pair metrics for the two arms' results — the metrics.json payload.

    Reads queryText from the off arm's results (review S6) and expectedHash /
    projectId from the corpus queries. Raises when the arms disagree on the set of
    query ids (a query missing from one arm is never silently dropped).
    """
    if off_arm.get("arm") != "off" or threshold_arm.get("arm") != "threshold":
        raise ValueError("compute_metrics expects the {'arm': 'off'|'threshold'} artifacts")
    off_results: Mapping[str, Mapping[str, Any]] = off_arm["results"]
    threshold_results: Mapping[str, Mapping[str, Any]] = threshold_arm["results"]
    if set(off_results) != set(threshold_results):
        raise ValueError("arms disagree on query ids — a query is missing from one arm")
    by_id = {query.id: query for query in queries}
    pairs: dict[str, dict[str, Any]] = {}
    for qid, off_result in off_results.items():
        query = by_id[qid]
        off_hits = top8_hits(off_result)
        threshold_hits = top8_hits(threshold_results[qid])
        shared, fraction = set_overlap(off_hits, threshold_hits)
        drop, backfill = drop_backfill(off_hits, threshold_hits)
        pairs[qid] = {
            "queryText": off_result.get("queryText"),
            "projectId": query.project_id,
            "expectedHash": query.expected_hash,
            "top8SetOverlapCount": shared,
            "top8SetOverlap": fraction,
            "rbo": rbo([hit["hash"] for hit in off_hits],
                       [hit["hash"] for hit in threshold_hits]),
            "drop": drop,
            "backfill": backfill,
            "expectedHashInTop8": {
                "off": expected_hash_in_top8(query.expected_hash, off_hits),
                "threshold": expected_hash_in_top8(query.expected_hash, threshold_hits)},
        }
    return {"meta": {"arms": list(ARMS), "rboP": RBO_P, "searchLimit": SEARCH_LIMIT,
                     "pairCount": len(pairs)},
            "pairs": pairs}


# ------------------------------------------------------------------ marker discipline


def validate_markers(arm: str, stderr_text: str) -> None:
    """Marker discipline — hard failures only, never an exact count (review C4: up to
    TWO lines per search is expected). The threshold arm must show >=1 `[mmr-poc]`
    line (the diversifier engaged); the off arm must show none (the gate holds)."""
    lines = [line for line in stderr_text.splitlines() if MARKER in line]
    if arm == "threshold":
        if not lines:
            raise MarkerDisciplineError(
                "threshold arm stderr has zero [mmr-poc] lines — the diversifier "
                "did not engage")
    elif arm == "off":
        if lines:
            raise MarkerDisciplineError(
                f"off arm stderr has {len(lines)} [mmr-poc] lines — the gate leaks: "
                f"{lines[:3]}")
    else:
        raise ValueError(f"unknown arm {arm!r} — arms are exactly {sorted(ARMS)}")


# ------------------------------------------------------------------ run pipeline


def prepare_data_root(copy: Path, work_dir: Path) -> Path:
    """Server data-root for a run: `memory.db` symlinked to the bank copy, plus
    symlinks to the install's models/extensions so the embedding model resolves.
    Pass an existing root via --data-root to skip this setup."""
    copy = Path(copy)
    if not copy.exists():
        raise FileNotFoundError(f"bank copy not found: {copy}")
    root = Path(work_dir) / "data-root"
    root.mkdir(parents=True, exist_ok=True)
    (root / "memory.db").symlink_to(copy.resolve())
    for name in ("models", "extensions"):
        source = Path.home() / ".ai-raccoon" / name
        if source.exists() and not (root / name).exists():
            (root / name).symlink_to(source)
    return root


def run_arm(arm: str, dll: Path, data_root: Path, queries: Sequence[EvalQuery],
            output_dir: Path, limit: int = SEARCH_LIMIT) -> dict[str, Any]:
    """Run one arm: one server process, every query through it interactively, stderr
    captured to arm-<name>.stderr. Marker discipline is validated BEFORE the arm JSON
    is written — a failed arm leaves its stderr for debugging but no half-artifact set."""
    output_dir.mkdir(parents=True, exist_ok=True)
    stderr_path = output_dir / f"arm-{arm}.stderr"
    calls = build_search_calls(queries, limit=limit, session_id=f"{SESSION_ID}-{arm}")
    results: dict[str, dict[str, Any]] = {}
    with StdioMcpClient(dll, arm_env(arm), stderr_path, data_root) as client:
        client.initialize()
        for query, call in zip(queries, calls):
            response = client.rpc("tools/call", call)
            results[query.id] = {**extract_results(response), "queryText": query.query}
    validate_markers(arm, stderr_path.read_text(encoding="utf-8", errors="replace"))
    arm_json: dict[str, Any] = {"arm": arm, "env": dict(ARMS[arm]), "results": results}
    write_json(output_dir / f"arm-{arm}.json", arm_json)
    return arm_json


def run_eval(dll: Path, corpus_path: Path, output_dir: Path, copy: Path | None = None,
             data_root: Path | None = None, limit: int = SEARCH_LIMIT) -> dict[str, Any]:
    """Both arms sequentially (one server at a time — never concurrent), then the pair
    metrics. Writes arm-off.json, arm-threshold.json, metrics.json and the two arm
    stderr files under output_dir; returns the metrics payload."""
    queries = load_corpus(corpus_path)
    output_dir.mkdir(parents=True, exist_ok=True)
    if data_root is None:
        if copy is None:
            raise ValueError("a bank copy (--copy) or a ready root (--data-root) is required")
        data_root = prepare_data_root(copy, Path(tempfile.mkdtemp(prefix="threshold-eval-root-")))
    arms: dict[str, dict[str, Any]] = {}
    for arm in ARMS:  # insertion order: off, then threshold — strictly sequential
        arms[arm] = run_arm(arm, dll, data_root, queries, output_dir, limit)
    metrics = compute_metrics(arms["off"], arms["threshold"], queries)
    write_json(output_dir / "metrics.json", metrics)
    return metrics


def write_json(path: Path, payload: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=1) + "\n", encoding="utf-8")


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="P3: sequential two-arm threshold eval runner (plan §P3)")
    parser.add_argument("--dll", type=Path, required=True, help="built AiRaccoon.dll")
    parser.add_argument("--corpus", type=Path, required=True, help="P2 corpus JSON")
    parser.add_argument("--copy", type=Path, default=None,
                        help="bank copy (memory.db); ignored with --data-root")
    parser.add_argument("--data-root", type=Path, default=None,
                        help="ready server data-root (skips the copy symlink setup)")
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT_DIR,
                        help="artifact directory (default: docs/work/threshold-committee-eval)")
    parser.add_argument("--limit", type=int, default=SEARCH_LIMIT,
                        help="memory_search limit (default: 8)")
    args = parser.parse_args(argv)
    run_eval(dll=args.dll, corpus_path=args.corpus, output_dir=args.output_dir,
             copy=args.copy, data_root=args.data_root, limit=args.limit)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
