# 0119 — A poison memory row is isolated from its batch and abandoned after three attempts

Date: 2026-09-27

Status: Accepted

## Context

`EntryEmbedder.EmbedAsync` embedded pending memory rows in sub-batches of 32: one generator call
for the rows, one for their distinct headings, then every `MarkEmbedded` inside one
`BEGIN IMMEDIATE`/`COMMIT`. Any exception in that sequence left the method. The only catch blocks
in the class were the transaction's own `ROLLBACK` and the migration relay's
report-and-rethrow; none of them isolated a row. The code corpus has had that isolation since
#466 (`CodeEmbedder.EmbedSliceAsync`, `code_entries.embed_attempts`, `CodeCorpusSchema.MaxEmbedAttempts`).
The memory corpus never got it.

`EntryEmbedderPoisonRowTests` puts one row whose content makes the generator throw among
healthy rows. On main at 1.53.1, six of its eight scenarios failed with the poison row's own
exception, `simulated poison row: this content can never embed`, thrown out of
`EntryEmbedder.EmbedAsync` (`EntryEmbedder.cs:361`):

- **The whole batch stays pending.** The healthy rows beside the poison row are never embedded.
  `SelectAllPendingForEmbed` orders by id, so the same batch comes back on every drain pass and
  fails the same way, and `HasPendingEmbed` keeps the 15 s on-demand poll awake for it forever.
- **`memory_embed_pending` never succeeds** for the project. `EmbedPendingAsync` selects the
  same batch and throws.
- **A model migration never finishes.** `DrainMigrationAsync` loops until the pending selection
  is empty. It never is, so the migration row stays open and the bank stays ToolGate-locked.
- **The structure heal behind `memory_embed_pending`** has the same shape. One heading that cannot
  embed fails every call for the project (`StructureHeal_OnePoisonHeading_HealsTheOthersAndStopsRetryingIt`,
  red on this branch before its fix).

## Decision

**Mirror `CodeEmbedder`: fall back to one row at a time, count failures per row on the row, and
stop selecting a row at a ceiling of three.** Three details differ from the code corpus, each
for a reason the memory corpus has and the code corpus does not.

- **Isolation.** A sub-batch that fails as a whole is rolled back and retried one row at a time,
  each row with its own generator call and its own one-row transaction (event 442, Debug). The
  healthy rows embed on that pass.
- **The attempts column.** `entries.embed_attempts INTEGER NOT NULL DEFAULT 0` is added the same
  way `code_entries.embed_attempts` was. There is no ladder step. `MemorySchema`'s tolerant column
  ensure now takes the table name and runs on a digest mismatch. For `entries` it also runs once
  more after the v17 rebuild, because that rebuild copies a fixed column list and would drop the
  column from a bank upgrading from v16. The Ddl block's comment names the column, which changes
  the digest, so every existing bank gets the column on its next open.
- **The ceiling.** A row that fails on its own is charged one attempt (event 443, Warning, with
  the exception). At three (`EntryEmbedder.MaxEmbedAttempts`) it is abandoned (event 444, Error,
  naming the row id and its source file or path). `SelectPendingForEmbed`, `SelectAllPendingForEmbed`
  and `HasPendingEmbed` carry `embed_attempts < 3` as a literal, pinned to the constant by
  `PendingSelections_CarryTheCeilingAsTheirLiteral`. The drain, `memory_embed_pending` and the
  migration relay all skip an abandoned row, and the poll stops waking for it.
- **An outage charges no row (differs from the code corpus).** The memory engine can be a remote
  endpoint, and a network outage makes every call fail. Charging each row for that would abandon
  a whole healthy backlog after three polls. So the first row failure in a fallback probes the
  engine with a fixed text. If the probe fails too, the pass fails with the row's exception and
  nothing is charged, which is what happened before this change. If the probe answers, the row
  is at fault and is charged.
- **A busy bank charges no row.** `SQLITE_BUSY` and `SQLITE_LOCKED` (another writer holding the
  lock past the busy timeout) propagate without the fallback, as they did before.
- **A model switch resets every row's attempts.** `StartMigrationAsync` runs `ResetEmbedAttempts`
  in the same transaction as `MarkAllEmbeddedPending`. A new engine may embed what the old one
  could not, and without the reset an abandoned row would miss the migration.
- **Pending counts include abandoned rows.** `memory_stats.pending`, `PendingCount` and
  `CountPendingEmbed` still count them. They are not embedded, and a number that quietly dropped
  them would claim the bank is fully searchable when it is not. Event 444 is where the operator
  finds out why the count does not reach zero.
- **The structure heal gives up on a heading, not the row.** A heading that fails alone while the
  engine answers gets the `''` sentinel. That takes the row out of the heal set while it keeps its
  content vector (event 445, Warning). An outage still fails the call and stamps nothing.

A simpler shape was considered: isolation with no ceiling, and no schema change. It does not
terminate. The poison row stays pending, so `DrainMigrationAsync` and an unlimited
`memory_embed_pending` would loop on it forever instead of throwing. Keeping the count in memory
would reset on every restart and still needs a per-call exclusion set. The column is the smallest
thing that terminates, and it is the shape the code corpus already runs.

## Consequences

- **Positive.** One bad memory row now costs at most three rounds of one-row generator calls and
  then nothing. Its batch neighbours embed on the first pass, `memory_embed_pending` returns, and
  a migration finishes with the row left pending and named in the log.
- **Positive.** A bank that was already wedged recovers on upgrade with no manual step. Its
  pending rows start at zero attempts, the first drain embeds the healthy ones, and the poison row
  is abandoned after three passes (`ExistingBankWedgedBeforeUpgrade_RecoversItsGoodRowsAndAbandonsThePoisonRow`).
- **Negative.** An abandoned row is not retried until its content changes (a rewrite or re-ingest
  inserts a new row with zero attempts) or the embedding model changes. A transient failure that
  hits one row three times in a row, while the engine answers the probe each time, abandons that
  row. That is the same trade the code corpus made.
- **Negative.** A failing batch costs up to 32 extra one-row calls plus one probe on the pass it
  fails. An engine outage costs one batch call, one row call and one probe per pass before the pass
  fails.
- **Neutral.** The inline embed on `memory_write` and `memory_add_content` is unchanged. It still
  reports a generator failure to the caller, and the stored row stays pending for the drain, which
  now isolates it.
