---
applyTo: 'docs/**/*.md,README.md,CLAUDE.md'
description: 'Documentation and specification maintenance rules.'
---

# Documentation

- Treat this project's requirements, functional-specification, architecture, data-model, and flow docs (whatever they're named here) as the authoritative specification.
- Update every affected specification document in the same change as a behavior change. Add an ADR for an architecture-level decision.
- Keep review-priority docs and agent-instruction docs consistent when changing shared review policy.
- Keep every agent-facing instruction file (CLAUDE.md-equivalent, Copilot/Junie/other agent instructions, scoped path instructions) consistent when changing shared agent policy — use a single machine-readable model as the source of truth if one exists in this project, rather than hand-editing each file independently.
- Do not include personal data, credentials, connection strings, or other secrets in examples or fixtures.
- Link to repository-relative documentation where context is needed; keep instructions self-contained rather than requiring reviewers to follow external links.
