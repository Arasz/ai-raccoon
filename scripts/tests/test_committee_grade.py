"""P4 committee tests (plan §P4, AC4.1-AC4.5).

Everything runs against a FakeRunner — no network, no live grader CLI:

- AC4.1 state-machine paths: accept-first-round, nudge-then-accept,
  replace-then-accept (version 1 exhausts 3 rounds), chain-exhaustion
  (version 3 exhausts the 9-round chain) — each asserting archived forms
  + manifest hashes.
- AC4.2 gating: malformed form -> <=2 re-asks (logged, not round-consuming),
  then void -> inconsistency path; 2/3 majority rejected; 3/3 accepted.
- AC4.3 sampling: 16 = changed + controls per the backfill rule, determinism,
  grade-blind stratification, committed metrics fixture contract.
- AC4.4 caps: >3 rounds/version triggers replacement; >9 chain rounds and a
  4th version are impossible by construction.
- AC4.5 H1 smoke: env-gated (AI_RACCOON_SMOKE_GRADERS=1) live CLI check.
"""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import random
import re
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "committee_grade.py"
FIXTURE_METRICS = (
    REPO_ROOT
    / "docs"
    / "work"
    / "threshold-committee-eval"
    / "fixtures"
    / "metrics-fixture.json"
)

_spec = importlib.util.spec_from_file_location("committee_grade", MODULE_PATH)
cg = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = (
    cg  # required before exec: dataclasses resolves annotations via sys.modules
)
_spec.loader.exec_module(cg)

HASH_RE = re.compile(r"^\d+\. hash=(\S+)$", re.MULTILINE)
NUDGE = cg.NUDGE_PHRASE


def ids_from_brief(brief: str) -> tuple[str, str, str]:
    sid = re.search(r'slotId="([^"]+)"', brief).group(1)
    qid = re.search(r'queryId="([^"]+)"', brief).group(1)
    arm = re.search(r'arm="([^"]+)"', brief).group(1)
    return sid, qid, arm


def hashes_from_brief(brief: str) -> list[str]:
    return HASH_RE.findall(brief)


def form_from_brief(brief: str, overrides: dict[str, str] | None = None) -> dict:
    """Build a schema-valid form for whatever query/arm a brief carries."""
    sid, qid, arm = ids_from_brief(brief)
    overrides = overrides or {}
    return {
        "slotId": sid,
        "queryId": qid,
        "arm": arm,
        "chunks": [
            {"hash": h, "grade": overrides.get(h, "A"), "reason": "answers the query"}
            for h in hashes_from_brief(brief)
        ],
        "overall": "ok",
    }


class FakeRunner:
    """Records every grader call; stdout comes from the test's responder."""

    def __init__(self, responder):
        self._responder = responder
        self.calls: list[dict] = []

    def run(self, brief: str, grader) -> str:
        self.calls.append({"grader": grader.index, "brief": brief})
        return self._responder(grader.index, brief)

    @property
    def call_count(self) -> int:
        return len(self.calls)

    def briefs_for(self, grader_index: int) -> list[str]:
        return [c["brief"] for c in self.calls if c["grader"] == grader_index]


def all_agree(index: int, brief: str) -> str:
    return json.dumps(form_from_brief(brief))


def mk_query(qid: str, overlap: float) -> cg.QueryMetrics:
    off = tuple(
        cg.Chunk(f"{qid}-off-{i}", f"snippet {qid} off {i}", f"src/{qid}-off-{i}.cs")
        for i in range(1, 9)
    )
    if overlap == 1.0:
        threshold = off
    else:
        threshold = tuple(
            cg.Chunk(
                f"{qid}-thr-{i}", f"snippet {qid} thr {i}", f"src/{qid}-thr-{i}.cs"
            )
            for i in range(1, 9)
        )
    return cg.QueryMetrics(
        query_id=qid,
        query_text=f"question about {qid}?",
        project_id="ai-raccoon",
        arms={"off": off, "threshold": threshold},
        top8_set_overlap=overlap,
    )


def assert_manifest_intact(forms_root: Path) -> list[dict]:
    """Every manifest entry must point at a real file with a matching sha256."""
    manifest = json.loads(
        (forms_root / "forms-manifest.json").read_text(encoding="utf-8")
    )
    entries = manifest["forms"]
    assert entries, "manifest must not be empty"
    for entry in entries:
        f = forms_root / entry["path"]
        assert f.is_file(), entry["path"]
        assert hashlib.sha256(f.read_bytes()).hexdigest() == entry["sha256"], entry[
            "path"
        ]
    return entries


def round_dir(forms_root: Path, slot_id: str, round_no: int) -> Path:
    return forms_root / "forms" / slot_id / str(round_no)


# ---------------------------------------------------------------------------
# AC4.1 — state machine paths (-k machine)
# ---------------------------------------------------------------------------


def test_machine_state_paths(tmp_path):
    # (a) accept-first-round: 3/3 identical vectors in round 1.
    query = mk_query("M001", 0.875)
    runner = FakeRunner(all_agree)
    report = cg.grade_slot(query, "off", runner, tmp_path)
    assert report["accepted"] is True
    assert report["exhausted"] is False
    assert report["rounds"] == 1
    assert report["nudges"] == 0
    assert report["replacements"] == 0
    assert runner.call_count == 3  # one round = 3 parallel submissions
    assert all(NUDGE not in b for b in (c["brief"] for c in runner.calls))
    assert report["gradeVector"] == [
        {"hash": f"M001-off-{i}", "grade": "A"} for i in range(1, 9)
    ]
    assert report["answerChunks"] == [f"M001-off-{i}" for i in range(1, 9)]
    for n in (1, 2, 3):
        assert (round_dir(tmp_path, "M001__off", 1) / f"grader-{n}.json").is_file()
    entries = assert_manifest_intact(tmp_path)
    assert len([e for e in entries if e["kind"] == "form"]) == 3
    assert len([e for e in entries if e["kind"] == "payload"]) == 3

    # (b) nudge-then-accept: grader 3 dissents in round 1, converges on the nudge.
    shutil.rmtree(tmp_path)
    tmp_path.mkdir()

    def nudge_responder(index: int, brief: str) -> str:
        if NUDGE in brief:
            return json.dumps(form_from_brief(brief))
        if index == 3:  # only grader 3 dissents in round 1
            return json.dumps(form_from_brief(brief, overrides={"M002-off-2": "0"}))
        return json.dumps(form_from_brief(brief))

    query = mk_query("M002", 0.875)
    runner = FakeRunner(nudge_responder)
    report = cg.grade_slot(query, "off", runner, tmp_path)
    assert report["accepted"] is True
    assert report["rounds"] == 2
    assert report["nudges"] == 1
    assert report["roundVerdicts"] == ["inconsistent", "accepted"]
    assert runner.call_count == 6
    round2_briefs = [c["brief"] for c in runner.calls[3:]]
    assert all(NUDGE in b for b in round2_briefs)
    # each grader is nudged against ITS OWN previous grades: grader 3's round-2
    # brief embeds its dissenting round-1 form
    assert '"hash": "M002-off-2"' in runner.briefs_for(3)[1]
    assert '"grade": "0"' in runner.briefs_for(3)[1]
    entries = assert_manifest_intact(tmp_path)
    assert len([e for e in entries if e["kind"] == "form"]) == 6
    assert (round_dir(tmp_path, "M002__off", 1) / "grader-3.json").is_file()
    assert (round_dir(tmp_path, "M002__off", 2) / "grader-3.json").is_file()

    # (c) replace-then-accept: version 1 exhausts 3 inconsistent rounds,
    # version 2 (a fresh query from the same stratum) accepts in round 4.
    shutil.rmtree(tmp_path)
    tmp_path.mkdir()
    v1 = mk_query("M003", 0.875)
    fresh = mk_query("M003R", 0.875)

    def replace_responder(index: int, brief: str) -> str:
        if "question about M003?" in brief:
            return json.dumps(
                form_from_brief(
                    brief, overrides={"M003-off-1": "0"} if index == 3 else {}
                )
            )
        return json.dumps(form_from_brief(brief))

    runner = FakeRunner(replace_responder)
    report = cg.grade_slot(
        v1,
        "off",
        runner,
        tmp_path,
        replacement_candidates=[fresh],
        replacement_rng=random.Random(cg.SEED),
        used_query_ids={v1.query_id},
    )
    assert report["accepted"] is True
    assert report["rounds"] == 4
    assert report["replacements"] == 1
    assert [v["queryId"] for v in report["versions"]] == ["M003", "M003R"]
    assert [v["rounds"] for v in report["versions"]] == [3, 1]
    assert [v["outcome"] for v in report["versions"]] == ["replaced", "accepted"]
    assert runner.call_count == 12  # 3 rounds x 3 graders + 3
    version2_briefs = [c["brief"] for c in runner.calls[9:]]
    assert all("question about M003R?" in b for b in version2_briefs)
    assert (round_dir(tmp_path, "M003__off", 4) / "grader-1.json").is_file()
    entries = assert_manifest_intact(tmp_path)
    assert len([e for e in entries if e["kind"] == "form"]) == 12

    # (d) chain-exhaustion: nobody ever converges -> reported, never skipped.
    shutil.rmtree(tmp_path)
    tmp_path.mkdir()

    def never_agree(index: int, brief: str) -> str:
        if index == 3:
            first = hashes_from_brief(brief)[0]
            return json.dumps(form_from_brief(brief, overrides={first: "0"}))
        return json.dumps(form_from_brief(brief))

    v1 = mk_query("M004", 0.875)
    pool = [mk_query("M004R1", 0.875), mk_query("M004R2", 0.875)]
    runner = FakeRunner(never_agree)
    report = cg.grade_slot(
        v1,
        "off",
        runner,
        tmp_path,
        replacement_candidates=pool,
        replacement_rng=random.Random(cg.SEED),
        used_query_ids={v1.query_id},
    )
    assert report["accepted"] is False
    assert report["exhausted"] is True
    assert report["exhaustReason"] == "chain-cap"
    assert report["rounds"] == 9
    assert report["roundVerdicts"] == ["inconsistent"] * 9
    assert [v["queryId"] for v in report["versions"]] == ["M004", "M004R1", "M004R2"]
    assert [v["rounds"] for v in report["versions"]] == [3, 3, 3]
    assert [v["outcome"] for v in report["versions"]] == [
        "replaced",
        "replaced",
        "exhausted",
    ]
    assert report["gradeVector"] is None
    assert report["answerChunks"] is None
    assert runner.call_count == 27
    entries = assert_manifest_intact(tmp_path)
    assert len([e for e in entries if e["kind"] == "form"]) == 27
    assert len([e for e in entries if e["kind"] == "payload"]) == 27


# ---------------------------------------------------------------------------
# AC4.2 — form gating (-k gating)
# ---------------------------------------------------------------------------


def test_form_gating(tmp_path, caplog):
    # The brief must instruct the PoC rubric, form-only output, no free-form verdict.
    query = mk_query("G001", 0.875)
    brief = cg.build_brief(query, "off", "G001__off", cg.GRADER_TRIO[0])
    assert "A" in brief and "1" in brief and "answers the query" in brief
    assert "Output ONLY the JSON form" in brief
    assert "no free-form verdict" in brief
    assert '"grade": "A"|"0"' in brief

    # (a) malformed -> 2 re-asks (same brief), then valid: round 1 still accepts.
    #     Submission 2 is valid JSON that fails the schema — the invalid-but-parsed branch.
    garbage = ["not json at all", '```json\n{"oops": 1}\n```']

    def flaky(index: int, brief: str) -> str:
        if index == 2 and garbage:
            return garbage.pop(0)
        return json.dumps(form_from_brief(brief))

    runner = FakeRunner(flaky)
    with caplog.at_level("WARNING", logger="committee_grade"):
        report = cg.grade_slot(query, "off", runner, tmp_path)
    assert report["accepted"] is True
    assert report["rounds"] == 1  # re-asks are NOT round-consuming
    assert report["reasks"] == 2
    g2 = runner.briefs_for(2)
    assert len(g2) == 3  # initial + exactly 2 re-asks
    assert g2[1] == g2[0] and g2[2] == g2[0]  # re-asked with the same brief
    warnings = [m for m in caplog.messages if "grader 2" in m]
    assert sum("malformed" in m for m in warnings) == 2
    assert (round_dir(tmp_path, "G001__off", 1) / "grader-2-reask-2.json").is_file()
    assert not (round_dir(tmp_path, "G001__off", 1) / "grader-2.json").exists()
    entries = assert_manifest_intact(tmp_path)
    assert len([e for e in entries if e["kind"] == "invalid"]) == 2

    # (b) malformed 3x -> void -> 2/3 majority is INCONSISTENT (never accepted)
    #     -> nudge round 2 accepts.
    shutil.rmtree(tmp_path)
    tmp_path.mkdir()

    def void_then_converge(index: int, brief: str) -> str:
        if NUDGE in brief:
            return json.dumps(form_from_brief(brief))
        if index == 2:
            return "garbage"
        return json.dumps(form_from_brief(brief))

    runner = FakeRunner(void_then_converge)
    report = cg.grade_slot(query, "off", runner, tmp_path)
    assert report["roundVerdicts"][0] == "inconsistent"  # 2/3 majority rejected
    assert report["accepted"] is True
    assert report["rounds"] == 2
    assert len(runner.briefs_for(2)) == 4  # 3 void round-1 submissions + 1 nudge
    assert report["reasks"] == 2

    # (c) 3/3 identical vectors accepted; 2/3 never.
    shutil.rmtree(tmp_path)
    tmp_path.mkdir()
    runner = FakeRunner(all_agree)
    report = cg.grade_slot(query, "off", runner, tmp_path)
    assert report["accepted"] is True
    assert report["roundVerdicts"] == ["accepted"]
    assert report["gradeVector"] == [
        {"hash": f"G001-off-{i}", "grade": "A"} for i in range(1, 9)
    ]

    # _validate_form pins the schema contract (mirrors committee_form.schema.json).
    schema = json.loads(
        (
            REPO_ROOT / "scripts" / "retrieval_tuning" / "committee_form.schema.json"
        ).read_text(encoding="utf-8")
    )
    good = form_from_brief(brief)
    ok_args = {
        "slot_id": "G001__off",
        "query_id": "G001",
        "arm": "off",
        "expected_hashes": {c.hash for c in query.arms["off"]},
    }
    assert cg._validate_form(good, **ok_args)
    assert schema["additionalProperties"] is False
    assert schema["properties"]["chunks"]["items"]["additionalProperties"] is False
    assert schema["properties"]["chunks"]["items"]["properties"]["grade"]["enum"] == [
        "A",
        "0",
    ]
    assert schema["required"] == ["slotId", "queryId", "arm", "chunks", "overall"]

    extra = dict(good)
    extra["surprise"] = 1
    assert not cg._validate_form(extra, **ok_args)  # additionalProperties: false

    bad_grade = json.loads(json.dumps(good))
    bad_grade["chunks"][0]["grade"] = "B"
    assert not cg._validate_form(bad_grade, **ok_args)  # grade enum

    missing_chunk = json.loads(json.dumps(good))
    missing_chunk["chunks"] = missing_chunk["chunks"][:-1]
    assert not cg._validate_form(missing_chunk, **ok_args)  # incomplete form

    wrong_slot = json.loads(json.dumps(good))
    wrong_slot["slotId"] = "other__off"
    assert not cg._validate_form(wrong_slot, **ok_args)  # identity mismatch

    chunk_extra = json.loads(json.dumps(good))
    chunk_extra["chunks"][0]["extra"] = 1
    assert not cg._validate_form(chunk_extra, **ok_args)


# ---------------------------------------------------------------------------
# AC4.3 — sampling (-k sampling)
# ---------------------------------------------------------------------------


def test_sampling(tmp_path):
    queries = [mk_query(f"CHG{i:02d}", 0.875) for i in range(12)]
    queries += [mk_query(f"CTL{i:02d}", 1.0) for i in range(9)]
    changed, controls = cg.stratify(queries)
    assert [q.query_id for q in changed] == sorted(q.query_id for q in changed)
    assert len(changed) == 12 and len(controls) == 9

    picked_changed, picked_controls = cg.pick_sample(changed, controls, seed=cg.SEED)
    assert len(picked_changed) == cg.CHANGED_PICK == 10
    assert len(picked_controls) == 6
    assert len(picked_changed) + len(picked_controls) == cg.SAMPLE_SIZE == 16
    assert {q.query_id for q in picked_changed}.isdisjoint(
        q.query_id for q in picked_controls
    )
    assert all(q.top8_set_overlap < 1.0 for q in picked_changed)
    assert all(q.top8_set_overlap == 1.0 for q in picked_controls)

    # determinism: same metrics + seed -> identical sample
    again = cg.pick_sample(changed, controls, seed=cg.SEED)
    assert [q.query_id for q in again[0]] == [q.query_id for q in picked_changed]
    assert [q.query_id for q in again[1]] == [q.query_id for q in picked_controls]

    # backfill: fewer than 10 changed -> controls fill the sample to 16
    few_changed = [mk_query(f"FEW{i:02d}", 0.5) for i in range(4)]
    many_controls = [mk_query(f"MCTL{i:02d}", 1.0) for i in range(20)]
    pc, pk = cg.pick_sample(few_changed, many_controls, seed=cg.SEED)
    assert len(pc) == 4 and len(pk) == 12 and len(pc) + len(pk) == 16

    # grade-blind: sampling sees only (queryId, overlap); identical ids and
    # overlaps with entirely different text/snippets produce the same sample.
    def rewritten(qs):
        out = []
        for q in qs:
            clone = cg.QueryMetrics(
                query_id=q.query_id,
                query_text="TOTALLY DIFFERENT TEXT " + q.query_id,
                project_id="other-project",
                arms={
                    arm: tuple(
                        cg.Chunk(c.hash + "-x", "unrelated snippet", "elsewhere.py")
                        for c in chunks
                    )
                    for arm, chunks in q.arms.items()
                },
                top8_set_overlap=q.top8_set_overlap,
            )
            out.append(clone)
        return out

    pc2, pk2 = cg.pick_sample(rewritten(changed), rewritten(controls), seed=cg.SEED)
    assert [q.query_id for q in pc2] == [q.query_id for q in picked_changed]
    assert [q.query_id for q in pk2] == [q.query_id for q in picked_controls]

    # sample.json: persisted header records the grader-trio config (P5 asserts
    # equality with its own constants against this record).
    sample_path = tmp_path / "sample.json"
    cg.write_sample_json(
        sample_path,
        seed=cg.SEED,
        picked_changed=picked_changed,
        picked_controls=picked_controls,
        n_changed=len(changed),
        n_controls=len(controls),
    )
    sample = json.loads(sample_path.read_text(encoding="utf-8"))
    header = sample["header"]
    assert header["graders"] == [
        {"index": 1, "provider": None, "model": None, "label": "session-default"},
        {
            "index": 2,
            "provider": "openrouter",
            "model": "meta/muse-spark-1.3-contributor",
            "label": "openrouter/meta/muse-spark-1.3-contributor",
        },
        {
            "index": 3,
            "provider": "openrouter",
            "model": "xiaomi/mimo-v2.5-pro",
            "label": "openrouter/xiaomi/mimo-v2.5-pro",
        },
    ]
    assert header["composition"] == {
        "changedAvailable": 12,
        "controlsAvailable": 9,
        "pickedChanged": 10,
        "pickedControls": 6,
        "total": 16,
        "backfilled": False,
    }
    assert [q["queryId"] for q in sample["queries"]] == [
        q.query_id for q in picked_changed
    ] + [q.query_id for q in picked_controls]

    # end-to-end glue on a tiny fixture with a FakeRunner (no network):
    # sample.json + committee-grades.json + forms-manifest.json all written.
    out_dir = tmp_path / "run"
    tiny = [mk_query("T-CHG", 0.5), mk_query("T-CTL", 1.0)]
    (tmp_path / "tiny-metrics.json").write_text(
        json.dumps(_metrics_doc(tiny)), encoding="utf-8"
    )
    grades = cg.run_committee(
        tmp_path / "tiny-metrics.json",
        out_dir,
        seed=cg.SEED,
        runner=FakeRunner(all_agree),
    )
    assert len(grades["slots"]) == 4  # 2 queries x 2 arms
    assert all(s["accepted"] for s in grades["slots"])
    assert (out_dir / "sample.json").is_file()
    assert (out_dir / "committee-grades.json").is_file()
    assert (out_dir / "forms-manifest.json").is_file()
    assert_manifest_intact(out_dir)


@pytest.mark.skipif(not FIXTURE_METRICS.exists(),
                    reason=f"committed metrics fixture missing: {FIXTURE_METRICS}")
def test_committed_metrics_fixture_contract() -> None:
    """The P3 metrics.json contract, loaded by path; the artifact tree holding
    the fixture was deleted from main (af1c2482), so it skips on a clean checkout."""
    fixture_queries = cg.load_metrics(FIXTURE_METRICS)
    assert len(fixture_queries) == 8
    f_changed, f_controls = cg.stratify(fixture_queries)
    assert len(f_changed) == 5 and len(f_controls) == 3
    for q in fixture_queries:
        assert len(q.arms["off"]) == 8 and len(q.arms["threshold"]) == 8
        assert q.query_text and q.project_id


def _metrics_doc(queries) -> dict:
    return {
        "queries": [
            {
                "queryId": q.query_id,
                "queryText": q.query_text,
                "projectId": q.project_id,
                "arms": {
                    arm: {
                        "results": [
                            {
                                "hash": c.hash,
                                "snippet": c.snippet,
                                "sourceFile": c.source_file,
                                "chunkIndex": i,
                            }
                            for i, c in enumerate(chunks)
                        ]
                    }
                    for arm, chunks in q.arms.items()
                },
                "pair": {
                    "top8SetOverlap": q.top8_set_overlap,
                    "rbo": 0.5,
                    "drop": [],
                    "backfill": [],
                },
            }
            for q in queries
        ]
    }


# ---------------------------------------------------------------------------
# AC4.4 — round caps (-k caps)
# ---------------------------------------------------------------------------


def test_round_caps(tmp_path):
    assert cg.MAX_ROUNDS_PER_VERSION == 3  # initial + <=2 nudges
    assert cg.MAX_CHAIN_ROUNDS_PER_SLOT == 9
    assert cg.MAX_CHAIN_ROUNDS_PER_SLOT == cg.MAX_ROUNDS_PER_VERSION * 3

    def never_agree(index: int, brief: str) -> str:
        if index == 3:
            first = hashes_from_brief(brief)[0]
            return json.dumps(form_from_brief(brief, overrides={first: "0"}))
        return json.dumps(form_from_brief(brief))

    v1 = mk_query("CAP00", 0.875)
    pool = [mk_query(f"CAP{i:02d}", 0.875) for i in range(1, 5)]
    runner = FakeRunner(never_agree)
    used = {v1.query_id}
    report = cg.grade_slot(
        v1,
        "off",
        runner,
        tmp_path,
        replacement_candidates=pool,
        replacement_rng=random.Random(cg.SEED),
        used_query_ids=used,
    )

    # >3 rounds for one query version triggers replacement (never a 4th round).
    assert [v["rounds"] for v in report["versions"]] == [3, 3, 3]
    assert all(v["rounds"] <= cg.MAX_ROUNDS_PER_VERSION for v in report["versions"])
    assert report["versions"][0]["outcome"] == "replaced"
    assert report["versions"][1]["queryId"] != v1.query_id

    # >9 chain rounds and a 4th version are impossible by construction:
    # version 3 exhausts, the slot is reported exhausted, and the two unused
    # pool candidates prove no fresh-budget replacement was drawn.
    assert report["exhausted"] is True
    assert report["accepted"] is False
    assert len(report["versions"]) == 3
    assert report["rounds"] == 9
    assert report["attempts"] == 9
    assert runner.call_count == 27  # 9 rounds x 3 graders — nothing beyond
    assert len(used & {p.query_id for p in pool}) == 2
    assert report["gradeVector"] is None and report["answerChunks"] is None


# ---------------------------------------------------------------------------
# AC4.5 — H1 smoke (-k smoke), env-gated: never run live in unit tests
# ---------------------------------------------------------------------------


def test_smoke_graders_env_gated(tmp_path):
    if not os.environ.get("AI_RACCOON_SMOKE_GRADERS"):
        pytest.skip(
            "H1 live smoke makes 3 real grader calls; set AI_RACCOON_SMOKE_GRADERS=1 to run it"
        )
    out_dir = tmp_path / "smoke"
    proc = subprocess.run(
        [
            sys.executable,
            str(REPO_ROOT / "scripts" / "retrieval_tuning" / "committee_grade.py"),
            "--smoke-graders",
            "--out-dir",
            str(out_dir),
        ],
        capture_output=True,
        text=True,
        check=False,
        timeout=1800,
        cwd=REPO_ROOT,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    entries = assert_manifest_intact(out_dir)
    forms = [e for e in entries if e["kind"] == "form"]
    assert len(forms) == 3  # one schema-valid form per grader model
