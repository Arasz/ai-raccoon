# ADR-0155: An idle catch-up settles Healthy, a notebook string reads a lone surrogate as U+FFFD, and a bank with no engine skips the drain

Date: 2026-11-20

Status: Accepted for 2.10.4.

Amends: [0154](0154-the-ignore-file-keeps-a-real-fingerprint-and-a-lone-surrogate-reads-as-u-fffd.md) (decision 2 also covers notebook cell text).

## Context

The 2.10.3 manual checklist run (`docs/work/checklist/2026-11-20-2-10-3-latest-versions.json`) found three defects.

**Notebook surrogate (#970).** ADR-0154 made the tokenizer read an unpaired surrogate as U+FFFD. A notebook cell
fails earlier: `NotebookChunker.Source` calls `JsonElement.GetString()`, and `System.Text.Json` throws
`InvalidOperationException` for an escape such as `\uDE00` or a `\uD83D` with no low half. The digest fails and the
whole watch goes to `Retrying`.

**Idle watch stays Scanning (#971).** `RegisterWatch` starts a watch in `Scanning` with no `LastSync`. Only a finished
digest job (`WatchPipeline.RunJobAsync`) moves it to `Healthy`. A catch-up scan that finds no due file and nothing to
reconcile enqueues no job, so after every restart an unchanged watch reads `Scanning` / `lastSync: never` until its next
file event.

**Drain with no engine (#973).** `EntryEmbedder.EmbedPendingBatchAsync` calls `CreateGenerator` without checking that a
memory engine is set. On a bank with no engine every drain pass throws, and `EmbedDrainService` logs EventId 1005 at
Warning with a full stack. `EmbedIfConfiguredAsync`, `EmbedQueryAsync` and `CodeEmbedder.EmbedPendingBatchAsync` already
treat a missing engine as a normal state.

## Decision

1. **Notebook strings read a lone surrogate as U+FFFD.** When `GetString()` refuses a cell's string, the chunker
   re-reads the raw JSON literal with each unpaired `\uD800`–`\uDFFF` escape replaced by `�`. A valid pair and an
   escaped backslash stay as written. This matches ADR-0154 decision 2: the file is indexed and the watch stays healthy.
2. **A catch-up pass that queues nothing settles its watch.** When a scan pass enqueues no digest and no delete, the
   pipeline moves the watch from `Scanning` to `Healthy` with `LastSync` set to now. It changes no other state: a
   `Retrying` or `Stopped` watch keeps its state, because only a successful digest proves recovery.
3. **No memory engine, no drain.** `EntryEmbedder.EmbedPendingBatchAsync` returns 0 when the provider setting is
   empty, the same rule the code corpus uses. Rows stay pending until `model embedding set` runs.

## Alternatives

- **Skip a notebook with a lone surrogate as `unparseable`.** Rejected for the same reason ADR-0154 gave: one escape
  would hide the whole file.
- **Pre-clean the whole notebook text before `JsonDocument.Parse`.** Rejected: it rewrites every string, also
  metadata, to fix a case that only cell sources and the kernel name reach.
- **Mark the watch Healthy at the end of every scan.** Rejected: a scan that queued jobs is not synced until they run,
  and the job path already sets `Healthy`.
- **Lower EventId 1005 to Debug.** Rejected: 1005 is the real failure signal for a configured engine.

## Consequences

- `get_watch_status` and `watch status` read `Healthy` after a restart for an unchanged watch.
- A notebook with an invalid escape is indexed with U+FFFD in place of the escape.
- A new install logs no drain warning before an engine is set.
