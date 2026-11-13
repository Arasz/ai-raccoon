---
name: run-manual-checklist
description: >-
  Use when a live build has to be verified by hand against the project's own checks file — a
  pre-flight or release checklist, a manual smoke test after an install, "does this actually work
  end to end", or any question the automated suite cannot answer because it needs a real install,
  a real server or real data. Requires the path to the project's checks file
  (e.g. docs/checklist/checks.json); with `init`, writes that file for a project that has none.
  Not for judging a diff (review-changes) or tracing one failure (debug-issue).
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: optIn
metadata:
  hermes:
    tags: [verification, manual-testing, release, checklist]
    related_skills: [design-tests, review-tests, evidence-first-research, decision-record]
---

# run-manual-checklist

The hand-run pass over a live build. The framework ships the rules and the mechanics; **the
project owns the checks.** They live in the project's own checks file, which a framework refresh
never touches, and change in the project's own pull requests.

A checklist is only worth the evidence behind it. Everything below exists so that a filled run
can be told apart from a plausible-sounding one.

## Inputs

- `checks=<path>` — **required.** The project's checks file, normally `docs/checklist/checks.json`.
  Without it, stop and ask for the path. Do not search for a template, and do not reuse a copy
  from another project or an older run.
- `init [path]` — the only call without an existing file. It writes the project's first checks
  file (see **Init**).
- Optional: a scope (item ids) when the owner asks for a partial pass.

`scripts/checklist.py` beside this file does the mechanical work. Run it; do not repeat its work
by eye.

| Command | Does |
|---|---|
| `checklist.py check <checks>` | Validates the checks file: unique kebab-case ids, non-empty expected results, existing anchors, a command for every fact, a non-empty item list |
| `checklist.py new-run <checks> --slug <what>` | Writes `<results-dir>/<date>-<slug>.json`: git metadata, every item blank, and every fact derived by running its command. Fails loudly when a fact command errors or prints nothing, or prints `0` for a fact marked `nonzero` |
| `checklist.py report <run> --checks <checks>` | Validates a filled run against the checks file and prints the counts, every fail with its evidence, every skip or substitution with its reason, every rejected result and every pending acceptance. Exit 0 complete, 1 incomplete, 2 usage, 3 acceptances pending, 4 complete but some results rejected |

## When NOT to use

- Anything the automated suite already covers. Repeating unit or integration coverage by hand
  buys nothing and goes stale.
- Judging a diff or a PR. That is a code review.
- Debugging one failing symptom. Run the checklist to find out *what* is broken; trace *why*
  somewhere else.

## Never

These protect the person running the checklist, not the checklist:

- **Never write to live data.** Every write goes to a scratch location you created. Read live
  data only through read-only handles.
- **Never bind a port or resource a real instance may hold.** Use an ephemeral one and read back
  what was bound. An assumed port is how a step ends up talking to someone else's server.
- **Never mark an item accepted from a plan.** Accepted means you ran the command and read its
  output.
- **Then the project's own `rules`.** `new-run` prints them. They are project facts (the live
  data path, the default port, the read-only flag), and they bind you exactly as the three above
  do.

## Process

1. `checklist.py check <checks>`. Fix a red result in the checks file first.
2. `checklist.py new-run <checks> --slug <what-you-are-checking>`. It writes the run file and
   derives the facts. **Never type a fact by hand** or copy one from an earlier run. A pinned
   fact is a second copy of a number that already exists, and it goes stale silently. When a
   fact command fails, the fact script is wrong. Fix it in the project; do not type the value.

   The run then asserts that the *running build* matches what the *tree* says. That comparison
   is the point: it is the only step that catches an install of something other than what you
   think you are testing. Prove the build's identity by something that cannot collide (a commit
   hash or a file digest), not by a version string alone.
3. Work each item. Fill in every field:
   - `command`: the exact command or tool call you ran.
   - `evidence`: the output you read, verbatim, trimmed to the deciding lines.
   - `observed-result`: what that output means.
   - `status`: `pass`, `fail`, `skipped` or `substituted`.
     - `skipped` needs a reason. It is not a pass.
     - `substituted` means you checked the behaviour with a different instrument than the item
       names, for example an automated test standing in for a path you could not drive live. Its
       reason must name that instrument.
   - `accepted` and `acceptance-reason`: whether the observed result is acceptable, and why. A
     `fail` can be accepted as a known defect when the reason names where the defect is tracked.
     `accepted` stays `null` until someone decides. A run with a `null` in it is unfinished. It
     is not a run with no objections. `false` means the result is not acceptable; the report
     lists it under Rejected and exits 4.
     Older runs may carry `acceptation-reason` or `skip-reason`; `report` reads them as the
     reason.

   Items are independent, so lanes can run in parallel. Give every lane its own isolation axis
   (its own scratch data, port and install path), and nothing else needs coordinating.
4. **Never inherit an earlier verdict's reason.** When an item was `fail`, `skipped` or
   `substituted` last time, find out *why* again from the source in front of you before you
   reuse the explanation. A reason that is right about the outcome and wrong about the cause is
   the hardest kind of stale fact to see.

   When this run finds that an earlier run was wrong, record it in
   `findings-against-prior-runs`, not inside an item. Those findings are often the most valuable
   thing a run produces.
5. When an item's feature no longer exists, **delete the item from the checks file** in the
   project's PR. Do not mark it skipped. A step for a removed feature either fails forever and
   gets waved through, or quietly passes against nothing.
6. For a scoped pass, write the ids you ran into `run.scope` as a list of strings. `report
   --checks` then expects exactly those ids: it flags a scoped id missing from the checks file,
   a scoped id missing from the run, and a run item outside the scope.
7. `checklist.py report <run> --checks <checks>`. Its output is the report: counts by status,
   every fail with its evidence, every rejected result, and every finding against an earlier
   run. Exit 1 means the run is not complete, 3 that acceptances are pending, 4 that some
   results were rejected. Say so instead of reporting a total.

## Making an item able to fail

Most steps can be written so that they pass whether or not the feature works. Four shapes cause
nearly all of it:

- **A filter needs a negative control.** Feed in clean content next to the shapes you expect to
  be rejected. A policy that rejects *everything* passes a rejection-only test, and it looks
  healthiest exactly when it is most broken.
- **Choose an input that the cheap path cannot carry.** When the item checks the clever half of
  a system (semantic search, a fallback, a cache miss), choose an input that the simple half
  cannot satisfy on its own. Otherwise a dead clever half passes.
- **Judge a result by how it was produced, when the output says so.** If the response reports
  which path, leg or source answered, require the path the item is about.
- **An empty list is a weak check.** A queue or a list on fresh data is `[]` whether the feature
  works or not. Create the thing first. Then assert on its content (identity, scores, reasons),
  not on the shape of the response.

Put the negative control into the item as `negative-control` when it is not obvious.

## Init

Run when a project opts in and has no checks file. The agent supplies the judgement; the script
only validates.

1. Read the project's README, its decision records, its how-to docs and the edges of its test
   suite. List the behaviours that **only a real install can prove**: install and identity, start
   and restart, the main write and read paths, persistence across a restart, refusal paths,
   migrations or upgrades, and the integration surface (CLI, API, tools).
2. For every fact a run compares against (the version, the size of a public surface), write a
   **command** that derives it from the tree, and mark `nonzero` where zero means "broken". If no
   command exists, write a small script in the project and put its tests on the project's CI.
   Never write a literal value.
3. Write each item: a kebab-case `item`, an `expected-result` concrete enough to fail, an `anchor`
   to the document that defines the behaviour, and a `negative-control` where needed. Use the
   shapes in **Making an item able to fail**.
4. Write the project's `rules`: where the live data is, which ports and resources belong to a
   real instance, how to read live data read-only.
5. Set `results-dir`. The default is `docs/work/checklist`. Never use a directory the product
   uses for its own data.
6. Run `checklist.py check` until it passes. Then run a first pass, because a checks file that
   has never been run has not been proven.

The shape, with two generic items, is in `examples/checks.json`.

## Keeping the checks current

- A change that adds or removes a behaviour edits the checks file in the same PR. Anchor each
  item to its decision record, so an item whose record was superseded is easy to find.
- The checks file is the only copy. Do not keep a second one in a skill directory, a template or
  another root. Two copies drift, and the stale one is found first.
- When you retire a checklist skill, delete it from every root it loads from (the project's
  skill directory, `.claude/skills/`, `~/.claude/skills/`, `~/.hermes/skills/`). A copy you did
  not delete is not dormant. It is the one the next search finds first.

## How a checklist rots

Earlier checklists were deleted after they drifted in the same ways. Each defence here exists
because its absence once caused a silent failure:

- **Facts pinned by hand** went stale for three releases. → `new-run` derives them.
- **Steps for deleted features** could not pass honestly. → Delete them from the checks file.
- **Results written into a data directory** that the product owns. → `results-dir`, with a rule.
- **No evidence field**, so a filled checklist and an invented one looked the same. → `report`
  refuses an item with no `command` or `evidence`.
- **Booleans that cannot say "skipped"** made an unrun item and a failed one look the same. →
  A four-state `status`, and `accepted` starts `null`.
- **Two drifting copies**, one of them inside the framework, overwritten at every refresh. → One
  project-owned checks file.

## Gotchas

- **A count that cannot fail is not a count.** Every counting primitive has a silent zero. A
  glob over a moved directory yields nothing, and a sum of nothing is 0. Mark such facts
  `nonzero`, and make the fact script raise rather than return 0.
- **An update can silently keep the previous build.** A failed pack or a prerelease that sorts
  below the installed release leaves the old binary in place. Identity by hash catches this.
- **Do not trust a long-running process to be the new build.** A rebuild does not restart a
  running server. Check what the process actually loaded.

## Verification checklist

- [ ] `checklist.py check` exits 0 on the checks file used
- [ ] The run file was written by `new-run`, and no fact was typed by hand
- [ ] Every item has `command` and `evidence`, read from output you saw
- [ ] `checklist.py report <run> --checks <checks>` exits 0, 3 or 4 (never 1 or 2), and its output is what was reported
- [ ] Findings against earlier runs are recorded in their own block
