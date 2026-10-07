# 0129 — The file watcher's event buffer is 64 KB

Date: 2026-10-07

Status: Accepted

## Context

`WatchEventSource` creates one `FileSystemWatcher` per registered path. On Windows the watcher
stores pending change notifications in a buffer the OS allocates from non-paged kernel pool, and
`FileSystemWatcher.InternalBufferSize` defaults to **8192 bytes (8 KB)**. A burst of changes under a
watched directory — a `git checkout`, an IDE "reformat all", an install — can fill that buffer
faster than the adapter drains it. The watcher then raises `Error` with an
`InternalBufferOverflowException` and **drops every event it could not store**.

That error reaches the logs twice (`WatchEventSource` event 300, and the `onError` callback's event
330 in `AppRegistrations`), which is what the field reports showed for a watch on
`C:\Source\LocalStartup`. No rescan is queued from the `Error` path itself. ADR-0121 added a
changed-files heal every 5 minutes, so a burst is now recovered within minutes rather than at the
next restart — but the overflow still recurs on every large burst and still loses events for a
window.

## Decision

**Every watcher `WatchEventSource.Start` creates sets `InternalBufferSize = 64 * 1024`.** 64 KB is
the documented maximum for the property; .NET clamps anything below 4096 up to 4096, so lower values
buy nothing on a large tree. The value is set for directory and file-mode watchers alike, keeping
one creation path. Other platforms ignore the property, so the setting is a no-op outside Windows.

`WatchEventSource` exposes the live `FileSystemWatcher` to tests (`WatcherFor`) so the configured
buffer is asserted on the watcher `Start` actually created, not on a factory beside it.

**Rejected alternatives.**

- *Leave the 8 KB default and rely on ADR-0121's heal.* The heal bounds the staleness, but it waits
  up to five minutes and the overflow still fires on every burst; a buffer that fits the burst is
  cheaper than a scan that repairs it.
- *A 32 KB buffer.* Half the headroom for the same per-watcher cost structure and no evidence it
  clears the observed bursts; the documented ceiling is 64 KB, so there is no reason to stop short.
- *Raise the buffer above 64 KB.* The docs say the buffer "must not exceed 64 KB"; the runtime
  setter does not enforce it, so a larger value would be an undocumented allocation from non-paged
  pool with no contract behind it.
- *Only bump directory watches.* File-mode watchers sit on the parent with a `Filter` set and
  `IncludeSubdirectories = false`, so their event volume is tiny. Bumping them too costs 64 KB per
  file watch and removes a second creation path that would drift — the uniform rule is the simpler
  one at this scale.

## Scope and limits

The 64 KB buffer reduces overflow frequency but cannot eliminate it: a burst larger than 64 KB still
overflows and still drops events. ADR-0121's 5-minute heal remains the correctness backstop, and the
`Error` path still only logs. The buffer comes from non-paged pool and cannot be paged to disk, which
is why it is capped rather than unbounded; the cost scales with the number of registered watches.

## Consequences

- **Positive.** The observed `InternalBufferOverflowException` on a busy root should stop, so events
  from a `git checkout` are digested live instead of at the next heal.
- **Neutral.** No tool schema, setting, status or log-event change. The overflow `Error` path is
  unchanged and a genuinely oversized burst still surfaces events 300/330.
- **Neutral.** Each watcher now holds 64 KB of non-paged pool instead of 8 KB.

## Verification

`tests/AiRaccoon.Tests/Integration/Watch/WatchEventSourceTests.cs` —
`Start_SetsInternalBufferTo64Kb_SoAChangeBurstIsNotDropped` asserts a started watch's
`InternalBufferSize` is 65536 (it read 8192 before this decision).