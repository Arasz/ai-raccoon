#!/usr/bin/env python3
"""Generate rules.json and the two ruleset walks from references/*.md; --check verifies.

Markdown is the source of truth (see `governance.md` "`rules.json` — the one condition it
exists under"). Every rule in
`references/{principles,universal,kind-*,stack-*}.md` is a heading — one of

    #### `<id>` — <title>            (stack-dotnet.md's convention)
    **`<id>` — <title>**             (universal.md / kind-*.md's convention)
    **`<id>`** — <title>             (principles.md's convention — bold wraps only the id)

followed by a fixed bullet block of `- key: value` lines (asterisk/backtick decoration around
the key is tolerated and stripped; several `key: value` pairs may share one line separated by
`·`, e.g. `- **severity:** blocker · **evidence:** strong · **flag:** argued`). Recognised keys:
design, review, check, severity, evidence, flag, parent, absorbs, cites, archetypes, invariant,
retired, rationale, and the single machine-read line

    - *meta:* pass=<0-8> order=<int> phase=<1-8 or review-only>

(universal.md's own header names this "the machine-read line": `pass`/`order` place a rule in
`walk-review.md`, `phase` places it in `walk-design.md`). Only L1 rules carry `phase` — the
design walk is populated from `universal.md`'s L1 rules only, never a kind/stack specialisation.
`parent`/`invariant` are single ids, or — rarely, on a stack rule that specialises two L1/L2
rules at once — a comma-separated list; every named id is checked individually. A key may also
join two or three of `design`/`review`/`check` with a slash (`- *design/review/check:* ...`,
`stack-terraform.md`'s "already stated in full elsewhere, cited not restated" shorthand) — the
one value is applied to every named field. L0 principles (`T0-nn`, no group segment) carry only
`title` and, optionally, `cites` — a principle is invariant-style, not rule-style, and has no
`check`. They are parsed into rules.json for lookup
but never placed in a walk. An unrecognised key, or a `*meta:*` value that is not the integer it
claims to be, is a `--check` violation (`[UNKNOWN-FIELD]` / `[BAD-META]`, F8) rather than a
silent drop.

This script emits three committed artefacts, in `references/` unless noted:
  - `../rules.json`     — one record per rule: id, layer, group, title, severity, evidence,
                          flag, parent, absorbs[], archetypes[], pass, order, phase, retired,
                          file, line. `retired` carries the tombstone date/reason verbatim (or
                          `null` for a live rule) so a consumer can tell the two apart without
                          parsing `title` — no prose beyond that one field, ever: the moment a
                          rationale is written into it, it is a second source of truth and must
                          be deleted (`governance.md` "`rules.json` — the one condition it
                          exists under").
  - `walk-review.md`    — passes 0-8, rules ordered by (pass, order); a rule whose `parent:`
                          shares its parent's pass renders indented under it.
  - `walk-design.md`    — steps 1-8 from `phase:`, omitting `phase: review-only`.

`--check` regenerates all three in memory and fails when a committed copy differs (drift), and
additionally fails on:
  1. a live (non-retired) L1 rule count above the cap of 40
  2. a L2/L3 rule (`T2-*`/`T3-*`) with no `parent:`
  3. a `parent:` naming an id that does not exist anywhere in the parsed set
  4. an `absorbs:` id that does not match the source-lane id grammar (below) and is not a known
     rule id — derived from what `references/*.md` actually contains, never a hand-maintained
     list (`derive-or-delete-the-list`; wave-1 review F4 deleted `lane-id-manifest.txt`, whose
     323 entries had drifted 31% out of date with nothing to notice)
  5. a `cites:` naming a repo file (backtick, `*.md`) or `invariant \\`<slug>\\`` whose
     `.ai-badger/invariants/<slug>.md` does not resolve directly and whose full relative path
     (not merely its basename — F14) is not found under one of a fixed set of citation roots
     (see `_CITATION_FALLBACK_ROOTS`), never a repo-wide scan
  6. a `weak`-evidence rule carrying `severity: blocker` or `flag: auto` (severity is parsed to
     its *ceiling* enum word first — F7 — so `major; **blocker** where ...` still compares)
  7. a file directly under `references/` whose name does not match `^[a-z][a-z-]*\\.md$`
  8. an `archetypes.md` table row naming a rule id (column "Rule demanding the guard") that does
     not resolve — `[BAD-ARCHETYPE]` (F2); the same parse also inverts the map to populate each
     resolving rule's `archetypes[]` in rules.json, which is the field's one stated consumer
  9. a rule missing a field its layer requires, or carrying an unrecognised key — F8:
     `[MISSING-FIELD]` / `[UNKNOWN-FIELD]`. L0 needs only id/title; L1 needs severity, evidence,
     flag, check, pass, order; L2/L3 need severity, evidence, flag, check, parent.
  10. a `check:` that is blank, or collapsed to one of `flag:`'s own words (`argued`, `auto`,
      `auto-unless-listed`) with no falsifier attached — `[EMPTY-CHECK]` (F5). The one legal
      flag-shaped value, `retired — not applicable.`, is allowed only on a rule that itself
      carries `retired:`.
  and on a parsed-rule count of zero (an index built from a moved/renamed directory must not
  report a clean, empty pass — see invariant `prove-the-check-fails`).

Absorbs id grammar (class 4), derived from `grep -ho 'absorbs:.*' references/*.md` over the real
tree, not invented: `P[1-8]`; `U-[A-Z]{3}-nn`; `K-[A-Z]{3,4}-nn`; `N-[A-Z]{2,4}-nn`;
`F-[A-Z0-9]{3,4}-nn` (the digit-tolerant form covers `F-E2E-nn` — a source-lane id predating
this ruleset's own digit-free `kind-end-to-end.md`/`T2-ETE-*` renaming, kept verbatim as
history); a legacy short id (`[URPTBDS]<1-2 digits>`, optionally prefixed literal `L2 `); or
an id already known to the parsed set (a `T…` rule id). Matched at the *start* of the token —
trailing prose the source lanes attached (`F-E2E-05's general form`, `N-MUT-02 parent`) is
tolerated, not required to be absent.

Per `feedback-regenerate-dont-fail.md`, the two modes differ only in what happens once the
structural checks pass: no-flag mode writes the regenerated artefacts unconditionally (fit for a
pre-commit hook); `--check` mode never writes, and additionally diffs the in-memory output
against the three committed files (fit for CI, and for the pytest wiring in
`tests/test_test_ruleset_index.py` — F1). A structural violation blocks writing in *either*
mode — regeneration cannot repair an authoring error, only drift.

Usage: rules_index.py [--root <skill-dir>] [--check]
  --root  : the review-tests skill directory (default: this script's own skill directory,
            i.e. the parent of scripts/). Tests point it at a fixture skill directory.
  --check : do not write; exit 1 on any structural violation or on drift, exit 0 when clean.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional

GENERATED_BANNER = (
    "<!-- generated by scripts/rules_index.py — do not edit; "
    "edit the rule's meta: line instead -->"
)

# T0-01 (no group) | T1-SCO-01 / T2-WID-01 / T3-NET-01 (layer-group-nn) | TX-EXC-01 (L4 project-local)
ID_RE = r"T(?:0-\d{2}|[1-4]-[A-Z0-9]+-\d{2}|X-[A-Z0-9]+-\d{2})"
# The `\*{0,2}` right after the id absorbs principles.md's "bold wraps only the id" shape
# (`**`T0-01`** — title`) as well as the plain ATX and "bold wraps the whole heading" shapes.
HEADING_RE = re.compile(
    rf"^(?:#{{2,4}}\s+|\*\*)`?(?P<id>{ID_RE})`?\*{{0,2}}\s*[—–-]\s*(?P<title>.+?)\*{{0,2}}\s*$"
)
FIELD_SEG_RE = re.compile(r"^\*{0,2}([A-Za-z/]+):\*{0,2}\s*(.*)$")
FILENAME_RE = re.compile(r"^[a-z][a-z-]*\.md$")
INVARIANT_CITE_RE = re.compile(r"invariant\s+`([a-z0-9-]+)`")
FILE_CITE_RE = re.compile(r"`([\w./-]+\.md)`")
ARCHETYPE_ROW_RE = re.compile(r"^\|\s*(A\d{2})\s*\|")
RULE_ID_LIKE_RE = re.compile(r"^T[0-9X]")

# F4: the source-lane id grammar, derived from grep -ho 'absorbs:.*' references/*.md — never a
# hand-maintained list. See the module docstring's "Absorbs id grammar" paragraph for per-branch
# provenance.
_ABSORBS_ID_ALTERNATION = "|".join((
    r"P[1-8]",
    r"U-[A-Z]{3}-\d{2}",
    r"K-[A-Z]{3,4}-\d{2}",
    r"N-[A-Z]{2,4}-\d{2}",
    r"F-[A-Z0-9]{3,4}-\d{2}",
    r"(?:L2 )?[URPTBDS]\d{1,2}",
    ID_RE,
))
ABSORBS_SHAPE_RE = re.compile(rf"^(?:{_ABSORBS_ID_ALTERNATION})\b")

GENERATED_WALK_FILES = {"walk-review.md", "walk-design.md"}
L1_CAP = 40
PASSES = list(range(9))          # 0..8
DESIGN_STEPS = list(range(1, 9))  # 1..8

SEVERITY_RANK = {"minor": 1, "major": 2, "blocker": 3}
SEVERITY_TOKEN_RE = re.compile(r"\b(blocker|major|minor)\b", re.IGNORECASE)

# F8: the schema. `meta` is handled separately (it explodes into pass_/order/phase, not a
# single attribute); everything else here is one Rule attribute of the same name.
KNOWN_FIELD_KEYS = {
    "design", "review", "check", "severity", "evidence", "flag", "parent",
    "absorbs", "cites", "archetypes", "invariant", "retired", "rationale",
}
# F8: fields required per layer, beyond id/title (always present — they come from the heading
# itself, never from a bullet line). L0 principles require nothing further.
_L1_REQUIRED = ("severity", "evidence", "flag", "check", "pass_", "order")
_L23_REQUIRED = ("severity", "evidence", "flag", "check", "parent")
_REQUIRED_DISPLAY = {"pass_": "pass"}


@dataclass
class Rule:
    """One parsed rule record. `pass_`/`order`/`phase` place the rule in a generated walk
    (`walk-review.md`/`walk-design.md`); they are absent (None) for L0 principles."""

    id: str
    title: str
    file: str
    line: int
    severity: Optional[str] = None
    evidence: Optional[str] = None
    flag: Optional[str] = None
    design: Optional[str] = None
    review: Optional[str] = None
    check: Optional[str] = None
    parent: Optional[str] = None
    absorbs: List[str] = field(default_factory=list)
    cites: Optional[str] = None
    archetypes: List[str] = field(default_factory=list)
    invariant: Optional[str] = None
    retired: Optional[str] = None
    rationale: Optional[str] = None
    pass_: Optional[int] = None
    order: Optional[int] = None
    phase: Optional[str] = None  # "1".."8" or "review-only"

    @property
    def layer(self):
        parts = self.id.split("-")
        return parts[0][1:]  # "0", "1", "2", "3", "4", "X"

    @property
    def group(self):
        parts = self.id.split("-")
        return parts[1] if len(parts) == 3 else None

    @property
    def is_retired(self) -> bool:
        return bool(self.retired)


# ---------------------------------------------------------------------------
# Parsing — line-oriented, no third-party deps (ADR-0005: no pyyaml).
# ---------------------------------------------------------------------------

def _split_field_line(line: str) -> List[tuple]:
    """A `- key: value[ · key: value ...]` bullet line to a list of (key, value) pairs."""
    body = line.strip()
    if not body.startswith("-"):
        return []
    body = body[1:].strip()
    segments = [s.strip() for s in body.split("·") if s.strip()] or [body]
    pairs = []
    for seg in segments:
        m = FIELD_SEG_RE.match(seg)
        if not m:
            continue
        key = m.group(1).lower()
        val = m.group(2).strip().strip("*").strip()
        pairs.append((key, val))
    return pairs


def _split_respecting_parens(text: str) -> List[str]:
    """Top-level-comma split: a comma inside `(...)` does not start a new item.

    An absorbs/archetypes list item may itself carry a parenthetical aside containing a comma
    (`L2 \\`U15\\` (a test's own timeout throws a distinguishable type, never the assertion's)`)
    — a plain `str.split(",")` shreds that aside into two bogus items. F4.
    """
    parts = []
    depth = 0
    start = 0
    for i, ch in enumerate(text):
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth = max(0, depth - 1)
        elif ch == "," and depth == 0:
            parts.append(text[start:i])
            start = i + 1
    parts.append(text[start:])
    return parts


def _strip_backticks(text: str) -> str:
    return text.replace("`", "").strip()


def _normalize_severity(raw: str) -> Optional[str]:
    """F7: `severity:` is stored as its *ceiling* enum word (`blocker` > `major` > `minor`),
    stripped of conditional prose — `major; **blocker** where the bound guards a safety
    property` compares equal to `blocker`, not to the whole sentence. A value with none of the
    three words (should not occur, but --check must not crash on it) passes through unchanged."""
    if not raw:
        return raw
    tokens = SEVERITY_TOKEN_RE.findall(raw.lower())
    if not tokens:
        return raw.strip()
    return max(tokens, key=lambda t: SEVERITY_RANK[t])


def _apply_meta(rule: Rule, val: str, lineno: int) -> List[str]:
    """`- *meta:* pass=3 order=14 phase=7` — the machine-read line every kind/stack lane uses
    (universal.md's own header names it that). Space-separated `key=value` tokens; any subset.
    A non-integer `pass=`/`order=` is `[BAD-META]` (F8) rather than an uncaught ValueError."""
    out: List[str] = []
    for token in val.split():
        if "=" not in token:
            continue
        k, v = token.split("=", 1)
        k, v = k.strip().lower(), v.strip()
        if k in ("pass", "order"):
            try:
                parsed = int(v) if v else None
            except ValueError:
                out.append(f"[BAD-META] {rule.id} line {lineno}: {k}={v!r} is not an integer")
                continue
            if k == "pass":
                rule.pass_ = parsed
            else:
                rule.order = parsed
        elif k == "phase":
            rule.phase = v or None
    return out


def _apply_one_field(rule: Rule, key: str, val: str) -> Optional[str]:
    """Apply one already-known-good `key` (schema-checked by the caller) to `rule`."""
    if key in ("absorbs", "archetypes"):
        items = [_strip_backticks(v) for v in _split_respecting_parens(val) if v.strip()]
        setattr(rule, key, items)
        return None
    if key in ("parent", "invariant"):
        setattr(rule, key, _strip_backticks(val))
        return None
    if key == "severity":
        rule.severity = _normalize_severity(val)
        return None
    setattr(rule, key, val)
    return None


def _apply_field(rule: Rule, key: str, val: str, lineno: int) -> List[str]:
    """Apply one parsed `key: value` pair to `rule`. Returns any `[BAD-META]`/`[UNKNOWN-FIELD]`
    violations (F8) — a slash-joined key (`design/review/check`) applies `val` to every named
    sub-key and reports one violation per unrecognised sub-key."""
    if key == "meta":
        return _apply_meta(rule, val, lineno)
    sub_keys = [k.strip() for k in key.split("/") if k.strip()]
    out: List[str] = []
    for sub in sub_keys:
        if sub not in KNOWN_FIELD_KEYS:
            out.append(f"[UNKNOWN-FIELD] {rule.id} line {lineno}: '{sub}' is not a schema key")
            continue
        _apply_one_field(rule, sub, val)
    return out


def parse_file(path: Path, root: Path) -> tuple:
    """Parse every `#### <id> — <title>` record in one references/*.md file.

    Returns `(rules, violations)` — `violations` are the F8 parse-time classes
    (`[UNKNOWN-FIELD]`, `[BAD-META]`) discovered while walking the file.
    """
    text = path.read_text(encoding="utf-8")
    relpath = path.relative_to(root).as_posix()
    rules: List[Rule] = []
    violations: List[str] = []
    current: Optional[Rule] = None
    for lineno, raw in enumerate(text.splitlines(), start=1):
        m = HEADING_RE.match(raw.strip())
        if m:
            current = Rule(id=m.group("id"), title=m.group("title").strip(),
                            file=relpath, line=lineno)
            rules.append(current)
            continue
        if current is None:
            continue
        if raw.strip() == "---":
            current = None
            continue
        for key, val in _split_field_line(raw):
            violations.extend(_apply_field(current, key, val, lineno))
    return rules, violations


def parse_tree(root: Path) -> tuple:
    """Parse every references/*.md rule file under `root`, excluding the generated walks.

    Returns `(rules, violations)` — see `parse_file`.
    """
    refs = root / "references"
    if not refs.is_dir():
        return [], []
    rules: List[Rule] = []
    violations: List[str] = []
    for path in sorted(refs.glob("*.md")):
        if path.name in GENERATED_WALK_FILES:
            continue
        file_rules, file_violations = parse_file(path, root)
        rules.extend(file_rules)
        violations.extend(file_violations)
    return rules, violations


def parse_archetypes(root: Path) -> Dict[str, List[str]]:
    """references/archetypes.md's pipe table: `{archetype_id: [backtick-token, ...]}` from
    column 4 ("Rule demanding the guard"). F2 — this is the join key `rules.json`'s
    `archetypes[]` field exists for (governance.md, "the one condition it exists under")."""
    path = root / "references" / "archetypes.md"
    if not path.exists():
        return {}
    out: Dict[str, List[str]] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        m = ARCHETYPE_ROW_RE.match(line.strip())
        if not m:
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) < 4:
            continue
        out[m.group(1)] = re.findall(r"`([^`]+)`", cells[3])
    return out


# ---------------------------------------------------------------------------
# Structural checks — the --check violation classes plus zero-count.
# ---------------------------------------------------------------------------

def _repo_anchor(root: Path) -> Path:
    """Walk up from `root` to the nearest repo marker; fall back to `root` itself.

    Hermetic by construction: a fixture with no `.git`/`VERSION`/`.ai-badger` above it
    anchors on itself, so citation resolution never reaches outside the tree under test.
    """
    cur = root
    for _ in range(6):
        if (cur / ".git").exists() or (cur / "VERSION").exists() or (cur / ".ai-badger").exists():
            return cur
        if cur.parent == cur:
            break
        cur = cur.parent
    return root


# F14: the fallback used to be `anchor.rglob(basename)` over the *whole* repo — O(repo) per
# unresolved token, and `cites: \`totally/wrong/path/README.md\`` passed because *some*
# README.md existed somewhere, path be damned. Bounded two ways instead: (a) the search only
# walks directories a real `cites:` token actually resolves under — derived from
# `grep -ho 'cites:.*' references/*.md` over the shipped ruleset: the citing rule's *own* skill
# `references/` (by far the common case — bare filenames like `governance.md`, `conflicts.md`,
# handled directly in `_citation_exists`, not via this list); plugin-skill bodies under
# `features/*/skills/*` (also reaches each such skill's own `references/`); stack instruction
# files under `features/*/instructions`; personas under `features/common/personas`; invariants
# under `.ai-badger/invariants` (the direct-path check in `check_cites` usually catches these
# first) or a *stack-specific* invariants directory (`features/*/invariants` — e.g.
# `design-every-reachable-state` lives under `features/ux/invariants/`,
# `clean-architecture-layering` under `features/dotnet/invariants/`, not the common one); `docs`
# for forward compatibility — not the invariant of "wherever the file happens to be". (b) A
# match requires every path segment the citation named, not just the basename, so
# `wrong/path/README.md` no longer matches a `README.md` that lives somewhere else entirely.
_CITATION_FALLBACK_ROOTS = (
    ".ai-badger/invariants",
    "features/*/invariants",
    "features/common/personas",
    "features/*/instructions",
    "features/*/skills/*",
    "docs",
)


def _citation_exists(anchor: Path, relpath: str, skill_root: Optional[Path] = None) -> bool:
    direct = anchor / relpath
    if direct.exists():
        return True
    if skill_root is not None and (skill_root / "references" / relpath).exists():
        return True
    parts = Path(relpath).parts
    for pattern in _CITATION_FALLBACK_ROOTS:
        for candidate_root in anchor.glob(pattern):
            if not candidate_root.is_dir():
                continue
            for p in candidate_root.rglob(parts[-1]):
                if ".git" in p.parts:
                    continue
                if p.parts[-len(parts):] == parts:
                    return True
    return False


def check_l1_cap(rules: List[Rule]) -> List[str]:
    live = [r for r in rules if r.layer == "1" and not r.is_retired]
    if len(live) > L1_CAP:
        return [f"[L1-CAP] live L1 rule count {len(live)} exceeds the cap of {L1_CAP}"]
    return []


def check_missing_parent(rules: List[Rule]) -> List[str]:
    out = []
    for r in rules:
        if r.layer in ("2", "3") and not r.parent:
            out.append(f"[NO-PARENT] {r.id} is L{r.layer} and carries no parent:")
    return out


def check_dangling_parent(rules: List[Rule]) -> List[str]:
    """`parent:` is normally one id (§3.1); a handful of stack rules specialise two (comma-
    separated) — every named id must resolve, individually."""
    known = {r.id for r in rules}
    out = []
    for r in rules:
        if not r.parent:
            continue
        for pid in (p.strip() for p in r.parent.split(",")):
            if pid and pid not in known:
                out.append(f"[BAD-PARENT] {r.id} parent: {pid} does not resolve to a parsed rule")
    return out


def check_absorbs_shape(rules: List[Rule]) -> List[str]:
    """F4: an `absorbs:` id is valid if it matches the source-lane id grammar (module docstring)
    or is itself a known rule id — no hand-maintained manifest to drift out from under it."""
    known_ids = {r.id for r in rules}
    out = []
    for r in rules:
        for aid in r.absorbs:
            if aid in known_ids:
                continue
            if not ABSORBS_SHAPE_RE.match(aid):
                out.append(f"[BAD-ABSORBS] {r.id} absorbs: {aid} does not match the "
                            f"source-lane id grammar (see the module docstring)")
    return out


def check_cites(root: Path, rules: List[Rule]) -> List[str]:
    anchor = _repo_anchor(root)
    out = []
    for r in rules:
        if not r.cites:
            continue
        for slug in INVARIANT_CITE_RE.findall(r.cites):
            if not (_citation_exists(anchor, f".ai-badger/invariants/{slug}.md")
                    or _citation_exists(anchor, f"{slug}.md", root)):
                out.append(f"[BAD-CITES] {r.id} cites: invariant `{slug}` — "
                            f"no {slug}.md found under {anchor}")
        for token in FILE_CITE_RE.findall(r.cites):
            if not _citation_exists(anchor, token, root):
                out.append(f"[BAD-CITES] {r.id} cites: `{token}` — not found under {anchor}")
    return out


def check_weak_severity(rules: List[Rule]) -> List[str]:
    out = []
    for r in rules:
        if r.evidence == "weak" and (r.severity == "blocker" or r.flag == "auto"):
            out.append(f"[WEAK-SEVERITY] {r.id} is evidence: weak but "
                        f"severity: {r.severity} / flag: {r.flag}")
    return out


def check_filenames(root: Path) -> List[str]:
    refs = root / "references"
    if not refs.is_dir():
        return []
    out = []
    for path in sorted(refs.iterdir()):
        if path.is_file() and not FILENAME_RE.match(path.name):
            out.append(f"[BAD-FILENAME] references/{path.name} does not match ^[a-z][a-z-]*\\.md$")
    return out


# F5 part (1): a `check:` collapsed to a bare flag word carries no runnable falsifier — the
# word belongs in `flag:`, not in `check:`. The one legal flag-shaped value is the retired
# tombstone's own placeholder, and only on a rule that actually carries `retired:`.
_CHECK_FLAG_WORDS = {"argued", "auto", "auto-unless-listed"}
_RETIRED_CHECK_PLACEHOLDER = "retired — not applicable."


def check_empty_check(rules: List[Rule]) -> List[str]:
    """F5: `[EMPTY-CHECK]` — `check:` is blank, or reduced to one of `flag:`'s own words with no
    falsifier attached. L0 principles carry no `check:` by design and are skipped; a genuinely
    missing `check:` on L1-L3 is also `[MISSING-FIELD]`, deliberately — the two classes name
    different defects (absent vs. present-but-useless)."""
    out = []
    for r in rules:
        if r.layer == "0":
            continue
        check = (r.check or "").strip()
        if not check:
            out.append(f"[EMPTY-CHECK] {r.id} check: is blank — no runnable falsifier")
            continue
        if check == _RETIRED_CHECK_PLACEHOLDER:
            if r.is_retired:
                continue
            out.append(f"[EMPTY-CHECK] {r.id} check: {check!r} but the rule carries no retired:")
            continue
        if check.rstrip(".") in _CHECK_FLAG_WORDS:
            out.append(f"[EMPTY-CHECK] {r.id} check: {check!r} is a flag word, not a falsifier")
    return out


def check_zero_rules(rules: List[Rule]) -> List[str]:
    if not rules:
        return ["[ZERO-RULES] parsed 0 rules — check the references/ directory path and file names"]
    return []


def check_duplicate_ids(rules: List[Rule]) -> List[str]:
    """Not one of the named classes, but a rule.json built over duplicate ids is corrupt
    (last-write-wins in every downstream lookup) — cheap to catch, so it is."""
    seen: dict = {}
    out = []
    for r in rules:
        if r.id in seen:
            out.append(f"[DUP-ID] {r.id} defined at {seen[r.id]} and again at {r.file}:{r.line}")
        else:
            seen[r.id] = f"{r.file}:{r.line}"
    return out


def check_bad_archetypes(root: Path, rules: List[Rule]) -> List[str]:
    """F2: every rule id named in archetypes.md's "Rule demanding the guard" column must
    resolve to a parsed rule. Non-rule backtick tokens in that column (`invariant
    \\`bounded-retry-loops\\``) are not rule citations and are not checked here."""
    known = {r.id for r in rules}
    out = []
    for aid, cited in sorted(parse_archetypes(root).items()):
        for token in cited:
            if RULE_ID_LIKE_RE.match(token) and token not in known:
                out.append(f"[BAD-ARCHETYPE] {aid} names {token}, which does not resolve")
    return out


def check_missing_fields(rules: List[Rule]) -> List[str]:
    """F8: per layer, the fields `governance.md`'s "Adding a rule" bar requires. L0 principles
    need nothing beyond id/title (both come from the heading, not a bullet line)."""
    out = []
    for r in rules:
        if r.layer == "0":
            continue
        required = _L1_REQUIRED if r.layer == "1" else _L23_REQUIRED
        for attr in required:
            if getattr(r, attr) in (None, "", []):
                out.append(f"[MISSING-FIELD] {r.id} has no {_REQUIRED_DISPLAY.get(attr, attr)}:")
    return out


def run_structural_checks(root: Path, rules: List[Rule],
                           parse_violations: Optional[List[str]] = None) -> List[str]:
    out: List[str] = list(parse_violations or [])
    out += check_zero_rules(rules)
    out += check_duplicate_ids(rules)
    out += check_l1_cap(rules)
    out += check_missing_parent(rules)
    out += check_dangling_parent(rules)
    out += check_absorbs_shape(rules)
    out += check_cites(root, rules)
    out += check_weak_severity(rules)
    out += check_filenames(root)
    out += check_bad_archetypes(root, rules)
    out += check_missing_fields(rules)
    out += check_empty_check(rules)
    return out


# ---------------------------------------------------------------------------
# Generation — rules.json, walk-review.md, walk-design.md.
# ---------------------------------------------------------------------------

def _invert_archetypes(archetype_map: Dict[str, List[str]], known_ids: set) -> Dict[str, List[str]]:
    """rule id -> sorted archetype ids that name it. Unresolved tokens are skipped here —
    `check_bad_archetypes` is what reports those; this just never invents a join for one."""
    out: Dict[str, List[str]] = {}
    for aid, cited in archetype_map.items():
        for token in cited:
            if token in known_ids:
                out.setdefault(token, []).append(aid)
    return {rid: sorted(aids) for rid, aids in out.items()}


def build_index(rules: List[Rule], archetype_map: Optional[Dict[str, List[str]]] = None) -> list:
    """rules.json content: structural fields only, no prose (`governance.md` "`rules.json` —
    the one condition it exists under")."""
    known_ids = {r.id for r in rules}
    rule_archetypes = _invert_archetypes(archetype_map or {}, known_ids)
    out = []
    for r in sorted(rules, key=lambda r: r.id):
        out.append({
            "id": r.id,
            "layer": r.layer,
            "group": r.group,
            "title": r.title,
            "severity": r.severity,
            "evidence": r.evidence,
            "flag": r.flag,
            "parent": r.parent,
            "absorbs": r.absorbs,
            "archetypes": rule_archetypes.get(r.id, []),
            "pass": r.pass_,
            "order": r.order,
            "phase": r.phase,
            "retired": r.retired,
            "file": r.file,
            "line": r.line,
        })
    return out


def _dump_json(data) -> str:
    return json.dumps(data, indent=2, ensure_ascii=False) + "\n"


def render_walk_review(rules: List[Rule]) -> str:
    """Passes 0-8, steps ordered by (pass, order); a rule sharing its parent's (pass, order)
    renders indented directly under it — "inserted at the parent's step number" (§5)."""
    lines = [GENERATED_BANNER, "", "# Review walk", ""]
    eligible = [r for r in rules if r.pass_ is not None and not r.is_retired]
    for p in PASSES:
        in_pass = sorted((r for r in eligible if r.pass_ == p), key=lambda r: (r.order or 0, r.id))
        if not in_pass:
            continue
        lines.append(f"## Pass {p}")
        lines.append("")
        for r in in_pass:
            prefix = "  - ↳" if r.parent else "-"
            lines.append(f"{prefix} **{r.id}** — {r.title} "
                          f"(severity: {r.severity}, flag: {r.flag})")
        lines.append("")
    return "\n".join(lines).rstrip("\n") + "\n"


def render_walk_design(rules: List[Rule]) -> str:
    """Steps 1-8 from `phase:`, omitting `phase: review-only` (§6)."""
    lines = [GENERATED_BANNER, "", "# Design walk", ""]
    eligible = [r for r in rules
                if r.phase not in (None, "review-only") and not r.is_retired]
    for step in DESIGN_STEPS:
        in_step = sorted((r for r in eligible if r.phase == str(step)),
                          key=lambda r: (r.order or 0, r.id))
        if not in_step:
            continue
        lines.append(f"## Step {step}")
        lines.append("")
        for r in in_step:
            prefix = "  - ↳" if r.parent else "-"
            lines.append(f"{prefix} **{r.id}** — {r.title} "
                          f"(severity: {r.severity}, flag: {r.flag})")
        lines.append("")
    return "\n".join(lines).rstrip("\n") + "\n"


def _atomic_write(path: Path, text: str) -> None:
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(text, encoding="utf-8")
    tmp.replace(path)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main(argv=None) -> int:
    """CLI entry point: build (or --check) rules.json + the two walks for a skill root."""
    ap = argparse.ArgumentParser(description=__doc__,
                                  formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", help="review-tests skill directory (default: this script's own)")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args(argv)

    default_root = Path(__file__).resolve().parent.parent
    root = Path(args.root).resolve() if args.root else default_root

    rules, parse_violations = parse_tree(root)
    violations = run_structural_checks(root, rules, parse_violations)
    if violations:
        for v in violations:
            print(v)
        print(f"{len(violations)} violation(s) — rules.json and the walks were NOT written")
        return 1

    archetype_map = parse_archetypes(root)
    index = build_index(rules, archetype_map)
    index_text = _dump_json(index)
    walk_review_text = render_walk_review(rules)
    walk_design_text = render_walk_design(rules)

    index_path = root / "rules.json"
    walk_review_path = root / "references" / "walk-review.md"
    walk_design_path = root / "references" / "walk-design.md"

    if args.check:
        drift = []
        for path, text, label in (
            (index_path, index_text, "rules.json"),
            (walk_review_path, walk_review_text, "references/walk-review.md"),
            (walk_design_path, walk_design_text, "references/walk-design.md"),
        ):
            if not path.exists() or path.read_text(encoding="utf-8") != text:
                drift.append(f"[STALE] {label} is missing or stale — run rules_index.py")
        if drift:
            for d in drift:
                print(d)
            return 1
        print(f"rules.json and both walks up to date — {len(rules)} rules parsed")
        return 0

    _atomic_write(index_path, index_text)
    _atomic_write(walk_review_path, walk_review_text)
    _atomic_write(walk_design_path, walk_design_text)
    print(f"wrote rules.json, walk-review.md, walk-design.md — {len(rules)} rules parsed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
