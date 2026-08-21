# Plan — two defects found by the 1.29.0 manual checklist

**Date:** 2026-08-21
**Source:** `docs/work/checklist/2026-08-21-1.29.0-arbitrary-embedding-models.json`
**Status:** plan, for MoE review before implementation

Both were found by running a real install against a real bank; neither is visible to
`dotnet test` today. Root causes below are **measured**, not inferred — the first one
falsified my own first diagnosis, which is why it is stated with its evidence.

---

## Defect A — `serve --restart` cannot cycle a server that is still warming up

### What the checklist saw

`serve --restart` against a server started ~10 s earlier reported

> ai-raccoon: port 7788 is in use but gave the probe no answer; nothing was asked to stop

and then died with `IOException: address already in use`. Reproduced on an ephemeral
port (63461) and an explicit one (7788).

### First diagnosis, and why it was wrong

I first concluded the probe verdict contradicted the endpoint: a hand-issued POST to
`/mcp` returns `401` with a JSON-RPC body in ~0.14 s, and `ServerProbe` treats a body
containing `jsonrpc` as `Answered`. A unit test asserting exactly that shape
(`ATokenGated401CarryingAJsonRpcBody_ReportsAnswered`) **passes on the current code** —
so the verdict logic was never the problem. That test is worth keeping regardless; it
pins a shape every real server returns and nothing covered it.

### What is proven, and what is not (revised after MoE review)

**Proven — the per-attempt bound is not retryable.** The architect lane measured this
against the pinned Polly 8.6.6 with `ServerProbe`'s exact shape:

```
current shape   → threw TaskCanceledException; attempts=1
with the fix    → threw TimeoutException;      attempts=3
control (HRE)   → threw HttpRequestException;  attempts=3
```

`attemptCts.CancelAfter` surfaces as `TaskCanceledException`, which is not in the
predicate (`ResiliencePipelineFactory.cs:32-35`), so it escapes `ExecuteAsync` on attempt
one and `ServerProbe.cs:74` converts it to `Unanswered`. The probe advertises three
attempts and takes one against its most likely failure mode. `Unanswered` then maps to
`Unknown` and `RestartServer` returns `PreBind.Bind(Unknown)` **before** `CycleAsync`
(`NodeRunner.cs:68-84`) — which is why the failing restart log contains no `ServerRestart`
event at all.

**Not proven — that this caused the checklist symptom.** My first write-up blamed a
warming server (WAL checkpoint, maintenance, ONNX load). Two measurements refute it:

- `EnsureEmbeddingAvailabilityAsync` runs at `NodeRunner.cs:113`, **before**
  `serverHost.StartAsync` at `:115`. During a model load the port is not bound, so a probe
  gets `ConnectionRefused` → `NotListening` → the restart proceeds. That is a bind race,
  not `Unanswered`.
- A clean reproduction of the failing sequence — start a server, wait 10 s, `serve
  --restart` — **cycled correctly** (`28252 → 28805`). A fresh server answers the probe
  within the 1 s bound by t+2.75 s, measured.

So the retry gap is a real defect worth fixing on its own merits, and the checklist
failure remains **unexplained**. The plan no longer claims otherwise. Finding 3 below is
what makes the next occurrence diagnosable instead of inferred.

### Proposed fix (revised)

1. **Polly-native bound**: `.AddRetry(...)` outer, `.AddTimeout(perAttempt)` inner, with
   `TimeoutRejectedException` in the predicate. This deletes the hand-rolled linked CTS
   and puts the bound where the resilience policy already lives — which is what
   `ResiliencePipelineFactory`'s own doc comment already claims ("startup windows") and
   today does not deliver.
2. **Remove the second bound.** `NodeRegistration.cs:14` sets
   `client.Timeout = ServerProbe.RequestTimeout` — a *second* 1 s bound racing the first.
   When it wins it throws `TaskCanceledException` whose inner (not outer) type is
   `TimeoutException`, and Polly matches the outer type only. Set the named client to
   `Timeout.InfiniteTimeSpan` and let the pipeline own the bound. **Without this the fix
   is green in tests and still broken in the field**, because every existing test builds a
   bare `HttpClient` with the 100 s default.
3. **Catch the new type.** `TimeoutRejectedException` is *not* an
   `OperationCanceledException` (measured), so without `catch (TimeoutRejectedException)
   => Unanswered` it escapes `ProbeAsync` and takes the restart down with an unhandled
   exception — breaking two currently-green tests
   (`AListenerThatNeverAnswers_ReportsUnanswered_NotAnEmptyPort`,
   `RespondsAsync_StaysFalseForEveryVerdictButAnswered`).
4. **Make the verdict visible.** `NodeRunner.Log.ProbeUnanswered` is `Debug`
   (`NodeRunner.cs:287`) and `ServerProbe` has no logger at all, which is why this had to
   be inferred from a correlation. Add a nested `static partial class Log` to
   `ServerProbe` recording which branch produced the verdict and the attempt count, and
   raise the pre-check verdict to Information — it is the pre-check for a destructive
   operation.

Rejected: raising `RequestTimeout`. It slows every dead-port probe and leaves the retry
dead for the case it exists to handle.

Cost to state: a *filtered* port (SYN dropped, no RST) goes from 1 s to ~3.1 s per probe,
and `RestartServer` probes once then `CycleAsync` probes again — ~6 s before the operator
sees a line. Acceptable; recorded so it is a decision rather than a surprise.

**Open for review:** whether `Unanswered` should reach `CycleAsync` at all rather than
short-circuiting the pre-check. `/observability` is in `McpTokenGate.OpenPaths`, so identifying the holder is
UNAUTHENTICATED and cheap — which strengthens the case for letting `Unanswered` through
rather than refusing before trying. Out of scope for this fix, but worth a ruling.

---

## Defect B — a re-ingested file leaves its previous chunks behind

### What the checklist saw

Ingesting `sextant.md` (300 B, 1 chunk), rewriting it larger (3,823 B, 5 chunks) and
re-ingesting the same path left both sets in the bank:

```
chunk_index | total_chunks | length
0           | 1            | 300     ← stale, content no longer in the file
0..4        | 5            | 44…1336 ← current
```

Search then returns content the file no longer contains.

### Root cause

Two ingest paths, one of which forgets to clear:

- **Watch digest** — `ReplaceIfFileChangedAsync` → `ReplaceCoreAsync` deletes by source
  path (plus subtree) inside one `BEGIN IMMEDIATE`, then re-ingests and upserts the
  fingerprint.
- **Direct tool** — `SqliteMemoryStore.IngestFileAsync` opens a connection and calls
  `fileIngestor.IngestFileAsync` straight through. No delete, so chunking a file into a
  *different* number of chunks strands every index the new set does not overwrite.

Content-hash dedup hides it whenever the chunk boundaries happen to line up, which is
why small fixtures never showed it.

### Proposed fix

Route the direct path through the same delete-then-ingest transaction the watch path
already uses, rather than adding a second cleanup. One mechanism, one place to be wrong.

`memory_ingest_directory` needs the same treatment — it walks files through the same
ingestor — but a directory ingest must delete **per file**, not by directory subtree, or
re-ingesting one file would wipe its siblings.

**Open for review:** whether `IngestFileAsync` should return the count of removed stale
chunks so the tool can report it, or stay silent as it does now.

---

## Sequencing: split into two PRs

Verified against PR #405's file list (167 files): it rewrites `FileIngestor.cs`,
`IFileIngestor.cs`, `SqliteMemoryStore.cs` and `SqliteMemoryStore.Replace.cs` — every file
Defect B touches — and **none** of `ServerProbe.cs`, `ResiliencePipelineFactory.cs` or
`NodeRunner.cs`. So the defects separate cleanly:

- **PR 1 — Defect A**, on `main`, now. No overlap with #405.
- **PR 2 — Defect B**, after #405 merges. Landing it on today's `FileIngestor` would fix
  the memory corpus only and leave `code_entries` stranding the same way, on a shape about
  to be replaced.

### What PR 2 must account for (from the #405 lane, read off the post-merge source)

`ReplaceCoreAsync` is private and does three things, only one of which is watch-coupled:

1. **Embed-queue capture/restore around the delete** (`CreateQueueRestoreTable` →
   `CaptureQueueRowsForSourcePath` → delete → `RestoreQueueRowsStillBacked`). **Not**
   watch-specific — the direct path needs it too, or a re-ingest silently drops queued
   embeddings. This is a second, quieter bug the naive fix would have introduced: delete
   the rows and the queued work pointing at them goes with it.
2. **The delete-both legs** (`DeleteBySourcePath` + `DeleteCodeBySourcePath`), parameterized
   on `{projectId, path, pathPrefix}`.
3. **The `watch_files` fingerprint upsert**, already conditional on
   `ingestResult.FingerprintEligible`. The only part a direct tool call must not do — a
   direct ingest of a non-watched file has no business writing `watch_files`.

**Shape:** extract (1) + (2) + the single `fileIngestor.IngestFileAsync` call into a shared
internal core with a fingerprint on/off knob. Watch path calls it with fingerprinting on,
the direct tool path with it off. The ingest call already self-routes to both corpora, so
one call covers memory and code.

**Per-file delete confirmed.** The delete legs match exact path OR
`pathPrefix = LikePattern.Escape(path) + "/%"`. For a *file* path the prefix leg is inert,
so a single-file re-ingest cannot touch siblings in either corpus. The subtree leg only
bites when the stored source path is itself a directory, which the digest rename/missing
legs rely on deliberately. `IngestDirectoryAsync` walks per file, so routing each walked
file through the shared core preserves per-file semantics without special-casing.

**Ignore semantics must not move.** On the direct path an ignored file returns 0 chunks and
does **not** delete; the digest path owns stale-chunk cleanup for ignored files. The
delete-then-ingest must therefore sit *after* the ignore check and inside the
not-ignored branch. Putting it before would make a direct ingest of a newly-ignored file
purge its chunks — a behaviour change dressed as a bug fix, and one no existing test would
catch. **B4 below pins it.**

## Test plan (TDD, RED first)

| # | Test | Fails today because |
|---|---|---|
| A1 | `ATokenGated401CarryingAJsonRpcBody_ReportsAnswered` | (already passes — kept as a pin, not a RED) |
| A2 | A probe whose first attempt exceeds the per-attempt bound and whose second answers → `Answered` | the timeout is not retryable, so it returns `Unanswered` after one attempt |
| A3 | A probe that exceeds the bound on **every** attempt → `Unanswered`, and the handler was called `MaxAttempts` times | today it is called once |
| A4 | Caller cancellation still surfaces as cancellation, not a retry storm | guards the fix against swallowing `ctx` |
| B1 | Re-ingest the same path with content that chunks to a different count → only the new chunk set remains | the direct path never deletes |
| B2 | Re-ingest one file in a directory → siblings survive | guards the fix against over-deleting by subtree |
| B3 | Re-ingest identical content → no duplicate rows, hash unchanged | dedup must keep working |
| B4 | Direct-ingest a file that `ai-raccoon.ignore` matches, after it was previously ingested → 0 chunks AND the existing chunks are still there | pins the ignore split: cleanup for ignored paths belongs to the digest path, not this one |
| B5 | Re-ingest a file with rows queued for embedding → the queue still points at the new chunks | the delete would otherwise drop queued work with the rows |

A2 and A3 are the ones that make the retry claim falsifiable: without them the fix is
indistinguishable from raising a constant.

## Acceptance

- Every test above written first and watched RED, except A1 which is a pin.
- `serve --restart` cycles a server started seconds earlier — verified by hand against a
  real install, since that is the only instrument that found it.
- Full fast suite green; `build-slow` green in CI.
- The checklist entry for `server-lifecycle` corrected: my recorded reason names the
  wrong mechanism, and a reason that is right about the outcome and wrong about the cause
  is exactly what this project's checklist skill warns rots first.
