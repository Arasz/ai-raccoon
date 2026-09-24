"""Pads one token row to a length bucket so an engine that compiles per shape sees few shapes."""

from __future__ import annotations

from typing import Sequence


def pad_row(ids: list[int], multiple: int, pad_id: int,
            buckets: Sequence[int] = ()) -> tuple[list[int], list[int]]:
    """(ids padded with pad_id, attention mask with 0 over the padding).

    With buckets, pads up to the smallest bucket that fits (none fits: no padding); otherwise up to
    the next multiple, where multiple 0 pads nothing.
    """
    if buckets:
        target = min((b for b in buckets if b >= len(ids)), default=len(ids))
    else:
        target = len(ids) + (-len(ids) % multiple if multiple > 0 else 0)
    padding = target - len(ids)
    return ids + [pad_id] * padding, [1] * len(ids) + [0] * padding
