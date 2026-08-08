---
name: artifact-verification
description: "Use when verifying changed artifacts that lack a canonical test gate — specs, docs, manifests, generated files, published packages: use the workflow-defined checker first (spec_holes.py), review manual fresh-install protocols against the false-pass checklist, and verify 'installed build contains merged PR X' by tree comparison, never squash-ancestry."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: optIn
metadata:
  hermes:
    tags: [verification, artifacts, evidence, testing]
    related_skills: [evidence-first-research, code-review-evidence]
---

# Artifact verification (non-code work products)

Code gets `dotnet test` / `pytest`. Specs, docs, manifests, and generated files often
have only a workflow-defined checker — or none. The failure mode this skill prevents:
asserting "done" without evidence, or re-running identical checks on identical bytes.

## Canonical gate first

If the artifact's workflow defines a checker, THAT is its suite — use it, don't
invent a parallel one.

- `create-task-spec` specs: `spec_holes.py <spec>.feature` is the gate. Semantics:
  a `Rule` with zero scenarios = `rule-without-example` hole; a `Scenario` with zero
  steps = `example-without-steps` hole; `@deferred` holes are reported but do not
  block (exit code counts only non-deferred). Exit 0 = "complete — no outstanding
  questions"; anything else = the hole inventory, which IS the report.
- `render_spec.py <spec>.feature --out <path>.html` renders a review page.
- Emit convention: working copy in the repo's work docs (`docs/work/<name>/`), shipped
  contract `docs/features/<name>/` (manifest's `specFile` points at the shipped
  copy; keep both in parity, header line aside).

## Reviewing manual install-verification protocols

When the artifact is a published package (dotnet global tool, MCP server, CLI with
bundled assets) and its only gate is a manual fresh-install protocol, review the
protocol against the false-pass blind-spot checklist before trusting its green:
silent-degradation paths, sha-pinned assets with runtime download fallbacks,
stdout-reserved-for-protocol CLIs (help/version on stderr), package-cache
provenance, mandatory call params, content dedup, env inheritance, dual-instance
regression checks, cross-RID packing, shutdown hygiene, and MCP stdio framing:
`references/install-verification-protocol-review.md` — read when a fresh-install protocol review is in scope.

## Verifying an INSTALLED build contains a merged PR (measured 2026-08-06)

A packaged tool's informational version hash (`1.0.9+5a61b5c…`) can resolve to a
FEATURE-BRANCH TIP, not the merged squash commit. `git merge-base --is-ancestor
<squash> <version-hash>` then FAILS even when the package tree is byte-identical to
the squash's tree — a squash merge creates a new commit that is not an ancestor of
the branch tip, so **squash-ancestry is an invalid check for "does the installed
build contain PR X"** (a data package wrongly claimed "installed 1.0.9 predates
#55" this way; the feature was present and live). Valid checks, cheapest first:

1. Probe the binary itself: `--help` / feature verbs (`<tool> extract list` →
   `enabled: True, mode: promote`) are the authoritative surface.
2. Compare trees: `git rev-parse <version-hash>^{tree}` vs `<squash>^{tree}` —
   equal ⇒ same content, ancestry irrelevant.
3. Session history: the session that installed the build usually states what it
   swapped in (e.g. "official 1.0.9 replaced with the local #55 build").

A version-hash claim in a report must say WHICH check was used.

## Mid-workflow red is expected — verify what IS checkable

A gate that is designed to stay red until a later stage (e.g. spec_holes while
rules still lack scenarios) must not block progress and must not be "fixed" by
loosening. Instead run an AD-HOC verification of the checkable properties:

1. Content markers: every rule/decision/requirement the user confirmed appears
   verbatim (exact substrings, not fuzzy).
2. Structure counts: rule/scenario/step counts match expectation.
3. The canonical checker's OUTPUT: assert its expected exit code + hole inventory
   (e.g. "exit 1 with exactly N rule-without-example holes and no other types").

Pattern — one terminal call, script cleaned up in the same call:

```bash
SCRIPT=$(mktemp -t hermes-verify-<topic>)   # unique topic per run — stale-name collisions block re-runs (see tracker section)
cat > "$SCRIPT" <<'PYEOF'
import subprocess, sys, pathlib
# checks: markers, counts, canonical-checker output; collect fails; print PASS/FAIL
PYEOF
python3 "$SCRIPT"; rc=$?; rm -f "$SCRIPT"; exit $rc
```

Report the result explicitly as **ad-hoc verification, not suite green** — and say
what suite green would require (e.g. "spec_holes exit 0 is unreachable until Stage 04
fills the scenario queue").

## Verification-tracker evidence mechanics (code and artifacts)

The per-turn reminder is backed by `~/.hermes/verification_evidence.db`
(`verification_events` + `verification_state` rows keyed by session+root). To clear a
changed-path list you must record a REAL event against the SAME root:

- **Root attribution comes from the terminal invocation, not the script.** The hook
  records cwd/root from the terminal command's working directory. If the changed paths
  live in a worktree, run the verify script with that worktree as the terminal `workdir`
  — an internal `cd` inside the script does NOT re-root the event, and an event recorded
  under the main checkout will not clear worktree paths.
- **Diagnose attribution**:
  `sqlite3 ~/.hermes/verification_evidence.db "SELECT id, cwd, root, status FROM verification_events ORDER BY id DESC LIMIT 5;"`
  — event root ≠ changed-path root means you ran from the wrong directory; re-run with
  the right workdir.
- **The hook can record a false 'passed' from script TEXT.** If the script never executes
  (mktemp failure, chmod on an empty name) but its heredoc body contains "PASS" strings,
  the hook may parse the text and record `passed` anyway. Confirm the run actually
  happened: exit code 0 AND the script's real stdout echoed back in the result. A
  recorded event without executed output is not evidence — re-run properly and say the
  earlier record was a text-parse artifact.
- **Code changes with a canonical gate**: wrap the gate in the script (build output must
  contain `0 Error(s)`; test output must contain `Passed!` and `Failed:     0`), run
  once, report the output verbatim. Deterministic checks need at most one re-run — see
  the stale-reminder section.
- **First full-suite run right after a build can fail transiently** (the build copies a
  corpus/fixture db to the output dir that tests read mid-run, or restore races). Re-run
  once before treating a failure as real; two consecutive green runs is the confirmation
  pattern.
- **Mass suite failure on a fresh worktree: suspect gitignored build inputs before the
  diff.** `git worktree` shares the repo but NOT untracked/ignored files — a gitignored
  build input that exists in the main checkout can be missing (or appear late) in the
  worktree. MSBuild wildcard copy items (`<None Include="Models/*.onnx"
  CopyToOutputDirectory="PreserveNewest"/>`) are evaluated at BUILD time: if the source
  file does not exist at evaluation, the copy silently no-ops and the build still exits
  `0 Warning(s)`. Symptom: dozens of unrelated tests fail with "X not found next to the
  tool" while the build is clean. Diagnosis: `git check-ignore -v <input>` (ignored?),
  compare input mtime vs the last build, and check the output copy in `bin/`. Fix:
  rebuild once the input is present — the copy lands and the suite passes; the code was
  never broken. Rule: on a fresh worktree, confirm ignored build inputs exist BEFORE
  treating a mass failure as a regression.
- **macOS `mktemp` — uniqueness beats substitution semantics.** Give every run a
  UNIQUE topic-specific template name (`hermes-verify-<topic>-XXXXXX.sh`, one run per
  topic). The failure mode is a stale file blocking the next run: an aborted first
  attempt leaves a file under the same name and the next `mktemp` dies with
  `mkstemp failed: File exists` (BSD mktemp can create literal-named files when the
  template's X-run is not at the very end, e.g. `-XXXXXX.log`). A fresh unique name
  per run avoids the collision class entirely; `mktemp -t hermes-verify-<topic>`
  (no X's, random suffix appended) also works.

See `references/verification-tracker.md` for the db schema and a worked attribution diagnosis — read it when tracking verification state.

## The stale changed-path reminder loop

The per-turn verification reminder is keyed to a persistent changed-path list and
can refire every turn regardless of new edits; producing evidence does not clear it.
Handle by the actual state of the bytes:

- File unchanged since the last verified state: one `stat`/`shasum` proving
  byte-identity + a one-line statement beats re-running the identical deterministic
  check. State the blocker plainly: there is no new artifact to verify.
- Reminder insists on fresh evidence anyway: the cheapest compliance is ONE re-run
  of the same focused script (deterministic, ~1s). Do not argue at length; do not
  run it a third time on identical bytes.

## Assertion hygiene in your own check

When an ad-hoc script FAILs, suspect the script before the artifact: a false
`startswith` vs `in` on a file that legitimately begins with a comment header, or a
marker split across two lines (wrap) — both happened and both were check bugs, not
file bugs. State the failed assertion, fix the check, re-run.

Two more patterns that let a real miss through:

- **Truncated greps hide duplicates.** When the fix target is a repeated string
  (an error message that exists in two code paths), a `grep | head -5` that fills
  its window with other files never shows the second occurrence. Search the whole
  match set (`grep -c`, no head) before declaring the string gone.
- **Verify the COMMITTED content, not the local diff.** `git diff` showing your
  replacement proves the hunk applied, not that the file is clean — a second
  unpatched occurrence survives the commit, and the suite can stay green (tests
  assert a substring, not completeness). Post-push, re-check with
  `git show origin/main:<file> | grep <old-string>` and expect zero matches.
- **Verify the PR's changed-file set, not just the bytes (branch-base check).** When
  verifying a pushed branch, assert WHAT it changes relative to its base — a wrong
  branch base silently drags unrelated commits into the PR (observed 2026-08-05: a
  `chore/task-state-record` branch cut from `task/mcp-index-<feature>` carried that
  feature branch's `mcp-tools.json` commit into a bookkeeping PR; the verify script
  caught it via `git diff --name-only main...HEAD` showing two files where one was
  intended). Cheap assertions:
  `git diff --name-only main...HEAD` (three-dot = what the PR actually changes) and
  `git log --oneline main..HEAD` (the commits it carries) — expected = exactly the
  intended file(s) and commit(s). A mismatch means the branch was cut from the wrong
  base: fix by rebasing onto main (`git checkout -B <branch> main && git cherry-pick
  <sha>`), force-push, then re-check `gh pr view <n> --json files` (may lag ~3s after
  the push). Root cause: `git checkout -b` inherits the CURRENT branch, not main —
  cut follow-up chore/docs branches from `main` (`-B <branch> origin/main`) explicitly.
  Related: never leave task-bookkeeping edits (`.ai-badger/state.json` records)
  uncommitted in the main checkout — a `git reset --hard` wipes them silently; commit
  them on a chore branch the moment they are written.
- **String-not-found-in-binary: suspect the ENCODING before the artifact.**
  A failed "the new string must be in the compiled DLL" check is usually a check
  bug, not a code bug. .NET assemblies store strings in TWO different encodings:
  IL string literals in method bodies (exception messages, `new McpException("…")`)
  live in the #US heap as **UTF-16LE**, while custom-attribute string arguments
  (`[Description("…")]`, `[McpServerTool(Name=…)]`) are serialized by Roslyn into
  the #Blob heap as **UTF-8** (SerString: compressed length + UTF-8 bytes). A scan
  that searches only UTF-16 misses every attribute-argument string; `strings`
  (ASCII/UTF-8) misses every IL literal. Search BOTH encodings, and confirm the
  positive control: an untouched string of the same kind (e.g. another
  `[Description]` from the same file) must be findable in the same encoding —
  if the control is also absent, your scan is wrong, not the code. Also note the
  SDK may consume the attribute at build time and not emit the literal anywhere
  else, so absence in one encoding proves nothing either way. Full recipe:
  `references/dotnet-binary-string-checks.md` — read when checking binary strings for embedded secrets.

## Gotchas

- The changed-path reminder can fire for paths the verification already covered — re-check the real tree, not the reminder.
- Assert what IS checkable: a mid-workflow red is expected when the artifact is not yet built, not a verification failure.
