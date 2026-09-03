---
name: documentation-drift-audit
description: "Use when auditing docs for drift vs code ('audit and fix documentation drift'): inventory claims with path:line, verify each against real files (scaffolders, manifests, hooks), classify verifiably-false vs design-position vs ambiguous vs historical, fix only the false, and report A/B/C. Also for post-merge doc-gap audits and user-facing doc compaction rewrites."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: optIn
metadata:
  hermes:
    tags: [documentation, drift, audit, verification]
    related_skills: [documentation, maintain-agent-instructions]
---

# Documentation drift audit

Audit documentation against the actual tree — scaffolder behavior, manifests, hook
registration, config schemas — fix **only** what is verifiably false, and hand
decision-grade findings back to the orchestrator. The point is to separate
"the doc is wrong" from "the doc records a design position" before touching anything.

## When to use

- A task says "audit and fix documentation drift" about a feature area.
- A doc claims something about what code does, scaffolds, ships, or fires
  (script names, hook events, manifest entries, activation conditions, file layout).
- A research record or extension doc needs its claims checked against real behavior.
- A feature just merged (e.g. a new backend/CLI verb behind an existing interface)
  and a follow-up audit must check CLAUDE.md/.hermes.md + user docs against the
  merged code — the "Phase 5" doc-gap audit. See the post-merge section below.
- A user-facing doc (root README, packaged README) needs a **compaction/rewrite**
  pass — "clean this up, use proper markdown, compact it and redirect to the
  detailed files". See the compaction section below; it is a rewrite, not a
  correction, but the verification discipline is the same.

## Method

1. **Inventory the claims first.** Grep the doc surface for the topic's vocabulary
   (script names, config keys, manifest entries, hook events, paths). List every
   candidate claim with `path:line` before editing anything.
2. **Verify each claim against actual file content.** Read the code that would make
   the claim true: the scaffolder's copy logic, the manifest, the plugin's
   `register()`, the extension's `requires`. Where a claim is about *scaffolded
   output*, check a real scaffolded project (or re-scaffold a throwaway) and `ls`
   the delivered tree. Never edit on the strength of the doc's own wording, and
   never trust your memory of what the code does — re-read it.
3. **Classify every finding** into exactly one bucket:
   - **Verifiably false** → fix directly, smallest edit that makes the sentence true.
   - **Design position** ("Claude-only by design", "replaced by native features") →
     report only. Changing it is a decision, not a correction.
   - **Ambiguous statement** (loose counts, vague referents) → report only; don't guess.
   - **Historical record** (changelog entries, dated research records) → never
     rewrite; they record what was true when written.
4. **Edit only the false claims**, quoting reality from verified evidence: name the
   exact files/scripts, the actual mechanism, and the measured outcome.
5. **Check generated mirrors.** If the repo keeps a committed mirror of its own
   source (e.g. a `.ai-badger/` tree mirroring `features/`), a source edit makes the
   mirror stale and trips the freshness gate. If constraints forbid touching the
   mirror, flag it in the report so the orchestrator re-scaffolds.
6. **Report A/B/C** — the shape that worked:
   - **A — drift found**: `path:line` + stale claim + reality (report only, no edits).
   - **B — edits made**: file + before/after summary.
   - **C — decision gaps**: items needing a human (exemption/reason strings now known
     to rest on false premises, future-state designs, shipping-inert artifacts,
     gate/mirror consequences).
7. **Re-verify after editing**: `git diff` shows exactly the intended change; grep
   `tests/` for assertions on removed strings; every sentence you wrote is backed by
   verified evidence (a real scaffolded tree, a `register()` list, a manifest).

## Post-merge feature audits (the "Phase 5" pattern)

For audits that follow a merged feature (worktree-isolated, docs-only, no push):

1. **Verify the checkout is actually on the claimed tree first.** The premise
   ("repo is now on merge X") is often stale — local `main` may sit behind
   `origin/main`. Check `git log --oneline -1` against `git log --oneline -1
   origin/main`; if behind, `git merge --ff-only origin/main` aligns the checkout
   **without creating a commit or pushing**, so it stays inside a "don't commit,
   don't push" constraint. Diagnose discrepancies with `git branch -a --contains
   <sha>` and `git merge-base HEAD origin/main`. Worktree variant: `git worktree
   add <path> origin/main`, confirm the target merge is an ancestor (`git
   merge-base --is-ancestor <sha> HEAD`), work ONLY there.

   **A locally-modified tracking file can block the fast-forward — and the merge
   may touch the same file.** Repos with a committed state/tracking file (e.g.
   `.ai-badger/state.json`) often carry an uncommitted orchestrator update exactly
   when an audit runs. Check first whether the merge modifies it at all:
   `git diff <old> <merge> --stat -- <file>`. If it does not, `git stash push --
   <file>` → ff → `git stash pop` is safe. If it does, expect a pop conflict and
   reconstruct rather than take a side: the stashed version was authored against
   the **pre-merge base**, so it can lack entries the merge added (e.g. the
   merge's own completed-task entry) while the merge lacks the local tracking
   delta. Take the merged version as base and re-apply the local change's
   *semantic delta* (prepend the new entry, update `next`/pointer fields), then
   verify with `git diff <merge> <stash> -- <file>` that the delta is exactly the
   intended local change. Do the surgery by JSON parse/modify/dump, not text
   retyping; entries may be keyed `taskId` instead of `id` — match on the
   identifying value, not the key. Return the file to plain unstaged-modification
   state with `git add <file> && git restore --staged <file>`. If you dropped the
   stash prematurely, nothing is lost — the drop only removes the ref, and the
   content stays reachable via `git show <sha>:<path>` (the sha is printed by
   `git stash drop`).
2. **Ground truth = the merged code, read first.** Extract the exact user-facing
   strings from the implementation before judging any doc: CLI verbs + option names,
   settings-table keys, error-message prefixes, MCP tool `[Description]` text. Docs
   must match these **verbatim** — check `sync.provider`-style key spelling, prompt
   channel (stderr prompt / stdin read / empty-aborts exit 1), redaction behavior,
   and default values against the code, not against memory or the PR description.
3. **Grep gates with intentional-residue classification.** Run
   `grep -rn "<old-phrasing>" --include="*.md" --include="*.cs" .` (and one for the
   old CLI verb). Classify EVERY hit before touching anything:
   - **Intentional:** internal class XML docs, subcommand help descriptions, and
     phrasing already paired with the new alternative ("S3-compatible **or Azure
     Blob**"). Also all of `docs/plans/` (pre-implementation), `docs/work/` (dated
     records), `docs/adr/` (frozen) — they record what was true when written; a plan
     quoting the old error string is correct history, not drift.
   - **Drift:** old-phrasing-as-the-only-option in current-surface docs (root
     README, `docs/reference/`, `docs/explanation/`, SECURITY, `src/*/README.md`).
   Only current-surface hits are fixable; a passing gate means zero remaining
   current-surface hits.

   **Before fixing pre-existing drift, check for a pending fix.** An open PR may
   already cover the same stale counts/omissions (common after a feature merge —
   e.g. a "docs: post-#N audit" PR sitting open while the next feature merged).
   `gh pr view <n> --json title,state,files` and `gh pr diff <n>` tell you what it
   touches. If it covers the drift, do NOT duplicate the edits — report the drift
   as covered-by-PR-<n> instead, and note the merge hazard: a PR that touches both
   the docs and the tracking file will conflict on the latter once the feature
   merge lands, so it needs a rebase.
4. **Generated instruction copies.** Root `CLAUDE.md`/`.hermes.md` are generated
   from `.ai-badger/` sources: `diff` root vs source and confirm the only difference
   is the managed-by header — use a plain `diff` and expect a 2-line prepend
   (comment + blank line); a `tail -n +N`-style offset comparison misaligns and
   raises a false alarm. Real drift is fixed in the source, never the copy; and
   respect the project's compaction policy — provider-neutral existing wording
   ("optional cloud sync") needs no expansion.

   **Not every mirror difference is drift — learn the expected transformations
   before flagging.** In ai-badger: the scaffolded `.ai-badger/hooks/hooks.json`
   is path-rewritten by design (plugin-root `${CLAUDE_PLUGIN_ROOT}/features/...`
   commands become `${CLAUDE_PROJECT_DIR}/.ai-badger/...` fallback command
   shapes); the plugin `skills/` tree ships a frontmatter-only `SKILL.md` stub
   plus a full-content `SKILL.full.md`; and the scaffolder rewrites commands into
   if-exists/elif/else shapes. Diff against the *shape* first, then diff only the
   content fields that should be byte-identical.
5. **Fix discipline + commit.** Smallest edit that makes the sentence true (word or
   sentence swap — no restructure, no new sections); conventional message
   (`docs(sync): fix stale wording after <feature> merge`); re-check `git status`
   shows only doc files.
6. **Decision gaps are reported, not fixed.** Anything a user would hit that the
   docs don't state — unsupported auth modes, resources that must pre-exist (e.g.
   a container the CLI doesn't create), missing dev-testing story, error tables
   covering only some prefixes — goes in the report as needs-decision, because
   writing it down either changes product behavior or is a product call.


> Compaction passes: read `references/compaction-rewrite-passes.md` when doing compaction/rewrite passes on READMEs or user-facing docs.


> ADR-vs-code drift: read `references/adr-vs-code-drift.md` when auditing ADR-vs-code drift (contract points vs factual points).

## Classification pitfalls

- A doc claim that **matches its own manifest/`extension.json`/`requires` entry is
  not drift** even if it seems odd — the code and doc may agree while the real
  problem is the *activation condition* (e.g. one extension gates on `agents=claude`,
  a sibling gates on `stacks=hermes`). Report the asymmetry as a decision gap;
  don't "fix" the doc.
- Count claims ("ships three hook scripts") are usually loose summaries — verify the
  real count; if the referent is ambiguous, list in A, don't edit.
- **Verify counts against code, not against sibling docs.** Docs drift from each
  other as well as from code ("seven are optIn" in one doc, eight in the other, while
  code declares eight). The declaration (e.g. a frontmatter key, a manifest) is
  the source of truth; when a count is unambiguous against it, fix the pre-existing
  wrong doc too, but report it as pre-existing so the maintainer knows it was not
  merge-caused.
- **Date a doc claim before blaming the merge:** `git log -S "<stale string>" --
  <file>` shows when a line was last written. A table last touched at 0.51.0 that
  omits a 0.78.0 server is pre-existing drift the merge merely failed to fix — still
  fix it if unambiguous, but report the provenance.
- **At-a-glance table rows that link to `#anchors` require the section to exist.**
  Adding a row without its section breaks link-checking gates (e.g. `docs_guard.py`
  scans every markdown link). Add row + section together, or make the row a plain
  name.
- "NOT scaffolded when agent X is present" is a common false claim: scaffolds
  usually copy **whole directories**; only `config.exclude`-style patterns filter.
  Verify by listing a real scaffolded project's delivered tree.
- After you fix a source doc, dated research records that quote the old text will
  quote text that no longer exists. That is correct — do not retro-edit the record.
- Doc-adjacent files can be **LIVE test inputs**, not archive. `.feature`/spec files
  under docs/ (e.g. `docs/work/**/*.feature`) may be wired into the test project —
  Reqnroll/SpecFlow list them via `<ReqnrollFeatureFiles Include=...>` in the test
  csproj. A stale-but-live spec is NOT a docs edit: rewriting its scenario text can
  break step bindings, and the steps themselves may be no-op bindings "verified by
  ToolInventoryTests" that still enumerate the old surface. Check the test project's
  feature-file globs before treating any `.feature` as historical; report stale live
  specs as decision gaps (fix requires coordinated test-code + spec edits, which a
  docs-only commit cannot do).
- Verify removal claims against **surviving code paths**. A task summary like "env
  vars no longer read" is often only partially true — one variable can survive via a
  dedicated provider (e.g. `EnvEncryptionKeyProvider` still wired in the composition
  root). Grep for the actual mechanism (`GetEnvironmentVariable`, provider
  registrations, `Sources.Clear()`) and document the exact survivor set instead of
  deleting every env-var mention.
- Respect scope constraints literally ("edit only files under features/ or docs/",
  "don't touch tooling/validate.py", "don't commit") and surface their consequences
  (stale mirror, failing gate) in the report instead of violating them.
- **A doc can contradict itself: prose vs compact block.** A reference page often
  carries both a prose description AND a compact verb/option block (a fenced
  code block listing CLI verbs, a summary table). The prose may be current while
  the compact block is stale — or vice versa. Observed 2026-08-05: the prose of
  `docs/reference/agent-memory-server.md` documented `watch registered
  [{project-id}]` and `watch remove {project-id|*}` (matching code), but the
  fenced verb block a page later omitted both. Grep the compact block against the
  code declaration (the CLI command tree, the option table) independently of the
  prose — a block that matches the prose but not the code is still drift.
- **Enumeration lists drift as a family, not one file.** A stale skill-name list ("the
  twelve operational skills", a tree sketch, an available-by-name sentence) usually lives
  in several docs written together — one omission in the codebase leaves N stale copies,
  and an audit pointed at two files (#303 fixed README + skills.md) can miss the siblings
  (framework-architecture.md, getting-started.md and the README tree sketch all still
  listed 12 of 14 default skills). After fixing the first copy, grep the whole doc surface
  for the enumeration's vocabulary, and verify the count against the declaration itself
  (each skill's `scope:` frontmatter), never against a sibling doc.
- **Tracking-file `next` pointers go stale while the task is still open.** `.ai-badger/
  state.json`'s `next` was written after PR #303 had already merged yet still listed it
  as pending, alongside a follow-up that had shipped in the meantime. When an audit
  touches a tracking file, verify each "pending" item against git log/state before
  keeping it — a pointer that names finished work sends the next task on a ghost errand.

## Verification

- Diff contains exactly the intended change.
- No tests assert the removed strings.
- Real-world evidence supports every sentence you wrote.
- A subset test run that fails while the full suite passes is usually order-dependent
  registration (module-level registries like tracker_lib.SESSION_SOURCES populated by
  earlier tests), not a regression from your edit — confirm against the full suite
  before chasing it.

## Gotchas

- "Verifiably-false" verdicts need path:line evidence — a claim you cannot pin to a real file is "ambiguous", not "false".
- Never hand-edit generated files: scaffolders and manifests regenerate them; classify them with the doc-surface map instead.
## References

- `references/readme-compaction-recipe.md` — worked example of the README (read when planning a README compaction)
  compaction (the project, 2026-08-06, 561 → 158 lines): ground-truth extraction
  list, redirect mapping, content-regression grep, the SDK-version stale-fact
  miss the reviewer caught, and the merge-race recovery (PR merged mid-review →
  cherry-pick orphaned commit onto a fresh branch from origin/main → follow-up PR).
- `references/ai-badger-drift-audit-map.md` — verified ai-badger framework facts (read when auditing ai-badger docs for drift)
  gathered during the 2026-08 hermes-task-tracking audit: scaffolder copy behavior,
  extension gating (`requires` semantics), hook manifest/plugin registration, the
  `.ai-badger/` freshness mirror, and where each mechanism lives. Re-verify before
  citing — framework code moves.
- `references/ai-badger-doc-surface-map.md` — which ai-badger files are generated (read when deciding what is generated vs hand-edited)
  (never hand-edit) vs hand-edited, where skill/MCP counts come from
  (each `SKILL.md`'s `scope:`, `stack-mcp.json`), the per-host `.mcp.json` fact, and
  the gates that verify doc edits (0.78.0 snapshot).
- `references/post-merge-doc-audit-recipe.md` — worked example of the post-merge (read when running a post-merge feature audit)
  feature audit (Azure Blob sync, 2026-08-05): setup commands, ground-truth
  extraction list, grep-gate classification, generated-copy diff check, and the
  decision-gap report shape.
- `references/post-merge-tracking-conflict-recipe.md` — worked example of a (read when a tracking-file conflict blocks a fast-forward)
  fast-forward blocked by a local tracking-file update the merge also touched
  (memory-grade hook audit, 2026-08-05): stash-push single file, conflict
  reconstruction via JSON surgery, dropped-stash recovery, the pending-PR check
  that prevented duplicating #303's fixes, and the expected mirror differences.
- `references/ai-badger-mcp-scaffold-declaration.md` — why a declared MCP server can (read when a declared MCP server is missing from a scaffold)
  be absent from scaffolded projects (scaffold-time PATH gate, tool command renames
  between versions, `den-refresh --force` when versions match, `.mcp.json` gitignored,
  `.github/mcp.json` #193 dedup), plus the Hermes `~/.hermes/config.yaml` route
  (`hermes mcp add`; adjust is proposal-only), a port-5000 single-instance
  bind, and the JSON-RPC stdio probe recipe — verified 2026-08-05.
