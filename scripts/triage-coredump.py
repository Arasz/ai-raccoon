#!/usr/bin/env python3
"""Prints what a Linux core dump says: the signal, the faulting module, the threads per module."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from coredump import summarize  # noqa: E402


def main(argv):
    if not argv:
        print("usage: triage-coredump.py <core> [<core>...]", file=sys.stderr)
        return 2
    for path in argv:
        # One unreadable dump must not hide the summary of the next one.
        try:
            for line in summarize(path):
                print(line)
        except (OSError, ValueError) as error:
            print("could not triage %s: %s" % (path, error), file=sys.stderr)
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
