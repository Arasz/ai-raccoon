#!/usr/bin/env python3
"""Render an evidence-first research record into a self-contained HTML view.

The markdown record is the artefact; this produces a view of it and refuses to write that view
inside the repository. Stdlib only, no network, inline SVG — see `references/provenance.md`.

Usage:
  python3 render_report.py <record.md> [--out <path.html>]
"""
from __future__ import annotations

import argparse
import html
import re
import sys
import tempfile
from pathlib import Path

# The closed set. A claim that does not name one of these is not a finding, it is an opinion.
GRADES = ("MEASURED", "READ", "INFERRED", "UNVERIFIED")

# Grades whose whole meaning is a citation. UNVERIFIED is exempt: "nobody checked" is an honest
# answer, and demanding a source for it would push writers to downgrade it to silence.
GRADES_NEEDING_EVIDENCE = ("MEASURED", "READ")

CHART_KINDS = ("provenance", "bars", "line", "matrix", "range")

GRADE_FILL = {
    "MEASURED": "#2f855a",
    "READ": "#2b6cb0",
    "INFERRED": "#b7791f",
    "UNVERIFIED": "#9b2c2c",
}

EXAMPLE_CHARTS = {
    "provenance": "",
    "bars": "title: gate seconds\npytest: 49\npylint: 12\nother lanes: 14\n",
    "line": "title: suite size\ncount: 2816,2848,2861,2885\n",
    "matrix": "title: options\n, cost, risk\nrebase, 2, 3\nmerge, 1, 1\n",
    "range": "title: push seconds\ndocs-only: 12..14..19\nfull: 68..75..91\n",
}

_FINDING_RE = re.compile(r"^###\s+(?P<id>\S+)\s+—\s+(?P<claim>.*?)\s*$", re.MULTILINE)
_GRADE_RE = re.compile(r"\[([A-Z]+)\]\s*$")
_CHART_RE = re.compile(r"^```chart:(\w+)\s*$(.*?)^```\s*$", re.MULTILINE | re.DOTALL)


def _section(text: str, heading: str) -> str:
    """The body under a `## heading`, up to the next `## `. Empty string when absent."""
    match = re.search(r"^##\s+" + re.escape(heading) + r"\s*$(.*?)(?=^##\s|\Z)",
                      text, re.MULTILINE | re.DOTALL)
    return match.group(1) if match else ""


def parse_findings(text: str):
    """Every finding in the record, with its grade validated. Raises ValueError on any lapse.

    Refusing beats defaulting: a finding silently graded UNVERIFIED reads as deliberate caution
    when it was actually an omission, and nobody re-opens it.
    """
    body = _section(text, "Findings")
    if not body.strip():
        raise ValueError("the record has no '## Findings' section, or it is empty")

    matches = list(_FINDING_RE.finditer(body))
    findings = []
    for index, match in enumerate(matches):
        start = match.end()
        end = matches[index + 1].start() if index + 1 < len(matches) else len(body)
        findings.append(_one_finding(match.group("id"), match.group("claim"), body[start:end]))
    if not findings:
        raise ValueError("the '## Findings' section holds no '### <id> — <claim> [GRADE]' entries")
    return findings


def _one_finding(finding_id: str, claim: str, body: str) -> dict:
    """Validate and normalise a single finding. Every raise names the finding's id."""
    grade_match = _GRADE_RE.search(claim)
    if grade_match is None:
        raise ValueError(
            f"{finding_id}: no grade. End the claim with one of {', '.join(GRADES)} in brackets"
        )
    grade = grade_match.group(1)
    if grade not in GRADES:
        raise ValueError(f"{finding_id}: {grade} is not a grade. Use one of {', '.join(GRADES)}")
    if grade in GRADES_NEEDING_EVIDENCE and "**Evidence:**" not in body:
        raise ValueError(
            f"{finding_id}: graded {grade} but carries no '**Evidence:**' line. "
            "Cite what was run or read, or downgrade it to INFERRED"
        )
    return {
        "id": finding_id,
        "grade": grade,
        "claim": claim[:grade_match.start()].strip(),
        "body": body.strip(),
    }


def provenance_mix(findings) -> dict:
    """Count per grade, including the grades nobody used — a missing zero reads as unconsidered."""
    mix = dict.fromkeys(GRADES, 0)
    for finding in findings:
        mix[finding["grade"]] += 1
    return mix


def _rows(body: str):
    """`label: value` lines, in order, ignoring a leading `title:` and blank lines."""
    out = []
    for raw in body.splitlines():
        line = raw.strip()
        if not line or line.lower().startswith("title:"):
            continue
        if ":" not in line:
            continue
        label, _, value = line.partition(":")
        out.append((label.strip(), value.strip()))
    return out


def _title(body: str) -> str:
    for raw in body.splitlines():
        if raw.strip().lower().startswith("title:"):
            return raw.split(":", 1)[1].strip()
    return ""


def _svg(width: int, height: int, parts) -> str:
    """Inline SVG, deliberately without `xmlns`.

    HTML5 supplies the SVG namespace for inline content, and omitting it keeps the page free of
    any `http://` string at all — so the "no external host" check stays a plain substring test
    rather than one carrying an exception a later reader would have to trust.
    """
    return (f'<svg viewBox="0 0 {width} {height}" width="100%" role="img">'
            + "".join(parts) + "</svg>")


def _text(x, y, content, cls="lbl") -> str:
    return f'<text x="{x}" y="{y}" class="{cls}">{html.escape(str(content))}</text>'


def _bars(body: str, mix=None) -> str:
    """Horizontal bars. Doubles as the provenance chart when `mix` is supplied."""
    pairs = ([(g, str(n)) for g, n in mix.items()] if mix is not None else _rows(body))
    values = [_number(v) for _, v in pairs]
    top = max(values + [1])
    parts = []
    for index, (label, raw) in enumerate(pairs):
        y = 12 + index * 34
        width = int(300 * _number(raw) / top) if top else 0
        fill = GRADE_FILL.get(label, "#4a5568")
        parts.append(f'<rect x="130" y="{y}" width="{max(width, 1)}" height="20" fill="{fill}"/>')
        parts.append(_text(0, y + 15, label))
        parts.append(_text(140 + max(width, 1), y + 15, raw, "val"))
    return _svg(520, 12 + len(pairs) * 34 + 12, parts)


def _number(raw: str) -> float:
    match = re.search(r"-?\d+(?:\.\d+)?", str(raw))
    return float(match.group(0)) if match else 0.0


def _line(body: str) -> str:
    """One polyline per `label: v1,v2,v3` row."""
    parts = []
    for index, (label, raw) in enumerate(_rows(body)):
        values = [_number(v) for v in raw.split(",") if v.strip()]
        if not values:
            continue
        top, low = max(values), min(values)
        span = (top - low) or 1
        step = 460 / max(len(values) - 1, 1)
        points = " ".join(
            f"{60 + i * step:.1f},{110 - 80 * (v - low) / span:.1f}"
            for i, v in enumerate(values)
        )
        parts.append(f'<polyline points="{points}" fill="none" stroke="#2b6cb0" '
                     f'stroke-width="2"/>')
        parts.append(_text(0, 20 + index * 16, f"{label}: {raw}"))
    return _svg(540, 140, parts)


def _matrix(body: str) -> str:
    """A heatmap: first row is the column header, each later row is `label, v, v`."""
    rows = [r for r in body.splitlines() if r.strip() and not r.strip().lower().startswith("title:")]
    grid = [[cell.strip() for cell in row.split(",")] for row in rows]
    if not grid:
        return _svg(520, 40, [])
    numbers = [_number(c) for row in grid[1:] for c in row[1:]]
    top = max(numbers + [1])
    parts = []
    for column, head in enumerate(grid[0][1:]):
        parts.append(_text(150 + column * 90, 16, head, "val"))
    for r, row in enumerate(grid[1:]):
        parts.append(_text(0, 46 + r * 30, row[0]))
        for c, cell in enumerate(row[1:]):
            shade = 0.15 + 0.75 * (_number(cell) / top if top else 0)
            parts.append(f'<rect x="{140 + c * 90}" y="{30 + r * 30}" width="80" height="22" '
                         f'fill="#2b6cb0" fill-opacity="{shade:.2f}"/>')
            parts.append(_text(175 + c * 90, 46 + r * 30, cell, "val"))
    return _svg(540, 40 + len(grid) * 30, parts)


def _range(body: str) -> str:
    """`label: low..measured..high` — the interval a measurement actually sat in."""
    pairs = _rows(body)
    triples = []
    for label, raw in pairs:
        values = [_number(v) for v in raw.split("..") if v.strip()]
        if len(values) == 3:
            triples.append((label, values))
    top = max([v for _, vals in triples for v in vals] + [1])
    parts = []
    for index, (label, (low, mid, high)) in enumerate(triples):
        y = 16 + index * 34
        x1, x2 = 130 + 360 * low / top, 130 + 360 * high / top
        parts.append(f'<line x1="{x1:.1f}" y1="{y}" x2="{x2:.1f}" y2="{y}" stroke="#4a5568" '
                     f'stroke-width="3"/>')
        parts.append(f'<circle cx="{130 + 360 * mid / top:.1f}" cy="{y}" r="5" fill="#2f855a"/>')
        parts.append(_text(0, y + 4, label))
        parts.append(_text(500, y + 4, f"{mid:g}", "val"))
    return _svg(560, 16 + len(triples) * 34 + 16, parts)


def render_chart(kind: str, body: str, mix=None) -> str:
    """One chart as inline SVG. An unknown kind is refused rather than silently skipped."""
    if kind not in CHART_KINDS:
        raise ValueError(f"{kind} is not a chart kind. Use one of {', '.join(CHART_KINDS)}")
    if kind == "provenance":
        return _bars(body, mix if mix is not None else dict.fromkeys(GRADES, 0))
    if kind == "bars":
        return _bars(body)
    if kind == "line":
        return _line(body)
    if kind == "matrix":
        return _matrix(body)
    return _range(body)


_CSS = """
:root{color-scheme:light dark}
body{font:15px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI",system-ui,sans-serif;
max-width:52rem;margin:0 auto;padding:2rem 1.25rem;background:#fff;color:#1a202c}
h1{font-size:1.6rem;line-height:1.3;margin:0 0 .25rem}
h2{font-size:1.15rem;margin:2.25rem 0 .75rem;border-bottom:1px solid #e2e8f0;padding-bottom:.3rem}
.q{color:#4a5568;margin:0 0 1.5rem}
.f{border-left:3px solid #cbd5e0;padding:.1rem 0 .1rem 1rem;margin:1.25rem 0}
.f h3{font-size:1rem;margin:.2rem 0 .4rem;font-weight:600}
.g{display:inline-block;font-size:.7rem;letter-spacing:.06em;font-weight:700;color:#fff;
border-radius:3px;padding:.1rem .4rem;margin-right:.5rem;vertical-align:.08rem}
.MEASURED{background:#2f855a}.READ{background:#2b6cb0}
.INFERRED{background:#b7791f}.UNVERIFIED{background:#9b2c2c}
.f.MEASURED{border-left-color:#2f855a}.f.READ{border-left-color:#2b6cb0}
.f.INFERRED{border-left-color:#b7791f}.f.UNVERIFIED{border-left-color:#9b2c2c}
svg{margin:.5rem 0 1rem;overflow:visible}
text{font:12px sans-serif;fill:#2d3748}text.val{font-size:11px;fill:#4a5568}
figure{margin:1rem 0;overflow-x:auto}figcaption{font-size:.8rem;color:#718096}
code{background:#edf2f7;padding:.1rem .3rem;border-radius:3px;font-size:.88em}
@media (prefers-color-scheme:dark){
body{background:#1a202c;color:#e2e8f0}h2{border-bottom-color:#2d3748}
.q{color:#a0aec0}text{fill:#e2e8f0}text.val{fill:#a0aec0}code{background:#2d3748}
.f{border-left-color:#4a5568}}
"""


def _inline(text: str) -> str:
    """Escape, then restore the two inline marks a record actually uses."""
    out = html.escape(text)
    out = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", out)
    return re.sub(r"`([^`]+)`", r"<code>\1</code>", out)


def _paragraphs(body: str) -> str:
    chunks = [c.strip() for c in re.split(r"\n\s*\n", body) if c.strip()]
    return "".join(f"<p>{_inline(c)}</p>" for c in chunks)


def render(record: str) -> str:
    """The whole record as one self-contained page: no fetches, no scripts, inline SVG."""
    findings = parse_findings(record)
    mix = provenance_mix(findings)

    title_match = re.search(r"^#\s+(.*)$", record, re.MULTILINE)
    title = title_match.group(1).strip() if title_match else "Research record"
    question = re.search(r"^\*\*Question:\*\*\s*(.*)$", record, re.MULTILINE)

    parts = [
        "<!doctype html><html><head><meta charset=\"utf-8\">",
        '<meta name="viewport" content="width=device-width,initial-scale=1">',
        f"<title>{html.escape(title)}</title><style>{_CSS}</style></head><body>",
        f"<h1>{html.escape(title)}</h1>",
    ]
    if question:
        parts.append(f'<p class="q">{_inline(question.group(1))}</p>')

    parts.append("<h2>How this is known</h2><figure>")
    parts.append(render_chart("provenance", "", mix))
    parts.append(f"<figcaption>{sum(mix.values())} findings by grade. "
                 "MEASURED and READ carry a citation; INFERRED is reasoning; "
                 "UNVERIFIED is an admission.</figcaption></figure>")

    for kind, body in _CHART_RE.findall(record):
        caption = _title(body)
        parts.append("<figure>" + render_chart(kind, body))
        parts.append((f"<figcaption>{html.escape(caption)}</figcaption>" if caption else "")
                     + "</figure>")

    parts.append("<h2>Findings</h2>")
    for finding in findings:
        grade = finding["grade"]
        body = _CHART_RE.sub("", finding["body"])
        parts.append(f'<section class="f {grade}"><h3><span class="g {grade}">{grade}</span>'
                     f'{html.escape(finding["id"])} — {_inline(finding["claim"])}</h3>'
                     f'{_paragraphs(body)}</section>')

    still_open = _section(record, "Still open")
    if still_open.strip():
        parts.append("<h2>Still open</h2><ul>")
        for line in still_open.splitlines():
            item = line.strip()
            if item.startswith(("- ", "* ")):
                parts.append(f"<li>{_inline(item[2:])}</li>")
        parts.append("</ul>")

    parts.append("</body></html>")
    return "".join(parts)


def output_path(repo_root, slug: str) -> Path:
    """Where the view goes: a temp directory, never the repository.

    The record is the artefact and lives in the repo. A rendered page committed beside it becomes
    a second source of truth, and within a month nobody can say which one is current.
    """
    _ = repo_root
    return Path(tempfile.gettempdir()) / "ai-badger-research" / f"{slug}.html"


def _is_inside(target: Path, root: Path) -> bool:
    """3.8-safe containment test; `Path.is_relative_to` is 3.9+."""
    try:
        target.resolve().relative_to(root.resolve())
        return True
    except ValueError:
        return False


def write_report(record: str, target, repo_root) -> Path:
    """Render and write, refusing any target inside the repository."""
    target = Path(target)
    if _is_inside(target, Path(repo_root)):
        raise ValueError(
            f"refusing to write the view inside the repository ({target}). The markdown record is "
            "the artefact; a committed HTML view becomes a second source of truth"
        )
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(render(record), encoding="utf-8")
    return target


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("record", help="the markdown research record")
    parser.add_argument("--out", default="", help="where to write the HTML view")
    parser.add_argument("--repo-root", default=".", help="tree the view may not be written into")
    args = parser.parse_args(argv)

    record_path = Path(args.record)
    text = record_path.read_text(encoding="utf-8")
    target = Path(args.out) if args.out else output_path(args.repo_root, record_path.stem)
    try:
        written = write_report(text, target, args.repo_root)
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    print(written)
    return 0


if __name__ == "__main__":
    sys.exit(main())
