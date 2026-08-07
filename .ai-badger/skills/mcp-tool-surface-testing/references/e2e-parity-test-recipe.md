# E2E tool-surface parity test recipe (xunit, over the wire)

Proven on the project 19/19 parity (`tests/the project.Tests/E2E/McpServerToolSurfaceE2ETests.cs`,
2026-08-06). Use when a repo wants a PERMANENT "every MCP tool answers over the wire"
regression test — the automated sibling of the manual surface audit in the parent skill.

## Harness (copy the repo's existing E2E class)

Same `McpServerFactory` + `McpClient` harness as the sibling E2E suite:

- `[Trait(Category, E2E)]`, `[Trait(Speed, Slow)]`, `[Collection(E2ETestCollection.Name)]`
  (collection = serial; the suite mutates env/data-root), `IAsyncLifetime`.
- `InitializeAsync`: `TestData.CreateBundledModel().EnsureAsync(...)` +
  `FakeEmbeddingEndpoint.StartAsync(...)` + `new McpServerFactory()` +
  `await _factory.CreateClientAsync()`.
- `CallAsync` helper → `_client.CallToolAsync(tool, dict, null, null, ct)`.
- `Text(result)`: `result.IsError.ShouldNotBe(true)` + concat `TextContentBlock`s.

## Two facts cover the whole surface

1. **tools/list exact set**:
   ```csharp
   var tools = await _client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, ct);
   names.OrderBy(n => n).ShouldBe(ExpectedToolNames.OrderBy(n => n));
   ```
   `OrderBy`-both-sides gives count + containment in one assertion (exact 19/19) with
   no dependency on Shouldly's `ignoreOrder` overload.
2. **Round-trip the tools the sibling suite does NOT cover** — read the sibling E2E
   file first and subtract. Dedicated project id (e.g. `"surface-test"`), temp dirs for
   ingest/watch, `delete_context` the project as the FINAL cleanup (also wipe rows you
   wrote mid-test so later assertions stay deterministic).

## Pitfalls that cost real iterations

- **`McpClient.ListToolsAsync(null, ct)` is AMBIGUOUS (CS0121).** Two overloads:
  `(RequestOptions?, CancellationToken)` and `(ListToolsRequestParams, CancellationToken)`.
  Fix: cast — `(ModelContextProtocol.RequestOptions?)null`. `CallToolAsync` has no such
  problem (string overload wins).
- **Watch tools fail `watching-disabled` until config is seeded.** Watching defaults
  OFF; scope defaults empty. Before `memory_watch_add`, seed per-project settings into
  the server's store (open a second `SqliteMemoryStore` on `_factory.DataRoot` while
  the server runs — same pattern the sibling suite's config-CLI helper uses):
  `watch.enabled.{projectId}` = `"true"`, `watch.scope.{projectId}` =
  `WatchConfigKeys.SerializeScope([tempDir])` (JSON array of normalized paths; scope
  entry must be an ancestor-or-equal of the watched path).
- **Watch status is background-timed: assert tolerantly.** Right after add the state is
  `Scanning` or `Healthy` — never a specific state. Compare case-insensitively
  (`ToLowerInvariant()` ∈ {scanning, healthy}) because the enum's JSON casing depends on
  the serializer's `JsonStringEnumConverter` setup. Never assert timestamps.
- **Sweep dry-run (default)**: assert shape only —
  `root.GetProperty("candidates").ValueKind == JsonValueKind.Array` and same for
  `deleted`. Fresh rows are never candidates (below threshold + older than TTL), so
  don't assert non-empty.
- **Delete round trips**: `memory_delete` by hash → `{"deleted":1}`;
  `memory_delete_context` by `"project:{id}"` → `{"deleted":1}`. Write the row first.
- **Ingest**: `memory_ingest_file` → `{"indexed":1}`, `memory_ingest_directory` →
  `{"scanned":1}`. Temp files/dirs in `try/finally`; `Directory.CreateTempSubdirectory`
  for dirs.

## If a round trip is flaky in-run

Drop the flaky tools from the parity test, note it in the report, and rely on their
unit/BDD suites. A parity test that intermittently fails is worse than one that covers
16/19 — it poisons every future CI run. (the tool watch trio was stable; the flaky
tests observed were unrelated fixed-port-bind and fake-timer BDD races.)

## Full-suite proof discipline

- Filtered run green first, then full suite; a full-suite failure right after a build
  can be transient (see `artifact-verification` skill — re-run once before treating as
  real; two consecutive green runs is the confirmation pattern).
- In a shared worktree with concurrent sessions: `git add <your files only>` (never
  `git add -A`), and after committing, verify your commits survived at HEAD
  (`git log --oneline -3 -- <file>`) — other sessions may rebase/amend the branch
  mid-task (observed: commit hashes changed under a live session).
