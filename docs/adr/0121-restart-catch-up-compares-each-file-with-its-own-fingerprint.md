# 0121 — Restart catch-up compares each file with its own fingerprint time

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
`FileSystemWatcher` burst during `git checkout` can deliver nothing for a replaced file. A digest
that fails drops its event (`WatchPipeline.RunJobAsync` records the failure and moves on), and a
watch stopped after five failures drops everything queued for it until restart. In each case the
next successful digest of a sibling file advances `last_change_ts` past the missed file's mtime.
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

## Decision

**A fingerprinted file is due for the restart scan when its mtime, in unix seconds, is at or after
its own `watch_files.updated_at`.** A file with no fingerprint stays due, and a never-synced watch
(`last_change_ts = 0`) still gets a full scan, as before.

- `IWatchStore.ListFileStampsAsync` returns path to `updated_at` for the project. It is one query
  over the same rows `ListFilesAsync` already read. `WatchCatchUp` loads it once per pass in place
  of the old path set.
- `WatchCatchUp.EnqueueChangedSince(projectId, path, watermark)` becomes
  `EnqueueChangedFiles(projectId, path)`. `WatchHostedService` calls it for every watch whose
  `last_change_ts` is non-zero. The column stays as the never-synced marker and the "last change"
  shown by `watch status`. It no longer filters anything.
- The comparison is `>=`, not `>`. The fingerprint time is taken after the digest read the file,
  both truncated to seconds, so a write in that same second may have landed after the read. Such a
  file gets one extra hash-skip on the next restart, and the touch moves its `updated_at` past the
  mtime.

**Rejected alternatives.**

- *Stop advancing the watermark past undigested work.* The watch has no idea which events it never
  received, so it cannot know where "undigested" begins.
- *Hash every file on every restart.* That catches content changes behind an unchanged mtime, but
  it reads every file under every root on each start. The per-file mtime check costs the same stat
  per file the old rule already paid, with no extra reads.
- *A periodic re-scan of active watches.* That would heal a missed event without a restart, but it
  adds a new timer and CPU cost while the server runs. It is a separate decision and this ADR does
  not take it. Restart stays the documented recovery point (feature rule "Watch errors are reported
  through memory_watch_status": "a missed event goes straight to catch-up processing on restart").

## Consequences

- **Positive.** A missed live event now heals on the next restart, as the feature file promised.
  Banks upgraded with files already stale heal on their first restart: those files have an mtime
  later than their fingerprint, so the first scan re-digests them. The owner's 388 files are in
  this group.
- **Neutral on CPU.** The scan still stats each file once. The only new cost is a map of path to
  a 64-bit time in place of a path set, and the few same-second files each restart re-reads once.
  The single-flight guard, the lease and the once-per-process `_active` gate from
  `docs/plans/2026-08-07-watch-scan-runaway-fix.md` are unchanged.
- **Accepted limit.** A file replaced by content with an mtime older than its fingerprint (`cp -p`,
  `tar -x`, `rsync -t`) is still not caught, because nothing but a content read can see it. `git`
  stamps checked-out files with the checkout time, so the case this ADR fixes is covered.
- **Supersedes** card D1's "restart re-ingests targets changed since it" (the per-watch timestamp as
  a filter) and the feature file's 2026-08-04 comment that missed events only happen while the
  server is down.
