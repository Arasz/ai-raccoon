"""F5 gate: the AC evidence collector's model-pin check must compare the
recorded revision to the pinned constant — a non-empty string is not a pin.

The collector itself runs against real artifacts (heavy); these tests pin the
pure gate it applies.
"""

import importlib.util
from pathlib import Path

COLLECTOR_PATH = (Path(__file__).resolve().parents[1] / "retrieval_tuning"
                  / "collect_ac_evidence.py")


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
