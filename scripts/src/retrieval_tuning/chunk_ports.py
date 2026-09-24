"""Stdlib-only ports of the product's MarkdownChunker and CodeChunker for the chunk-window eval.

Same contracts as the C# chunkers (docs/adr/0036, 0048): no chunk over budget, a bounded tail
overlay for markdown, a cut section's heading deferred to the chunk holding its content, and
code chunks that tile the file's lines. Sub-fence re-fencing is not ported: an oversized fence
falls back to per-line units.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Callable, Sequence

TokenCount = Callable[[str], int]

OVERLAY_TOKENS = 48


@dataclass(frozen=True)
class MarkdownChunk:
    text: str
    sections: tuple[str, ...]


@dataclass(frozen=True)
class CodeChunk:
    text: str
    line_start: int
    line_end: int


@dataclass
class _Unit:
    lines: list[str]
    tokens: int
    heading_level: int = 0
    source_header: bool = False

    @property
    def is_heading(self) -> bool:
        return self.heading_level > 0

    @property
    def is_blank(self) -> bool:
        return len(self.lines) == 1 and not self.lines[0].strip()

    @property
    def is_contentful(self) -> bool:
        return not self.is_blank and not self.is_heading

    @property
    def is_section_opener(self) -> bool:
        return self.is_heading and self.heading_level <= 2 and not self.source_header


def slug(heading: str) -> str:
    """The eval sets' `file#anchor` form of a heading: lowercase words joined by hyphens."""
    return "-".join(re.findall(r"[a-z0-9]+", heading.lower()))


def distinct_in_order(items: Sequence[str]) -> list[str]:
    seen: set[str] = set()
    return [x for x in items if not (x in seen or seen.add(x))]


def _split_lines(text: str) -> list[str]:
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    lines = text.splitlines(keepends=True)
    return lines


def _largest_prefix(text: str, max_tokens: int, count: TokenCount) -> int:
    lo, hi = 0, len(text)
    while lo < hi:
        mid = lo + (hi - lo + 1) // 2
        if count(text[:mid]) <= max_tokens:
            lo = mid
        else:
            hi = mid - 1
    return lo


def _heading_level(line: str) -> int:
    trimmed = line.lstrip()
    level = len(trimmed) - len(trimmed.lstrip("#"))
    if 1 <= level <= 6 and level < len(trimmed) and trimmed[level] == " " and trimmed[level + 1:].strip():
        return level
    return 0


def _add_unit_or_split(units: list[_Unit], text: str, max_tokens: int, count: TokenCount) -> None:
    n = count(text)
    if n <= max_tokens:
        units.append(_Unit([text], n))
        return
    remaining = text
    while remaining:
        head_len = max(1, _largest_prefix(remaining, max_tokens, count))
        head = remaining[:head_len]
        units.append(_Unit([head], count(head)))
        remaining = remaining[head_len:]


def _is_fence(line: str) -> bool:
    trimmed = line.lstrip()
    return trimmed.startswith("```") or trimmed.startswith("~~~")


def _markdown_units(text: str, max_tokens: int, count: TokenCount) -> list[_Unit]:
    units: list[_Unit] = []
    fence: list[str] | None = None
    for line in _split_lines(text):
        if fence is None:
            if _is_fence(line):
                fence = [line]
            else:
                _add_unit_or_split(units, line, max_tokens, count)
            continue
        fence.append(line)
        if _is_fence(line):
            _flush_fence(units, fence, max_tokens, count)
            fence = None
    if fence is not None:
        _flush_fence(units, fence, max_tokens, count)
    for unit in units:
        if len(unit.lines) == 1:
            unit.heading_level = _heading_level(unit.lines[0])
            unit.source_header = unit.lines[0].lstrip().lower().startswith("## source:")
    return units


def _flush_fence(units: list[_Unit], fence: list[str], max_tokens: int, count: TokenCount) -> None:
    exact = count("".join(fence))
    if exact <= max_tokens:
        units.append(_Unit(fence, exact))
        return
    for line in fence:
        _add_unit_or_split(units, line, max_tokens, count)


def _contexts(units: list[_Unit]) -> list[str]:
    """The leaf heading in force before each unit (levels 1-2, as HeadingPathParser keeps)."""
    stack: list[tuple[int, str]] = []
    contexts: list[str] = []
    for unit in units:
        contexts.append(stack[-1][1] if stack else "")
        if unit.is_section_opener:
            level = unit.heading_level
            while stack and stack[-1][0] >= level:
                stack.pop()
            stack.append((level, unit.lines[0].lstrip()[level:].strip()))
    return contexts


def _joined(units: list[_Unit]) -> str:
    return "".join(line for unit in units for line in unit.lines)


def _defer_open_section(chunk: list[_Unit], new_count: int, units: list[_Unit], cursor: int) -> int:
    new_start = len(chunk) - new_count
    opener = next((i for i in range(len(chunk) - 1, new_start, -1) if chunk[i].is_section_opener), -1)
    if opener < 0:
        return 0
    has_own_content = any(u.is_contentful for u in chunk[opener + 1:])
    idx = cursor
    while idx < len(units) and units[idx].is_blank:
        idx += 1
    continues = idx < len(units) and not units[idx].is_section_opener
    if has_own_content and not continues:
        return 0
    if not any(u.is_contentful for u in chunk[new_start:opener]):
        return 0
    removed = len(chunk) - opener
    del chunk[opener:]
    return removed


def chunk_markdown(text: str, max_tokens: int, count: TokenCount,
                   overlay_tokens: int = OVERLAY_TOKENS) -> list[MarkdownChunk]:
    """MarkdownChunker.ChunkWithHeadings: greedy line units, tail overlay, cut sections deferred."""
    overlay_tokens = min(overlay_tokens, max(0, max_tokens - 1))
    units = _markdown_units(text, max_tokens, count)
    contexts = _contexts(units)
    chunks: list[MarkdownChunk] = []
    previous: list[_Unit] | None = None
    cursor = 0
    while cursor < len(units):
        overlay: list[_Unit] = []
        used = 0
        for unit in reversed(previous or []):
            if used + unit.tokens > overlay_tokens:
                break
            overlay.insert(0, unit)
            used += unit.tokens
        chunk = list(overlay)
        tokens = used
        new_count = 0
        c = cursor
        while c < len(units):
            if new_count > 0 and tokens + units[c].tokens > max_tokens:
                break
            chunk.append(units[c])
            tokens += units[c].tokens
            new_count += 1
            c += 1
        while count(_joined(chunk)) > max_tokens:
            if len(chunk) > new_count:
                chunk.pop(0)
                continue
            if new_count <= 1:
                break
            chunk.pop()
            new_count -= 1
            c -= 1
        while (deferred := _defer_open_section(chunk, new_count, units, c)) > 0:
            new_count -= deferred
            c -= deferred
        body = _joined(chunk)
        if body.strip():
            new_start = len(chunk) - new_count
            leaves = [contexts[cursor + i] for i, u in enumerate(chunk[new_start:]) if u.is_contentful]
            sections = tuple(distinct_in_order([slug(leaf) for leaf in leaves if leaf]))
            chunks.append(MarkdownChunk(body, sections))
        previous = chunk
        cursor = c
    return chunks


def _brace_delta(text: str) -> int:
    return text.count("{") - text.count("}")


def chunk_code(text: str, max_tokens: int, count: TokenCount) -> list[CodeChunk]:
    """CodeChunker.Chunk: blank-line blocks, last brace-balanced boundary within budget, no overlay."""
    lines = _split_lines(text)
    if not lines or all(not line.strip() for line in lines):
        return []
    units: list[tuple[str, int, int, int, int]] = []
    balance = 0

    def add_line(line: str, number: int) -> None:
        nonlocal balance
        n = count(line)
        if n <= max_tokens:
            balance += _brace_delta(line)
            units.append((line, n, number, number, balance))
            return
        remaining = line
        while remaining:
            head = remaining[:max(1, _largest_prefix(remaining, max_tokens, count))]
            balance += _brace_delta(head)
            units.append((head, count(head), number, number, balance))
            remaining = remaining[len(head):]

    def add_block(block: list[str], start: int, end: int) -> None:
        nonlocal balance
        joined = "".join(block)
        n = count(joined)
        if n <= max_tokens:
            balance += _brace_delta(joined)
            units.append((joined, n, start, end, balance))
            return
        for i, line in enumerate(block):
            add_line(line, start + i)

    block: list[str] = []
    block_start = 1
    for i, line in enumerate(lines):
        block.append(line)
        at_transition = (not line.strip() and i + 1 < len(lines) and lines[i + 1].strip()
                         and any(x.strip() for x in block))
        if at_transition:
            add_block(block, block_start, i + 1)
            block = []
            block_start = i + 2
    if block:
        add_block(block, block_start, len(lines))

    chunks: list[CodeChunk] = []
    cursor = 0
    while cursor < len(units):
        tokens, reach, c = 0, cursor, cursor
        while c < len(units):
            if c > cursor and tokens + units[c][1] > max_tokens:
                break
            tokens += units[c][1]
            reach = c
            c += 1
        end = next((k for k in range(reach, cursor - 1, -1) if units[k][4] == 0), reach)
        while end > cursor and count("".join(u[0] for u in units[cursor:end + 1])) > max_tokens:
            end -= 1
        chunks.append(CodeChunk("".join(u[0] for u in units[cursor:end + 1]), units[cursor][2], units[end][3]))
        cursor = end + 1
    return chunks
