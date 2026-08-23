---
name: feed-badger
description: >-
  Use when something learned in this repo belongs in the ai-badger framework itself — a new
  skill, persona, invariant, instruction or fix that is project-agnostic — and the user wants to
  contribute it back. Opens a draft PR against the framework; refuses anything project-specific.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [contribution, framework, pr, catalog]
    related_skills: [welcome-ai-badger, den-refresh]
---

# feed-badger

The reverse of `welcome-ai-badger`: it finds framework-managed content you have added or
changed in a project, keeps only what is genuinely reusable, generalizes it, and contributes it
back to `ai-badger` as a **draft PR** for human review.

## Responsibility split

- **Scripts (mechanical):** `detect_additions.py` diffs `.ai-badger/` against `manifest.json`
  and lists candidates; `open_pr.py` does the git branch/commit/push + `gh pr create --draft`.
- **You (creative):** classify each candidate (agnostic / generalizable / project-specific),
  drop project-specific ones with a reason, **generalize** the keepers (strip project paths,
  domain terms, repo names), and **place** each into the correct `{stack}/{feature}/` path in an
  ai-badger checkout.

## Flow

1. **Detect candidates** (from the target repo root):
   ```bash
   python3 "$AI_BADGER/features/common/skills/feed-badger/scripts/detect_additions.py" --target . --root "$AI_BADGER"
   ```
   Emits `new` (files not from the framework) and `changed` (files edited beyond the scaffold)
   candidates, by feature.

2. **Classify & generalize.** For each candidate decide agnostic / generalizable /
   project-specific. Drop project-specific ones (state why). For the keepers, rewrite to remove
   anything project-coupled — no repo names, no domain nouns, no absolute paths. Decide the
   target stack (or `common`) and feature. A brand-new stack or feature is allowed.

3. **Place into an ai-badger checkout.** Clone or reuse a checkout of `Arasz/ai-badger`, write
   each generalized file to its `{stack}/{feature}/` path, then regenerate the index:
   ```bash
   python3 "<checkout>/tooling/index_build.py"
   python3 "<checkout>/tooling/validate.py" --all
   ```

4. **Open a draft PR.** Write a PR body summarizing each contribution and why it is agnostic,
   then:
   ```bash
   python3 "$AI_BADGER/features/common/skills/feed-badger/scripts/open_pr.py" \
     --checkout <checkout> --branch feed/<slug> \
     --title "feed: <summary>" --body-file <body.md> --repo Arasz/ai-badger \
     --path features/<stack>/<feature>/<name> --path index.json
   ```
   `--path` is **required and repeatable**: name every path you placed, plus `index.json` if you regenerated it. Only declared paths are staged, so an unrelated dirty file in the checkout cannot ride along in the PR.

   Every declared path is scanned for credential-shaped literals before anything is staged. A finding refuses the PR and names the file and the shape — never the matched text. It is a guard, not proof: it checks known literal shapes, so a
   clean run is not a certificate.

   Use `--dry-run` to preview the git/gh commands without executing (useful for testing).

## Rules

- **Draft, always.** Contributions land as draft PRs; a human reviews and merges. Never
  auto-merge.
- **Agnostic bar is high.** When unsure whether something is reusable, keep it in the project,
  not the framework. Better to under-contribute than to pollute the catalog.
- **Provenance drives detection.** `feed-badger` only works on repos scaffolded by ai-badger
  (those with `.ai-badger/manifest.json`).

## Gotchas

- **Draft PR, always.** A human reviews and merges; never auto-merge.
- **`--path` is required and repeatable.** Only declared paths are staged, so an unrelated dirty file cannot ride along in the PR.
- **The credential scan is a guard, not proof.** It checks known literal shapes; a clean run is not a certificate.
- **The agnostic bar is high.** When unsure, keep it in the project, not the framework.

## Error Recovery

When any script in the feed flow (`detect_additions.py`, `open_pr.py`) exits non-zero or emits an error, attempt recovery before surfacing the failure.

1. **Parse the error.** Scripts emit structured JSON — read the `error` field to classify the failure.

2. **Attempt automatic recovery.** Try the applicable fix, then re-run the failed step.

   | Error | Fix |
      |---|---|
   | `manifest.json` missing or corrupt | Project not scaffolded — run `welcome-ai-badger` first |
   | `detect_additions.py` found no candidates | Confirm `.ai-badger/` has changes beyond manifest; check git status |
   | `open_pr.py` — `gh` not authenticated | `gh auth login` or set `GITHUB_TOKEN` |
   | `open_pr.py` — branch already exists | Delete the remote branch (`git push origin --delete <branch>`) or use a new slug |
   | `open_pr.py` — push rejected | Pull latest, rebase, force-push (draft branch only) |
   | `index_build.py` / `validate.py` error after placing files | Fix the placed files, re-run index build + validate |

   After applying a fix, **re-run the failed step** and continue the flow. If it succeeds, report what was fixed.

3. **Recovery failed — offer to create a GitHub issue.** Follow
   `.ai-badger/skills/welcome-ai-badger/references/reporting-a-framework-bug.md` **when a fix does not recover the failure**: ask permission first, gate on `gh` being installed and authenticated, sanitize the config before including it.
   **Never create the issue without explicit user approval** — that rule holds even if the reference file is not present.

## Verification Checklist

- [ ] `detect_additions.py` ran and every candidate was classified — dropped project-specific ones have stated reasons
- [ ] Every keeper generalized — no repo names, domain nouns, or absolute paths
- [ ] Placed files pass `index_build.py` + `validate.py --all` in the checkout
- [ ] Draft PR opened with `--path` naming every placed path (and `index.json` if regenerated)
- [ ] Credential scan clean
