#!/usr/bin/env python3
"""Refuse to move content that looks like it carries a credential.

Shared by both paths that move content across a boundary: the inbound Hermes learned-skill
sync, and feed-badger's outbound draft PR. High-confidence literal shapes only — this is a
guard, not proof, and it never redacts: a match refuses the move and reports *where*.

The reporting contract is what makes a finding safe to print, log, or put in a PR body:
a finding is `{file, pattern}` where `pattern` is always a label from UNSAFE_LITERAL_LABELS.
No scanned byte ever reaches a caller (see CodeQL py/clear-text-logging).
"""
from __future__ import annotations

import re
from pathlib import Path
from typing import Dict, Iterable, List

# High-confidence literal shapes only — refuse and report, never redact.
UNSAFE_LITERAL_PATTERNS = (
    ("provider api key", re.compile(r"\b(?:sk|rk|pk)-[A-Za-z0-9_-]{16,}")),
    ("github token", re.compile(r"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}")),
    ("private key block", re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    ("aws secret access key",
     re.compile(r"AWS_SECRET_ACCESS_KEY\s*[=:]\s*[\"']?[A-Za-z0-9/+=]{40,}")),
    ("credential assignment",
     re.compile(r"(?i)\b(?:password|passwd|secret|token|api[_-]?key)\s*[=:]\s*"
                r"[\"']?[A-Za-z0-9/+=._-]{20,}")),
)
LITERAL_SCAN_MAX_BYTES = 1_000_000
# The only vocabulary a finding may report. Keeping it fixed is what makes a finding
# safe to print: a new pattern contributes a label, never captured text.
UNSAFE_LITERAL_LABELS = frozenset(label for label, _ in UNSAFE_LITERAL_PATTERNS)


def scan_file(path: Path, rel: str) -> List[Dict[str, str]]:
    """Findings for one file. Skips symlinks, unreadable files, and oversized ones."""
    if not path.is_file() or path.is_symlink():
        return []
    try:
        if path.stat().st_size > LITERAL_SCAN_MAX_BYTES:
            return []
        text = path.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return []
    findings = []
    for label, pattern in UNSAFE_LITERAL_PATTERNS:
        # bool() pins the match to a boolean: no match object, group, or span can
        # escape this scope, so no scanned byte can reach a caller.
        if bool(pattern.search(text)):
            findings.append({"file": rel, "pattern": label})
    return findings


def scan_tree(source_dir: Path) -> List[Dict[str, str]]:
    """Findings for every file under `source_dir`, reported relative to it."""
    findings: List[Dict[str, str]] = []
    for path in sorted(Path(source_dir).rglob("*")):
        findings.extend(scan_file(path, path.relative_to(source_dir).as_posix()))
    return findings


def scan_paths(root: Path, rel_paths: Iterable[str]) -> List[Dict[str, str]]:
    """Findings for the named files and directories, reported relative to `root`.

    A path that does not exist is skipped rather than raised: the caller is naming what it
    intends to publish, and a stale name is not a reason to fail closed on the others.
    """
    root = Path(root)
    findings: List[Dict[str, str]] = []
    for rel in rel_paths:
        target = root / rel
        if target.is_dir():
            for path in sorted(target.rglob("*")):
                findings.extend(scan_file(path, path.relative_to(root).as_posix()))
        else:
            findings.extend(scan_file(target, str(rel)))
    return findings
