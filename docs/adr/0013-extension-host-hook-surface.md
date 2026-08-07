# 0013 — Extension host hook surface: drop OnSweepAsync and OnConsolidateAsync

Date: 2026-08-07

Status: Accepted

## Context

`docs/work/features-agent-memory/spec-issue-1.md` §6.2 specifies `IMemoryExtension` with
five hooks: `OnWriteAsync`, `OnSearchAsync`, `OnDeleteAsync`, `OnSweepAsync`, and
`OnConsolidateAsync`. Two of those five were never wired up:

- `DegradationExtension`, the spec's intended consumer of `OnSweepAsync`, was never built —
  sweep policy instead lives in `SweepService`
  (`src/AiRaccoon.Infrastructure/Degradation/SweepService.cs`), which calls `IMemoryStore`
  directly and never constructs a `SweepContext`.
- `StackPromotionExtension`, the spec's intended consumer of `OnConsolidateAsync`, is
  explicitly marked deferred in the spec's own words: "Not built in V1 — the seam exists,
  the policy doesn't." No `ConsolidationContext` is constructed anywhere in `src/`.

`MemoryExtensionHost` (`src/AiRaccoon.Core/Rating/MemoryExtensionHost.cs`), the sole
implementer of `IMemoryStore` that runs extension hooks, has no dispatcher for either
hook — no code path can ever call `OnSweepAsync` or `OnConsolidateAsync`. The spec also
promises third-party extension pickup via assembly scanning (`WithExtensionsFromAssembly`,
§6.2), but that method does not exist anywhere in the repo: the extension list is
hardcoded in DI (`src/AiRaccoon/Setup/Dependencies.cs:54-56`, currently just
`RetrievalRatingExtension`). So even a correctly-implemented third-party extension has no
way to register itself, let alone reach these two hooks.

The sole production implementer, `RetrievalRatingExtension`
(`src/AiRaccoon.Infrastructure/Rating/RetrievalRatingExtension.cs`), implements both hooks
as no-ops, self-documented as "kept registered to preserve the extension-host
architecture" — an interface obligation with no behavior behind it.

## Decision

Shrink `IMemoryExtension` to the four hooks the host actually dispatches:
`OnWriteAsync`, `OnSearchAsync`, `OnDeleteAsync`, and `OnSourceChangedAsync`. Delete
`OnSweepAsync`, `OnConsolidateAsync`, and their `SweepContext`/`ConsolidationContext`
records.

Sweep and consolidation policy continue to live where they already live —
`SweepService` and `WorkspaceService` — and both already delete through `IMemoryStore`,
which DI binds to `MemoryExtensionHost` (`Dependencies.cs:57`). `SweepService.SweepAsync`
deletes via `IMemoryStore.DeleteAsync`; `WorkspaceService.ConsolidateAsync` and
`DiscardAsync` delete via `IMemoryStore.DeleteContextAsync`. Both already fire
`OnDeleteAsync` on every registered extension. An extension that wants to observe
forgetting driven by either sweep or consolidation still can, through the hook that
already exists and already fires.

## Consequences

- **Negative:** a future degradation or stack-promotion extension that needs to see sweep
  candidates or consolidation outcomes *before* deletion (e.g. to veto or reshape a
  candidate set, not just observe the aftermath) must re-add the hook, its dispatcher in
  `MemoryExtensionHost`, and `WithExtensionsFromAssembly` for third-party pickup — all
  three were always required for the seam to be reachable, and none of them existed.
- **Neutral:** supersedes spec §6.2's five-hook list; the spec continues to describe
  `DegradationExtension` and `StackPromotionExtension` as hook consumers, which is now
  documented here as superseded rather than corrected in place (specs are not edited after
  the fact in this repo).

## Alternatives rejected

- **(a) Wire both hooks up.** Building the `MemoryExtensionHost` dispatchers and the
  `DegradationExtension`/`StackPromotionExtension` consumers would require inventing merge
  semantics that don't exist yet: how would an extension-proposed sweep candidate set
  combine with `DegradationPolicy`'s own candidate selection? Rejected — that's unspecified
  product policy, not a mechanical wiring fix, and no caller needs it today.
- **(b) Leave the hooks declared but undispatched, as-is.** Rejected — a seam nothing can
  reach, documented as if it works, is documentation that lies. It costs every future
  implementer of `IMemoryExtension` a no-op method they must write and maintain for no
  behavior.

**Evidence:** `src/AiRaccoon.Core/Rating/MemoryExtensionHost.cs` (no `OnSweepAsync`/
`OnConsolidateAsync` dispatcher); `src/AiRaccoon.Infrastructure/Rating/RetrievalRatingExtension.cs:6-9,21-25`
(sole implementer, both hooks no-ops, self-documented as vestigial);
`src/AiRaccoon/Setup/Dependencies.cs:54-57` (hardcoded extension list, no assembly
scanning); `src/AiRaccoon.Infrastructure/Degradation/SweepService.cs:56`
(`store.DeleteAsync`); `src/AiRaccoon.Infrastructure/Workspace/WorkspaceService.cs:65,79`
(`store.DeleteContextAsync`); `docs/work/features-agent-memory/spec-issue-1.md:302-320`
(hook list, `WithExtensionsFromAssembly`, and the "Not built in V1" deferral).
