"""Unit tests for retrieval_tuning.evaluate — settings application + scoring orchestration."""

import math
import os

import pytest

from retrieval_tuning.evaluate import evaluate
from retrieval_tuning.scoring import Metrics


def entry(**overrides):
    base = {
        "id": "E001",
        "category": "ADR (Decision)",
        "query": "what does adr 42 decide?",
        "expectedSource": "docs:adr:0042-*.md",
        "targetProjectId": "ai-raccoon",
        "targetScope": "project",
        "searchLimit": 5,
    }
    base.update(overrides)
    return base


class FakeServer:
    """Stand-in for ScratchServer: records search calls, returns canned results."""

    def __init__(self, data_root, binary="fake-bin", port=48321, results_by_id=None):
        self.data_root = data_root
        self.binary = binary
        self.port = port
        self.results_by_id = results_by_id or {}
        self.search_calls = []

    def search(self, corpus_entry):
        self.search_calls.append(corpus_entry)
        return self.results_by_id.get(corpus_entry["id"], [])


@pytest.fixture
def recording_binary(tmp_path):
    """A fake ai-raccoon that appends its argv to a log (for the settings path)."""
    log = tmp_path / "settings-calls.log"
    script = tmp_path / "fake-settings.py"
    script.write_text(
        "#!/usr/bin/env python3\n"
        "import os, sys\n"
        "with open(os.environ['FAKE_BIN_LOG'], 'a') as f:\n"
        "    f.write('|'.join(sys.argv[1:]) + '\\n')\n"
        "sys.exit(int(os.environ.get('FAKE_BIN_EXIT', '0')))\n"
    )
    os.chmod(script, 0o755)
    return script, log


class TestEvaluate:
    def test_returns_hand_computed_metrics(self, tmp_path):
        corpus = [entry(id="E1"), entry(id="E2")]
        server = FakeServer(
            data_root=tmp_path,
            results_by_id={
                # E1: target at rank 1 -> nDCG 1.0; E2: target at rank 2 -> 1/log2(3)
                "E1": [{"hash": "abc123", "sourceFile": "docs/adr/0042-widget.md"}],
                "E2": [
                    {"hash": "zzz", "sourceFile": "docs/other.md"},
                    {"hash": "abc123", "sourceFile": "docs/adr/0042-widget.md"},
                ],
            },
        )
        metrics = evaluate(server, {}, corpus, apply_settings=False)
        assert isinstance(metrics, Metrics)
        expected_mean = (1.0 + 1.0 / math.log2(3)) / 2.0
        assert metrics.mean_ndcg5 == pytest.approx(expected_mean)
        assert metrics.mean_mrr5 == pytest.approx((1.0 + 0.5) / 2.0)
        assert metrics.hit3_rate == pytest.approx(1.0)
        assert metrics.hit1_rate == pytest.approx(0.5)
        assert len(metrics.per_query) == 2
        assert set(metrics.per_category) == {"ADR (Decision)"}
        assert metrics.config == {}

    def test_applies_settings_before_searching(self, tmp_path, recording_binary, monkeypatch):
        script, log = recording_binary
        monkeypatch.setenv("FAKE_BIN_LOG", str(log))
        corpus = [entry(id="E1")]
        server = FakeServer(data_root=tmp_path, binary=str(script), results_by_id={"E1": []})
        evaluate(server, {"rrfK": 5, "fusion": True}, corpus, apply_settings=True)
        calls = [line.split("|") for line in log.read_text().splitlines()]
        assert len(calls) == 2
        assert calls[0][4:] == ["settings", "retrieval", "rrfk", "set", "5"]
        assert calls[1][4:] == ["settings", "retrieval", "fusion", "enable"]

    def test_apply_settings_false_never_spawns(self, tmp_path, recording_binary, monkeypatch):
        script, log = recording_binary
        monkeypatch.setenv("FAKE_BIN_LOG", str(log))
        corpus = [entry(id="E1")]
        server = FakeServer(data_root=tmp_path, binary=str(script), results_by_id={"E1": []})
        evaluate(server, {"rrfK": 5}, corpus, apply_settings=False)
        assert not log.exists()

    def test_empty_settings_dict_skips_settings(self, tmp_path, recording_binary, monkeypatch):
        script, log = recording_binary
        monkeypatch.setenv("FAKE_BIN_LOG", str(log))
        corpus = [entry(id="E1")]
        server = FakeServer(data_root=tmp_path, binary=str(script), results_by_id={"E1": []})
        evaluate(server, {}, corpus, apply_settings=True)
        assert not log.exists()

    def test_per_category_breakdown(self, tmp_path):
        corpus = [entry(id="E1", category="file"), entry(id="E2", category="non-file")]
        server = FakeServer(
            data_root=tmp_path,
            results_by_id={
                "E1": [{"hash": "abc123", "sourceFile": "docs/adr/0042-widget.md"}],
                "E2": [],
            },
        )
        metrics = evaluate(server, {}, corpus, apply_settings=False)
        assert metrics.per_category["file"].mean_ndcg5 == pytest.approx(1.0)
        assert metrics.per_category["non-file"].mean_ndcg5 == pytest.approx(0.0)

    def test_every_corpus_entry_is_searched(self, tmp_path):
        corpus = [entry(id="E1"), entry(id="E2"), entry(id="E3")]
        server = FakeServer(data_root=tmp_path)
        evaluate(server, {}, corpus, apply_settings=False)
        assert [c["id"] for c in server.search_calls] == ["E1", "E2", "E3"]
