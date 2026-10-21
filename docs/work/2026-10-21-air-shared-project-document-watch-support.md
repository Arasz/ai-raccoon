# Shared project document watches

**Date:** 2026-10-21
**Task:** `air-shared-project-document-watch-support`
**Branch:** `task/air-shared-project-document-watch-support` → PR #906
**Release:** 1.62.0

## What it does

A documents directory need not belong to a repository, and a minted project id makes it private
by default. The exact, case-sensitive id `shared` is now a built-in document target in one bank:
it needs no minted id and no registration row, its documents land in the existing shared tier
(`scope='shared', project_id='shared'`), and every project's default search reads them. An
explicit `scope=project` search still reads the project only.

The target supports document ingestion and watches, nothing else: it refuses a project-only
search, custom context labels, workspaces, note writes and the promotion/sweep operations, and it
skips code files rather than stranding them as private code rows. Watches keep the normal
enablement, path-allowlist and access-tier checks, and removing a watch retains the indexed
documents.

A pre-existing private owner under the literal id `shared` — a registration, private rows, code
rows, workspaces, or a legacy watch that was never activated — blocks the target with a
`shared-project-conflict` refusal instead of publishing that data. The machine-local setting
`shared.documents.watch-owner` records that the reserved target owns the shared watch rows, so an
upgrade never silently adopts a legacy watch.

The contract is ADR-0137; the operator steps are in
[docs/reference/agent-memory-server.md](../reference/agent-memory-server.md#documents-shared-across-projects);
the research that produced the design is
[2026-10-21-shared-project-watches-research.md](2026-10-21-shared-project-watches-research.md).

## The scope seam

The target's rules — the id it owns, the context its rows live in, the operations it refuses —
started as a static helper (`SharedProject`) called from twenty places, one per component that
routes a write or a read. They now sit behind one contract, `ISharedProjectScope`
(`src/AiRaccoon.Core/Projects/ISharedProjectScope.cs`), with one implementation,
`SharedProjectScope`, resolved by the composition root. The store, the ingestor, the watch store,
the watch digest, the workspace service, the project directory, the repair, the search dispatcher,
the write service, the tool gate, the registration guard and the cwd resolver all take the scope
as a **required** constructor parameter.

Two decisions inside that seam are worth keeping:

- **The dependency is required, never nullable with a fallback.** A nullable parameter with a
  default instance lets a component be built without a scope and quietly route on the fallback —
  the contract becomes decoration in exactly the case it exists for. The tests that build a
  component by hand pass the scope explicitly (`TestData.SharedScope`); the composition root
  supplies the registered implementation.
- **The row routing goes through the contract too.** The ingestor and the search-context resolver
  ask the scope which context an id's rows live in, so the reserved target's routing has one home.
  A mutation check proves the seam is load-bearing: making `ContextFor` return the project context
  for the reserved id turns six tests red, and only one before the routing was moved.

## Evidence

| Gate | Result |
|---|---|
| `dotnet build` | clean, 0 warnings |
| Fast suite (`Speed=Fast&Performance!=Benchmark`) | 5090 tests, 0 failed |
| Slow suite (`Speed=Slow&Performance!=Benchmark`) | 1592 tests, 0 failed |
| CI `build-fast` / `build-bdd` / `build-slow` on the head | green |
| The retry-surface gate | green (it was the red build-fast on the first push) |
| Mutation: the reserved id routes to its own project context | 6 tests red, then green again |

### The review round, and why the first four attempts were empty

Four delegated reviews came back with no answer at all: the child received the prompt, started an
assistant turn and ended it with an empty message — zero tokens, no error, exit 0. The cause is
not the model id. The child argv carried a valid `--model` in every case
(`openrouter/anthropic/claude-opus-5.5`, `openrouter/openai/gpt-5.6-sol`, `openai/gpt-6.1-sol`),
and the *models* endpoint answered 200. A **chat completion** answered `HTTP 402 — Insufficient
credits`: the OpenRouter account has no credits, so the child's model call fails and the turn ends
empty. The `openai` provider has no key in the environment at all. The repository's own
`claude-review` workflow skipped for the same reason class — "set CLAUDE_CODE_OAUTH_TOKEN or
ANTHROPIC_API_KEY", no credential configured for the repo.

Two details worth keeping, because both look like the cause and are not:

- **The child's raw stdout is empty in every run, including the successful one.** The harness
  rebuilds the transcript from the child's own session record, so an empty stdout is normal here.
  The failure signal is the *empty assistant message*, not the empty stdout.
- **The bare `opus` alias fails the model-id regex**, so the harness refuses to emit it and falls
  back to the resolved full id — visible in the argv of the failing runs. That refusal is a
  separate harness-side wart, not what killed the child: the emitted id was valid every time.

Pinning the child to the **radius** provider (`radius/claude-opus-5-5`) fixed it: the review ran
and returned a verdict. A repository whose review gate depends on a model call needs a credential
that a child process can actually use — that is the operational finding, and it is worth fixing
before the next PR blocks on it.

### What the review said

The independent reviewer read the branch and returned **no blocker — safe to merge**. It found
the routing sound (`ContextFor` called from the ingestor, the store and the search-context
resolver; `IsReservedId` at the routing points; no bypass; no private-row leakage), the layer
clean (Core depends only on `AiRaccoon.Core.Memory`), and no false-pass test in the shared suite.
One minor finding: the refusal test asserted the exception *type* but not the refusal *message*.
That is now fixed — `SharedProjectScopeAndWorkspaceSearch_AreExplicitlyUnsupported` asserts
`shared-project-unsupported` in both refusals.

The in-session review covered the same ground: the shared target's
routing, refusals, delete scoping (`project_id`-scoped, so a shared watch never removes another
owner's rows), the DI seam and the layering were each checked against the source, and the
mutation check above stands in for the "watch the gate go red" discipline.

## Still open

- **The merge needs one approving review.** The `default` ruleset on `main` requires one
  approving review on the latest push, and no reviewer can run here (see above), so PR #906 sits
  ready for review with green CI. One approval (the owner's, or a configured bot) unblocks
  `gh pr merge 906 --squash`.
- The reserved target is deliberately narrow in this delivery: no owned-only shared query, no
  shared workspace, no document formats beyond Markdown, plain text, JSON and notebooks, and no
  implicit selection from the working directory. Each of those is a separate contract.
