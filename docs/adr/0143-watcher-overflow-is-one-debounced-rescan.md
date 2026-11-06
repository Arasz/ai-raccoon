# ADR-0143: A watcher buffer overflow is one debounced rescan, and the buffer size is a setting

Date: 2026-11-06

Status: Accepted for 2.5.0.

## Context

ADR-0129 raised the `FileSystemWatcher` buffer to 64 KB, the documented maximum, but a larger burst still overflows it. An overflow raises `Error` with `InternalBufferOverflowException` and the watcher drops every event it could not store. Before this decision a burst of overflows did two things badly:

- Each one logged twice at `Error` (event 300 from `WatchEventSource` and 330 from the `onError` callback), so one `git checkout` could write dozens of lines.
- None queued a rescan. The dropped changes waited for the 5-minute heal (ADR-0121).

The buffer size was also a constant, so a machine that overflowed on every burst had no way to trade non-paged pool for headroom, and a machine with many watches no way to give it back.

## Decision

**Overflow is a rescan trigger, not a failure report.** `WatchEventSource.HandleError` treats `InternalBufferOverflowException` separately from every other watcher error:

- The first overflow of a watch opens a window of `WatchEventSource.OverflowDebounce` (2 s). Overflows inside the window only add to its count.
- When the window closes, the source logs one `Warning` (event 299, with the count) and raises `Overflowed(projectId, path)` once.
- `WatchHostedService` handles `Overflowed` by queueing a changed-files scan for that watch if it is active and not stopped. A running scan may already have passed the lost change, so joining it recovers nothing: the rescan is held until that scan ends, then queued on the next reconcile pass (2.5.1).
- The window is fixed from the first overflow, not extended by later ones, so a sustained storm still rescans every 2 s at most instead of never.
- `Stop` and `StopAll` drop an open window.

Every other watcher error still reports through `onError` and event 300 unchanged.

**The buffer size is the global setting `watch.buffer-size.global`.** `WatchBuffer.Resolve` reads it: absent or unreadable is 64 KB, and a number is clamped to 4096–65536 (.NET raises anything below 4096; 64 KB is the documented ceiling, ADR-0129). `WatchHostedService` reads it when it starts a watcher, so a new value applies to watchers started after it. `WatchEventSource.Start` takes the size as a parameter.

The key is global only. The buffer is a per-watcher OS allocation, not a per-project choice, so it has no per-project prefix and needs no `ProjectSettingsKeys` entry. It is written like any global setting, through the settings endpoint.

**Rejected alternatives.**

- *One non-recursive watcher per allowed directory, so ignored folders are never watched (`node_modules`, `bin`, `obj`, `.git`).* It removes the noise at its source and shrinks the number of events the buffer must hold, which makes it the better long-term fix. It is also a different design: it needs a directory walk at start, a watcher added for every directory created later and removed for every one deleted, and a rule for a directory renamed into or out of scope. Windows keeps one change log per volume and each watcher costs a non-paged buffer, so the watcher count matters too. It is a separate decision, to be taken with a measurement of how much of the overflow volume ignored folders cause. This ADR does not block it; with it, overflow would be rarer and this rescan would remain the backstop.
- *Rescan on every overflow.* A burst of N overflows queued N scans of the same tree. The scan guard joins a running scan but not one queued after it finishes.
- *Extend the window on each overflow (a trailing-edge debounce).* A storm that never pauses would postpone the rescan indefinitely.
- *A per-project buffer size.* It would add a settings prefix, repair and census entries, and CLI commands for a value that is a property of the machine.

## Consequences

- **Positive.** A burst writes one warning and queues one scan instead of dozens of error lines and no scan. Dropped events are recovered in about two seconds, not up to five minutes.
- **Positive.** An operator can lower the buffer on a machine with many watches or keep it at the maximum.
- **Neutral.** The 64 KB default is unchanged, so ADR-0129's test still holds.
- **Neutral.** A new value takes effect for watchers started after it is written (a restart, or a watch re-enabled or added).
- **Negative.** Within the 2 s window, the changes the overflow dropped are not yet ingested.

## Verification

`WatchEventSourceTests` — five overflows inside the window raise `Overflowed` once and report no error; the count is logged once as event 299; a later overflow opens a new window; separate watches rescan separately; `Stop` and `StopAll` cancel a pending rescan; `Start` honours a given buffer size. `WatchHostedServiceTests` — three overflows queue exactly one changed-files scan, which ingests the file the overflow dropped, and the setting reaches the started watcher. `WatchBufferTests` — default, parse failure and both clamps. With the overflow branch disabled, five of these tests fail.
