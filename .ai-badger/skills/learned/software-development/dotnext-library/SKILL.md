---
name: dotnext-library
description: Use when working with DotNext in this repo.
version: 1.0.0
author: Hermes Agent
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, threading, concurrency, dotnext]
---

# DotNext (.NEXT) — usage guide for this repo

DotNext (https://dotnet.github.io/dotNext/, GitHub dotnet/dotNext) is a high-performance .NET library suite aimed at
near-zero-allocation, high-load scenarios. MIT-licensed, 100% managed. **Repo pins: DotNext 6.6.0 + DotNext.Threading
6.6.0** (Directory.Packages.props), both .NET 10-compatible (6.x = active support line).

## What the repo already uses

- `DotNext.Collections.Generic` in McpServerSetup.cs and several smoke tests:
    - `IReadOnlyList<T>.Singleton(item)` / `IReadOnlySet<T>.Singleton(item)` — allocation-free single-element read-only
      list/set. This is the main usage so far.
- The package refs were added 2026-08-06 (commit 6af28cd) — the library is available for new concurrency work but NOT
  yet used for locking.

## Collections (DotNext.Collections.Generic)

- `list.Convert(mapper)` — lazy read-only mapped view (list/collection/dictionary variants).
- `IReadOnlyList<T>.Singleton(x)`, `IReadOnlySet<T>.Singleton(x)` — single-element, no array alloc.
- `IReadOnlyList<T>.Repeat(item, n)` — n-element view over one stored item.
- `collection.ToArray()` — for ICollection<T>/IReadOnlyCollection<T>.
- `array.ToString(",")` — collection-to-string with delimiter.
- `list.Slice(1..)` — ListSegment<T> (range slicing for List<T>, like ArraySegment).
- `Set.Range(0L.Disclosed, 3L.Disclosed)` — ordered numeric range sets.
- `IEnumerable<T>.Copy()` — pool-rented MemoryOwner<T> copy.
- `items.ForEach(handler)` — functional iteration.

## Async locking (DotNext.Threading)

Async locks are NON-REENTRANT, don't block the caller thread, and are allocation-free when uncontended. **Never mix
blocking locks (Monitor/ReaderWriterLockSlim/Lock) with async locks on the same object** — they're unaware of each
other. SemaphoreSlim is the one exception (has both blocking and async acquisition on the same object).

### AsyncLock — unified facade

```csharp
using DotNext.Threading;

var semaphore = new SemaphoreSlim(1, 1);
var syncLock = Lock.Semaphore(semaphore);        // blocking facade
var asyncLock = AsyncLock.Semaphore(semaphore);  // async facade — SAME object

using (await asyncLock.AcquireAsync(CancellationToken.None)) { }
```

AsyncLock implements IAsyncDisposable (graceful shutdown for supported lock types).

### Per-object reader/writer extensions (make any object thread-safe)

```csharp
using static DotNext.Threading.AsyncLock;

var builder = new StringBuilder();
using (await AcquireReadLockAsync(builder, CancellationToken.None)) { ... }
using (await AcquireWriteLockAsync(builder, CancellationToken.None)) { builder.Append("x"); }
```

### AsyncExclusiveLock (mutex)

```csharp
var gate = new AsyncExclusiveLock();
await gate.AcquireAsync(token);      // ValueTask, no thread block
try { /* critical section */ }
finally { gate.Release(); }
```

Also: `AcquireAsync(TimeSpan, token)` (throws TimeoutException), `IsLockHeld`,
`DisposeAsync()`, `ConcurrencyLevel`/`HasConcurrencyLimit` for wait-queue caps (11th waiter throws
ConcurrencyLimitReachedException), diagnostics (`LockContentionCounter`), debugging (`TrackSuspendedCallers`).

### Other primitives (same family, DotNext.Threading)

- AsyncReaderWriterLock / AsyncSharedLock — reader/writer with async acquisition.
- AsyncSemaphore, AsyncExchanger, AsyncCountdownEvent, AsyncBarrier.
- QueuedSynchronizer<TContext> — base class for custom async sync primitives.

## 6.6.0 additions (the reason the pin matters)

### AsyncMulticastSequence<T> — async broadcast channel (single producer, N consumers)

```csharp
var channel = new AsyncMulticastSequence<WatchEvent>();
// producer:
var notified = await channel.WriteAsync(evt, token);   // returns listener count
channel.TryComplete();                                  // signal end (+ optional exception)

// consumer (each subscriber calls GetAsyncEnumerator):
await foreach (var evt in channel) { ... }
```

- Implements IAsyncEnumerable<T>; each consumer creates its own enumerator.
- `NotifyListenersSequentially` (init): true = Current doesn't ack, MoveNextAsync does; false = Current acks
  immediately.
- `IsCompleted`, `TryComplete(Exception?)`.
- Natural fit for the watch pipeline's current `Channel<WatchEvent>` fan-out if multiple consumers appear (today it's
  single-consumer, so Channel stays the simpler shape — the
  "ask if a simpler shape would do" invariant applies).

### Listen — background IAsyncEnumerable consumption

`Listen` extension method consumes an IAsyncEnumerable<T> in the background (fire-and-forget loop with cancellation) —
e.g. draining a channel/multicast sequence without an explicit await foreach in a hosted service.

## Applicability in this repo (measured 2026-08-06)

- Watch pipeline: `Channel<WatchEvent>` + `lock (_gate)` + SemaphoreSlim per-project gates (WatchScheduler). DotNext
  async locks COULD replace the lock+sema mix, but the current shape works and tests are green — only reach for DotNext
  where it buys something (allocation-free contended locking, multicast fan-out, queue caps).
- No existing code uses the async-lock primitives yet — they're available on the pin.

## Pitfalls

- Async locks are not reentrant — deadlock on recursive acquisition (hard to diagnose; the docs warn explicitly).
- Do not mix blocking and async facades over the same object except SemaphoreSlim.
- `AcquireAsync(timeout)` throws TimeoutException; `AcquireAsync(token)` throws OperationCanceledException /
  PendingTaskInterruptedException.
- API docs live at https://dotnet.github.io/dotNext/api/ — the features pages under /features/core/ and
  /features/threading/ (some deep links 404; the API pages are reliable).

## Verification

- Package present: `grep DotNext Directory.Packages.props` → 6.6.0 both.
- Usage compiles against the pinned versions; no version drift allowed without a Directory.Packages.props change.
