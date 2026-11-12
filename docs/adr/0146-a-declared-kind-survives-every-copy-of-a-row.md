# ADR-0146: A declared kind survives every copy of a row

Date: 2026-11-12

Status: Accepted for 2.9.0.

## Context

A row's kind (`entry_type`: `memory` or `file`) is declared by the path that wrote it (ADR-0141 types the vector partitions by it, and the query path filters on it). Two copy paths ignore that declaration.

- **Workspace consolidation.** `WorkspaceService.ConsolidateAsync` lists the outbox, which already returns each row's kind, and promotes each kept row through `AddContentAsync`.
- **`share_memory`.** `SqliteMemoryStore.ShareAsync` reads the source row and writes the shared copy through `AddContentAsync`. Its source select carried no kind.

`AddContentAsync` hard-coded `EntryKind.Memory`, so every copy of a `file` row became a `memory` row. The copy then left the file kind's filters, and a later reingest of the file no longer found the row it should replace. Issue #917 item 5 reported this; owner ruling D6 settled the direction.

## Decision

**The kind is a required argument of `AddContentAsync`.** The method has no default and no overload without it. Every caller states the kind it is writing.

- Consolidation passes the kind the outbox row declares.
- `ShareAsync` reads `entry_type` from the source row (`SelectSourceByHashAndProject` now selects it) and writes it.
- The `memory_add_content` write of agent notes passes `Memory`.

**An undeclared kind is resolved from the row's shape.** A source row whose kind is `none`, null or a value this build does not know passes `EntryKind.None`. `AddContentAsync` then resolves it with the classifier's shape rule (`Classify`: a source file equal to the path is a `file`, anything else is a `memory`), the same rule `ResolveStoredValue` applies to undeclared rows elsewhere. A declared kind is never re-derived.

**No copy writes a TTL.** `AddContentAsync` inserts through `InsertEntry`, which has no `ttl_days` column, so a copy carries no TTL whatever its kind. A TTL is a `memory` row's forgetting knob (`set_memory_ttl` already refuses other kinds); a copied `file` row must not gain one. A test pins that a copied `file` row has none.

**Scope.** This record covers consolidation and share. Re-ingest keeps a row's kind in a later change, which removes `preserveMemoryMetadata` and will amend this record.

**Rejected alternatives.**

- *An optional `entryKind` parameter defaulting to `Memory`.* It keeps every old call site compiling, which is the defect: a caller that forgets the kind silently writes a memory.
- *Copy the whole source row, including TTL, rating and counters.* A copy into a new tier is a new row with its own history; only the declared kind is identity, not usage.

## Consequences

- **Positive.** A `file` row stays a `file` through consolidation and share, so kind filters and reingest see the row the same way before and after.
- **Positive.** A forgotten kind is a compile error, not a wrong row.
- **Negative.** `IMemoryStore.AddContentAsync` changes signature; every implementer and caller is updated in the same change.
- **Neutral.** A legacy `none` row resolves by shape, so its copy may be classified differently from the source row until the backfill declares the source.

## Verification

`EntryTypeWriteTests` — `AddContentAsync` writes the kind it is given, `ShareAsync` of a `file` row yields a shared `file` row without a TTL. `WorkspaceServiceTests` — consolidation passes each row's declared kind. With `Memory` hard-coded in `AddContentAsync`, or the selected `entry_type` ignored in `ShareAsync`, these tests fail.
