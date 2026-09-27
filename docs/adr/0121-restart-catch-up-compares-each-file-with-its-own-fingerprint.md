# 0121 — Catch-up compares each file with its own fingerprint, and heals on a timer

Date: 2026-09-27

Status: Accepted

## Context

Card D1 of the file-watcher design (`docs/plans/file-watcher-implementation.md`) gave each watch
one last-change timestamp, `watches.last_change_ts`, and had the restart scan re-queue every file
whose mtime was later than it. `WatchDigestExecutor` writes that column on every digest of any file
in the watch: a real change, a hash-skip touch, a delete. So the column means "the last time
anything under this root was digested", not "every change before this moment has been seen".

The rule rests on the premise the feature file wrote down on 2026-08-04: missed events only happen
while the server is down. That premise is false. A running server loses events too. A
`FileSystemWatcher` burst during `git checkout` can deliver nothing for a replaced file, and no
error either. A digest that fails drops its event (`WatchPipeline.RunJobAsync` records the failure
and moves on), and a watch stopped after five failures drops everything queued for it. In each case
the next successful digest of a sibling file advances `last_change_ts` past the missed file's mtime.
From then on `mtime > last_change_ts` is false for that file, it already has a fingerprint, and
every restart skips it. Nothing short of touching the file brings it back.

The owner's bank showed exactly this on 2026-09-27. A read-only copy held 9,339 `watch_files` rows.
Hashing each file on disk the way the digest does (SHA-256 of the normalized path plus content)
found 388 whose stored hash no longer matched the file: 232 under `ai-raccoon`, 153 under `jsaa`,
3 under `ai-badger`. Every one of the 388 had an mtime later than its own fingerprint's
`updated_at`. A recount minutes later (421 files, the extra ones edited in between by agents at
work) found every stale file's mtime at or before its watch's `last_change_ts`. For `jsaa` that
meant a watermark of 2026-09-27 10:32 UTC against mtimes from 2026-08-30 to 2026-09-23. The worst
lag was 774.8 hours. The restart scan had run over these files many times and skipped each one.

Two more facts shaped the decision. Restart was the only recovery point, and a server can run for
days. And the catch-up walk enumerated every entry under the root before filtering, `.git`,
`node_modules`, `bin`/`obj` and agent worktrees included. Over the owner's ten watch roots that is
1.05 million entries and about 7.5 CPU-seconds to stat 9,494 indexable files, too much to repeat on
a timer.

## Decision

**1. A fingerprinted file is due when its mtime is at or after its own fingerprint time, or its
size differs from the size the digest recorded.** A file with no fingerprint stays due, and a
never-synced watch (`last_change_ts = 0`) still gets a full scan.

- `IWatchStore.ListFileStampsAsync` returns path to `WatchFileStamp(UpdatedAt, Size)` for the
  project, one query loaded once per pass.
- `watch_files` gains a nullable `size` column through the digest-gated ensure path
  (`MemorySchema.EnsureWatchFilesSizeColumnAsync`), with no version bump. The digest reads the file
  length before it reads the content and records it with `IWatchStore.SetFileSizeAsync`, on both
  the replace and the hash-skip path. A row written before the column existed keeps `NULL` and is
  judged by mtime alone until its next digest fills the size in. An upgrade does not rehash
  anything.
- The mtime comparison is `>=`, not `>`. The fingerprint time is taken after the digest read the
  file, both truncated to seconds, so a write in that same second may have landed after the read.
  Such a file gets one extra hash-skip, and the touch moves its `updated_at` past the mtime.
- `WatchCatchUp.EnqueueChangedSince(projectId, path, watermark)` becomes
  `EnqueueChangedFiles(projectId, path)`. `last_change_ts` stays as the never-synced marker and the
  "last change" that `watch status` shows. It no longer filters anything.

**2. The walk never descends into a hidden or deny-set directory.** `WatchCatchUp` enumerates with a
`FileSystemEnumerable` whose recurse predicate skips a directory whose name starts with `.` or is
in `WatchDenySet.Names`. Every file under such a directory was already excluded by the per-file
`WatchDenySet.Excludes` check, which stays, so the set of files is the same. Measured over the
owner's roots, one walk dropped from about 7.5 CPU-s to about 0.1 CPU-s for the same 9,494 files. An
unreadable denied directory no longer fails the whole scan.

**3. Every active watch that has not stopped gets a changed-files scan every 5 minutes
(`WatchHostedService.HealInterval`).** The first interval starts at the first reconcile pass, so it
never doubles the start-up scan. The scan is the same one restart runs: the single-flight guard joins
a scan already running, and the cross-process lease keeps two servers from scanning one root. Five
minutes is short enough that an agent rarely searches stale code after a branch switch. At 0.1 CPU-s
per pass it costs about 0.03% of one core. A watch that has stopped after five failures is skipped;
its pipeline would drop the queued events anyway, and restart remains its recovery (feature rule 14).

**4. The first successful digest after a failure scans that watch at once.** `WatchPipeline` raises
`Recovered` when a watch in `Retrying` digests successfully, and the hosted service queues a
changed-files scan for it. The failed digest dropped its event, so the change it carried is picked up
without waiting for the timer. `Stopped` has no in-process way out except removing the watch, and
re-adding it starts a full scan, so no trigger is needed there.

**Rejected alternatives.**

- *Stop advancing the watermark past undigested work.* The watch has no idea which events it never
  received, so it cannot know where "undigested" begins.
- *Hash every file on every scan.* That catches every content change, but it reads every file
  under every root on each pass. The stat-based check costs the same one stat per file the old
  rule already paid.
- *A shorter interval, such as one minute.* The walk is cheap enough, but each pass also takes a
  lease row per watch. Five minutes keeps those bank writes negligible and still heals within
  minutes.

## Consequences

- **Positive.** A missed live event now heals within 5 minutes, or right away after a failed digest
  recovers, and on restart as before. Banks upgraded with files already stale heal on their first
  scan: those files have an mtime later than their fingerprint. The owner's 388 files are in this
  group.
- **Positive.** Every catch-up scan, restart or timer, walks only the directories it can index,
  which makes the start-up scan cheaper too.
- **Neutral.** Two new log events: 322 (Debug) for a heal pass and 323 (Information) for a recovery
  scan. One schema column, added without a version bump.
- **Remaining limit.** A file replaced by content of exactly the same byte length with an mtime
  older than its fingerprint is not caught, because nothing but a content read can see it. That
  needs a tool that preserves old mtimes (`cp -p`, `tar -x`, `rsync -t`) and an identical size.
  `git` stamps checked-out files with the checkout time, so it is never this case.
- **Supersedes** card D1's "restart re-ingests targets changed since it" (the per-watch timestamp as
  a filter), the feature file's 2026-08-04 comment that missed events only happen while the server
  is down, and restart as the only recovery point for a missed event.
