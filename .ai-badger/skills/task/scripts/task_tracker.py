#!/usr/bin/env python3
"""CLI for the /task skill: task lifecycle + token-usage tracking.

Commands:
  start <taskId> [--title T] [--branch B] [--no-cron] [--risk]
      register task, start token checkpoint; --risk marks it as running automated gates in
      limited mode (formatting, fast tests, lint only)
  finish <taskId>
      finish checkpoint + usage calc (requires .ai-badger/state.json updated)
  grade <taskId> <0-5>
      save the user's quality grade
  subagent <taskId> <totalTokens> [--description D]
      record a completed subagent's token cost
  reattach <taskId>
      point task at the current session (after resume)
  status
      print all tasks (state, tokens, grade)
  install-cron / uninstall-cron
      manage the 30-min resume cron job

Exit codes: 0 ok, 2 bad input, 3 finish blocked (.ai-badger/state.json not updated since
task start).
"""
# pylint: disable=missing-function-docstring
# Ported verbatim from the originating job-search-ai-assistant repo's /task skill: kept in
# lockstep with that source rather than churned for local docstring style rules.

from __future__ import annotations

import argparse
import json
import shlex
import subprocess
import sys

import tracker_lib as lib

CRON_MARKER = "# task-skill-resume"
_NO_CRONTAB_MARKER = "no crontab for"


class CrontabUnavailable(Exception):
    """The current crontab could not be read, or the `crontab` binary is missing.

    Never treat this as "the crontab is empty" — doing so is what turns a transient read
    failure into an overwrite of the user's real cron jobs.
    """


# Under .ai-badger/, not any one agent's directory: this skill ships in features/common/, which
# all four supported agents share, and .ai-badger/ is the only directory every project has.
WORKTREE_DIR = ".ai-badger/worktrees"


def _git(root, *args, check=True):
    """Run git in `root` and return stdout. Returns '' when check is False and git fails."""
    result = subprocess.run(["git", "-C", str(root), *args],
                            capture_output=True, text=True, check=False, env=lib.git_env())
    if result.returncode != 0 and check:
        raise subprocess.CalledProcessError(result.returncode, result.args,
                                            result.stdout, result.stderr)
    return result.stdout if result.returncode == 0 else ""


def worktree_path(root, task_id):
    """Where a task's worktree lives. One per task, named by the task id.

    A task id arrives as a CLI argument, so it is untrusted input being spliced into a path:
    `..` or a separator would put `git worktree add/remove` somewhere nobody asked for. Refuse
    rather than sanitise — a silently rewritten id would not match the one in the tracking JSON.
    """
    name = str(task_id)
    if not name or name in {".", ".."} or "/" in name or "\\" in name:
        raise ValueError(
            f"task id {name!r} is not usable as a directory name: it must not be empty, "
            "'.', '..', or contain a path separator"
        )
    return lib.Path(root) / WORKTREE_DIR / name


def ensure_worktree(root, task_id, branch):
    """Create (or adopt) the task's worktree on `branch`. Returns its path, or None.

    None when no branch was recorded: a task that did not ask for one does not get one
    invented for it. Idempotent, so a resumed task re-running `start` is not an error.
    """
    if not branch:
        return None
    path = worktree_path(root, task_id)
    if path.is_dir():
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    existing = _git(root, "rev-parse", "--verify", "--quiet", branch, check=False).strip()
    args = ["worktree", "add"] + ([] if existing else ["-b", branch])
    args += [str(path)] + ([branch] if existing else [])
    _git(root, *args)
    return path


def worktree_blockers(path):
    """Reasons this worktree must not be removed: uncommitted files, then unmerged commits.

    Checked in that order because the first is what a person recognises. An empty list means
    every change in here also exists somewhere else, so removing it loses nothing.
    """
    if not path or not lib.Path(path).is_dir():
        return []
    blockers = []
    dirty = _git(path, "status", "--porcelain", check=False).strip()
    if dirty:
        names = ", ".join(line[3:] for line in dirty.splitlines()[:5])
        blockers.append(f"uncommitted changes: {names}")
    head = _git(path, "rev-parse", "--abbrev-ref", "HEAD", check=False).strip()
    if head:
        # Commits reachable from HEAD and from nowhere else — not on a remote, and not on any
        # other local branch. Excluding both matters: `--not --remotes` alone calls every commit
        # unique in a repo with no remote, and omitting other locals would flag a branch whose
        # work is already merged. What is left LOOKS like it disappears with this directory —
        # unless a squash merge already delivered the same content under a different SHA
        # (this repo squash-merges every PR), which `_squash_landed` checks for below before
        # this becomes a blocker.
        others = [b for b in _git(path, "branch", "--format=%(refname:short)",
                                  check=False).split() if b != head]
        only_here = _git(path, "log", "HEAD", "--not", "--remotes", *others, "--oneline",
                         check=False).strip()
        if only_here and not _squash_landed(path):
            blockers.append(
                f"{len(only_here.splitlines())} commit(s) on {head} and nowhere else")
    return blockers


def _default_branch(path):
    """The branch a squash-merged PR lands on, or None if it can't be told.

    `git symbolic-ref refs/remotes/origin/HEAD` is what `git clone` records and is
    authoritative wherever there is a remote. Nothing sets it in a plain `git init` repo
    (every scratch repo this is tested against, and any local-only worktree), so fall back to
    whichever of `main`/`master` exists locally. None (rather than a guess) tells the caller
    to fall back to the old reachability-only behaviour instead of comparing content against
    the wrong branch.
    """
    ref = _git(path, "symbolic-ref", "-q", "--short", "refs/remotes/origin/HEAD",
              check=False).strip()
    if ref:
        return ref
    for candidate in ("main", "master"):
        if _git(path, "rev-parse", "--verify", "--quiet", candidate, check=False).strip():
            return candidate
    return None


def _squash_landed(path):
    """Whether HEAD's own changes already exist on the default branch, squash or not.

    `git merge --squash` (what every PR here goes through) discards the branch's commit SHAs:
    the default branch gets one *new* commit with a matching tree, so plain reachability
    (`--not --remotes` above) still calls the branch's original commits unique. A raw
    `git diff <default> HEAD` at the current tip is not reliable either, once the default
    branch keeps moving after the squash lands — that diff then also carries whatever landed
    on the default branch afterwards, which has nothing to do with this branch (measured on
    #278: a squash-merged worktree showed a 19-file diff against main days later, entirely
    from main's own unrelated progress).

    So: build one synthetic commit for everything this branch has added since it forked
    (merge-base..HEAD, squashed into a single tree) and ask `git cherry` whether an equivalent
    *patch* already exists somewhere in the default branch's history. `cherry` compares by
    patch content, not tree state or SHA, so a later unrelated commit on the default branch
    can't hide the match the way a tip-only diff would.
    """
    default = _default_branch(path)
    if not default:
        return False
    merge_base = _git(path, "merge-base", "HEAD", default, check=False).strip()
    if not merge_base:
        return False
    tree = _git(path, "rev-parse", "HEAD^{tree}", check=False).strip()
    if not tree:
        return False
    synthetic = _git(path, "commit-tree", tree, "-p", merge_base, "-m", "_",
                     check=False).strip()
    if not synthetic:
        return False
    cherry = _git(path, "cherry", default, synthetic, check=False).strip()
    return cherry.startswith("-")


def release_worktree(root, task_id):
    """Remove the task's worktree. Returns (removed, reason-it-was-kept).

    Refuses rather than forcing: a worktree holding the only copy of a change is the one case
    where cleanup is indistinguishable from data loss. (False, "") means there was nothing to
    remove, which is not a failure — `finish` runs whether or not `start` made one.
    """
    path = worktree_path(root, task_id)
    if not path.is_dir():
        return False, ""
    blockers = worktree_blockers(path)
    if blockers:
        return False, "; ".join(blockers)
    _git(root, "worktree", "remove", str(path))
    return True, ""


def _session_or_die(args) -> dict:
    session = {
        "sessionId": getattr(args, "session_id", None),
        "transcriptPath": getattr(args, "transcript_path", None),
    }
    if not session["sessionId"]:
        resolved = lib.resolve_own_session()
        session["sessionId"] = resolved.get("sessionId")
        session["transcriptPath"] = session["transcriptPath"] or resolved.get("transcriptPath")
        session["source"] = resolved.get("source")
    else:
        # An explicit --session-id is a transcript id: attribute it to the transcript-reading
        # source so the right checkpoint maker is chosen. No default — a session source must
        # actually be registered (run welcome-ai-badger with one).
        session["source"] = lib.transcript_source()
        if session["source"] is None:
            print(
                "No session source that reads transcripts is registered (run "
                "welcome-ai-badger with an agent that provides one); an explicit "
                "--session-id cannot be attributed.",
                file=sys.stderr,
            )
            sys.exit(2)
    if not session["sessionId"]:
        print(
            "No session reference. No session source resolved this process and no session "
            "env var is set; pass --session-id/--transcript-path explicitly.",
            file=sys.stderr,
        )
        sys.exit(2)
    return session


def cmd_start(args) -> int:
    session = _session_or_die(args)
    source = lib.session_source(session["source"])
    if source is None:
        print(
            f"Session source {session['source']!r} is not registered (run welcome-ai-badger "
            "with an agent that provides it); nothing can checkpoint this session.",
            file=sys.stderr,
        )
        return 2
    checkpoint = source["checkpoint"](session)
    with lib.locked_store():
        tasks = lib.load_tasks()
        conflict = lib.find_other_entry_with_session(tasks, session["sessionId"], args.task_id)
        if conflict is not None and conflict.get("state") != lib.STATE_FINISHED:
            print(
                f"Session {session['sessionId']} is already attached to task "
                f"{conflict['taskId']!r} (state={conflict.get('state')}), which isn't finished "
                f"yet. Refusing to also attach it to {args.task_id!r} — this usually means "
                "current-session.json is stale (a hook didn't fire yet for the real new "
                "session). Pass --session-id/--transcript-path explicitly if this attachment "
                "is genuinely intended.",
                file=sys.stderr,
            )
            return 2
        entry = lib.find_entry(tasks, args.task_id)
        if entry is None:
            entry = {"taskId": args.task_id}
            tasks["tasks"].append(entry)
        if entry.get("state") == lib.STATE_FINISHED:
            print(
                f"Task {args.task_id} is already FINISHED; refusing to restart it.",
                file=sys.stderr,
            )
            return 2
        entry.update(
            {
                "title": args.title or entry.get("title", ""),
                "sessionId": session["sessionId"],
                "transcriptPath": session["transcriptPath"],
                "cwd": str(lib.PROJECT_ROOT),
                "branch": args.branch or entry.get("branch", ""),
                # Sticky: a resumed task re-runs `start` without repeating its flags, and gates
                # silently going back to full is the failure this switch exists to avoid.
                "risk": bool(args.risk or entry.get("risk", False)),
                "startedAt": entry.get("startedAt") or lib.now_iso(),
                "finishedAt": None,
                "state": entry.get("state") or lib.STATE_STARTED,
                # The session source that owns this session decides the resume invocation;
                # recorded so finish (a later process) can pick the same checkpoint maker.
                "trackingSource": session["source"],
                "resumeCommand": source["resume"](session["sessionId"]),
                "resumeAttempts": entry.get("resumeAttempts", []),
            }
        )
        lib.save_json(lib.EXECUTED_TASKS, tasks)

        usage = lib.load_usage()
        usage_entry = lib.find_entry(usage, args.task_id)
        if usage_entry is None:
            usage_entry = {"taskId": args.task_id, "subagents": [], "grade": None}
            usage["tasks"].append(usage_entry)
        usage_entry["sessionId"] = session["sessionId"]
        usage_entry["trackingSource"] = session["source"]
        checkpoints = usage_entry.setdefault("checkpoints", {})
        checkpoints.setdefault("start", checkpoint)  # keep the original start on re-runs
        checkpoints["latest"] = checkpoint
        lib.save_json(lib.TOKEN_USAGE, usage)

    # Outside the lock: this shells out to git, and holding the store lock across it would let a
    # slow checkout block every other tracker call.
    created = None
    if not args.no_worktree:
        try:
            created = ensure_worktree(lib.PROJECT_ROOT, args.task_id, entry.get("branch", ""))
        except (subprocess.CalledProcessError, OSError, ValueError) as exc:
            # State is already persisted; a missing git or a bad id must not turn that into a
            # traceback. OSError covers `git` being absent or not executable.
            detail = getattr(exc, "stderr", None) or exc
            print(f"could not create the task worktree: {detail}", file=sys.stderr)

    if args.no_cron:
        print(
            "--no-cron is deprecated and is now a no-op: cron installation is opt-in via "
            "--cron.",
            file=sys.stderr,
        )
    if args.cron:
        install_cron(quiet=True)

    print(
        json.dumps(
            {
                "taskId": args.task_id,
                "state": entry["state"],
                "sessionId": session["sessionId"],
                "worktree": str(created) if created is not None else None,
                "startContextTokens": checkpoint["contextTokens"],
            }
        )
    )
    print(
        f"REMINDER (SKILL.md Phase 1 step 3): ask the user to run `/rename {args.task_id}` now, "
        "so this session's label matches the task. Do not skip this silently.",
        file=sys.stderr,
    )
    return 0


def cmd_finish(args) -> int:
    with lib.locked_store():
        tasks = lib.load_tasks()
        entry = lib.find_entry(tasks, args.task_id)
        if entry is None:
            print(f"Unknown task {args.task_id}. Run start first.", file=sys.stderr)
            return 2
        if not args.force and not lib.state_json_updated_since(entry["startedAt"]):
            print(
                ".ai-badger/state.json has not been modified since task start "
                f"({entry['startedAt']}). Update it with what this task changed/learned, "
                "then re-run finish (or pass --force if the task genuinely produced no new "
                "knowledge).",
                file=sys.stderr,
            )
            return 3

        source = lib.session_source(entry.get("trackingSource") or lib.transcript_source() or "")
        if source is None:
            print(
                f"Task {args.task_id} has no registered session source (trackingSource "
                f"{entry.get('trackingSource')!r}); run welcome-ai-badger with an agent "
                "that provides one.",
                file=sys.stderr,
            )
            return 2
        checkpoint = source["checkpoint"]({
            "sessionId": entry.get("sessionId"),
            "transcriptPath": entry.get("transcriptPath"),
        })
        entry["state"] = lib.STATE_FINISHED
        entry["finishedAt"] = lib.now_iso()
        entry["stateJsonUpdated"] = lib.state_json_updated_since(entry["startedAt"])
        lib.save_json(lib.EXECUTED_TASKS, tasks)

        usage = lib.load_usage()
        usage_entry = lib.find_entry(usage, args.task_id)
        if usage_entry is None:
            usage_entry = {
                "taskId": args.task_id, "subagents": [], "grade": None, "checkpoints": {},
            }
            usage["tasks"].append(usage_entry)
        checkpoints = usage_entry.setdefault("checkpoints", {})
        checkpoints["finish"] = checkpoint
        checkpoints["latest"] = checkpoint
        start_cp = checkpoints.get("start", checkpoint)
        usage_entry["usage"] = lib.compute_usage(
            start_cp, checkpoint, usage_entry.get("subagents", [])
        )
        lib.save_json(lib.TOKEN_USAGE, usage)

    worktree = {"removed": False, "keptBecause": ""}
    if not args.keep_worktree:
        try:
            removed, reason = release_worktree(lib.PROJECT_ROOT, args.task_id)
        except (subprocess.CalledProcessError, OSError, ValueError) as exc:
            # Same reasoning as cmd_start: report it as a kept worktree, never a traceback.
            removed, reason = False, str(getattr(exc, "stderr", None) or exc)
        worktree = {"removed": removed, "keptBecause": reason}
        if reason:
            print(f"kept the task worktree — {reason}", file=sys.stderr)

    stats = lib.claude_md_stats()
    print(
        json.dumps(
            {
                "taskId": args.task_id,
                "state": lib.STATE_FINISHED,
                "worktree": worktree,
                "usage": usage_entry["usage"],
                "claudeMd": {
                    "overBudget": stats["overBudget"],
                    "chars": stats["chars"],
                    "lines": stats["lines"],
                },
            },
            indent=2,
        )
    )
    if stats["overBudget"]:
        print(
            f"CLAUDE.md is over budget ({stats['chars']} chars / {stats['lines']} lines, "
            f"limits {stats['maxChars']}/{stats['maxLines']}). Compact it now per the "
            "skill's compaction rules.",
            file=sys.stderr,
        )
    return 0


def cmd_grade(args) -> int:
    if not 0 <= args.grade <= 5:
        print("Grade must be 0-5.", file=sys.stderr)
        return 2
    with lib.locked_store():
        usage = lib.load_usage()
        entry = lib.find_entry(usage, args.task_id)
        if entry is None:
            print(f"Unknown task {args.task_id}.", file=sys.stderr)
            return 2
        entry["grade"] = args.grade
        entry["gradedAt"] = lib.now_iso()
        lib.save_json(lib.TOKEN_USAGE, usage)
    print(f"Grade {args.grade}/5 saved for {args.task_id}.")
    return 0


def cmd_subagent(args) -> int:
    if bool(args.total_tokens is not None) == bool(args.delegation):
        print(
            "Pass exactly one of <total_tokens> or --delegation <id>, not both.",
            file=sys.stderr,
        )
        return 2
    with lib.locked_store():
        usage = lib.load_usage()
        entry = lib.find_entry(usage, args.task_id)
        if entry is None:
            print(f"Unknown task {args.task_id}. Run start first.", file=sys.stderr)
            return 2
        record = {"description": args.description or "", "at": lib.now_iso()}
        if args.delegation:
            # The session source that recorded the task decides how delegation tokens are
            # read (an installed source may read them from its session store; a source that
            # records none yields None and the refusal below fires).
            source = lib.session_source(entry.get("trackingSource") or lib.transcript_source() or "")
            if source is None:
                print(
                    f"Task {args.task_id} has no registered session source (trackingSource "
                    f"{entry.get('trackingSource')!r}); run welcome-ai-badger with an agent "
                    "that provides one.",
                    file=sys.stderr,
                )
                return 2
            delegation_usage = source.get("delegation_usage")
            usage_data = delegation_usage(args.delegation) if delegation_usage else None
            if usage_data is None:
                print(
                    f"Delegation {args.delegation}: no token record in this session source "
                    "(unknown, not completed, or the source records no delegation tokens). "
                    "Refusing to record a fabricated number.",
                    file=sys.stderr,
                )
                return 2
            record.update({
                "totalTokens": usage_data["totalTokens"],
                "delegationId": args.delegation,
                "model": usage_data["model"],
                "apiCalls": usage_data["apiCalls"],
            })
        else:
            record["totalTokens"] = args.total_tokens
        entry.setdefault("subagents", []).append(record)
        # Recompute usage even if `finish` already ran — review-fix rounds and other subagent
        # work routinely land after the finish checkpoint, and usage must not go stale then.
        checkpoints = entry.get("checkpoints", {})
        start_cp = checkpoints.get("start")
        end_cp = checkpoints.get("finish") or checkpoints.get("latest")
        if start_cp and end_cp:
            entry["usage"] = lib.compute_usage(start_cp, end_cp, entry["subagents"])
        lib.save_json(lib.TOKEN_USAGE, usage)
    print(f"Recorded {record['totalTokens']} subagent tokens for {args.task_id}.")
    return 0


def cmd_reattach(args) -> int:
    session = _session_or_die(args)
    with lib.locked_store():
        tasks = lib.load_tasks()
        conflict = lib.find_other_entry_with_session(tasks, session["sessionId"], args.task_id)
        if conflict is not None and conflict.get("state") != lib.STATE_FINISHED:
            print(
                f"Session {session['sessionId']} is already attached to task "
                f"{conflict['taskId']!r} (state={conflict.get('state')}), which isn't finished "
                f"yet. Refusing to also reattach {args.task_id!r} to it — this usually means "
                "current-session.json is stale. Pass --session-id/--transcript-path explicitly "
                "if this attachment is genuinely intended.",
                file=sys.stderr,
            )
            return 2
        entry = lib.find_entry(tasks, args.task_id)
        if entry is None:
            print(f"Unknown task {args.task_id}.", file=sys.stderr)
            return 2
        entry["sessionId"] = session["sessionId"]
        entry["transcriptPath"] = session["transcriptPath"]
        entry["trackingSource"] = session["source"]
        source = lib.session_source(session["source"])
        if source is None:
            print(
                f"Session source {session['source']!r} is not registered (run welcome-ai-badger "
                "with an agent that provides it); nothing can resume this session.",
                file=sys.stderr,
            )
            return 2
        entry["resumeCommand"] = source["resume"](session["sessionId"])
        if entry.get("state") != lib.STATE_FINISHED:
            entry["state"] = lib.STATE_IN_PROGRESS
        lib.save_json(lib.EXECUTED_TASKS, tasks)
    print(f"Task {args.task_id} reattached to session {session['sessionId']}.")
    return 0


def _format_mix(model_mix) -> str:
    """Shortest honest rendering of the model mix: the biggest share and its model."""
    if not model_mix:
        return "-"
    model, share = max(model_mix.items(), key=lambda kv: kv[1])
    return f"{model.replace('claude-', '')}:{share:.0%}"


def cmd_status(_args) -> int:
    tasks = lib.load_tasks()["tasks"]
    usage = lib.load_usage()
    if not tasks:
        print("No tracked tasks.")
        return 0
    for entry in tasks:
        usage_entry = lib.find_entry(usage, entry["taskId"]) or {}
        usage_stats = usage_entry.get("usage") or {}
        totals = usage_stats.get("grandTotal")
        cache_eff = usage_stats.get("cacheEfficiency")
        grade = usage_entry.get("grade")
        # Only the reduced mode is named. A gate mode nobody can see is the dangerous one, and
        # printing "risk=off" on every full-gate task would train readers to skip the column.
        risk = " risk=on" if entry.get("risk") else ""
        print(
            f"{entry['taskId']:<12} {entry.get('state', '?'):<12} "
            f"started={entry.get('startedAt', '-')} finished={entry.get('finishedAt') or '-'} "
            f"tokens={totals if totals is not None else '-'} "
            f"cacheEff={cache_eff if cache_eff is not None else '-'} "
            f"mix={_format_mix(usage_stats.get('modelMix'))} "
            f"grade={grade if grade is not None else '-'}{risk}"
        )
    return 0


def _run_crontab(argv: list, **kwargs):
    """subprocess.run wrapper for crontab invocations: a missing binary is a reported
    condition (`CrontabUnavailable`), never a traceback.
    """
    try:
        return subprocess.run(argv, capture_output=True, text=True, check=False, **kwargs)
    except FileNotFoundError as exc:
        raise CrontabUnavailable(f"crontab executable not found: {exc}") from exc


def _current_crontab() -> str:
    """Return the user's current crontab text, or "" if they genuinely have none yet.

    Raises `CrontabUnavailable` for every other failure — a read failure must never be
    conflated with an empty crontab, since the caller may go on to write an authoritative
    replacement computed from this value.
    """
    result = _run_crontab(["crontab", "-l"])
    if result.returncode == 0:
        return result.stdout
    stderr = (result.stderr or "").strip()
    if _NO_CRONTAB_MARKER in stderr.lower():
        return ""
    raise CrontabUnavailable(stderr or f"crontab -l exited with status {result.returncode}")


def _desired_cron_line() -> str:
    script = shlex.quote(_cron_escape(str(lib.SCRIPT_DIR / "resume_cron.py")))
    log = shlex.quote(_cron_escape(str(lib.DATA_DIR / "resume.log")))
    return f"*/30 * * * * /usr/bin/env python3 {script} run >> {log} 2>&1 {CRON_MARKER}"


def _cron_escape(value: str) -> str:
    """Escape `%` for a crontab command field.

    cron(5): an unescaped `%` in the command is turned into a newline, and everything after
    it is sent to the command as stdin — a `%` in an interpolated path would silently
    truncate the job.
    """
    return value.replace("%", r"\%")


def install_cron(quiet: bool = False) -> int:
    """Install the resume cron job, correcting a stale marker line rather than skipping it.

    A marker line whose generated command no longer matches the current script/log paths
    (e.g. the skill moved on disk) is replaced in place, since the marker check alone would
    otherwise leave a dead cron entry pointing at a script that no longer exists.
    """
    try:
        current = _current_crontab()
    except CrontabUnavailable as exc:
        print(f"Not installing resume cron job: {exc}", file=sys.stderr)
        return 1
    desired_line = _desired_cron_line()
    lines = current.splitlines()
    marker_indices = [i for i, line in enumerate(lines) if CRON_MARKER in line]

    if marker_indices and all(lines[i] == desired_line for i in marker_indices):
        if not quiet:
            print("Resume cron job already installed.")
        return 0

    lib.ensure_data_dir()
    if marker_indices:
        new_lines = list(lines)
        first = marker_indices[0]
        new_lines[first] = desired_line
        for i in reversed(marker_indices[1:]):
            del new_lines[i]
        verb = "Updated"
    else:
        new_lines = lines + [desired_line]
        verb = "Installed"
    new_tab = "\n".join(new_lines) + "\n"
    try:
        result = _run_crontab(["crontab", "-"], input=new_tab)
    except CrontabUnavailable as exc:
        print(f"Not installing resume cron job: {exc}", file=sys.stderr)
        return 1
    if result.returncode != 0:
        print(f"Failed to install cron job: {result.stderr}", file=sys.stderr)
        return 1
    if not quiet:
        print(f"{verb} 30-min resume cron job.")
    return 0


def uninstall_cron() -> int:
    try:
        current = _current_crontab()
    except CrontabUnavailable as exc:
        print(f"Not modifying crontab: {exc}", file=sys.stderr)
        return 1
    kept = [line for line in current.splitlines() if CRON_MARKER not in line]
    try:
        result = _run_crontab(["crontab", "-"], input="\n".join(kept) + "\n")
    except CrontabUnavailable as exc:
        print(f"Not modifying crontab: {exc}", file=sys.stderr)
        return 1
    if result.returncode != 0:
        print(f"Failed to update crontab: {result.stderr}", file=sys.stderr)
        return 1
    print("Resume cron job removed.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    def add_session_args(p):
        p.add_argument("--session-id")
        p.add_argument("--transcript-path")

    p_start = sub.add_parser("start")
    p_start.add_argument("task_id")
    p_start.add_argument("--title", default="")
    p_start.add_argument("--branch", default="")
    p_start.add_argument(
        "--cron", action="store_true",
        help="Install the 30-min resume cron job (opt-in).",
    )
    p_start.add_argument(
        "--no-cron", action="store_true",
        help="Deprecated no-op: cron installation is opt-in via --cron now.",
    )
    p_start.add_argument(
        "--risk", action="store_true",
        help="Run automated quality gates in limited mode: formatting, fast tests and lint "
             "only, no e2e or integration suites. The full suite is still owed before "
             "integrating — the agent runs it, alone.",
    )
    p_start.add_argument(
        "--no-worktree", action="store_true",
        help="Record the branch without creating a worktree for it.",
    )
    add_session_args(p_start)

    p_finish = sub.add_parser("finish")
    p_finish.add_argument("task_id")
    p_finish.add_argument("--force", action="store_true")
    p_finish.add_argument(
        "--keep-worktree", action="store_true",
        help="Leave the task's worktree on disk instead of removing it.",
    )

    p_grade = sub.add_parser("grade")
    p_grade.add_argument("task_id")
    p_grade.add_argument("grade", type=int)

    p_sub = sub.add_parser("subagent")
    p_sub.add_argument("task_id")
    p_sub.add_argument("total_tokens", nargs="?", type=int,
                       help="Manual token count; mutually exclusive with --delegation.")
    p_sub.add_argument("--delegation",
                       help="Delegation id whose tokens the session source records.")
    p_sub.add_argument("--description", default="")

    p_re = sub.add_parser("reattach")
    p_re.add_argument("task_id")
    add_session_args(p_re)

    sub.add_parser("status")
    sub.add_parser("install-cron")
    sub.add_parser("uninstall-cron")

    args = parser.parse_args()
    if args.command == "start":
        return cmd_start(args)
    if args.command == "finish":
        return cmd_finish(args)
    if args.command == "grade":
        return cmd_grade(args)
    if args.command == "subagent":
        return cmd_subagent(args)
    if args.command == "reattach":
        return cmd_reattach(args)
    if args.command == "status":
        return cmd_status(args)
    if args.command == "install-cron":
        return install_cron()
    if args.command == "uninstall-cron":
        return uninstall_cron()
    return 2


if __name__ == "__main__":
    sys.exit(main())
