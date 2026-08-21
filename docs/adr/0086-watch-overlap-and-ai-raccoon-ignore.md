# 0086. Watch overlap resolution and `ai-raccoon.ignore` — one-transaction prune/reject, no version bump

Date: 2026-08-21

Status: Accepted

Plan: `docs/work/2026-08-21-code-search-implementation-plan.md` (rev 3, §2.1, §2.2, §2.3, §3.5,
§12.6 S7/S8).

## Context

Code corpus support means "watch a repo root" becomes the default action, and a repo root
almost always contains watches already registered on subdirectories (`docs/`, `src/`) from
before this feature existed. Two questions needed answers that did not exist before: what
happens when a new watch's scope contains — or is contained by — an existing one, and how does
a bank that already has overlapping watches get corrected once this ships.

The owner's requirements (plan §2.1/§2.2) settled the shape: **the broader watch wins.**
Registering a watch whose scope contains another's prunes the contained watch; registering a
narrower watch inside an existing broader one is **rejected**, not silently absorbed — an agent
that asked to watch `/repo/docs` and gets told "already covered by `/repo`" needs to know that,
not have its request quietly no-op.

## Decision

1. **Containment** = `IngestPath.IsWithinScope(inner, outer)` — a separator-aware real-path
   prefix check (`/repo2` is never contained by `/repo`, even though the strings share a
   prefix).
2. **Atomicity (review MUST-FIX, upgraded from a documented-gap in the rev-1 plan):** the prune
   and the new registration are **one `BEGIN IMMEDIATE` store transaction**
   (`PruneAndAddAsync`, over the watch rows and the `watch_files` cascade). A crash anywhere in
   that transaction leaves either the old watches or the new watch — never a path with no
   watch covering it. The runtime `UnregisterWatch` calls for pruned watches run **after**
   commit; a crash between commit and unregister leaves stale runtime state that the hosted
   service's own registration poll reconciles, so digest ownership stays deterministic even
   across that window.
3. **Tie-break for mutual containment** (real-path-equivalent registrations via symlink or case
   spelling on case-insensitive filesystems): keep the **longest literal path; on equal
   length, the first-registered**. A watch is never pruned by a survivor whose real path
   equals its own.
4. **Ordering in `AddAsync`:** reject-if-contained, then prune-and-register in the one
   transaction above. A narrower add inside an existing broader watch throws
   `WatchOverlapException` naming the covering watch; nothing is written.
5. **`ai-raccoon.ignore`** — one file per watch/tree root, gitignore-subset syntax (`*`, `**`
   as a whole segment, trailing `/` directory patterns, leading/anchored `/`, no `!` negation
   in v1, any-match-wins, case per host OS), read fresh once per scan/digest event (no cache).
   An ignored path is **never fingerprinted, never chunked** in either corpus; a digest landing
   on a now-ignored path **deletes stale chunks** (cleanup for a file indexed before the ignore
   line existed) and updates `last_change_ts` only. The ignore file is never self-ignored.
   **Ignore wins over an explicit `memory_ingest_file`** of the same path — consistent with
   "never fingerprinted," not a separate rule. Editing the ignore file triggers a full re-scan
   of the watch, single-flighted (`WatchScanGuard`'s existing per-watch join, not a new queue).
6. **Repo-watch-by-default:** registering a watch on a repo root applies rule 1 to every watch
   already inside it, then catch-up scans the whole repo into the correct corpora. Enumeration
   skips hidden directories and a built-in deny set (`node_modules`, `bin`, `obj`, `.git`,
   `.venv`, `__pycache__`, `dist`, `build`, `target`) for repo-root watches specifically — the
   ignore file is the extension surface for anything else; without this a repo-watch-by-default
   indexes dependency trees at real embedding cost.

## The migration reversal (S7/S8) — recorded honestly

The plan's first cut (§4) reached this decision as a version-ladder step: `CurrentVersion`
10→11, a guarded, one-time migration that retro-prunes whatever overlapping watches already
exist on a bank opened by the first binary that ships this feature. **That step is reverted.**
`CurrentVersion` stays 10.

**Why:** `user_version` survives `VACUUM INTO`, and this repo's sync gate refuses a pull from a
newer `user_version` than the puller's own. A version bump on first open means: any concurrent
session running the older binary against the same bank — and this repo runs concurrent sessions
as standard practice, not an edge case — hard-fails the instant the bump lands, and every peer
that syncs against the bumped bank is refused until it upgrades too. A one-time migration is the
wrong shape for "a repo-watch app used by more than one process at a time."

**The fix:** the retro-prune runs the way `MigrateIngestScopeKeysAsync` already does
(`MemorySchema.cs:942`) — **unconditional, ungated, idempotent, on every bank open, no version
bump, soft per-row failure handling.** ADR-0023 is the repo's own precedent for exactly this
shape: a trigger-body fix that must reach every existing bank without a version bump belongs
beside `MigrateIngestScopeKeysAsync`, probing state first and writing only on the branch that
still needs it, reserving the version ladder for changes that genuinely need guarded, ordered,
one-time work (a non-idempotent `ALTER TABLE`, a data backfill that cannot re-run safely).
Retro-pruning overlapping watches **can** re-run safely — a bank with no overlaps left pays one
cheap read and writes nothing — which is precisely the property ADR-0023 requires before it
allows a check to bypass the ladder.

## Consequences

- **Positive**: no concurrent-session or sync-refusal blast radius from shipping this feature —
  every process, whatever binary version it runs, opens the same bank without a compatibility
  cliff.
- **Positive**: the retro-prune is provably idempotent by construction (a probe-then-act shape,
  not "runs once and hopes"), so a corrupted or partially-applied prune from a crash is simply
  retried on the next open.
- **Negative**: the retro-prune's cost is paid on every open (one cheap read), not once ever —
  the trade ADR-0023 already made and this decision inherits rather than re-argues.
- **Not addressed**: this ADR does not cover the code drain mechanism (ADR-0087) or the search
  surface (ADR-0088).

Extends ADR-0023 (probe-first, unconditional, idempotent migrations beside
`MigrateIngestScopeKeysAsync`) and depends on ADR-0085 (the corpus this watch machinery feeds).
