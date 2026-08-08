#!/usr/bin/env python3
"""Promotion-usefulness scorer v3 for ai-raccoon shared-tier candidates.

Usage:  python3 scorer.py <path-to-candidates-json>
Prints  [{"id": <int>, "score": <float>}, ...] for every candidate.

Evolution of the round-1 baseline (base_scorer.py + refine.py), merged into
one file. Same design: a provenance archetype supplies a prior on the 0-4
usefulness scale, bounded content-shape evidence moves the entry off it, and a
note-refinement layer separates durable facts from status dumps for
agent-authored notes. Deterministic, Python 3 stdlib only, no network.
"""

import json
import math
import re
import sys

# --------------------------------------------------------------------------
# 0. Text preparation
# --------------------------------------------------------------------------

HEX_NAME = re.compile(r"^[0-9a-f]{32,}\.md$", re.I)
TURN_MIRROR = re.compile(
    r"</?(invoke|parameter|content|sourceFile|antml:invoke|function_calls)\b|"
    r'<invoke\s+name=|<parameter\s+name=',
    re.I,
)


def basename(path):
    return (path or "").rsplit("/", 1)[-1].lower()


def words(text):
    return re.findall(r"[A-Za-z][A-Za-z0-9_\-']*", text)


def per100(count, nwords):
    return 100.0 * count / max(nwords, 1)


def clamp(x, lo, hi):
    return lo if x < lo else (hi if x > hi else x)


# --------------------------------------------------------------------------
# 1. Provenance archetypes -> prior on the 0-4 usefulness scale
# --------------------------------------------------------------------------
# Ordered: the first predicate that matches wins.
#
# v3 changes vs baseline:
#   - status_log:        session/status ledgers (.remember done-files, session
#                        notes, *_status files) — write-once progress records.
#   - auto_memory_index: a Claude auto-memory MEMORY.md — an index of links to
#                        topic files, not a fact itself.
#   - auto_memory_note:  a Claude auto-memory topic file — often a distilled
#                        durable lesson; gets the note-refinement layer.
#   - organic notes are split by the doc path they mirror: a note whose path
#                        embeds /docs/plans/ is plan output, not a durable
#                        fact, and no longer inherits the full organic prior.

ARCHETYPE_PRIORS = {
    "turn_mirror": 0.45,
    "status_log": 0.35,
    "auto_memory_index": 0.70,
    "auto_memory_note": 1.60,
    "organic_note": 2.95,
    "organic_plan_note": 1.55,
    "organic_work_note": 2.35,
    "doc_index": 0.25,
    "adr": 2.85,
    "charter": 2.45,
    "explanation": 2.30,
    "measurement": 2.10,
    "research_synthesis": 1.90,
    "reference": 1.45,
    "catalog_page": 1.10,
    "changelog_entry": 1.05,
    "work_note": 1.15,
    "plan": 0.80,
    "review": 0.75,
}

# Archetypes that get the agent-note refinement layer (section 4).
NOTE_ARCHETYPES = {
    "organic_note", "organic_plan_note", "organic_work_note", "auto_memory_note",
}

AUTO_MEMORY_DIR = re.compile(r"/\.claude/.*/memory/", re.I)
STATUS_NAME = re.compile(r"(^|[-_])(status|session|checkpoint|handoff|standup)([-_.]|\d|$)", re.I)


def archetype(cand):
    path = cand.get("path") or ""
    p = path.lower()
    base = basename(path)
    value = cand.get("value") or ""

    # A mirror of an agent's own tool-call turn.
    if len(TURN_MIRROR.findall(value)) >= 2:
        return "turn_mirror"

    # Progress ledgers: .remember done-files, session notes, status files.
    if "/.remember/" in p or base.endswith(".done.md"):
        return "status_log"

    # Claude auto-memory tree.
    if AUTO_MEMORY_DIR.search(p):
        if base == "memory.md":
            return "auto_memory_index"
        if STATUS_NAME.search(base):
            return "status_log"
        return "auto_memory_note"
    if STATUS_NAME.search(base) and cand.get("source_file") is not None:
        return "status_log"

    # Agent-authored memory write: no ingest source, or a content-addressed
    # filename rather than a path in a docs tree.
    if cand.get("source_file") is None or HEX_NAME.match(base):
        # A note whose recorded path mirrors a docs-tree document inherits
        # that document's nature, not the organic prior.
        if "/docs/plans/" in p or "/plans/" in p:
            return "organic_plan_note"
        if "/docs/work/" in p or "/docs/reviews/" in p:
            return "organic_work_note"
        return "organic_note"

    # Directory indexes and rolling ledgers.
    if base in ("readme.md", "changelog.md", "index.md"):
        return "doc_index"

    # A numbered decision record.
    if "/adr/" in p or "/decisions/" in p or (
        re.match(r"^\d{4}-", base) and not re.match(r"^\d{4}-\d{2}-\d{2}", base)
    ):
        return "adr"
    if "charter" in base:
        return "charter"
    if "/explanation/" in p or "/design/" in p or "architecture" in base:
        return "explanation"
    if "/plans/" in p or base.startswith("plan") or "-plan" in base or base.endswith("plan.md"):
        return "plan"
    if (
        "/reviews/" in p
        or "review" in base
        or "moe-" in base
        or re.match(r"^[a-z]\d+-", base)
        or "findings" in base
        or "incident" in base
    ):
        return "review"
    if "sweep" in base or "benchmark" in base or "-perf" in base or "perf-" in base:
        return "measurement"
    if "/archive/" in p or "research" in base or "synthesis" in base or "report" in base:
        return "research_synthesis"
    if "/reference/" in p or "/how-to/" in p or "/tutorial/" in p:
        return "reference"
    if "/guide" in p or base in ("skills.md", "getting-started.md", "glossary.md"):
        return "catalog_page"
    if "/changelog/" in p or "/releases/" in p:
        return "changelog_entry"
    return "work_note"


# --------------------------------------------------------------------------
# 2. Content-shape evidence (unchanged from baseline except where noted)
# --------------------------------------------------------------------------

RULE_PATTERNS = [
    r"\bnever\b", r"\balways\b", r"\bmust (?:still |be |not |name|make|raise)",
    r"\bdo not\b", r"\bdon't\b", r"\bdoes not\b(?=[^.]{0,40}\b(?:index|need|cover|apply))",
    r"\bprefer\b", r"\binvariant\b", r"\btraps?\b", r"\bgotchas?\b", r"\bbeware\b",
    r"\bmitigations?\b", r"\bworkarounds?\b", r"\brule\b", r"\bcontract\b",
    r"\bfuture (?:session|audit|reader|maintainer)", r"\bbefore proposing\b",
    r"\bnot (?:evidence|drift|duplication|debt)\b", r"\bis correct output\b",
    r"\bread (?:that|this|it) (?:comment |note )?before\b",
    r"\brecorded here\b", r"\bso nobody\b", r"\bexempt\b", r"\bby design\b",
    r"\bdeliberate(?:ly)?\b", r"\bon purpose\b", r"\brequired\b",
    r"\bsilently\b", r"\bfails? open\b", r"\bworse than\b",
]
RULE_RE = re.compile("|".join(RULE_PATTERNS), re.I)

MEASURE_WORDS = re.compile(
    r"\bmeasured\b|\bbest-of-\d|\bwall (?:clock|time)\b|\bbenchmark(?:ed|dotnet)?\b|"
    r"\bobserved\b|\breproduc(?:ed|ible)\b|\bverified independently\b|\bp9[59]\b|"
    r"\bthroughput\b|\bpeak rss\b|\bpassed?\b\s*\d|\bnDCG\b|\brecall@|\bMRR\b",
    re.I,
)
NUM_UNIT = re.compile(
    r"\b\d+(?:[.,]\d+)?\s?(?:ms|s|sec|secs|seconds|m|min|mb|gb|kb|%|×|x)\b|"
    r"\b\d+(?:[.,]\d+)?\s?(?:tests?|chunks?|packages?|lines?|files?|commits?)\b",
    re.I,
)

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
FRONTMATTER = re.compile(r"^---\s*\nupdated:", re.M)
VERSION_ROW = re.compile(r"^\s*\|\s*\d+\.\d+", re.M)

PROJECT_ALIASES = {
    "ai-badger": ["ai-badger", "ai_badger", "badger_lib", "welcome-ai-badger"],
    "ai-raccoon": ["ai-raccoon", "airaccoon", "ai_raccoon"],
    "jsaa": ["jsaa", "job-search-ai-assistant", "jobsearchaiassistant"],
    "hermes-default": ["hermes"],
}


def features(cand):
    v = cand.get("value") or ""
    w = words(v)
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
    f["access"] = cand.get("access_count") or 0
    return f


# --------------------------------------------------------------------------
# 3. Base scoring (baseline shape, retuned)
# --------------------------------------------------------------------------


def base_score(cand, arch, f):
    base = ARCHETYPE_PRIORS[arch]
    adj = 0.0

    # Durability evidence: generalizable rules are what the shared tier carries.
    adj += clamp(0.30 * f["rule"], 0.0, 0.75)

    # A measurement is durable when measurement words accompany the numbers.
    if f["measure_words"] >= 1 and f["num_unit"] >= 1:
        adj += clamp(0.12 * min(f["measure_words"], 4) + 0.05 * min(f["num_unit"], 4), 0.0, 0.50)
    elif f["measure_words"] >= 2:
        adj += 0.15

    # Subject is another project: the fact already crossed a boundary.
    if f["foreign_subject"]:
        adj += 0.25
    if f["foreign_projects"] >= 2:
        adj += 0.10

    if f["heading_start"]:
        adj += 0.12
    if f["mid_sentence"]:
        adj -= 0.18

    if f["access"] > 0:
        adj += clamp(0.05 * math.log1p(f["access"]), 0.0, 0.20)

    # Pointer rows: the payload names other documents instead of stating a fact.
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

    # Rows of a findings/work-package register.
    if f["finding_rows"] >= 3:
        adj -= 0.45
    elif f["finding_rows"] >= 1:
        adj -= 0.28

    # In-flight coordination language.
    adj -= clamp(0.22 * f["ephemera"], 0.0, 0.65)

    if f["superseded"]:
        adj -= 0.40

    # Frontmatter + preamble only: a document header, not content.
    if f["frontmatter"] and f["nchars"] < 900:
        adj -= 0.55
    elif f["frontmatter"]:
        adj -= 0.25
    if f["nchars"] < 420:
        adj -= 0.35
    if f["nwords"] < 60:
        adj -= 0.25

    # Indexes and transcripts cannot be rescued by prose quality.
    hi = 0.15 if arch in ("doc_index", "turn_mirror", "status_log", "auto_memory_index") else 1.30
    score = base + clamp(adj, -1.60, hi)

    if f["nwords"] < 25:
        score = min(score, 0.60)

    return clamp(score, 0.0, 4.0)


# --------------------------------------------------------------------------
# 4. Note refinement (baseline refine.py, folded in and extended)
# --------------------------------------------------------------------------
# Separates durable agent-written facts from status dumps. Applied to organic
# notes and Claude auto-memory topic files.

STATUS_OPENER = re.compile(
    r"^\s*(Done\b|Fixed\b|Verified\b|Merged\b|Pinned\b|Recorded\b|Rendered\b|All confirmed|"
    r"Status\b|Prep done|Shell ready|Task closed|Cleanup complete|Amended\b|Locked in|"
    r"Draft PR|Fresh verification|No code has changed|f: |Implementation in flight|"
    r"Extension complete|Rollout complete|Both\b|Ad-hoc verification|Render(?:\s|ing)|"
    r"Plan written|Software MoE|From\b|Here.s the current|PR[- #]|\*\*1\.|"
    r"Reviewer\b|Still working|All \d|Everything\b|Confirmed\b|Complete[d:]|Wrapped\b|"
    r"Update[d:]|Progress\b|Current state|Handoff\b|Session\b)",
    re.I,
)
STATUS_VOCAB = re.compile(
    r"\b(merged|pushed|re-pushed|in flight|worktree|pre-push|gate chain|full suite|"
    r"suite green|passed\b|skipped\b|exit 0|handed over|dispatched|still working|"
    r"waiting on|re-run|rebased|cherry-pick|draft PR|ready for review|cron job|"
    r"status board|deliverable|committed as|origin/main|HEAD|CI checks|"
    r"nothing left pending|checklist|all green|wrapped up|next step|remaining work)\b",
    re.I,
)
SECOND_PERSON = re.compile(r"\b(your|you'll|you can|as instructed|per your)\b", re.I)
TEST_COUNTS = re.compile(
    r"\b\d+\s*(passed|failed|skipped|errors|warnings|green|assertions?)\b|\bexit 0\b|\b\d+/\d+\b",
    re.I,
)
MEASURE = re.compile(r"\d+(?:\.\d+)?\s*(?:s|ms|x|%|lines?|KB|MB|GB|tokens?)\b")
DURABLE = re.compile(
    r"\[facts\]|\bREGRESSION\b|\bgotcha\b|\broot cause\b|\brequires?\b|\bmust\b|"
    r"\bnever\b|\balways\b|\bconvention\b|\bone \w+ per \w+|\bholds\b|\bby design\b|"
    r"\bcontract\b|\bprecedence\b|\bsemantics\b|\bfailure mode\b|\btrap\b|"
    r"\bthe fix was\b|\bfix was\b|\bworkaround\b|\bmitigations?\b",
    re.I,
)
DATED_FACT = re.compile(r"\(\d{4}-\d{2}-\d{2}\)[:,]")
COMMIT_HASH = re.compile(r"\b[0-9a-f]{7,10}\b")
# First line announces work completion ("Full analysis complete.", "X folded
# into the plan and committed") — a conversational status wrapper even when
# the body carries substance.
COMPLETION_OPEN = re.compile(
    r"^[^.\n]{0,70}\b(complete[d.]?|committed|folded into|landed|shipped|done[.:]|wrapped)\b",
    re.I,
)
PROPOSAL = re.compile(
    r"\b(proposed|proposal|recommendation|recommended|we should|"
    r"should (?:add|be|create|store|land)|option [A-C]\b|phase \d|next steps?)\b",
    re.I,
)


def refine_note(cand, base, f):
    v = cand.get("value") or ""
    nwords = len(v.split())
    d = 0.0

    n_status = len(STATUS_VOCAB.findall(v))
    opener = bool(STATUS_OPENER.search(v[:80]))
    if opener:
        d -= 1.2
    elif COMPLETION_OPEN.search(v.lstrip()[:80]):
        d -= 0.8
    d -= min(1.5, 0.15 * n_status)
    if SECOND_PERSON.search(v):
        d -= 0.5
    if len(COMMIT_HASH.findall(v)) >= 2:
        d -= 0.3

    # Test-count noise ("1367 passed / 0 failed") is not a measurement.
    real_measures = len(MEASURE.findall(TEST_COUNTS.sub(" ", v)))
    if real_measures >= 2:
        d += 0.5

    n_dur = len(DURABLE.findall(v))
    d += min(1.0, 0.35 * n_dur)
    if DATED_FACT.search(v[:120]):
        d += 0.5

    # Short definitional fact exemption.
    if nwords < 40 and n_dur >= 1 and n_status <= 1:
        base = max(base, 2.2)

    # A note that is one big table is a log or a test matrix, not a fact.
    if f["table_frac"] >= 0.8:
        d -= 0.9

    # Proposal vocabulary: not yet true, hence not yet durable.
    d -= min(0.9, 0.25 * len(PROPOSAL.findall(v)))

    score = base + max(-2.8, min(1.5, d))

    # A status-shaped note with no durable content is a progress report,
    # whatever its provenance prior said.
    if opener and n_dur == 0:
        score = min(score, 1.10)

    return score


# --------------------------------------------------------------------------
# 5. Entry point
# --------------------------------------------------------------------------


def score_candidate(cand):
    arch = archetype(cand)
    f = features(cand)
    score = base_score(cand, arch, f)
    if arch in NOTE_ARCHETYPES:
        score = refine_note(cand, score, f)
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
