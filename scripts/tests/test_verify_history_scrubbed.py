"""Tests for verify-history-scrubbed.py's exit-code logic — the #414 S6b post-rewrite gate.

Follows the scripts/tests convention for a hyphenated top-level entrypoint (see
test_nightly_triage.py): load it by file path with importlib, then exercise it against a
local fixture repo built in a temp dir. No network access — the real remote is only cloned
when the script runs for real (see the runbook)."""

import importlib.util
import subprocess
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parent.parent / "verify-history-scrubbed.py"


@pytest.fixture(scope="module")
def verify():
    spec = importlib.util.spec_from_file_location("verify_history_scrubbed", SCRIPT)
    assert spec is not None and spec.loader is not None
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _git(repo_dir: Path, *args: str) -> None:
    subprocess.run(["git", *args], cwd=repo_dir, check=True, capture_output=True)


def _repo_with_one_committed_file(tmp_path: Path, filename: str = "secret.txt") -> Path:
    repo = tmp_path / "fixture-repo"
    repo.mkdir()
    _git(repo, "init", "--quiet", "--initial-branch=main")
    _git(repo, "config", "user.email", "test@example.com")
    _git(repo, "config", "user.name", "Test")
    (repo / filename).write_text("private prose\n")
    _git(repo, "add", filename)
    _git(repo, "commit", "--quiet", "-m", "add tracked file")
    return repo


def test_commit_count_is_nonzero_for_a_path_with_history(verify, tmp_path):
    repo = _repo_with_one_committed_file(tmp_path)
    assert verify.commit_count(repo, "secret.txt") == 1


def test_commit_count_is_zero_for_a_path_never_committed(verify, tmp_path):
    repo = _repo_with_one_committed_file(tmp_path)
    assert verify.commit_count(repo, "never-existed.txt") == 0


def test_dirty_paths_names_only_paths_with_history(verify):
    results = {"a.txt": 2, "b.txt": 0, "c.txt": 1}
    assert verify.dirty_paths(results) == ["a.txt", "c.txt"]


def test_main_exits_nonzero_when_a_tracked_path_still_has_history(verify, tmp_path):
    repo = _repo_with_one_committed_file(tmp_path)
    code = verify.main(["--repo", str(repo), "--path", "secret.txt"])
    assert code == 1


def test_main_exits_zero_when_no_tracked_path_has_history(verify, tmp_path):
    repo = _repo_with_one_committed_file(tmp_path, filename="unrelated.txt")
    code = verify.main(["--repo", str(repo), "--path", "still-not-there.txt"])
    assert code == 0
