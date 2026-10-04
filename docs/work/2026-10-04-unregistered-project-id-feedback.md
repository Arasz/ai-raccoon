# Refinement feedback — Refusing unregistered project ids: six decisions

<!-- refinement-form: refinement:2026-10-04-air-refuse-unregistered-project-id:v1 · saved 2026-10-04T12:03:43.891Z · answered 6/6 -->

Source document: `docs/work/2026-10-04-unregistered-project-id-research.md`

| Id | Decision | Verdict | Notes |
|---|---|---|---|
| D0 | High-effort loop | APPROVE | — |
| D1 | Every MCP tool that takes a projectId refuses an unregistered id, reads included | APPROVE | — |
| D2 | Dropped ids are refused on reads too | APPROVE | — |
| D3 | CLI/HTTP settings writers refuse unregistered ids; sync pull and existing watches become a follow-up issue | APPROVE | — |
| D4 | `project id register <guid> [--name]`; ai-badger's scaffold and den-refresh call it | APPROVE | "ai-badger already started working on this". The ai-badger side is handled in that repo; this PR ships the ai-raccoon verb only |
| D5 | Ships as 1.57.0 with a Breaking changes README line | APPROVE | — |

## Follow-up feedback (2026-10-04, chat)

- **F1:** "ensure cases are handled: ai-raccoon without ai-badger, so the ai-raccoon needs tool that will return id and ai-badger needs to check if the id exists". The resulting design:
  - MCP `project_id_get(name)`, used before `project_id_token_get`
  - CLI `project id get --name <repo>`
  - CLI `project id check <guid>`
  - ai-badger runs get/check before register

Not answered: none.

<!-- end refinement feedback -->
