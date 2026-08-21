"""Unit tests for the sextant probe (plan §4, gate G1).

Covers the pure parts: corpus label -> hash resolution against the
investigation doc appendix, the recorded expectation tables (top-5 hash pins),
the top-5 matcher (drift detection), bound-port parsing and the port /
data-root safety asserts. The live server lifecycle is not exercised here.
"""

import pytest

from retrieval_tuning import probe_sextant as probe

# Investigation doc appendix (docs/work/2026-08-20-hybrid-retrieval-fusion-investigation.md):
# label -> 8-char hash prefix of the 9-entry corpus.
DOC_CORPUS = {
    "astrolabe": "61dfec67",
    "invoice": "c7053da1",
    "signal-15": "27e3ce27",
    "review-note": "9648f7e6",
    "guide-intro": "bd1587a2",
    "guide-details": "93f41fe5",
    "cross-project": "b032ac63",
    "sextant": "80e36737",
    "notes-digest": "97948a1a",
}

# Investigation doc §3a: Query B default-config top-9 (hash table). Top-5 pins.
DOC_QUERY_B_TOP5 = ["bd1587a2", "93f41fe5", "61dfec67", "80e36737", "b032ac63"]

QUERY_IDS = [
    "astrolabe-original", "astrolabe-richer", "sextant-zero-overlap",
    "sextant-richer", "invoice", "widget-guide", "alien-tokens",
]


class TestCorpusLabels:
    def test_labels_resolve_to_the_doc_appendix_hashes(self):
        assert probe.LABEL_TO_HASH == DOC_CORPUS

    def test_all_hashes_are_8_char_hex(self):
        for label, h in probe.LABEL_TO_HASH.items():
            assert len(h) == 8, label
            int(h, 16)


class TestExpectations:
    def test_expectations_cover_all_seven_probe_queries(self):
        assert set(probe.EXPECTATIONS) == set(QUERY_IDS)

    def test_every_pin_is_a_recorded_hash_or_an_explicit_unresolved(self):
        for qid, exp in probe.EXPECTATIONS.items():
            assert set(exp["pins"]) <= {1, 2, 3, 4, 5}, qid
            for pos, pin in exp["pins"].items():
                if pin["type"] == "hash":
                    assert len(pin["hash"]) == 8, (qid, pos)
                    int(pin["hash"], 16)
                elif pin["type"] == "label":
                    assert pin["label"] in probe.LABEL_TO_HASH, (qid, pos)
                elif pin["type"] == "unresolved":
                    assert pin["label"], (qid, pos)
                else:
                    pytest.fail(f"{qid}:{pos} unknown pin type {pin['type']}")

    def test_sextant_zero_overlap_pins_match_the_doc_top5_table(self):
        pins = probe.EXPECTATIONS["sextant-zero-overlap"]["pins"]
        assert [pins[i]["hash"] for i in (1, 2, 3, 4, 5)] == DOC_QUERY_B_TOP5

    def test_every_expectation_cites_its_source(self):
        for qid, exp in probe.EXPECTATIONS.items():
            assert exp["source"], qid

    def test_astrolabe_original_position3_is_pinned_to_review_note(self):
        # Doc §4 records rank 3 as 'near-miss'; the first live probe run (2026-08-21)
        # observed review-note 9648f7e6 there and the pin now carries that provenance.
        pins = probe.EXPECTATIONS["astrolabe-original"]["pins"]
        assert pins[3] == {"type": "hash", "hash": "9648f7e6"}


class TestResolveExpected:
    def test_resolves_label_pins_via_corpus(self):
        exp = probe.EXPECTATIONS["invoice"]
        expected = probe.resolve_expected(exp)
        assert expected[1] == ("1", "bd1587a2")  # guide-intro
        assert expected[3] == ("3", "c7053da1")  # invoice

    def test_unresolved_position_resolves_to_none_with_reason(self):
        exp = {"pins": {3: {"type": "unresolved", "label": "near-miss"}}}
        expected = probe.resolve_expected(exp)
        assert expected[3] == ("3", None)  # unresolved sentinel


class TestMatchTop5:
    def test_identical_top5_passes(self):
        observed = ["bd1587a2", "93f41fe5", "61dfec67", "80e36737", "b032ac63"]
        exp = probe.EXPECTATIONS["sextant-zero-overlap"]
        assert probe.match_top5(observed, exp) == []

    def test_reordered_top5_is_detected(self):
        observed = ["93f41fe5", "bd1587a2", "61dfec67", "80e36737", "b032ac63"]
        exp = probe.EXPECTATIONS["sextant-zero-overlap"]
        mismatches = probe.match_top5(observed, exp)
        assert len(mismatches) == 2
        assert mismatches[0]["position"] == 1
        assert mismatches[0]["observed"] == "93f41fe5"
        assert mismatches[0]["expected"] == "bd1587a2"

    def test_replaced_top5_entry_is_detected(self):
        observed = ["bd1587a2", "93f41fe5", "c7053da1", "80e36737", "b032ac63"]
        exp = probe.EXPECTATIONS["sextant-zero-overlap"]
        mismatches = probe.match_top5(observed, exp)
        assert len(mismatches) == 1
        assert mismatches[0]["position"] == 3
        assert mismatches[0]["expected"] == "61dfec67"
        assert mismatches[0]["observed"] == "c7053da1"

    def test_unresolved_position_fails_by_design(self):
        exp = {"pins": {3: {"type": "unresolved", "label": "near-miss"}}}
        observed = ["61dfec67", "bd1587a2", "b032ac63", "93f41fe5", "80e36737"]
        mismatches = probe.match_top5(observed, exp)
        assert any(m["position"] == 3 and m["expected"] is None for m in mismatches)

    def test_unpinned_positions_are_not_asserted(self):
        # invoice pins only positions 1-3 (doc records top-3); positions 4-5
        # must not produce mismatches no matter what is observed.
        observed = ["bd1587a2", "93f41fe5", "c7053da1", "anything", "else"]
        exp = probe.EXPECTATIONS["invoice"]
        assert probe.match_top5(observed, exp) == []


class TestPortParsing:
    def test_parses_bound_port_from_serve_log(self):
        log = "info: Now listening on: http://127.0.0.1:61234\n"
        assert probe.parse_bound_port(log) == 61234

    def test_missing_listening_line_raises(self):
        with pytest.raises(RuntimeError, match="bound port"):
            probe.parse_bound_port("nothing here")


class TestSafetyAsserts:
    def test_port_7721_is_rejected(self):
        with pytest.raises(probe.SafetyError, match="7721"):
            probe.assert_scratch_safety(port=7721, data_root="/tmp/scratch-bank")

    def test_live_bank_data_root_is_rejected(self):
        with pytest.raises(probe.SafetyError, match="data-root"):
            probe.assert_scratch_safety(port=61234, data_root="/Users/arasz/.ai-raccoon")

    def test_scratch_port_and_root_pass(self):
        # must not raise
        probe.assert_scratch_safety(port=61234, data_root="/tmp/continue-testing-algorithm/runs/x")
