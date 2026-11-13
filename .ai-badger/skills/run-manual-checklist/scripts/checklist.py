#!/usr/bin/env python3
"""Mechanical half of a manual checklist: validate a checks file, start a run, report a run.

`check CHECKS` validates; `new-run CHECKS --slug S` writes a run skeleton with facts derived;
`report RUN --checks CHECKS` summarises a filled run. Exit codes: 0 ok, 1 invalid or incomplete,
2 usage or unreadable input, 3 complete but acceptances pending, 4 complete but some results
rejected (`accepted: false`); pending wins over rejected.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

ID_RE = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
TOP_KEYS = {"schema", "results-dir", "rules", "facts", "items"}
STATUSES = {"pass", "fail", "skipped", "substituted"}
REASON_KEYS = ("acceptance-reason", "acceptation-reason", "skip-reason")
FACT_TIMEOUT = 300


class UsageError(Exception):
    """Unreadable input or a refused write; the CLI maps it to exit 2."""


# Standalone skills cannot import the framework; kept in sync by test_git_invocation.
GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                    "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")


def git_env(env: dict[str, str] | None = None) -> dict[str, str]:
    """Copy the environment without variables that redirect Git to another repository."""
    out = dict(os.environ if env is None else env)
    for name in GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


def load_json(path: Path) -> Any:
    """Read *path* as JSON or raise UsageError."""
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise UsageError(f"{path}: cannot read JSON: {exc}") from exc


def is_text(value: Any) -> bool:
    """True for a non-blank string."""
    return isinstance(value, str) and bool(value.strip())


def repo_root(checks: Path, override: str | None) -> Path:
    """The --repo directory, else the git toplevel of the checks file, else its directory."""
    if override:
        return Path(override).resolve()
    start = Path(checks).resolve().parent
    out = subprocess.run(["git", "-C", str(start), "rev-parse", "--show-toplevel"],
                         capture_output=True, text=True, env=git_env(), check=False)
    return Path(out.stdout.strip()) if out.returncode == 0 and out.stdout.strip() else start


def inside(path: Path, root: Path) -> bool:
    """True when *path* resolves to *root* or something under it."""
    return path.resolve().is_relative_to(root.resolve())


def validate_checks(data: Any, root: Path) -> list[tuple[str, str]]:
    """Return every (json-path, problem) found in a parsed checks file."""
    if not isinstance(data, dict):
        return [("$", "top level must be an object")]
    problems: list[tuple[str, str]] = []
    for key in sorted(set(data) - TOP_KEYS):
        problems.append((f"$.{key}", "unknown top-level key"))
    schema = data.get("schema")
    if not isinstance(schema, int) or isinstance(schema, bool) or schema != 1:
        problems.append(("$.schema", "must be the integer 1"))
    results = data.get("results-dir")
    if not is_text(results) or Path(results).is_absolute():
        problems.append(("$.results-dir", "must be a non-empty relative path"))
    elif ".." in Path(results).parts or not inside(root / results, root):
        problems.append(("$.results-dir", "must stay inside the repository"))
    rules = data.get("rules", [])
    if not isinstance(rules, list) or not all(isinstance(r, str) for r in rules):
        problems.append(("$.rules", "must be a list of strings"))
    problems += validate_facts(data.get("facts"))
    problems += validate_items(data.get("items"), root)
    return problems


def validate_facts(facts: Any) -> list[tuple[str, str]]:
    """Problems in the `facts` object."""
    if not isinstance(facts, dict):
        return [("$.facts", "must be an object")]
    problems = []
    for name, fact in facts.items():
        where = f"$.facts.{name}"
        if not isinstance(fact, dict):
            problems.append((where, "must be an object"))
            continue
        if not is_text(fact.get("command")):
            problems.append((f"{where}.command", "must be a non-empty string"))
        if "nonzero" in fact and not isinstance(fact["nonzero"], bool):
            problems.append((f"{where}.nonzero", "must be a boolean"))
    return problems


def validate_items(items: Any, root: Path) -> list[tuple[str, str]]:
    """Problems in the `items` list."""
    if not isinstance(items, list) or not items:
        return [("$.items", "must be a non-empty list")]
    problems = []
    seen: set[str] = set()
    for index, entry in enumerate(items):
        where = f"$.items[{index}]"
        if not isinstance(entry, dict):
            problems.append((where, "must be an object"))
            continue
        ident = entry.get("item")
        if not isinstance(ident, str) or not ID_RE.match(ident):
            problems.append((f"{where}.item", "must be a kebab-case id"))
        elif ident in seen:
            problems.append((f"{where}.item", f"duplicate item id {ident!r}"))
        else:
            seen.add(ident)
        if not is_text(entry.get("expected-result")):
            problems.append((f"{where}.expected-result", "must be a non-empty string"))
        if "anchor" in entry:
            problems += validate_anchor(entry["anchor"], f"{where}.anchor", root)
    return problems


def validate_anchor(anchor: Any, where: str, root: Path) -> list[tuple[str, str]]:
    """Problems in one anchor: a string `path[#fragment]` naming a file under *root*."""
    if not is_text(anchor):
        return [(where, "must be a non-empty string")]
    path = anchor.split("#", 1)[0]
    candidate = (root / path).resolve()
    if (not path or Path(path).is_absolute()
            or not candidate.is_relative_to(root.resolve()) or not candidate.is_file()):
        return [(where, f"anchor file not found: {path or anchor}")]
    return []


def cmd_check(args: argparse.Namespace) -> int:
    """`check`: print `ok: N items, M facts` or every problem."""
    data = load_json(args.checks)
    problems = validate_checks(data, repo_root(args.checks, args.repo))
    for where, text in problems:
        print(f"{args.checks}:{where}: {text}", file=sys.stderr)
    if problems:
        return 1
    print(f"ok: {len(data['items'])} items, {len(data['facts'])} facts")
    return 0


def git_value(root: Path, *git_args: str) -> str:
    """Stripped stdout of a git command in *root*, empty when git fails."""
    out = subprocess.run(["git", "-C", str(root), *git_args], capture_output=True, text=True,
                         env=git_env(), check=False)
    return out.stdout.strip() if out.returncode == 0 else ""


def derive_fact(name: str, fact: dict, root: Path) -> str:
    """Run one fact command; raise ValueError naming the fact when its value is untrustworthy."""
    try:
        done = subprocess.run(fact["command"], shell=True, cwd=root, capture_output=True,
                              text=True, timeout=FACT_TIMEOUT, check=False)
    except subprocess.TimeoutExpired as exc:
        raise ValueError(f"fact {name!r}: timed out after {FACT_TIMEOUT}s") from exc
    value = done.stdout.strip()
    if done.returncode != 0:
        tail = done.stderr.strip()[-300:]
        raise ValueError(f"fact {name!r}: exit {done.returncode}: {tail}")
    if not value:
        raise ValueError(f"fact {name!r}: printed nothing")
    if fact.get("nonzero") and value == "0":
        raise ValueError(f"fact {name!r}: printed 0 but is marked nonzero")
    return value


def skeleton_item(entry: dict) -> dict:
    """An unfilled run item for one checks item."""
    item = {"item": entry["item"]}
    if "anchor" in entry:
        item["anchor"] = entry["anchor"]
    item.update({"expected-result": entry["expected-result"], "command": "", "evidence": "",
                 "observed-result": "", "status": "", "accepted": None,
                 "acceptance-reason": ""})
    return item


def dump_json(data: Any) -> str:
    """The on-disk JSON form of a run file."""
    return json.dumps(data, indent=1, ensure_ascii=False) + "\n"


def cmd_new_run(args: argparse.Namespace) -> int:
    """`new-run`: derive facts and write a run skeleton."""
    data = load_json(args.checks)
    root = repo_root(args.checks, args.repo)
    problems = validate_checks(data, root)
    for where, text in problems:
        print(f"{args.checks}:{where}: {text}", file=sys.stderr)
    if problems:
        return 1
    date = args.date or datetime.date.today().isoformat()
    if not ID_RE.match(args.slug):
        raise UsageError(f"--slug {args.slug!r} must be a kebab-case id")
    if not DATE_RE.match(date):
        raise UsageError(f"--date {date!r} must be YYYY-MM-DD")
    try:
        datetime.date.fromisoformat(date)
    except ValueError as exc:
        raise UsageError(f"--date {date!r} is not a calendar date") from exc
    target = root / data["results-dir"] / f"{date}-{args.slug}.json"
    if not inside(target, root):
        raise UsageError(f"{target} is outside the repository")
    if target.exists() and not args.force:
        raise UsageError(f"{target} exists; pass --force to overwrite")
    derived = {}
    try:
        for name, fact in data["facts"].items():
            derived[name] = derive_fact(name, fact, root)
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        return 1
    run = {"run": {"date": date, "branch": git_value(root, "rev-parse", "--abbrev-ref", "HEAD"),
                   "commit": git_value(root, "rev-parse", "HEAD"), "note": ""},
           "derived": derived, "findings-against-prior-runs": [],
           "items": [skeleton_item(entry) for entry in data["items"]]}
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(dump_json(run), encoding="utf-8")
    print(target)
    for rule in data.get("rules", []):
        print(f"rule: {rule}")
    return 0


def reason_of(item: dict) -> str:
    """The first non-blank reason among the accepted reason fields."""
    for key in REASON_KEYS:
        if is_text(item.get(key)):
            return item[key].strip()
    return ""


def validate_run(run: Any, checks: Any) -> list[str]:
    """Return every completeness problem of a parsed run file; *checks* is parsed or None."""
    if not isinstance(run, dict):
        return ["run file: top level must be an object"]
    if not isinstance(run.get("run"), dict):
        return ["run file: `run` must be an object"]
    items = run.get("items")
    if not isinstance(items, list):
        return ["run file: `items` must be a list"]
    problems = scope_problems(run["run"]["scope"]) if "scope" in run["run"] else []
    ids = set()
    for index, item in enumerate(items):
        where = f"items[{index}]"
        if not isinstance(item, dict) or not is_text(item.get("item")):
            problems.append(f"{where}: missing `item` id")
            continue
        where = f"item {item['item']!r}"
        if item["item"] in ids:
            problems.append(f"{where}: duplicate item id")
        ids.add(item["item"])
        problems += validate_run_item(item, where)
    if checks is not None and not problems:
        problems += cross_check(run, ids, checks)
    return problems


def scope_problems(scope: Any) -> list[str]:
    """Problems with `run.scope`, which must be a non-empty list of item ids."""
    if (isinstance(scope, list) and scope
            and all(isinstance(i, str) and ID_RE.fullmatch(i) for i in scope)):
        return []
    return ["run file: `run.scope` must be a non-empty list of item ids"]


def evidence_text(value: Any) -> str:
    """Evidence as one string: a non-blank string, or the non-blank lines of a list of strings."""
    if isinstance(value, list):
        if not all(isinstance(line, str) for line in value):
            return ""
        return " | ".join(line.strip() for line in value if line.strip())
    return value.strip() if is_text(value) else ""


def validate_run_item(item: dict, where: str) -> list[str]:
    """Completeness problems of one run item."""
    problems = []
    if not is_text(item.get("command")):
        problems.append(f"{where}: empty `command`")
    if not evidence_text(item.get("evidence")):
        problems.append(f"{where}: empty `evidence`")
    if not is_text(item.get("observed-result")):
        problems.append(f"{where}: empty `observed-result`")
    status = item.get("status")
    if not isinstance(status, str) or status not in STATUSES:
        problems.append(f"{where}: status {status!r} not one of {sorted(STATUSES)}")
    elif status in ("skipped", "substituted") and not reason_of(item):
        problems.append(f"{where}: {status} without a reason "
                        f"({'/'.join(REASON_KEYS)})")
    if status == "fail" and item.get("accepted") is True and not reason_of(item):
        problems.append(f"{where}: accepted fail without a reason ({'/'.join(REASON_KEYS)})")
    if item.get("accepted") is not None and not isinstance(item.get("accepted"), bool):
        problems.append(f"{where}: `accepted` must be null, true or false")
    return problems


def cross_check(run: dict, ids: set[str], checks: Any) -> list[str]:
    """Problems comparing run item ids with the checks file, honouring run.scope."""
    entries = checks.get("items", []) if isinstance(checks, dict) else []
    known = {e["item"] for e in entries if isinstance(e, dict) and "item" in e}
    scope = run["run"].get("scope")
    wanted = known if scope is None else known & set(scope)
    problems = [f"item {i!r}: not in the checks file" for i in sorted(ids - known)]
    problems += [f"item {i!r}: in scope but missing from the run" for i in sorted(wanted - ids)]
    if scope is not None:
        problems += [f"scope {i!r}: not in the checks file" for i in sorted(set(scope) - known)]
        problems += [f"item {i!r}: run item outside run.scope"
                     for i in sorted((ids & known) - set(scope))]
    expectations = {e["item"]: e.get("expected-result") for e in entries}
    for item in run["items"]:
        if item["item"] in known and item.get("expected-result") != expectations[item["item"]]:
            problems.append(f"item {item['item']!r}: expected-result differs from the checks file")
    derived = run.get("derived")
    if not isinstance(derived, dict) or set(derived) != set(checks.get("facts", {})):
        problems.append("run file: derived fact keys differ from the checks file")
    return problems


def render_report(run: dict) -> tuple[str, bool, bool]:
    """Markdown summary of a complete run, and whether any item is pending or rejected."""
    items = run["items"]
    counts = {s: sum(1 for i in items if i["status"] == s) for s in sorted(STATUSES)}
    head = run["run"]
    out = [f"# Run {head.get('date', '')} {head.get('branch', '')} {head.get('commit', '')}",
           "", "## Counts", ""]
    out += [f"- {status}: {n}" for status, n in counts.items()]
    out += ["", "## Failures", ""]
    fails = [i for i in items if i["status"] == "fail"]
    out += [f"- {i['item']}: {evidence_text(i['evidence'])}" for i in fails] or ["none"]
    out += ["", "## Skipped or substituted", ""]
    skipped = [i for i in items if i["status"] in ("skipped", "substituted")]
    out += [f"- {i['item']} ({i['status']}): {reason_of(i)}" for i in skipped] or ["none"]
    out += ["", "## Pending acceptance", ""]
    pending = [i for i in items if i.get("accepted") is None]
    out += [f"- {i['item']}" for i in pending] or ["none"]
    out += ["", "## Rejected", ""]
    rejected = [i for i in items if i.get("accepted") is False]
    out += [f"- {i['item']}: {reason_of(i) or 'no reason given'}" for i in rejected] or ["none"]
    out += ["", "## Findings against prior runs", ""]
    findings = run.get("findings-against-prior-runs") or []
    out += [f"- {f}" for f in findings] or ["none"]
    return "\n".join(out) + "\n", bool(pending), bool(rejected)


def cmd_report(args: argparse.Namespace) -> int:
    """`report`: exit 1 incomplete, 3 pending acceptance, 4 rejected results, 0 done."""
    run = load_json(args.run)
    checks = load_json(args.checks)
    problems = [f"{args.checks}:{where}: {text}"
                for where, text in validate_checks(checks, repo_root(args.checks, args.repo))]
    if not problems:
        problems = [f"{args.run}: {text}" for text in validate_run(run, checks)]
    for text in problems:
        print(text, file=sys.stderr)
    if problems:
        return 1
    text, pending, rejected = render_report(run)
    sys.stdout.write(text)
    if pending:
        return 3
    return 4 if rejected else 0


def build_parser() -> argparse.ArgumentParser:
    """The argparse tree for the three subcommands."""
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("check", help="validate a checks file")
    check.add_argument("checks", type=Path)
    check.add_argument("--repo")
    check.set_defaults(func=cmd_check)
    new_run = sub.add_parser("new-run", help="write a run skeleton with derived facts")
    new_run.add_argument("checks", type=Path)
    new_run.add_argument("--slug", required=True)
    new_run.add_argument("--date")
    new_run.add_argument("--repo")
    new_run.add_argument("--force", action="store_true")
    new_run.set_defaults(func=cmd_new_run)
    rep = sub.add_parser("report", help="summarise a filled run")
    rep.add_argument("run", type=Path)
    rep.add_argument("--checks", type=Path, required=True)
    rep.add_argument("--repo")
    rep.set_defaults(func=cmd_report)
    return parser


def main(argv: list[str] | None = None) -> int:
    """Entry point; returns the process exit code."""
    args = build_parser().parse_args(argv)
    try:
        return args.func(args)
    except UsageError as exc:
        print(str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
