"""Deliver the canonical model-groups registry into a scaffolded project.

One function, called from `Scaffolder.run`: copy
`features/common/data/model-groups.json` to `<target>/.ai-badger/model-groups.json` and
record its provenance. The copy is managed, rewritten on every run (recorded with
`seedOnce: false`): the delivered file is framework truth, and the manifest entry is what
the freshness guard and drift compare it against. Consumer projects never edit it;
per-consumer level/model mapping only reads it.
"""
from __future__ import annotations

import shutil
from pathlib import Path

SOURCE = Path("features") / "common" / "data" / "model-groups.json"
TARGET_NAME = "model-groups.json"


def deliver(ctx) -> None:
    """Copy the canonical registry into the scaffold and record its provenance."""
    src = ctx.root / SOURCE
    if not src.is_file():
        ctx.notes.append("model-groups registry missing from the catalog — skipped")
        return
    dest = ctx.aib / TARGET_NAME
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(src, dest)
    ctx.record_template(src, dest)
