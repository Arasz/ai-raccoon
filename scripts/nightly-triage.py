#!/usr/bin/env python3
"""Run the unfiltered test suite and classify any failure as flake or regression.

The nightly's test step — unfiltered on purpose, because it is the trait-typo
backstop for the three filtered PR jobs (build-fast/bdd/slow). Runs the full suite
once with a trx logger; when something fails, re-runs exactly the failed tests once
(--no-build) and classifies each:

    rerun-pass            -> flake candidate
    rerun-fail            -> regression
    in known-flakes.json  -> known flake (no rerun: the ledger is the owner-approved
                             record-and-tolerate list, one PR per entry with evidence)

Exit 0 iff every failed test is ledgered — the repo's record-and-tolerate policy,
machine-decidable. Mass failure (>50 failed) and no-trx (build failure / test-host
crash) exit 1 without a rerun.

Outputs:
    TestResults/nightly.trx, TestResults/rerun.trx, TestResults/classification.json
    TestResults/serve-logs/   (E2E diag dir copied on failure, when present)
    a one-line summary on stdout and in $GITHUB_STEP_SUMMARY when set
    a 'nightly'-labelled issue (create or comment) for unledgered failures when
    GH_TOKEN is set

Usage:
    python3 scripts/nightly-triage.py
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
LEDGER_PATH = Path(__file__).resolve().parent.parent / "known-flakes.json"
TEST_RESULTS = Path("TestResults")
MASS_FAILURE_THRESHOLD = 50
SERVE_LOG_DIAG_DIR = Path(os.environ.get("TMPDIR", "/tmp")) / "ai-raccoon-crash-diag"


def parse_trx(path: Path) -> dict:
    """Counters, duration and failed FQNs from a trx file."""
    root = ET.parse(path).getroot()
    counters = root.find("t:ResultSummary/t:Counters", TRX_NS)
    times = root.find("t:Times", TRX_NS)
    failed = [
        r.get("testName")
        for r in root.findall(".//t:UnitTestResult", TRX_NS)
        if r.get("outcome") == "Failed"
    ]
    total = int(counters.get("total") or 0) if counters is not None else 0
    executed = int(counters.get("executed") or 0) if counters is not None else 0
    return {
        "total": total,
        "passed": int(counters.get("passed") or 0) if counters is not None else 0,
        "failed": int(counters.get("failed") or 0) if counters is not None else 0,
        # The trx Counters element has no skipped attribute (verified against a real trx:
        # total=2 executed=1 for one pass + one skip) — skipped is total minus executed.
        "skipped": max(0, total - executed),
        "started_at": times.get("start") if times is not None else None,
        "finished_at": times.get("finish") if times is not None else None,
        "failed_tests": failed,
    }


def safe_parse_trx(path: Path) -> dict | None:
    """parse_trx, or None when the file is corrupt/truncated (a test-host crash mid-write) —
    the caller treats that as unclassifiable rather than dying with a traceback."""
    try:
        return parse_trx(path)
    except (ET.ParseError, OSError, ValueError):
        return None


def trx_duration_s(trx: dict) -> float | None:
    """Test-suite wall duration from the trx Times element, if both ends parse."""
    if not trx["started_at"] or not trx["finished_at"]:
        return None
    try:
        start = datetime.fromisoformat(trx["started_at"])
        finish = datetime.fromisoformat(trx["finished_at"])
        return max(0.0, (finish - start).total_seconds())
    except ValueError:
        return None


def load_ledger() -> dict[str, dict]:
    """known-flakes.json -> {exact FQN: entry}; a missing or malformed ledger is empty."""
    if not LEDGER_PATH.exists():
        return {}
    try:
        entries = json.loads(LEDGER_PATH.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return {}
    if not isinstance(entries, list):
        return {}
    return {
        entry["test"]: entry
        for entry in entries
        if isinstance(entry, dict) and isinstance(entry.get("test"), str)
    }


def build_filter(fqns: list[str]) -> str:
    """xunit filter matching exactly the given FQNs."""
    return "|".join(f"FullyQualifiedName~{fqn}" for fqn in fqns)


# Class FQNs are safe in a --filter value; the only fatal character is the space, and these
# shapes (verified 2026-08-20: `+` and backtick included) never contain one.
_FILTER_SAFE_PART = re.compile(r"[A-Za-z0-9_.+`]+")


def class_fqns_from_test_names(fqns: list[str]) -> list[str]:
    """Class FQNs from trx testNames, deduped, order-preserving.

    xunit v3 theory rows carry the argument-rendered display name in the trx testName:
    'Ns.Class.Method(label: "...", argv: [...])'. The first '(' always opens the method's
    argument list (C# identifiers cannot contain '('), and the method name is the last
    '.'-separated segment before it. A class-level rerun is wider than the failed rows but
    safe: classification stays per-FQN on the first run's failures, and a rerun-only failure
    goes red (main()).
    """
    classes: list[str] = []
    seen: set[str] = set()
    for fqn in fqns:
        cls = fqn.split("(", 1)[0].rsplit(".", 1)[0]
        if cls not in seen:
            seen.add(cls)
            classes.append(cls)
    return classes


def classify(failed_tests: list[str], ledger: dict[str, dict],
             rerun_failed: set[str]) -> dict[str, str]:
    """Per-FQN verdict: known (in ledger), regression (failed again), flake (passed alone)."""
    return {
        fqn: "known" if fqn in ledger else ("regression" if fqn in rerun_failed else "flake")
        for fqn in failed_tests
    }


def run_dotnet_test(trx_name: str, extra: list[str] | None = None) -> subprocess.CompletedProcess:
    """One dotnet test invocation writing its trx under TestResults/.

    The directory comes from --results-directory, not the logger's LogFileDirectory:
    the trx logger ignores that parameter and writes into the TEST PROJECT's TestResults
    dir instead (witnessed twice on the first branch-nightly dispatch — the suite passed,
    the trx was not where the script read, and the run reported unclassifiable).
    """
    os.makedirs(TEST_RESULTS, exist_ok=True)
    command = [
        "dotnet", "test", "--nologo",
        "--results-directory", str(TEST_RESULTS.resolve()),
        "--logger", f"trx;LogFileName={trx_name}",
    ]
    if extra:
        command.extend(extra)
    return subprocess.run(command, check=False)


def format_duration(seconds: float | None) -> str:
    if seconds is None:
        return "duration n/a"
    minutes, secs = divmod(int(seconds), 60)
    return f"{minutes}m{secs:02d}s"


def summarize(date: str, verdict: str, trx: dict, classes: dict[str, str],
              rerun_only: frozenset[str] = frozenset()) -> str:
    """The one-liner that is the run's whole report card."""
    parts = [f"{date} {verdict}",
             f"{trx['passed']} passed / {trx['failed']} failed / {trx['skipped']} skipped",
             format_duration(trx_duration_s(trx))]
    flaky = [fqn for fqn, c in classes.items() if c == "flake"]
    if flaky:
        parts.append("flake candidates: " + ", ".join(flaky))
    regression = [fqn for fqn, c in classes.items() if c == "regression"]
    if regression:
        parts.append("regressions: " + ", ".join(regression))
    known = [fqn for fqn, c in classes.items() if c == "known"]
    if known:
        parts.append("known flakes: " + ", ".join(known))
    unclassifiable = [fqn for fqn, c in classes.items() if c == "unclassifiable"]
    if unclassifiable:
        parts.append("unclassifiable: " + ", ".join(unclassifiable))
    if rerun_only:
        parts.append("new failures in rerun: " + ", ".join(sorted(rerun_only)))
    return " — ".join(parts)


def write_classification(verdict: str, classes: dict[str, str], trx: dict,
                         rerun_only: frozenset[str] = frozenset()) -> None:
    os.makedirs(TEST_RESULTS, exist_ok=True)
    payload = {
        "verdict": verdict,
        "all_ledgered": bool(classes) and all(c == "known" for c in classes.values()),
        "failed": [{"test": fqn, "class": cls} for fqn, cls in sorted(classes.items())],
        "rerun_only": sorted(rerun_only),
        "counts": {k: trx[k] for k in ("total", "passed", "failed", "skipped")},
        "duration_s": trx_duration_s(trx),
    }
    (TEST_RESULTS / "classification.json").write_text(
        json.dumps(payload, indent=2) + "\n", encoding="utf-8")


def copy_serve_logs() -> None:
    """The E2E test's LATEST diag dir into TestResults/ so it uploads with the trx. Only the
    latest run's dir — the GUID dirs accumulate locally and all of them would bloat the artifact."""
    latest = SERVE_LOG_DIAG_DIR / "LATEST.txt"
    if not latest.exists():
        return
    source = Path(latest.read_text(encoding="utf-8").strip())
    if not source.is_dir():
        return
    try:
        shutil.copytree(source, TEST_RESULTS / "serve-logs", dirs_exist_ok=True)
    except OSError:
        pass  # diagnostics are best-effort


def write_step_summary(summary: str) -> None:
    """The one-liner onto the Actions run page, when running in CI."""
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    try:
        Path(path).write_text(summary + "\n", encoding="utf-8")
    except OSError:
        pass


def gh(args: list[str]) -> subprocess.CompletedProcess:
    """Runs gh and returns the full result — the caller decides what a failure means.

    A silent failure here is a lost notification (2026-08-20: the create 422'd on a missing
    'nightly' label and the script reported nothing), so callers must surface rc/stderr.
    """
    try:
        return subprocess.run(["gh", *args], capture_output=True, text=True, check=False)
    except FileNotFoundError:
        return subprocess.CompletedProcess(["gh", *args], returncode=127,
                                           stdout="", stderr="gh: command not found")


def file_or_comment_issue(summary_line: str, classes: dict[str, str], first_fqn: str) -> str:
    """Create one open issue per failing test family, or comment on the existing one.

    Returns a note ("" on success) the caller folds into the summary line — a gh failure
    must be visible, not swallowed. The issue title/search use the METHOD FQN (everything
    before the first '('): the display-name-suffixed theory FQN carries GitHub search
    operators ('(', ')', '"', 'label:') and can exceed the search/title limits.
    """
    if "GH_TOKEN" not in os.environ:
        return ""
    run_url = (f"{os.environ.get('GITHUB_SERVER_URL', 'https://github.com')}/"
               f"{os.environ.get('GITHUB_REPOSITORY', '')}/actions/runs/"
               f"{os.environ.get('GITHUB_RUN_ID', '')}")
    body = (f"{summary_line}\n\nRun: {run_url}\n\nDiagnostics: "
            f"`nightly-diagnostics` artifact (trx, classification, serve logs).")
    clean_fqn = first_fqn.split("(", 1)[0] if first_fqn != "unclassifiable" else first_fqn

    # The 'nightly' label may not exist in a fresh repo; create it idempotently so the
    # issue create cannot 422 on it (the 2026-08-20 silent-failure root cause).
    label_ok = gh(["label", "create", "nightly", "--force", "--color", "d73a4a",
                   "--description", "red nightly run"]).returncode == 0
    notes: list[str] = []
    if not label_ok:
        notes.append("gh label create nightly failed — filing without the label")

    search = gh(["issue", "list", "--state", "open", "--search", f"{clean_fqn} in:title",
                 "--json", "number", "--jq", ".[0].number"])
    if search.returncode != 0:
        notes.append("gh issue list failed (rc {0}): {1}".format(
            search.returncode, search.stderr.strip() or search.stdout.strip() or "no output"))
        return "; ".join(notes)
    existing = search.stdout.strip()

    title = f"nightly red {os.environ.get('GITHUB_RUN_DATE', '')}: {clean_fqn}"
    if not label_ok:
        title = f"[nightly] {title}"
    if existing:
        result = gh(["issue", "comment", existing, "--body", body])
        step = "issue comment"
    else:
        args = ["issue", "create", "--title", title, "--body", body]
        if label_ok:
            args += ["--label", "nightly"]
        result = gh(args)
        step = "issue create"
    if result.returncode != 0:
        notes.append("gh {0} failed (rc {1}): {2}".format(
            step, result.returncode,
            result.stderr.strip() or result.stdout.strip() or "no output"))
    return "; ".join(notes)


def main() -> int:
    today = os.environ.get("GITHUB_RUN_DATE") or datetime.now(timezone.utc).strftime("%Y-%m-%d")
    ledger = load_ledger()
    first = run_dotnet_test("nightly.trx")
    first_trx = TEST_RESULTS / "nightly.trx"
    if not first_trx.exists():
        summary = f"{today} red(unclassifiable) — no trx after the first run (build failure or test-host crash)"
        note = file_or_comment_issue(summary, {}, "unclassifiable")
        summary = f"{summary} — gh: {note}" if note else summary
        print(summary)
        write_step_summary(summary)
        return 1

    trx = safe_parse_trx(first_trx)
    if trx is None:
        summary = (f"{today} red(unclassifiable) — corrupt/truncated trx after the first run "
                   "(test-host crash mid-write)")
        note = file_or_comment_issue(summary, {}, "unclassifiable")
        summary = f"{summary} — gh: {note}" if note else summary
        print(summary)
        write_step_summary(summary)
        return 1

    failed = trx["failed_tests"]
    if not failed:
        if first.returncode != 0:
            # The host crashed AFTER writing results: a green-looking trx from a dead host is
            # not a green run — the repo's one unfiltered backstop must not report what it
            # did not finish.
            summary = (f"{today} red(unclassifiable) — test host crashed after writing results "
                       f"(exit {first.returncode})")
            verdict = "unclassifiable"
        else:
            summary = f"{today} green — {trx['passed']} passed / 0 failed / {trx['skipped']} skipped — " \
                      f"{format_duration(trx_duration_s(trx))}"
            verdict = "green"
        print(summary)
        write_step_summary(summary)
        write_classification(verdict, {}, trx)
        if verdict != "green":
            note = file_or_comment_issue(summary, {}, "unclassifiable")
            summary = f"{summary} — gh: {note}" if note else summary
            print(summary)
            write_step_summary(summary)
        return 0 if verdict == "green" else 1

    if len(failed) > MASS_FAILURE_THRESHOLD:
        summary = f"{today} red(mass failure) — {len(failed)} failed, no rerun " \
                  f"(environment signal, not {len(failed)} individual flakes)"
        note = file_or_comment_issue(summary, {}, failed[0])
        summary = f"{summary} — gh: {note}" if note else summary
        print(summary)
        write_step_summary(summary)
        write_classification("mass", {}, trx)
        return 1

    # Rerun only the failures the ledger does not already own.
    unknowns = [fqn for fqn in failed if fqn not in ledger]
    rerun_failed: set[str] = set()
    rerun_crashed = False
    if unknowns:
        rerun_trx_path = TEST_RESULTS / "rerun.trx"
        rerun_trx_path.unlink(missing_ok=True)  # a stale trx from a prior local run must not count
        # Class-level filter: trx testNames for xunit theory rows carry the argument-rendered
        # display name, whose spaces break `dotnet test --filter` at MSBuild parsing (MSB4177,
        # 2026-08-20 — the rerun never ran and every failure came back unclassifiable).
        class_names = class_fqns_from_test_names(unknowns)
        if any(not _FILTER_SAFE_PART.fullmatch(name) for name in class_names):
            # An FQN shape the derivation cannot guarantee safe (e.g. a future generic-method
            # FQN): fail safe — no rerun, the unknowns stay unclassifiable.
            rerun_crashed = True
        else:
            rerun = run_dotnet_test("rerun.trx",
                                    ["--no-build", "--filter", build_filter(class_names)])
            parsed = safe_parse_trx(rerun_trx_path) if rerun_trx_path.exists() else None
            if parsed is not None:
                rerun_failed = set(parsed["failed_tests"])
            # A crashed rerun proves nothing: the unknowns it did not re-fail stay unclassifiable,
            # not "flake" — only an intact, all-green rerun earns that verdict.
            rerun_crashed = rerun.returncode != 0 or parsed is None

    classes = classify(failed, ledger, rerun_failed)
    if rerun_crashed:
        for fqn in unknowns:
            if classes[fqn] == "flake":
                classes[fqn] = "unclassifiable"

    # A class-level rerun exercises tests that PASSED in the first run; one of those failing is
    # a red nobody asked for — name it and never let it hide behind a green-looking exit.
    rerun_only = rerun_failed - set(failed) - set(ledger)
    for fqn in sorted(rerun_only):
        classes[fqn] = "unclassifiable"

    all_ledgered = all(c == "known" for c in classes.values())
    verdict = "green(known flakes only)" if all_ledgered else "red"
    summary = summarize(today, verdict, trx, classes, frozenset(rerun_only))
    if not all_ledgered:
        copy_serve_logs()
        note = file_or_comment_issue(summary, classes, failed[0])
        summary = f"{summary} — gh: {note}" if note else summary
    print(summary)
    write_step_summary(summary)
    write_classification(verdict, classes, trx, frozenset(rerun_only))

    if not all_ledgered:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
