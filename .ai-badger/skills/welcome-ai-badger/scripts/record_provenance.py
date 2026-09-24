"""Provenance hashing for scaffold.py's manifest entries — kept out of its line budget (#194).

One function, `provenance_hashes`, called from `Scaffolder.record`.
"""
from __future__ import annotations

from pathlib import Path
from typing import Any, Dict


def provenance_hashes(bl, feature: str, source: Path, target: Path,
                       extra: Dict[str, Any]) -> Dict[str, Any]:
    """The hash fields a manifest entry for (feature, source, target) should carry.

    Directory entries (skills) get `hash`/`dirMeta` for the target and `sourceHash`/
    `sourceMeta` for the framework source (#110). A file entry whose feature type is
    `hashes_source` (templates, adjustments) records the framework SOURCE's hash as `hash` —
    drift.compare re-hashes the source for file entries, and any other choice can never match
    (ADR-0006) — plus the written output's own hash as `outputHash`, so a later prune can tell
    an edited output from a superseded one instead of always reading a rendered file as
    "edited" against its unrendered source (L3-5, D11). `outputHash` is left off when the
    target does not exist yet at record time (a seed-once entry recorded ahead of its first
    copy): prune then treats that entry as unknown rather than guessing.
    """
    if source.is_dir():
        fingerprint = bl.dir_content_hash(
            target, exclude=bl.SKILL_EXCLUDE_PATTERNS + ["extensions"],
            exclude_rel=extra.get("projectOwned"))
        source_print = bl.dir_content_hash(
            source, exclude=bl.SKILL_EXCLUDE_PATTERNS + ["extensions"])
        return {
            "hash": fingerprint["content_hash"],
            "dirMeta": {
                "file_count": fingerprint["file_count"],
                "dir_count": fingerprint["dir_count"],
            },
            "sourceHash": source_print["content_hash"],
            "sourceMeta": {
                "file_count": source_print["file_count"],
                "dir_count": source_print["dir_count"],
            },
        }
    hashes_source = bl.feature_type(feature).hashes_source
    fields = {"hash": bl.sha256_file(source if hashes_source else target)}
    if hashes_source and target.is_file():
        # Version-stamp-normalized: a rendered template's body carries "Scaffolded by
        # ai-badger <version>", and a raw byte hash would make outputHash churn on every
        # version bump alone, which the rest of the manifest is built to ignore (#206).
        fields["outputHash"] = bl.content_hash_ignoring_version_stamp(target)
    return fields
