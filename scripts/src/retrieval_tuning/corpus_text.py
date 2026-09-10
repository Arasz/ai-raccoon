"""Markup-aware topic repair for the project-corpus generator (Package B).

`repair_topic` turns a raw chunk value into a markup-free prose topic. It is a
repair function (strip markup, pick the first clean sentence span), never a
classifier: it must not import or reference the report's debris predicate —
T7 fails the suite if it ever does, so the headline debris count stays an
independent measurement.
"""

from __future__ import annotations

import re

TOPIC_MAX_CHARS = 100
TOPIC_DERIVATION = "markup-aware-v1"

_MD_LINK = re.compile(r"!?\[([^\]]*)\]\([^)]*\)")
_MD_REF = re.compile(r"\[([^\]]*)\]\[[^\]]*\]")
_HTML_TAG = re.compile(r"</?[A-Za-z][^>]*>")
_HTML_ENT = re.compile(r"&[a-z]+;|&#\d+;")
_FENCE = re.compile(r"^\s*(```|~~~)")
_HR = re.compile(r"^\s*([-*_]\s*){3,}$")
_HEAD = re.compile(r"^\s{0,3}#{1,6}\s+")
_QUOTE = re.compile(r"^\s{0,3}>\s?")
_BULLET = re.compile(r"^\s{0,6}(?:[-*+]|\d{1,3}[.)])\s+")
_TABLE_SEP = re.compile(r"^\s*\|?[\s:|-]+\|[\s:|-]*$")
_TABLE_ROW = re.compile(r"^\s*\|.*\|\s*$")
_MERMAID = re.compile(
    r"^\s*(graph|flowchart|sequenceDiagram|classDiagram|stateDiagram|erDiagram|gantt|pie|mindmap)\b"
)
_ODD = re.compile(r"[‘’“”]")
_MARKUP_CHARS = re.compile(r"[{}\[\]<>\\`|~^*_]")
_EMPH = re.compile(r"(\*\*|__|\*|_)(?=\S)(.*?)(?<=\S)\1")
_SCOPED_AT = re.compile(r"@([a-z0-9-]+/)")


def _strip_lines(value: str) -> list[str]:
    """One cleaned line per raw line: fenced/table/quote/heading/list/mermaid
    markers dropped, table rows collapsed to their richest cell, Markdown
    links/images/refs unwrapped, HTML tags/entities stripped, emphasis
    stripped."""
    out: list[str] = []
    for raw in value.split("\n"):
        line = raw.rstrip()
        if _FENCE.match(line):
            continue
        if _HR.match(line):
            continue
        if _TABLE_SEP.match(line):
            continue
        if _MERMAID.match(line):
            continue
        line = _HEAD.sub("", line)
        line = _QUOTE.sub("", line)
        if _TABLE_ROW.match(line):
            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            cells = sorted(cells, key=len, reverse=True)
            if not cells:
                continue
            line = cells[0]
            if len(cells) > 1 and re.fullmatch(r"[\d.,%\-–— ]*", line):
                line = cells[1]
        line = _BULLET.sub("", line)
        line = _MD_LINK.sub(r"\1", line)
        line = _MD_REF.sub(r"\1", line)
        line = _HTML_TAG.sub("", line)
        line = _HTML_ENT.sub(" ", line)
        line = line.replace("`", "")
        line = _EMPH.sub(r"\2", line)
        out.append(line.strip())
    return out


def _keyword_span(text: str, max_words: int = 12) -> str:
    """Token summary for text with no clean prose span: markup chars dropped,
    words kept in order."""
    words = re.findall(r"[A-Za-z0-9][A-Za-z0-9.'\-]*", text)
    words = [w for w in words if not re.fullmatch(r"[\d.]+", w)]
    if not words:
        words = re.findall(r"[A-Za-z0-9][A-Za-z0-9.'\-]*", text)
    return " ".join(words[:max_words])


def repair_topic(value: str, *, fallback: str | None = None) -> str:
    """First clean sentence span of the chunk text (≥3 word tokens, no markup
    chars), capped at TOPIC_MAX_CHARS on a word boundary; keyword-span, then
    `fallback`, then "this note". Never empty."""
    if not value:
        return fallback or "this note"
    value = _SCOPED_AT.sub(r"\1", value)
    lines = [line for line in _strip_lines(value) if line]
    joined = re.sub(r"\s+", " ", " ".join(lines)).strip()
    if not joined:
        return fallback or "this note"
    spans = [s.strip() for s in re.split(r"(?<=[.!?])\s+", joined) if s.strip()]
    for span in spans:
        span = _ODD.sub("", span)
        words = re.findall(r"[^\W\d_]+", span, re.UNICODE)
        if len(words) >= 3 and not _MARKUP_CHARS.search(span):
            topic = span if len(span) <= TOPIC_MAX_CHARS else (
                span[:TOPIC_MAX_CHARS].rsplit(" ", 1)[0]
            )
            topic = topic.strip().rstrip(".,;:!?'\"`*)]—–-")
            if topic and not re.search(r"https?://", topic):
                return topic
    keyword = _MARKUP_CHARS.sub(" ", _keyword_span(joined))
    keyword = re.sub(r"\s+", " ", keyword).strip()[:TOPIC_MAX_CHARS].rsplit(" ", 1)[0].strip()
    if keyword and len(keyword) >= 8:
        return keyword
    if fallback:
        return fallback
    return joined[:TOPIC_MAX_CHARS].strip() or "this note"
