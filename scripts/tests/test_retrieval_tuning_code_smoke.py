"""Structural checks for the code-corpus smoke eval-set (plan §12.2 H8/WP8).

code-smoke.json is a SMALL, in-repo sanity fixture (5-10 queries against this
repo's own src/ tree), not the deferred full multi-repo eval-set (OQ5). These
checks pin its shape and catch source-path drift (a renamed/moved file the
fixture still points at) without needing a live server or an ingested bank —
the fuller eval_corpus.py suite already covers anchor resolution against a
real bank copy for the memory eval-set; this fixture has no bank to resolve
against yet.
"""
from __future__ import annotations

from pathlib import Path

from retrieval_tuning.corpus import load_corpus, validate_entries

REPO_ROOT = Path(__file__).resolve().parents[2]
CODE_SMOKE_PATH = (
    REPO_ROOT / "scripts" / "src" / "retrieval_tuning" / "eval-sets" / "code-smoke.json"
)


def _source_to_repo_path(expected_source: str) -> Path:
    """'src:AiRaccoon.Core:Watch:IgnoreRules.cs' -> REPO_ROOT/src/AiRaccoon.Core/Watch/IgnoreRules.cs."""
    return REPO_ROOT.joinpath(*expected_source.split(":"))


class TestCodeSmokeEvalSet:
    def test_file_exists_and_is_small(self):
        assert CODE_SMOKE_PATH.exists()
        entries = load_corpus(CODE_SMOKE_PATH)
        assert 5 <= len(entries) <= 10

    def test_structurally_valid_per_the_shared_corpus_validator(self):
        entries = load_corpus(CODE_SMOKE_PATH)
        problems = validate_entries(entries)  # no db_path: no bank to resolve anchors against yet
        assert problems == []

    def test_every_entry_targets_the_code_corpus(self):
        entries = load_corpus(CODE_SMOKE_PATH)
        for entry in entries:
            assert entry.get("kind") == "code", f"{entry['id']} must set kind: code"

    def test_every_expected_source_resolves_to_a_real_file_in_this_repo(self):
        """Guards against source-path drift: a renamed file the fixture still points at."""
        entries = load_corpus(CODE_SMOKE_PATH)
        for entry in entries:
            source = entry["expectedSource"]
            repo_path = _source_to_repo_path(source)
            assert repo_path.is_file(), f"{entry['id']}: expectedSource '{source}' -> {repo_path} does not exist"

    def test_ids_are_unique(self):
        entries = load_corpus(CODE_SMOKE_PATH)
        ids = [entry["id"] for entry in entries]
        assert len(ids) == len(set(ids))
