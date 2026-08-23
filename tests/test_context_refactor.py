#!/usr/bin/env python3
"""Validation tests for research-based refactor of .ai-badger/ context files.

Each test verifies a specific acceptance criterion from the refactor plan.
Run: python3 tests/test_context_refactor.py
"""
import re
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent
CLAUDE_MD = PROJECT_ROOT / ".ai-badger" / "CLAUDE.md"
CODE_REVIEWER_MD = PROJECT_ROOT / ".ai-badger" / "agents" / "code-reviewer.md"
DOC_INSTRUCTIONS = PROJECT_ROOT / ".ai-badger" / "instructions" / "documentation.instructions.md"
DELEGATION_MD = PROJECT_ROOT / ".ai-badger" / "delegation.md"

# Tier 1 invariants (always-loaded, critical)
TIER1_NAMES = [
    "TDD is mandatory",
    "Done means proven",
    "Check the source, not your own reasoning",
    "Store secrets outside tracked files",
    "Clean layering",
    "Plain names",
    "Ask if a simpler shape would do",
    "Use platform security APIs",
]

failures = []


def fail(test_name: str, msg: str):
    failures.append((test_name, msg))
    print(f"  FAIL: {test_name} — {msg}")


def ok(test_name: str):
    print(f"  OK:   {test_name}")


def read_file(path: Path) -> str:
    return path.read_text(encoding="utf-8")


# ──────────────────────────────────────────────
# Change 1: Constraint count — ≤8 Tier 1 invariants
# ──────────────────────────────────────────────
def test_tier1_count():
    """CLAUDE.md invariant section should have ≤8 items (constraint-count tax)."""
    content = read_file(CLAUDE_MD)
    inv_section = re.search(
        r"## Non-negotiable invariants\n(.*?)(?=\n## )", content, re.DOTALL
    )
    if not inv_section:
        fail("tier1_count", "Could not find invariant section")
        return

    inv_text = inv_section.group(1)
    names = re.findall(r"- \*\*(.+?)\*\*", inv_text)
    count = len(names)

    if count > 8:
        fail("tier1_count", f"Found {count} invariants in CLAUDE.md (target: ≤8)")
    else:
        ok("tier1_count")


# ──────────────────────────────────────────────
# Change 2: Positive framing — no negative-framed invariants
# ──────────────────────────────────────────────
def test_no_negative_framing():
    """No invariant should start with 'No' or 'Never' (positive framing)."""
    content = read_file(CLAUDE_MD)
    inv_section = re.search(
        r"## Non-negotiable invariants\n(.*?)(?=\n## )", content, re.DOTALL
    )
    if not inv_section:
        fail("no_negative", "Could not find invariant section")
        return

    inv_text = inv_section.group(1)
    tier1_lines = []
    for name in TIER1_NAMES:
        pattern = re.compile(rf"- \*\*{re.escape(name)}\*\*.*", re.IGNORECASE)
        match = pattern.search(inv_text)
        if match:
            tier1_lines.append((name, match.group(0)))

    negative_starts = []
    for name, line in tier1_lines:
        after_bold = re.sub(r"^- \*\*.*?\*\*\s*[—–-]\s*", "", line).strip()
        if after_bold.lower().startswith("no ") or after_bold.lower().startswith("never "):
            negative_starts.append(name)

    if negative_starts:
        fail("no_negative", f"Invariants with negative framing: {negative_starts}")
    else:
        ok("no_negative")


# ──────────────────────────────────────────────
# Change 5: Position effects — Tier 1 at head, TDD at tail
# ──────────────────────────────────────────────
def test_tier1_at_head():
    """First invariant must be TDD (most critical)."""
    content = read_file(CLAUDE_MD)
    inv_section = re.search(
        r"## Non-negotiable invariants\n(.*?)(?=\n## )", content, re.DOTALL
    )
    if not inv_section:
        fail("tier1_at_head", "Could not find invariant section")
        return

    inv_text = inv_section.group(1)
    names = re.findall(r"- \*\*(.+?)\*\*", inv_text)
    if not names:
        fail("tier1_at_head", "No invariants found")
        return

    first_name = names[0]
    if first_name.lower() != "tdd is mandatory":
        fail("tier1_at_head", f"First invariant is '{first_name}', expected 'TDD is mandatory'")
    else:
        ok("tier1_at_head")


def test_tier1_at_tail():
    """Last invariant must be Tier 1 (position effects: end is strong)."""
    content = read_file(CLAUDE_MD)
    inv_section = re.search(
        r"## Non-negotiable invariants\n(.*?)(?=\n## )", content, re.DOTALL
    )
    if not inv_section:
        fail("tier1_at_tail", "Could not find invariant section")
        return

    inv_text = inv_section.group(1)
    names = re.findall(r"- \*\*(.+?)\*\*", inv_text)
    if not names:
        fail("tier1_at_tail", "No invariants found")
        return

    last_name = names[-1]
    tier1_lower = [n.lower() for n in TIER1_NAMES]
    if last_name.lower() not in tier1_lower:
        fail("tier1_at_tail", f"Last invariant is not Tier 1: '{last_name}'")
    else:
        ok("tier1_at_tail")


# ──────────────────────────────────────────────
# Change 7: Third-person framing in code-reviewer
# ──────────────────────────────────────────────
def test_code_reviewer_third_person():
    """code-reviewer.md must include third-person framing guidance."""
    content = read_file(CODE_REVIEWER_MD)
    has_third_person = (
        "third-person" in content.lower()
        or "objective criteria" in content.lower()
        or "the spec requires" in content.lower()
    )
    if not has_third_person:
        fail("code_reviewer_third_person", "No third-person framing guidance found")
    else:
        ok("code_reviewer_third_person")


# ──────────────────────────────────────────────
# Change 6: Escalation rules in code-reviewer
# ──────────────────────────────────────────────
def test_code_reviewer_escalation():
    """code-reviewer.md must include escalation rule for non-converging reviews."""
    content = read_file(CODE_REVIEWER_MD)
    has_escalation = (
        "restart" in content.lower()
        or "consolidated" in content.lower()
        or "escalat" in content.lower()
    )
    if not has_escalation:
        fail("code_reviewer_escalation", "No escalation/restart rule found")
    else:
        ok("code_reviewer_escalation")


# ──────────────────────────────────────────────
# Change 9: Humanization in documentation instructions
# ──────────────────────────────────────────────
def test_doc_instructions_humanization():
    """documentation.instructions.md must include humanization rules."""
    if not DOC_INSTRUCTIONS.exists():
        fail("doc_humanization", f"File not found: {DOC_INSTRUCTIONS}")
        return

    content = read_file(DOC_INSTRUCTIONS)
    has_humanization = (
        "burstiness" in content.lower()
        or "sentence length" in content.lower()
        or "humaniz" in content.lower()
    )
    if not has_humanization:
        fail("doc_humanization", "No humanization rules found in documentation instructions")
    else:
        ok("doc_humanization")


# ──────────────────────────────────────────────
# Change 3: Reasoning-model guidance in delegation
# ──────────────────────────────────────────────
def test_delegation_reasoning_model():
    """delegation.md must include reasoning-model-aware dispatch guidance."""
    content = read_file(DELEGATION_MD)
    has_reasoning = (
        "reasoning model" in content.lower()
        or "strip" in content.lower()
        or "scaffolding" in content.lower()
    )
    if not has_reasoning:
        fail("delegation_reasoning", "No reasoning-model guidance found in delegation.md")
    else:
        ok("delegation_reasoning")


# ──────────────────────────────────────────────
# Structural: CLAUDE.md references tiered invariants
# ──────────────────────────────────────────────
def test_claude_md_tier_reference():
    """CLAUDE.md should reference the full invariant set in .ai-badger/invariants/."""
    content = read_file(CLAUDE_MD)
    has_reference = ".ai-badger/invariants/" in content
    if not has_reference:
        fail("claude_md_tier_ref", "CLAUDE.md does not reference .ai-badger/invariants/")
    else:
        ok("claude_md_tier_ref")


# ──────────────────────────────────────────────
# Main
# ──────────────────────────────────────────────
def main():
    print("\n=== Context Refactor Validation Tests ===\n")

    test_tier1_count()
    test_no_negative_framing()
    test_tier1_at_head()
    test_tier1_at_tail()
    test_code_reviewer_third_person()
    test_code_reviewer_escalation()
    test_doc_instructions_humanization()
    test_delegation_reasoning_model()
    test_claude_md_tier_reference()

    total = 9
    print(f"\n{'='*50}")
    if failures:
        print(f"RESULT: {len(failures)} FAIL, {total - len(failures)} PASS\n")
        for name, msg in failures:
            print(f"  ✗ {name}: {msg}")
        sys.exit(1)
    else:
        print(f"RESULT: {total} PASS, 0 FAIL — all checks green")
        sys.exit(0)


if __name__ == "__main__":
    main()
