"""Pads one token row to a length bucket so an engine that compiles per shape sees few shapes."""

from __future__ import annotations


def pad_row(ids: list[int], multiple: int, pad_id: int) -> tuple[list[int], list[int]]:
    """(ids padded with pad_id up to the next multiple, attention mask with 0 over the padding); multiple 0 pads nothing."""
    padding = -len(ids) % multiple if multiple > 0 else 0
    return ids + [pad_id] * padding, [1] * len(ids) + [0] * padding
