#!/usr/bin/env python3
"""Speech-act evidence-grammar scorer for shared-tier promotion candidates.

Usage: python3 scorer.py <candidates.json>  ->  stdout JSON [{"id": int, "score": float}, ...]

Design: an entry is scored by WHAT KIND OF SPEECH it is, not which archetype
bucket it falls into. Nineteen generic signals — durability grammar (modal
contracts, causal mechanism, failure/gotcha vocabulary, measurement citations,
file:line evidence), narration grammar (past-tense ratio, status/progress
vocabulary, PR references, plan scaffolding, metadata headers, future-tense
backlog), structure shape (bullet/table/link-list fraction, code fences), and
provenance (organic write citing a source, ADR path, session-log path) — are
z-scored against corpus statistics and summed with fixed signs. No fitted
weights, no id lookups, no candidate-specific strings. Deterministic, stdlib only.
"""
import json
import math
import re
import sys

_HEX64 = re.compile(r'^[0-9a-f]{64}\.md$')

RE_STATUS = re.compile(
    r'\b(merged|committed|pushed|shipped|dispatched|landed|drained|closed|reopened|'
    r'in flight|still (?:waiting|working|grinding|running)|will land|'
    r'next steps?|work completed|this session|status recap|interim status|'
    r'in progress|waiting on|kicked off|under way|underway)\b', re.I)
RE_PR_REF = re.compile(r'\bPR ?#\d+|\(#\d+\)')
RE_PLAN = re.compile(
    r'^\s*(?:\*\*)?(AC|Gate|Scope|Deliverables?|Depends on|Wave \d|Phase \d|Step \d|'
    r'Quality gate|Acceptance|Rollback|Owner|Estimate)\b', re.I)
RE_TABLE_ROW = re.compile(r'^\s*\|', re.M)
RE_META_HDR = re.compile(
    r'^\s*(?:\*\*)?(Reviewer|Date|Author|Base|Head|Commits? reviewed|Branch|Graph|Task)'
    r'(?:\*\*)?\s*:', re.I | re.M)
RE_MODAL = re.compile(
    r"\b(must|never|always|cannot|can't|only if|requires?|do(?:es)? not|doesn't|"
    r"won't|will not|has to|needs? to)\b", re.I)
RE_CAUSAL = re.compile(
    r'\b(because|so that|therefore|hence|which means|otherwise|unless|due to|'
    r'the reason|root cause|mechanism|consequences?)\b', re.I)
RE_FAILURE = re.compile(
    r'\b(fails?|failed|breaks?|broken|silently|corrupt\w*|wrong|bug|crash\w*|'
    r'hangs?|leak\w*|exit \d+|exit code|no error|reads as|masks?|starvation|'
    r'stale|drifts?|mismatch)\b', re.I)
RE_MEASURE = re.compile(
    r'\b(measured|benchmark\w*|probes?|probed|verified|reproduced|evidence|'
    r'observed|profil\w+|\[MEASURED\]|\[READ\]|\[INFERRED\])\b', re.I)
RE_FILELINE = re.compile(r'[\w./-]+\.(?:cs|py|md|json|yml|yaml|ts|tsx|sh|toml|csproj):\d+')
RE_PAST_VERB = re.compile(r'\b\w{3,}ed\b')
RE_PRESENT_COPULA = re.compile(r'\b(is|are|has|have|does|do|means|lives|resolves|'
                               r'returns|reads|carries|exists|applies|defaults)\b')
RE_FUTURE = re.compile(r"\b(will|pending|remaining|upcoming|to be done|not yet|todo)\b", re.I)
RE_CODEFENCE = re.compile(r'^```', re.M)

# Feature order is load-bearing: it matches MU/SD/SIGNS below.
# MU/SD are corpus statistics (mean/stddev over the 292-candidate corpus),
# not per-candidate data. SIGNS are domain-assigned (+durability, -narration).
NAMES = ['d_modal', 'd_causal', 'd_failure', 'd_measure', 'd_fileline',
         'organic_cited', 'pk_adr', 'past_ratio', 'd_status', 'd_plan',
         'bullet_frac', 'pk_session', 'pk_remember', 'd_future', 'd_prref',
         'd_metahdr', 'd_tablerow', 'd_codefence', 'pk_readme']
SIGNS = [1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, -1]
MU = [0.7936119627238504, 0.1691321223540896, 0.7653712876784883,
      0.7216487747961793, 0.2009808115985464, 0.09246575342465753,
      0.030821917808219176, 0.6767004492113136, 0.7416692424432475,
      0.018009598198404975, 0.3975708622469246, 0.06164383561643835,
      0.03767123287671233, 0.2042455321435273, 0.3497039477633745,
      0.012402038600668738, 0.11092779936958018, 0.14681615602995118,
      0.04452054794520548]
SD = [1.049986425448677, 0.44640544830826584, 1.271987582925799,
      1.186071464659792, 0.7421200417388353, 0.2896823050658565,
      0.17283497097185674, 0.25007825602989525, 1.4790955394342407,
      0.07199243333715138, 0.33767865163391503, 0.2405075324120471,
      0.1903998715605156, 0.5236479978754268, 0.9090180419329794,
      0.050032354873974456, 0.2808880770999461, 0.6478121721375973,
      0.2062485606152541]


def extract(entry):
    text = entry.get('value') or ''
    path = (entry.get('path') or '').lower()
    fname = path.rsplit('/', 1)[-1] if path else ''
    source = entry.get('source_file')
    lines = [l for l in text.split('\n') if l.strip()]
    nlines = max(len(lines), 1)
    kc = max(len(text), 1) / 1000.0

    def dens(rx):
        return len(rx.findall(text)) / kc

    organic = source is None
    hashconv = bool(_HEX64.match(fname))
    organic_cited = 1.0 if (organic and path and not hashconv) else 0.0
    pk_session = 1.0 if (('/memory/' in path or '/.remember/' in path) and
                         re.search(r'(status|session|today|phase|rescan|continued|done)',
                                   fname)) else 0.0
    pk_remember = 1.0 if '/.remember/' in path else 0.0
    pk_adr = 1.0 if '/adr/' in path else 0.0
    pk_readme = 1.0 if fname == 'readme.md' else 0.0

    past = len(RE_PAST_VERB.findall(text))
    pres = len(RE_PRESENT_COPULA.findall(text))
    past_ratio = past / max(past + pres, 1)
    bullet_frac = sum(1 for l in lines if l.lstrip().startswith(('-', '*', '|'))) / nlines
    plan_frac = sum(1 for l in lines if RE_PLAN.match(l)) / nlines

    return [dens(RE_MODAL), dens(RE_CAUSAL), dens(RE_FAILURE), dens(RE_MEASURE),
            dens(RE_FILELINE), organic_cited, pk_adr, past_ratio,
            dens(RE_STATUS), plan_frac, bullet_frac, pk_session, pk_remember,
            dens(RE_FUTURE), dens(RE_PR_REF),
            len(RE_META_HDR.findall(text)) / nlines,
            len(RE_TABLE_ROW.findall(text)) / nlines,
            dens(RE_CODEFENCE), pk_readme]


def score(entry):
    f = extract(entry)
    return sum(SIGNS[j] * (f[j] - MU[j]) / SD[j] for j in range(len(NAMES)))


def main():
    if len(sys.argv) != 2:
        print('usage: python3 scorer.py <candidates.json>', file=sys.stderr)
        return 2
    with open(sys.argv[1]) as fh:
        candidates = json.load(fh)
    out = [{'id': c['id'], 'score': round(score(c), 6)} for c in candidates]
    json.dump(out, sys.stdout)
    print()
    return 0


if __name__ == '__main__':
    sys.exit(main())
