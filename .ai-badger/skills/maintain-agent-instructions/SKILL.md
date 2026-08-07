---
name: maintain-agent-instructions
description: >-
  Use when agent instruction files have drifted from each other or from the policy model —
  CLAUDE.md, copilot-instructions.md, AGENTS.md, hosted-review and path-scoped instruction files
  — or when validation/drift checks fail in CI. Reconciles them from the machine-readable model
  in .ai-badger/agent-instructions/.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [agent-instructions, drift, claude, copilot]
    related_skills: [welcome-ai-badger, update-documentation]
---

# Maintain agent instructions

This skill keeps agent guidance consistent while minimizing always-loaded context. It follows a
hub-and-spoke model: one compact, always-loaded entrypoint per agent (`CLAUDE.md`,
`.github/copilot-instructions.md`, …) plus detailed, path-scoped or on-demand
instruction files. The model is machine-readable so drift between agent files can be checked by
script instead of by eye — if the project records this decision as an ADR, link it here.

## When NOT to Use

- A single-file typo fix in one instruction file — edit it directly
- No drift exists and CI checks pass
- Authoring brand-new policy from scratch — that is content work, not reconciliation

## Principles

- Use scripts first; inspect only failing files/rules.
- Keep each agent's always-loaded entrypoint compact. Put detailed guidance in scoped or
  on-demand files.
- Update the agent-instructions model (`.ai-badger/agent-instructions/model.json` by default; see
  `references/agent-instruction-model.md`) **when** changing shared policy.
- Treat `.github/instructions/*.instructions.md` (or the project's equivalent) as the shared
  path-scoped implementation rule source.
- Do not rewrite every agent file just to rephrase. Make the smallest consistency-preserving edit.
- The scripts are check-only by default; the agent handles semantic policy decisions and wording.

## Standard workflow

1. Run validation:

   ```bash
   node .ai-badger/skills/maintain-agent-instructions/scripts/validate-agent-instructions.mjs
   ```

2. Run drift detection:

   ```bash
   node .ai-badger/skills/maintain-agent-instructions/scripts/check-agent-drift.mjs
   ```

   Both scripts are `#!/usr/bin/env node` and import only `node:fs` / `node:path` — Node 18+,
   no bundler, no dependencies. Run them from the project root; they read `process.cwd()` and
   `AGENT_INSTRUCTIONS_DIR` (default `.ai-badger/agent-instructions`). Adjust the path prefix
   if the skill was scaffolded elsewhere.

3. If both pass, report success.
4. If either fails:
   - inspect only the reported files and rules,
   - update the model if the policy changed,
   - update the smallest affected instruction file(s),
   - rerun both scripts.
5. If the change modifies architecture/process policy, add or update an ADR (if the project keeps
   one).

## Copilot compatibility

GitHub Copilot CLI and Copilot coding agent discover repository instructions in standard files,
including `.github/copilot-instructions.md`, `.github/instructions/**/*.instructions.md`,
`AGENTS.md`, and `CLAUDE.md`. Keep Copilot-compatible rules in `.github/copilot-instructions.md`
and `.github/instructions/*`; the validation scripts are plain command-line checks so
Copilot-driven automation or CI can run the same checks.

## Script style

Scripts are small deterministic helpers. They should:

- avoid LLM calls,
- avoid network calls,
- read the agent-instructions model (path resolved via `AGENT_INSTRUCTIONS_DIR`, default
  `.ai-badger/agent-instructions`; read `references/agent-instruction-model.md` **when writing a
  script that reads the model**),
- report precise file/rule failures,
- exit non-zero on errors,
- keep warnings separate from errors,
- avoid editing files unless a future explicit `--write` mode is added.

Read `references/agent-instruction-model.md` **when the model contract is in question** and
`references/copilot-compatibility.md` **when phrasing a Copilot-specific rule**.

## Gotchas

No environment-specific gotchas known.

## Verification Checklist

- [ ] Both scripts ran from the project root
- [ ] Both exit 0 — or every reported failure was fixed and the re-runs pass
- [ ] Only the reported files and rules were touched
- [ ] The model was updated before any shared-policy change
- [ ] ADR added or updated when architecture/process policy changed
