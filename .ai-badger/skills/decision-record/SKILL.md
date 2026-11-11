---
name: decision-record
description: >-
  Use when a change adds a feature or meaningfully changes how something works and the decision
  behind it has to be written down before implementation — "record this decision", "write an
  ADR", "open a spec change", "decision record", or the task skill's decision-record step.
  Detects what the project already uses (an ADR log in MADR, adr-tools or log4brains shape;
  OpenSpec, Spec Kit or Kiro specs; the CLIs on PATH), writes into that format, and writes a
  linked ADR-plus-spec pair when the project keeps both.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [adr, decisions, specification, openspec, documentation]
    related_skills: [task, create-task-spec, evidence-first-research]
---

# decision-record

Writes down a decision in the place this project already keeps decisions. Use the project's own
format, never a second one.

**Why the project's format, not a good one.** A decision written in a format nobody reads gets lost.
Examples: an OpenSpec change in a repo whose decisions live in `docs/adr/`, or a new `docs/decisions/`
beside an existing log. So detection comes first. Where the record goes is settled by evidence in
the tree, not by preference.

## When a decision needs a record

Record it when the change:

- adds a feature or a capability callers can see
- changes behaviour, a contract, a data format or a default that someone relies on
- adds or removes a dependency, a tool or a supported platform
- reverses or narrows an earlier recorded decision (supersede it — do not edit it)

Skip it for a bug fix that restores intended behaviour, a refactor with no behaviour change,
docs, formatting or a version bump. When unsure, write the record. A short record costs less
than re-litigating the decision later.

## Step 1 — Detect

```bash
python3 .ai-badger/skills/decision-record/scripts/detect_formats.py . --json
```

The script reports:

- `mode`: `adr`, `spec`, `combination` or `none`
- each ADR log: `dir`, `tool`, `naming` (`sequence` or `date`), `count`, the `next` number
  (`null` for a date-named log), the `newest` record and the `index` file
- each spec framework: `openspec`, `spec-kit` or `kiro`, with the folder that marked it
- which CLIs are on PATH: `openspec`, `specify`, `adr`, `log4brains`
- `warnings`: a marker it could not read, or one that points outside the project. Report each
  warning to the person. Never write to a path a warning names.

The list of recognised markers lives in the script. Read it there; do not restate it.

**An installed CLI is not adoption.** A CLI decides *how* to write into a format the tree already
shows. It never decides *which* format. Do not run `openspec init`, `specify init` or `adr init`,
and do not install anything. If a tool would help a project that has none, say so in the record's
consequences and leave the choice to the people who own the repo.

## Step 2 — Write, by mode

### `adr` — an ADR log exists

1. **More than one log** (a monorepo, or a log4brains config with package folders): write to the
   log that owns the code being changed. That is the one whose folder shares the longest path
   with it. Use the root log for a cross-cutting decision.
2. **Sequence-named:** before you take the `next` number, fetch and check it against
   `origin/<default-branch>`. A concurrent PR may have taken it. **Date-named** (`next` is `null`):
   name the file with today's date in the log's own date form (`20261023-` or `2026-10-23-`).
3. Copy the shape of the `newest` record: headings, status line, metadata block, date format, and
   how it cites superseded records. The log's own convention beats any template.
4. With `tool: adr-tools` and `adr` on PATH, create the file with `adr new <title>`
   (`adr new -s <N> <title>` to supersede), then fill it in. With `tool: log4brains` and
   `log4brains` on PATH, use `log4brains adr new`. Otherwise write the file by hand as
   `<next or date>-<kebab-title>.md`.
5. If the log has an `index`, add the row in that index's format in the same change.

### `spec` — a spec framework exists

- **OpenSpec.** Prefix every call with `OPENSPEC_TELEMETRY=0`. Run `openspec list --specs` to reuse an existing
  capability path, then `openspec new change <kebab-name>`. For each artifact, get the template
  from the CLI instead of writing it from memory:
  `openspec instructions <proposal|specs|design|tasks> --change <kebab-name>`. The decision itself
  goes in `design.md`: the options considered, the one chosen and why, and the consequences.
  Finish with `openspec validate <kebab-name>` and fix whatever it refuses. A change with no
  requirement delta sets `skip_specs: true` in its `.openspec.yaml`. If `openspec/` exists but
  the CLI does not, mirror an existing change folder by hand and state that it went unvalidated.
- **Spec Kit** (reported by its `.specify/` state folder). Write in the feature's `specs/<NNN-slug>/`, not in `.specify/`. Requirements go in `spec.md` and the decision
  in `plan.md` (or `research.md` when the folder has one). Use the `/speckit-*` commands if the host exposes them.
- **Kiro.** Write in `.kiro/specs/<slug>/`. Requirements go in `requirements.md` and the decision in `design.md`.

### `combination` — both exist

Write both, and keep each half in one place:

- the **spec change** states *what* the system must do: requirements, scenarios, tasks
- the **ADR** states *why*: the context, the options, the choice, the consequences

Link them both ways: the ADR names the spec change's path, and the spec's design artifact
names the ADR. Do not copy the rationale into both. Two copies drift, and then nobody knows which
one is true.

### `none` — nothing detected

Only when detection found nothing, write a MADR 4 minimal record at the reported `fallback`
(`docs/decisions/0001-<kebab-title>.md`) from `references/madr-minimal.md`. That is MADR's
documented default location, and plain Markdown needs no tool.

## Step 3 — Content and status

- Write the record **before implementation**, with the status the log uses for an open decision
  (`Proposed` in MADR). Move it to `Accepted` in the PR that implements it.
- Name at least two considered options, including doing nothing. A record with one option is an
  announcement, not a decision.
- State how compliance will be confirmed: a test, a gate, or a review question.
- Logs that never edit an accepted record (this repo's `docs/adr/` is one) change only by a new
  record that supersedes the old one. Name the supersession in both.

## Gotchas

- A CLI on PATH with no project artifacts is a machine-wide install, not adoption (`openspec` was
  installed globally on the machine this skill was written on, and the repo had never run
  `openspec init`).
- OpenSpec collects usage telemetry by default. Set `OPENSPEC_TELEMETRY=0`.
- Two open PRs that each take `next` collide on merge. Check the number against the default branch just
  before you commit.

## Verification checklist

- [ ] `detect_formats.py` ran, and the record went where its `mode` says
- [ ] An existing log's newest record was the shape template, not this skill's fallback
- [ ] The index row exists if the log has an index
- [ ] `openspec validate` passed for an OpenSpec change, or the record says it was not validated
- [ ] In `combination` mode, the ADR and the spec change link to each other and the rationale lives only in the ADR
- [ ] At least two options are considered, and the confirmation method is named
- [ ] No framework was initialised and no tool installed

## Files

- `scripts/detect_formats.py` — tree and PATH detection; `--json` for the full result.
- `references/madr-minimal.md` — read only when `mode` is `none`: the fallback template.
