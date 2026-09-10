"""F5 gate: the AC evidence collector's model-pin check must compare the
recorded revision to the pinned constant — a non-empty string is not a pin.

F1 gate: the frozen replication contract lives in `data/knobs.json["PARAMS"]`
and drives the live eval (`retrieve._FROZEN`, `evaluate`, `ingest`). KNOB_DEFAULTS
is pinned by `test_retrieval_tuning_settings`, PARAMS was pinned by nothing, and
the collector read its "frozen" expectations from the same JSON it audits — a
JSON edit moved both sides together. The literal below is the independent
anchor (tests may hold literals by design); the collector keeps its own literal
for runtime use, pinned to this one.

The collector itself runs against real artifacts (heavy); these tests pin the
pure gate it applies.
"""

import importlib.util
from pathlib import Path

COLLECTOR_PATH = (Path(__file__).resolve().parents[1] / "retrieval_tuning"
                  / "collect_ac_evidence.py")

# The frozen replication contract the plan records, verbatim. Additions are
# deliberate; a value edit here and in data/knobs.json together is the only
# way to move the gate, and that is a visible, reviewable diff by design.
FROZEN_PARAMS = {
    "rrfK": 60,
    "ftsWeight": 1,
    "vectorWeight": 1,
    "limit": 8,
    "minRelativeScore": 0.6,
    "sourceLambda": 0.1,
    "consolidationThreshold": 0.1,
    "docScoreFormula": "max",
    "candidateWindow": "max3x100",
    "structureAlpha": 0.5,
    "fusionNoRegression": False,
    "scope": "project",
    "kind": "memory",
    "model": "Salesforce/SFR-Embedding-Code-400M_R",
}


def _load_collector():
    spec = importlib.util.spec_from_file_location("collect_ac_evidence", COLLECTOR_PATH)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def test_model_pin_check_fails_on_unpinned_or_empty_or_mismatched():
    mod = _load_collector()
    pinned = "cb950dc80d677c6fdc00f56c8ddd20ca2642c59e"
    # a non-empty but wrong revision FAILS (the old check passed anything)
    failures = mod.model_pin_failures(
        {"modelRevision": "some-other-rev", "modelBytes": 5},
        {"modelRevision": "some-other-rev"}, pinned)
    assert failures and "cb950dc8" in failures[0]
    # a missing record FAILS
    assert mod.model_pin_failures({"modelBytes": 5}, {}, pinned)
    # zero bytes FAILS (an empty cache is not a pin)
    assert mod.model_pin_failures(
        {"modelRevision": pinned, "modelBytes": 0},
        {"modelRevision": pinned}, pinned)
    # results disagreeing with params FAILS
    assert mod.model_pin_failures(
        {"modelRevision": pinned, "modelBytes": 5},
        {"modelRevision": "different"}, pinned)


def test_model_pin_check_passes_on_the_pinned_revision():
    mod = _load_collector()
    pinned = "cb950dc80d677c6fdc00f56c8ddd20ca2642c59e"
    params = {"modelRevision": pinned, "modelBytes": 869254400}
    results = {"modelRevision": pinned}
    assert mod.model_pin_failures(params, results, pinned) == []


def test_repo_params_are_pinned_to_the_frozen_contract():
    """F1: a JSON edit to PARAMS must break a gate, not silently move the eval."""
    from retrieval_tuning import repo_data  # noqa: PLC0415

    params = repo_data.KNOBS["PARAMS"]
    assert params == FROZEN_PARAMS
    # The eight knobs PARAMS shares with the settings defaults must agree; the
    # two copies of the contract may not drift apart.
    defaults = repo_data.KNOBS["KNOB_DEFAULTS"]
    shared = {k: v for k, v in defaults.items() if k != "fusion"}
    assert {k: params[k] for k in shared} == shared
    assert params["limit"] == 8
    assert params["minRelativeScore"] == 0.6


def test_collector_frozen_knobs_are_literals_pinned_to_the_contract():
    """F1: the collector must not read its expectations from the JSON it audits."""
    mod = _load_collector()
    assert mod.FROZEN_PARAMS == FROZEN_PARAMS
    assert set(mod.FROZEN_KNOBS) <= set(FROZEN_PARAMS)


def test_frozen_knob_check_fails_on_a_tampered_params_dict():
    """F1 fail-capability: one changed value names itself; no JSON involved."""
    mod = _load_collector()
    tampered = {**FROZEN_PARAMS, "rrfK": 61}
    failures = mod.frozen_knob_failures(tampered)
    assert failures and "rrfK" in failures[0]
    # ... and passes on the frozen contract itself.
    assert mod.frozen_knob_failures(dict(FROZEN_PARAMS)) == []
