#!/usr/bin/env python3
"""Grep for the isolation controls `design-tests` Stage 4 declares — time, network, fs, env,
random, shared state — across the test files a design or review pass just produced or is judging.

Categories and patterns follow the ruleset's isolation rules (review-tests references/universal.md, T1-ISO-*); the
five isolation controls are `references/universal.md` group `T1-ISO-*`. Python 3 stdlib only —
this file ships to every scaffolded consumer project, which never has ai-badger's own `engine/`
on its path (ADR-0005).

A hit is a finding, not a disqualification: an integration test may legitimately need a real
port, and a file whose setup demonstrably neutralises the hit (`FakeTimeProvider`, a fake-timer
call, an MSW handler) is downgraded to `mitigated` rather than dropped, so a reader can still see
what was controlled and how. The two wall-clock-*assertion* rows are never mitigable — asserting
elapsed time is wrong regardless of what else the file does (T1-ISO-01).

Usage: scan_uncontrolled_resources.py <path>... [--json]
  <path>...  one or more files or directories; directories are scanned recursively for
             .cs/.ts/.tsx/.js files, skipping node_modules/bin/obj/dist/build/.git.
  --json     emit a JSON array of findings instead of the one-line-per-finding text form.

Exit code is always 0 — findings are data for a human or the benchmark harness to read, never a
gate a scripted caller should branch on.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable, List, Tuple

SKIP_DIR_NAMES = {"node_modules", "bin", "obj", "dist", "build", ".git", "__pycache__"}
CS_EXTENSIONS = {".cs"}
TS_EXTENSIONS = {".ts", ".tsx", ".js"}
ALL_EXTENSIONS = CS_EXTENSIONS | TS_EXTENSIONS

BLOCKER = "blocker"
MAJOR = "major"
MITIGATED = "mitigated"

# Mitigation groups: a marker found anywhere in the raw file text downgrades every hit in that
# group, in that file, to `mitigated`. Limited to the three concrete mitigations the ruleset names
# — a fake clock, a fake-timer call, an MSW handler — rather than inventing more (ask-if-simpler).
#
# `sleep-or-delay` is file-wide-mitigable only for TS (`vi.useFakeTimers()` etc. really do
# control `setTimeout`/`setInterval` and a `setTimeout`-backed sleep). On the C# side a fake
# `TimeProvider` never intercepts `Thread.Sleep` or `Task.Delay(int)` — both still hit the real
# OS timer — so `Thread.Sleep` is its own `thread-sleep` category (NEVER_MITIGABLE below) and a
# `Task.Delay` hit is judged per call by `_task_delay_is_mitigated`, not by this file-wide table.
TIME_MITIGATION_RE = re.compile(
    r"FakeTimeProvider|useFakeTimers|vi\.useFakeTimers|jest\.useFakeTimers")
NETWORK_MITIGATION_RE = re.compile(r"server\.use\(|setupServer\(|\bmsw\b")
MITIGATION_GROUPS = {
    "wall-clock-now": TIME_MITIGATION_RE,
    "sleep-or-delay": TIME_MITIGATION_RE,
    "network": NETWORK_MITIGATION_RE,
}
NEVER_MITIGABLE = {"wall-clock-assertion", "thread-sleep"}

# A `Task.Delay` call is mitigated only when that same call passes a TimeProvider argument (the
# .NET idiom for a delay the test controls), or the file drives a fake clock's `.Advance(...)` —
# never by a bare `FakeTimeProvider` field sitting unused elsewhere in the file (W2-04).
_TASK_DELAY_TIMEPROVIDER_ARG_RE = re.compile(r"Task\.Delay\s*\([^)]*\b[Tt]imeProvider\b")
_FAKE_TIME_ADVANCE_RE = re.compile(r"FakeTimeProvider")
_ADVANCE_CALL_RE = re.compile(r"\.Advance\s*\(")


def _task_delay_is_mitigated(line: str, full_text: str) -> bool:
    """C# `Task.Delay` only — see the module-level comment above `MITIGATION_GROUPS`."""
    if _TASK_DELAY_TIMEPROVIDER_ARG_RE.search(line):
        return True
    return bool(_FAKE_TIME_ADVANCE_RE.search(full_text) and _ADVANCE_CALL_RE.search(full_text))


@dataclass(frozen=True)
class Finding:
    file: str
    line: int
    category: str
    severity: str
    snippet: str
    mitigated: bool


# category -> (severity, applicable_extensions, [regex, ...])
_CATEGORIES: List[Tuple[str, str, frozenset, List[str]]] = [
    ("wall-clock-now", MAJOR, frozenset(CS_EXTENSIONS), [
        r"DateTime\.(Now|UtcNow|Today)", r"DateTimeOffset\.(Now|UtcNow)", r"TimeProvider\.System",
    ]),
    ("wall-clock-now", MAJOR, frozenset(TS_EXTENSIONS), [
        r"Date\.now\(\)", r"new\s+Date\s*\(\s*\)",
    ]),
    ("stopwatch", MAJOR, frozenset(CS_EXTENSIONS), [
        r"new\s+Stopwatch", r"Stopwatch\.StartNew", r"\.ElapsedMilliseconds", r"\.Elapsed\b",
    ]),
    ("thread-sleep", MAJOR, frozenset(CS_EXTENSIONS), [
        r"Thread\.Sleep",
    ]),
    ("sleep-or-delay", MAJOR, frozenset(CS_EXTENSIONS), [
        r"Task\.Delay\s*\(\s*[0-9]",
    ]),
    ("sleep-or-delay", MAJOR, frozenset(TS_EXTENSIONS), [
        r"\bsetTimeout\b", r"\bsetInterval\b", r"await\s+new\s+Promise\(r\s*=>\s*setTimeout",
    ]),
    ("unseeded-random", MAJOR, frozenset(CS_EXTENSIONS), [
        r"new\s+Random\s*\(\s*\)",
    ]),
    ("unseeded-random", MAJOR, frozenset(TS_EXTENSIONS), [
        r"Math\.random\(\)",
    ]),
    ("unpinned-identity", MAJOR, frozenset(CS_EXTENSIONS), [
        r"Guid\.NewGuid\s*\(\s*\)",
    ]),
    ("unpinned-identity", MAJOR, frozenset(TS_EXTENSIONS), [
        r"crypto\.randomUUID\(\)",
    ]),
    ("environment", MAJOR, frozenset(CS_EXTENSIONS), [
        r"Environment\.(GetEnvironmentVariable|CurrentDirectory|MachineName|UserName|ProcessPath)",
        r"AppDomain\.CurrentDomain", r"ConfigurationManager\.", r"IConfiguration.*json.*file",
    ]),
    ("environment", MAJOR, frozenset(TS_EXTENSIONS), [
        r"process\.env\.", r"process\.cwd\(\)", r"import\.meta\.dir",
    ]),
    ("filesystem", MAJOR, frozenset(CS_EXTENSIONS), [
        r"System\.IO\.(File|Directory|Path)\.", r"(?<!Fake)File\.(Read|Write|Exists|Delete)",
        r"Directory\.(Create|Delete|Get)", r"Path\.GetTempPath", r"Path\.GetTempFileName",
    ]),
    ("filesystem", MAJOR, frozenset(TS_EXTENSIONS), [
        r"node:fs", r'from\s+["\']fs["\']',
    ]),
    ("network", MAJOR, frozenset(CS_EXTENSIONS), [
        r"new\s+HttpClient\s*\(\s*\)", r"https?://(?!localhost|127\.0\.0\.1)",
    ]),
    ("network", MAJOR, frozenset(TS_EXTENSIONS), [
        r"fetch\s*\(", r"https?://(?!localhost|127\.0\.0\.1)",
    ]),
    ("process-or-port", MAJOR, frozenset(CS_EXTENSIONS), [
        r"Process\.(Start|GetProcesses)", r"new\s+(TcpListener|Socket|HttpListener)",
        r"\.Listen\s*\(",
    ]),
    ("process-or-port", MAJOR, frozenset(TS_EXTENSIONS), [
        r"new\s+Worker", r"child_process", r"net\.createServer", r"\.listen\s*\(",
    ]),
    ("storage", MAJOR, frozenset(TS_EXTENSIONS), [
        r"\blocalStorage\b", r"\bsessionStorage\b", r"\bindexedDB\b",
    ]),
    ("global-mutation", MAJOR, frozenset(TS_EXTENSIONS), [
        r"globalThis\.\w+\s*=", r"document\.title\s*=", r"window\.\w+\s*=",
    ]),
    ("mutable-static", MAJOR, frozenset(CS_EXTENSIONS), [
        r"static\s+(?!readonly)\w+\s+\w+\s*(=|;)",
    ]),
    # Never mitigable — T1-ISO-01, always blocker regardless of file context.
    # No `\b` tightening here: real C# code writes `FromMilliseconds`/`ElapsedMilliseconds`
    # as single compound identifiers, so a suffix-anchored `\bMilliseconds\b`/`\bElapsed\b`
    # would go dark on exactly the calls this category exists to catch. No concrete C# false
    # positive was found in review (unlike the TS `ms`-substring case, W2-02) — left as-is.
    ("wall-clock-assertion", BLOCKER, frozenset(CS_EXTENSIONS), [
        r"Assert.*Elapsed", r"Should.*Elapsed", r"BeLessThan.*Milliseconds",
    ]),
    # `duration`/`elapsed`/`took` are anchored on both sides so a variable that merely ends in
    # those letters (`items`, `terms`, `navItems`, ...) cannot match; `ms` additionally requires
    # a numeric comparator on the same `expect(...)` call, since a bare `\bms\b` boundary alone
    # still matches nothing useful without one (W2-02 — 0/9 precision on the unanchored form).
    ("wall-clock-assertion", BLOCKER, frozenset(TS_EXTENSIONS), [
        r"expect\([^)]*\b(duration|elapsed|took)\b[^)]*\)",
        r"expect\([^)]*\bms\b[^)]*\)\s*\.\s*"
        r"(toBeLessThan|toBeGreaterThan|toBeLessThanOrEqual|toBeGreaterThanOrEqual)\s*\(\s*\d",
    ]),
]

_COMPILED = [
    (category, severity, exts, [re.compile(p) for p in patterns])
    for category, severity, exts, patterns in _CATEGORIES
]

# A `//` only starts a line comment when it opens the line or follows whitespace, and never
# when it is immediately preceded by `:` — the shape of `https://` and `http://` inside a string
# literal (W2-06: an unconditional `//.*$` blanked the network patterns' only live match, since
# every `https?://` URL was truncated at its own `//`). Still a heuristic, not a tokenizer: it
# does not track string literals in general, so a `//` glued to other punctuation inside a
# string (`"a//b"`) can still be mis-treated as code either way. Acceptable for a scanner whose
# findings are data for a human to read, never a gate (see the module docstring).
_LINE_COMMENT_RE = re.compile(r"(?:^|(?<=\s))(?<!:)//.*$")
_BLOCK_COMMENT_START_RE = re.compile(r"/\*")
_BLOCK_COMMENT_END_RE = re.compile(r"\*/")


def _strip_comments(text: str) -> List[str]:
    """Return `text`'s lines with `//...` and `/* ... */` spans blanked out.

    A heuristic, not a tokenizer — it does not track string literals, so a `//` glued to other
    text inside a string can still be mis-treated as a comment start. Acceptable for a scanner
    whose findings are data for a human to read, never a gate (see the module docstring).
    """
    out: List[str] = []
    in_block = False
    for raw in text.splitlines():
        line = raw
        if in_block:
            end = _BLOCK_COMMENT_END_RE.search(line)
            if end is None:
                out.append("")
                continue
            line = line[end.end():]
            in_block = False
        while True:
            start = _BLOCK_COMMENT_START_RE.search(line)
            if start is None:
                break
            end = _BLOCK_COMMENT_END_RE.search(line, start.end())
            if end is None:
                line = line[: start.start()]
                in_block = True
                break
            line = line[: start.start()] + line[end.end():]
        line = _LINE_COMMENT_RE.sub("", line)
        out.append(line)
    return out


def _extension_for(filename: str) -> str:
    return Path(filename).suffix


def scan_text(text: str, filename: str) -> List[Finding]:
    """Findings in `text`, attributed to `filename` for its extension and for reporting."""
    ext = _extension_for(filename)
    if ext not in ALL_EXTENSIONS:
        return []
    raw_lines = text.splitlines()
    stripped_lines = _strip_comments(text)

    mitigation_groups_present = {
        group for group, marker_re in MITIGATION_GROUPS.items() if marker_re.search(text)
    }

    findings: List[Finding] = []
    for category, severity, exts, patterns in _COMPILED:
        if ext not in exts:
            continue
        file_wide_mitigated = (
            category in mitigation_groups_present and category not in NEVER_MITIGABLE
        )
        per_call_delay_check = category == "sleep-or-delay" and ext in CS_EXTENSIONS
        for lineno, stripped in enumerate(stripped_lines, start=1):
            if any(p.search(stripped) for p in patterns):
                if category in NEVER_MITIGABLE:
                    mitigated = False
                elif per_call_delay_check:
                    mitigated = _task_delay_is_mitigated(stripped, text)
                else:
                    mitigated = file_wide_mitigated
                findings.append(Finding(
                    file=filename, line=lineno, category=category,
                    severity=MITIGATED if mitigated else severity,
                    snippet=raw_lines[lineno - 1].strip()[:200],
                    mitigated=mitigated,
                ))
    findings.sort(key=lambda f: (f.line, f.category))
    return findings


def _iter_target_files(paths: Iterable[str]) -> Iterable[Path]:
    for raw in paths:
        p = Path(raw)
        if p.is_file():
            if p.suffix in ALL_EXTENSIONS:
                yield p
            continue
        if not p.is_dir():
            continue
        for child in sorted(p.rglob("*")):
            if not child.is_file() or child.suffix not in ALL_EXTENSIONS:
                continue
            if SKIP_DIR_NAMES & set(child.relative_to(p).parts[:-1]):
                continue
            yield child


def scan_paths(paths: Iterable[str]) -> List[Finding]:
    findings: List[Finding] = []
    for path in _iter_target_files(paths):
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        findings.extend(scan_text(text, str(path)))
    return findings


def _format_text(findings: List[Finding]) -> str:
    lines = [
        f"{f.file}:{f.line}:{f.category}:{f.severity}:{f.snippet}" for f in findings
    ]
    return "\n".join(lines)


def main(argv=None) -> int:
    """CLI entry point. Always returns 0 — findings are data, never a gate."""
    ap = argparse.ArgumentParser(description=__doc__,
                                  formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+", help="files or directories to scan")
    ap.add_argument("--json", action="store_true", help="emit JSON instead of the text form")
    args = ap.parse_args(argv)

    findings = scan_paths(args.paths)
    if args.json:
        print(json.dumps([asdict(f) for f in findings], indent=2))
    else:
        text = _format_text(findings)
        if text:
            print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
