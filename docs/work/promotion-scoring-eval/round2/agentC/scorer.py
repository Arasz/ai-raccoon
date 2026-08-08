#!/usr/bin/env python3
"""Promotion-usefulness scorer (round 2, agent C — freestyle).

Usage: python3 scorer.py <candidates.json>  ->  [{"id": ..., "score": ...}] on stdout.

Design: every candidate is routed to a provenance CHANNEL derived from its
source path (or its organic origin); the channel supplies a prior on the 0-4
usefulness scale. Bounded content evidence — durable-rule language, verified
measurements, cross-project reach, coordination ephemera, pointer/status
shapes — moves the entry off that prior. Organic writes (source_file null)
additionally pass through a status-dump/turn-mirror refinement, because their
dominant failure mode is a conversation status recap masquerading as a fact.

Deterministic, stdlib only, no network.
"""

import json
import math
import re
import sys

# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

def clamp(x, lo, hi):
    return lo if x < lo else (hi if x > hi else x)


def basename(path):
    return (path or "").rsplit("/", 1)[-1].lower()


WORD = re.compile(r"[A-Za-z][A-Za-z0-9_\-']*")


def per100(count, nwords):
    return 100.0 * count / max(nwords, 1)


HEX_NAME = re.compile(r"^[0-9a-f]{32,}\.md$", re.I)


def effective_path(cand):
    """The most informative provenance path: ingest source, else the stored
    path with any shared/ promotion prefix stripped."""
    src = cand.get("source_file")
    if src:
        return src
    p = cand.get("path") or ""
    if p.startswith("shared/"):
        p = p[len("shared/"):]
    return p


# ---------------------------------------------------------------------------
# channels (provenance archetypes)
# ---------------------------------------------------------------------------
# First match wins; ordering encodes which provenance fact dominates.

CHANNEL_PRIOR = {
    "turn_mirror": 0.35,
    "remember_log": 0.30,        # .remember/ status journals
    "auto_memory_session": 0.30,  # session-*-status dumps in Claude auto-memory
    "auto_memory_index": 0.55,    # MEMORY.md — rows of pointers
    "auto_memory_note": 2.70,     # named durable note in Claude auto-memory
    "organic_note": 2.30,         # agent memory_write, refined below
    "doc_index": 0.35,            # README/CHANGELOG/index ledgers
    "adr": 2.55,
    "charter": 2.30,
    "explanation": 2.15,
    "measurement": 2.10,
    "research_synthesis": 1.75,
    "reference": 1.45,
    "changelog_entry": 1.05,
    "work_note": 1.30,
    "plan": 0.70,
    "review": 0.95,
    "catalog_page": 1.05,
    "other_doc": 1.10,
}

TURN_MIRROR = re.compile(
    r"</?(invoke|parameter|content|sourceFile|antml:invoke|function_calls)\b|"
    r"<invoke\s+name=|<parameter\s+name=",
    re.I,
)


def mirror_prefix(value):
    """If a tool-call transcript is embedded, return the prose before it.

    A true turn-mirror starts (nearly) at the transcript; a real note with a
    transcript accidentally appended keeps a substantial prose prefix, which
    is the entry worth scoring."""
    m = TURN_MIRROR.search(value)
    if not m:
        return value, False
    if m.start() >= 300:
        return value[: m.start()], False
    return value, len(TURN_MIRROR.findall(value)) >= 2


def channel(cand):
    value = cand.get("value") or ""
    _, is_mirror = mirror_prefix(value)
    if is_mirror:
        return "turn_mirror"

    p = effective_path(cand).lower()
    base = basename(p)

    if "/.remember/" in p or p.startswith(".remember/"):
        return "remember_log"

    # Claude Code auto-memory tree: ~/.claude/projects/<slug>/memory/*
    if "/.claude/" in p and "/memory/" in p:
        if base == "memory.md":
            return "auto_memory_index"
        if base.startswith("session") or "status" in base or "handoff" in base:
            return "auto_memory_session"
        return "auto_memory_note"

    if cand.get("source_file") is None or HEX_NAME.match(basename(cand.get("path") or "")):
        # organic agent write with no ingest source
        if not p or HEX_NAME.match(base) or p == (cand.get("path") or "").lower():
            return "organic_note"
        # promoted entry whose stored path embeds a real docs path: fall
        # through and classify by that path, but remember it is organic.
        return "organic_note"

    if base in ("readme.md", "changelog.md", "index.md"):
        return "doc_index"
    if "/adr/" in p or "/decisions/" in p or (
        re.match(r"^\d{4}-", base) and not re.match(r"^\d{4}-\d{2}-\d{2}", base)
    ):
        return "adr"
    if "charter" in base:
        # A dated charter under docs/work is in-flight review coordination,
        # not a durable project charter.
        if re.match(r"^\d{4}-\d{2}-\d{2}", base):
            return "review"
        return "charter"
    if "/explanation/" in p or "/design/" in p or "architecture" in base:
        return "explanation"
    if "/plans/" in p or base.startswith("plan") or "-plan" in base or base.endswith("plan.md") \
            or "backlog" in base or "checkpoint" in base or "dev-review" in base:
        return "plan"
    if (
        "/reviews/" in p
        or "review" in base
        or "moe-" in base
        or re.match(r"^[a-z]\d+-", base)
        or "findings" in base
        or "incident" in base
        or "diagnosis" in base
    ):
        return "review"
    if "sweep" in base or "benchmark" in base or "-perf" in base or "perf-" in base:
        return "measurement"
    if "/archive/" in p or "research" in base or "synthesis" in base or "report" in base:
        return "research_synthesis"
    if "/reference/" in p or "/how-to/" in p or "/tutorial/" in p:
        return "reference"
    if "/changelog/" in p or "/releases/" in p:
        return "changelog_entry"
    if "/guide" in p or base in ("skills.md", "getting-started.md", "glossary.md"):
        return "catalog_page"
    if "/docs/work/" in p:
        return "work_note"
    if "/docs/" in p:
        return "other_doc"
    return "work_note"


# ---------------------------------------------------------------------------
# content evidence
# ---------------------------------------------------------------------------

RULE_RE = re.compile(
    r"\bnever\b|\balways\b|\bmust (?:still |be |not |name|make|raise|treat|come|already|exist)|"
    r"\bdo not\b|\bdon't\b|\bdoes not apply\b|(?<!\bI )(?<!we )\bcannot\b|\bprefer\b|\binvariant\b|\btraps?\b|\bgotchas?\b|"
    r"\bbeware\b|\bmitigations?\b|\bworkarounds?\b|\brule\b|\bcontract\b|\bsemantics\b|"
    r"\bprecedence\b|\bby design\b|\bdeliberate(?:ly)?\b|\bon purpose\b|"
    r"\bsilently\b|\bfails? open\b|\bworse than\b|\bREGRESSION\b|\broot cause\b|"
    r"\baccepted, not a defect\b|\bnot a defect\b|\bexempt\b|\bso nobody\b|\brecorded here\b|"
    r"\bfuture (?:session|audit|reader|maintainer)|\binvisible to\b|\bhides?\b(?=[^.]{0,60}\b(?:from|rows|content|search))",
    re.I,
)

MEASURE_WORDS = re.compile(
    r"\bmeasured\b|\bbest-of-\d|\bwall (?:clock|time)\b|\bbenchmark(?:ed|dotnet)?\b|"
    r"\bobserved\b|\breproduc(?:ed|ible)\b|\bverified\b|\bp9[59]\b|"
    r"\bthroughput\b|\bpeak rss\b|\bnDCG\b|\brecall@|\bMRR\b",
    re.I,
)
NUM_UNIT = re.compile(
    r"\b\d+(?:[.,]\d+)?\s?(?:ms|s|sec|secs|seconds|min|mb|gb|kb|%|×|x)\b|"
    r"\b\d+(?:[.,]\d+)?\s?(?:chunks?|packages?|files?|entries|rows)\b",
    re.I,
)

# in-flight coordination / lifecycle ephemera
EPHEMERA_RE = re.compile(
    r"\bAC:|\bGate(?:s)?:|\bacceptance criteria\b|\beffort:|\bimpact:|\bevidence:\s*(?:MEASURED|INFERRED)|"
    r"\bworktree\b|\bwave \d|\bsub-?agents?:|\bpersona\s*/\s*model|\bcloses:|\bdispatch(?:ed|ing)?\b|"
    r"\bTDD: (?:yes|no)\b|\bowner (?:ruling|action|requirement)\b|\brisk if deferred\b|"
    r"\breviewer lane\b|\blens (?:a\d|of)\b|\bthis lens\b|\bread-only (?:review|audit)\b|"
    r"\bout of scope for\b|\bfollow-?ups? §|\bstatus:\s*(?:executed|open|verified)\b|"
    r"\bPR #\d+|\bissue #\d+|\bPRs? #\d+|\b#\d{3,}\b",
    re.I,
)

SUPERSEDED_RE = re.compile(
    r"\bsupersed(?:ed|es|ing)\b|\bhistorical note\b|\bno longer\b|\bwas (?:reversed|retracted)\b|"
    r"\bhistorical\)|\bthis was resolved\b|\bhas now (?:fired|lapsed)\b|\bpreviously lived\b",
    re.I,
)

FINDING_ROW = re.compile(r"^\s*\|\s*\**\s*[A-Z]{1,3}[-]?\d{1,3}\b", re.M)
TABLE_ROW = re.compile(r"^\s*\|")
MD_LINK = re.compile(r"\]\(")
DOC_FILENAME = re.compile(r"[\w.\-/]+\.md\b")
FRONTMATTER = re.compile(r"^---\s*\n(?:updated|name|version):", re.M)
VERSION_ROW = re.compile(r"^\s*\|\s*\d+\.\d+", re.M)

# status-dump / conversation-recap signals (organic failure mode)
STATUS_OPENER = re.compile(
    r"^\s*(Done\b|Fixed\b|Verified\b|Merged\b|Pinned\b|Recorded\b|Rendered\b|All confirmed|"
    r"Status\b|Prep done|Shell ready|Task closed|Cleanup complete|Amended\b|Locked in|"
    r"Draft PR|Fresh verification|No code has changed|f: |Implementation in flight|"
    r"Extension complete|Rollout complete|Ad-hoc verification|Render(?:\s|ing)|"
    r"Plan written|Software MoE|Here.s the current|PR[- #]|\*\*1\.|"
    r"All verification|Verification (?:confirmed|complete)|"
    r"[A-Z][\w' -]{0,30}\b(?:complete|done|closed|finished|delivered)\b\s*[.:!\u2014\u2013-])",
    re.I,
)
STATUS_VOCAB = re.compile(
    r"\b(merged|pushed|re-pushed|in flight|worktree|pre-push|gate chain|full suite|"
    r"suite green|passed\b|skipped\b|exit 0|handed over|dispatched|still working|"
    r"waiting on|re-run|rebased|cherry-pick|draft PR|ready for review|cron job|"
    r"status board|deliverable|committed as|origin/main|HEAD|CI checks)\b",
    re.I,
)
SECOND_PERSON = re.compile(r"\b(your|you'll|you can|as instructed|per your)\b", re.I)
TEST_COUNTS = re.compile(
    r"\b\d+\s*(passed|failed|skipped|errors|warnings|green|assertions?)\b|\bexit 0\b|\b\d+/\d+\b",
    re.I,
)
MEASURE_UNITS_LOOSE = re.compile(r"\d+(?:\.\d+)?\s*(?:s|ms|x|%|lines?|KB|MB|GB|tokens?)\b")
DURABLE_LOOSE = re.compile(
    r"\[facts\]|\bREGRESSION\b|\bgotcha\b|\broot cause\b|\brequires?\b|\bmust\b|\bnever\b|"
    r"\balways\b|\bconvention\b|\bone \w+ per \w+|\bholds\b|\bby design\b|\bcontract\b|"
    r"\bprecedence\b|\bsemantics\b|\bmeans\b[^.\n]{0,40}\bnot\b",
    re.I,
)
DATED_FACT = re.compile(r"\(\d{4}-\d{2}-\d{2}\)[:,]|\bverified \d{4}(?:-\d{2})?\b|\(verified ")
FIRST_PERSON = re.compile(r"\bI\b|\bmy\b|\bme\b")
URL_RE = re.compile(r"https?://")
CONTENTS_HEADER = re.compile(r"^#{1,3}\s*Contents\b", re.M | re.I)
DIR_README = re.compile(r"^#\s+[\w.\-]+/\s*$", re.M)
META_HEADER_LINE = re.compile(
    r"^\s*\*{0,2}(Task|Project|Worktree|Branch(?:/worktree)?|Date|Reviewer|Draft|Owner|Updated|Version|Corpus|Scope)\s*[:*]",
    re.M | re.I,
)
IMPERATIVE_ITEM = re.compile(
    r"^\s*(?:\d+\.|[-*])\s+(?:Review|Reconcile|Run|Submit|Report|Finish|Fix|Update|Rebase|Merge|Check|Verify|Register|Grep|Add|Remove|Delete|Rename|Move)\b",
    re.M,
)
COMMIT_HASH = re.compile(r"\b[0-9a-f]{7,10}\b")

PROJECT_ALIASES = {
    "ai-badger": ["ai-badger", "ai_badger", "badger_lib", "welcome-ai-badger"],
    "ai-raccoon": ["ai-raccoon", "airaccoon", "ai_raccoon"],
    "jsaa": ["jsaa", "job-search-ai-assistant", "jobsearchaiassistant"],
    "hermes-default": ["hermes"],
    "arasz-home-page": ["arasz-home-page", "arasz.dev", "home-page"],
}


def features(cand):
    v, _ = mirror_prefix(cand.get("value") or "")
    w = WORD.findall(v)
    nw = len(w)
    lines = [ln for ln in v.splitlines() if ln.strip()]
    nlines = max(len(lines), 1)
    f = {}

    f["rule"] = per100(len(RULE_RE.findall(v)), nw)
    f["measure_words"] = len(MEASURE_WORDS.findall(v))
    f["num_unit"] = len(NUM_UNIT.findall(v))
    f["ephemera"] = per100(len(EPHEMERA_RE.findall(v)), nw)
    f["superseded"] = 1 if SUPERSEDED_RE.search(v) else 0
    f["finding_rows"] = len(FINDING_ROW.findall(v))
    f["table_frac"] = sum(1 for ln in lines if TABLE_ROW.match(ln)) / nlines
    f["link_density"] = per100(len(MD_LINK.findall(v)), nw)
    f["docname_density"] = per100(len(DOC_FILENAME.findall(v)), nw)
    f["version_rows"] = len(VERSION_ROW.findall(v))
    f["frontmatter"] = 1 if FRONTMATTER.search(v.lstrip()) else 0
    f["nchars"] = len(v)
    f["nwords"] = nw

    stripped = v.lstrip()
    first = stripped[:1]
    f["mid_sentence"] = 1 if (first.islower() or first in "),;") else 0
    f["heading_start"] = 1 if stripped.startswith("#") else 0

    own = cand.get("project_id") or ""
    low = v.lower()
    head = low[:250]
    foreign = 0
    foreign_head = 0
    for pid, aliases in PROJECT_ALIASES.items():
        if pid == own:
            continue
        if any(a in low for a in aliases):
            foreign += 1
        if any(a in head for a in aliases):
            foreign_head = 1
    f["foreign_projects"] = foreign
    f["foreign_subject"] = foreign_head

    # status-dump features
    opener_head = v.lstrip("*#>-\u2013 \t\n")[:80]
    f["status_opener"] = 1 if STATUS_OPENER.search(opener_head) else 0
    f["status_vocab"] = len(STATUS_VOCAB.findall(v))
    f["second_person"] = 1 if SECOND_PERSON.search(v) else 0
    f["commit_hashes"] = len(COMMIT_HASH.findall(v))
    f["real_measures"] = len(MEASURE_UNITS_LOOSE.findall(TEST_COUNTS.sub(" ", v)))
    f["durable_loose"] = len(DURABLE_LOOSE.findall(v))
    f["dated_fact"] = 1 if DATED_FACT.search(v[:120]) else 0
    f["first_person"] = per100(len(FIRST_PERSON.findall(v)), nw)
    f["meta_header"] = len(META_HEADER_LINE.findall(v))
    f["imperatives"] = len(IMPERATIVE_ITEM.findall(v))
    f["urls"] = len(URL_RE.findall(v))
    f["contents_index"] = 1 if CONTENTS_HEADER.search(v) else 0
    f["dir_readme"] = 1 if DIR_README.match(v.lstrip()) else 0
    return f


# ---------------------------------------------------------------------------
# scoring
# ---------------------------------------------------------------------------

def doc_adjust(f, ch="work_note"):
    """Content evidence for document-derived chunks."""
    adj = 0.0
    rule_cap = 0.45 if ch == "plan" else 1.10
    adj += clamp(0.38 * f["rule"], 0.0, rule_cap)

    if f["measure_words"] >= 1 and f["num_unit"] >= 1:
        adj += clamp(0.12 * min(f["measure_words"], 4) + 0.05 * min(f["num_unit"], 4), 0.0, 0.50)
    elif f["measure_words"] >= 2:
        adj += 0.15

    if f["foreign_subject"]:
        adj += 0.25
    if f["foreign_projects"] >= 2:
        adj += 0.10

    if f["heading_start"]:
        adj += 0.10
    if f["mid_sentence"]:
        adj -= 0.18

    pointer = 0.0
    if f["table_frac"] >= 0.55:
        pointer += 0.30
    if f["link_density"] >= 1.5:
        pointer += 0.45
    if f["docname_density"] >= 2.0:
        pointer += 0.35
    if f["version_rows"] >= 3:
        pointer += 0.35
    adj -= clamp(pointer, 0.0, 1.00)

    if f["finding_rows"] >= 3:
        adj -= 0.20
    elif f["finding_rows"] >= 1:
        adj -= 0.12

    adj -= clamp(0.22 * f["ephemera"], 0.0, 0.65)
    adj -= clamp(0.18 * f["first_person"], 0.0, 0.55)
    if f["meta_header"] >= 4:
        adj -= 0.75
    elif f["meta_header"] >= 2:
        adj -= 0.45
    if f["imperatives"] >= 3:
        adj -= 0.30
    if f["measure_words"] >= 1 and f["rule"] >= 0.8:
        adj += 0.35

    if f["superseded"]:
        adj -= 0.40

    if f["frontmatter"] and f["nchars"] < 900:
        adj -= 0.55
    elif f["frontmatter"]:
        adj -= 0.25
    if f["nchars"] < 420:
        adj -= 0.35
    if f["nwords"] < 60:
        adj -= 0.25
    return adj


def organic_adjust(f):
    """Refinement for agent-authored writes: reward durable phrasing and real
    measurements, punish conversation-status shape."""
    d = 0.0
    if f["status_opener"]:
        d -= 1.20
    d -= min(1.5, 0.15 * f["status_vocab"])
    if f["second_person"]:
        d -= 0.50
    if f["commit_hashes"] >= 2:
        d -= 0.30
    if f["real_measures"] >= 2:
        d += 0.50
    d += min(1.0, 0.35 * f["durable_loose"])
    if f["dated_fact"]:
        d += 0.50
    if f["foreign_subject"]:
        d += 0.20
    # pointer-shaped organic writes are still pointers
    if f["link_density"] >= 1.5 or f["docname_density"] >= 2.5:
        d -= 0.40
    if f["table_frac"] >= 0.55:
        d -= 0.50
    if f["contents_index"]:
        d -= 1.20
    if f["urls"] >= 3 and f["durable_loose"] <= 2:
        d -= 0.50
    if f["docname_density"] >= 4.0 and f["durable_loose"] == 0 and f["rule"] < 0.5:
        d -= 0.30
    if f["meta_header"] >= 1:
        d -= 0.35
    if f["imperatives"] >= 3:
        d -= 0.40
    if f["dir_readme"]:
        d -= 0.80
    if f["finding_rows"] >= 3:
        d -= 0.70
    if f["superseded"]:
        d -= 0.40
    return d


def score_candidate(cand):
    ch = channel(cand)
    base = CHANNEL_PRIOR[ch]
    f = features(cand)

    if f["nwords"] < 8:
        return clamp(min(base, 0.50), 0.0, 4.0)

    if ch in ("turn_mirror", "remember_log", "auto_memory_session", "auto_memory_index", "doc_index"):
        # Hard-noise channels: content cannot buy them back very far — the
        # payload describes other work or other documents.
        lift = 0.0
        if ch == "auto_memory_index" and f["rule"] >= 1.0:
            lift = 0.15
        return clamp(base + lift, 0.0, 4.0)

    if ch == "organic_note":
        adj = organic_adjust(f)
        # short definitional fact exemption: a one-breath durable rule
        if f["nwords"] < 45 and f["durable_loose"] >= 1 and f["status_vocab"] <= 1 and not f["status_opener"]:
            base = max(base, 2.4)
        if f["nwords"] < 15:
            adj -= 0.30
        return clamp(base + clamp(adj, -2.2, 1.6), 0.0, 4.0)

    if ch == "auto_memory_note":
        # curated durable note; still check for status shape
        adj = 0.0
        adj += clamp(0.20 * f["rule"], 0.0, 0.60)
        if f["measure_words"] >= 1 and f["num_unit"] >= 1:
            adj += 0.30
        if f["foreign_subject"]:
            adj += 0.25
        adj -= min(1.2, 0.12 * f["status_vocab"])
        if f["status_opener"]:
            adj -= 0.80
        if f["superseded"]:
            adj -= 0.40
        return clamp(base + clamp(adj, -1.8, 1.2), 0.0, 4.0)

    # document channels
    adj = doc_adjust(f, ch)
    score = base + clamp(adj, -1.60, 1.30)
    if f["nwords"] < 25:
        score = min(score, 0.60)
    return clamp(score, 0.0, 4.0)


def main(argv):
    if len(argv) != 2:
        sys.stderr.write("usage: scorer.py <candidates.json>\n")
        return 2
    with open(argv[1], "r", encoding="utf-8") as fh:
        candidates = json.load(fh)
    out = [{"id": int(c["id"]), "score": round(float(score_candidate(c)), 4)} for c in candidates]
    json.dump(out, sys.stdout)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
