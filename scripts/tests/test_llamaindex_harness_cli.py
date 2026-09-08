"""AC3 wiring: every harness module runs via python -m (one-command gate).

Rejected shape (recorded): direct `python3 .../ingest.py` execution — the
modules use relative imports, which have no parent package under __main__;
making that work needs import surgery in three modules, while `python -m
llamaindex_harness.X` from scripts/retrieval_tuning needs none.
"""

import subprocess
import sys
from pathlib import Path

RETRIEVAL_TUNING = Path(__file__).resolve().parents[1] / "retrieval_tuning"


def _run_help(module: str):
    return subprocess.run([sys.executable, "-m", f"llamaindex_harness.{module}",
                           "--help"],
                          capture_output=True, text=True, timeout=180,
                          cwd=RETRIEVAL_TUNING)


def test_ingest_runs_as_module():
    proc = _run_help("ingest")
    assert proc.returncode == 0, proc.stderr[-500:]
    assert "--copy" in proc.stdout


def test_evaluate_runs_as_module():
    proc = _run_help("evaluate")
    assert proc.returncode == 0, proc.stderr[-500:]
    assert "--scratch-data-root" in proc.stdout


def test_report_runs_as_module():
    proc = _run_help("report")
    assert proc.returncode == 0, proc.stderr[-500:]
    assert "--results" in proc.stdout
