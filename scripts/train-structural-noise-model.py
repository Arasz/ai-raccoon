#!/usr/bin/env python3
"""Trains and exports the structural-noise detector shipped in
src/AiRaccoon.Core/Memory/QueryGuard/Structural/ (docs/adr/0041).

Not part of any CI pipeline: it needs the research record's read-path corpus
(gen2/queries.jsonl, 24 real tool/agent transcripts' worth of noise + real memory-bank
signal), which is not checked into this repository — see the research record referenced
from docs/adr/0041 for how that corpus was built and where it lives. Re-run this only to
recalibrate against a *different* corpus (e.g. this deployment's own query stream) or to
retrain after a genuine change to the feature set.

Requires scikit-learn == 1.9.0, numpy == 2.5.1 (the versions the record's own numbers were
measured against) and the QUERIES_JSONL / QUERIES_FEAT_JSONL corpus files.

Usage:
    python3 scripts/train-structural-noise-model.py <path-to-gen2-dir> [output-dir]

Emits, into <output-dir> (default: this script's directory):
    StructuralNoiseModel.Trees.g.cs   -- copy into src/AiRaccoon.Core/Memory/QueryGuard/Structural/
    held-out-reproduction-fixture.json -- copy into the matching tests/ assets/ directory
    model.json                         -- raw trained model + measured numbers, for the ADR
"""
from __future__ import annotations

import json
import math
import re
import sys
from pathlib import Path

import numpy as np
from sklearn.ensemble import HistGradientBoostingClassifier
from sklearn.metrics import roc_auc_score
from sklearn.model_selection import StratifiedKFold, cross_val_predict

# ---------------------------------------------------------------------------
# The 30-feature extractor: the research record's 31 shape statistics
# (gen2/features.py) minus compress_ratio (F5/F7 — free to drop, removes the
# only allocating step). Kept in lockstep with
# src/AiRaccoon.Core/Memory/QueryGuard/Structural/StructuralNoiseFeatures.cs;
# that C# port is parity-tested against this Python reference
# (StructuralNoiseFeaturesTests.Extract_MatchesThePythonReferenceImplementation).
# ---------------------------------------------------------------------------
ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")
TS = re.compile(r"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}|\d{2}:\d{2}:\d{2}[.,]?\d*|\d{4}/\d{2}/\d{2}")
HEXRUN = re.compile(r"\b[0-9a-fA-F]{8,}\b|\b[A-Za-z0-9+/]{24,}={0,2}\b")
PATHTOK = re.compile(r"[/\\][\w.\-]+[/\\]|^[/~.]?[\w.\-]*[/\\][\w.\-/\\]+$")
URL = re.compile(r"https?://|\bwww\.")
SEPLINE = re.compile(r"^[\s\-=_*#~+|.]{6,}$")
STACKFRAME = re.compile(r'^\s+(at |File "|from |in )')
KVLINE = re.compile(r"^\s*[\w.\-\[\]]+\s*[:=]\s*\S")
TOKEN = re.compile(r"[A-Za-z]+(?:'[A-Za-z]+)?|\S")
WORD = re.compile(r"^[a-z]+$")

PRONOUNS = {"i", "we", "you", "my", "our", "your", "me", "us", "i'm", "we're",
            "you're", "i've", "we've", "mine", "yours", "ours"}
STOPWORDS = {"the", "a", "an", "is", "are", "was", "were", "be", "been", "to",
             "of", "and", "or", "but", "in", "on", "for", "with", "that",
             "this", "it", "as", "at", "by", "from", "not", "no", "so", "if",
             "then", "than", "because", "which", "what", "when", "how", "why",
             "do", "does", "did", "has", "have", "had", "can", "could",
             "should", "would", "will", "there", "their", "they", "its"}

FEATURE_NAMES = [
    "log_chars", "n_lines", "mean_line_len", "cv_line_len", "max_line_len_norm",
    "frac_lines_indented", "frac_lines_dup", "frac_sep_lines", "frac_kv_lines",
    "frac_stack_frames", "punct_ratio", "digit_ratio", "upper_ratio",
    "space_ratio", "bracket_ratio", "slash_ratio", "type_token_ratio",
    "mean_token_len", "frac_word_tokens", "frac_dirty_tokens",
    "path_token_ratio", "ansi_density", "ts_density", "hexrun_density",
    "url_density", "pronoun_ratio", "stopword_ratio", "sentence_end_ratio",
    "char_entropy", "mean_words_per_line",
]  # 30 -- compress_ratio dropped (F5/F7)


def extract30(text: str) -> list[float]:
    n = len(text)
    if n == 0:
        return [0.0] * len(FEATURE_NAMES)
    lines = text.split("\n")
    nl = len(lines)
    lens = [len(line) for line in lines]
    mean_len = sum(lens) / nl
    var = sum((x - mean_len) ** 2 for x in lens) / nl
    cv = (math.sqrt(var) / mean_len) if mean_len > 0 else 0.0

    indented = sum(1 for line in lines if line[:1] in (" ", "\t"))
    dup = nl - len(set(lines))
    sep = sum(1 for line in lines if SEPLINE.match(line))
    kv = sum(1 for line in lines if KVLINE.match(line))
    sf = sum(1 for line in lines if STACKFRAME.match(line))

    punct = digits = upper = space = brack = slash = 0
    for ch in text:
        if ch.isspace():
            space += 1
        elif ch.isdigit():
            digits += 1
        elif not ch.isalnum():
            punct += 1
            if ch in "{}[]()<>":
                brack += 1
            elif ch in "/\\":
                slash += 1
        if ch.isupper():
            upper += 1

    toks = TOKEN.findall(text)
    nt = max(len(toks), 1)
    lower = [t.lower() for t in toks]
    ttr = len(set(lower)) / nt
    mean_tok = sum(len(t) for t in toks) / nt
    word_toks = sum(1 for t in lower if WORD.match(t))
    dirty = sum(1 for t in toks if not t.isalpha())
    ws = text.split()
    pathtoks = sum(1 for w in ws if PATHTOK.search(w))
    npaths = pathtoks / max(len(ws), 1)

    ansi = len(ANSI.findall(text)) / nl
    ts = len(TS.findall(text)) / nl
    hexrun = len(HEXRUN.findall(text)) / nl
    url = len(URL.findall(text)) / nl

    pron = sum(1 for t in lower if t in PRONOUNS) / nt
    stop = sum(1 for t in lower if t in STOPWORDS) / nt
    sent = (text.count(". ") + text.count(".\n") + text.count("? ") + text.count("! ")) / nt

    from collections import Counter
    cnt = Counter(text)
    ent = -sum((c / n) * math.log2(c / n) for c in cnt.values())

    words_per_line = nt / nl

    return [
        math.log1p(n), math.log1p(nl), mean_len, cv, math.log1p(max(lens)),
        indented / nl, dup / nl, sep / nl, kv / nl, sf / nl,
        punct / n, digits / n, upper / n, space / n, brack / n, slash / n,
        ttr, mean_tok, word_toks / nt, dirty / nt, npaths,
        ansi, ts, hexrun, url, pron, stop, sent,
        ent, words_per_line,
    ]


def gbm() -> HistGradientBoostingClassifier:
    # Hyperparameters cited throughout docs/adr/0041 and the research record (F3).
    return HistGradientBoostingClassifier(max_iter=200, max_depth=4, learning_rate=0.1, random_state=0)


def thr_at(scores_sorted_desc: np.ndarray, target: float) -> float:
    """Out-of-fold calibration (gen2/lofo2.py thr_at) — ported to C# as StructuralNoiseCalibrator."""
    k = int(np.floor(target * len(scores_sorted_desc)))
    if k <= 0:
        return float(scores_sorted_desc[0]) + 1e-9
    return float(scores_sorted_desc[k - 1]) + 1e-12


def export_csharp_trees(model: HistGradientBoostingClassifier, out_path: Path) -> int:
    baseline = float(model._baseline_prediction.ravel()[0])
    tree_start, feature_idx, threshold, left, right, is_leaf, value = [0], [], [], [], [], [], []
    for it in model._predictors:
        nodes = it[0].nodes
        feature_idx.extend(int(x) for x in nodes["feature_idx"])
        threshold.extend(float(x) for x in nodes["num_threshold"])
        left.extend(int(x) for x in nodes["left"])
        right.extend(int(x) for x in nodes["right"])
        is_leaf.extend(1 if bool(x) else 0 for x in nodes["is_leaf"])
        value.extend(float(x) for x in nodes["value"])
        tree_start.append(len(value))

    def fmt(name: str, vals: list, is_double: bool, per_line: int) -> str:
        t = "double" if is_double else "int"
        parts = [repr(v) for v in vals] if is_double else [str(v) for v in vals]
        body = "\n".join("        " + ", ".join(parts[i:i + per_line]) + ","
                         for i in range(0, len(parts), per_line))
        return f"    private static readonly {t}[] {name} =\n    [\n{body}\n    ];\n"

    lines = [
        "// <auto-generated>",
        "// Generated by scripts/train-structural-noise-model.py -- see docs/adr/0041.",
        "// DO NOT EDIT BY HAND.",
        "// </auto-generated>",
        "namespace AiRaccoon.Core.Memory.QueryGuard.Structural;",
        "",
        "public static partial class StructuralNoiseModel",
        "{",
        f"    private const int TreeCount = {len(model._predictors)};",
        f"    private const double Baseline = {baseline!r};",
        "",
        fmt("TreeStart", tree_start, False, 16),
        fmt("FeatureIdx", feature_idx, False, 16),
        fmt("Threshold", threshold, True, 12),
        fmt("Left", left, False, 16),
        fmt("Right", right, False, 16),
        fmt("IsLeaf", is_leaf, False, 16),
        fmt("Value", value, True, 12),
        "}",
    ]
    out_path.write_text("\n".join(lines) + "\n")
    return len(value)


def main() -> None:
    gen2 = Path(sys.argv[1] if len(sys.argv) > 1 else "gen2")
    out = Path(sys.argv[2] if len(sys.argv) > 2 else Path(__file__).resolve().parent)

    rows = [json.loads(l) for l in (gen2 / "queries.jsonl").open()]
    X = np.array([extract30(r["text"]) for r in rows], dtype=np.float64)
    X = np.nan_to_num(X, nan=0.0, posinf=0.0, neginf=0.0)
    y = np.array([r["cls"] for r in rows])
    fam = np.array([r["family"] for r in rows])

    # 85/15 stratified-by-family split, held out BEFORE the shipped model is fit -- the 15% is
    # never seen in training and is exported as the C# reproduction test's fixture.
    rng = np.random.default_rng(1234)
    train_idx, test_idx = [], []
    for g in sorted(set(fam)):
        gi = rng.permutation(np.where(fam == g)[0])
        cut = max(1, int(0.85 * len(gi)))
        train_idx.extend(gi[:cut])
        test_idx.extend(gi[cut:])
    train_idx, test_idx = np.array(sorted(train_idx)), np.array(sorted(test_idx))

    xtr, ytr = X[train_idx], y[train_idx]
    skf = StratifiedKFold(n_splits=5, shuffle=True, random_state=0)
    oof = cross_val_predict(gbm(), xtr, ytr, cv=skf, method="predict_proba")[:, 1]
    oof_auc = roc_auc_score(ytr, oof)
    sig_sorted = np.sort(oof[ytr == 0])[::-1]
    thresholds = {str(t): thr_at(sig_sorted, t) for t in (0.01, 0.02, 0.05)}

    final = gbm()
    final.fit(xtr, ytr)

    proba_test = final.predict_proba(X[test_idx])[:, 1]
    ytest = y[test_idx]
    held_out_auc = roc_auc_score(ytest, proba_test) if len(set(ytest)) > 1 else float("nan")

    n_nodes = export_csharp_trees(final, out / "StructuralNoiseModel.Trees.g.cs")

    fixture_rows = [{"id": rows[i]["id"], "family": rows[i]["family"], "cls": int(y[i]), "text": rows[i]["text"]}
                    for i in test_idx]
    (out / "held-out-reproduction-fixture.json").write_text(
        json.dumps({"thresholds": thresholds, "rows": fixture_rows}, indent=2))

    summary = {
        "train_rows": int(len(train_idx)), "test_rows": int(len(test_idx)),
        "train_only_oof_auc": oof_auc, "held_out_test_auc": held_out_auc,
        "thresholds": thresholds, "n_tree_nodes": n_nodes,
        "sklearn_version": __import__("sklearn").__version__, "numpy_version": np.__version__,
    }
    (out / "model.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
