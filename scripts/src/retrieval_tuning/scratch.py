"""Scratch bank lifecycle: one composition for "fresh copy + started server".

Every repeat of an eval run needs the same three steps in the same order:
byte-copy the quiesced base into a fresh data root, run the caller's pre-server
checks (row parity/refusal), then start the scratch server. That composition
lives here once; `evaluate` wires it into the repeat loop, and the single-run
path (an operator-prepared data root) still goes through the same
`start_server` in `retrieval_tuning.server`.

`fresh_scratch_copy` is the copy half: stale WAL/SHM sidecars are removed so a
prior repeat's writes can never bleed into the next run; the base itself is
opened read-only by the copy.
"""

from __future__ import annotations

import shutil
from contextlib import contextmanager
from pathlib import Path

__all__ = ["ScratchRefused", "fresh_scratch_copy", "scratch_server"]


class ScratchRefused(RuntimeError):
    """A pre-server check refused the scratch; nothing has served it."""


def fresh_scratch_copy(base_db: Path, data_root: Path) -> Path:
    """Byte-copy the quiesced base into data_root/memory.db (one per repeat)."""
    base_db, data_root = Path(base_db), Path(data_root)
    if not base_db.exists():
        raise FileNotFoundError(f"scratch base not found: {base_db}")
    data_root.mkdir(parents=True, exist_ok=True)
    dest = data_root / "memory.db"
    for stale in (dest, dest.with_name(dest.name + "-wal"),
                  dest.with_name(dest.name + "-shm")):
        stale.unlink(missing_ok=True)
    shutil.copyfile(base_db, dest)
    return dest


@contextmanager
def scratch_server(base_db: Path, data_root: Path, *, binary: str = "ai-raccoon",
                   before_start=None):
    """Fresh copy -> `before_start(db)` -> started scratch server, in that order.

    `before_start` runs after the copy and before any server exists, so a
    refused or already-mutated scratch never serves; raise `ScratchRefused`
    from it to stop the run. The server import is lazy so this module stays
    stdlib-only for the CI harness lane (the SDK comes from server.py/mcp.py).
    """
    fresh_scratch_copy(base_db, data_root)
    db = Path(data_root) / "memory.db"
    if before_start is not None:
        before_start(db)
    from .server import start_server  # noqa: PLC0415 — lazy: keeps this module stdlib-only

    with start_server(data_root, binary=binary) as server:
        yield server
