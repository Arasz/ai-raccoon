# ADR-0148: The bank guards "only memory rows carry a TTL" and "entry_type is a known kind" with triggers

Date: 2026-11-12

Status: Accepted for 2.9.0.

## Context

Since 2.1.0 only a Memory row carries a TTL. Four code paths enforce the rule each on their own: the file ingest reader, the chunk budget reconciler, the sync merge (a `CASE` on the merged kind) and `UpdateEntryTtl`. The bank itself holds none of it. A new write path that forgets the rule stores a File row with a TTL, and the forgetting sweep then deletes a document the ingest would have re-created with no TTL.

The kind vocabulary has the same gap. `entry_type` is declared `CHECK(entry_type IN (...))` on a fresh bank and by the `ALTER TABLE ADD COLUMN` that adds it to a legacy bank. A development bank created while the column shipped as nullable and unchecked has neither, so `INSERT ... entry_type = 'bogus'` succeeds there and no typed query can place the row.

Issue #917 items 1 and 6 ask the bank to hold both rules, on every bank, old and new. The owner ruled on two decisions in the 2026-11-12 refinement form: D1 (repair the existing rows by clearing the TTL) and D2 (use a trigger, not a CHECK). Both are approved.

## Decision

**A BEFORE INSERT trigger and a BEFORE UPDATE OF `ttl_days, entry_type` trigger guard `entries`.** Each raises `ABORT` with a message naming the rule when:

- `NEW.ttl_days IS NOT NULL AND NEW.entry_type <> 'memory'`, or
- `NEW.entry_type IS NULL OR NEW.entry_type NOT IN (<known kinds>)`.

The kind list is `MemorySchema.EntryKindList`, built from `EntryKindWireNames`. It is the list the column CHECK already uses, so there is one source. `none` stays a known kind: a write path that omits the column lands `none`, and the doctor counts those rows.

**A trigger, not a CHECK (D2).** SQLite cannot add a CHECK to an existing table; it needs the full rebuild of `entries` that the v17 step already does once, with its FTS, vec0 and trigger re-creation. A trigger needs no rebuild, fits a bank of any age, and also covers the development banks that lack the column CHECK (item 6).

**Schema v24 installs the triggers and repairs the rows first (D1).** The step runs `UPDATE entries SET ttl_days = NULL WHERE ttl_days IS NOT NULL AND entry_type <> 'memory'` and then creates the triggers. Pre-2.1.0 File rows may carry a TTL on purpose, and the sweep still honours it; the owner approved clearing them.

**The triggers are not part of `Ddl`.** `Ddl` runs before the migration ladder, and the v20 and v21 backfills run `UPDATE ... entry_type` on legacy rows that may carry a TTL. A trigger in `Ddl` would abort those migrations. This follows the bucket-index precedent: a fresh bank creates the triggers directly after `Ddl`, a legacy bank gets them from the v24 step.

**The doctor reports the rule.** The object inventory already names a missing trigger, because the expected bank is a fresh one. The kind counts gain one number: rows with a TTL that are not memory.

The code-side enforcement stays in this change. Removing the redundant copies is a later step.

## Consequences

- **Positive.** A write path that breaks either rule fails loudly at the statement, on every bank.
- **Positive.** A development bank without the column CHECK gets the domain guard.
- **Negative.** The v24 step clears the TTL of every non-memory row once; the count is reported in the pull request.
- **Negative.** Two triggers run on every insert and on updates of the two columns. They read only `NEW`, so the cost is a comparison per row.
- **Neutral.** The schema digest does not change, because the triggers are outside `Ddl`.

## Verification

Tests assert that inserting a File row with a TTL throws; that a v23 bank with a TTL on a File row and on a Memory row opens at v24 with the first cleared and the second kept; that the full ladder from the oldest legacy fixture with a TTL on a File row stays healthy; that on a bank whose `entry_type` has no CHECK, `entry_type = 'bogus'` throws; and that the doctor reports the new count and a missing guard trigger. Each test fails when its clause is removed.
