# Tool-surface test → fix loop: verified patterns (2026-08-06, the project 21-tool surface)

Session detail behind the mcp-tool-surface-testing fix-loop section. Both PARTIALs from the
full-surface test were fixed same-day with this sequence; the delete-scope findings were the
answer to an owner question.

## Response-shape fixes (TDD, both RED first)

1. `memory_list` returned `files` as a stringified JSON string.
   Fix: `ListResult(string Files)` → `ListResult(JsonNode Files)`; the tool parses the store's
   JSON tree (`JsonNode.Parse(files) ?? new JsonObject()`). Tool-layer change only — the
   store interface (`ListFilesAsync` → `Task<string>`) stayed untouched, which kept ~10
   fakes/call-sites compiling. New unit tests: `List_ReturnsFilesAsJsonObject`,
   `List_PreservesNestedFileTree` (fake store with settable `FilesJson`).
2. `memory_workspace_discard` returned `{deleted: n}`; docs + sibling `consolidate` say
   `discarded`. Fix: dedicated `WorkspaceDiscardResult(int Discarded)` record (DeleteContext
   keeps `DeletedContextResult`). New unit test `WorkspaceDiscard_ReportsDiscardedKey`; E2E
   pin `Worktree_Discard_RemovesTheOutbox` updated `"deleted":1` → `"discarded":1`.

## Pitfalls hit while fixing

- **Access guard denies destructive tools in unit tests unless full mode is seeded.** Default
  project mode is rw; `WorkspaceDiscard` (AccessRequirement.Destructive) throws
  `access-denied: memory_workspace_discard requires mode full (current rw)` from the guard,
  not your test. Seed `_store.Settings[AccessModePolicy.ProjectSettingKey("acme")] = "full"`
  like the sibling consolidate test does.
- **Same-message test file, two writers:** another session's patch, applied against a stale
  copy, silently removed a seed line I had just added (the file showed a patch-conflict-free
  tree but my line was gone). Re-read the region after every patch in a shared checkout; a
  test failure that contradicts what you just wrote = check the file first. Their commit also
  landed mid-verification (naming refactor on top of my commits) — `git diff` empty afterwards
  because their commit absorbed the working-tree delta; re-check `git log`/`git status` before
  concluding your work is uncommitted or clobbered.
- **`set -e` + `| grep` verification script runs cleanup after failures:** grep exits 0 (it
  matched the summary lines) so the script proceeded to `rm` the temp probe file even though
  the test run had failed — losing the probe mid-debug. Use `set -o pipefail` or separate the
  cleanup step.
- **Probe compile errors are iterative** (missing `TestData.CreateTempRoot` —
  it's `TestData.` static, not a local; nested `StubChunker` needed per test class;
  `SyncService` is a concrete class — subclass it like `FakeSyncService` with `null!` factory
  args). Budget a couple of compile-fix rounds before the probe passes.

## Delete-scope semantics (the "is delete project-only?" answer)

- `memory_delete(projectId, hash)` = `DELETE FROM entries WHERE hash = @hash AND project_id =
  @projectId` (MemorySql.cs). Project-scoped by the **column**, not the context: it can delete
  committed rows AND that project's shared rows (shared rows keep the sharer's project_id).
  Other projects' rows are never touched. To delete a shared row, pass its OWN hash (differs
  from the source row's — path gains `shared/` prefix, hash = SHA256(path+content)), e.g. from
  a scope=shared search.
- `memory_delete_context(projectId, context)` builds the DELETE from `FilterFor`:
  `project:` / `workspace:` / custom labels / `label:` prefixes all carry `project_id`.
- **HAZARD (owner decision pending):** the `"shared"` branch of `FilterFor` emits only
  `scope = 'shared'` — NO project filter. `memory_delete_context(<anyProjectId>, "shared")`
  deletes every shared row in the bank across all projects. The destructive-access gate checks
  only the caller's project mode. Candidate fixes: scope the shared branch to the caller's
  project_id, or gate the shared tier separately.

## Composition-probe verification (ad-hoc, not canonical)

Unit tests use fake stores, so they cannot prove the real store's output flows through a
tool-layer fix. Temp probe pattern: a test class in the test project wiring the REAL
`SqliteMemoryStore` (real SQLite + memory_list_files extension) into the REAL `MemoryTools`,
asserting `Files` is a `JsonObject` and the camelCase-serialized response has
`"files"` of `JsonValueKind.Object` (note: default `JsonSerializer.Serialize` uses PascalCase —
pass `PropertyNamingPolicy = CamelCase` to mirror the MCP path). Run filtered → delete. The
live Hermes bridge runs the INSTALLED tool, not the build tree — a live call shows the OLD
behaviour; E2E (WebApplicationFactory real server) is the fix's canonical live-path evidence.
