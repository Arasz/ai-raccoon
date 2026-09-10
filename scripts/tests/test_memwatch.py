"""C5/P2-AC4 memwatch gates: live kill of a process TREE (grandchildren
included — a pure kill-decision test cannot see them), full stdout passthrough
(the probe version printed only out[-6000:]), and the breach exit code 99."""

import os
import re
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "retrieval_tuning"))

from memwatch import main as memwatch_main  # noqa: E402

HOG = (
    "import subprocess, sys, time\n"
    "p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)'])\n"
    "print(f'GRANDCHILD {p.pid}', flush=True)\n"
    "buf = bytearray(250 * 1024 * 1024)\n"
    "time.sleep(120)\n"
)


def test_memwatch_streams_child_stdout_in_full(capsys, tmp_path):
    log = tmp_path / "mem.log"
    code = memwatch_main(["--cap-mb", "4096", "--interval", "0.05", "--log", str(log),
                          "--", sys.executable, "-c",
                          "print('HEAD' + 'x' * 20000); print('TAIL-MARKER')"])
    out = capsys.readouterr().out
    assert code == 0
    assert "HEAD" in out and "TAIL-MARKER" in out
    assert out.count("x") >= 20000, "child stdout was clipped"
    # the status log must close cleanly and carry the final result line
    log_text = log.read_text()
    assert "result=exit(0)" in log_text


def test_memwatch_live_kills_the_process_tree_on_breach(capsys):
    code = memwatch_main(["--cap-mb", "50", "--interval", "0.1", "--",
                          sys.executable, "-c", HOG])
    out = capsys.readouterr().out
    assert code == 99, f"breach must exit 99, got {code}: {out[-500:]}"
    match = re.search(r"GRANDCHILD (\d+)", out)
    assert match, f"hog did not report its grandchild pid: {out[-500:]}"
    pid = int(match.group(1))
    deadline = time.time() + 5.0
    while time.time() < deadline:
        try:
            os.kill(pid, 0)
            time.sleep(0.1)
        except ProcessLookupError:
            return
    raise AssertionError(f"grandchild {pid} survived the tree kill")


def test_memwatch_runs_as_module_documents_cap_and_sampling():
    # P2 AC4: one-command gate — `python -m memwatch --help` must work (no
    # package-relative imports), state the default cap, and document the
    # sampling limitation (a backstop, never presented as a cgroup).
    retrieval_tuning = Path(__file__).resolve().parents[1] / "retrieval_tuning"
    proc = subprocess.run([sys.executable, "-m", "memwatch", "--help"],
                          capture_output=True, text=True, timeout=120,
                          cwd=retrieval_tuning)
    assert proc.returncode == 0, proc.stderr[-500:]
    assert "--cap-mb" in proc.stdout
    assert "sampling" in proc.stdout.lower()
    assert "12 GB" in proc.stdout


def test_memwatch_default_cap_is_12gb():
    from memwatch import DEFAULT_CAP_MB
    assert DEFAULT_CAP_MB == 12 * 1024
