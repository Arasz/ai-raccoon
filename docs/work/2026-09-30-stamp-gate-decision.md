# stamp-gate — convergence-stamp decision (embedding.chunkBudget)

Task `air-254-vs-1022-retrieval-measurement-809` step `stamp-gate`, issue #809.
Decides which `copy-1022` branch runs: STAMPED / FALLBACK / GATED.

Data root: this step writes no data root — it is a read-only probe. The live bank is never written.

## Probe (read-only, verbatim)

```
probe_time_utc: 2026-09-30T09:06:10Z
$ sqlite3 "file:$HOME/.ai-raccoon/memory.db?mode=ro" "SELECT key,value FROM settings WHERE key LIKE 'embedding.chunkBudget%';"
embedding.chunkBudget.retryAttempts|1022:2

$ sqlite3 ... "SELECT COUNT(*) FROM settings WHERE key = 'embedding.chunkBudget';"
0
$ sqlite3 ... "SELECT COUNT(*) FROM settings WHERE key = 'embedding.chunkBudget.retryAttempts';"
1
$ sqlite3 ... "SELECT COUNT(*) FROM entries WHERE embed_state != 'embedded';"
0
```

(URI is `mode=ro` on every open; probe tool `sqlite3` 3.54.0. An earlier probe in the same
session at 2026-09-30T08:28Z showed the same rows.)

## Reading the evidence

- `embedding.chunkBudget` (the convergence stamp) is **absent** (count 0) on the live bank.
  Per ADR-0125 the stamp is present iff the last reconcile run converged — so the live bank
  has NOT converged at the resolved budget as of the probe time.
- `embedding.chunkBudget.retryAttempts|1022:2` — the resolved budget is 1022 and the live
  bank is at **2 of the 3 unproven passes** ADR-0125 allows per resolved budget
  (`MaxRetryAttempts`, ChunkBudgetReconciler.cs:77). The third unproven pass stops the
  gating (residual population named in ADR-0125).
- 0 entries awaiting embed — the unproven state is about note-group re-chunk proof, not
  pending embeddings.

## Decision: **FALLBACK**

`copy-1022` takes the copy and **converges it on a disposable data root until the
`embedding.chunkBudget` stamp is present** (read-only probe for the stamp after each pass),
with the refusal window noted — the plan's FALLBACK branch verbatim.

- **STAMPED** would have applied if `embedding.chunkBudget` had been present at probe time
  (copy converged on arrival). It is not.
- **GATED** is not warranted: the FALLBACK machinery exists precisely for an un-stamped
  source, and nothing in the probe shows convergence being impossible on a disposable root.

### Carry-into-copy-1022 notes (load-bearing)

1. **Inherited retry counter:** a copy taken now carries `retryAttempts|1022:2`. The
   disposable convergence's first pass is therefore the budget's **3rd** pass. If it remains
  unproven, ADR-0125's ceiling applies (gating stops; residual population). The convergence
   evidence must record which passes ran and how the counter moved, and the pin's `branch`
   field must say `FALLBACK` with that evidence.
2. **Order is load-bearing (PR-3.R3):** copy -> converge (FALLBACK only) -> PIN. The pin's
   sha256 must describe the FINAL artifact; re-pin after any convergence write (the
   convergence writes the bank).
3. If the live server restarts during the task window and stamps the live bank
   (`live-stamp-opportunistic` watches for exactly that), the copy decision does **not**
   change retroactively — this decision is timestamped evidence about the source as of
   2026-09-30T09:06:10Z. A stamped-later live bank can serve as corroboration in the
   report only.
