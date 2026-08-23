---
name: den-refresh
description: >-
  Use when an already-scaffolded project is behind the framework — a drift notice appeared, a new
  ai-badger version shipped, or the user asks to "refresh"/"update ai-badger". Reports what
  changed, backs up .ai-badger/, and re-scaffolds from the project's existing config.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [scaffolding, drift, upgrade, refresh]
    related_skills: [welcome-ai-badger, feed-badger]
---

# den-refresh

Pulls framework updates from the ai-badger catalog into a project that was
already scaffolded. **The script does all mechanical work; you (the agent)
present the report and help the user review the diff.**

This is the update direction of the framework: framework → project (update).
For initial setup use `welcome-ai-badger`; to contribute back use `feed-badger`.

## Responsibility split

- **Script (mechanical):** `refresh.py` validates prerequisites (config.json,
  manifest.json), runs drift detection, re-scaffolds with the existing config,
  and emits a JSON report. Skills with extensions (e.g., `task` with
  `github`/`hermes` extensions) are re-scaffolded and their extensions
  re-embedded automatically.
- **You (creative only):** present the report, help the user review what
  changed, and offer to commit or discard. There is no config authoring, no
  stack detection, no plugin scope prompt — those belong to `welcome-ai-badger`.

## Prerequisites

- Project has `.ai-badger/config.json` and `.ai-badger/manifest.json` (it was
  scaffolded by `welcome-ai-badger`)
- An ai-badger framework checkout is accessible (`$AI_BADGER`)

## Flow

1. **Run refresh.** From the target repo root:
   ```bash
   python3 "$AI_BADGER/features/common/skills/den-refresh/scripts/refresh.py" --target . --root "$AI_BADGER"
   ```
   Add `--prune-cache` or `--prune-namespaces` only when the user has asked for it (steps 3
   and 3c) — both delete from the user's home directory. Add `--force` only
   as recovery (see Error Recovery) — it re-scaffolds unconditionally, bypassing every drift
   signal.
   This:
   - Validates that config.json and manifest.json exist
   - Reads the manifest to extract scaffolded skill names
   - Runs drift detection against the framework's current content
   - If drift is found, re-scaffolds using the existing config.json
   - Outputs a JSON report with drift details and scaffold notes

   Exit codes: 0 = success (up to date or changes applied), 2 = error (missing
   config/manifest, invalid config).

2. **Review the report.** The JSON output includes:
   - `frameworkVersion` — the version in config.json vs. the framework's, plus the version
     that actually wrote `manifest.json`. config.json advances only when a re-scaffold ran
   - `drift.changed` — framework sources that have moved ahead of what this project holds
   - `drift.removed` — scaffolded files whose framework source no longer exists
   - `drift.orphaned` — entries whose stack this project no longer lists in `config.stacks`.
     The framework still ships them; this project stopped asking. The re-scaffold prunes them,
     leaving anything edited in place with a note
   - `drift.versionChanged` — the version alone moved; it is stamped into manifest.json and
     every generated agent file, so those are stale even when no content changed
   - `drift.locallyModified` — skills this project edited in place. Not framework drift, but
     a re-scaffold overwrites them; move anything worth keeping into `project-local.md`
   - `drift.skipped` — directory entries from a manifest written before source hashing
     existed. Nothing records what the framework looked like, so they cannot be compared;
     a re-scaffold makes them comparable
   - `drift.newItems` — catalog items the project has never scaffolded, including ones
     added to the framework's always-on `common` stack after this project was set up
   - `drift.configChanged` — `null` unless config.json itself no longer matches what the
     last scaffold was built from (an `exclude`, `commands`, `stacks`, `agents` or
     `personaRouting` edit). Present as `{"recorded": ..., "current": ...}`; `recorded: null`
     means this manifest predates the field and self-heals on this one re-scaffold
   - `newStacks` — stacks detectable in the target but missing from config. Advisory only:
     it never triggers a re-scaffold, so non-empty `newStacks` alongside `reScaffolded: false`
     is the expected shape, not a contradiction
   - `reScaffolded` — whether a re-scaffold was performed
   - `note` — present only when config.json and the framework disagree on the version and no
     re-scaffold ran; the generated files were not rewritten, so say so rather than reporting green
   - `scaffold` — if re-scaffolded: entry count, refreshed skill names, notes
   - `frameworkCopies` — present only when more than one tree on the machine claims to be
     ai-badger, or when `~/.ai-badger/framework` exists. `competing` names the path, version
     and owner of each; `cache` reports what happened to `~/.ai-badger/framework`
     (`reported` by default, `removed` with `--prune-cache`, `refused` with the reason)
   - `skillUsage` — which delivered skills this project was observed using, so unused ones can
     be pruned from the listing budget (see step 3b). `used` / `unused` / `cannotTell`, plus the
     `window` the claim rests on, the `channels` that answered, and the `limits` of both
   - `hermesNamespaces` — present only when every ai-badger symlink in a
     `~/.hermes/skills/<project>/` directory is dangling: that project's `.ai-badger/skills/`
     tree is gone. Every project's namespace is swept, not just this one (see step 3c). Each
     entry carries `path`, `links` (dead links ai-badger placed), `kept` (entries it did not —
     a non-zero `kept` means the directory itself survives the prune), the `target` that no
     longer exists, and `status` (`reported` by default, `removed` with `--prune-namespaces`,
     `failed` with the reason)

3. **Surface competing copies.** When the report carries `frameworkCopies`, tell the user which
   trees exist and at what versions — a drift notice fires once per tree, so two contradictory
   notices mean two installs, not a framework bug. `~/.ai-badger/framework` is ai-badger's own
   fallback clone and nothing updates it in place; offer to re-run with `--prune-cache` to
   remove it. **Never offer to delete `~/.claude/plugins/cache/`** — Claude Code owns that path
   and ai-badger only reads it.

3b. **Offer the prune candidates — never prune them.** `skillUsage.unused` names skills this
   project holds and nobody invoked over `skillUsage.window`. The payoff is the listing budget:
   a host allots a fixed slice of context to skill descriptions and drops the least-invoked ones
   first, so an unused skill crowds one somebody does use. Tell the user the candidates and how
   to decline them — `exclude.skills` in `config.json`, which is self-executing on the next
   refresh (`drift.configChanged`) — and stop there. **`config.json` is project-owned; never
   edit it as part of a refresh.**

   Report the rest of the section with the candidates, not instead of them:
   - `used` carries `evidence` — `invocation` (someone ran it) or `hook` (nobody ran it, but a
     hook it ships fired, so it is doing work every session). Both mean leave it alone
   - `cannotTell` is the honest bucket, and it is not a weaker `unused`: a skill listed there
     was never observable. Never propose pruning one
   - `window.days` bounds every claim. A short window and a quarterly skill produce a
     confident-sounding recommendation that is simply wrong — say the number out loud
   - `limits` states what the channels cannot see. Repeat it; a recommendation without it is
     the one the user will regret
   - `hint`, when present, replaces the recommendation: nothing could be observed, and the fix
     it names (`behaviorist.py on 4h`, then work normally) is the whole answer

3c. **Present the orphaned namespaces and ask.** When the report carries `hermesNamespaces`,
   list each `path` with its `links` count and the `target` that no longer exists, then **ask
   the user whether to remove them**. Only on a yes, re-run the command with
   `--prune-namespaces`. Never pass the flag on your own initiative — a namespace can dangle
   because its project sits on a drive that is not mounted right now, and a refresh that
   deletes it takes the user's Hermes skill wiring with it. Two things to say out loud:
   - the sweep covers **every** project's namespace, not just this one. That is deliberate —
     an orphan's project no longer exists to run `den-refresh` in, so nothing else will ever
     reach it — but it means the paths named may belong to work the user has forgotten
   - only the links ai-badger placed are removed. Anything else in that directory — a
     Hermes-authored skill, a link pointing outside a `.ai-badger/skills/` tree — stays, and
     the directory stays with it: that is what a non-zero `kept` means. A directory holding
     no ai-badger link at all, such as a Hermes category like `react` or `uncategorized`, is
     never listed and never touched. If the user expected one of those gone, it is theirs to
     delete by hand

4. **Review the diff.** After re-scaffold, `git diff` shows exactly what
   changed. Seed-once files (state.json, markers-context.json, model.json) are
   preserved and won't appear in the diff unless they were mutated by the
   project before the refresh.

5. **Commit or discard.** Managed files should be committed to pick up the
   framework updates. Seed-once files are project-owned and never overwritten.

## How it differs from `welcome-ai-badger`

| | welcome-ai-badger | den-refresh |
|---|---|---|
| When to use | First time setup | Subsequent updates |
| Detection | Runs detect.py | Reads existing config |
| Config | Agent authors new config.json | Uses existing config.json |
| Questions | Asks for summary, domain, persona routing, plugin scope | No questions |
| Plugin install | Runs plugin install commands | Skips plugin install |
| Skills | Scaffolds from the skill list | Extracts skill names from manifest |

## Rules

- **Never re-detect.** den-refresh uses the project's existing config.json as-is.
  If the config needs updating (new stacks, changed commands), edit it first,
  then run den-refresh. **Exception:** den-refresh detects stacks that have signals
  in the target but are missing from config, and reports them as `newStacks` —
  advisory only, it never gates the re-scaffold (#134). Two paths from there, both
  project-owned: **accept** — add the stack to `config.stacks` and re-run; the edit
  itself is drift (`configChanged`, #128), so the re-scaffold that delivers it is
  self-executing. **Decline** — add it to `.ai-badger/stack-ignore.json`; never
  overwritten by a re-scaffold.
- **Seed-once files survive.** `state.json`, `markers-context.json`, and
  `model.json` are seed-once and preserved across re-scaffolds (Why — see Gotchas.).
- **Preserved regions survive.** Managed agent files (`CLAUDE.md`,
  `.ai-badger/CLAUDE.md`, `AGENTS.md`, the Copilot and Hermes files) are
  regenerated in full, so project-authored content in them is dropped *unless*
  it sits between `<!-- ai-badger:keep-start -->` and `<!-- ai-badger:keep-end -->`.
  When a refresh reports dropped content, that is the fix to offer (Why — see Gotchas.).
- **Stack ignore list.** If `.ai-badger/stack-ignore.json` exists, stacks
  listed in its `ignore` array are excluded from `newStacks` detection.
  Use this to suppress false-positive stack detection (e.g. `python`
  detected because `.mcp.json` references `python3` for a tool dependency).
  The file is project-owned (manual) and never overwritten by re-scaffold.
- **Skills with extensions are refreshed.** The script extracts skill names
  from the manifest, so skills like `task` (with `github`/`hermes` extensions)
  are re-scaffolded and their extensions re-embedded.
- **Managed files are overwritten.** Everything else under `.ai-badger/` that
  the framework originally placed is refreshed to the framework's current
  content. Review the diff before committing.
- **Usage evidence has two sources and one of them cannot prove absence.** The invocation
  channel reads Claude Code's own transcript store (`~/.claude/projects/<mangled cwd>/*.jsonl`,
  this project's sessions and its worktrees) for `Skill` calls and slash commands — any skill,
  but only on that host, and only as far back as Claude Code retains transcripts. The hook
  channel reads call-behaviorist's audit log, which records hooks, so it can confirm a skill is
  working and never that one is idle. Only the invocation channel produces a prune candidate;
  with no transcript store, every skill lands in `cannotTell` and the report recommends nothing.
  Neither channel reads message content — skill names and timestamps only.
- **Only `~/.ai-badger/framework` is ever pruned, and only on request.** ai-badger created
  that clone, so it may remove it — never silently, only under `--prune-cache`, never when it
  is the root in use, and never when the path is a symlink or holds one that leaves it. Every
  other tree (Claude Code's plugin cache, a framework checkout) is reported and left alone: no
  command destroys state it did not create.

## Gotchas

- **Seed-once files survive.** `state.json`, `markers-context.json` and `model.json` are
  preserved across re-scaffolds — a refresh never resets them.
- **Preserved regions are the only survival path.** Managed agent files are regenerated in
  full; project-authored content in them is dropped unless it sits between
  `<!-- ai-badger:keep-start -->` and `<!-- ai-badger:keep-end -->`.
- **Absence is not a declaration.** A deleted skill, persona, invariant or instruction comes
  back on the next refresh — add its name to `exclude` in `config.json` to decline it for good.

## Notes

- If the project's config.json is invalid, den-refresh exits with an error.
  Fix the config first (re-run `welcome-ai-badger` steps 2-4 if needed), then
  run den-refresh.
- If `index.json` is missing or stale in the framework checkout, run
  `python3 "$AI_BADGER/tooling/index_build.py"` first.
- den-refresh delegates to the same `scaffold.py` that `welcome-ai-badger`
  uses — the re-scaffold is identical to an initial scaffold, just driven by
  an existing config.
- A deleted skill, persona, invariant or instruction comes back: absence is not a
  declaration (Why — see Gotchas.). To decline one for good, add its name to `exclude` in `config.json`
  (`{"skills": ["mcp-index"]}`) and re-run den-refresh — the edit is self-executing
  (`drift.configChanged`, #128), so no separate step is needed. The refresh then stops
  delivering it and removes the discovery symlinks ai-badger placed for it. The same applies
  to any other config-only edit: `commands`, `personaRouting`, `agents`, `stacks`.
- **Dropping a stack from `config.stacks` is the other way to stop asking**, and it now
  converges the same way: the entries that stack delivered are reported as `drift.orphaned` and
  pruned by the re-scaffold. Before 0.42.0 the removal was invisible and the files were left
  behind (#116).

## Error Recovery

When `refresh.py` exits non-zero or returns JSON with an `error` field, attempt
recovery before surfacing the failure to the user.

1. **Parse the error.** The script emits structured JSON — read the `error` field
   and any `validationErrors` array to classify the failure.

2. **Attempt automatic recovery.** Try the applicable fix, then re-run the
   refresh command from step 1 of the Flow.

   > Fix table: read references/error-recovery.md when refresh.py exits non-zero or returns a JSON `error` field.

   After applying a fix, **re-run the refresh**. If it succeeds, report what was
   fixed and continue with the normal flow (review diff, commit).

3. **Recovery failed — offer to create a GitHub issue.** Follow
   `.ai-badger/skills/welcome-ai-badger/references/reporting-a-framework-bug.md` **when a fix
   does not recover the failure**: ask
   permission first, gate on `gh` being installed and authenticated, sanitize the config
   before including it. **Never create the issue without explicit user approval** — that rule
   holds even if the reference file is not present.

## Verification Checklist

- [ ] `refresh.py` exited 0
- [ ] Report read section by section: `frameworkVersion`, `drift.*`, `reScaffolded`, `note`, `frameworkCopies`, `hermesNamespaces`
- [ ] Competing copies surfaced; `~/.ai-badger/framework` pruned only on request
- [ ] Orphaned Hermes namespaces presented and the user asked; `--prune-namespaces` passed only on a yes
- [ ] Prune candidates offered, never pruned — `config.json` untouched
- [ ] Diff reviewed before commit
- [ ] Seed-once files (`state.json`, `markers-context.json`, `model.json`) absent from the diff