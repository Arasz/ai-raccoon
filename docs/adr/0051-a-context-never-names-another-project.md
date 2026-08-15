# 0051. A context never names another project, on any path

Date: 2026-08-14

Status: Accepted

## Context

`7698dc63` (2026-08-13) fixed a write that named another project: `EntryBucket.For` now throws
`ContextOutsideProjectException` when a `project:` or `label:` context names a project other than the
caller's, and `MemoryStoreContextScopeTests.AddContentAsync_NamingAnotherProjectInTheContext_WritesNothing`
asserts it.

That fix was applied to the function where the bug was found, not to the rule. The **delete** path has
its own copy of the same context-to-rows mapping — `FilterFor` in `SqliteMemoryStore`, roughly a
thousand lines away in a different file — and it had neither the check nor a test. Worse, its
`project:` branch bound `project_id` from the *caller-supplied context string* rather than from the
caller's `projectId`, so the argument the access gate authorised was discarded, and its `shared` branch
carried no project predicate at all.

The 2026-08-14 project-scope review's adversarial pass exploited both against a real server. From a
project named `attacker`, at access mode `full`:

```
memory_delete_context {projectId: "attacker", context: "project:victim"} -> {"deleted": 2}
memory_stats victim   (before) -> {"entries":2}   (after) -> {"entries":0}
memory_stats attacker (after)  -> {"entries":1}   # the attacker's own row untouched
```

and the same call with `context: "shared"` destroyed the cross-project tier. The precondition was met
in production: `access.mode.global` on the deployed bank is `full`, and `memory_sweep` requires it.

The gate itself was never broken — at the default `rw` the same call is correctly refused. What was
broken is that **an invariant was ratified and tested on one side of a two-sided rule.**

## Decision

**One function decides whether a context stays inside the caller's project, and every path that
accepts an untrusted context calls it.**

`ContextScope.RequireWithinProject(context, projectId)` lives in `AiRaccoon.Core.Memory` and throws
`ContextOutsideProjectException` when the context names another project. The shared tier belongs to no
project, so it is outside every one.

- `EntryBucket.For` (write) calls it for `project:` and `label:`.
- `SqliteMemoryStore.DeleteContextAsync` calls it, which also refuses `shared`.
- `FilterFor` moved out of `SqliteMemoryStore` to `ContextFilter.cs`, beside `EntryBucket` — the two
  halves of one rule no longer sit in different files. It **maps; it does not authorise**, and its
  `project:` branch now binds the caller's id, so the surprising line is gone at the root as well as
  guarded above it.

**A direct `memory_write` to `context: "shared"` is deliberately still allowed.** It is a separate
question — whether the shared tier should be reachable outside `memory_share`'s review pipeline — and
it is an owner decision, not a security fix. `EntryBucket.For` says so at the branch.

**`FilterFor`/`ContextFilter.For` is not itself a guard, and must not become one.** Its other two
callers pass contexts they built themselves, and one of them — `SweepService` — legitimately passes
`shared`. Putting the refusal inside the mapping would break the sweep. Authorisation belongs at the
entry point that accepted the untrusted string.

## Consequences

- `memory_delete_context` refuses a foreign `project:` context and refuses `shared`, at every access
  mode. A caller can still delete its own project context, its own label contexts, and its own
  workspaces.
- `ContextOutsideProjectException`'s message generalised from "A write may only target its own project"
  to "An operation may only target its own project" — it is no longer write-only.
- The `SqliteMemoryStore` size ratchet caught the guard at 1298 lines against a 1291 cap. Its own note
  said to take one of the remaining seams rather than raise it; the delete seam came out and the cap
  was **lowered to 1251** with the history recorded. Four seams remain: write, search, ingest,
  embedding.

## What this does not fix

Recorded so it is not read as broader than it is.

- **Access mode is still not an authorization boundary.** It resolves the mode of the project the
  caller *names*; there is no caller identity anywhere in `IMemoryAccessGuard`. This ADR stops a
  context from re-targeting an operation; it does not stop a caller from naming any `projectId` it
  likes. That is a design change, not a patch.
- **`memory_promotion_list` with no `projectId` still skips the gate entirely** and returns every
  project's queued content.
- **`memory_sync` still uploads the whole bank**, every project, regardless of the `projectId` it is
  given.

## Evidence

- Exploit and refusal: `docs/reviews/2026-08-14-project-scope-review.md`, "Review integrity".
- Red-first: `DeleteContextAsync_NamingAnotherProject_DeletesNothing` and
  `DeleteContextAsync_NamingTheSharedTier_DeletesNothing` both failed with "should throw
  `ContextOutsideProjectException` but did not" before the change, with
  `DeleteContextAsync_WithTheCallersOwnProjectContext_Deletes` passing throughout.
- `src/AiRaccoon.Core/Memory/ContextScope.cs`, `src/AiRaccoon.Infrastructure/Sqlite/ContextFilter.cs`,
  `src/AiRaccoon.Infrastructure/Sqlite/EntryBucket.cs`,
  `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` (`DeleteContextAsync`).
