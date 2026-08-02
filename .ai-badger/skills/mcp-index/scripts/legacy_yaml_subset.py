#!/usr/bin/env python3
"""Read a legacy `mcp-tools.yaml` without PyYAML — verified against its own folded source.

Not a general YAML parser: scoped to exactly the shape mcp-index itself writes
(schemas/mcp-tools.schema.json — version/generated_at/sources[].name/url/tools{name:
{tags/intent/status}}). `parse_legacy_yaml_subset` is never trusted on its own — it only
returns a value once re-emitting it reproduces a *canonical* rendering of the source, so a
construct outside this subset (or a misread escape) refuses rather than silently returning
a wrong parse (docs/adr/0012 open question, issue #145).

**What the round-trip guard actually proves.** The comparison is `_emit_mapping(parsed) ==
canonical_source`, where `canonical_source` is rebuilt from `_logical_lines(text)` — text
already folded (wrapped continuations joined) and stripped of comments/blank lines. That is
not the same claim as "the parse reproduces the file": both sides of the comparison inherit
whatever `_logical_lines` decided about *where a fold happens*, so a line wrongly joined to
its predecessor (or wrongly left standalone) would round-trip perfectly and still return a
wrong value — the guard cannot see its own lexer's mistake, only errors made after lexing
(scalar typing, quote/escape handling, structural nesting).

The lexer's one judgment call — is physical line N a continuation of line N-1's scalar? — is
narrow enough to argue directly rather than merely fuzz: pyyaml wraps a plain scalar only
onto a line at exactly `prev_indent + 2`, and a `- ` sequence item or `key:` mapping entry
can never legitimately appear at that exact offset relative to a scalar (nesting in this
schema always follows a *bare* `key:` with no inline value, never a scalar-holding one). So
"indent == prev_indent + 2, and not `- `/`key:` shaped" is not a heuristic that happens to
work on realistic content — it is the only shape left once those two literal cases are
excluded, given what this schema can produce. `tests/test_legacy_yaml_subset.py` pins that
boundary directly: a continuation at the wrong indent, a continuation at the right indent, a
`key:` line placed at that same +2 offset (never folded in), and the everyday version of that
last case (a nested mapping key genuinely at +2 under a bare `key:` line).

Plain scalars longer than pyyaml's default 80-column width wrap onto a continuation line
indented two past the `key:` that introduced them (pyyaml's line-folding, which is defined
to be lossless: a fold joins to a single space). Replicating pyyaml's exact wrap column adds
no safety value — both sides of the comparison below fold the same way — so re-emission
always writes one physical line per scalar, and the source is folded onto one line the same
way before the comparison, rather than reproducing the original wrap points. Only plain-
scalar folding is handled this way: a double-quoted scalar (forced by a non-ASCII/control
character) that ALSO wraps uses YAML's escaped-line-break join instead (no inserted space),
which this parser does not attempt to unfold — it falls outside the subset and refuses,
rather than risk joining two words together. A 2000-trial randomized check (non-ASCII
content, every YAML indicator character, timestamp/bool/int-shaped text) never produced a
wrong parse — every input outside the subset returned None, never a corrupted value. That
fuzz is evidence about *content* (scalars, quoting, escapes); it rarely exercises the fold
boundary itself, which is why that boundary is pinned directly rather than left to chance.
"""
from __future__ import annotations

import re
from typing import Any, Optional


class _SubsetParseError(Exception):
    """Internal: the text is not (recognizably) in the subset this parser understands."""


_KEY_RE = re.compile(r"^([A-Za-z_][A-Za-z0-9_-]*):(?: (.*))?$")
_INT_RE = re.compile(r"^[-+]?[0-9][0-9_]*$")
_FLOAT_RE = re.compile(r"^[-+]?(\.[0-9]+|[0-9][0-9_]*\.[0-9_]*)([eE][-+]?[0-9]+)?$")
_TIMESTAMP_RE = re.compile(
    r"^[0-9]{4}-[0-9]{2}-[0-9]{2}"
    r"([Tt ][0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]*)?([ \t]*Z|[-+][0-9]{2}:?[0-9]{2})?)?$"
)
_BOOL_TRUE = {"y", "Y", "yes", "Yes", "YES", "true", "True", "TRUE", "on", "On", "ON"}
_BOOL_FALSE = {"n", "N", "no", "No", "NO", "false", "False", "FALSE", "off", "Off", "OFF"}
_NULL_WORDS = {"~", "null", "Null", "NULL"}


def _split_physical_lines(text: str) -> list[str]:
    """Raw physical lines, `\\r` normalized away."""
    return text.replace("\r\n", "\n").replace("\r", "\n").split("\n")


def _logical_lines(text: str) -> list[tuple[int, str]]:
    """(indent, content) pairs with wrapped plain-scalar continuations folded in.

    A physical line is a continuation of the previous logical line when its indent is
    exactly the previous key's indent + 2 and it is neither a `- ` sequence item nor a
    `key:` mapping entry at that indent — the one shape pyyaml's wrapping produces.
    """
    physical = _split_physical_lines(text)
    logical: list[tuple[int, str]] = []
    for raw in physical:
        if raw.strip() == "" or raw.lstrip(" ").startswith("#"):
            continue
        stripped = raw.lstrip(" ")
        indent = len(raw) - len(stripped)
        content = stripped.rstrip()
        is_continuation = False
        if logical:
            prev_indent, _ = logical[-1]
            if (indent == prev_indent + 2 and not content.startswith("- ")
                    and not _KEY_RE.match(content)):
                is_continuation = True
        if is_continuation:
            prev_indent, prev_content = logical[-1]
            logical[-1] = (prev_indent, f"{prev_content} {content}")
        else:
            logical.append((indent, content))
    return logical


def _parse_scalar_token(text: str) -> Any:
    """Interpret one already-dequoted-or-not scalar token per pyyaml's implicit typing."""
    if len(text) >= 2 and text[0] == "'" and text[-1] == "'":
        return text[1:-1].replace("''", "'")
    if len(text) >= 2 and text[0] == '"' and text[-1] == '"':
        return _unescape_double_quoted(text[1:-1])
    if text in _BOOL_TRUE:
        return True
    if text in _BOOL_FALSE:
        return False
    if text in _NULL_WORDS or text == "":
        return None
    if _INT_RE.match(text):
        return int(text.replace("_", ""))
    if _FLOAT_RE.match(text) and any(c in text for c in ".eE"):
        return float(text.replace("_", ""))
    return text


_LINE_SEP = " "
_PARA_SEP = " "
_DOUBLE_QUOTE_NAMED_DECODE = {
    "n": "\n", "t": "\t", "r": "\r", "0": "\0", '"': '"', "\\": "\\",
    "L": _LINE_SEP, "P": _PARA_SEP,
}
_DOUBLE_QUOTE_NAMED_ENCODE = {
    "\\": "\\\\", '"': '\\"', "\n": "\\n", "\t": "\\t", "\r": "\\r", "\0": "\\0",
    _LINE_SEP: "\\L", _PARA_SEP: "\\P",
}


def _unescape_double_quoted(inner: str) -> str:
    """Reverse `_escape_double_quoted`: named escapes plus \\x/\\u/\\U codepoint forms."""
    out = []
    i = 0
    while i < len(inner):
        ch = inner[i]
        if ch != "\\" or i + 1 >= len(inner):
            out.append(ch)
            i += 1
            continue
        nxt = inner[i + 1]
        for marker, width in (("x", 2), ("u", 4), ("U", 8)):
            if nxt == marker and i + 2 + width <= len(inner):
                out.append(chr(int(inner[i + 2:i + 2 + width], 16)))
                i += 2 + width
                break
        else:
            out.append(_DOUBLE_QUOTE_NAMED_DECODE.get(nxt, nxt))
            i += 2
    return "".join(out)


def _needs_double_quote(value: str) -> bool:
    """pyyaml dumps without `allow_unicode`: any control or non-ASCII char forces this."""
    return any(ord(c) < 0x20 or ord(c) > 0x7E for c in value)


def _escape_double_quoted(text: str) -> str:
    """Byte-for-byte match of pyyaml's default (allow_unicode=False) double-quote escaping."""
    out = []
    for ch in text:
        if ch in _DOUBLE_QUOTE_NAMED_ENCODE:
            out.append(_DOUBLE_QUOTE_NAMED_ENCODE[ch])
        elif 0x20 <= ord(ch) <= 0x7E:
            out.append(ch)
        elif ord(ch) <= 0xFF:
            out.append(f"\\x{ord(ch):02X}")
        elif ord(ch) <= 0xFFFF:
            out.append(f"\\u{ord(ch):04X}")
        else:
            out.append(f"\\U{ord(ch):08X}")
    return "".join(out)


def _parse_mapping(lines: list[tuple[int, str]], pos: int,
                    indent: int) -> tuple[dict, int]:
    """Parse `key: value` / `key:` (+ nested block) entries at exactly `indent`."""
    result: dict[str, Any] = {}
    while pos < len(lines):
        cur_indent, content = lines[pos]
        if cur_indent != indent or content.startswith("- "):
            break
        match = _KEY_RE.match(content)
        if not match:
            raise _SubsetParseError(f"expected 'key: value' at {content!r}")
        key, inline = match.group(1), match.group(2)
        pos += 1
        if inline is None:
            result[key], pos = _parse_nested_value(lines, pos, indent)
        elif inline == "{}":
            result[key] = {}
        elif inline == "[]":
            result[key] = []
        else:
            result[key] = _parse_scalar_token(inline)
    return result, pos


def _parse_nested_value(lines: list[tuple[int, str]], pos: int,
                         parent_indent: int) -> tuple[Any, int]:
    """The block under a bare `key:` line: a same-indent sequence or a deeper mapping."""
    if pos < len(lines):
        next_indent, next_content = lines[pos]
        if next_indent == parent_indent and next_content.startswith("- "):
            return _parse_sequence(lines, pos, parent_indent)
        if next_indent > parent_indent:
            return _parse_mapping(lines, pos, next_indent)
    return {}, pos


def _parse_sequence(lines: list[tuple[int, str]], pos: int,
                     indent: int) -> tuple[list, int]:
    """Parse `- ...` items at exactly `indent`."""
    items: list[Any] = []
    while pos < len(lines):
        cur_indent, content = lines[pos]
        if cur_indent != indent or not content.startswith("- "):
            break
        rest = content[2:]
        match = _KEY_RE.match(rest)
        if match:
            item_indent = indent + 2
            fake = list(lines)
            fake[pos] = (item_indent, rest)
            item, pos = _parse_mapping(fake, pos, item_indent)
        else:
            item = _parse_scalar_token(rest)
            pos += 1
        items.append(item)
    return items, pos


def _needs_quoting(value: str) -> bool:
    """Would pyyaml's safe dumper quote this plain scalar to preserve its string type?"""
    if value == "":
        return True
    if value != value.strip():
        return True
    if value in _BOOL_TRUE or value in _BOOL_FALSE or value in _NULL_WORDS:
        return True
    if _INT_RE.match(value) or (_FLOAT_RE.match(value) and any(c in value for c in ".eE")):
        return True
    if _TIMESTAMP_RE.match(value):
        return True
    if value[0] in "-?:,[]{}#&*!|>'\"%@`":
        return True
    if ": " in value or value.endswith(":") or " #" in value:
        return True
    return False


def _emit_scalar(value: Any) -> str:
    """One token, quoted exactly when `_needs_quoting`/`_needs_double_quote` says pyyaml would."""
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return str(value)
    text = str(value)
    if _needs_double_quote(text):
        return '"' + _escape_double_quoted(text) + '"'
    if _needs_quoting(text):
        return "'" + text.replace("'", "''") + "'"
    return text


def _emit_mapping(mapping: dict, indent: int) -> str:
    pad = " " * indent
    out = []
    for key, value in mapping.items():
        if isinstance(value, dict):
            if not value:
                out.append(f"{pad}{key}: {{}}\n")
            else:
                out.append(f"{pad}{key}:\n{_emit_mapping(value, indent + 2)}")
        elif isinstance(value, list):
            if not value:
                out.append(f"{pad}{key}: []\n")
            else:
                out.append(f"{pad}{key}:\n{_emit_sequence(value, indent)}")
        else:
            out.append(f"{pad}{key}: {_emit_scalar(value)}\n")
    return "".join(out)


def _emit_sequence(seq: list, indent: int) -> str:
    pad = " " * indent
    out = []
    for item in seq:
        if isinstance(item, dict):
            if not item:
                out.append(f"{pad}- {{}}\n")
                continue
            it = iter(item.items())
            first_key, first_value = next(it)
            if isinstance(first_value, dict) and first_value:
                out.append(f"{pad}- {first_key}:\n{_emit_mapping(first_value, indent + 4)}")
            elif isinstance(first_value, dict):
                out.append(f"{pad}- {first_key}: {{}}\n")
            elif isinstance(first_value, list) and first_value:
                out.append(f"{pad}- {first_key}:\n{_emit_sequence(first_value, indent + 2)}")
            elif isinstance(first_value, list):
                out.append(f"{pad}- {first_key}: []\n")
            else:
                out.append(f"{pad}- {first_key}: {_emit_scalar(first_value)}\n")
            rest = dict(it)
            if rest:
                out.append(_emit_mapping(rest, indent + 2))
        else:
            out.append(f"{pad}- {_emit_scalar(item)}\n")
    return "".join(out)


def parse_legacy_yaml_subset(text: str) -> Optional[dict]:
    """Parse legacy YAML without pyyaml, trusting the result only if it round-trips.

    Returns None (never raises) whenever the text falls outside the recognized subset, or
    whenever re-emitting the parsed structure does not reproduce the *folded* source (see
    the module docstring for exactly what that guard does and does not cover) — the caller
    treats both the same way: refuse, rather than risk a silently wrong parse.
    """
    try:
        lines = _logical_lines(text)
        if not lines:
            return None
        parsed, pos = _parse_mapping(lines, 0, lines[0][0])
        if pos != len(lines):
            return None
        reemitted = _emit_mapping(parsed, lines[0][0])
    except (_SubsetParseError, StopIteration, ValueError, IndexError):
        return None
    canonical_source = "\n".join(f"{' ' * i}{c}" for i, c in lines) + "\n"
    if reemitted != canonical_source:
        return None
    return parsed
