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
    return {
        "total": int(counters.get("total") or 0) if counters is not None else 0,
        "passed": int(counters.get("passed") or 0) if counters is not None else 0,
        "failed": int(counters.get("failed") or 0) if counters is not None else 0,
        "skipped": int(counters.get("skipped") or 0) if counters is not None else 0,
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


def classify(failed_tests: list[str], ledger: dict[str, dict],
             rerun_failed: set[str]) -> dict[str, str]:
    """Per-FQN verdict: known (in ledger), regression (failed again), flake (passed alone)."""
    return {
        fqn: "known" if fqn in ledger else ("regression" if fqn in rerun_failed else "flake")
        for fqn in failed_tests
    }


def run_dotnet_test(trx_name: str, extra: list[str] | None = None) -> subprocess.CompletedProcess:
    """One dotnet test invocation writing its trx under TestResults/."""
    os.makedirs(TEST_RESULTS, exist_ok=True)
    command = [
        "dotnet", "test", "--nologo",
        "--logger", f"trx;LogFileName={trx_name};LogFileDirectory={TEST_RESULTS}",
    ]
    if extra:
        command.extend(extra)
    return subprocess.run(command, check=False)


def format_duration(seconds: float | None) -> str:
    if seconds is None:
        return "duration n/a"
    minutes, secs = divmod(int(seconds), 60)
    return f"{minutes}m{secs:02d}s"


def summarize(date: str, verdict: str, trx: dict, classes: dict[str, str]) -> str:
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
    return " — ".join(parts)


def write_classification(verdict: str, classes: dict[str, str], trx: dict) -> None:
    os.makedirs(TEST_RESULTS, exist_ok=True)
    payload = {
        "verdict": verdict,
        "all_ledgered": bool(classes) and all(c == "known" for c in classes.values()),
        "failed": [{"test": fqn, "class": cls} for fqn, cls in sorted(classes.items())],
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


def gh(args: list[str]) -> str:
    return subprocess.run(["gh", *args], capture_output=True, text=True, check=False).stdout.strip()


def file_or_comment_issue(summary_line: str, classes: dict[str, str], first_fqn: str) -> None:
    """Create one open issue per failing test family, or comment on the existing one."""
    if "GH_TOKEN" not in os.environ:
        return
    run_url = (f"{os.environ.get('GITHUB_SERVER_URL', 'https://github.com')}/"
               f"{os.environ.get('GITHUB_REPOSITORY', '')}/actions/runs/"
               f"{os.environ.get('GITHUB_RUN_ID', '')}")
    body = (f"{summary_line}\n\nRun: {run_url}\n\nDiagnostics: "
            f"`nightly-diagnostics` artifact (trx, classification, serve logs).")
    existing = gh(["issue", "list", "--label", "nightly", "--state", "open",
                   "--search", first_fqn, "--json", "number", "--jq", ".[0].number"])
    if existing:
        gh(["issue", "comment", existing, "--body", body])
    else:
        title = f"nightly red {os.environ.get('GITHUB_RUN_DATE', '')}: {first_fqn}"
        gh(["issue", "create", "--title", title, "--body", body, "--label", "nightly"])


def main() -> int:
    today = os.environ.get("GITHUB_RUN_DATE") or datetime.now(timezone.utc).strftime("%Y-%m-%d")
    ledger = load_ledger()
    first = run_dotnet_test("nightly.trx")
    first_trx = TEST_RESULTS / "nightly.trx"
    if not first_trx.exists():
        summary = f"{today} red(unclassifiable) — no trx after the first run (build failure or test-host crash)"
        print(summary)
        write_step_summary(summary)
        return 1

    trx = safe_parse_trx(first_trx)
    if trx is None:
        summary = (f"{today} red(unclassifiable) — corrupt/truncated trx after the first run "
                   "(test-host crash mid-write)")
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
        return 0 if verdict == "green" else 1

    if len(failed) > MASS_FAILURE_THRESHOLD:
        summary = f"{today} red(mass failure) — {len(failed)} failed, no rerun " \
                  f"(environment signal, not {len(failed)} individual flakes)"
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
        rerun = run_dotnet_test("rerun.trx", ["--no-build", "--filter", build_filter(unknowns)])
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

    all_ledgered = all(c == "known" for c in classes.values())
    verdict = "green(known flakes only)" if all_ledgered else "red"
    summary = summarize(today, verdict, trx, classes)
    print(summary)
    write_step_summary(summary)
    write_classification(verdict, classes, trx)

    if not all_ledgered:
        copy_serve_logs()
        file_or_comment_issue(summary, classes, failed[0])
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
