#!/usr/bin/env python3
"""Promotion-usefulness scorer — round 1, lane C ("passage rhetoric").

Usage: python3 scorer.py <candidates.json>  ->  [{"id": ..., "score": ...}] on stdout

The model never looks at `path`, `source_file`, `project_id`, `rating`,
`access_count`, `created_at` or `id`.  It reads the chunk's own text and asks
two questions of it:

  1. Does this passage *make a claim*, and of what kind?  The chunk is split
     into rhetorical units (headings, fenced code, table rows, list items,
     sentences).  Each unit earns claim value for the grammar of explanation —
     deontic modals, causal/failure connectives, contrast, subordination,
     negation, a measurement with units — and loses it for the grammar of
     coordination — status vocabulary, task/gate/review vocabulary, links and
     cross-references, second-person instructions.
  2. Is the claim's *subject* portable?  Generic technology vocabulary lifts a
     claim; naming files and repo paths sinks it.  This is a multiplier on the
     claim, not an addend: an explanation of a local file is still local.

The chunk score is an order statistic over its units (best unit, runner-up,
mean) plus whole-chunk register corrections (length, sentence density,
parenthetical density, capitalised-token density, filename density,
coordination vocabulary), mapped through a logistic onto 0-4.

Deterministic, Python 3 standard library only, no file I/O beyond argv[1].
Everything is regex counting and arithmetic; it ports to C# unchanged.
"""

import json
import math
import re
import sys

# --------------------------------------------------------------------------
# Constants.  Hand-set on round numbers, deliberately NOT fitted: coordinate
# descent on the training set raised train rho and *lowered* cross-validated
# rho, so the grid search was discarded (see METHOD.md).
# --------------------------------------------------------------------------

K = {
    # unit aggregation
    "best": 1.00,          # weight of the strongest unit in the chunk
    "second": 0.30,        # weight of the runner-up unit
    "density": 0.50,       # weight of the mean unit value
    "bias": 0.35,
    # claim families (per unit)
    "w_norm": 0.95,        # deontic / rule language
    "w_mech": 0.95,        # causal + failure-mode language
    "w_contrast": 0.70,    # "not what you'd think" language
    "w_advice": 0.60,      # actionable remedy language
    "w_sub": 0.60,         # subordinating conjunctions
    "w_neg": 0.60,         # negation
    "w_meas": 0.85,        # measurement word AND a number with a unit
    "w_meas_soft": 0.40,   # one of the two
    # subject portability (multiplier on claim)
    "port_gain": 0.45,     # generic technology vocabulary
    "p_local": 0.45,       # filenames and repo paths
    "port_floor": 0.25,
    # non-claim registers (per unit, subtractive)
    "p_status": 0.60,
    "p_coord": 0.60,
    "p_pointer": 0.75,
    "p_instr": 0.50,
    # whole-chunk register corrections
    "w_len": 0.80,
    "w_sent": 0.50,
    "p_paren": 0.60,
    "p_caps": 0.60,
    "p_docname": 0.50,
    "reg_coord": 0.80,
    # 0-4 calibration
    "map_mid": 2.50,
    "map_width": 1.85,
}

# --------------------------------------------------------------------------
# Lexicon.  Every entry is generic English or generic software English; there
# is no term here that names a project, document, person or repository.
# --------------------------------------------------------------------------

# deontic / rule-giving language
NORM = re.compile(
    r"\b(must|never|always|cannot|can't|do not|don't|shall not|"
    r"required|requires?|forbidden|prefer(?:red|s)?|has to|have to|"
    r"only ever|not allowed|invariant|convention|contract|"
    r"by design|deliberate(?:ly)?|on purpose|non-negotiable|"
    r"the rule|rule of thumb|semantics|precedence)\b", re.I)

# causal and consequence connectives, plus verbs of silent breakage
MECH = re.compile(
    r"\b(because|since|so that|otherwise|which means|means that|"
    r"the reason|root cause|causes?|caused|leads? to|results? in|"
    r"silently|invisible|no error|without warning|"
    r"therefore|hence|as a result|breaks?|broke|defeats?|"
    r"swallow\w*|hides?|masks?|shadows?|overwrit\w+|resurrect\w*|"
    r"unless|until|whenever|vacuous\w*|no-?op|drops? the|"
    r"never (?:fires?|runs?|registers?|reaches?|leaves?))\b|→", re.I)

# the strongest single marker of a portable gotcha: a failure with no signal
FAILMODE = re.compile(
    r"\bfails? (?:open|closed|silently|identically)\b|"
    r"\bsilently\b|\bwith no error\b|\bno error\b|"
    r"\bnothing (?:happens|resolves|notices|checks)\b", re.I)

# "this is not what it looks like"
CONTRAST = re.compile(
    r"\b(counter-?intuitiv\w*|surprisingly|in fact|actually|"
    r"even though|despite|traps?|gotchas?|pitfalls?|caveats?|beware|"
    r"misleading|looks? like|reads? as|appears? to|not obvious|"
    r"non-obvious|the catch|worse than|instead of|rather than|"
    r"is not redundant|neither is obvious)\b", re.I)

# a remedy the reader can carry away
ADVICE = re.compile(
    r"\bhow to apply\b|\bthe fix is\b|\bthe workaround\b|"
    r"\bmitigat\w+\b|\buse\s+\w+\s*,?\s*not\b|\binstead\b|"
    r"\bcheck\s+\w+\s+(?:first|before)\b|\bprefer\b", re.I)

MEAS = re.compile(
    r"\b\d+(?:[.,]\d+)?\s?(?:ms|s|sec|secs|seconds|min|minutes|hours?|"
    r"mb|gb|kb|%|×|x|rps|qps|tokens?|lines?|bytes?)\b", re.I)
MEASWORD = re.compile(
    r"\b(measured|benchmark\w*|observed|reproduc\w+|"
    r"p9[59]|throughput|latency|median|baseline|best-of-\d|"
    r"ndcg|recall@|mrr|wall (?:clock|time))\b", re.I)

# the register of a status recap
STATUS = re.compile(
    r"\b(done|completed?|merged|pushed|re-pushed|passing|passed|"
    r"all tests|exit 0|green|clean working tree|in flight|"
    r"next steps?|todo|blocked|ready for review|committed|rebased|"
    r"landed|shipped|verified today|no blockers|remaining|"
    r"current state|status|recommendation|assessment)\b"
    r"|✅|❌|⏺|✓", re.I)
# per the rubric, a test count is status and not a measurement; it is removed
# before the measurement patterns run
TESTCOUNT = re.compile(
    r"\b\d+\s*(?:passed|failed|skipped|tests?|errors?|warnings?)\b|"
    r"\bexit 0\b|\b\d+/\d+\b", re.I)

# the register of in-flight coordination
COORD = re.compile(
    r"\b(wave \d|phase \d|lens [a-z]?\d?|worktree|acceptance criteria|"
    r"AC[- ]?\d|gates?\b|effort|owner (?:action|ruling|requirement)|"
    r"dispatch\w*|sub-?agents?|persona|backlog|deliverable|"
    r"out of scope|follow-?ups?|reviewer|checkpoint|handoff|"
    r"this (?:review|audit|task|plan|session|wave|lens|epic)|"
    r"severity|priority|verdict|scope)\b", re.I)
PRREF = re.compile(r"#\d{1,4}\b|\bPR \d|\bissue \d")
SEVERITY = re.compile(
    r"\b(?:high|medium|low|critical|certain|likely|blocker)\b\s*\|", re.I)

# the register of a pointer: this chunk tells you where something else is
LINK = re.compile(r"\]\(|\[\[")
SEEALSO = re.compile(
    r"\bsee (?:also|the|§)|\bfull (?:method|output|detail)|"
    r"\brecorded in\b|\bdocumented in\b|\bper §|§\s*\d|"
    r"\bADR-?\s?\d{3,4}\b|\bNFR-\w+|\brelated:", re.I)
FILEREF = re.compile(
    r"[\w/.\-]+\.(?:cs|ts|tsx|js|py|yml|yaml|json|razor|sql|sh|tf|md)"
    r"\b(?::\d+(?:-\d+)?)?")
PATHREF = re.compile(r"\b(?:src|tests?|infra|scripts|docs|engine|tooling)/[\w/.\-]+")

# second-person walkthroughs are project onboarding, not portable facts
INSTR = re.compile(r"\byou(?:'ll|'re|r)?\b|\bas instructed\b|^\s*\d+\.\s+[A-Z][a-z]+\b", re.I)

# an embedded tool-call transcript
TRANSCRIPT = re.compile(
    r"</?(?:invoke|parameter|antml:|function_calls|sourceFile)\b|"
    r"<invoke\s+name=|<parameter\s+name=", re.I)

SUBORD = re.compile(
    r"\b(because|which|that|so|if|when|where|unless|until|while|"
    r"although|though|whereas|since|rather|than|whether|before|"
    r"after|once|as|why|how)\b", re.I)
NEGATION = re.compile(
    r"\b(no|not|never|nothing|none|cannot|can't|don't|doesn't|"
    r"isn't|won't|without|neither|nor|fails?|failed|missing|"
    r"absent|nobody|nowhere)\b", re.I)

SENTEND = re.compile(r"[.!?](?:\s|$)")
PAREN = re.compile(r"\(")
WORD = re.compile(r"[A-Za-z][A-Za-z0-9_'-]*")
SENT_SPLIT = re.compile(r"(?<=[.!?])\s+(?=[A-Z`*\[(—])")

# Technology nouns whose meaning survives leaving this repo.  Used only to
# decide whether a claim is *about* something another project also has.  It is
# a fixed linguistic resource, not a learned table: no entry was added because
# a particular training row wanted it.
GENERIC = set("""
git github gitlab ci cd pipeline workflow workflows action actions runner cron docker container
kubernetes terraform azure aws gcp cloud serverless lambda function functions storage blob
queue cache redis sqlite postgres mysql database sql index migration schema table
http https rest grpc json yaml xml csv regex unicode encoding oauth token jwt tls ssl
cors csrf xss injection secret credential credentials api sdk cli shell bash zsh npm bun node python
dotnet nuget msbuild roslyn linq async await thread threadpool lock deadlock
memory allocation latency throughput timeout retry backoff idempotent race concurrency
browser dom css html react angular vue typescript javascript webpack vite bundle hydration
test tests testing mock stub fixture coverage mutation stryker xunit nunit pytest jest bdd
mcp llm prompt embedding vector tokenizer agent agents subagent
markdown frontmatter symlink filesystem path env environment config configuration
process exit signal stdout stderr stdin log logging telemetry metric trace span
gmail google oauth2 dns tcp port socket header cookie session cache-control
""".split())

# unit kinds that carry no claim of their own
SKIP = ("heading", "frontmatter", "code", "rule")


# --------------------------------------------------------------------------
# Segmentation
# --------------------------------------------------------------------------

def clip_transcript(value):
    """A chunk that opens on a tool-call transcript is a transcript.  One that
    reaches a transcript only after real prose is that prose plus noise."""
    m = TRANSCRIPT.search(value)
    if not m:
        return value, False
    if m.start() >= 300:
        return value[:m.start()], False
    return value, True


def sentences(text):
    parts = [p.strip() for p in SENT_SPLIT.split(text) if p.strip()]
    return parts or [text]


def units(value):
    """Split a chunk into rhetorical units, tagged by kind."""
    out = []
    lines = value.split("\n")
    i = 0

    # YAML frontmatter: keep only its description, which is a written summary
    if lines and lines[0].strip() == "---":
        j = 1
        while j < len(lines) and lines[j].strip() != "---":
            j += 1
        if j < len(lines):
            for ln in lines[1:j]:
                m = re.match(r"\s*description:\s*(.+)$", ln)
                if m:
                    out.append(("prose", m.group(1).strip().strip('"')))
            out.append(("frontmatter", "\n".join(lines[:j + 1])))
            i = j + 1

    in_fence = False
    fence = []
    para = []

    def flush():
        if para:
            text = " ".join(para).strip()
            del para[:]
            for s in sentences(text):
                out.append(("prose", s))

    while i < len(lines):
        line = lines[i]
        st = line.strip()

        if st.startswith("```"):
            if in_fence:
                fence.append(line)
                out.append(("code", "\n".join(fence)))
                fence = []
                in_fence = False
            else:
                flush()
                in_fence = True
                fence = [line]
            i += 1
            continue
        if in_fence:
            fence.append(line)
            i += 1
            continue
        if not st:
            flush()
            i += 1
            continue
        if st.startswith("#"):
            flush()
            out.append(("heading", st))
            i += 1
            continue
        if st.startswith("|"):
            flush()
            cells = [c.strip() for c in st.strip("|").split("|")]
            if all(re.fullmatch(r":?-{2,}:?", c or "-") for c in cells):
                out.append(("rule", st))
            else:
                out.append(("table", st))
            i += 1
            continue
        if re.match(r"^([-*+]|\d+\.)\s+", st):
            flush()
            buf = [st]
            i += 1
            while i < len(lines):
                nxt = lines[i]
                if not nxt.strip():
                    break
                if re.match(r"^\s*([-*+]|\d+\.)\s+", nxt):
                    break
                if nxt.strip().startswith(("#", "|", "```")):
                    break
                if not nxt.startswith((" ", "\t")):
                    break
                buf.append(nxt.strip())
                i += 1
            body = " ".join(buf)
            is_link = (re.match(r"^([-*+]|\d+\.)\s+\[[^\]]*\]\(", body)
                       or body.lstrip("-*+ ").startswith("[["))
            out.append(("link" if is_link else "item", body))
            continue

        para.append(st)
        i += 1

    if in_fence and fence:
        out.append(("code", "\n".join(fence)))
    flush()
    return out


# --------------------------------------------------------------------------
# Per-unit signals
# --------------------------------------------------------------------------

def unit_signals(text):
    words = WORD.findall(text)
    # per the rubric, a test count is status and not a measurement
    clean = TESTCOUNT.sub(" ", text)
    s = {
        "nw": len(words),
        "norm": len(NORM.findall(text)),
        "mech": len(MECH.findall(text)) + 2 * len(FAILMODE.findall(text)),
        "contrast": len(CONTRAST.findall(text)),
        "advice": len(ADVICE.findall(text)),
        "meas": len(MEAS.findall(clean)),
        "measword": len(MEASWORD.findall(clean)),
        "status": len(STATUS.findall(text)) + len(TESTCOUNT.findall(text)),
        "coord": (len(COORD.findall(text)) + len(PRREF.findall(text))
                  + len(SEVERITY.findall(text))),
        "pointer": len(LINK.findall(text)) + len(SEEALSO.findall(text)),
        "fileref": len(FILEREF.findall(text)) + len(PATHREF.findall(text)),
        "instr": len(INSTR.findall(text)),
        "sub": len(SUBORD.findall(text)),
        "neg": len(NEGATION.findall(text)),
        "sent": len(SENTEND.findall(text)),
        "paren": len(PAREN.findall(text)),
        "caps": sum(1 for w in words if w[:1].isupper()),
    }
    s["generic"] = len(set(w.lower() for w in words if w.lower() in GENERIC))
    return s


def unit_value(kind, s, k):
    """Claim value of one unit, or None if the unit cannot carry a claim."""
    if kind in SKIP or s["nw"] < 5:
        return None
    if kind == "link":
        return -0.6

    # counts per 100 words, with a floor on the denominator so that a short
    # unit cannot manufacture a huge density from one hit
    dens = 100.0 / max(s["nw"], 14)

    claim = 0.0
    claim += k["w_norm"] * min(s["norm"] * dens / 4.0, 1.2)
    claim += k["w_mech"] * min(s["mech"] * dens / 4.0, 1.3)
    claim += k["w_contrast"] * min(s["contrast"] * dens / 4.0, 1.0)
    claim += k["w_advice"] * min(s["advice"] * dens / 4.0, 0.8)
    claim += k["w_sub"] * min(s["sub"] * dens / 12.0, 1.2)
    claim += k["w_neg"] * min(s["neg"] * dens / 8.0, 1.2)
    if s["meas"] and s["measword"]:
        claim += k["w_meas"]
    elif s["meas"] >= 2 or s["measword"]:
        claim += k["w_meas_soft"]

    port = (1.0
            + k["port_gain"] * min(s["generic"] * dens / 10.0, 1.0)
            - k["p_local"] * min(s["fileref"] * dens / 6.0, 1.4))
    port = max(k["port_floor"], port)

    noise = 0.0
    noise += k["p_status"] * min(s["status"] * dens / 4.0, 1.5)
    noise += k["p_coord"] * min(s["coord"] * dens / 4.0, 1.5)
    noise += k["p_pointer"] * min(s["pointer"] * dens / 4.0, 1.2)
    noise += k["p_instr"] * min(s["instr"] * dens / 4.0, 1.0)

    return claim * port - noise


# --------------------------------------------------------------------------
# Chunk score
# --------------------------------------------------------------------------

def score_candidate(cand, k=K):
    value, is_transcript = clip_transcript(cand.get("value") or "")
    if is_transcript:
        return 0.15

    parts = [(kind, unit_signals(text)) for kind, text in units(value)]

    vals = []
    words = 0
    coord = 0
    for kind, s in parts:
        words += s["nw"]
        coord += s["coord"]
        v = unit_value(kind, s, k)
        if v is not None:
            vals.append(v)
    if not vals:
        return 0.20

    vals.sort(reverse=True)
    best = vals[0]
    second = vals[1] if len(vals) > 1 else 0.0
    mean = sum(vals) / len(vals)
    raw = k["bias"] + k["best"] * best + k["second"] * second + k["density"] * mean

    nw = max(words, 1)
    # a chunk cut short by the chunker is usually a heading or a fragment
    raw += k["w_len"] * clamp(math.log((words + 20.0) / 110.0), -1.2, 1.0)
    # finished sentences per 100 words: prose asserts, tables and lists enumerate
    raw += k["w_sent"] * clamp((100.0 * sum(s["sent"] for _, s in parts) / nw - 4.5) / 2.5, -1.0, 1.0)
    # parentheses mark asides, citations and hedges
    raw -= k["p_paren"] * min(100.0 * sum(s["paren"] for _, s in parts) / nw / 4.0, 1.2)
    # capitalised tokens mark proper nouns, headings and grid cells
    raw -= k["p_caps"] * min(100.0 * sum(s["caps"] for _, s in parts) / nw / 14.0, 1.2)
    # a chunk that mostly names documents is a pointer
    raw -= k["p_docname"] * min(100.0 * sum(s["fileref"] for _, s in parts) / nw / 4.0, 1.2)
    # coordination vocabulary anywhere in the chunk colours the whole chunk
    raw -= k["reg_coord"] * min(coord * (100.0 / max(words, 40)) / 4.0, 1.0)

    return squash(raw, k)


def clamp(x, lo, hi):
    return lo if x < lo else (hi if x > hi else x)


def squash(raw, k):
    """Map an unbounded raw score onto 0-4.  Monotone, so it does not change
    the ranking; it fixes where the 0.4 promotion floor falls."""
    z = (raw - k["map_mid"]) / k["map_width"]
    if z < -30.0:
        return 0.0
    if z > 30.0:
        return 4.0
    return 4.0 / (1.0 + math.exp(-z))


def main(argv):
    if len(argv) != 2:
        sys.stderr.write("usage: scorer.py <candidates.json>\n")
        return 2
    with open(argv[1], "r", encoding="utf-8") as fh:
        candidates = json.load(fh)
    out = [{"id": int(c["id"]), "score": round(float(score_candidate(c)), 4)}
           for c in candidates]
    json.dump(out, sys.stdout)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
