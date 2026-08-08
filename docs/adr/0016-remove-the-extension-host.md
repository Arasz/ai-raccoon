# 0016 — Remove the extension host

Date: 2026-08-08

Status: Accepted

## Context

`MemoryExtensionHost` (`src/AiRaccoon.Core/Rating/MemoryExtensionHost.cs`) decorates
`IMemoryStore` and dispatches four hooks — `OnWriteAsync`, `OnSearchAsync`, `OnDeleteAsync`,
`OnSourceChangedAsync` — to every registered `IMemoryExtension`. ADR-0013 already cut the
hook surface from the spec's original five hooks to these four, on the grounds that the
other two were declared but undispatched. The four that remained are dispatched, but
production has exactly one registered extension, `RetrievalRatingExtension`
(`src/AiRaccoon.Infrastructure/Rating/RetrievalRatingExtension.cs`), and every one of its
hook implementations is `Task.CompletedTask` — self-documented as "kept registered to
preserve the extension-host architecture," an interface obligation with no behavior behind
it. The rating pipeline it was meant to carry already moved on-row, inside
`SqliteMemoryStore.SearchAsync`.

`WatchDigestExecutor` genuinely calls `OnSourceChangedAsync` on every digest (post-hash-skip,
pre-ingest, and on delete). But `RetrievalRatingExtension` does not override it — it inherits
`IMemoryExtension`'s default no-op body. So the one dispatch site that is actually reachable
in production fans out to zero behavior: a real call into an interface method that does
nothing, every time a watched file changes. Reachable and dispatched is not the same as
doing anything — nothing here needs to be re-homed, because nothing here has behavior to
carry forward.

Three more properties of the current design compound the case for removal rather than
partial trim:

- **The "every client is bound through the decorator" premise is already false.**
  `CliCommandRunner.cs:35` builds `new SqliteMemoryStore(...)` directly and hands it to
  `ConfigCommands.RunAsync` — the CLI path never goes through `MemoryExtensionHost` at all.
  Only the MCP server's DI graph (`Dependencies.cs`) wires the decorator in.
- **Test wiring already treats the host as ceremony, not a real seam.** Across the watch
  test stacks (`WatchTestFakes.cs`, `WatchIntegrationTests.cs`, `FileWatcherFeatureContext.cs`),
  the same object is constructed once and passed twice to `WatchDigestExecutor` — once as
  `IMemoryStore store`, once as `MemoryExtensionHost extensionHost` — because there is
  nothing distinct for the second parameter to be.
- **The spec's third-party extensibility story was never built.** Spec §6.2 promises
  assembly-scanned pickup (`WithExtensionsFromAssembly`, mirroring the SDK's
  `WithToolsFromAssembly`) so an external implementer of `IMemoryExtension` could register
  itself. That method does not exist anywhere in `src/`; the extension list is hardcoded in
  `Dependencies.cs` to the one first-party extension. No external implementer can exist
  today, so "extensibility" describes a closed set of one inert class.

## Decision

Delete the extension host entirely: `IMemoryExtension`, `MemoryExtensionHost`,
`RetrievalRatingExtension`, and the `SourceChangedContext`/`SourceChangeKind` types that
existed only to feed `OnSourceChangedAsync`. `WatchDigestExecutor` drops its
`OnSourceChangedAsync` call sites along with the `MemoryExtensionHost` constructor
parameter and the `ToKind` helper that translated `WatchEventKind` into the
now-deleted `SourceChangeKind`. DI (`Dependencies.cs`) registers `IMemoryStore` as
`SqliteMemoryStore` directly:

```csharp
services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());
```

`RatingPolicy` (`src/AiRaccoon.Core/Rating/RatingPolicy.cs`) stays — it backs the on-row
rating computation in `SqliteMemoryStore.SearchAsync`/sweep, which is unrelated to the hook
pipeline being removed.

No callback, event, or `Action` constructor parameter replaces `OnSourceChangedAsync` in
`WatchDigestExecutor`. The dispatch loop had exactly zero live consumers; adding a narrower
seam in its place would be building an abstraction before any caller needs it — the thing
this repo's own "ask if a simpler shape would do" invariant rules out.

This supersedes ADR-0013 in full: the four-hook surface ADR-0013 narrowed the spec down to
no longer exists, so there is nothing left for that hook surface to be a decision about.
ADR-0013 is not edited — ADRs are immutable per `docs/adr/README.md` — this entry
supersedes it going forward. This also reverses `spec-issue-1.md` §6.2 ("Extension pipeline
(the extensibility layer)"): the interface, the host, the first-party extension, and the
assembly-scanning promise it describes are all gone. The spec is not edited after the fact
in this repo, consistent with how ADR-0013 already handled the same situation.

## Consequences

- **Negative:** if a future feature genuinely needs to observe every write/search/delete
  (or a source-change event) across all store implementations, that seam has to be rebuilt
  from scratch, including a real dispatcher and a real second consumer to prove out the
  ordering contract — there is no vestigial interface left to hang a new hook off of.
- **Positive:** deletes an interface, a decorator class, a first-party implementer that did
  nothing, two DTO/enum types, and every test fixture that existed solely to construct or
  assert against them (`MemoryExtensionHostTests.cs` in full, the `HookedStore` branch and
  `RecordingExtension` in `NativeMemorySteps.cs`, `RecordingExtension`/`Extension` in
  `WatchTestFakes.cs`, the hook-only assertions and two hook-only tests in
  `WatchDigestExecutorTests.cs`). `IMemoryStore` now has exactly one production
  implementation reachable from every client (MCP and CLI alike), closing the gap the
  Context section identified.
- **Neutral:** the two `MemoryExtensionHostTests.cs` cases that pinned "sweep and
  consolidation delete through `IMemoryStore`, not around it" are not lost — they are
  rehomed as `SweepServiceTests`/`WorkspaceServiceTests` cases against a recording
  `IMemoryStore` stub, because that layering invariant has nothing to do with the hook
  pipeline being removed.

## Alternatives rejected

- **(a) Keep `OnSourceChangedAsync` behind an event/callback/`Action` parameter on
  `WatchDigestExecutor`, dropping only the rest of the interface.** Rejected — the ruling
  this ADR executes is explicit that this is an abstraction with no buyer: the sole
  registered extension never overrides the hook, so there is no current behavior to
  preserve a narrower seam for. Building the seam anyway is speculative.
- **(b) Trim `IMemoryExtension` further but keep the host and the three still-dispatched
  hooks (`OnWriteAsync`/`OnSearchAsync`/`OnDeleteAsync`).** Rejected — `RetrievalRatingExtension`
  is a no-op on all three; keeping the host keeps the CLI/MCP asymmetry (Context, bullet 1)
  and the double-parameter test ceremony (Context, bullet 2) alive for hooks nothing
  implements.
- **(c) Build `WithExtensionsFromAssembly` now, to make the third-party extensibility story
  real instead of removing it.** Rejected — no third-party extension exists to pick up, and
  no caller has asked for one; building the scanning mechanism speculatively repeats the
  same over-engineering this ADR is removing, just at the DI layer instead of the hook
  layer.

## Evidence

`src/AiRaccoon.Infrastructure/Rating/RetrievalRatingExtension.cs:14-18` (`OnWriteAsync`/
`OnSearchAsync`/`OnDeleteAsync` all `Task.CompletedTask`) and its absence of an
`OnSourceChangedAsync` override (inherits `IMemoryExtension`'s default no-op) — the sole
extension never overrides the one hook the production dispatch loop actually calls, so that
dispatch loop is a no-op end to end; `src/AiRaccoon.Infrastructure/Watch/WatchDigestExecutor.cs:37-39,55-57`
(the two live `OnSourceChangedAsync` call sites); `src/AiRaccoon/Setup/CliCommandRunner.cs:35`
(`new SqliteMemoryStore(...)` built directly, bypassing the decorator — "every client is
bound through the decorator" is already false); `tests/AiRaccoon.Tests/Unit/Watch/WatchTestFakes.cs:18-19`,
`tests/AiRaccoon.Tests/Integration/WatchIntegrationTests.cs:577,580`,
`tests/AiRaccoon.Tests/BDD/FileWatcherFeatureContext.cs:211,214` (the same object passed
twice to `WatchDigestExecutor`, once as `IMemoryStore`, once as `MemoryExtensionHost`);
`docs/work/features-agent-memory/spec-issue-1.md:302-320` (§6.2's `WithExtensionsFromAssembly`
promise, never implemented anywhere in `src/`); `docs/adr/0013-extension-host-hook-surface.md`
(the hook surface this ADR supersedes in full).
