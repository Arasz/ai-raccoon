#!/usr/bin/env python3
"""Promotion-usefulness scorer (lane B): score the speech acts, not the provenance.

    python3 scorer.py <candidates.json>   ->  [{"id": int, "score": float}, ...]

The model never reads `path`, `source_file`, `project_id`, `rating`, `access_count`,
`created_at` or `id`. It splits the entry text into *units* (paragraphs, bullet items,
sentences), asks of each unit "what kind of utterance is this?", and combines the
strongest units with a handful of whole-chunk artifact detectors.

Standard library only. Deterministic. No I/O beyond the input path.
Portable to C#: regex counting plus arithmetic.
"""

import json
import re
import sys

# --------------------------------------------------------------------------------------
# Tunable constants. Everything the model can be perturbed on lives here.
# --------------------------------------------------------------------------------------

# Unit-level speech-act weights: how much each cue family moves one unit's claim strength.
U = {
    "prose": 1.0,    # is this connected English at all
    "claim": 0.6,    # negation / contrast — an assertion that rules something out
    "gotcha": 0.8,   # surprising behaviour, silent failure, root cause
    "norm": 0.8,     # a rule: must / never / always / is not
    "causal": 0.8,   # a mechanism: because / so that / which means
    "measure": 0.5,  # a measured quantity with units
    "advice": 0.4,   # applicable instruction addressed to a reader
    "status": 0.4,   # (penalty) progress report
    "ledger": 1.0,   # (penalty) plan / finding-table bookkeeping
    "cell": 0.8,     # (penalty) the unit is a table row
}

# Chunk-level combination. Fitted by ridge regression on 228 labelled rows.
W = {
    "bias": 0.48,
    "top2": 0.51,      # mean strength of the two strongest units
    "volume": 0.75,    # how much claim-bearing text there is at all
    "length": 0.26,    # raw size, capped
    "links": -0.54,    # pointer density -> index / table of contents
    "frontmatter": -0.44,
    "xrefs": -0.38,    # cross-references per unit -> a fragment of a local argument
    "startsbullet": -0.50,
}

# Shape constants.
PROSE_FULL = 0.42     # function-word fraction at which a unit counts as full prose
UNIT_MIN_WORDS = 8    # below this a unit is not judged
VOLUME_FULL = 8.0     # summed unit strength that counts as "plenty of claims"
LENGTH_FULL = 170.0   # words at which the length term saturates
LINK_HALF = 1.0       # links per 100 words at which the link penalty is half-strength
XREF_HALF = 0.5       # cross-refs per unit at which the xref penalty is half-strength
FRONTMATTER_FULL = 3.0

# Calibration: the ridge fit is shrunk toward the mean, so stretch it back onto the
# 0-4 label scale (a monotone map — it changes the floor decision, never the ranking).
CAL_CENTER = 1.39   # train label mean
CAL_SPREAD = 1.62   # train label sd / raw-score sd (variance matching)
CAL_TOE = 0.10       # below this the scale compresses instead of clamping, so the
CAL_TOE_RATE = 10.0  # worst entries stay distinguishable from each other

# --------------------------------------------------------------------------------------
# Lexicons
# --------------------------------------------------------------------------------------

STOP = {
    "the", "a", "an", "of", "to", "in", "is", "it", "that", "and", "for", "on", "as",
    "with", "not", "but", "this", "are", "be", "by", "from", "at", "or", "was", "were",
    "has", "have", "had", "which", "they", "their", "its", "than", "then", "so", "if",
    "when", "what", "no", "all", "one", "can", "do", "does",
}

WORD = re.compile(r"[A-Za-z][A-Za-z'\-]*")


def _any(patterns):
    return re.compile("|".join(patterns), re.I)


RE_NEG = _any([
    r"\bnot\b", r"\bnever\b", r"\bnothing\b", r"\bno\b", r"\bcannot\b", r"\bcan't\b",
    r"\bdoesn't\b", r"\bdon't\b", r"\bwithout\b", r"\bneither\b", r"\bnor\b",
])
RE_CONTRAST = _any([
    r"\brather than\b", r"\binstead of\b", r"\binstead\b", r"\bbut\b", r"\bhowever\b",
    r"\bthough\b", r"\bwhereas\b", r"\bonly\b", r"\bunless\b", r"\byet\b", r"\bactually\b",
])
RE_NORM = _any([
    r"\bmust\b", r"\balways\b", r"\bnever\b", r"\bdo not\b", r"\bshould\b", r"\brequires?\b",
    r"\bprefer\b", r"\bhas to\b", r"\bhave to\b", r"\bis not\b", r"\bare not\b",
])
RE_GOTCHA = _any([
    r"\bsilent(?:ly)?\b", r"\btraps?\b", r"\broot cause\b", r"\bgotcha", r"\bbeware\b",
    r"\bsurpris\w*", r"\bnon-obvious\b", r"\bvacuous\w*", r"\bmisleading\b", r"\blies\b",
    r"\bin fact\b", r"\bturns out\b", r"\bcounter-?intuitiv\w*", r"\bsubtl\w+",
    r"\bno error\b", r"\bwithout (?:an? )?error\b",
])
RE_CAUSAL = _any([
    r"\bbecause\b", r"\bso that\b", r"\btherefore\b", r"\bwhich means\b", r"\bthe reason\b",
    r"\bwhy\b", r"\bsince\b", r"\bhence\b", r"\bcauses?\b", r"\bcaused\b",
])
RE_MEASURE = _any([
    r"\bmeasured\b", r"\bbenchmark\w*", r"\bmedian\b", r"\bbest-of\b", r"\bwall(?:-| )time\b",
    r"\breproduc\w+", r"\bobserved\b",
])
RE_UNIT = re.compile(r"\d+(?:[.,]\d+)?\s?(?:ms\b|s\b|MB\b|GB\b|kB\b|KB\b|MiB\b|%|×)")
RE_ADVICE = _any([
    r"\bhow to apply\b", r"\buse \w", r"\bcheck \w", r"\bprefer \w", r"\bavoid \w",
    r"\bnever \w", r"\balways \w", r"\bdo not \w", r"\bmake sure\b", r"\bnote that\b",
])
RE_STATUS = _any([
    r"\bcomplete(?:d)?\b", r"\bpassing\b", r"\bexit 0\b", r"\bin progress\b", r"\bwaiting\b",
    r"\bnext session\b", r"\bstatus\b", r"\bpushed\b", r"\bdispatched\b", r"\bdraft pr\b",
    r"\bwip\b", r"\btodo\b", r"\bremaining\b", r"\bstill open\b", r"\bship(?:ped)?\b",
    r"\bverified\b", r"\bdone\b", r"\bsession\b",
])
RE_LEDGER = _any([
    r"\bwp-?\s?\d", r"\bac:\b", r"\bgate:\b", r"\bdepends on\b", r"\beffort\b", r"\bwave \d",
    r"\bscope:\b", r"\bgoal:\b", r"\bfollow-?ups?\b", r"\bbacklog\b", r"\bnext steps?\b",
    r"\bphase \d", r"\bowner action\b", r"\bseverity\b", r"\bpriority\b", r"\bfindings?\b",
    r"\brecommend\w*",
])

RE_LINK = re.compile(r"\]\(")
RE_XREF = re.compile(r"ADR-?\s?\d{3,4}|§|#\d{2,4}\b|\b[A-Z]\d{1,2}\b|\bNFR-|\bFR-")
RE_FRONTMATTER = re.compile(
    r"^\s*---\s*$|^\s*(?:updated|version|modified|originSessionId|node_type|name|"
    r"description|title|type|status|date|deciders?|author|supersedes)\s*:", re.M)
RE_UNIT_SPLIT = re.compile(r"\n\s*\n|\n(?=\s*(?:[-*+]|\d+\.)\s)|(?<=[.!?])\s+")
RE_STARTS_BULLET = re.compile(r"\s*(?:[-*+]|\d+\.)\s")


# --------------------------------------------------------------------------------------
# Model
# --------------------------------------------------------------------------------------

def saturate(x, half):
    """0 at x=0, 0.5 at x=half, ->1 as x grows. Keeps outliers from dominating."""
    return x / (x + half) if x > 0 else 0.0


def split_units(text):
    """A unit is one utterance: a paragraph, a bullet item, or a sentence."""
    parts = RE_UNIT_SPLIT.split(text)
    return [p.strip() for p in parts if len(WORD.findall(p)) >= UNIT_MIN_WORDS]


def unit_strength(unit, u=U):
    """How strongly does this unit assert a portable claim?

    Positive: it reads as English, it rules something out, it names a surprise, it
    states a rule, it gives a mechanism, it carries a measured quantity, it tells the
    reader what to do. Negative: it reports progress, it does plan bookkeeping, or it
    is a table row rather than an utterance.
    """
    words = WORD.findall(unit)
    n = max(1, len(words))
    stop_frac = sum(1 for w in words if w.lower() in STOP) / n

    v = u["prose"] * min(1.0, stop_frac / PROSE_FULL)
    v += u["claim"] * min(1.0, (len(RE_NEG.findall(unit)) + len(RE_CONTRAST.findall(unit))) / 3.0)
    v += u["gotcha"] * min(1.0, len(RE_GOTCHA.findall(unit)))
    v += u["norm"] * min(1.0, len(RE_NORM.findall(unit)) / 2.0)
    v += u["causal"] * min(1.0, len(RE_CAUSAL.findall(unit)))
    v += u["measure"] * min(1.0, (len(RE_MEASURE.findall(unit)) + len(RE_UNIT.findall(unit))) / 2.0)
    v += u["advice"] * min(1.0, len(RE_ADVICE.findall(unit)) / 2.0)
    v -= u["status"] * min(1.0, len(RE_STATUS.findall(unit)) / 2.0)
    v -= u["ledger"] * min(1.0, len(RE_LEDGER.findall(unit)) / 2.0)
    if unit.lstrip().startswith("|"):
        v -= u["cell"]
    return v


def chunk_features(text, u=U):
    text = text or ""
    words = WORD.findall(text)
    n_words = max(1, len(words))
    units = split_units(text)
    strengths = sorted((unit_strength(x, u) for x in units), reverse=True) or [0.0]

    top_k = strengths[:2]
    return {
        "top2": sum(top_k) / len(top_k),
        "volume": min(1.0, sum(s for s in strengths if s > 0) / VOLUME_FULL),
        "length": min(1.0, n_words / LENGTH_FULL),
        "links": saturate(100.0 * len(RE_LINK.findall(text)) / n_words, LINK_HALF),
        "frontmatter": min(1.0, len(RE_FRONTMATTER.findall(text)) / FRONTMATTER_FULL),
        "xrefs": saturate(len(RE_XREF.findall(text)) / max(1, len(units)), XREF_HALF),
        "startsbullet": 1.0 if RE_STARTS_BULLET.match(text) else 0.0,
    }


def score_text(text, u=U, w=W, center=CAL_CENTER, spread=CAL_SPREAD, toe=CAL_TOE):
    f = chunk_features(text, u)
    raw = w["bias"] + sum(w[k] * f[k] for k in f)
    s = center + (raw - center) * spread
    if s < toe:
        # Monotone soft floor: compresses toward 0 without ever tying two entries.
        s = toe / (1.0 + CAL_TOE_RATE * (toe - s))
    return min(4.0, s)


def main(argv):
    if len(argv) != 2:
        sys.stderr.write("usage: scorer.py <candidates.json>\n")
        return 2
    with open(argv[1], "r", encoding="utf-8") as fh:
        candidates = json.load(fh)
    out = [{"id": c["id"], "score": round(score_text(c.get("value") or ""), 4)}
           for c in candidates]
    json.dump(out, sys.stdout)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
