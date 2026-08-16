#!/usr/bin/env python3
"""Layer 1 (background QoS) + layer 2 (parallelism budget) wrapper for a test/build command.

Usage: run_suite.py [--agents N] [--reserve M] [--no-qos] [--slots K] -- <command...>

Layer 1 prefixes the command with `taskpolicy -b` on macOS, so it competes for CPU as a
background task instead of starving whatever else is running on the machine (measured
1.0x of idle under `-b` vs 4.8x without, under 20 concurrent saturating processes). Layer 2
computes a per-agent worker budget and exports it as AI_BADGER_TEST_WORKERS, because neither
Vitest nor Playwright has a worker env var of its own — the project's own config reads it,
the same shape `playwright.config.ts` already uses for CI.

Both layers are independently disableable, and each is best-effort: a failure anywhere in
this script's own machinery is a warning on stderr, never a reason the wrapped command does
not run — see references/machine-load.md for the measurements and the recovery recipe.

Do NOT wrap long-lived infrastructure (AppHost, dev servers, emulators) with this script —
`taskpolicy -b` is inherited by children, and applying it to a process the tests block on
starting up tightens that process's own timeouts by the same ~2.11x this buys the foreground.
Wrap only the test runner invocation itself.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import List, Optional, Sequence, Tuple

DEFAULT_RESERVE = 2
WORKERS_ENV = "AI_BADGER_TEST_WORKERS"
QOS_ENV = "AI_BADGER_QOS"
STATE_DIR_ENV = "AI_BADGER_RUN_SUITE_STATE_DIR"


def cpu_count() -> int:
    """Usable core count for this process: `os.process_cpu_count()` (respects a cgroup or
    `taskset` limit) falling back to `os.cpu_count()`. Never `os.sched_getaffinity` — it is
    Linux-only and raises AttributeError on macOS/Windows, and `process_cpu_count` already
    reads it internally where it exists. Floors at 1 so a divide-by-it is always safe."""
    getter = getattr(os, "process_cpu_count", None)
    if getter is not None:
        try:
            n = getter()
        except OSError:
            n = None
        if n:
            return n
    n = os.cpu_count()
    return n if n else 1


def budget(cores: int, agents: int, reserve: int = DEFAULT_RESERVE,
           slots: Optional[int] = None) -> int:
    """Per-agent parallelism budget: max(1, (cores - reserve) // min(agents, slots or agents)).

    Never 0 — an oversubscribed machine (e.g. agents=20, cores=10) still hands each agent 1
    worker rather than starving it or dividing by zero. Derived from the actual core and agent
    counts rather than a fixed constant, because a percentage cap (`maxWorkers: "50%"`) cannot
    compose: it is 50% of the whole machine in *every* worktree, so five worktrees ask for
    250% between them.
    """
    divisor = max(1, min(agents, slots if slots is not None else agents))
    return max(1, (cores - reserve) // divisor)


def qos_enabled(no_qos: bool) -> bool:
    """True when layer 1 (`taskpolicy -b`) applies to this run."""
    if no_qos:
        return False
    if os.environ.get(QOS_ENV, "").strip().lower() == "off":
        return False
    return sys.platform == "darwin"


def qos_prefix(no_qos: bool) -> List[str]:
    """['<taskpolicy>', '-b'] when layer 1 applies here, else [] — never raises."""
    if not qos_enabled(no_qos):
        return []
    taskpolicy = shutil.which("taskpolicy")
    if taskpolicy is None:
        print("run_suite: on macOS but 'taskpolicy' is not on PATH; running without QoS",
              file=sys.stderr)
        return []
    return [taskpolicy, "-b"]


def _state_dir() -> Path:
    override = os.environ.get(STATE_DIR_ENV)
    if override:
        return Path(override)
    return Path.home() / ".ai-badger" / "run-suite"


def _log_invocation(cores: int, agents: int, reserve: int, slots: Optional[int],
                     workers: Optional[int], qos: bool) -> None:
    """Best-effort JSONL breadcrumb (`references/machine-load.md`'s diagnosis recipes read
    it). Never raises: a write failure here is a stderr warning, not a reason the wrapped
    command does not run — that is the whole point of gate 4."""
    try:
        directory = _state_dir()
        directory.mkdir(parents=True, exist_ok=True)
        record = {"ts": time.time(), "cores": cores, "agents": agents, "reserve": reserve,
                   "slots": slots, "workers": workers, "qos": qos}
        with open(directory / "run-suite.log", "a", encoding="utf-8") as fh:
            fh.write(json.dumps(record) + "\n")
    except OSError as exc:
        print(f"run_suite: could not write state log under {_state_dir()} ({exc}); "
              f"continuing without it", file=sys.stderr)


def _split_argv(argv: Sequence[str]) -> Tuple[List[str], List[str]]:
    """(run_suite's own args, wrapped command) split on the first bare `--`."""
    argv = list(argv)
    if "--" in argv:
        idx = argv.index("--")
        return argv[:idx], argv[idx + 1:]
    return argv, []


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="run_suite.py", description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--agents", type=int, default=1,
                         help="How many agents/worktrees are competing for the machine "
                              "right now (default: 1).")
    parser.add_argument("--reserve", type=int, default=DEFAULT_RESERVE,
                         help=f"Cores to leave for the OS/orchestrator (default: "
                              f"{DEFAULT_RESERVE}).")
    parser.add_argument("--no-qos", action="store_true",
                         help="Skip the `taskpolicy -b` prefix even on macOS.")
    parser.add_argument("--slots", type=int, default=None,
                         help="How many of --agents currently hold a run slot, if fewer "
                              "than all of them are running at once.")
    return parser


def build_command(command: Sequence[str], no_qos: bool) -> List[str]:
    """The final argv: the QoS prefix (if any) followed by the caller's command."""
    return [*qos_prefix(no_qos), *command]


def main(argv: Optional[Sequence[str]] = None) -> int:
    own_argv, command = _split_argv(sys.argv[1:] if argv is None else argv)
    args = _build_parser().parse_args(own_argv)

    if not command:
        print("run_suite: no command given; usage: run_suite.py [--agents N] [--reserve M] "
              "[--no-qos] [--slots K] -- <command...>", file=sys.stderr)
        return 2

    # Every step below is guarded the same way: a failure prints one stderr line and the
    # script still reaches the final subprocess.run — see the module docstring's "a false
    # 'your tests never ran' is worse than transient oversubscription."
    try:
        cores = cpu_count()
    except Exception as exc:  # pylint: disable=broad-exception-caught
        print(f"run_suite: could not determine core count ({exc}); assuming 1 core",
              file=sys.stderr)
        cores = 1

    workers: Optional[int]
    try:
        workers = budget(cores, args.agents, args.reserve, args.slots)
    except Exception as exc:  # pylint: disable=broad-exception-caught
        print(f"run_suite: could not compute a worker budget ({exc}); leaving "
              f"{WORKERS_ENV} unset", file=sys.stderr)
        workers = None

    if workers is not None and WORKERS_ENV not in os.environ:
        os.environ[WORKERS_ENV] = str(workers)

    try:
        qos = qos_enabled(args.no_qos)
    except Exception as exc:  # pylint: disable=broad-exception-caught
        print(f"run_suite: could not resolve QoS state ({exc}); running without it",
              file=sys.stderr)
        qos = False

    _log_invocation(cores, args.agents, args.reserve, args.slots, workers, qos)

    try:
        full_command = build_command(command, args.no_qos)
    except Exception as exc:  # pylint: disable=broad-exception-caught
        print(f"run_suite: could not build the QoS-prefixed command ({exc}); running the "
              f"bare command instead", file=sys.stderr)
        full_command = list(command)

    result = subprocess.run(full_command, check=False)
    return result.returncode


if __name__ == "__main__":
    sys.exit(main())
