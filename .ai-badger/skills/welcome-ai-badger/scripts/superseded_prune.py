"""The prune, one of the scaffold's collaborators.

Deletes what an earlier run placed and this config no longer asks for. Three ways to stop
asking: decline via `config.exclude`, the delivering stack leaving (#116), or the catalog
source disappearing (#130) — including a skill whose whole directory the framework dropped
(#243). Nothing the project edited or owns is ever removed silently: it is reported instead.
"""
from __future__ import annotations

import shutil
from typing import Any, Dict, List, Optional

from _shared import _within
from scaffold_context import ScaffoldContext


class SupersededPrune:
    """Removes a previous run's copies this config no longer asks for, naming each one."""

    def __init__(self, ctx: ScaffoldContext, skill_delivery):
        self.ctx = ctx
        self.skill_delivery = skill_delivery

    def prune(self, entries: List[Dict[str, Any]]) -> None:
        """Prune every superseded entry of the manifest a previous run left."""
        handled: List[str] = []
        for entry in entries:
            reason = self._reason(entry)
            rel = entry.get("target", "")
            if reason is None or any(rel.startswith(prefix) for prefix in handled):
                continue
            path = self.ctx.target / rel
            directory = path.is_dir() and not path.is_symlink()
            if not _within(self.ctx.target, path) or not (directory or path.is_file()):
                continue
            if directory:
                # A declined skill's directory is the exclusion notes' to report: the framework
                # still ships it, and the project may re-include it.
                if self._declined(entry):
                    continue
                handled.append(rel.rstrip("/") + "/")
                owned = self.skill_delivery.project_owned_files(path, entry.get("name") or "")
                if owned:
                    self.ctx.notes.append(f"{rel} holds project-owned {', '.join(owned)} — "
                                          f"left in place ({reason})")
                    continue
            edited = self._edited_here(entry, entries)
            if edited is None:
                self.ctx.notes.append(
                    f"{rel} left in place — cannot tell whether it was edited "
                    f"(no outputHash recorded) ({reason})")
                continue
            if edited:
                self.ctx.notes.append(f"{rel} was edited here — left in place ({reason})")
                continue
            if directory:
                shutil.rmtree(path)
            else:
                path.unlink()
            self.ctx.notes.append(f"removed {rel} — {reason}")

    def _declined(self, entry: Dict[str, Any]) -> bool:
        """True when `config.exclude` names the item this entry delivered."""
        return entry.get("name") in self.ctx.excluded.get(entry.get("feature"), set())

    def _reason(self, entry: Dict[str, Any]) -> Optional[str]:
        """Why this config no longer asks for a file a previous run placed, or None to keep it."""
        import badger_lib as bl

        feature, source = entry.get("feature"), entry.get("source")
        if self._declined(entry):
            return f"declined in config.exclude.{feature}"
        if bl.is_orphaned(entry, bl.delivering_stacks(self.ctx.config)):
            return f"stack '{entry.get('stack')}' is no longer in config.stacks"
        if feature in bl.EXCLUDABLE_FEATURES and source and not (self.ctx.root / source).exists():
            return "no longer in the framework catalog"
        return None

    def _edited_here(self, entry: Dict[str, Any], entries: List[Dict[str, Any]]
                      ) -> Optional[bool]:
        """Whether the on-disk copy differs from what this run would write — None when the
        comparison cannot be made at all.

        A `hashes_source` entry's `hash` is the framework SOURCE's hash (ADR-0006), which
        never matches rendered output, so its `outputHash` (D11) is what compares here when
        present. Without one — an older manifest, or a seed-once entry recorded before its
        first copy — the answer is unknown, not "edited": the caller must not blame the
        project for an edit it cannot show. A directory entry owns its subtree, so an edit to
        any entry nested inside it counts too — pruning the tree would otherwise take it along.
        """
        import badger_lib as bl

        rel = (entry.get("target") or "").rstrip("/")
        path = self.ctx.target / rel
        if path.is_file():
            if "outputHash" in entry:
                return bl.content_hash_ignoring_version_stamp(path) != entry["outputHash"]
            if bl.feature_type(entry.get("feature")).hashes_source:
                return None
            return bl.sha256_file(path) != entry.get("hash")
        if not path.is_dir():
            return False
        fingerprint = bl.dir_content_hash(
            path, exclude=bl.SKILL_EXCLUDE_PATTERNS + ["extensions"],
            exclude_rel=bl.nested_entry_targets(entries, rel))
        if fingerprint["content_hash"] != entry.get("hash"):
            return True
        nested = [self._edited_here(n, entries) for n in entries
                  if (n.get("target") or "").startswith(rel + "/")]
        if any(n is True for n in nested):
            return True
        if any(n is None for n in nested):
            return None
        return False
