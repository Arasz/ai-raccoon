# opt-skills — Implementation plan (MoE-integrated)

**Date:** 2026-08-07
**Status:** PLAN (not yet implemented — implementation is a follow-up task)
**Inputs:** docs/work/2026-08-07-skills-optimization-audit.md (audit, items G1-G6 corrected, I1-I14); agentskills.io skill-creation best practices.
**Planning MoE:** 3 parallel architect agents (deleg_3c3a4429) — general items, individual items, tooling+repo-shape.
**Review MoE:** pending (separate group, code-reviewer persona).

## Ground rules for execution (apply to every section)

1. All skill-content edits happen in `features/` (single source of truth), then `python3 tooling/sync_plugin_skills.py` re-syncs the Claude Code plugin copy in the same commit (G5-corrected; pre-push `plugin-skills` gate enforces).
2. TDD where code changes (G4); skill-content changes are docs — gates are the greps/commands in each item's acceptance criteria.
3. Frontmatter `description` is the only text an agent reads before choosing a skill — never weaken it while restructuring bodies (render_pointer keeps frontmatter verbatim).
4. Tests that pin skill content: 91 test files mention SKILL.md; check `tests/test_skill_scope_declarations.py`, `tests/test_every_check_can_fail.py` before changing shipped skill lists.
5. One PR per task; this plan is the PR's scope statement.

## Part A — General improvements (from planning MoE lane 1)

# General skills conventions — G1, G2, G3, G6 (plan section)

**Source:** `docs/work/2026-08-07-skills-optimization-audit.md` (items G1, G2, G3, G6). **Scope:** content edits to the 23 distributed `SKILL.md` files only. **Out of scope (other lanes):** G4 (skills-lint in validate.py), G5 (root `skills/` shape), I1–I4 (reference-table splits), I5–I10 (per-skill enrichment) — this plan only lays the conventions those lanes build on.

## 0. Lane boundaries and sequencing (read first)

- G1, G2, G3, G6 touch **10 overlapping files** (task, den-refresh, welcome-ai-badger, feed-badger, maintain-agent-instructions, commit-reminder, prompt-markers, create-task-spec, auto-wm, call-behaviorist appear in ≥2 items). They are **not parallel-safe against each other**. Recommended shape: **one PR** ("skills general conventions"), 4 commits (G3 frontmatter → G1 gotchas → G6 checklists → G2 conditions), because the repo rule is one PR per task and the task is one audit.
- Sequencing vs other lanes: **G-PR lands first**; I5–I10 (enrich Gotchas/checklists on the same files) and I1–I4 (splits on mcp-index, call-behaviorist, den-refresh) follow after; G4's lint needs this content in place; G5 must not restructure root `skills/` while this PR regenerates it.
- **Audit gap to fix in this plan:** G3's list omits `call-behaviorist` (bare frontmatter) and `create-task-spec` (platforms only). Actual count needing G3 work is **10**, not 8.

## Shared execution loop (every item, in order)

1. Edit `features/{common,claude}/skills/<name>/SKILL.md` in the opt-skills worktree.
2. `python3 tooling/sync_plugin_skills.py` — regenerates root `skills/` (15 of 23 skills ship there; `sync_plugin_skills.py --check` is a **CI gate** and `tests/test_every_check_can_fail.py` pins it fails on divergence).
3. Re-scaffold this repo's own `.ai-badger/`: `python3 features/common/skills/welcome-ai-badger/scripts/scaffold.py --config .ai-badger/config.json --target . --root . --generated-at "$(date -u +%Y-%m-%dT%H:%M:%SZ)"` — the tracked `.ai-badger/skills/*/SKILL.md` mirrors `features/` and CI's `gates/scaffold_freshness_guard.py` **re-scaffolds the repo and fails on any divergence beyond stamps**. (Mandatory for skills with extensions — e.g. task — whose scaffolded copy is a merge, not a copy.)
4. Gates, in the worktree with the main checkout's `.venv` (worktrees have none): `python3 tooling/sync_plugin_skills.py --check` · `python3 gates/scaffold_freshness_guard.py` · `python3 tooling/index_build.py --check` · `python3 tooling/validate.py --all` · `python3 -m pytest -q`.
5. Repo invariant: bump `VERSION` (patch), add `docs/changelog/{version}-{slug}.md`, update `docs/changelog/README.md` if the format changes.
6. Optional but recommended: add a ≤30-line "Catalog conventions" block to `docs/skills.md` (after "At a glance") capturing the four rules below; follow the `update-documentation` discipline.

---

## G1 — One gotchas convention, named "Gotchas"

**Convention (defined once, enforced by review until G4's lint lands):** a `## Gotchas` section holds concrete corrections to mistakes the agent would otherwise make (environment/API/tool-behaviour specific). Stop-and-reconsider conditions keep the existing `## Red flags — STOP` name. Rationalization tables mean STOP and belong to the Red-flags family. Canonical order: procedure → `## Gotchas` → `## Red flags — STOP` → `## Verification Checklist` → aux sections (`## Files`, `## Error Recovery`, `## Recovery`, `## Notes` last).

**A. Rename in place (6 files):**

| File | Current heading | New heading |
|---|---|---|
| `features/claude/skills/auto-wm/SKILL.md` (l.63) | `## Common mistakes` | `## Gotchas` |
| `features/common/skills/ai-raccoon-memory/SKILL.md` (l.56) | `## 6. Pitfalls` | `## 6. Gotchas` (keeps numbered scheme) |
| `features/common/skills/mcp-index/SKILL.md` (l.238) | `## Common Pitfalls` | `## Gotchas` |
| `features/common/skills/differential-feature-refactor/SKILL.md` (l.179) | `## Common Pitfalls — STOP and go back to the authority set` | `## Red flags — STOP` (content unchanged; h3 `### Rationalizations — all of these mean STOP` at l.43 stays) |
| `features/common/skills/owner-gate-review/SKILL.md` (l.121) | `## Common Pitfalls — STOP` | `## Red flags — STOP` (omitted from audit's rename list; same rule) |
| `features/common/skills/migrate-documentation/SKILL.md` (l.169/182) | h2 `## Rationalizations — every one of these means STOP` (l.169) above h2 `## Red flags — STOP` (l.182) | demote l.169 to `### Rationalizations — every one of these means STOP` (its table stays under it); keep l.182 as `## Red flags — STOP` with its bullets; move the h3+table below the Red-flags bullets so no content dangles. There is no duplicate heading to delete. |

**B. Add `## Gotchas` by lifting embedded prose (2 files — the other four B candidates are owned by I5–I8):** task, commit-reminder, prompt-markers and feed-badger get their Gotchas sections from the I-series (I5–I8 bulletize the same prose); do NOT add a second `## Gotchas` there in this PR. For the two files below, extract the named corrections, leave a one-line pointer at the source ("Why — see Gotchas.").

| File | Source prose to lift |
|---|---|
| `welcome-ai-badger` | Flow step 3 (l.56–59: deleting a scaffolded file is undone by the next refresh — use `exclude`), Notes (l.102–107: unbalanced keep-markers leave file untouched — a marker typo never loses content), Flow step 6 (l.82–83: drift notice fires once per tree) |
| `den-refresh` | Rules (l.147–148 seed-once files survive; l.149–153 preserved regions are the only survival path), Notes (l.189–194: absence is not a declaration — use config `exclude`) |

**C. Add the one-line note (14 files):** `debug-issue`, `explore-codebase`, `refactor-safely`, `review-changes`, `scaffold-documentation`, `update-documentation`, `evidence-first-research`, `maintain-agent-instructions`, `differential-feature-refactor`, `migrate-documentation`, `owner-gate-review`, **plus `call-behaviorist`, `code-review-checklist`, `create-task-spec`** (review round 1 finding 3: without them the `== 23` acceptance and G4 rule 9 are unreachable).

**Template (B and C):**

```markdown
## Gotchas

- **<mistake the agent would make>.** <correction, 1–2 sentences>.

## Gotchas

No environment-specific gotchas known.
```

**Placement anchors:** rename skills — in place. Add-skills — immediately before `## Red flags — STOP` where present (evidence-first-research: between `## Charts` and `## Red flags — STOP`; owner-gate-review: between `## Writing decision cards` and `## Red flags — STOP`); before `## Verification Checklist` when no Red flags (task: after `## Recovery` — but task's Gotchas comes from I5, not here); otherwise before the last aux section (update-documentation, scaffold-documentation, refactor-safely, review-changes, explore-codebase, debug-issue: before their `## Red flags — STOP`; maintain-agent-instructions: end of file).

**Acceptance (reviewer runs):**
```bash
grep -rn "^## .*Gotchas" features/common/skills/*/SKILL.md features/claude/skills/auto-wm/SKILL.md | wc -l   # == 23 (3 renamed A + 2 lifted B + 4 via I5-I8 + 14 one-line C)
grep -rn "^## \\(Common mistakes\\|Pitfalls\\|Common Pitfalls\\)" features --include=SKILL.md                  # no output (note: `## 6. Gotchas` in ai-raccoon-memory matches the first grep via `.*`; a missed `## 6. Pitfalls` rename needs its own check)
grep -rn "^## 6. Pitfalls" features --include=SKILL.md                                                         # no output (numbered-scheme variant of the rename check)
grep -rn "Rationalizations" features --include=SKILL.md | grep -v "STOP"                                       # no output
# plus shared loop: sync --check, freshness guard, pytest green
```

**Parallel:** no — touches 23 files, 6 of them shared with G3/G6. **Risks:** root `skills/` regeneration mandatory (G1 renames/touches 12 root-shipped skills); `test_plugin_copy_points_at_the_tailored_one.py` pins bootstrap-three byte-equality and pointer frontmatter — re-sync satisfies both; do not introduce evidence-table shapes (Fact/Claim/Measurement rows) — `test_skill_bodies_carry_procedure_not_evidence.py` fails on them; do not add new `python3 <path>.py` invocations (test_skill_docs).

## G2 — Explicit progressive-disclosure conditions

**Rule:** every `references/...` mention in a SKILL.md body is triggered by an explicit when/if condition — on the mention line itself or within the checker's context window. The checker (below) examines the line plus its immediate neighbours `{i−1, i, i+1}` (a 1-indexed 3-line window — the plan prose "the immediately preceding line" is loose; the code is the contract), skipping numbered-step lines (`^\s*\d+\.\s`) and an explicit exempt list. Same predicate is G4 rule 8 (shared function — see Part C rule 8).

**Edits (10 files, ~16 line changes):** add/rewrite the trigger; keep the `"Read \`references/X.md\` when <trigger>"` shape.

| File:line | Change |
|---|---|
| `evidence-first-research`:46 | "Full rules in `references/provenance.md`" → "…read `references/provenance.md` **when a grade is in doubt**" — final wording owned by I12 (review round 1 finding 21); implement I12's version, not this one |
| `evidence-first-research`:116–117 | Files list → "`references/provenance.md` — read **when grading a finding**; `references/report-template.md` — read **when writing the record**" — same: I12 owns the final text |
| `owner-gate-review`:142–143 | Files list → "…read **when writing the form**"; "…read **when reconciling a saved result**" (l.103 already conditioned — leave) |
| `maintain-agent-instructions`:73 | "see `references/agent-instruction-model.md`" → "…read it **when writing a script that reads the model**" (l.24's paragraph gets a `when` added in PR-1: "…model (`references/agent-instruction-model.md`) **when** changing shared policy" — rule 8/G2 must agree; see rule-8 note below) |
| `maintain-agent-instructions`:79–80 | "Use `references/…` for the model contract" → "Read `references/agent-instruction-model.md` **when the model contract is in question** and `references/copilot-compatibility.md` **when phrasing a Copilot-specific rule**" |
| `scaffold-documentation`:23 | "Reference: `references/structure.md`" → "…read it **when the canonical tree is in question**" |
| `scaffold-documentation`:72 | paragraph "…belongs in a `references/` subdirectory…" → lead with a trigger: "**When placing a skill's reference material**, put it in a `references/` subdirectory *inside* that skill…" (review round 1: this paragraph was flagged by the plan's own gate; the `references/`-as-directory mention needs the trigger too) |
| `update-documentation`:35 | "References: …" → "Read `references/placement.md` **when choosing a target path**, `references/trust.md` **when an evidence line is challenged**, `references/amendments.md` **when phrasing an amendment's reason**" (l.27 step-bound — leave) |
| `update-documentation`:37 | the `../scaffold-documentation/references/structure.md` mention (2 lines below the edited l.35 — outside the checker's {i−1, i, i+1} window) → reword to "…structure is that skill's primary concern — read it **when the canonical tree is in question**" |
| `update-documentation`:104 | "Everything else is in `references/placement.md`" → "…read it **when the one-line test does not settle the target**" |
| `migrate-documentation`:32–36 | add per-file triggers: placement "**when a target path is in doubt**", trust "**when freezing**", amendments "**when amending**", structure "**when the canonical tree is in question**" |
| `create-task-spec`:142 | blockquote → "read `references/why-elicitation.md` **when the contract shape is questioned**" |
| `den-refresh`:225, `feed-badger`:96, `welcome-ai-badger`:149 | prepend trigger to the mention line: "**Recovery failed:** follow `…/reporting-a-framework-bug.md` **when a fix does not recover the failure** — ask permission first…" (the trigger word must be on the mention line or within its 3-line window — "ask permission first" alone contains no when/if/before/after) |

Already compliant (verified, no edit): `differential-feature-refactor`:63, `owner-gate-review`:42, `evidence-first-research`:58, `update-documentation`:27 (numbered steps); `scaffold-documentation`:84 (explicit exemption — see acceptance; the "bullet lead" reading is not why it passes).

**Acceptance (reviewer runs):**
```bash
.venv/bin/python3 - <<'EOF'
import pathlib, re
ok = re.compile(r"\b(when|if|before|after|only when)\b", re.I)
numbered = re.compile(r"^\s*\d+\.\s")
exempt = {"scaffold-documentation:84"}   # explicit exemption (generic directory mention, not a file pointer)
bad = []
for p in sorted(pathlib.Path("features").glob("*/skills/*/SKILL.md")):
    lines = p.read_text().splitlines()
    for i, line in enumerate(lines, 1):
        if "references/" not in line or numbered.match(line): continue
        ctx = "\n".join(lines[max(0,i-2):i+1])
        if f"{p.parent.name}:{i}" in exempt or ok.search(ctx): continue
        bad.append(f"{p.parent.name}:{i}")
assert not bad, bad
print("G2 conditions OK")
EOF
```

**Parallel:** partially — only `maintain-agent-instructions`, `owner-gate-review`, `create-task-spec`, `welcome-ai-badger`, `den-refresh`, `feed-badger` overlap G1/G3/G6; `evidence-first-research`, `scaffold-documentation`, `update-documentation`, `migrate-documentation` are G2-only. Not parallel-safe as a separate PR (shared files); safe as a commit inside the single PR. **Risks:** none of the split lanes' `references/` content is touched (I1–I4 keep their own files); keep conditions factual — a wrong trigger misleads more than no trigger (review each against the reference file's actual content).

## G3 — Uniform frontmatter metadata

**Canonical block** (already used by 13 skills — copy `code-review-checklist`'s):

```yaml
---
name: <name>
description: >-
  <existing description, byte-unchanged>
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [<tag>, <tag>, ...]
    related_skills: [<sibling>, ...]
---
```

**Edits (10 files):**

| File | Current | Add |
|---|---|---|
| `features/claude/skills/auto-wm/SKILL.md` | bare | version 1.0.0, `author: Hermes Agent`, license, platforms, tags `[autonomy, permissions, hooks, guardrails]`, related `[call-behaviorist, commit-reminder]` |
| `call-behaviorist` | bare | version 1.0.0, author ai-badger, license, platforms, tags `[observability, hooks, audit, logging]`, related `[auto-wm, commit-reminder]` |
| `commit-reminder` | bare | tags `[git, commits, hooks, safety]`, related `[call-behaviorist, task]` |
| `create-task-spec` | platforms only | version/author/license/metadata; tags `[specification, gherkin, requirements, contracts]`, related `[task, behavioral-contracts]` |
| `den-refresh` | bare | tags `[scaffolding, drift, upgrade, refresh]`, related `[welcome-ai-badger, feed-badger]` |
| `feed-badger` | bare | tags `[contribution, framework, pr, catalog]`, related `[welcome-ai-badger, den-refresh]` |
| `maintain-agent-instructions` | bare | tags `[agent-instructions, drift, claude, copilot]`, related `[welcome-ai-badger, update-documentation]` |
| `prompt-markers` | bare | tags `[prompts, markers, hooks, context]`, related `[auto-wm, call-behaviorist]` |
| `task` | platforms `[linux, macos]` only | keep platforms as-is; version/author/license/metadata; tags `[task, orchestration, delegation, worktree]`, related `[create-task-spec, commit-reminder]` |
| `welcome-ai-badger` | bare | tags `[scaffolding, onboarding, setup, detection]`, related `[den-refresh, feed-badger]` |

Never touch `name`/`description`; `version:` is a content marker, not the framework `VERSION` (no coupling — `test_breaking_versions` reads framework versions only).

**Acceptance (reviewer runs):**
```bash
.venv/bin/python3 - <<'EOF'
import pathlib, yaml  # NOTE: pyyaml is optional in this repo (engine/requirements.txt, guarded) — run this acceptance with the repo .venv, or use the same stdlib extractor rule 10 uses (review round 1 Part C F6)
names = ["auto-wm","call-behaviorist","commit-reminder","create-task-spec","den-refresh",
         "feed-badger","maintain-agent-instructions","prompt-markers","task","welcome-ai-badger"]
need = {"version","author","license","platforms","metadata"}
bad = []
for n in names:
    p = pathlib.Path(f"features/common/skills/{n}/SKILL.md")
    if not p.exists(): p = pathlib.Path(f"features/claude/skills/{n}/SKILL.md")
    fm = yaml.safe_load(p.read_text().split("---")[1])          # proves YAML validity
    miss = need - fm.keys()
    h = fm.get("metadata", {}).get("hermes", {})
    if not (isinstance(h, dict) and "tags" in h and "related_skills" in h):
        miss.add("metadata.hermes.{tags,related_skills}")
    if miss: bad.append((n, sorted(miss)))
assert not bad, bad
print("G3 frontmatter OK")
EOF
# after re-scaffold: grep -c "metadata" .ai-badger/skills/<name>/SKILL.md  # >= 1 for each name above
```

**Parallel:** no — 10 files, 8 shared with G1/G6. **Risks:** (a) YAML must stay valid — the scaffolder copies SKILL.md verbatim and Hermes parses the frontmatter; (b) `sync_plugin_skills.py` renders root `skills/` pointers with frontmatter verbatim and `test_plugin_copy_points_at_the_tailored_one.py` asserts pointer frontmatter == source frontmatter — re-sync satisfies it; (c) audit's "8 skills" list is wrong — 10 need work; (d) the repo's own `.ai-badger/skills/` copies must be re-scaffolded (freshness guard).

## G6 — Verification-checklist convention

**Rule:** every skill with 4+ procedural steps ends with `## Verification Checklist` — last section of the file, except where a `## Files` section exists (then checklist precedes Files; `## Error Recovery`/`## Recovery`/`## Notes` aux sections stay after it). Items are concrete, greppable, and reuse commands the skill already documents (test_skill_docs forbids inventing new `python3` paths).

**Edits (4 files — ownership split with the I-series, review round 1 finding 2):** den-refresh, feed-badger, welcome-ai-badger and maintain-agent-instructions get their checklists from I4/I7/I9/I10 — do NOT add a second `## Verification Checklist` there in this PR. This PR owns: task, commit-reminder, migrate-documentation, update-documentation.

| File | Checklist items (shape) |
|---|---|
| `task` | `task_tracker.py status` shows finished + `.ai-badger/state.json` updated; all work in the worktree `start` created (no stray commits on the main checkout's branch); every plan point's acceptance gate ran; `finish` left no worktree with unmerged/uncommitted work (`keptBecause` empty or resolved); token cost reported and compact/fresh-session advice given (or auto-continue condition held) |
| `commit-reminder` | `ensure_committed.py` run; at-risk projects named and work committed or taken over; hook verified firing (one edit → count checked; escalation visible in audit trail when enabled). Insert BEFORE `## Files` (l.121) — the rule says checklists precede Files |
| `migrate-documentation` | state file committed and shows zero pending; drain report exists for every deleted legacy file; no delete shipped in the same PR as its replacement; every move recorded (`git mv` + move record); freeze list respected |
| `update-documentation` | every `evidence=` line resolves to a real `path:line` (opened, not inferred); ledger accepted the entry; no frozen build-input file touched; verification-span budget respected (≤2); report matches what was recorded |

**Acceptance (reviewer runs):**
```bash
for f in task commit-reminder migrate-documentation update-documentation; do
  grep -c "^## Verification Checklist" features/common/skills/$f/SKILL.md   # == 1 each
done
# eyeball: checklist is the last section before ## Files (migrate/update end with Red flags; commit-reminder has Files last)
```

**Parallel:** no — 6 of 8 files shared with G1/G3. **Risks:** checklists must not duplicate step postconditions (update-documentation's are per-step; keep only cross-cutting checks); keep each item a verifiable boolean; do not reference commands the skill doesn't document (test_skill_docs).

---

## Consolidated risks (all items)

1. **root `skills/` divergence → CI failure.** `sync_plugin_skills.py --check` runs in CI (`pylint.yml`) and `test_every_check_can_fail.py` pins it. Every edit to any of the 15 root-shipped skills requires step 2 of the shared loop. Missing it fails the PR.
2. **Repo's own `.ai-badger/` divergence → CI failure.** `gates/scaffold_freshness_guard.py` re-scaffolds the repo in CI. Requires shared-loop step 3 (re-scaffold), mandatory for extension-bearing skills (task).
3. **No test pins the exact bodies of the 23 SKILL.md files** (verified: test_evidence_first_research, test_den_refresh, test_learned_skills_sync, test_skill_usage, test_agent_doc_budget, test_skill_bodies_carry_procedure_not_evidence, test_scaffold_skill_extensions pin behavior or other files, not these bodies). The only content pins are the sync/freshness machinery above.
4. **Convention-adjacent tests:** don't introduce evidence tables (test_skill_bodies…), new `python3` paths (test_skill_docs), or new `plugin:skill` references (test_third_party_skill_references — `related_skills` values are bare names, safe).
5. **Size budgets:** safe — all 23 are ≤364 lines; max addition ~40 lines (task: frontmatter + gotchas + checklist → ~280).
6. **Worktree mechanics:** opt-skills worktree has no `.venv`; run all gates with the main checkout's `/Users/arasz/RiderProjects/ai-badger/.venv/bin/python3`; run sync/scaffolder/freshness-guard with `--root` pointing at the worktree.
7. **Cross-lane collisions:** I1–I4 (mcp-index, call-behaviorist, den-refresh), I5–I10 (task, commit-reminder, feed-badger, prompt-markers, welcome-ai-badger, maintain-agent-instructions) edit the same files this PR touches — the G-PR must merge first; G5 must not change root `skills/` shape concurrently.


## Part B — Individual skill improvements (from planning MoE lane 2)

# Individual skill improvements I1–I12 — executable plan (opt-skills worktree)

Base path for all edits: `.ai-badger/worktrees/opt-skills/` (repo root: `/Users/arasz/RiderProjects/ai-badger`). All paths below are relative to that root.

## Cross-cutting rules (apply to every item)

**R1 — Root `skills/` mirror is regenerated by `tooling/sync_plugin_skills.py`, gate-enforced (review round 1 finding 9).** Run `python3 tooling/sync_plugin_skills.py` in the same commit as every skill edit; the pre-push `plugin-skills` lane (`.lefthook/pre-push/verify.sh:96`) runs `--check` and fails on divergence — the per-file diff ritual below is only a quick sanity check, not the enforcement (the earlier "nothing enforces it" framing is stale; integration decision 1 supersedes it):
- Stub+full shape → the sync regenerates `skills/<name>/SKILL.full.md` + pointer SKILL.md (verified for `task`; applies to `code-review-checklist`, `mcp-index`, `call-behaviorist`, `commit-reminder`, `prompt-markers`, `maintain-agent-instructions`, `ai-raccoon-memory`).
- Full-copy shape → the sync regenerates `skills/<name>/SKILL.md` (verified byte-identical for `den-refresh`, `feed-badger`, `welcome-ai-badger`).
- Gate: `for d in <names>; do diff -q skills/$d/SKILL.full.md features/common/skills/$d/SKILL.md || diff -q skills/$d/SKILL.md features/common/skills/$d/SKILL.md; done` → all identical.

**R2 — Extension-merge machinery (do not touch markers).** `welcome-ai-badger/scripts/extensions.py` embeds `extensions/<name>/` fragments into the *scaffolded* SKILL.md at `<!-- EXT:name -->` markers and requires the `<!-- MERGE_EXTENSIONS -->` sentinel. `tests/test_scaffold_skill_extensions.py` pins this (markers keep position, sentinel removed after merge). Only `code-review-checklist`'s SKILL.md carries EXT markers and the sentinel (review round 1 finding 14: task, migrate-documentation, scaffold-documentation and update-documentation also ship `extensions/` dirs, but their SKILL.md bodies carry no `<!-- EXT:` markers) — its 9 `<!-- EXT:* -->` markers (incl. `cross-cutting` after §3.4 and `backend-runtime` after §4.3) and the sentinel must survive I1.

**R3 — `tests/test_skill_docs.py` pins SKILL.md script invocations.** Every literal `python3 <path>.py` in a SKILL.md must resolve (and no bare `scripts/x.py`). Consequence: (a) don't add new `python3` invocations in new sections unless the target script exists; (b) moved content is *unchecked* once in `references/`, so splits are safe; (c) `test_welcome_names_only_real_config_keys` (in `tests/test_skill_docs.py:100`) reads `welcome-ai-badger/SKILL.md` — I9 must not introduce new backticked `<name>Scope` keys.

**R4 — No evidence tables.** `tests/test_skill_bodies_carry_procedure_not_evidence.py` rejects `| Fact |` / `| Claim |` / `| Measurement |` tables in SKILL.md bodies — none of the planned additions use those headers.

**R5 — `index_build.py` keys skills on SKILL.md presence + frontmatter description.** No frontmatter/description changes are planned in I1–I12; still run `python3 tooling/index_build.py --check` after edits (uses main checkout `.venv/bin/python3` per repo invariant).

**R6 — Drift is self-healing by design.** New `references/` files change the framework source hash; real projects will see them as `drift.changed`/`newItems` on next `den-refresh` and pick them up. Expected, no code change. `tests/test_drift.py` is content-independent — unaffected.

**R7 — Standard verification for every item:** `python3 -m pytest -q` (full suite) + the per-skill greps below + root-mirror diff. Commit per skill (one PR for the whole I-series per "one task = one PR", but commit per skill for reviewability).

---

## I1 — code-review-checklist (363 lines / 4375 tok) — split security + observability

1. **Files:** edit `features/common/skills/code-review-checklist/SKILL.md`; create `features/common/skills/code-review-checklist/references/security.md`, `references/observability.md`; sync `skills/code-review-checklist/SKILL.full.md` (R1).

2. **What moves where:**
   - Lines 168–199 (heading `### 3.3 Security` through the OpenAI-provenance note, incl. 8 checklist items and the ts-extension note) → `references/security.md` (keep the `> Distilled from …` note with it).
   - Lines 238–262 (heading `### 4.3 Observability` through the Kotlin-provenance note, incl. all 5 items) → `references/observability.md`.
   - In SKILL.md, replace each moved block with a one-line pointer (review round 1 finding 4: do NOT keep the `### 3.3 Security` / `### 4.3 Observability` headings — the acceptance greps `== 0` require them gone): `> Full checklist: read references/security.md if the diff touches security surfaces (auth, secrets, input handling, redirects).` (same for observability: `…read references/observability.md if the diff touches metrics, logs, tracing, or readiness probes.`)
   - All other phases (1, 2, 3.1, 3.2, 3.4, 4.1, 4.2, 5–9) stay inline — checklists load whole. Keep `<!-- EXT:cross-cutting -->` (line 212) and `<!-- EXT:backend-runtime -->` (line 264) exactly where they are (R2).

3. **New sections:** none (splits only).

4. **Acceptance:**
   - `wc -l features/common/skills/code-review-checklist/SKILL.md` ≤ 325 (expected ≈ 315).
   - `grep -c "^### 3.3 Security" SKILL.md` == 0 and `grep -c "^### 4.3 Observability" SKILL.md` == 0 (content lives in references now).
   - `test -f references/security.md && test -f references/observability.md`.
   - `grep -c "references/security.md" SKILL.md` == 1 and that line contains `if`; same for `references/observability.md` (G2 gate).
   - `grep -c "MERGE_EXTENSIONS" SKILL.md` == 1; `grep -c "<!-- EXT:" SKILL.md` == 9.
   - `diff -q skills/code-review-checklist/SKILL.full.md features/common/skills/code-review-checklist/SKILL.md` → identical.

5. **Risks:** none of the moved lines contain `python3` invocations (R3 safe); token estimate ≈ 3700–3900 (< 5k); scaffolded copies grow via extension merge (R2), so judge size on the feature copy.

## I2 — mcp-index (265 / 3643) — split status + heuristics tables; add When NOT to Use

1. **Files:** edit `features/common/skills/mcp-index/SKILL.md`; create `references/status.md`, `references/heuristics.md`; sync `skills/mcp-index/SKILL.full.md` (R1).

2. **What moves where:**
   - Lines 187–213 (whole section `## Server status — why a silent server is still in the index`: intro para, status-enum table, "unknown is the honest reading" para, "additive enum" para) → `references/status.md`.
   - Lines 215–236 (whole section `## Auto-tagging Heuristics`: last-resort rule, issue-#171 warning, pattern table) → `references/heuristics.md`.
   - Replace each with a one-line pointer: `> Status meanings: read references/status.md if `update` reports a status other than `ok` (or when a silent server needs explaining).` and `> Auto-tagging rules: read references/heuristics.md when a tool came back `[general]` and you are deciding whether to curate it or extend the catalog.`
   - Keep inline: `origin` table (43–47, command-time authority), `## Tag Taxonomy` (60–71, needed by `tag`), all `## Commands` blocks, `## Where the server list comes from` (168–186), `## Common Pitfalls`, `## Verification Checklist`.

3. **New sections:** `## When NOT to Use` (insert after `## When to Use`, ~5 lines): one-off tool lookup → read the index JSON directly; writing a brand-new MCP server → `hermes-mcp-setup`; no MCP servers in the project → nothing to index; a wrong-tool call that is a one-off → tag it, don't re-architect.

4. **Acceptance:**
   - `grep -c "^## Server status" SKILL.md` == 0; `grep -c "^## Auto-tagging Heuristics" SKILL.md` == 0.
   - `test -f references/status.md && test -f references/heuristics.md`; each SKILL.md mention of them contains `if`/`when` (G2).
   - `grep -c "^## When NOT to Use" SKILL.md` == 1 with ≥ 3 bullets.
   - `wc -l` ≤ 240 (expected ≈ 230); all `mcp_index.py` command invocations retained (R3: `tests/test_skill_docs.py` passes).
   - `diff -q skills/mcp-index/SKILL.full.md …` identical.

5. **Risks:** none of the moved sections carry script invocations; `tests/test_mcp_index*.py` target scripts/JSON, not SKILL.md — verified no pinning.

## I3 — call-behaviorist (235 / 3561) — split record/event/finding tables; add verification checklist

1. **Files:** edit `features/common/skills/call-behaviorist/SKILL.md`; create `references/record-format.md`, `references/findings.md`; sync `skills/call-behaviorist/SKILL.full.md` (R1).

2. **What moves where:**
   - `references/record-format.md` gets the three dense tables: record key table (49–57), retrieval event table (81–89), retrieval key table (98–106). Add a 2-line head: the compact-JSON example (46–47) and the `PIPE_BUF` atomic-append constraint (59–63) — they explain the single-letter keys.
   - `references/findings.md` gets the whole subsection `### What the findings mean` (162–184): findings table + the `health`-verdict paragraphs ("health is ok/warn/degraded/unknown…", "Evidence is not the same as lines in the log").
   - In SKILL.md, replace with pointers: `> Field-by-field record and event semantics: read references/record-format.md when interpreting `tail` or `analyze` output.` and `> Finding meanings and health verdict rules: read references/findings.md when interpreting `analyze` output.`
   - Keep inline: record example (40–47), the load-bearing bullets (65–70: version on every record / start-vs-skip / project), retrieval-telemetry intro (75–79) + redaction subsection (108–115), `## Where things live`, `## Reading the log` jq examples, `### Where the expected components come from` (148–161), `### Writing it up` (201–213), `### Filing it` (215–229), turn-off note (231–235).

3. **New sections:** final `## Verification Checklist` (≈7 items): `status` shows enabled with an expiry; `tail` renders records one line each; `analyze --json` exits 0 and names findings; report leads with what is wrong, includes window + record count, names versions/ranges for `version_skew`; `window.unattributed` reported if non-zero; logging expired/switched off when done.

4. **Acceptance:**
   - `grep -c "^| \`t\` |" SKILL.md` == 0 and `grep -c "^| \`hit\` |" SKILL.md` == 0 (tables gone from body; review round 1 finding 13: the original `kind` row does not exist today — `hit` is the real first event-table row).
   - `test -f references/record-format.md && test -f references/findings.md`; each mention in SKILL.md contains `when` (G2).
   - `grep -c "^## Verification Checklist" SKILL.md` == 1.
   - `wc -l` ≤ 210 (expected ≈ 200); `python3 -m pytest tests/test_behaviorist_analyze.py -q` passes (script untouched).
   - `diff -q skills/call-behaviorist/SKILL.full.md …` identical.

5. **Risks:** none of the moved tables contain `python3` invocations; no test pins call-behaviorist SKILL.md body (verified).

## I4 — den-refresh (227 / 3598) — split error-recovery table; add verification checklist

1. **Files:** edit `features/common/skills/den-refresh/SKILL.md`; create `references/error-recovery.md`; sync `skills/den-refresh/SKILL.md` (full-copy shape, R1).

2. **What moves where:** the error/fix table (211–219, 9 rows) → `references/error-recovery.md` (with the 2-line lead "structured JSON error → classify → fix → re-run"). In SKILL.md keep the `## Error Recovery` steps 1–3 intact and replace the table with: `> Fix table: read references/error-recovery.md when refresh.py exits non-zero or returns a JSON `error` field.`

3. **New sections:** final `## Verification Checklist` (after Error Recovery, ≈6 items): refresh exited 0; report read section by section (`frameworkVersion`, `drift.*`, `reScaffolded`, `note`, `frameworkCopies`); competing copies surfaced, `~/.ai-badger/framework` only pruned on request; prune candidates offered, never pruned (`config.json` untouched); diff reviewed before commit; seed-once files (`state.json`, `markers-context.json`, `model.json`) absent from the diff.

4. **Acceptance:**
   - `grep -c "Scaffold script raised an exception" SKILL.md` == 0 (table gone); `grep -c "references/error-recovery.md" SKILL.md` == 1 and the line contains `when`.
   - `test -f references/error-recovery.md`; `grep -c "^## Verification Checklist" SKILL.md` == 1.
   - `wc -l` ≤ 235 (expected ≈ 228 — table out, checklist in).
   - `python3 -m pytest tests/test_den_refresh.py tests/test_skill_usage.py -q` passes.
   - `diff -q skills/den-refresh/SKILL.md features/common/skills/den-refresh/SKILL.md` identical.

5. **Risks:** table rows' `python3 …` commands (all `$AI_BADGER`-prefixed or `-m pip`) are already unresolvable-prefix-skips in `test_skill_docs.py`; moving them out is safe. `tests/test_den_refresh.py` mocks skill content (`# name` bodies) — no pinning.

## I5 — task (238 / 3829) — add Gotchas + When NOT to use

1. **Files:** edit `features/common/skills/task/SKILL.md`; sync `skills/task/SKILL.full.md` (R1; stub `skills/task/SKILL.md` untouched).

2. **What moves where:** nothing (no split).

3. **New sections:**
   - `## When NOT to Use` (after the intro block, before `## Config contract`, ≈5 lines): a single-file typo fix or one-off question — no tracking/worktree/delegation needed; work the user wants done inline in this session; anything where the token-tracked pipeline's overhead exceeds the task (use the plain workflow).
   - `## Gotchas` (before `## Recovery`, ≈12 lines, lifted from existing prose — do NOT renumber phases):
     1. `start` without `--worktree` records a branch name nothing creates — `status` then reports a branch that doesn't exist (2026-08-01: two commits landed on `main`).
     2. `finish` refuses and keeps the worktree when it holds work that exists nowhere else — read `worktree.keptBecause`; a kept worktree is unmerged/uncommitted work, not failed cleanup.
     3. Never rewrite always-loaded context files (`CLAUDE.md`, `.ai-badger/state.json`) mid-task — subagent cache reads depend on a byte-stable prefix (~10× cost); rewrite only between tasks.
     4. Two levels of dispatch, no deeper — a widening agent tree starves the machine.

4. **Acceptance:**
   - `grep -c "^## Gotchas" SKILL.md` == 1 with ≥ 4 bullets; `grep -c "^## When NOT to Use" SKILL.md` == 1.
   - Phase headings stable: `grep -c "^## Phase" SKILL.md` == 6 (task_tracker.py prints "SKILL.md Phase 1 step 3" — must stay true).
   - `wc -l` ≤ 265 (expected ≈ 255); `python3 -m pytest tests/test_task_checkpoint_wiring_end_to_end.py tests/test_adjust_task_claude.py tests/test_adjust_task_hermes.py -q` passes.
   - `diff -q skills/task/SKILL.full.md features/common/skills/task/SKILL.md` identical.

5. **Risks:** added text must not introduce backticked config keys or new `python3` invocations (R3). Scripts referencing SKILL.md (`task_tracker.py:339`, `poll_limit.py:54`) reference phase numbers/other skills — unaffected by appended sections.

## I6 — commit-reminder (135 / 1843) — add Gotchas

1. **Files:** edit `features/common/skills/commit-reminder/SKILL.md`; sync `skills/commit-reminder/SKILL.full.md` (R1).

2. **What moves where:** nothing.

3. **New sections:** `## Gotchas` (after `## Escalation: an agent that never commits`, before `## Configuration`, ≈10 lines):
   1. The escalation bar is three *new highs*, not three commands over a span of time — an agent editing the same five files repeatedly is never asked twice.
   2. Anything that lowers the count clears the unanswered counter — `git stash` or a cleaned build dir clears it exactly like a commit; the hook cannot tell them apart.
   3. The hook only ever adds `additionalContext` — no `decision`/`permissionDenied`/`continue` on any code path (changelog 0.33.0: the third-party-interception incident).
   4. `ensure_committed.py` exits 0 even when work is at risk — and on malformed state; a crash would be worse than the report a parent must read.

4. **Acceptance:**
   - `grep -c "^## Gotchas" SKILL.md` == 1 with ≥ 4 bullets; `wc -l` ≤ 155 (expected ≈ 148).
   - `python3 -m pytest tests/test_commit_reminder.py tests/test_commit_reminder_hook.py tests/test_commit_reminder_wiring.py tests/test_commit_reminder_hermes.py tests/test_commit_command_escalation.py -q` passes.
   - `diff -q skills/commit-reminder/SKILL.full.md …` identical.

5. **Risks:** keep the `## Migration` section as-is (it is a compatibility note, not a gotcha); no test reads this SKILL.md body (verified).

## I7 — feed-badger (99 / 1258) — add Gotchas + verification checklist

1. **Files:** edit `features/common/skills/feed-badger/SKILL.md`; sync `skills/feed-badger/SKILL.md` (full-copy shape, R1).

2. **What moves where:** nothing.

3. **New sections:**
   - `## Gotchas` (after `## Rules`, ≈8 lines): (1) draft PR, always — a human reviews and merges, never auto-merge; (2) `--path` is required and repeatable — only declared paths are staged, so an unrelated dirty file cannot ride along; (3) the credential scan is a guard, not proof — it checks known literal shapes; a clean run is not a certificate; (4) the agnostic bar is high — when unsure, keep it in the project.
   - Final `## Verification Checklist` (after `## Error Recovery`, ≈6 items): `detect_additions.py` ran and every candidate was classified (dropped project-specific ones have stated reasons); every keeper generalized (no repo names, domain nouns, absolute paths); placed files pass `index_build.py` + `validate.py --all` in the checkout; draft PR opened with `--path` naming every placed path (and `index.json` if regenerated); credential scan clean.

4. **Acceptance:**
   - `grep -c "^## Gotchas" SKILL.md` == 1 (≥ 4 bullets); `grep -c "^## Verification Checklist" SKILL.md` == 1 and it is the last `##` section (`grep "^## " SKILL.md | tail -1`).
   - `wc -l` ≤ 125 (expected ≈ 115); `python3 -m pytest tests/test_open_pr.py tests/test_detect_additions.py -q` passes.
   - `diff -q skills/feed-badger/SKILL.md features/common/skills/feed-badger/SKILL.md` identical.

5. **Risks:** root mirror is the full copy — the pre-push `plugin-skills` gate (verify.sh:96) fails the PR if `skills/feed-badger/` desyncs, so run the sync in the same commit (R1; the earlier "no test gate" framing is stale).

## I8 — prompt-markers (93 / 1190) — add Gotchas

1. **Files:** edit `features/common/skills/prompt-markers/SKILL.md`; sync `skills/prompt-markers/SKILL.full.md` (R1).

2. **What moves where:** nothing.

3. **New sections:** `## Gotchas` (after `## Auditing`, before `## Installation`, ≈10 lines):
   1. The hook *appends* via `additionalContext` and never rewrites the prompt — prepending or rewriting invalidates prompt caching for that turn and every subsequent one (rationale recorded in ADR-0017; mirror it in the project's ADRs instead of re-deriving).
   2. Registration merges into existing arrays — if the project already runs a `UserPromptSubmit` hook (e.g. task's session tracker), add an entry, never replace it; the host runs all registered hooks.
   3. The audit write is best-effort by design — it only fires when an `.ai-badger` directory already exists; a missing `marker-state.json` is not a hook failure.
   4. Marker definitions live in `markers-context.json` — edit that file to add/change a marker, not the hook.

4. **Acceptance:**
   - `grep -c "^## Gotchas" SKILL.md` == 1 (≥ 4 bullets); `wc -l` ≤ 115 (expected ≈ 105).
   - `python3 -m pytest tests/test_user_prompt_hook.py tests/test_scaffold_hook_wiring.py tests/test_adjust_hooks_copilot.py -q` passes.
   - `diff -q skills/prompt-markers/SKILL.full.md …` identical.

5. **Risks:** no script path/config-key changes (R3 safe); hook-wiring tests target `user_prompt_hook.py` behavior, not SKILL.md.

## I9 — welcome-ai-badger (152 / 2129) — add verification checklist

1. **Files:** edit `features/common/skills/welcome-ai-badger/SKILL.md`; sync `skills/welcome-ai-badger/SKILL.md` (full-copy shape, R1).

2. **What moves where:** nothing.

3. **New sections:** final `## Verification Checklist` (after `## Error Recovery`, ≈7 items): `validate.py --kind config` passed on the authored config; scaffold output covers exactly the selected stacks — no leakage from unselected stacks; `.ai-badger/` holds config.json, manifest.json, CLAUDE.md, agents/, instructions/, invariants/, skills/, agent-instructions/, state.json; plugin-setup commands relayed per the chosen scope (default or local-only); preserved hand-authored discovery files reported, not overwritten; any "competing copies" tree list relayed, and nothing outside the target deleted.

4. **Acceptance:**
   - `grep -c "^## Verification Checklist" SKILL.md` == 1, last `##` section (`grep "^## " SKILL.md | tail -1`).
   - `wc -l` ≤ 175 (expected ≈ 165); no new backticked `<name>Scope` keys (R3: `test_welcome_names_only_real_config_keys` in `tests/test_skill_docs.py:100` passes — the file is real; the standalone name is not).
   - `python3 -m pytest tests/test_scaffold_skill_extensions.py tests/test_scaffolding.py -q` passes.
   - `diff -q skills/welcome-ai-badger/SKILL.md features/common/skills/welcome-ai-badger/SKILL.md` identical.

5. **Risks:** in *scaffolded* copies, `extensions.py` appends `project-local.md` content after the checklist — the checklist won't be the literal last section when project-local content exists; acceptable (note in PR description). (The "keep `<!-- MERGE_EXTENSIONS -->` layout untouched" note from the draft is moot — welcome-ai-badger's SKILL.md carries no sentinel; review round 1 finding 20.)

## I10 — maintain-agent-instructions (81 / 882) — add When NOT to use + verification checklist

1. **Files:** edit `features/common/skills/maintain-agent-instructions/SKILL.md`; sync `skills/maintain-agent-instructions/SKILL.full.md` (R1).

2. **What moves where:** nothing.

3. **New sections:**
   - `## When NOT to Use` (after intro, before `## Principles`, ≈4 lines): a single-file typo fix in one instruction file — edit it directly; no drift exists and CI checks pass; authoring brand-new policy from scratch (that is content work, not reconciliation).
   - Final `## Verification Checklist` (≈6 items): both scripts ran from the project root; both exit 0 (or every reported failure was fixed and re-runs pass); only the reported files/rules were touched; the model was updated before any shared-policy change; ADR added/updated when architecture/process policy changed.

4. **Acceptance:**
   - `grep -c "^## When NOT to Use" SKILL.md` == 1; `grep -c "^## Verification Checklist" SKILL.md` == 1 (last `##` section).
   - `wc -l` ≤ 105 (expected ≈ 95).
   - `diff -q skills/maintain-agent-instructions/SKILL.full.md …` identical.

5. **Risks:** existing `references/…` mentions already carry soft conditions (`by default; see`, `see` inside step context) — leave them; `tests/js/form_template.test.mjs` pins a different file (form template), unaffected.

## I11 — ai-raccoon-memory (114 / 1323) — add When NOT to use

1. **Files:** edit `features/common/skills/ai-raccoon-memory/SKILL.md`; sync `skills/ai-raccoon-memory/SKILL.full.md` (R1).

2. **What moves where:** nothing.

3. **New sections:** `## When NOT to Use` (after the title intro, before `## 1. Watch-on-docs ritual`, ≈5 lines): a one-off lookup ("have we seen X before?") — run `memory_search` and be done, no watch ritual, no write-back; no docs directory to watch and no durable fact to write — the ritual adds ceremony, not value; the memory-grade hook when you only need one answer (it is opt-in by env var; don't enable it for a single search).

4. **Acceptance:**
   - `grep -c "^## When NOT to Use" SKILL.md` == 1 (≥ 3 bullets); `wc -l` ≤ 130 (expected ≈ 122).
   - Section numbering unchanged: `grep -c "^## [0-9]" SKILL.md` == 8 (Pitfalls/checklist stay numbered as-is).
   - `python3 -m pytest tests/test_memory_grade_*.py -q` passes; `diff -q skills/ai-raccoon-memory/SKILL.full.md …` identical.

5. **Risks:** `test_common_ai_raccoon_mcp_server.py` only asserts SKILL.md file existence — safe.

## I12 — evidence-first-research (118 / 1501) — loading conditions on Files

1. **Files:** edit `features/common/skills/evidence-first-research/SKILL.md` only (no root mirror — `evidence-first-research` is absent from root `skills/`; verified).

2. **What moves where:** nothing. Rewrite `## Files` (lines 114–118) to:
   - `references/provenance.md` — what each grade means, what disqualifies one, worked examples. **Read it when grading a finding, or when a grade is disputed.**
   - `references/report-template.md` — the record shape the renderer parses. **Read it when writing the record (step 3).**
   - `scripts/render_report.py` — record → self-contained HTML. No network, inline SVG, no scripts.
   - Also append the same condition to the inline mention at line 46: `Full rules in references/provenance.md — read it when grading or when a grade is disputed.` (Line 58's mention already sits inside the step that is its condition; leave it.)

3. **New sections:** none.

4. **Acceptance:**
   - G2 gate: `grep -n "references/" SKILL.md` → 3 of 4 mention lines carry an explicit `when`/`if` (review round 1 finding 5: line 58's mention sits inside its own step — the step IS the condition — so the gate is `grep -c 'references/.*\(when\|if\)' == 3`, with line 58's in-step conditioning noted in a comment).
   - `python3 -m pytest tests/test_evidence_first_research.py -q` passes (it pins `references/provenance.md` + `references/report-template.md` existence post-scaffold — untouched files).
   - `wc -l` ≤ 125 (expected ≈ 120).

5. **Risks:** none — text-only edit; renderer and scripts untouched.


## Part C — Tooling gate + repo shape (from planning MoE lane 3)

# G4 — skills-lint gate in `tooling/validate.py` · G5 — root `skills/` consistency

## G5 first: what root `skills/` actually is (answers the audit's Still-open question)

**Finding (read `tooling/scaffold.py` — it does not exist; the scaffold lives elsewhere):** there is no `tooling/scaffold.py`. The scaffolder is `features/common/skills/welcome-ai-badger/scripts/scaffold.py` + `skill_delivery.py`, and it delivers skills to **`<project>/.ai-badger/skills/`** only (verified in `skill_delivery.py`: `skills_root = target / ".ai-badger" / "skills"`). The root `skills/` dir is written by **`tooling/sync_plugin_skills.py`** (`TARGET = ROOT / "skills"`), which exists because **Claude Code scans `<plugin-root>/skills/` and nowhere else (ADR-0008)**. So root `skills/` is **distribution-critical, not dev-only and not scaffold output** — the audit's two proposed options are both wrong:

- **Drop root `skills/`** → breaks Claude Code plugin distribution (ADR-0008).
- **Flatten all 15 to stubs** → breaks the bootstrap three. `BOOTSTRAP_SKILLS = {welcome-ai-badger, den-refresh, feed-badger}` deliberately keep their full body inline (they run before/without a scaffold; `den-refresh`/`feed-badger` must run the framework's own copy across breaking version boundaries — ADR-0011). Pinned by `tests/test_plugin_copy_points_at_the_tailored_one.py` (`test_the_body_is_inline` asserts shipped == source byte-for-byte and no `SKILL.full.md`).

The "three shapes" framing in the audit was a misread: `SKILL.full.md` is the stub's **companion**, not a third shape. The real shape set is two, both intentional: `{SKILL.md-pointer + SKILL.full.md}` for the 12 non-bootstrap skills, `{inline SKILL.md, no SKILL.full.md}` for the 3 bootstrap skills. **The drift hazard the audit worried about is already closed**: `sync_plugin_skills.py --check` renders the expected copy into a temp dir and hash-compares against `skills/` (so a maintainer editing `skills/task/SKILL.full.md` directly fails `--check`), and the repo is in sync today (`test_the_real_repo_is_in_sync` asserts `--check == 0`).

**What is genuinely missing for G5:** the shape rule lives only in tests; no script asserts "every `skills/` dir has exactly the derived shape" as a gate. Plan:

1. Add `shape_violations(root: Path) -> List[str]` to `tooling/sync_plugin_skills.py` — **derive the expected top-level file set by rendering each shipped skill into a temp dir and comparing entry names against the dest dir** (review round 1 Part C F5; `check_skill` already renders into a temp dir, so reuse that path — do NOT reimplement `render_into`'s contract, which the earlier SKILL.md-and-SKILL.full.md formulation disagreed with on the no-frontmatter edge where `render_into` writes no `SKILL.full.md`). The render-compare subsumes: missing `SKILL.md`, missing/extra `SKILL.full.md`, and extra top-level `SKILL.*` variants, for shipped non-bootstrap (pointer+full) and bootstrap (inline, no full) skills alike; orphaned dirs stay handled by `_orphans`. Wire into `check_all()` so `--check` exits 1 on any shape violation. (Verified: no currently-shipped skill violates the rule — latent, not live.)
2. Content drift root↔features stays exactly as-is (`check_skill` hash-compare; no change).
3. Tests in `tests/test_plugin_copy_points_at_the_tailored_one.py` style — note that file is real-root-only, so the new G5 tests use `tmp_path` fake trees via `load_script` (conftest.py:320–340 supports it; review round 1 Part C F7).
4. Update the audit G5 gate wording: "every root skills/ dir has **one of the two declared shapes, assigned exactly by `BOOTSTRAP_SKILLS`**; drift check covers root↔features equality" — both now machine-checked.

## G4 — skills-lint: where it plugs in

`tooling/validate.py` already has kinds `skills`/`skills-source` — but those schema-validate the JSON manifests (`features/*/skills.json`), **not** `SKILL.md` files. The audit's Still-open question ("new kind or index_build?") is answered by the repo's own precedent: `hooks_manifest_agent_gaps()` is a bespoke tree rule wired into `validate_all()`, reported via `_report()`, running under `--all` and the pre-push `validate` lane (`.lefthook/pre-push/verify.sh`: `validate) "$PY" tooling/validate.py --all`). **Simplest shape that serves: mirror it — one function, no new CLI kind, no index_build change.**

- Add `skills_lint(root: Path) -> List[str]` to `validate.py`, called from `validate_all()` as `ok &= _report("skills lint", skills_lint(root))`. Handles a missing `features/` (fake test roots) by returning `[]`.
- Discovery glob mirrors `index_build._skill_items`: `features/*/skills/*/SKILL.md`, skipping `*-extensions` dirs → 23 files today (22 common + `features/claude/skills/auto-wm`). The plugin `skills/` dir is **out of scope** (stub pointers are not real bodies; G5 covers shape/content there).
- **index_build.py interaction: none.** It discovers dirs containing `SKILL.md` (unchanged) and is deliberately YAML-free; the audit's "index_build emits tags" suggestion has no consumer (`welcome-ai-badger`/`feed-badger` read `name`/`path`/`scope` only) — skip it (ask-if-simpler: it would add frontmatter parsing to a tool that doesn't need it).
- **Frontmatter parsing (ADR-0005 constraint):** ADR-0005 rejected YAML-frontmatter parsing for *behavioural* script use on cost. The lint is a tooling-side gate, but stay consistent: **reuse/extend the stdlib, line-oriented extractor pattern already in `badger_lib.skill_description` / `_folded_lines`** (handles `>-` folded scalars; extend to bracket lists `[a, b]` and the `metadata:` → `  hermes:` → `    tags:` nesting). No new pyyaml dependency. Anything the extractor cannot parse deterministically is reported as a violation (refuse, don't pass — repo invariant), with a one-line hint. PyYAML stays optional, exactly as today.

## G4 — the lint rules (each = one small pure function, one test)

Checked per catalog `SKILL.md`; body = text after the closing `---`:

| # | Rule | Predicate / constant |
|---|---|---|
| 1 | name grammar | `re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", name)` and `len(name) <= 64` (constants `NAME_RE`, `MAX_NAME_LEN = 64`) |
| 2 | name == dir | frontmatter `name:` == parent dir name |
| 3 | description present | `description:` key exists and non-empty after folding (reuse `bl.skill_description`) |
| 4 | description ≤1024 | `len(desc) <= 1024` (`MAX_DESC_LEN = 1024`) |
| 5 | description starts "Use when" | `desc.startswith("Use when")` |
| 6 | size lines | `len(body.splitlines()) <= 500` (`MAX_LINES = 500`, exact) |
| 7 | size tokens | `len(body) / 4 <= 5000` (`MAX_TOKENS = 5000`, chars/4 proxy — deterministic stdlib; review round 1 measured the corpus max: code-review-checklist body = 16,904 chars → **4,226 proxy** (whole file 4,422), NOT ≈3,700 as first estimated — the proxy tracks the audit's real-tokenizer count (4375) at ≈1.01×, so the budget still holds with margin; whitespace-token proxy rejected: it undercounts 1.53–1.75× and would admit ~8k real tokens. PR must record the measured corpus max in a comment.) |
| 8 | references/ conditions (G2) | **Must BE the G2 checker, not a second predicate** (review round 1 findings 7 and Part C F1): extract the G2 acceptance logic — regex `\b(when|if|before|after|only when)\b`, 3-line context window `{i−1, i, i+1}`, numbered-step skip, same exempt list — into one shared function both the G2 acceptance script and `skills_lint` call. The earlier paragraph-level `when|if|unless` formulation is withdrawn: it flags the plan's own "already compliant" set (maintain-agent-instructions:24, owner-gate-review:42/103, scaffold-documentation:84) and misses scaffold-documentation:72 — it serves neither invariant |
| 9 | gotchas (G1) | body contains `^##\s+(?:\d+\.\s+)?Gotchas\b` (MULTILINE — the `(?:\d+\.\s+)?` accepts ai-raccoon-memory's `## 6. Gotchas`; review round 1 Part C F2) **or** a line matching `no environment-specific gotchas known` (IGNORECASE) |
| 10 | frontmatter completeness (G3) | required keys all present and parseable: `name, description, version, author, license, platforms, metadata.hermes.tags, metadata.hermes.related_skills` (arrays may be empty; presence is the gate) |

Measured today (2026-08-07, `.venv/bin/python3`): all 23 pass rules 1–7; **0/23** pass rule 9 (`## Gotchas`); **11/23** mention `references/` — 26 mention lines / 31 occurrences, 2 already conditioned (owner-gate-review:103, maintain-agent-instructions:24), 19 flagged by the line-checker (review round 1 finding 11 corrects the earlier "29 mentions, 0 conditioned" count); **10/23** miss frontmatter keys (rule 10). So rules 8–10 fail the corpus until the G1/G2/G3 content work lands — **ordering is mandatory**, see below.

## G4 — tests (new file `tests/test_skills_lint.py`, `test_validate.py` style: `tmp_path` + `load_script`, no file edits to the real tree)

- One test per rule with a minimal violating fixture SKILL.md (e.g. `test_name_grammar_rejects_uppercase`, `test_references_mention_without_condition_is_reported`, `test_gotchas_section_required`, `test_frontmatter_missing_keys_reported`, `test_size_over_500_lines_reported`, …) + one positive fixture passing all rules.
- `test_validate_all_reports_skills_lint`: fake root (copy real schemas per `_copy_real_schemas`) + one violating skill → `rc == 1`, `"skills lint"` and `INVALID` in output.
- `test_skills_lint_ignores_a_root_without_features`: fake root → `rc == 0`.
- **Final PR only:** `test_the_real_corpus_passes_skills_lint` (real root, `skills_lint(root) == []`) — lands together with the G1/G2/G3 corpus fixes so `test_all_flag_validates_the_real_framework_tree_and_reports_ok` never goes red.

**Sequencing (one PR per task):** PR 1–3 = corpus conformance (G1 gotchas rename+lifts, G2 conditions on the 26 mention lines, G3 frontmatter on the 10 skills) — content only, no tooling. PR 4 = G4 lint + tests + real-corpus test (green because 1–3 landed). G5 = independent PR (shape assertion + tests). VERSION bump + changelog entry per PR (repo invariant; G4 is a feature → minor).

## Acceptance criteria (concrete commands, from the worktree root; venv = main checkout's)

```bash
# G5
.venv/bin/python3 tooling/sync_plugin_skills.py --check          # exit 0: shape + drift both green
.venv/bin/python3 -m pytest -q tests/test_plugin_copy_points_at_the_tailored_one.py
# G4 — after corpus PRs
.venv/bin/python3 tooling/validate.py --all                      # exit 0, prints "ok       skills lint"
.venv/bin/python3 -m pytest -q tests/test_skills_lint.py
.venv/bin/python3 -m pytest -q                                   # full suite green
bash .lefthook/pre-push/verify.sh validate                       # lane green standalone
# corpus readiness probe (before PR 4): every line prints PASS
.venv/bin/python3 - <<'EOF'
import re, pathlib
for p in sorted(pathlib.Path("features").glob("*/skills/*/SKILL.md")):
    t = p.read_text(); b = t.split("---", 2)[2]
    ok = (len(b.splitlines()) <= 500 and len(b)/4 <= 5000
          and bool(re.search(r"^##\s+Gotchas", b, re.M) or re.search(r"no environment-specific gotchas known", b, re.I)))
    print("PASS" if ok else "FAIL", p.parent.name)
EOF
# G3-metadata-survives-scaffolding check (audit Still-open #4): scaffold a scratch project, diff frontmatter
#   .venv/bin/python3 features/common/skills/welcome-ai-badger/scripts/scaffold.py \
#     --config <scratch>/config.json --target <scratch> --root . --no-install
#   diff <(sed -n '1,/^---$/p' features/common/skills/task/SKILL.md) \
#        <(sed -n '1,/^---$/p' <scratch>/.ai-badger/skills/task/SKILL.md)   # empty = survives
```

**Note on mutation testing:** `validate.py` additions run through the pre-push `mutation`-adjacent lanes; keep each rule function tiny and fully covered by the fixture tests above.


## Integration decisions (orchestrator, after all lanes land)

1. **Root mirror sync is gate-enforced — lane 1's R1 is corrected.** The pre-push gate
   `.lefthook/pre-push/verify.sh:96` (`plugin-skills`) runs `tooling/sync_plugin_skills.py --check`
   (verified by the orchestrator; lanes 0 and 2 agree). The per-file `diff` ritual in lane 1's R1
   is replaced by the shared loop's step 2 (`python3 tooling/sync_plugin_skills.py`) — one command,
   gate-backed, covers all 15 shipped skills. Lane 1's stated risk "no automated gate" is stale.
2. **Audit G5 stands as corrected**: shapes are intentional (12 pointer+full, 3 bootstrap inline);
   the only G5 work is lane 2's `shape_violations()` assertion in `sync_plugin_skills.py` (new,
   small, test-pinned).
3. **Sequencing (from lanes 0 and 2, consistent):** PR-1 corpus conventions (G1+G2+G3+G6 in 4
   commits, one PR), PR-2 individual improvements (I1–I12, one PR, commit per skill), PR-3 G4
   skills-lint (needs corpus green first), PR-4 G5 shape assertion. Each PR bumps VERSION +
   changelog (G4 is a feature → minor; content PRs patch).
4. **G4 rule 9 calibration:** the "gotchas present" rule must accept the one-line
   "No environment-specific gotchas known" note (G1 template) — lane 2's rule 9 already does.
5. **G2's checker script** (lane 0 acceptance) is the executable gate for the G2 commit; it must
   run green before PR-1 is reviewable.
6. **Lane 0's audit-gap fix adopted:** G3 covers 10 skills (adds call-behaviorist,
   create-task-spec), not 8.
7. **Open decisions carried into implementation:** (a) size-budget policy for trigger-specific
   operational skills — the plan treats ≤500/≤5000 as a hard gate (rule 6-7) and the I1–I4 splits
   as the way to stay under; (b) `metadata.hermes.tags` survival through scaffolding — lane 2's
   acceptance includes the scaffold-diff probe; (c) Hermes-side skills (`.ai-badger/skills/`) get
   re-scaffolded by the freshness guard, so frontmatter edits propagate — no separate step.
8. **Token accounting:** planning-lane delegation (deleg_3c3a4429) carried no token record in the
   session source; `task_tracker.py subagent` refused to fabricate. Recorded as unknown.

## Review record (MoE review group)

**Round 1 — 2026-08-07, 2 parallel code-reviewer agents (deleg_d300e912), verdicts: APPROVE-WITH-FIXES (both).** All findings verified by the reviewers against the real corpus and repo code (no files edited by reviewers). Dispositions below; every FIXED item is already applied to this plan.

| # | Severity | Finding (short) | Disposition |
|---|---|---|---|
| R1-1 | BLOCKER | G2 acceptance gate fails on 5 lines after planned edits (scaffold-documentation:72, update-documentation:37, den-refresh:225, feed-badger:96, welcome-ai-badger:149) | FIXED — G2 edit table now includes all 5 with trigger wording; trigger word placed on the mention line / in-window |
| R1-2 | BLOCKER | Part A × Part B duplicate Gotchas/Checklist sections on 7 files; I-item `== 1` greps would fail | FIXED — ownership split: G1-B defers task/commit-reminder/prompt-markers/feed-badger to I5–I8; G6 defers den-refresh/feed-badger/welcome-ai-badger/maintain-agent-instructions to I4/I7/I9/I10 |
| R1-3 | MAJOR | G1 acceptance `== 23` unreachable: call-behaviorist, code-review-checklist, create-task-spec get no Gotchas | FIXED — added to G1 list C (14 files) |
| R1-4 | MAJOR | I1 edit contradicts its own acceptance grep (`### 3.3 Security` heading) | FIXED — pointer-only replacement, no headings; 9→8 items corrected |
| R1-5 | MAJOR | I12 acceptance `== 4` yields 3 (line 58's mention is in-step) | FIXED — acceptance `== 3` with in-step note |
| R1-6 | MAJOR | G1 migrate-documentation merge misdescribed (no duplicate heading; Red-flags bullets would dangle) | FIXED — demote h2→h3, keep l.182, move h3+table below bullets |
| R1-7 | MAJOR | G2 checker and G4 rule 8 are two different predicates; rule 8 flags the plan's own "already compliant" set | FIXED — rule 8 IS the G2 checker (one shared function); paragraph-level `when|if|unless` withdrawn |
| R1-8 | MAJOR | G2 context-window prose wrong (code is {i−1,i,i+1}); scaffold-documentation:84 "bullet lead" justification false | FIXED — rule prose corrected; :84 re-justified as explicit exemption |
| R1-9 | MAJOR | R1 "nothing enforces root sync" stale | FIXED — R1 defers to `sync_plugin_skills.py` + pre-push gate (also I7 risk line) |
| R1-10 | MINOR | "5 files" header vs 6 rows | FIXED |
| R1-11 | MINOR | G2/G4 corpus counts wrong (12/23, 29 mentions, 0 conditioned) | FIXED — re-measured: 11/23, 26 lines/31 occurrences, 2 conditioned, 19 flagged |
| R1-12 | MINOR | I2 line range 176–185 wrong | FIXED — 168–186 |
| R1-13 | MINOR | I3 `kind` row grep vacuous | FIXED — greps `hit` row |
| R1-14 | MINOR | "Only code-review-checklist has extensions/" false | FIXED — reworded (markers/sentinel only) |
| R1-15 | MINOR | test_welcome_names_only_real_config_keys is not a file | FIXED — cited as test_skill_docs.py:100 (2 places) |
| R1-16 | MINOR | G6 commit-reminder placement vs own rule | FIXED — insert before `## Files` |
| R1-17 | MINOR | Off-by-one line counts (den-refresh 227→228, maintain 81→80, G1 22→20 unique, 13→12 root-shipped) | FIXED |
| R1-18 | NIT | "When NOT to use" capitalization inconsistent with existing "When NOT to Use" | FIXED — unified to "When NOT to Use" (I2/I5/I10/I11) |
| R1-19 | NIT | G1 acceptance grep misses `## 6. Pitfalls` | FIXED — added numbered-variant grep |
| R1-20 | NIT | I9 sentinel note moot | FIXED — marked moot |
| R1-21 | NIT | G2 and I12 both edit evidence-first-research files with different final text | FIXED — I12 owns the wording; G2's evidence-first-research rows now say "see I12" |
| R2-F1 | MAJOR | Lint rule 8 ≠ G2 gate (same as R1-7) | FIXED (with R1-7) |
| R2-F2 | MAJOR | Rule 9 regex rejects `## 6. Gotchas` | FIXED — `^##\s+(?:\d+\.\s+)?Gotchas\b` |
| R2-F3 | MAJOR | scaffold-documentation:72 missing from G2 table (same as R1-1) | FIXED |
| R2-F4 | MINOR | chars/4 calibration wrong (3,700 → 4,226 body / 4,422 whole) | FIXED |
| R2-F5 | MINOR | shape_violations reimplements render_into contract | FIXED — render-compare approach |
| R2-F6 | NIT | G3 acceptance imports pyyaml unguarded | FIXED — noted venv requirement / stdlib extractor |
| R2-F7 | NIT | Wiring notes (CI pylint.yml:42; G5 tests need tmp_path via load_script) | FIXED — G5 test note added |

**Also confirmed by the review round (no action):** the plan's architecture (features/ as source of truth, sync+freshness gates, PR sequencing) is sound; the wiring of `skills_lint` into `validate_all()`/`_report()`/pre-push `validate` lane works; the simpler-shape question was examined and the full lint gate is the right shape with rule 8 shared; I1's EXT-marker survival and all cited test pins verified.

**Verdict after fixes:** the plan is executable as amended. The review-round findings themselves are the quality gate for this plan task: 28 findings, 0 open.
