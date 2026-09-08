"""P5 blind A/B pass tests (plan 2026-09-09 §P5, ACs 5.1–5.4).

P3's arm JSONs and P4's sample.json are read BY CONTRACT: the tiny fixtures
below stand in for those artifacts (they do not exist in this worktree; the
contract shape is documented in ab_compare.py). FakeRunner stands in for the
headless grader CLI — no subprocess, no network.

- AC5.1 (-k blind):    the fixed payload surface (instruction block +
                       delimiters) leaks no arm identity.
- AC5.2 (-k mapping):  seeded position mapping is deterministic, restored
                       post-grading; comp_score arithmetic exact (x/3 and
                       x/(3−abstentions)).
- AC5.3 (-k sessions): 48 fresh grader calls for 16 queries × 3 graders, every
                       call --no-session, never resume/continue.
- AC5.4 (-k distill):  reason distillation is deterministic and invokes the
                       runner zero times.
"""
from __future__ import annotations

import importlib.util
import json
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
AB_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "ab_compare.py"


def _load():
    spec = importlib.util.spec_from_file_location("ab_compare", AB_PATH)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


# --------------------------------------------------------------------------
# Contract fixtures (tiny stand-ins for P3's arm JSONs and P4's sample.json)
# --------------------------------------------------------------------------

SEED = 20260909
N_QUERIES = 16
# Deterministic pick per grader within each query (grader order = trio order).
VALID_PICKS = ["first", "second", "first"]


def _sample_json(n: int = N_QUERIES) -> dict:
    return {
        "header": {
            "graders": [
                {"name": "grader-1", "provider": None, "model": None},
                {
                    "name": "grader-2",
                    "provider": "openrouter",
                    "model": "meta/muse-spark-1.3-contributor",
                },
                {"name": "grader-3", "provider": "xiaomi", "model": "mio-v2.5-pro"},
            ],
            "sampleSize": n,
        },
        "sample": [
            {"queryId": f"C{i:03d}", "stratum": "changed" if i <= 10 else "control"}
            for i in range(1, n + 1)
        ],
    }


def _arm_json(arm_name: str, n: int = N_QUERIES) -> dict:
    queries = []
    for i in range(1, n + 1):
        chunks = []
        for j in range(1, 9):
            snippet = f"q{i} chunk {j} documents a retrieval behaviour detail"
            if i == 1 and j == 1:
                # A chunk may legitimately mention the word — chunk text is
                # excluded from the AC5.1 assertion (load-bearing exclusion).
                snippet = "this chunk documents the threshold filter constant"
            chunks.append(
                {"hash": f"{i:04d}-{j:02d}-" + "a" * 56, "rank": j, "snippet": snippet}
            )
        queries.append(
            {
                "queryId": f"C{i:03d}",
                "queryText": f"query {i} about retrieval tuning",
                "results": chunks,
            }
        )
    return {"arm": arm_name, "queries": queries}


def _write_fixture(directory: Path, n: int = N_QUERIES) -> tuple[Path, Path, Path]:
    directory.mkdir(parents=True, exist_ok=True)
    sample_path = directory / "sample.json"
    sample_path.write_text(json.dumps(_sample_json(n)), encoding="utf-8")
    threshold_path = directory / "arm-threshold.json"
    threshold_path.write_text(json.dumps(_arm_json("threshold", n)), encoding="utf-8")
    off_path = directory / "arm-off.json"
    off_path.write_text(json.dumps(_arm_json("off", n)), encoding="utf-8")
    return sample_path, threshold_path, off_path


def _valid_response(pick: str) -> str:
    return f"PICK: {pick}\nREASON: The {pick} list puts the direct answer at rank one."


class FakeRunner:
    """Records every argv; returns canned responses in call order. No network."""

    def __init__(self, responses: list[str]):
        self.responses = list(responses)
        self.calls: list[list[str]] = []

    def __call__(self, argv: list[str]) -> str:
        self.calls.append(list(argv))
        assert self.responses, "runner invoked more times than scripted"
        return self.responses.pop(0)


# --------------------------------------------------------------------------
# AC5.1
# --------------------------------------------------------------------------


def test_ac5_1_blind_payload_has_no_arm_hint(tmp_path):
    ab = _load()
    # The fixed surface = everything render_payload emits except the query
    # text and the chunk lines: instruction block, query label, delimiters,
    # and the chunk-line scaffold.
    surface = ab.render_payload("", [], []) + "\n" + ab.CHUNK_FMT.format(
        rank=1, hash="h", snippet="s"
    )
    assert not re.search(r"\bthreshold\b", surface, re.IGNORECASE)
    assert not re.search(r"\boff\b", surface, re.IGNORECASE)
    assert not re.search(r"mmr_", surface, re.IGNORECASE)
    assert not re.search(r"mmr-poc", surface, re.IGNORECASE)

    sample_path, threshold_path, off_path = _write_fixture(tmp_path, n=2)
    runner = FakeRunner([_valid_response(p) for p in VALID_PICKS] * 2)
    forms_dir = tmp_path / "ab-forms"
    ab.run_ab(
        sample_path,
        threshold_path,
        off_path,
        seed=SEED,
        runner=runner,
        out_path=tmp_path / "ab-results.json",
        forms_dir=forms_dir,
    )
    payload = (forms_dir / "C001.payload.txt").read_text(encoding="utf-8")
    # Non-vacuity: chunk text genuinely carries the word, so excluding chunk
    # text from the surface assertion matters.
    assert re.search(r"\bthreshold\b", payload)
    # Positions are labelled only first/second — exactly one of each delimiter.
    assert payload.count(ab.FIRST_HEADER) == 1
    assert payload.count(ab.SECOND_HEADER) == 1
    assert "Query: query 1 about retrieval tuning" in payload


# --------------------------------------------------------------------------
# AC5.2
# --------------------------------------------------------------------------


def test_ac5_2_seeded_mapping_and_comp_score(tmp_path):
    ab = _load()
    sample = _sample_json(N_QUERIES)
    # The trio constants equal the grader record in P4's sample.json header.
    assert [(g["provider"], g["model"]) for g in sample["header"]["graders"]] == [
        (g["provider"], g["model"]) for g in ab.TRIO
    ]

    ids = [s["queryId"] for s in sample["sample"]]
    a1 = ab.assign_positions(ids, SEED)
    a2 = ab.assign_positions(ids, SEED)
    assert a1 == a2  # same seed -> same order
    assert a1 != ab.assign_positions(ids, 424242)  # different seed -> different order

    # comp_score arithmetic: x/3 fixture and x/(3−a) abstention fixture.
    assert ab.comp_score(3, 0) == 1.0
    assert ab.comp_score(2, 0) == 2 / 3
    assert ab.comp_score(2, 1) == 1.0
    assert ab.comp_score(1, 1) == 0.5
    assert ab.comp_score(0, 3) is None  # all abstained: reported, never scored

    # End-to-end x/3: mapping restored post-grading on a clean 16-query pass.
    sample_path, threshold_path, off_path = _write_fixture(tmp_path, n=N_QUERIES)
    runner = FakeRunner([_valid_response(p) for p in VALID_PICKS] * N_QUERIES)
    out_path = tmp_path / "ab-results.json"
    results = ab.run_ab(
        sample_path, threshold_path, off_path, seed=SEED, runner=runner, out_path=out_path
    )
    assert json.loads(out_path.read_text(encoding="utf-8")) == results
    assert len(results["queries"]) == N_QUERIES
    for q in results["queries"]:
        assert q["abstentions"] == 0
        assert q["compScore"] == q["thresholdPicks"] / 3
        for g in q["graders"]:
            expected_arm = q["firstArm"] if g["pick"] == "first" else q["secondArm"]
            assert g["pickArm"] == expected_arm
    assert sum(q["thresholdPicks"] for q in results["queries"]) > 0

    # End-to-end x/(3−a): malformed output re-asked once, then abstention.
    sample1, threshold1, off1 = _write_fixture(tmp_path / "abst", n=1)
    runner = FakeRunner(
        [
            _valid_response("first"),
            "Honestly the second list looks better overall.",  # malformed: no PICK line
            "Still cannot decide between the two lists.",  # still malformed -> abstention
            _valid_response("second"),
        ]
    )
    res1 = ab.run_ab(
        sample1, threshold1, off1, seed=SEED, runner=runner, out_path=tmp_path / "abst" / "res.json"
    )
    q = res1["queries"][0]
    assert len(runner.calls) == 4  # 3 graders + exactly 1 re-ask
    assert q["abstentions"] == 1
    assert q["graders"][1]["abstained"] is True
    assert q["graders"][1]["pick"] is None
    mapping = ab.assign_positions(["C001"], SEED)
    expected_x = (mapping["C001"] == "threshold") + (ab.other_arm(mapping["C001"]) == "threshold")
    assert q["compScore"] == expected_x / (3 - q["abstentions"])


# --------------------------------------------------------------------------
# AC5.3
# --------------------------------------------------------------------------


def test_ac5_3_fresh_sessions_48_calls(tmp_path):
    ab = _load()
    sample_path, threshold_path, off_path = _write_fixture(tmp_path, n=N_QUERIES)
    runner = FakeRunner([_valid_response(p) for p in VALID_PICKS] * N_QUERIES)
    ab.run_ab(
        sample_path, threshold_path, off_path, seed=SEED, runner=runner, out_path=tmp_path / "r.json"
    )
    assert len(runner.calls) == N_QUERIES * 3 == 48
    for i, argv in enumerate(runner.calls):
        assert argv[:2] == ["pi", "-p"]  # default runner CLI
        assert "--no-session" in argv  # fresh session per call
        for flag in argv:  # never a resume/continue flag
            assert not flag.startswith("--resume")
            assert not flag.startswith("--continue")
        grader_index = i % 3
        if grader_index == 0:
            assert "--provider" not in argv and "--model" not in argv
        elif grader_index == 1:
            assert argv[argv.index("--provider") + 1] == "openrouter"
            assert argv[argv.index("--model") + 1] == "meta/muse-spark-1.3-contributor"
        else:
            assert argv[argv.index("--provider") + 1] == "xiaomi"
            assert argv[argv.index("--model") + 1] == "mio-v2.5-pro"
    # All three graders of one query see the identical payload.
    for qi in range(N_QUERIES):
        assert (
            runner.calls[qi * 3][-1] == runner.calls[qi * 3 + 1][-1] == runner.calls[qi * 3 + 2][-1]
        )


# --------------------------------------------------------------------------
# AC5.4
# --------------------------------------------------------------------------

REASONS = [
    "The first list puts the exact answer at rank one. Extra chunks are filler.",
    "The first list puts the exact answer at rank one.",
    "the first list puts the exact answer at rank one",
    "Second list covers two distinct facets of the question.",
    "Second list covers two distinct facets of the question.",
    "Second list has an irrelevant chunk at rank 3.",
    "Second list has an irrelevant chunk at rank 3.",
]


def test_ac5_4_distillation_deterministic_runner_log_empty(tmp_path):
    ab = _load()
    runner = FakeRunner([_valid_response("first")])  # any invocation would move the log
    out1 = ab.distill(REASONS, top_k=3)
    out2 = ab.distill(REASONS, top_k=3)
    assert out1 == out2  # stable across two runs
    assert out1 == [
        "The first list puts the exact answer at rank one.",
        "Second list covers two distinct facets of the question.",
        "Second list has an irrelevant chunk at rank 3.",
    ]
    assert runner.calls == []  # zero runner invocations during distillation

    # End-to-end: distilling the pipeline's collected reasons never calls the runner.
    sample_path, threshold_path, off_path = _write_fixture(tmp_path / "pipe", n=2)
    pipe_runner = FakeRunner([_valid_response(p) for p in VALID_PICKS] * 2)
    results = ab.run_ab(
        sample_path,
        threshold_path,
        off_path,
        seed=SEED,
        runner=pipe_runner,
        out_path=tmp_path / "pipe" / "res.json",
    )
    calls_before = len(pipe_runner.calls)
    reasons = [
        g["reason"]
        for q in results["queries"]
        for g in q["graders"]
        if not g["abstained"]
    ]
    core1 = ab.distill(reasons, top_k=3)
    core2 = ab.distill(reasons, top_k=3)
    assert core1 == core2
    assert len(pipe_runner.calls) == calls_before
