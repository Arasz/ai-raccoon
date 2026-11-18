# ADR-0152: Ignore rules reload on a change token, and an in-process watch change wakes the reconcile

Date: 2026-11-18

Status: Accepted for 2.10.1.

Amends: [0151](0151-the-watcher-polls-at-10s-caches-ignore-rules-and-logs-a-skip-at-debug.md) decisions 1 and 2.

## Context

The review of 2.10.0 (#963) found two gaps.

1. **The ignore-rules cache has a guard that no test can fail.** `IgnoreRulesProvider` keys each entry on the file's stamp (last write time, length) and keeps a per-root generation counter, so that an invalidation that arrives while a load reads the file stops that load from caching the old parse. A mutant that always caches survived all 574 watch, ingestion and ignore-rules tests. The interleaving needs control of the read, and the provider reads through `File` directly.
2. **`add_watch` now waits up to 10 s for its watcher.** `WatchService.AddAsync` registers the pipeline state, but the OS watcher and the initial scan start only in a `WatchHostedService` reconcile pass, which runs on the timer only. ADR-0151 widened that timer from 1 s to 10 s. The same applies to `remove_watch` and to a change of the watch-enabled setting written in the server process.

.NET already solves the first problem in `FileConfigurationProvider` (`reloadOnChange`): it calls `ChangeToken.OnChange(() => FileProvider.Watch(path), reload)`. A change token is taken before the file is read, so a change during the read fires the token, and the next load does not use the parse from before it. `PhysicalFileProvider` supplies the token either from a `FileSystemWatcher` or, in polling mode, from a timer that compares the file's last write time every 4 s (`PhysicalFilesWatcher.DefaultPollingInterval`).

## Decision

1. **`IgnoreRulesProvider` caches each root's rules with an `IChangeToken`, in the same shape as `FileConfigurationProvider`.** A load takes the root's token *before* it reads the file. The entry is valid while the token has not changed. The token is a `CompositeChangeToken` of:
   - the file provider's `Watch(FileName)` token, and
   - a per-root `CancellationChangeToken` that `Invalidate(root)` cancels (a digest of the ignore file, a rename away from or onto the name).

   The provider reads the file through an injected `IFileProvider` per root. Production uses `PhysicalFileProvider` in **active polling mode** (`UsePollingFileWatcher = true`, `UseActivePolling = true`, `ExclusionFilters.None`). This is the only setting that creates no `FileSystemWatcher`: `PhysicalFileProvider.CreateFileWatcher` (v11.0.0, line 171) makes one unless both flags are true, and `PhysicalFilesWatcher` enables it on the root for every token. A second OS watcher per root would duplicate the event stream that ADR-0151 set out to reduce (on macOS, FSEvents watches the tree). Active polling runs one timer that stats each registered file every 4 s. It only fires tokens and does not read or parse the file. The hand-written stamp check and generation counter are removed. The token does their job.
2. **An in-process watch change wakes the reconcile loop.** A `WatchReconcileSignal` singleton holds one pending wake-up (a bounded channel of capacity 1 that drops extra writes). `WatchService.AddAsync` and `RemoveAsync` request a wake-up after their transaction commits. A server-side write of a watch-enabled setting also requests one. `WatchHostedService` waits for the first of the signal or `PollInterval`. The 10 s interval now applies only to changes that another process made.
3. **`IsIgnoreFileName` compares case-insensitively on Windows and macOS.** The default file systems on those hosts ignore case, so a file the loader reads as the rules file is also recognised as the rules file when it is renamed or deleted.

## Alternatives

- **Keep the stamp and generation counter, and add a reader seam.** This makes the guard testable, but it keeps a hand-written mechanism that `Microsoft.Extensions.FileProviders` already ships and documents.
- **`PhysicalFileProvider` with `UsePollingFileWatcher = true` and lazy polling.** Rejected: the provider still creates and enables a `FileSystemWatcher` on the root, which is a second watcher beside the one that `WatchEventSource` already runs.
- **`ChangeToken.OnChange` reloading eagerly, as `FileConfigurationProvider` does.** Rejected: re-parsing rules that nobody asked for is more work than a check at the next load. The token alone marks the entry stale, and the next load reads the file.
- **A small hand-written `IFileProvider` that stats lazily.** Rejected: it is the hand-written stamp check again, beside a library that ships it.
- **Shorten `PollInterval` again.** Rejected: that restores the idle bank opens that ADR-0151 removed.

## Consequences

- **Positive.** The in-flight-invalidation guard is a token captured before the read, and a test can fire it deterministically through a fake `IFileProvider`. `add_watch`, `remove_watch` and an in-process enable or disable act within one pass, not within 10 s.
- **Negative.** One polling timer per root stats the ignore file every 4 s while a token is registered. That is one `stat` per root per 4 s, against the three bank opens per second that ADR-0151 removed. The provider is disposed with `IgnoreRulesProvider`, which stops the timer.
- **Negative.** A polling token checks the last write time once every 4 s. An edit to the ignore file of a root that no watch covers (an `ingest_directory` run) can therefore be served the old rules for up to 4 s. In 2.10.0 the stamp check saw the edit at the next load. A watched root is not affected, because the digest of the edit invalidates the root immediately. An edit that keeps the same stamp is seen only through that digest, as in 2.10.0.
- **Negative.** A new package reference: `Microsoft.Extensions.FileProviders.Physical` (the same 11.0.0 release train as the other `Microsoft.Extensions` packages).
- **Negative.** On a case-sensitive APFS volume, a file that differs from `ai-raccoon.ignore` only by case is treated as the rules file by name. The effect is an extra invalidation and a skipped ingest of that one file.
- **Confirmation.** `IgnoreRulesProviderTests` fires the token while a fake provider serves the read, and asserts that the next load reads again. `WatchHostedServiceTests` adds a watch with the fake clock stopped, and asserts that the watcher started and an initial scan was queued. `IgnoreRulesProviderTests` asserts the host comparison on each OS family.
