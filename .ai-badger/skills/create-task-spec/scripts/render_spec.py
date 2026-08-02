#!/usr/bin/env python3
"""Render a Gherkin spec as a self-contained page that shows its gaps as well as its content.

Shares `spec_holes.scan` so the page and the completeness gate can never disagree. Stdlib only.
"""
from __future__ import annotations

import argparse
import html
import json
import os
import sys
from typing import List, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import spec_holes  # noqa: E402  pylint: disable=wrong-import-position

FEATURE_KEYWORDS = spec_holes.FEATURE_KEYWORDS
RULE_KEYWORDS = spec_holes.RULE_KEYWORDS
EXAMPLE_KEYWORDS = spec_holes.EXAMPLE_KEYWORDS
BACKGROUND_KEYWORDS = spec_holes.BACKGROUND_KEYWORDS
STEP_KEYWORDS = spec_holes.STEP_KEYWORDS

STYLES = """
:root {
  --paper: #f4f5f7; --surface: #fff; --ink: #16181f; --ink-2: #4b5162; --ink-3: #737a8c;
  --line: #d6d9e2; --accent: #33478f; --accent-soft: #e6e9f5;
  --open: #a32f2f; --open-soft: #f8e6e6; --held: #8a5a0b; --held-soft: #f7eedd;
  --mono: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  --sans: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial, sans-serif;
}
@media (prefers-color-scheme: dark) {
  :root {
    --paper: #101219; --surface: #171a23; --ink: #e8eaf1; --ink-2: #b2b8c7; --ink-3: #858c9e;
    --line: #2c313e; --accent: #94a5e9; --accent-soft: #1e2441;
    --open: #e58686; --open-soft: #2f1a1a; --held: #d6a45a; --held-soft: #2c2415;
  }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--paper); color: var(--ink); font-family: var(--sans);
       line-height: 1.6; }
.wrap { max-width: 46rem; margin: 0 auto; padding: 2.5rem 1.25rem 5rem;
        display: flex; flex-direction: column; gap: 1.75rem; }
h1 { font-size: 1.7rem; line-height: 1.2; margin: 0; }
.story { color: var(--ink-2); font-size: 1rem; white-space: pre-line; margin: 0; }
.summary { display: flex; flex-wrap: wrap; gap: .5rem; }
.pill { font-family: var(--mono); font-size: .7rem; letter-spacing: .08em;
        text-transform: uppercase; padding: .25rem .55rem; border: 1px solid currentColor;
        border-radius: 2px; }
.pill.ok { color: #1f7a4c; }
.pill.open { color: var(--open); background: var(--open-soft); }
.pill.held { color: var(--held); background: var(--held-soft); }
.rule { border: 1px solid var(--line); background: var(--surface); }
.rule > h2 { margin: 0; padding: .75rem 1rem; font-size: 1rem; border-bottom: 1px solid var(--line); }
.example { padding: .75rem 1rem; border-bottom: 1px solid var(--line); }
.example:last-child { border-bottom: 0; }
.example > h3 { margin: 0 0 .4rem; font-size: .93rem; font-weight: 600; }
.steps { margin: 0; padding: 0; list-style: none; font-family: var(--mono); font-size: .8rem;
         display: flex; flex-direction: column; gap: .15rem; color: var(--ink-2); }
.kw { color: var(--accent); font-weight: 600; }
.hole { border-left: 3px solid var(--open); background: var(--open-soft); }
.hole.is-deferred { border-left-color: var(--held); background: var(--held-soft); }
.hole-note { font-family: var(--mono); font-size: .7rem; letter-spacing: .06em;
             text-transform: uppercase; color: var(--ink-3); }
.manifest { border: 1px solid var(--line); background: var(--surface); padding: 1rem; }
.manifest h2 { margin: 0 0 .5rem; font-size: 1rem; }
.manifest dt { font-family: var(--mono); font-size: .7rem; letter-spacing: .06em;
               text-transform: uppercase; color: var(--ink-3); margin-top: .6rem; }
.manifest dd { margin: .15rem 0 0; color: var(--ink-2); font-size: .9rem; }
footer { color: var(--ink-3); font-size: .8rem; border-top: 1px solid var(--line);
         padding-top: 1rem; }
"""


def _esc(text: str) -> str:
    return html.escape(text, quote=True)


def _keyword(line: str, keywords):
    for keyword in keywords:
        if line.startswith(keyword):
            return keyword
    return ""


class _Node:
    """A rule or an example, with whatever steps were elicited for it."""

    def __init__(self, kind: str, title: str, line: int) -> None:
        self.kind = kind
        self.title = title
        self.line = line
        self.steps: List[str] = []
        self.examples: List["_Node"] = []
        self.deferred = False


def _parse(text: str):
    """Return (feature title, story lines, top-level nodes) — presentation shape, not a full AST."""
    title = ""
    story: List[str] = []
    nodes: List[_Node] = []
    current_rule = None
    current_example = None
    in_doc_string = False
    delimiter = ""
    pending_deferral = False
    seen_feature = False

    for number, raw in enumerate(text.splitlines(), start=1):
        stripped = raw.strip()

        if in_doc_string:
            if stripped.startswith(delimiter):
                in_doc_string = False
            continue
        opener = _keyword(stripped, spec_holes.DOC_STRING_DELIMITERS)
        if opener:
            in_doc_string, delimiter = True, opener
            continue

        if not stripped or stripped.startswith("#"):
            continue
        if stripped.startswith("@"):
            pending_deferral = spec_holes.DEFERRED_TAG in stripped.split()
            continue

        keyword = _keyword(stripped, FEATURE_KEYWORDS)
        if keyword:
            title = stripped[len(keyword):].strip()
            seen_feature = True
            current_rule = current_example = None
            pending_deferral = False
            continue

        keyword = _keyword(stripped, RULE_KEYWORDS)
        if keyword:
            current_rule = _Node("rule", stripped[len(keyword):].strip(), number)
            current_rule.deferred = pending_deferral
            nodes.append(current_rule)
            current_example = None
            pending_deferral = False
            continue

        keyword = _keyword(stripped, EXAMPLE_KEYWORDS)
        if keyword:
            current_example = _Node("example", stripped[len(keyword):].strip(), number)
            current_example.deferred = pending_deferral
            if current_rule is not None:
                current_rule.examples.append(current_example)
            else:
                nodes.append(current_example)
            pending_deferral = False
            continue

        keyword = _keyword(stripped, BACKGROUND_KEYWORDS)
        if keyword:
            current_example = None
            pending_deferral = False
            continue

        if _keyword(stripped, STEP_KEYWORDS):
            if current_example is not None:
                current_example.steps.append(stripped)
            continue

        if seen_feature and current_rule is None and current_example is None:
            story.append(stripped)

    return title, story, nodes


def _render_steps(node: _Node) -> str:
    if not node.steps:
        return ""
    items = []
    for step in node.steps:
        head, _, rest = step.partition(" ")
        items.append(f'<li><span class="kw">{_esc(head)}</span> {_esc(rest)}</li>')
    return '<ul class="steps">' + "".join(items) + "</ul>"


def _render_example(node: _Node) -> str:
    classes = ["example"]
    note = ""
    if not node.steps:
        classes.append("hole")
        if node.deferred:
            classes.append("is-deferred")
        state = "deferred" if node.deferred else "open"
        note = (f'<p class="hole-note">example-without-steps — {state}: '
                "no steps elicited yet</p>")
    return (f'<div class="{" ".join(classes)}"><h3>{_esc(node.title)}</h3>'
            f"{note}{_render_steps(node)}</div>")


def _render_rule(node: _Node) -> str:
    classes = ["rule"]
    note = ""
    if not node.examples:
        classes.append("hole")
        if node.deferred:
            classes.append("is-deferred")
        state = "deferred" if node.deferred else "open"
        note = (f'<div class="example"><p class="hole-note">rule-without-example — {state}: '
                "no example illustrates this rule</p></div>")
    body = note + "".join(_render_example(child) for child in node.examples)
    return (f'<section class="{" ".join(classes)}"><h2>Rule: {_esc(node.title)}</h2>'
            f"{body}</section>")


def _render_manifest(manifest: Optional[dict]) -> str:
    if not manifest:
        return ""
    rows = []
    for key, value in manifest.items():
        if isinstance(value, (list, tuple)):
            shown = ", ".join(str(item) for item in value)
        elif isinstance(value, dict):
            shown = json.dumps(value)
        else:
            shown = str(value)
        rows.append(f"<dt>{_esc(str(key))}</dt><dd>{_esc(shown)}</dd>")
    return '<section class="manifest"><h2>Manifest</h2><dl>' + "".join(rows) + "</dl></section>"


def render(text: str, manifest: Optional[dict] = None) -> str:
    """Return a self-contained HTML page for `text`, marking every outstanding question."""
    title, story, nodes = _parse(text)
    holes = spec_holes.scan(text)
    open_holes = [hole for hole in holes if not hole.deferred]
    held = [hole for hole in holes if hole.deferred]

    pills = []
    if not holes:
        pills.append('<span class="pill ok">complete</span>')
    if open_holes:
        pills.append(f'<span class="pill open">{len(open_holes)} open</span>')
    if held:
        pills.append(f'<span class="pill held">{len(held)} deferred</span>')

    body = "".join(_render_rule(node) if node.kind == "rule" else _render_example(node)
                   for node in nodes)

    return (
        "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n"
        '<meta name="viewport" content="width=device-width, initial-scale=1">\n'
        f"<title>{_esc(title) or 'Specification'}</title>\n<style>{STYLES}</style>\n"
        "</head>\n<body>\n<div class=\"wrap\">\n"
        f"<header><h1>{_esc(title)}</h1>"
        f'<p class="story">{_esc(chr(10).join(story))}</p></header>\n'
        f'<div class="summary">{"".join(pills)}</div>\n'
        f"{_render_manifest(manifest)}\n{body}\n"
        "<footer>Generated by create-task-spec. Holes are questions still to elicit; "
        "a deferred hole carries an owner ruling.</footer>\n"
        "</div>\n</body>\n</html>\n"
    )


def _main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("spec", help="path to a .feature file")
    parser.add_argument("--manifest", help="path to the spec.json manifest")
    parser.add_argument("--out", help="write here instead of stdout")
    args = parser.parse_args(argv)

    with open(args.spec, encoding="utf-8") as handle:
        text = handle.read()

    manifest = None
    if args.manifest:
        with open(args.manifest, encoding="utf-8") as handle:
            manifest = json.load(handle)

    page = render(text, manifest)
    if not args.out:
        print(page)
        return 0

    with open(args.out, "w", encoding="utf-8") as handle:
        handle.write(page)
    print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(_main())
