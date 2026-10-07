# 0130 — The file watcher ignores directory-name notifications

Date: 2026-10-07

Status: Accepted

## Context

`WatchEventSource` watched `NotifyFilters.FileName | NotifyFilters.DirectoryName |
NotifyFilters.LastWrite`. The `DirectoryName` half never produces anything ingestable: a
directory's own Created/Changed/Renamed event reaches `WatchDigestExecutor.DigestAsync`, which
returns immediately for any path that is a directory ("nothing to digest, and NOT a vanished
file"). It is dead weight on the pipeline and it adds events to the watcher's buffer — the same
buffer ADR-0129 enlarged because a change burst overflows it. Directory churn (build output,
`.git` object directories, package managers) is a large share of that burst.

`NotifyFilter` is not a Windows-only concept: on Linux the runtime drops directory events when
`DirectoryName` is unset (`FileSystemWatcher.Linux.cs`), and on macOS `TranslateFlags` keys the
directory/file split off the same flag (`FileSystemWatcher.OSX.cs`). So the filter changes what the
adapter is handed on every platform, not just Windows.

One behaviour did depend on `DirectoryName`: a directory's Deleted event is not a directory that
exists, so it reached the delete branch and `DeleteSourcePathAsync` cascaded the fingerprints and
chunks of everything under it. That cascade is a convenience, not the only cleanup path — ADR-0121's
changed-files scan runs every 5 minutes and `WatchCatchUp.ReconcileAsync` enqueues a Deleted for
every fingerprinted file missing on disk, and the walk re-enqueues files that appear at new paths.

## Decision

**A watcher watches `NotifyFilters.FileName | NotifyFilters.LastWrite`.** `DirectoryName` is
deliberately absent.

The one delay this buys: deleting or renaming a directory clears its old-path rows at the next
reconcile (within 5 minutes) instead of at the directory event. A file-level Created/Deleted/Changed
still digests live, which is what the feature promises.

**Rejected alternatives.**

- *Keep `DirectoryName`.* It feeds the digest nothing (the directory branch returns), so its only
  outcomes are extra buffer pressure and one immediate cascade the heal already performs.
- *Keep `DirectoryName` for the cascade and filter directory events at the adapter.* The scan
  already owns that cleanup; duplicating it in the adapter adds a second code path for the same
  result and does not reduce the buffer load that caused the overflow.
- *Drop `FileName`/`LastWrite` too and rely on directory events.* Directories summarize contents
  rather than per-file changes, so the adapter would lose the file identity it needs.

## Consequences

- **Positive.** Directory-heavy churn no longer enqueues events the digest discards. That lowers
  the chance a burst overflows the 64 KB buffer (ADR-0129) and trims pipeline work.
- **Behaviour change.** A directory delete or rename is reflected within the 5-minute reconcile
  rather than immediately. No permanent staleness: `ReconcileAsync` removes rows for files gone from
  disk, and the walk re-ingests files at their new paths.
- **Neutral.** No tool, schema, settings or log-event change. On Linux and macOS the runtime applies
  the same directory/file split, so the change is not Windows-only.

## Verification

`tests/AiRaccoon.Tests/Integration/Watch/WatchEventSourceTests.cs` —
`Start_DoesNotWatchDirectoryNameNotifications` asserts a started watch's `NotifyFilter` is exactly
`FileName | LastWrite` (it read `FileName | DirectoryName | LastWrite` before this decision), and the
directory-delete/rename cleanup itself is covered by the existing catch-up/reconcile tests.