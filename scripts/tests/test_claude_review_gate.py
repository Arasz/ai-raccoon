"""The Claude review gate (.github/scripts/claude-review-gate.sh) decides whether a push is reviewed.

It reads `gh pr view --json additions,deletions,reviews` on stdin and prints `review=true|false`:
an approved PR is never re-reviewed, an open change request is always re-reviewed, and
otherwise a diff of SMALL_DIFF_LINES changed lines or fewer is skipped.
"""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

import pytest

GATE = Path(__file__).resolve().parents[2] / ".github" / "scripts" / "claude-review-gate.sh"


def _decide(additions: int, deletions: int, reviews: list[tuple[str, str]]) -> str:
    payload = {
        "additions": additions,
        "deletions": deletions,
        "reviews": [{"author": {"login": login}, "state": state} for login, state in reviews],
    }
    run = subprocess.run(["bash", str(GATE)], input=json.dumps(payload), capture_output=True, text=True, check=True)
    lines = [line for line in run.stdout.splitlines() if line.startswith("review=")]
    assert len(lines) == 1, run.stdout
    return lines[0].removeprefix("review=")


def test_a_large_unreviewed_diff_is_reviewed():
    assert _decide(400, 20, []) == "true"


def test_a_small_unreviewed_diff_is_skipped():
    assert _decide(45, 1, []) == "false"


def test_the_threshold_is_inclusive_at_fifty_changed_lines():
    assert _decide(40, 10, []) == "false"
    assert _decide(40, 11, []) == "true"


def test_an_approved_pr_is_not_reviewed_again_however_large():
    assert _decide(5000, 0, [("claude", "CHANGES_REQUESTED"), ("claude", "APPROVED")]) == "false"


def test_an_open_change_request_is_reviewed_even_when_small():
    assert _decide(2, 0, [("claude", "APPROVED"), ("claude", "CHANGES_REQUESTED")]) == "true"


@pytest.mark.parametrize("ignored", ["COMMENTED", "DISMISSED"])
def test_comments_and_dismissed_reviews_are_not_verdicts(ignored):
    assert _decide(2, 0, [("claude", "CHANGES_REQUESTED"), ("claude", ignored)]) == "true"
    assert _decide(400, 0, [("claude", "APPROVED"), ("claude", ignored)]) == "false"


def test_a_human_approval_does_not_stop_the_claude_review():
    assert _decide(400, 0, [("Arasz", "APPROVED")]) == "true"


def test_the_bot_login_form_counts_as_claude():
    assert _decide(400, 0, [("claude[bot]", "APPROVED")]) == "false"
