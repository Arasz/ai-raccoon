---
name: documentation
description: "Use when a task concerns the project's documentation tree itself — creating it where none exists, updating or amending documents in it, or migrating/reorganising an existing docs layout — and you must pick which specialized documentation skill covers it. This gateway routes; read manifest.json to choose a member and open that member's SKILL.md. Not for writing prose inside a document that already has a home."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: optIn
metadata:
  hermes:
    tags: [documentation, routing, gateway]
    related_skills: []
---

# documentation

One registered entry point for three specialized documentation skills. The members are not
registered skills — they live one nesting level below registration, where agents never look on
their own. This file is the router; `manifest.json` is the machine-readable map.

## How to route

1. Match the task against the table below (or the member `triggers` in `manifest.json`).
2. Open exactly one member's `SKILL.md` — `manifest.json` gives each member's `paths.skill`.
3. Do the work from the member. If no member matches, say so; do not improvise from this page.
4. Read another member's references **only when** the member you opened cites them by path —
   the three share reference material and travel together by design.

| Member | Open it when |
|---|---|
| [scaffold-documentation](references/scaffold-documentation/SKILL.md) | Use when a repository has no documentation tree yet, or the canonical docs layout is missing or was hand-created. |
| [update-documentation](references/update-documentation/SKILL.md) | Use when adding to or changing documents in an existing canonical docs tree. |
| [migrate-documentation](references/migrate-documentation/SKILL.md) | Use when reorganising or moving docs that already exist into the canonical layout. |

Each row names its member's SKILL.md under `references/` — open one only when its "Use when"
column matches the task, so a single member loads instead of all three.

## Gotchas

No environment-specific gotchas known.
