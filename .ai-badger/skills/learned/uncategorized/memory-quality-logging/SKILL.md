---
name: memory-quality-logging
description: REMOVED 2026-08-11 — file-based memory_search grading is gone; see docs/plans/2026-08-11-search-quality-metric-plan.md for the replacement.
version: 2.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [memory, ai-raccoon, dogfooding, jsonl, grading, telemetry]
    related_skills: [ai-raccoon-integration, hermes-session-telemetry, ingest-docs-to-memory]
---

# Memory-quality logging — REMOVED (2026-08-11)

The file-based grading workflow this skill documented is **removed** (task
mem-cleanup, owner decision): `AI_BADGER_MEMORY_GRADE` → `memory_grade.py` →
`~/.ai-badger/memory-grade/memory-quality.jsonl` (+ `pending.json`) is gone from
ai-badger (PR #373) and the ai-raccoon provider's `memory-operations.jsonl` writer is
gone too (ai-raccoon PR #259). The env var is inert; `memory_grade.py` no longer exists
in the plugin or the framework.

**Do not** run the old recipe (grade asks, `memory_grade.py grade <ts> <1-5>`,
`scripts/audit_coverage.py` — its log inputs are no longer written). The historical
files may still exist on the machine (`~/.ai-badger/memory-grade/`, backups in
`/tmp/mem-cleanup/`) but nothing writes them.

**The replacement approach** is the server-side quality table in
`docs/plans/2026-08-11-search-quality-metric-plan.md` (ai-raccoon repo): 100 % search
capture + follow-through measurement, no JSONL files. Until it ships, grade quality
manually in-session (rated searches noted in the session report) — no file logging.
