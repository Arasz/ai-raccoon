---
applyTo: 'docs/**/*.md,README.md,CLAUDE.md'
description: 'Documentation and specification maintenance rules.'
---

# Documentation

- Treat this project's requirements, functional-specification, architecture, data-model, and flow docs (whatever they're named here) as the authoritative specification.
- Update every affected specification document in the same change as a behavior change. Add an ADR for an architecture-level decision.
- Keep review-priority docs and agent-instruction docs consistent when changing shared review policy.
- Keep every agent-facing instruction file (CLAUDE.md-equivalent, Copilot/other agent instructions, scoped path instructions) consistent when changing shared agent policy — use a single machine-readable model as the source of truth if one exists in this project, rather than hand-editing each file independently.
- Do not include personal data, credentials, connection strings, or other secrets in examples or fixtures.
- Link to repository-relative documentation where context is needed; keep instructions self-contained rather than requiring reviewers to follow external links.

## Humanization (anti-AI-tell writing)

Documentation should read as human-written prose, not LLM output. Apply these rules:

- **Vary sentence lengths** — mix short punchy sentences (3-6 words) with longer flowing ones (20-30 words). Avoid uniform 15-22 word sentences.
- **Purge AI vocabulary** — never use: `delve`, `leverage`, `utilize`, `robust`, `comprehensive`, `streamline`, `foster`, `facilitate`, `pivotal`, `nuanced`, `multifaceted`, `crucial`, `enduring`, `garner`, `valuable`, `vibrant`, `tapestry`, `testament`, `underscores`, `highlights`.
- **Avoid em dashes** — limit to at most 1 per 1000 words. Use parentheses or commas instead.
- **No colon-header lists in prose** — convert `**Header:** Content` patterns to flowing sentences in narrative sections.
- **Active voice preferred** — swap passive constructions (`serves as`, `stands as`, `boasts`) with direct verbs (`is`, `has`).
- **Take a stance** — express a clear position or acknowledge genuine uncertainty rather than remaining sterile and neutral.
