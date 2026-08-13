---
name: ai-raccoon-state-checklist
description: Use when running AiRaccoon pre-flight state evaluation.
---

# AiRaccoon State Pre-Flight Checklist

Use this skill when performing a complete pre-flight state evaluation of AiRaccoon features and operational status.

## Process

1. Load `templates/checklist-template.json` from this skill.
2. For each step in the checklist, execute the corresponding evaluation or test.
3. Fill out the observed results, check status (`checked: true`), evaluation (`accepted: true/false`), and acceptation reason.
4. Write the filled-out checklist resource to `.ai-raccoon/state-checklist-<timestamp>.json` in the current project or workspace.

## Checklist Features Covered

- **Build & Global Package**: Build local `main`, pack, and force update local `ai-raccoon` global tool package.
- **General Workflow - Memory Write**: Write durable memory entries using `memory_write` / MCP tools.
- **General Workflow - Memory Read**: Query memory entries using `memory_search` / hybrid search.
- **File Watch**: Check live file watch registrations and status via `memory_watch_status`.
- **Promotion Queue**: Inspect promotion queue state and pending candidates via `memory_promotion_list`.
- **Semantica Integration - Graph Import**: Verify graph import state and `.ai-raccoon/semantica-graph.json`.
- **Semantica Integration - JSON Chunking**: Verify structured entity and relation chunking in Semantica memory.
- **Semantica Integration - Full Decisions Retrieval**: Test retrieval of full decision records / ADR details.
- **Semantica Integration - High-Value Information Analysis**: Assess key architectural insights extracted from Semantica graph memory.
- **Prometheus Auto-Grader**: Audit Prometheus auto-grader state, bridge port 7721, and live auto-grading.
