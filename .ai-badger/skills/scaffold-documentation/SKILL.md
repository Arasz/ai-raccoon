---
name: scaffold-documentation
description: >-
  Use when a repository has no documentation tree yet, or the canonical docs layout is missing,
  incomplete or was hand-created — "set up docs", "scaffold documentation", "create the docs
  structure", a fresh repo with only a README, a docs directory missing its directory READMEs, or a
  structure check that reports absent directories. Not for adding or editing a document (use
  update-documentation), and not for reorganising docs that already exist (use
  migrate-documentation).
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [documentation, structure, diataxis, scaffolding]
    related_skills: [update-documentation, migrate-documentation]
---

# Scaffold the documentation tree

Create the canonical documentation tree and its seed READMEs, so that every later document has a
legal home before anyone has to invent one. Reference: `references/structure.md` — the tree, the
filename grammar, and the freeze list; read it **when the canonical tree is in question**. The
docs root defaults to `docs/`; read it from
`.ai-badger/config.json`'s `docs.root` if the project sets one.

**What a finished scaffold IS** — four parts, in this order:

1. The canonical tree, every directory present, none extra.
2. A root `README.md` — a **complete** map: one row per directory, no omissions.
3. One `README.md` per directory, each a complete map of that directory.
4. One ledger entry per seeded file, if the project keeps a ledger, so the next person's check is
   green from the first commit.

Seeded READMEs are meant to be edited. They carry no generated-file header and no golden test.

## Steps

1. **Inventory the current tree against the canonical one.** **Postcondition:** you have a written
   list of missing and extra paths. If the list is empty on both sides, the tree is already
   canonical — say so and stop; you are not the right skill.
2. **Create every missing directory.** **Postcondition:** each one exists and is a directory. A
   path that collided with an existing *file* is resolved one path at a time — never by deleting or
   re-running blind.
3. **Structure gate. Re-run the inventory from step 1 and do not proceed until it comes back
   empty** — no content is written before it does. Content written into a wrong tree has to be
   moved, and moving it costs the file's history on every file.
4. **Write the root README and each directory README as complete maps.** This is judgement — naming
   a directory's purpose is not scriptable. **Postcondition:** every **file** in each governed
   directory appears in its parent README, and every relative link in what you wrote resolves to a
   path that exists. Check both by hand; a missing entry is a document nobody will find.
5. **Record the seeded files** in the project's documentation ledger, if it has one.
   **Postcondition:** every seeded file has an entry, and the ledger's own consistency check is
   green. An unrecorded seed makes the very next person's check fail.

## Scripts versus judgement

A step whose output is checkable is a script call, where the project has a script. A step needing
judgement is prose followed by a check of its postcondition.

**Never script:** what a directory is for. A generated purpose line reads like a decision and is
not one, and nobody re-opens it.

Do not put a script inside this skill directory. Documentation tooling, if a project has any,
belongs in the project's scripts directory and is bound through `.ai-badger/config.json`'s `docs`
map — see `extensions/ledger/`.

## Where reference material lives

**A skills directory registers exactly one nesting level, and a directory without a `SKILL.md` is
silently ignored rather than erroring.** Verified across the agents ai-badger supports. So
**when placing a skill's reference material**, put it in a `references/` subdirectory *inside*
that skill, which ships
with it — never in a sibling directory beside it, which would never be indexed and would never be
delivered. The same rule applies to the documentation tree you are creating: nothing is picked up
because it is nearby.

## Gotchas

No environment-specific gotchas known.

## Red flags — STOP

- Writing any document before the structure inventory comes back empty
- Adding a directory the canonical tree does not have, "because this repo needs it"
- A directory README that lists the interesting files instead of all of them
- Putting a script inside this skill directory
- Creating or touching content under a frozen build-input directory — read the freeze list in
  `references/structure.md` when a build-input path is in question
- Reporting the scaffold done with seeded files the ledger does not know about
