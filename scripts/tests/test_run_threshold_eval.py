"""P3 runner tests — sequential two-arm threshold eval (plan §P3, AC3.1–AC3.4).

All unit tests are fixture-based (no live bank, no server process, no network). The
live smoke (AC3.4) is env-gated on `AI_RACCOON_EVAL_COPY` + `AI_RACCOON_EVAL_DLL`
(optional `AI_RACCOON_EVAL_DATA_ROOT`) and skips without them.

P2's corpus is consumed by CONTRACT: the tiny committed fixture below mirrors the
documented schema (entries carry `id`, query text, `projectId`, `expectedHash|null`,
`holdout`; header carries `snapshotSha256`/`seed`). P2's generator module is never
imported — it does not exist in this worktree.

Marker discipline background (review C4): up to TWO `[mmr-poc]` lines per search are
expected — `SearchResultMerge` and `AdjustMergedResults` both call `Merge`, and the
threshold filter is idempotent. The gates assert presence/absence, never an exact
count; a test that "fixed" the duplicate would be wrong.

RBO constant: the pinned truncated form is `s += p**d * |A_d ∩ B_d| / d` for
d = 1..min(len), result ×(1−p) — identical 8-lists give 0.5126 (the eval report's
ceiling). This is deliberately NOT the textbook p**(d-1) form (which would give
0.5695); the tests pin the constant so a formula regression cannot pass silently.
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
RUNNER_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "run_threshold_eval.py"

COPY_ENV = "AI_RACCOON_EVAL_COPY"
DLL_ENV = "AI_RACCOON_EVAL_DLL"
DATA_ROOT_ENV = "AI_RACCOON_EVAL_DATA_ROOT"


# ------------------------------------------------------------------ module loader


@pytest.fixture(scope="module")
def runner():
    spec = importlib.util.spec_from_file_location("run_threshold_eval", RUNNER_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    # dataclasses resolves annotations via sys.modules[module] on 3.14 — register first
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


# ------------------------------------------------------------------ P2-contract fixtures


E_A = "a" * 64  # C001's corpus anchor (expectedHash)
E_B = "b" * 64  # C003's corpus anchor (expectedHash)


def _h(i: int) -> str:
    """Distinct 64-hex-char fake entry hash."""
    return f"{i:02d}" * 32


CORPUS_FIXTURE = {
    "header": {"snapshotSha256": "0" * 64, "seed": 42, "evalQueries": 3},
    "queries": [
        {"id": "C001", "query": "gmail monitoring spec requirements",
         "projectId": "jsaa", "expectedHash": E_A, "holdout": False},
        {"id": "C002", "query": "message bus contract ack discipline",
         "projectId": "ai-raccoon", "expectedHash": None, "holdout": False},
        {"id": "C003", "query": "workspace outbox isolation semantics",
         "projectId": "hermes-default", "expectedHash": E_B, "holdout": False},
    ],
}

QT = {entry["id"]: entry["query"] for entry in CORPUS_FIXTURE["queries"]}


def _write_corpus(tmp_path: Path, corpus: dict) -> Path:
    path = tmp_path / "corpus-fixture.json"
    path.write_text(json.dumps(corpus), encoding="utf-8")
    return path


@pytest.fixture()
def corpus_path(tmp_path: Path) -> Path:
    return _write_corpus(tmp_path, CORPUS_FIXTURE)


# ------------------------------------------------------------------ arm-result fixtures


def _chunk(h: str, ranking: int, source_file: str | None = None) -> dict:
    return {"hash": h, "ranking": ranking, "path": f"src/{h[:6]}.cs",
            "sourceFile": source_file or f"sf-{h[:6]}.cs", "chunkIndex": 3,
            "snippet": "text " * 120}  # exactly 600 chars — within the cap


def _top8(hashes: list[str], source_files: dict[str, str] | None = None) -> dict:
    return {"memory": [_chunk(h, rank, (source_files or {}).get(h))
                       for rank, h in enumerate(hashes, 1)],
            "code": []}


def _arm(name: str, per_query: dict[str, dict]) -> dict:
    return {"arm": name, "env": {}, "results": {qid: {**res, "queryText": QT[qid]}
                                                for qid, res in per_query.items()}}


OFF_ARM = _arm("off", {
    "C001": _top8([E_A, _h(1), _h(2), _h(3), _h(4), _h(5), _h(6), _h(7)],
                  {E_A: "off-expected-adr.cs"}),
    "C002": _top8([_h(i) for i in range(1, 9)]),
    "C003": _top8([E_B, _h(2), _h(3), _h(4), _h(5), _h(6), _h(7), _h(8)]),
})

THRESHOLD_ARM = _arm("threshold", {
    # C001: drops the anchor E_A, backfills _h(8)
    "C001": _top8([_h(1), _h(2), _h(3), _h(4), _h(5), _h(6), _h(7), _h(8)],
                  {_h(8): "threshold-backfill-adr.cs"}),
    # C002: identical to off (control by the overlap definition)
    "C002": _top8([_h(i) for i in range(1, 9)]),
    # C003: reorder-only (top swap) — same 8 hashes, control by overlap, RBO < ceiling
    "C003": _top8([_h(2), E_B, _h(3), _h(4), _h(5), _h(6), _h(7), _h(8)]),
})


# ------------------------------------------------------------------ AC3.1: arm env


def test_arm_env_mapping_exact(runner) -> None:
    """Owner decision: exactly two arms — off (MMR_DISABLE=1) and threshold
    (MMR_MODE=threshold MMR_TAU=0.95). MMR was discarded; no third arm exists."""
    assert runner.ARMS == {"off": {"MMR_DISABLE": "1"},
                           "threshold": {"MMR_MODE": "threshold", "MMR_TAU": "0.95"}}
    base = {"PATH": "/usr/bin", "HOME": "/x"}
    assert runner.arm_env("off", base) == {"PATH": "/usr/bin", "HOME": "/x",
                                           "MMR_DISABLE": "1"}
    assert runner.arm_env("threshold", base) == {"PATH": "/usr/bin", "HOME": "/x",
                                                 "MMR_MODE": "threshold", "MMR_TAU": "0.95"}


def test_arm_env_strips_stray_shell_mmr_vars(runner, monkeypatch) -> None:
    """A stray MMR_* var in the invoking shell must never flip an arm (P1's proven
    `_session_env` discipline): the base env is stripped before overrides apply."""
    monkeypatch.setenv("MMR_MODE", "mmr")
    monkeypatch.setenv("MMR_TAU", "0.3")
    env = runner.arm_env("off")
    assert env["MMR_DISABLE"] == "1"
    assert "MMR_MODE" not in env and "MMR_TAU" not in env
    env = runner.arm_env("threshold")
    assert env["MMR_MODE"] == "threshold" and env["MMR_TAU"] == "0.95"


def test_arm_env_unknown_arm_raises(runner) -> None:
    with pytest.raises(ValueError, match="unknown arm"):
        runner.arm_env("mmr")


# ------------------------------------------------------------------ AC3.1: scoping


def test_scoping_calls_carry_query_project_id(runner, corpus_path) -> None:
    """Every built memory_search call carries the query's OWN projectId (research
    record §3: cross-project scoping silently serves 0/8 overlap)."""
    queries = runner.load_corpus(corpus_path)
    calls = runner.build_search_calls(queries)
    assert [call["arguments"]["projectId"] for call in calls] == \
        ["jsaa", "ai-raccoon", "hermes-default"]
    assert [call["arguments"]["query"] for call in calls] == [q.query for q in queries]
    assert all(call["arguments"]["limit"] == runner.SEARCH_LIMIT for call in calls)
    assert all(call["name"] == "memory_search" for call in calls)
    assert all(call["arguments"]["sessionId"] for call in calls)


def test_scoping_holdout_entry_refused(runner, tmp_path) -> None:
    """Holdout ids are never queried: an entry flagged holdout=true is refused at load."""
    corpus = json.loads(json.dumps(CORPUS_FIXTURE))
    corpus["queries"][1]["holdout"] = True
    with pytest.raises(runner.CorpusContractError, match="holdout"):
        runner.load_corpus(_write_corpus(tmp_path, corpus))


def test_scoping_fold_divergent_project_id_refused(runner, tmp_path) -> None:
    """Review M2: the search gate folds aib→ai-badger and
    job-search-ai-assistant→jsaa, so a raw-spelled anchor could never be served —
    a corpus entry carrying a fold-divergent projectId is refused."""
    for alias in ("aib", "job-search-ai-assistant"):
        corpus = json.loads(json.dumps(CORPUS_FIXTURE))
        corpus["queries"][0]["projectId"] = alias
        with pytest.raises(runner.CorpusContractError, match="fold-divergent"):
            runner.load_corpus(_write_corpus(tmp_path, corpus))


def test_scoping_fixture_has_no_fold_divergent_ids(runner, corpus_path) -> None:
    queries = runner.load_corpus(corpus_path)
    assert all(q.project_id not in runner.FOLD_DIVERGENT_PROJECT_IDS for q in queries)


def test_scoping_duplicate_ids_refused(runner, tmp_path) -> None:
    corpus = json.loads(json.dumps(CORPUS_FIXTURE))
    corpus["queries"].append(dict(corpus["queries"][0]))
    with pytest.raises(runner.CorpusContractError, match="duplicate"):
        runner.load_corpus(_write_corpus(tmp_path, corpus))


# ------------------------------------------------------------------ AC3.2: metrics


def test_metrics_known_overlap_drop_backfill(runner, corpus_path) -> None:
    metrics = runner.compute_metrics(OFF_ARM, THRESHOLD_ARM,
                                     runner.load_corpus(corpus_path))
    assert metrics["meta"]["arms"] == ["off", "threshold"]
    assert metrics["meta"]["pairCount"] == 3
    # C001: off serves the anchor + h1..h7; threshold drops the anchor, backfills h8.
    pair = metrics["pairs"]["C001"]
    assert pair["top8SetOverlapCount"] == 7
    assert pair["top8SetOverlap"] == pytest.approx(7 / 8)
    assert pair["drop"] == [{"hash": E_A, "sourceFile": "off-expected-adr.cs"}]
    assert pair["backfill"] == [{"hash": _h(8), "sourceFile": "threshold-backfill-adr.cs"}]
    # C002: identical arms → control by the overlap definition (review C2).
    pair = metrics["pairs"]["C002"]
    assert pair["top8SetOverlap"] == pytest.approx(1.0)
    assert pair["drop"] == [] and pair["backfill"] == []
    # C003: reorder-only (top swap) → STILL a control by set overlap, never by RBO.
    pair = metrics["pairs"]["C003"]
    assert pair["top8SetOverlap"] == pytest.approx(1.0)
    assert pair["drop"] == [] and pair["backfill"] == []
    # Review S6: P4/P5 read queryText from metrics.json, not from the corpus.
    for qid, text in QT.items():
        assert metrics["pairs"][qid]["queryText"] == text


def test_metrics_rbo_identical_lists_ceiling(runner, corpus_path) -> None:
    """Identical 8-lists pin to 0.5126 (tol 1e-3) — the eval report's ceiling. This
    discriminates the pinned p**d form from the textbook p**(d-1) form (0.5695)."""
    hashes = [_h(i) for i in range(1, 9)]
    assert runner.rbo(hashes, list(hashes)) == pytest.approx(0.5126, abs=1e-3)
    metrics = runner.compute_metrics(OFF_ARM, THRESHOLD_ARM,
                                     runner.load_corpus(corpus_path))
    assert metrics["pairs"]["C002"]["rbo"] == pytest.approx(0.5126, abs=1e-3)


def test_metrics_rbo_known_top_swap(runner, corpus_path) -> None:
    """A swap of the top two positions keeps every prefix set from depth 2 on, so the
    pinned formula gives Σ_{d=2..8} p^d ×(1−p) = 0.4226 (the p**(d-1) form would read
    0.3795 — this test pins the documented form)."""
    up, down = [_h(i) for i in range(1, 9)], [_h(2), _h(1)] + [_h(i) for i in range(3, 9)]
    assert runner.rbo(up, down) == pytest.approx(0.4226, abs=1e-3)
    metrics = runner.compute_metrics(OFF_ARM, THRESHOLD_ARM,
                                     runner.load_corpus(corpus_path))
    assert metrics["pairs"]["C003"]["rbo"] == pytest.approx(0.4226, abs=1e-3)


def test_metrics_expected_hash_auto_grade(runner, corpus_path) -> None:
    """H2 flag per arm: True/False when the anchor moved, None when the query carries
    no anchor (content-targeted), True/True when both arms serve it."""
    metrics = runner.compute_metrics(OFF_ARM, THRESHOLD_ARM,
                                     runner.load_corpus(corpus_path))
    assert metrics["pairs"]["C001"]["expectedHashInTop8"] == {"off": True, "threshold": False}
    assert metrics["pairs"]["C002"]["expectedHashInTop8"] == {"off": None, "threshold": None}
    assert metrics["pairs"]["C003"]["expectedHashInTop8"] == {"off": True, "threshold": True}


def test_metrics_snippet_truncated_and_top8_contract(runner) -> None:
    """Extraction + top-8 contract: snippets capped at 600; the top-8 list is the
    memory section in SERVED order (the server's `ranking` is a float score — never
    re-sorted); code-section hits are recorded in the payload but excluded from the
    top-8 (independent score scale, live-verified: each section restarts at 1.0)."""
    def hit(h: str, rank: float, snippet_len: int) -> dict:
        return {"hash": h, "ranking": rank, "path": f"p/{h[:6]}",
                "sourceFile": f"s/{h[:6]}.cs", "chunkIndex": 3,
                "snippet": "x" * snippet_len}
    data = {"results": [hit(_h(1), 1.0, 700), hit(_h(2), 0.98, 10),
                        hit(_h(3), 0.95, 700), hit(_h(4), 0.9, 5)],
            "code": [hit(_h(9), 1.0, 700), hit(_h(10), 0.99, 700)]}
    response = {"result": {"content": [{"type": "text",
                                        "text": json.dumps({"data": data})}]}}
    extracted = runner.extract_results(response)
    assert [c["hash"] for c in extracted["memory"]] == [_h(1), _h(2), _h(3), _h(4)]
    assert [c["hash"] for c in extracted["code"]] == [_h(9), _h(10)]
    # served order preserved (ranks 1.0, 0.98, 0.95, 0.9 — a re-sort would be a no-op
    # here, so serve them deliberately out of score order to pin no-re-sorting):
    data["results"] = [hit(_h(3), 0.95, 700), hit(_h(1), 1.0, 700),
                       hit(_h(4), 0.9, 700), hit(_h(2), 0.98, 700)]
    response = {"result": {"content": [{"type": "text",
                                        "text": json.dumps({"data": data})}]}}
    served_unsorted = runner.extract_results(response)
    top8 = runner.top8_hits({**served_unsorted, "queryText": "q"})
    assert [c["hash"] for c in top8] == [_h(3), _h(1), _h(4), _h(2)]  # served order
    assert all(len(c["snippet"]) <= 600 for c in top8)
    assert len(top8[0]["snippet"]) == 600  # 700 → truncated to the cap
    assert all(c["path"] and c["sourceFile"] and c["chunkIndex"] is not None
               for c in top8)


# ------------------------------------------------------------------ AC3.3: marker discipline


THRESHOLD_STDERR = (
    "info: AiRaccoon starting\n"
    "[mmr-poc] mode=threshold tau=0.95 pool=230 kept=8\n"
    "[mmr-poc] mode=threshold tau=0.95 pool=230 kept=8\n"  # second Merge call site — expected
    "info: search complete\n"
)


def test_markers_threshold_with_markers_ok(runner) -> None:
    runner.validate_markers("threshold", THRESHOLD_STDERR)  # must not raise


def test_markers_threshold_zero_raises(runner) -> None:
    with pytest.raises(runner.MarkerDisciplineError, match="did not engage"):
        runner.validate_markers("threshold", "info: started\ninfo: done\n")


def test_markers_off_leak_raises(runner) -> None:
    with pytest.raises(runner.MarkerDisciplineError, match="leaks"):
        runner.validate_markers("off", THRESHOLD_STDERR)


def test_markers_off_clean_ok(runner) -> None:
    runner.validate_markers("off", "info: started\ninfo: done\n")  # must not raise


def test_markers_two_per_search_tolerated(runner) -> None:
    """Review C4: up to TWO marker lines per search (two Merge call sites) is the
    expected shape — three searches, six lines, no raise. Guards against someone
    'fixing' the duplicate or adding an upper-bound assertion."""
    stderr = "[mmr-poc] mode=threshold tau=0.95 pool=230 kept=8\n" * 6
    runner.validate_markers("threshold", stderr)  # must not raise


# ------------------------------------------------------------------ AC3.4: live smoke


SMOKE_CORPUS = {
    "header": {"snapshotSha256": "0" * 64, "seed": 42, "evalQueries": 2},
    "queries": [
        {"id": "S001", "query": "message bus reply ack discipline",
         "projectId": "ai-raccoon", "expectedHash": None, "holdout": False},
        {"id": "S002", "query": "gmail monitoring spec requirements",
         "projectId": "jsaa", "expectedHash": None, "holdout": False},
    ],
}


def test_smoke_live_two_queries_two_arms(runner, tmp_path: Path) -> None:
    """AC3.4: 2 queries × 2 arms through the real server produce arm-off.json,
    arm-threshold.json + metrics.json under the output dir; off stderr carries zero
    [mmr-poc] lines, threshold stderr at least one. Skips without the copy + dll."""
    copy, dll = os.environ.get(COPY_ENV), os.environ.get(DLL_ENV)
    if not copy or not dll:
        pytest.skip(f"live smoke needs {COPY_ENV} (bank copy) + {DLL_ENV} (built "
                    "AiRaccoon.dll) in the environment")
    copy_path, dll_path = Path(copy), Path(dll)
    if not copy_path.exists() or not dll_path.exists():
        pytest.skip(f"live smoke env points at missing files: {copy_path} / {dll_path}")
    data_root_env = os.environ.get(DATA_ROOT_ENV)
    if data_root_env:
        data_root = Path(data_root_env)
        if not (data_root / "memory.db").exists():
            pytest.skip(f"{DATA_ROOT_ENV} has no memory.db: {data_root}")
    else:
        data_root = runner.prepare_data_root(copy_path, tmp_path)

    corpus = _write_corpus(tmp_path, SMOKE_CORPUS)
    out_dir = tmp_path / "out"
    metrics = runner.run_eval(dll=dll_path, corpus_path=corpus, output_dir=out_dir,
                              data_root=data_root)

    for name in ("arm-off.json", "arm-threshold.json", "metrics.json",
                 "arm-off.stderr", "arm-threshold.stderr"):
        assert (out_dir / name).exists(), f"missing artifact: {name}"
    assert "[mmr-poc]" not in (out_dir / "arm-off.stderr").read_text(encoding="utf-8",
                                                                     errors="replace")
    assert "[mmr-poc]" in (out_dir / "arm-threshold.stderr").read_text(encoding="utf-8",
                                                                      errors="replace")
    assert set(metrics["pairs"]) == {"S001", "S002"}
    assert metrics["meta"]["arms"] == ["off", "threshold"]
    for name in ("arm-off.json", "arm-threshold.json"):
        arm = json.loads((out_dir / name).read_text(encoding="utf-8"))
        assert set(arm["results"]) == {"S001", "S002"}
        assert all("queryText" in result for result in arm["results"].values())
    pair = metrics["pairs"]["S001"]
    assert set(pair["expectedHashInTop8"]) == {"off", "threshold"}
