#!/usr/bin/env python3
"""Cross-artifact verification for task air-254-vs-1022-retrieval-measurement-809 (issue #809).

Re-derives every reported number class from the artifact the consolidated report
(`docs/work/2026-09-30-809-measurement-report.md`) names for it, and exits non-zero on any
mismatch. Classes:

  C  chain    eval-set-100.json header.snapshotSha256 == copy-1022 pin sha == A/B record citation
  W  webgpu   per-run peaks and medians vs result.json; corrected numeric F12 check (findings.md)
  R  rechunk  three 448 elapsed values + six counts vs quiet.log; metrics rows vs the root-A bank
  F  refusal  pass-1/pass-2 windows vs quiet.log and the probe loop; convergence vs the pin json
  A  ab       objective means, regressions, floor, paired, blind vs metrics.json/paired.json/blind
  E  eval     query/field change counts vs the pre-rebase commit; hashes resolve in the pinned copy

Usage: python3 scripts/verify-809-report.py [--raw-rechunk-root DIR] [--self-test]

The root-A raw artifacts (quiet.log, memory.db, probe loop) are scratch files under /tmp; pass
`--raw-rechunk-root` when they live elsewhere. A missing raw artifact is a FAIL, not a skip: the
verification is about the artifacts, and an absent one cannot be re-derived.
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import re
import sqlite3
import subprocess
import sys
from pathlib import Path

REPORT = "docs/work/2026-09-30-809-measurement-report.md"
AB_RECORD = "docs/work/2026-09-30-254-vs-1022-retrieval-ab.md"
PIN_JSON = "docs/work/2026-09-30-copy-1022.pin.json"
EVAL_SET = "scripts/retrieval_tuning/corpora/eval-set-100.json"
EVAL_PRE_REBASE = "ab5b789c^:" + EVAL_SET
AB_DIR = "docs/work/2026-09-30-254-vs-1022-retrieval-ab"
WEBGPU_DIR = "docs/work/device-benchmark/2026-09-30-m4-webgpu-1022"
ANCHOR_JSON = "docs/work/device-benchmark/2026-09-26-m4/result.json"

# The root-A bank metrics rows for this measurement all land after this epoch (the phase
# note's own "today's rows" filter).
ROOT_A_METRICS_FLOOR = 1790759000

REBUDGET_RE = re.compile(
    r"Chunk-budget rebudget at 1022 tokens: "
    r"(?P<note>\d+) note group\(s\) and (?P<mirror>\d+) mirror group\(s\) re-chunked, "
    r"(?P<unchanged>\d+) unchanged, (?P<retryable>\d+) retryable, "
    r"(?P<unprovable>\d+) unprovable, (?P<terminal>\d+) terminal in "
    r"(?P<elapsed>\d+:\d\d:\d\d\.\d+)"
)
REFUSAL_RE = re.compile(r'ToolRefusals: "memory_search" refused: model-migration-in-progress')


def hms_to_ms(elapsed: str) -> float:
    hours, minutes, seconds = elapsed.split(":")
    return (int(hours) * 3600 + int(minutes) * 60 + float(seconds)) * 1000.0


class Checker:
    """Collects PASS/FAIL/INFO lines and the failure count that drives the exit code."""

    def __init__(self) -> None:
        self.passed = 0
        self.failures: list[str] = []
        self.cls = ""

    def start(self, cls: str, title: str) -> None:
        self.cls = cls
        print(f"== [{cls}] {title}")

    def _record(self, status: str, name: str, detail: str) -> None:
        print(f"  {status:<7} {self.cls}.{name}: {detail}")

    def eq(self, name: str, expected, actual, tol: float = 0.0) -> None:
        ok = (
            abs(float(expected) - float(actual)) <= tol
            if isinstance(expected, float) or isinstance(actual, float)
            else expected == actual
        )
        if ok:
            self.passed += 1
            self._record("PASS", name, f"{actual!r}")
        else:
            self.failures.append(f"{self.cls}.{name}")
            self._record("FAIL", name, f"expected {expected!r}, got {actual!r}")

    def contains(self, name: str, text: str, needle: str) -> None:
        if needle in text:
            self.passed += 1
            self._record("PASS", name, f"report carries {needle!r}")
        else:
            self.failures.append(f"{self.cls}.{name}")
            self._record("FAIL", name, f"report does not carry {needle!r}")

    def subset(self, name: str, expected_values: set, actual_values, tol: float = 0.001) -> None:
        missing = [v for v in expected_values if not any(abs(v - a) <= tol for a in actual_values)]
        if not missing:
            self.passed += 1
            self._record("PASS", name, f"{len(expected_values)} value(s) present in artifact")
        else:
            self.failures.append(f"{self.cls}.{name}")
            self._record("FAIL", name, f"missing from artifact: {missing}")

    def missing(self, name: str, path: Path) -> None:
        self.failures.append(f"{self.cls}.{name}")
        self._record("FAIL", name, f"artifact missing: {path}")

    def info(self, name: str, detail: str) -> None:
        self._record("INFO", name, detail)


def read_text(root: Path, rel: str) -> str:
    return (root / rel).read_text(encoding="utf-8", errors="replace")


def git_show(root: Path, spec: str) -> str:
    done = subprocess.run(
        ["git", "show", spec], cwd=root, capture_output=True, text=True, check=False
    )
    if done.returncode != 0:
        raise RuntimeError(f"git show {spec}: {done.stderr.strip()}")
    return done.stdout


def open_readonly(db: Path):
    for uri in (f"file:{db}?mode=ro", f"file:{db}?mode=ro&immutable=1"):
        try:
            return sqlite3.connect(uri, uri=True)
        except sqlite3.OperationalError:
            continue
    raise sqlite3.OperationalError(f"cannot open {db} read-only")


def check_chain(root: Path, ck: Checker) -> None:
    ck.start("C", "eval-set ⇄ copy-1022 pin ⇄ A/B record sha chain")
    pin = json.loads((root / PIN_JSON).read_text())
    ev = json.loads((root / EVAL_SET).read_text())
    ab = read_text(root, AB_RECORD)
    ck.eq("pin-eval", pin["sha256"], ev["header"]["snapshotSha256"])
    ck.contains("record-citation", ab, pin["sha256"])
    ck.eq("pin-entry-count", pin["entries"], 21765)
    ck.eq("eval-query-count", ev["header"]["queryCount"], len(ev["queries"]))


def check_webgpu(root: Path, ck: Checker) -> None:
    ck.start("W", "WebGPU peaks vs result.json (corrected numeric F12 check)")
    result = json.loads(read_text(root, f"{WEBGPU_DIR}/result.json"))
    anchor = json.loads(read_text(root, ANCHOR_JSON))
    report = read_text(root, REPORT)
    runs = result["runs"]
    ck.eq("runs", 6, len(runs))
    for device, provider in (("auto", "WebGPU"), ("cpu", "CPU")):
        selected = [r for r in runs if r["device"] == device]
        ck.eq(f"{device}-runs", 3, len(selected))
        ck.eq(f"{device}-provider-actual", {provider}, {r["provider_actual"] for r in selected})
        ck.eq(f"{device}-status", {"ok"}, {r["status"] for r in selected})
    auto = result["summary"]["devices"]["auto"]["phys_footprint_peak_kib"]
    cpu = result["summary"]["devices"]["cpu"]["phys_footprint_peak_kib"]
    anchor_med = anchor["summary"]["devices"]["auto"]["phys_footprint_peak_kib"]["median"]
    ck.eq("auto-median", 2662883, auto["median"])
    ck.eq("auto-min-max", (2350867, 2675475), (auto["min"], auto["max"]))
    ck.eq("cpu-median", 2357555, cpu["median"])
    ck.eq("cpu-min-max", (2220051, 2552899), (cpu["min"], cpu["max"]))
    ck.eq("neural-peaks-zero", (0, 0),
           (result["summary"]["devices"]["auto"]["neural_footprint_peak_kib"]["median"],
            result["summary"]["devices"]["cpu"]["neural_footprint_peak_kib"]["median"]))
    threshold_anchor = 2 * anchor_med
    threshold_cpu = 2 * cpu["median"]
    ck.eq("threshold-1", 4137540, threshold_anchor)
    ck.eq("threshold-2", 4715110, threshold_cpu)
    falsified = auto["median"] > threshold_anchor or auto["median"] > threshold_cpu
    ck.eq("corrected-numeric-verdict", "SURVIVES", "FALSIFIED" if falsified else "SURVIVES")
    ck.eq("median-falsified-by-anchor", False, auto["median"] > threshold_anchor)
    ck.eq("median-falsified-by-cpu", False, auto["median"] > threshold_cpu)
    ck.eq("worst-run-holds", False,
           auto["max"] > threshold_anchor or auto["max"] > 2 * cpu["max"])
    ck.eq("adversarial-holds", False, auto["max"] > 2 * cpu["min"])
    for needle in ("2,662,883", "2,675,475", "2,357,555", "4,137,540", "4,715,110", "SURVIVES"):
        ck.contains("report-number", report, needle)
    # The pre-registered jq check is vacuous; reproduce the mutation only if jq is available.
    jq = subprocess.run(["which", "jq"], capture_output=True, text=True, check=False)
    if jq.returncode == 0:
        broken = subprocess.run(
            ["jq", "-e",
             '(.summary.devices.auto.phys_footprint_peak_kib) as $a | '
             '(.summary.devices.cpu.phys_footprint_peak_kib) as $c | '
             '(($a > 4137540) or ($a > 2*$c)) | IN(true,false)',
             str(root / f"{WEBGPU_DIR}/result.json")],
            capture_output=True, text=True, check=False)
        synthetic = subprocess.run(
            ["jq", "-e",
             '(.summary.devices.auto.phys_footprint_peak_kib) as $a | '
             '(.summary.devices.cpu.phys_footprint_peak_kib) as $c | '
             '(($a > 4137540) or ($a > 2*$c)) | IN(true,false)'],
            input='{"summary":{"devices":{"auto":{"phys_footprint_peak_kib":{"median":99999999}},'
                  '"cpu":{"phys_footprint_peak_kib":{"median":1}}}}}',
            capture_output=True, text=True, check=False)
        ck.eq("vacuous-jq-broken-on-real", "true", broken.stdout.strip())
        ck.eq("vacuous-jq-true-at-99999999", "true", synthetic.stdout.strip())
        ck.eq("corrected-check-catches-mutation", "FALSIFIED",
              "FALSIFIED" if 99999999 > threshold_anchor or 99999999 > threshold_cpu
              else "SURVIVES")
        ck.info("vacuous-jq", "pre-registered jq check returns true for both artifacts and the "
                              "99,999,999 KiB mutation — non-discriminating, as documented")
    else:
        ck.info("vacuous-jq", "jq not on PATH; corrected numeric check above is the gate")


def check_rechunk(root: Path, ck: Checker, raw_root: Path) -> None:
    ck.start("R", "re-chunk elapsed + six counts vs quiet.log and root-A metrics")
    report = read_text(root, REPORT)
    quiet = raw_root / "quiet.log"
    if not quiet.is_file():
        ck.missing("quiet-log", quiet)
        return
    lines = quiet.read_text(encoding="utf-8", errors="replace").splitlines()
    parsed = [
        (int(m["note"]), int(m["mirror"]), int(m["unchanged"]), int(m["retryable"]),
         int(m["unprovable"]), int(m["terminal"]), m["elapsed"])
        for line in lines
        for m in [REBUDGET_RE.search(line)]
        if m
    ]
    expected = [
        (764, 1043, 3684, 13, 0, 1, "00:05:11.4600913"),
        (0, 0, 5491, 13, 0, 1, "00:03:35.2905774"),
        (0, 0, 5491, 0, 13, 1, "00:08:16.0222926"),
    ]
    ck.eq("quiet-448-lines", 3, len(parsed))
    ck.eq("quiet-448-values", expected, parsed)
    for needle in (
        "| phase 1 elapsed | 00:05:11.4600913 (311.46 s) |",
        "| phase 1 counts | 764 note + 1,043 mirror re-chunked, 3,684 unchanged, 13 retryable, "
        "0 unprovable, 1 terminal |",
        "| phase 2 elapsed | 00:03:35.2905774 (215.29 s) |",
        "| phase 2 counts | 0 + 0 re-chunked, 5,491 unchanged, 13 retryable, 0 unprovable, "
        "1 terminal |",
        "| scan-only elapsed | 00:08:16.0222926 (496.02 s) |",
        "| scan-only counts | 0 note + 0 mirror re-chunked, 5,491 unchanged, 0 retryable, "
        "13 unprovable, 1 terminal |",
    ):
        ck.contains("report-number", report, needle)

    db = raw_root / "memory.db"
    if not db.is_file():
        ck.missing("root-a-bank", db)
        return
    con = open_readonly(db)
    try:
        cur = con.cursor()
        rows: dict[str, list[float]] = collections.defaultdict(list)
        cur.execute(
            "SELECT name, value FROM metrics WHERE recorded_at >= ? AND name IN ("
            "'chunk.rechunk.duration_ms','chunk.rechunk.groups','drain.memory.duration_ms',"
            "'drain.memory.rows','job.model-migration.duration_ms')",
            (ROOT_A_METRICS_FLOOR,),
        )
        for name, value in cur.fetchall():
            rows[name].append(float(value))
    finally:
        con.close()
    ck.eq("metrics-chunk-duration-count", 3, len(rows["chunk.rechunk.duration_ms"]))
    ck.eq("metrics-chunk-groups-count", 3, len(rows["chunk.rechunk.groups"]))
    ck.eq("metrics-job-duration-count", 3, len(rows["job.model-migration.duration_ms"]))
    ck.subset("metrics-chunk-duration-matches-448",
              {hms_to_ms(e) for *_, e in expected}, rows["chunk.rechunk.duration_ms"])
    ck.subset("metrics-chunk-groups-matches-448",
              {float(note + mirror) for note, mirror, *_ in expected},
              rows["chunk.rechunk.groups"])
    ck.subset("metrics-drain-duration", {1432172.6063, 215291.1077, 496084.4052},
              rows["drain.memory.duration_ms"])
    ck.subset("metrics-drain-rows", {21757.0, 0.0}, rows["drain.memory.rows"])
    ck.subset("metrics-job-duration", {1432276.9538, 215291.3937, 496096.2831},
              rows["job.model-migration.duration_ms"])
    for needle in (
        "`chunk.rechunk.duration_ms` 311,460.0913 ms, `chunk.rechunk.groups` 1,807",
        "`drain.memory.duration_ms` 1,432,172.6063 ms",
        "44,107 owed, 21,757 embedded, 23m52.17s",
    ):
        ck.contains("report-number", report, needle)


def check_refusals(root: Path, ck: Checker, raw_root: Path) -> None:
    ck.start("F", "refusal windows vs quiet.log, probe loop and copy-1022 pin json")
    report = read_text(root, REPORT)
    quiet = raw_root / "quiet.log"
    if not quiet.is_file():
        ck.missing("quiet-log", quiet)
        return
    lines = quiet.read_text(encoding="utf-8", errors="replace").splitlines()
    marker = next((i for i, line in enumerate(lines) if "PASS2-SECTION-BEGIN" in line), None)
    ck.eq("pass2-marker-present", True, marker is not None)
    if marker is None:
        return
    pass1 = [line for line in lines[:marker] if REFUSAL_RE.search(line)]
    pass2 = [line for line in lines[marker + 1:] if REFUSAL_RE.search(line)]
    stamp = lambda line: line.split(" ", 1)[0]
    ck.eq("pass1-refusal-count", 10, len(pass1))
    ck.eq("pass2-refusal-count", 233, len(pass2))
    ck.eq("quiet-total-refusals", 243, len(pass1) + len(pass2))
    ck.eq("pass1-first", "2026-09-30T09:40:44.4558880+00:00", stamp(pass1[0]))
    ck.eq("pass1-last", "2026-09-30T10:04:55.2215140+00:00", stamp(pass1[-1]))
    ck.eq("pass2-first", "2026-09-30T10:18:53.2377690+00:00", stamp(pass2[0]))
    ck.eq("pass2-last", "2026-09-30T10:27:07.0299030+00:00", stamp(pass2[-1]))

    probe = raw_root / "probe-pass2-loop.txt"
    if probe.is_file():
        records: list[tuple[str, list[str]]] = []
        for line in probe.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith("2026-") and "|" in line:
                records.append((line.split("|", 1)[0], []))
            elif records:
                records[-1][1].append(line)
        refusal_idx = [i for i, (_, body) in enumerate(records)
                       if any("model-migration-in-progress" in b for b in body)]
        ck.eq("probe-refusal-count", 233, len(refusal_idx))
        if refusal_idx:
            ck.eq("probe-first-refusal", "2026-09-30T10:18:51Z",
                  records[refusal_idx[0]][0])
            ck.eq("probe-last-refusal", "2026-09-30T10:27:07Z",
                  records[refusal_idx[-1]][0])
            after_ts, after_body = records[refusal_idx[-1] + 1]
            ck.eq("probe-first-success", "2026-09-30T10:27:09Z", after_ts)
            ck.eq("probe-first-success-has-result", False,
                  any("model-migration-in-progress" in b for b in after_body))
        else:
            ck._record("FAIL", "probe-refusals", "no refusal record found")
            ck.failures.append("F.probe-refusals")
    else:
        ck.missing("probe-loop", probe)

    pin = json.loads(read_text(root, PIN_JSON))
    window = pin["convergence"]["refusalWindow"]
    ck.eq("copy1022-first", "2026-09-30T09:17:29Z", window["firstRefusalAt"])
    ck.eq("copy1022-last", "2026-09-30T09:19:52Z", window["lastRefusalAt"])
    ck.eq("copy1022-closed", "2026-09-30T09:19:57Z", window["closedAt"])
    ck.eq("copy1022-probes", (25, 10), (window["probes"]["refused"], window["probes"]["ok"]))
    for needle in (
        "first 09:40:44.4558880Z, last 10:04:55.2215140Z",
        "refusals 10:18:51Z to 10:27:07Z",
        "233 refusals, then first success 10:27:09Z",
        "first 09:17:29Z, last 09:19:52Z, closed 09:19:57Z; 25 refused / 10 ok probes",
        "243 refusal lines",
    ):
        ck.contains("report-number", report, needle)


def check_ab(root: Path, ck: Checker) -> None:
    ck.start("A", "A/B metrics vs metrics.json, paired.json and blind/ab-results.json")
    report = read_text(root, REPORT)
    metrics = json.loads(read_text(root, f"{AB_DIR}/metrics.json"))
    paired = json.loads(read_text(root, f"{AB_DIR}/paired.json"))
    blind = json.loads(read_text(root, f"{AB_DIR}/blind/ab-results.json"))
    ck.eq("repeat-count", 3, metrics["repeatCount"])
    ck.eq("runs", 6, len(metrics["runs"]))
    for arm in ("chunk254", "chunk1022"):
        for metric in ("ndcg5", "mrr5", "hit3", "hit1"):
            spread = metrics["spread"][arm][metric]
            ck.eq(f"{arm}-{metric}-spread-zero", (0.0, 0.0),
                  (spread["range"], spread["stddev"]))
    expectations = {
        ("chunk254", "ndcg5"): "0.4038", ("chunk254", "mrr5"): "0.3652",
        ("chunk254", "hit3"): "0.4800", ("chunk254", "hit1"): "0.2800",
        ("chunk1022", "ndcg5"): "0.5848", ("chunk1022", "mrr5"): "0.5267",
        ("chunk1022", "hit3"): "0.6500", ("chunk1022", "hit1"): "0.3900",
    }
    for (arm, metric), shown in expectations.items():
        ck.eq(f"{arm}-{metric}-mean", shown,
              f"{metrics['spread'][arm][metric]['values'][0]:.4f}")
        ck.contains("report-number", report, shown)
    regressions = metrics["regressions"]
    ck.eq("regressions", (43, 14, True),
          (regressions["forChunk254"], regressions["forChunk1022"],
           regressions["ownerReviewFlagged"]))
    floor = metrics["floor"]
    ck.eq("floor-pass", (False, True),
          (floor["evaluated"]["chunk254"]["pass"], floor["evaluated"]["chunk1022"]["pass"]))
    ck.eq("floor-owner-review", True, floor["ownerReviewRequired"])
    agg = paired["aggregate"]
    ck.eq("paired-overlap", 0.035, agg["meanTop8SetOverlap"])
    ck.eq("paired-rbo", 0.01901170157785714, agg["meanRbo"])
    ck.eq("paired-changed", (100, 0), (agg["queriesChanged"], agg["queriesUnchanged"]))
    ck.eq("paired-drops-backfills", (772, 769), (agg["totalDrops"], agg["totalBackfills"]))
    picks = collections.Counter(g["pickArm"] for q in blind["queries"] for g in q["graders"])
    calls = sum(len(q["graders"]) for q in blind["queries"])
    abstentions = sum(q["abstentions"] for q in blind["queries"])
    mean_comp = sum(q["compScore"] for q in blind["queries"]) / len(blind["queries"])
    ck.eq("blind-picks", {"chunk254": 24, "chunk1022": 3}, dict(picks))
    ck.eq("blind-calls", 27, calls)
    ck.eq("blind-abstentions", 0, abstentions)
    ck.eq("blind-mean-comp", 0.8888888888888888, mean_comp, tol=1e-9)
    per_grader = collections.Counter(
        (g["name"], g["pickArm"]) for q in blind["queries"] for g in q["graders"])
    for grader, (for254, for1022) in (
        ("grader-1", (8, 1)), ("grader-2", (7, 2)), ("grader-3", (9, 0))
    ):
        ck.eq(f"blind-{grader}", (for254, for1022),
              (per_grader[(grader, "chunk254")], per_grader[(grader, "chunk1022")]))
    for needle in (
        "0.0350", "0.0190", "100 / 0", "772 / 769",
        "24 / 3 of 27 calls", "0.8889",
        "grader-1 8-1, grader-2 7-2, grader-3 9-0",
        "| 254-side regressions (1022 higher) | 43 |",
        "| 1022-side regressions (254 higher) | 14 |",
    ):
        ck.contains("report-number", report, needle)


def check_eval(root: Path, ck: Checker) -> None:
    ck.start("E", "eval counts vs eval-set-100.json and the committed corpus")
    report = read_text(root, REPORT)
    current_raw = (root / EVAL_SET).read_text(encoding="utf-8")
    current = json.loads(current_raw)
    old_raw = git_show(root, EVAL_PRE_REBASE)
    old = json.loads(old_raw)
    head_raw = git_show(root, "HEAD:" + EVAL_SET)
    ck.eq("working-tree-equals-HEAD", True, current_raw == head_raw)
    ck.eq("queries", 100, len(current["queries"]))
    ck.eq("query-count-header", 100, current["header"]["queryCount"])
    ck.eq("query-ids-unique", 100, len({q["id"] for q in current["queries"]}))
    # The plan-graph AC1 check diffed `.queries[].text`, a key no corpus entry has: both sides are
    # 100 nulls, so the diff is vacuous. The honest `.query` variant is checked below.
    ck.eq("vacuous-text-key-absent", True,
          all(q.get("text") is None for q in current["queries"]))
    ck.info("vacuous-text-check", "the plan's [.queries[].text] diff compares 100 nulls; the "
                                  "honest [.queries[].query] comparison below is the gate "
                                  "(mutation-proven at ab5b789c, cited in eval-reanchor decisions)")
    ck.eq("query-text-changes", 0,
          sum(1 for a, b in zip(old["queries"], current["queries"])
              if a.get("query") != b.get("query")))
    mutated = [dict(q) for q in current["queries"]]
    mutated[0]["query"] = mutated[0]["query"] + " MUTATED"
    vacuous_sees = ([q.get("text") for q in old["queries"]]
                    != [q.get("text") for q in mutated])
    ck.eq("vacuous-text-diff-blind-to-mutation", False, vacuous_sees)
    ck.eq("honest-query-diff-sees-mutation", True,
          [q.get("query") for q in old["queries"]] != [q.get("query") for q in mutated])
    changes: dict[str, int] = collections.Counter()
    for before, after in zip(old["queries"], current["queries"]):
        for key in set(before) | set(after):
            if before.get(key) != after.get(key):
                changes[key] += 1
    ck.eq("field-changes", {"answerSpan": 60, "expectedHash": 93, "expectedSource": 58},
          dict(changes))
    for needle in (
        "query text changes | 0 of 100",
        "`expectedHash` changes | 93",
        "`expectedSource` changes | 58",
        "`answerSpan` changes | 60",
        "other fields changed | 0",
    ):
        ck.contains("report-number", report, needle)

    pin = json.loads(read_text(root, PIN_JSON))
    copy_db = Path(pin["path"])
    if copy_db.is_file():
        con = sqlite3.connect(f"file:{copy_db}?mode=ro", uri=True)
        try:
            cur = con.cursor()
            hashes = [q["expectedHash"] for q in current["queries"]]
            marks = ",".join("?" * len(hashes))
            cur.execute(f"SELECT hash, COUNT(*) FROM entries WHERE hash IN ({marks}) GROUP BY hash",
                        hashes)
            counts = dict(cur.fetchall())
            unresolved = [h for h in hashes if counts.get(h, 0) != 1]
            ck.eq("expectedHash-resolve-exactly-once", [], unresolved)
        finally:
            con.close()
    else:
        ck.info("copy-db", f"pinned copy not on disk ({copy_db}); resolution check skipped")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--raw-rechunk-root",
                        default=os.environ.get("VERIFY809_RECHUNK_ROOT",
                                               "/tmp/aira-rechunk-measure"))
    parser.add_argument("--self-test", action="store_true",
                        help="prove the comparator can fail, then exit 0")
    args = parser.parse_args(argv)
    root = Path(__file__).resolve().parents[1]
    ck = Checker()
    if args.self_test:
        ck.start("S", "self-test")
        ck.eq("deliberate-mismatch", 1, 2)
        ck.subset("deliberate-missing-value", {3}, [1, 2])
        ck.contains("deliberate-missing-text", "the quick brown fox", "lazy dog")
        expected_failures = [
            "S.deliberate-mismatch", "S.deliberate-missing-value", "S.deliberate-missing-text"
        ]
        if ck.failures == expected_failures:
            print("SELFTEST-OK: eq, subset and contains each recorded their deliberate failure")
            return 0
        print(f"SELFTEST-FAIL: comparator missed a mismatch ({ck.failures})")
        return 1
    print(f"# verify-809-report.py @ {root}")
    check_chain(root, ck)
    check_webgpu(root, ck)
    check_refusals(root, ck, Path(args.raw_rechunk_root))
    check_rechunk(root, ck, Path(args.raw_rechunk_root))
    check_ab(root, ck)
    check_eval(root, ck)
    print(f"== SUMMARY: {ck.passed} passed, {len(ck.failures)} failed")
    if ck.failures:
        print("FAILURES: " + ", ".join(ck.failures))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
