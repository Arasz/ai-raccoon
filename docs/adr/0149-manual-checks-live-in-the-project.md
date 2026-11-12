# ADR-0149: Manual checks live in the project, not in the framework skill

Date: 2026-11-12

Status: Accepted.

## Context

The ai-badger framework shipped `ai-raccoon-manual-checklist`, a skill that held AiRaccoon's 44 manual checks, its fact script and its poison embedding stub. A skill that names one project's checks is not framework material: every release of the framework had to carry AiRaccoon's checklist, and a check change needed a framework release.

Framework ADR-0050 replaces it with a generic `run-manual-checklist` skill. The skill reads the checks from the project's own `docs/checklist/checks.json`, and a project that has no such file has no checks.

## Decision

**The checks live in `docs/checklist/checks.json`.** The file holds the rules, the facts and the items (one `expected-result` each, with an optional `anchor` to the ADR or how-to that states the expectation). The framework skill `run-manual-checklist` runs them.

**The two helper scripts are project scripts.** `scripts/checklist-derive-facts.py` derives the version, the MCP tool count and the MCP prompt count from the tree; the `facts` block in `checks.json` calls it. `scripts/checklist-poison-embedding-stub.py` is the embedding engine the poison-row item starts. Their tests sit in `scripts/tests/` and join the stdlib list in `build.yml`.

**Results stay in `docs/work/checklist/`.** The location does not change.

## Consequences

- **Positive.** A new or changed check is one edit in this repository, reviewed with the code that makes it necessary.
- **Positive.** The framework carries no AiRaccoon knowledge in this area.
- **Negative.** This repository owns two more scripts and their tests.
- **Neutral.** The old skill copy in `.ai-badger/` stays until the next re-scaffold removes it.
