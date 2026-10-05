# 0127. Reads need a registered project id too

Date: 2026-10-04

Status: Accepted. Ratified by the owner on 2026-10-04: gate D0-D5, 6/6 APPROVE, plus follow-up F1
(`docs/work/2026-10-04-unregistered-project-id-feedback.md`). Amends ADR-0089 decisions 3 and 6.

## Context

ADR-0089 decision 3 refused a *write* under an unregistered project id and let every read through
("reads pass through untouched"). Reads are not free, though:

- Every call writes a `metrics` row keyed by the projectId argument, through the telemetry filter.
- `memory_search` also writes a `search_quality` row and about 13 search-phase `metrics` rows.
- `memory_share_extract mode=propose`, which is gated as a read, writes `promotion_queue` rows.

On 2026-10-04 the live bank held 12 project ids that matched no project. All of them owned only
telemetry left behind by reads. The largest was `d42bd180-…`: 843 `metrics` and 75
`search_quality` rows. It is the `.ai-badger/project-id` that the ai-badger scaffold wrote as a
uuid4 and never registered. That repo's memory hook searched under the id on every prompt and
found nothing, without any error. Writes under it failed with `project-not-registered`, so the
agent minted a second id and split the project's memory.

The research record is `docs/work/2026-10-04-unregistered-project-id-research.md`.

## Decision

1. **A read under an unregistered id is refused (`project-not-registered`), the same as a write.**
   - The test is unchanged: an id passes when it is registered, or when the bank already holds
     rows for it. A legacy id with rows keeps working, with the one-time warning 433.
   - A read never registers an id. That holds even on an unmigrated bank, where a write under a
     raw-text id still auto-registers until a project-ids repair finishes.
   - A refused call writes no `metrics` row under the refused id (it is tagged `refused`) and no
     `search_quality` row.

2. **A retired (dropped) id is refused on reads too (`project-retired`).**

3. **ToolGate resolves an id through the durable alias map without checking the migration
   marker.**
   - `RequestRepair` reopens `finished_at` on its kind's single row, so while any project-ids
     repair is open, `IsMigratedAsync` returns false.
   - Under the old marker check, every read under an alias id (this repo's own hook id among
     them) would have been refused for the length of the repair window, about 3.5 minutes on the
     live bank.
   - An empty map is pass-through by design, and the map loads only from the durable table, so
     checking the marker bought nothing. The marker still decides the registration guard's
     auto-register branch.
   - Resolution and retirement come from one pure function, `ProjectIdAliasMap.Apply`, which
     ToolGate, the project directory and the settings endpoint all share.

4. **Calls that pass without a registered id.** These four, and no others:
   - `project_id_token_get`
   - `project_id_get`
   - `memory_promotion_list` with `allProjects=true`
   - `memory_performance` under the reserved `__self_metrics__` id, matched exactly and
     case-sensitively

   A blank projectId that the cwd resolves to an id must still pass the registration test. A
   test derived from the registered tools checks that every tool taking a projectId refuses an
   unregistered one.

5. **Settings writes keyed by a project id follow the same rules.** The writers are `PUT /settings`
   for `access.mode.project:`, `watch.enabled.`, `watch.concurrency.`, `ingest.scope.` and the
   legacy `watch.scope.`, plus the CLI verbs built on it.
   - **Resolve.** The key's id goes through `Apply`, on `GET ?key=` as well as `PUT`.
   - **Rewrite.** An alias is rewritten to its winner. Resolving reads too matters: if only writes
     were rewritten, `ingest scope add` under an alias would read the alias's empty list and
     replace the winner's whole list.
   - **Refuse.** An unknown or retired id is refused with 409, which the CLI reports as exit 18. A
     refused write never registers the id.
   - **Exceptions.** A scope write whose new list is a subset of the stored list passes, and so
     does the `*` wildcard. DELETE routes are unaffected, so stale per-project state can always be
     removed.
   - Alias resolution never rewrites a per-project key into a machine-global key. Repair
     preserves machine-global keys when an alias map contains the literal `global` owner.
   - **Consequence.** A raw-text id that is neither registered nor holding rows cannot take
     `ingest scope add` or `watch enable` until it is registered; an id that holds rows passes.

6. **`project id register | get | check` replace decision 6's unbuilt `generate` and `convert`.**
   - `ai-raccoon project id register <id> [--name <n>]` registers an id a project already uses:
     - It resolves aliases first. An alias of a registered project answers
       `already registered <winner>` and writes no row.
     - A retired id is refused.
     - A raw-text id that is neither registered nor row-holding is refused as not a guid.
     - The first non-null name wins, so a later direct registration with `--name` fills a NULL
       name. Registering an alias leaves the winner's name unchanged.
   - `project id get --name <n>` prints the stored id. Raw-text legacy ids are printed as stored.
   - `project id check <id>` reports `known`, `unknown` or `retired`.
   - All three reach the server over the `/projects` endpoint. The CLI never opens the bank.

7. **Look up before you mint.**
   - `project_id_get(name)` returns the id registered under an exact, case-sensitive name. It
     needs no caller id and never registers.
   - With ai-raccoon alone, an agent calls `project_id_get` first and `project_id_token_get` only
     on a miss.
   - With ai-badger, the scaffold runs `get --name <repo>` when `.ai-badger/project-id` is missing,
     and runs `register <file id>` when it is present. The ai-badger side lives in that repo.

8. **Exit codes 18 and 19.** `ErrorCode.Usage.ProjectUnknown = 18` covers not found, unknown and
   retired. `ErrorCode.Usage.ProjectAmbiguous = 19` is for a name several projects share; the
   candidates go to stderr only. Usage is chosen on fit: these are wrong inputs that the caller
   fixes. It is not the only decade with free slots.

## Consequences

- **A wrong id now fails loudly, on the first search.** A memory hook running in a repo whose id is
  unregistered stays silent until the id is registered or aliased. Hooks already fail open.
- **A new CLI against an old server.** A 1.57.0 CLI talking to an older server finds no `/projects`
  route and exits 57 (`EndpointMissing`). The repair loop reads `RepairOpen` as false against an
  old server, so it behaves as it did before 1.57.0. Settings writers are the sharper edge: an
  older, still-running server has no 409 path for a per-project settings write, so the write
  succeeds and the CLI exit looks clean until the server restarts on 1.57.0 and the next write is
  refused. Mixed binary and server versions are unsupported (the version-skew rule stated in the
  1.44.0 breaking-change note).
- **Cost per read.**
  - A registered id costs one bank open plus a primary-key SELECT on `projects`.
  - An unregistered id adds the rows probe.
  - The `projects` table is small and has no index on `name`. `get` is a full scan by design.
- **Follow-ups.** `memory_sync` pull, which merges rows under remote ids, and watch ingestion for
  `watches` rows created before 1.57.0 still bypass registration. Both are tracked in #847.

## Alternatives rejected

- **Auto-register on read.** It turns every typo into a project. That is the accident ADR-0089
  removed for writes.
- **Keep reads open and only stop recording telemetry under unknown ids.** Nothing breaks, but a
  typo still returns empty results without any error, and the owner ruled for loud refusal (D1).
- **Latch the migration marker so it can never reopen.**
  `ProjectIdsMigrationGateTests.IsMigratedAsync_AfterASecondRequest_ReopensToFalse` pins
  reopening as intended. Dropping the marker from ToolGate's resolution path (decision 3) removes
  the need for a latch.
- **Minting through ai-raccoon and rewriting `.ai-badger/project-id`.** It changes the message-bus
  identity (ai-badger ADR-0025) and strands mail addressed to the old id. Registering the existing
  id keeps the bus id and the memory id the same string.
