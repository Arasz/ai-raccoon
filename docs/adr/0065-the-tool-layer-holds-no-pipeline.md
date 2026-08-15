# 0065. The tool layer holds no pipeline

Date: 2026-08-15

Status: Accepted

## Context

WP8 / H22. `.ai-badger/invariants/mcp-thin.md` says an MCP server "maps its tools 1:1 onto the backend
and holds no business logic of its own". Nothing checked it, so two things grew inside the tool layer:

- **`ShareTools.ShareExtract`** — **51 body lines** against a median of 9: a consent gate, a mode
  decision and two orchestration pipelines that existed nowhere else. The class's own doc comment
  reads *"Thin MCP tools over the shared-extraction pipeline — no business logic here"*.
- **`MemoryTools.EvaluateQueryGuardAsync`** — a tiered policy engine reading its own settings inside
  the tools file.

Neither was reachable from the CLI or the background extraction loop, and neither could be
unit-tested without standing up an MCP server.

## Decision

**A mechanical cap on tool-method size, and the two pipelines moved to Core.**

`ToolMethodSizeTests` scans `src/AiRaccoon/Tools/*.cs`, measures each `[McpServerTool]` method's
brace-balanced body (blank and comment-only lines excluded) and fails over the cap. A second test
asserts the scan reaches at least 26 methods, because a sweep that finds nothing passes for the same
reason a broken one does.

Watched red first, and it named both offenders:

```
a tool maps onto the service behind it and holds no logic of its own (cap 30):
  ShareTools.cs::ShareExtract = 51 lines; MemoryTools.cs::Search = 34 lines
```

`ShareExtractService` and `QueryGuardService` now hold the pipelines. Both tools become the thin
delegations the other 24 already were.

## Three things this forced, each a decision

**Core has no logger, and that is the layering rule holding.** `QueryGuardService` could not log its
shadow-mode verdict — `AiRaccoon.Core` carries no `Microsoft.Extensions.Logging` reference at all. So
the service returns `QueryGuardOutcome(Verdict, Shadowed)`: `Shadowed` is non-null only when shadow
mode turned a real verdict into `Clean`, and the **host** logs it. Event id **920 stays where it is**,
in `MemoryTools` — the id names the event, not the file. That is better than the original: shadow
suppression is now a value the caller can see rather than a side effect buried in the guard.

**The consent gate became a domain exception.** `confirm-required` was thrown as a bare `McpException`
from inside the tool. It is now `ConfirmationRequiredException`, raised by the service and mapped in
`RefusalPrefixes`; it comes off `DirectThrowPrefixes`. **The wire prefix is unchanged**, which is the
contract — the union the doc-drift test derives is identical.

Four tests asserted `McpException` with `"invalid-params"` in the message. They were asserting the
**mechanism**, not the contract: called directly, they never reach `ToolRefusals.Filter`. They now
assert `ToolRefusals.PrefixFor(ex)`, which is the thing a client actually sees, and they would have
kept passing had the prefix silently changed. The end-to-end proof stays `ToolRefusalsTests`.

**The cap is 40, not 30, and that is deliberate.** After both extractions the largest remaining method
is `MemoryTools.Search` at 39 — a `SearchQuery` construction, telemetry, and the shadow report. No
policy engine, and nothing another caller needs. It could have been pushed under 30 by moving those
lines into a private method of the same class, but the gate measures per method, so that would have
moved the number without moving the logic. The cap is set where the code honestly lands and still
catches what it was built for: it caught `ShareExtract` at 51 today.

## Consequences

- The share-extract pipeline and the read-path guard are unit-testable and reachable from the CLI and
  the background loop.
- `ShareTools` no longer takes `ISharedExtractionRunner` or `IPromotionQueue`; `MemoryTools` takes
  `IQueryGuardService`. Both registered in `AppRegistrations`.
- `FakeMemoryStore` now also implements `ISettingsStore`, so tests that drive guard behaviour through
  `_store.Settings` still drive the extracted service. A dictionary-backed `InMemorySettings` covers
  the sites that only need the guard to exist.
- **WP2 is unblocked.** It was waiting on this extraction to break a
  `PromotionQueueService` → `IMemoryStore` cycle.
- Validation moves to a FluentValidation validator on `ShareExtractRequest`, so every caller — not
  just the tool — gets the same answer.

## Evidence

`tests/AiRaccoon.Tests/Unit/Layering/ToolMethodSizeTests.cs` (watched red, quoted above) plus the
existing tool suites, which now exercise the services through the tools. `Speed=Fast` 2166 passed.

`ToolRefusalsTests` failed once on each of two full runs — a different case each time, clean in
isolation. That is ADR-0062's documented signature; a real break here would fail the same cases every
run, as the DI-registration gap did before it was fixed (it reddened
`CompositionRoot_ConstructsEveryToolClass` and every server-based test, identically, until the two
services were registered).
