# adr/

Architecture decision records: immutable, frozen. Each file records one decision in date
order (`NNNN-slug.md`); records are never edited after acceptance — a new decision gets a
new number. Add an ADR for any architecture-level decision via `create-task-spec` /
`owner-gate-review` workflow.

## Contents

| ADR | Decision |
|-----|----------|
| [0001 — FluentValidation in the core](0001-fluentvalidation-in-core.md) | Domain validation via FluentValidation |
| [0002 — OpenTelemetry observability](0002-opentelemetry-observability.md) | BCL-only Meter/ActivitySource on all MCP tools |
| [0003 — Source file as first-class citizen](0003-source-file-first-class-citizen.md) | `source_file` column + weighted FTS source index + source identity on results |
