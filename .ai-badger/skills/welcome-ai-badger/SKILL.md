---
name: welcome-ai-badger
description: >-
  Use when a repository should be set up with ai-badger — "welcome-ai-badger", "scaffold this
  project", "add agent instructions here", "onboard this repo" — whether it is new or already has
  agent files. Detects stacks, writes .ai-badger/, and generates each configured agent's
  discovery file.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [scaffolding, onboarding, setup, detection]
    related_skills: [den-refresh, feed-badger]
---

# welcome-ai-badger

Scaffolds a target repository with a project-tailored selection of ai-badger framework
features. **The scripts do all mechanical work; you (the agent) only author `config.json` — the
one creative artifact — and answer/ask a few questions.**

## Responsibility split (do not blur it)

- **Scripts (mechanical, deterministic):** `detect.py` proposes a config; `validate.py` checks
  it; `scaffold.py` builds `.ai-badger/`, assembles `CLAUDE.md`, copies agent files, records
  provenance in `manifest.json`.
- **You (creative only):** turn the proposed config into a good `config.json` — write
  `project.summary`/`domain`, choose/confirm stacks, define `personaRouting`, resolve any
  detection ambiguity by asking the user. Then hand it back to `validate.py`.

## Prerequisites

Framework scripts need `jsonschema`:
```bash
python3 -m pip install -r "$AI_BADGER/engine/requirements.txt"
```
`$AI_BADGER` = this framework's root (the dir containing `index.json`, `schemas/`, `common/`).
If `index.json` is missing or stale, run `python3 "$AI_BADGER/tooling/index_build.py"` first.

## Flow

1. **Detect.** From the target repo root:
   ```bash
   python3 "$AI_BADGER/features/common/skills/welcome-ai-badger/scripts/detect.py" --target . --root "$AI_BADGER" > /tmp/proposed-config.json
   ```
   This proposes stacks (with `requires` expanded), detected coding agents
   (claude/copilot/hermes — only those with traces in the repo or user scope), source control,
   and build/test/lint/run commands.

2. **Author `config.json`.** Read the proposal. Fill in `project.summary` and `project.domain`
   (the domain is the *business* purpose, never a stack). Confirm the stack list against
   `index.json` (`stacks` must be known stacks). Add `personaRouting` mapping kinds of work to
   the personas that will be scaffolded (base roles: `architect`, `test-engineer`,
   `code-reviewer`, plus each selected stack's engineer persona). **Ask the user only when a
   choice is genuinely ambiguous** (e.g. detection found both a frontend and a backend and you
   can't tell the project's focus).

3. **Ask plugin scope.** Ask the user: **default** (honor each plugin entry's declared scope) or
   **local-only** (force every plugin install to project scope). Set `skillScope` accordingly.
   (There is deliberately no "user-only" option.)

   If the user declines a skill, persona, invariant or instruction, name it in `exclude`
   (`{"skills": ["mcp-index"]}`) — deleting the scaffolded file is undone by the next refresh
   (Why — see Gotchas.).
   The declined item is not delivered and its discovery symlinks are removed; the copy already
   under `.ai-badger/skills/` stays on disk for the user to delete.

4. **Validate.**
   ```bash
   python3 "$AI_BADGER/tooling/validate.py" --kind config /tmp/proposed-config.json
   ```
   Fix any reported error in the config and re-run until it passes.

5. **Scaffold.**
   ```bash
   python3 "$AI_BADGER/features/common/skills/welcome-ai-badger/scripts/scaffold.py" \
     --config /tmp/proposed-config.json --target . --root "$AI_BADGER" \
     --generated-at "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
   ```
   Produces `.ai-badger/` (config.json, manifest.json, CLAUDE.md, agents/, instructions/,
   invariants/, skills/, agent-instructions/, state.json) and agent-discovery copies for each
   detected agent (`CLAUDE.md`, `.github/copilot-instructions.md`). Note the
   printed plugin-setup commands and run them per the chosen scope (or hand them to the user).
   **Existing hand-authored discovery files are preserved by default** — see the preserve note
   below; on a mature repo the scaffold will report which files it left untouched.

6. **Verify & report.** Confirm the scaffold matches the stacks (no leakage from unselected
   stacks). Summarize what was written, the plugin commands, and any notes the script emitted.
   When the output ends with a list of trees that "claim to be ai-badger", relay it: a drift
   notice fires once per tree (Why — see Gotchas.), so competing copies explain contradictory
   notices. Scaffolding
   deletes nothing in the home directory — `den-refresh --prune-cache` is the one command that
   removes `~/.ai-badger/framework`, and `~/.claude/plugins/cache/` is Claude Code's to manage.

## Notes

- **Idempotent:** re-running `scaffold.py` refreshes managed files and the manifest. Safe to
  re-run after editing `config.json`.
- **Copy-vs-reference:** essential agent files (CLAUDE.md, HERMES.md, copilot-instructions)
  are *copied* to their conventional locations with a header pointing at `.ai-badger/` as the
  source of truth, because agent CLIs discover them by convention. A thin-proxy (symlink)
  alternative was considered and dropped: symlinks break on Windows, and Copilot does not
  follow references.
- **Preserve-by-default (mature repos):** a discovery file that already exists and does *not* carry
  the ai-badger managed header is treated as hand-authored and left untouched — its `.ai-badger/`
  source copy is still written, and the scaffold emits a `preserved …` note. Framework-written
  copies (which carry the header) and brand-new files are written/refreshed normally, so
  idempotent re-scaffolding still works. Pass `--overwrite-agent-files` to force the old
  copy-over behavior on every discovery file.
- **Preserved regions (per-block):** content between `<!-- ai-badger:keep-start -->` and
  `<!-- ai-badger:keep-end -->` is carried verbatim into the regenerated file, in order, at the
  end. This applies to every managed agent file *and* its `.ai-badger/` source-of-truth copy, so
  a project block added to `.ai-badger/CLAUDE.md` survives a re-scaffold. Unbalanced or nested
  markers leave the file untouched and emit a note — a marker typo never loses content (Why — see
  Gotchas.). Tell the
  user about this whenever they ask where to put project-authored content in a managed file.
- **Extensions:** config-gated skill extensions (e.g. the GitHub PR/issue extension of `task`)
  are embedded automatically iff `config.json` supplies their required data.

## Gotchas

- **Deleting a scaffolded file does not decline the item.** The next refresh brings it back —
  decline a skill, persona, invariant or instruction by naming it in `exclude` in `config.json`,
  not by deleting the delivered copy.
- **A keep-marker typo never loses content.** Unbalanced or nested
  `<!-- ai-badger:keep-start -->`/`<!-- ai-badger:keep-end -->` markers leave the file untouched
  and emit a note rather than mangling it.
- **A drift notice fires once per tree.** Competing ai-badger copies each claim the repo, so a
  relayed tree list explains contradictory notices — it is not a bug to fix.

## Updating an already-scaffolded project

The initial scaffold is a one-time setup. For ongoing updates when the framework
releases new features or fixes, use **`den-refresh`** instead of re-running welcome:

```bash
python3 "$AI_BADGER/features/common/skills/den-refresh/scripts/refresh.py" --target . --root "$AI_BADGER"
```

`den-refresh` checks what changed upstream, re-scaffolds with your existing
config.json (no re-detection, no questions), and reports the result. Seed-once
files (state.json, markers-context.json, model.json) are preserved. Review the
diff before committing.

## Error Recovery

When any script in the welcome flow (`detect.py`, `validate.py`, `scaffold.py`)
exits non-zero or emits an error, attempt recovery before surfacing the failure.

1. **Parse the error.** Scripts emit structured JSON with an `error` field and
   sometimes `validationErrors`. Read both to classify the failure.

2. **Attempt automatic recovery.** Try the applicable fix, then re-run the
   failed step.

   | Error | Fix |
   |---|---|
   | `jsonschema` import error | `python3 -m pip install -r "$AI_BADGER/engine/requirements.txt"` |
   | `index.json` missing or stale | `python3 "$AI_BADGER/tooling/index_build.py"` |
   | `validate.py` reports config errors | Read errors, patch config JSON, re-validate |
   | `scaffold.py` file-permission / encoding error | Fix the file/permission, retry once |
   | `detect.py` found no stacks | Check that `$AI_BADGER` points at a valid framework checkout (has `index.json`) |
   | Agent file write failed (read-only discovery file) | Pass `--overwrite-agent-files` or remove the conflicting file |

   After applying a fix, **re-run the failed step** and continue the flow. If it
   succeeds, report what was fixed.

3. **Recovery failed — offer to create a GitHub issue.** Follow
   `.ai-badger/skills/welcome-ai-badger/references/reporting-a-framework-bug.md` **when a fix
   does not recover the failure**: ask
   permission first, gate on `gh` being installed and authenticated, sanitize the config
   before including it. **Never create the issue without explicit user approval** — that rule
   holds even if the reference file is not present.

## Verification Checklist

- [ ] `validate.py --kind config` passed on the authored config
- [ ] Scaffold output covers exactly the selected stacks — no leakage from unselected stacks
- [ ] `.ai-badger/` holds config.json, manifest.json, CLAUDE.md, agents/, instructions/, invariants/, skills/, agent-instructions/, state.json
- [ ] Plugin-setup commands relayed per the chosen scope (default or local-only)
- [ ] Preserved hand-authored discovery files reported, not overwritten
- [ ] Any "competing copies" tree list relayed, and nothing outside the target deleted
