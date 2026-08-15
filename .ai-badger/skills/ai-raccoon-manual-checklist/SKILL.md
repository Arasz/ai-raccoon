---
name: ai-raccoon-manual-checklist
description: >-
  Use when hand-verifying a live AiRaccoon build — a pre-flight or release checklist, a manual
  smoke test after installing the global tool, a "does this actually work end to end" pass before
  shipping, or any question `dotnet test` cannot answer because it needs a real install, a real
  server and a real bank. Derives the version and tool surface from the product instead of pinning
  them, records the command and the output behind every answer, and writes the filled checklist to
  docs/work/checklist/.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [checklist, manual-testing, release, verification, evidence]
    related_skills: [ai-raccoon-memory, review-changes]
---

# AiRaccoon manual checklist

The hand-run pass over a live build: the things `dotnet test` cannot answer because they need a
real install, a real server and a real bank.

A checklist is only worth the evidence behind it. The whole design below exists to keep a filled
checklist distinguishable from a plausible-sounding one — read "How this checklist rots" before
adding, removing or "tidying" anything here, because every failure listed there was found in a
real predecessor and each is easy to reintroduce.

## When NOT to use

- Anything `dotnet test` already covers. This pass is for behaviour that only appears in a real
  install; duplicating unit or integration coverage by hand buys nothing and goes stale.
- Judging a diff or a PR — that is a code review, not a build verification.
- Debugging one failing symptom. Run the checklist to find out *what* is broken; trace *why*
  somewhere else.

## Never

These three protect the person running the checklist, not the checklist:

- **Never write to the user's live bank** at `~/.ai-raccoon`. Read it only through `?mode=ro` /
  `PRAGMA query_only=1`. The checklist's own writes go to a scratch data root you create with
  `--data-root`.
- **Never bind the default port** (7721) for a step that starts a server — the user's own server
  is usually on it, and `--restart` will cycle theirs. Use `--port 0` and read the bound port back.
- **Never mark an item accepted from a plan.** Accepted means you ran the command and read its
  output.

## Process

1. Copy `templates/checklist-template.json` to
   `docs/work/checklist/<yyyy-mm-dd>-<what-you-are-checking>.json`, creating the directory if
   needed. Results live under `docs/work/checklist/` — never the repo root, and never
   `.ai-raccoon/`, which is a bank directory, not a reports directory.

2. **Derive the facts the checklist compares against, before running anything**, and write them
   into `derived`. Run `scripts/derive-facts.sh` from the repo root; it prints the version, the
   MCP tool count and the prompt count, and it exits non-zero when any of them come back empty or
   zero rather than handing you a confident `0`. A count typed from memory or copied from a
   previous run is a second copy of a number that already exists, and it goes stale silently.

   The checklist then asserts that the *running binary* matches what the *tree* says. That
   comparison is the point: it is the only step that can catch a build that installed something
   other than what you think you are testing.

3. Work each item, filling in every field:
   - `command` — the exact command or tool call you ran.
   - `evidence` — the output you read, verbatim, trimmed to the deciding lines.
   - `observed-result` — what it means.
   - `status` — `pass`, `fail`, `skipped` or `substituted`. A `skipped` needs a reason; it is not
     a pass. `substituted` means the behaviour was checked by a different instrument than the item
     names — an automated test standing in for a path you could not drive live, say. Folding that
     into `pass` overstates it and into `skipped` understates it, so it gets its own word, and the
     reason must name the instrument. Record any deliberate deviation from the item's stated
     method the same way.
   - `accepted` + `acceptance-reason` — whether the observed result is acceptable, and why. A
     `fail` may still be accepted as a known, tracked defect, as long as the reason names where it
     is tracked.

   `accepted` starts as `null`, meaning nobody has answered yet. Leave it null until you decide;
   a run with any null left in it is unfinished, not a run with no objections.

4. **Never inherit a prior verdict's reason.** Where an item was `partial`, `fail`, `skipped` or
   `substituted` last time, re-derive *why* against the source in front of you before reusing the
   explanation. A previous run blamed a job's cadence for three event ids never firing; the real
   cause was that they log only when there is something to purge, and a fresh bank has nothing.
   The outcome matched, so the wrong mechanism survived a release — and it discouraged seeding the
   data that would have exercised them. A reason that is right about the outcome and wrong about
   the cause is the hardest kind of stale fact to see.

   When a run finds that an earlier one was wrong, that belongs in `findings-against-prior-runs`,
   not buried in an item. A checklist that can only describe the current build cannot report that
   the last checklist lied, and those findings are often the most valuable thing a run produces.

5. Items whose feature no longer exists get **deleted from the template**, not marked skipped. A
   step for a removed feature is worse than no step: it either fails forever and gets waved
   through, or it quietly "passes" against nothing.

6. Report counts by status, every `fail` with its evidence line, and every finding against a prior
   run. A run where some item has no `command` or no `evidence` is not complete — say so instead
   of reporting a total.

Items are independent, so lanes can run in parallel against one packed binary. The axis that keeps
them from colliding is the data root: give every lane its own `--data-root`, and nothing else needs
coordinating.

## Making an item able to fail

Most of these steps can be written so that they pass whether or not the feature works. Three
shapes account for nearly all of it:

- **A filter needs a negative control.** Feed clean content alongside the shapes you expect
  rejected. A policy that rejects *everything* passes a rejection-only test, and looks healthiest
  exactly when it is most broken.
- **A semantic-retrieval query must be one that keyword match cannot carry.** Query for
  "an antique navigation instrument reflecting evening light in a stargazing room" against content
  that says "astrolabe", "lamplight", "observatory" — no literal overlap, so a dead vector leg
  actually fails the item. A query sharing words with the stored text passes on BM25 alone and
  tells you nothing about the half you meant to test.
- **An empty list is a weak check.** A queue or candidate list on a fresh bank returns `[]`
  whether it works or is broken. Create the thing first, then assert on its content — the score,
  the reasons, the identity — not on the shape of the response.

## Scope

Derived per run from the product, not from this list. The headings below are the stable shape of
the pass; the specific steps under each come from what step 2 found and from what is actually
registered in the build in front of you.

- **Build and install** — Release build, pack, force-update the global tool, and `--version`
  matches the derived version.
- **Server lifecycle** — the server starts and `--restart` cycles the one it finds, both on a
  non-default port.
- **Write path** — a write stores and returns a hash; a rejected write says so, with a reason,
  rather than returning a fabricated entry.
- **Read path** — search returns the written entry, get returns its content by hash, and a
  `file#section` anchor resolves its exact chunk.
- **Noise filtering** — each *registered* write-path policy rejects what it claims to, and the
  rejected content stays retrievable from the noise store. Check which policies are registered
  before writing steps for them.
- **Read-path query guard** — the refuse and annotate tiers behave as specified, and any detector
  that ships disabled is still disabled until explicitly armed.
- **File watch** — watch status reflects live registrations.
- **Promotion queue** — the promotion list reports candidates accurately.
- **Full MCP surface** — every derived tool and prompt is reachable.
- **Observability** — emitted event ids resolve against the logging event-id reference.

Anchor each item to the decision record that defines the behaviour, so a step whose ADR was
superseded is easy to spot and delete.

## How this checklist rots

Two predecessors were deleted after both drifted the same six ways. Each defence below is here
because its absence already caused a silent failure:

- **Facts pinned by hand.** One asserted `--version → 1.9.1` and "25 tools" while the tree was at
  1.12.0 with 26 tools. The pins had been wrong for three releases and nothing noticed, because
  the only thing comparing them was a human reading two numbers. → step 2 derives them.
- **Steps for a deleted feature.** Both still tested a noise policy that had been removed by a
  later ADR. A step whose subject does not exist cannot pass honestly. → step 4 deletes them.
- **Results written into a bank directory.** Reports landed in `.ai-raccoon/`, the directory name
  the product uses for banks. → step 1 fixes the destination.
- **No evidence field.** The template recorded a claim with no room for the command or the output
  behind it, so a filled checklist and an invented one were indistinguishable afterwards. →
  `command` and `evidence` are required.
- **Booleans that cannot express "skipped".** `checked`/`accepted` flags collapsed three states
  into two: an unrun item and a failed one both read as `false`/`false`. → `status` is a tri-state
  and `accepted` starts null.
- **Two drifting copies.** Two directories each held a copy, and one had lost its `templates/`
  directory entirely, so its own step 1 pointed at a file that was not there. → one copy, and the
  template ships beside the skill.

**Retiring a checklist skill means deleting it from every root it can load from**, not from the
one you were looking at. The deletion that removed the two `.ai-badger/skills/learned/` copies
missed `~/.hermes/skills/`, where a third copy is still installed, still carries its `templates/`,
and still pins an expected version three minors stale. It was the *first* hit for someone
searching for this checklist, and they began executing it. Enumerate the roots — the project's
`.ai-badger/skills/learned/`, `.claude/skills/`, `~/.claude/skills/`, `~/.hermes/skills/` — and
confirm the removal in each. The stale copy wins whoever searches first, so a copy you did not
delete is not dormant; it is the one in use.

## Gotchas

- `grep -c` across several files prints one count per file; summing them wrongly, or pointing the
  glob at a path that no longer exists, both yield a plausible number instead of an error. That is
  why step 2 goes through the script — it fails loudly on an empty match.
- `--port 0` binds an ephemeral port, so the port must be read back from the server's own output.
  Assuming a port here is how a checklist step ends up talking to somebody else's server.
- Force-updating a global tool can silently keep the previous build if the pack step failed
  earlier in the same run. The version comparison in step 2 is what catches it; do not skip it
  because the build "looked fine".
- A scratch `--data-root` must be a path the running user can create. Pointing it inside a
  read-only or root-owned directory produces failures that look like product bugs.
