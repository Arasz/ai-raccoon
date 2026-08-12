#!/usr/bin/env python3
"""One frontmatter extractor: where a `---` block ends, and what its top-level keys are.

Line-oriented rather than YAML-parsed — ADR-0005 keeps pyyaml optional, so nothing behavioural
may depend on it. A caller that needs real YAML types parses `Frontmatter.raw` itself.
"""
from __future__ import annotations

import re
from typing import Dict, List, NamedTuple, Optional, Tuple

FENCE = "---"

# A top-level key: at the start of its line, so an indented continuation is never one.
KEY_RE = re.compile(r"^([A-Za-z_][A-Za-z0-9_.-]*):\s*(.*)$")

# The block scalar indicators a folded value is written with; its text is on the lines below.
BLOCK_INDICATORS = (">", ">-", ">+", "|", "|-", "|+", "")


class Entry(NamedTuple):
    """One top-level key with every line it owns, continuations included, verbatim."""

    key: str
    lines: Tuple[str, ...]

    @property
    def inline(self) -> str:
        """Whatever follows `key:` on the key's own line, stripped — quotes and all."""
        return KEY_RE.match(self.lines[0]).group(2).strip()

    def value(self) -> str:
        """The scalar, a folded or nested block joined into one line.

        Quotes are kept: `Frontmatter.raw` is what a caller hands a real YAML parser, so a
        value stripped here would disagree with the one that parser reads.
        """
        parts = [] if self.inline in BLOCK_INDICATORS else [self.inline]
        parts += [line.strip() for line in self.lines[1:] if line.strip()]
        return " ".join(parts).strip()


class Frontmatter(NamedTuple):
    """A document split at its frontmatter fence.

    `head` is everything through the closing fence, so `head + body` is the original text.
    `well_formed` is false when a line inside the block is neither a top-level key, an
    indented continuation, nor blank — a shape no line-oriented reader can resolve.
    """

    present: bool
    head: str
    raw: str
    body: str
    entries: Tuple[Entry, ...]
    well_formed: bool

    def fields(self) -> Dict[str, str]:
        """Top-level key -> folded value. A repeated key keeps the last, as YAML does."""
        return {entry.key: entry.value() for entry in self.entries}

    def duplicate_keys(self) -> List[str]:
        """Keys appearing more than once, in first-seen order.

        Rule 11's oracle: a YAML parser resolves a duplicate to the last value silently, so a
        validator reading the first would pass a file every host reads differently.
        """
        seen: Dict[str, int] = {}
        order: List[str] = []
        for entry in self.entries:
            seen[entry.key] = seen.get(entry.key, 0) + 1
            if seen[entry.key] == 1:
                order.append(entry.key)
        return [key for key in order if seen[key] > 1]

    def entry(self, key: str) -> Optional[Entry]:
        """The last entry for `key`, or None. Last, because that is the value YAML reads."""
        found = [e for e in self.entries if e.key == key]
        return found[-1] if found else None


_ABSENT = Frontmatter(False, "", "", "", (), False)


def split(text: str) -> Frontmatter:
    """Split `text` at its frontmatter fence; `Frontmatter.present` is false when it has none.

    The opening fence must be the first line: a `---` further down is a thematic break (#241).
    """
    lines = text.splitlines(keepends=True)
    if not lines or lines[0].strip() != FENCE:
        return _ABSENT._replace(body=text)
    close = next((i for i, line in enumerate(lines[1:], 1) if line.strip() == FENCE), None)
    if close is None:
        return _ABSENT._replace(body=text)
    inner = lines[1:close]
    return Frontmatter(
        present=True,
        head="".join(lines[:close + 1]),
        raw="".join(inner),
        body="".join(lines[close + 1:]),
        entries=_entries(inner),
        well_formed=all(KEY_RE.match(line) or not line.strip() or line[:1] in (" ", "\t")
                        for line in inner),
    )


def _entries(lines: List[str]) -> Tuple[Entry, ...]:
    """Group `lines` into one entry per top-level key; anything before the first key is dropped."""
    out: List[Tuple[str, List[str]]] = []
    for line in lines:
        match = KEY_RE.match(line)
        if match:
            out.append((match.group(1), [line]))
        elif out:
            out[-1][1].append(line)
    return tuple(Entry(key, tuple(owned)) for key, owned in out)


def fields(text: str) -> Dict[str, str]:
    """Every top-level frontmatter key of `text` mapped to its folded value; {} when it has none."""
    return split(text).fields()
