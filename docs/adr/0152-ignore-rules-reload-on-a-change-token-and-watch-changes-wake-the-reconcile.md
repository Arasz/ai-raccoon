# ADR-0152: Ignore rules reload on a change token, and an in-process watch change wakes the reconcile

Date: 2026-11-18

Status: Proposed for 2.10.1.

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

   The provider reads the file through an injected `IFileProvider` per root. Production uses `PhysicalFileProvider` in **polling mode** (`UsePollingFileWatcher = true`, `ExclusionFilters.None`). It does not use the `FileSystemWatcher` mode, because that watcher is recursive over the root. It would duplicate the event stream that ADR-0151 set out to reduce, and on Windows it would also duplicate the kernel buffer. The hand-written stamp check and generation counter are removed. The token does their job.
2. **An in-process watch change wakes the reconcile loop.** A `WatchReconcileSignal` singleton holds one pending wake-up (a bounded channel of capacity 1 that drops extra writes). `WatchService.AddAsync` and `RemoveAsync` request a wake-up after their transaction commits. A server-side write of a watch-enabled setting also requests one. `WatchHostedService` waits for the first of the signal or `PollInterval`. The 10 s interval now applies only to changes that another process made.
3. **`IsIgnoreFileName` compares case-insensitively on Windows and macOS.** The default file systems on those hosts ignore case, so a file the loader reads as the rules file is also recognised as the rules file when it is renamed or deleted.

## Alternatives

- **Keep the stamp and generation counter, and add a reader seam.** This makes the guard testable, but it keeps a hand-written mechanism that `Microsoft.Extensions.FileProviders` already ships and documents.
- **`PhysicalFileProvider` with its `FileSystemWatcher`.** Rejected: it watches the whole root recursively. That is a second watcher per root, beside the one that `WatchEventSource` already runs.
- **Active polling (`UseActivePolling = true`) with `ChangeToken.OnChange` reloading eagerly.** Rejected: a background timer that re-parses rules nobody asked for is more than a lazy check at the next load. Lazy polling checks the stamp at most once every 4 s, and only when a load asks.
- **Shorten `PollInterval` again.** Rejected: that restores the idle bank opens that ADR-0151 removed.

## Consequences

- **Positive.** The in-flight-invalidation guard is a token captured before the read, and a test can fire it deterministically through a fake `IFileProvider`. `add_watch`, `remove_watch` and an in-process enable or disable act within one pass, not within 10 s.
- **Negative.** A polling token checks the last write time at most once every 4 s. An edit to the ignore file of a root that no watch covers (an `ingest_directory` run) can therefore be served the old rules for up to 4 s. In 2.10.0 the stamp check saw the edit at the next load. A watched root is not affected, because the digest of the edit invalidates the root immediately. An edit that keeps the same stamp is seen only through that digest, as in 2.10.0.
- **Negative.** A new package reference: `Microsoft.Extensions.FileProviders.Physical` (the same 11.0.0 release train as the other `Microsoft.Extensions` packages).
- **Negative.** On a case-sensitive APFS volume, a file that differs from `ai-raccoon.ignore` only by case is treated as the rules file by name. The effect is an extra invalidation and a skipped ingest of that one file.
- **Confirmation.** `IgnoreRulesProviderTests` fires the token while a fake provider serves the read, and asserts that the next load reads again. `WatchHostedServiceTests` adds a watch with the fake clock stopped, and asserts that the watcher started and an initial scan was queued. `IgnoreRulesProviderTests` asserts the host comparison on each OS family.
