#!/usr/bin/env python3
"""Memory guard for heavy harness lanes (P2 AC4 backstop).

Runs ``<command...>``, samples the child process TREE's RSS every --interval
seconds, streams the child's stdout/stderr through in full, and SIGTERMs the
whole tree (then SIGKILLs stragglers) when the tree crosses --cap-mb. Sampling
is periodic: a spike shorter than --interval can be missed — this is a
backstop, not a cgroup. Heavy lanes (ingest/eval/refactor rerun) never run
concurrently on one box: two default caps exceed it.

Usage:
    python3 memwatch.py [--cap-mb 12288] [--interval 2.0] [--log FILE] -- CMD [ARGS...]
    python3 memwatch.py 12288 mem.log CMD [ARGS...]   # legacy positional form

Exit: the child's exit code, or 99 when the cap was breached (tree killed).
"""

from __future__ import annotations

import argparse
import os
import signal
import subprocess
import sys
import threading
import time

DEFAULT_CAP_MB = 12 * 1024


def _ps_rows() -> list[tuple[int, int, int]]:
    out = subprocess.run(["ps", "-Ao", "pid,ppid,rss"], capture_output=True,
                         text=True, timeout=10).stdout
    rows = []
    for line in out.splitlines()[1:]:
        parts = line.split()
        if len(parts) < 3:
            continue
        try:
            rows.append((int(parts[0]), int(parts[1]), int(parts[2])))
        except ValueError:
            continue
    return rows


def tree_rss_mb(root_pid: int) -> int:
    """Resident set size of root_pid plus every descendant, in MB."""
    rows = _ps_rows()
    children: dict[int, list[int]] = {}
    rss: dict[int, int] = {}
    for pid, ppid, kb in rows:
        children.setdefault(ppid, []).append(pid)
        rss[pid] = kb
    total = rss.get(root_pid, 0)
    stack = [root_pid]
    while stack:
        for child in children.get(stack.pop(), []):
            total += rss.get(child, 0)
            stack.append(child)
    return total // 1024


def _descendants(root_pid: int) -> list[int]:
    rows = _ps_rows()
    children: dict[int, list[int]] = {}
    for pid, ppid, _ in rows:
        children.setdefault(ppid, []).append(pid)
    found, stack = [], [root_pid]
    while stack:
        for child in children.get(stack.pop(), []):
            found.append(child)
            stack.append(child)
    return found


def kill_tree(proc: subprocess.Popen, sig: int) -> None:
    """Signal every descendant first, then the root (the root may exit first)."""
    for pid in _descendants(proc.pid):
        try:
            os.kill(pid, sig)
        except (ProcessLookupError, PermissionError):
            pass
    try:
        proc.send_signal(sig)
    except ProcessLookupError:
        pass


def _stream(pipe, sink) -> None:
    for line in pipe:
        sink.write(line)
        sink.flush()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--cap-mb", type=int, default=None,
                        help=f"RSS cap in MB (default {DEFAULT_CAP_MB} = 12 GB)")
    parser.add_argument("--interval", type=float, default=2.0,
                        help="sampling period seconds; a spike shorter than this can be missed")
    parser.add_argument("--log", default=None,
                        help="memwatch status log path (child output still streams to stdout)")
    parser.add_argument("remainder", nargs=argparse.REMAINDER)
    args = parser.parse_args(argv)

    remainder = list(args.remainder)
    cap_mb = args.cap_mb if args.cap_mb is not None else DEFAULT_CAP_MB
    log_path = args.log
    if len(remainder) >= 3 and remainder[0].isdigit():  # legacy: cap_mb log cmd...
        cap_mb = int(remainder.pop(0))
        log_path = remainder.pop(0)
    if remainder and remainder[0] == "--":
        remainder = remainder[1:]
    if not remainder:
        parser.error("no command given")

    log = open(log_path, "a", encoding="utf-8") if log_path else None

    def note(text: str) -> None:
        print(text, flush=True)
        if log is not None:
            log.write(text + "\n")
            log.flush()

    proc = subprocess.Popen(remainder, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True)
    reader = threading.Thread(target=_stream, args=(proc.stdout, sys.stdout), daemon=True)
    reader.start()
    note(f"memwatch: pid={proc.pid} cap={cap_mb}MB interval={args.interval}s")
    peak = 0
    breached = False
    try:
        while proc.poll() is None:
            rss = tree_rss_mb(proc.pid)
            peak = max(peak, rss)
            if rss > cap_mb:
                note(f"CAP BREACH {rss}MB > {cap_mb}MB — SIGTERM the tree")
                kill_tree(proc, signal.SIGTERM)
                try:
                    proc.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    kill_tree(proc, signal.SIGKILL)
                    proc.kill()
                    proc.wait(timeout=10)
                breached = True
                break
            time.sleep(args.interval)
        if not breached:
            proc.wait()
        reader.join(timeout=10)
    finally:
        if log is not None:
            log.close()
    result = "KILLED" if breached else f"exit({proc.returncode})"
    note(f"memwatch: peak={peak}MB cap={cap_mb}MB result={result}")
    return 99 if breached else proc.returncode


if __name__ == "__main__":
    sys.exit(main())
