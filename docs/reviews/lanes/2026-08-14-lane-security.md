# Lane report — security, encryption, access control and sync

Campaign: project-scope review, base `1d1889d517baf840df0b839f547091bd7f46808b`.
Model: opus · persona: code-reviewer · read-only, own worktree. Lane verified the base SHA.

> **Orchestrator calibration (added after the lane finished).** F1's stated precondition — access
> mode `full` — **is met on the live install**: `SELECT value FROM settings WHERE key =
> 'access.mode.global'` on `~/.ai-raccoon/memory.db` returns **`full`**, and the bank holds five
> real projects (`jsaa` 8,863 rows, `ai-raccoon` 3,997, `ai-badger` 1,940, `arasz-home-page` 855,
> `hermes-default` 464) plus 138 shared-tier rows. F1 is therefore **armed, not latent**. The
> orchestrator independently re-read `SqliteMemoryStore.cs:1053-1061` and confirms the lane's
> reading verbatim.

---

### F1 — `memory_delete_context` takes its project id from the caller-supplied context string, so it deletes any project's entries and can wipe the entire shared tier [READ]
**Severity:** BLOCKER
**Evidence:**
- `src/AiRaccoon/Tools/MemoryTools.cs:277-280` — gates on `AccessRequirement.Destructive` for `projectId`, then passes the raw `context` argument straight through to `store.DeleteContextAsync(projectId, context, …)`.
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:393,402` — `FilterFor(context, projectId, "")` then `DELETE FROM entries WHERE {filter}`.
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:1058-1061` — the `project:` branch binds `["projectId"] = context["project:".Length..]`. **The caller's `projectId` argument is discarded and replaced by the string parsed out of the context.**
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:1053-1055` — the `shared` branch returns `scope = 'shared'` with **no project predicate at all** and an empty parameter dictionary.
- `src/AiRaccoon.Core/Memory/ContextNaming.cs:8` — `SharedContext = "shared"`.
- Note the asymmetry that makes this a bug rather than a design: the `workspace:` branch (`:1063-1066`) and the `label:` branch (`:1074-1082`) **both** bind `["projectId"] = projectId` — the caller's. Only `project:` substitutes, and only `shared` omits it.

**Attack:** with any one project resolving to mode `full`, call
`memory_delete_context(projectId: "<that project>", context: "project:victim-repo")` →
`DELETE FROM entries WHERE scope='project' AND project_id='victim-repo'`, response `{"deleted": N}`.
Then `memory_delete_context(projectId: "<same>", context: "shared")` →
`DELETE FROM entries WHERE scope='shared'`, destroying the promotion tier every project reads.

**Why it matters:** this is the identical defect commit `7698dc63` fixed in `EntryBucket.For` on the
*write* path — the same `context["project:".Length..] → project_id` mapping. The fix was scoped to
the function where the bug was found, not to the concept, and `FilterFor` is the delete path's copy
of that mapping sitting 1,000 lines away in a different file. The doc comment above `FilterFor`
(`:1046-1048`) audits it for SQL injection and is correct about that — it is fully parameterised —
which is precisely how the authorization hole survived review.

**Fix:** in `FilterFor`, bind `["projectId"] = projectId` (the caller's) in the `project:` branch and
throw `ContextOutsideProjectException` when the context names a different one — reusing the check
`EntryBucket.For` already has; and add `AND project_id = @projectId` to the `shared` branch, or
refuse `shared` on the delete path entirely. Best: extract the confinement check into one function
both `EntryBucket.For` and `FilterFor` call.

---

### F2 — Access mode resolves the mode of the project the caller *names*, so it is not an authorization boundary for anything [READ]
**Severity:** HIGH
**Evidence:** `src/AiRaccoon/Access/MemoryAccessGuard.cs:9-24` — `ResolveAsync(projectId)` reads
`access.mode.project:{projectId}` else the global default. There is no caller identity parameter
anywhere in `IMemoryAccessGuard`, `ToolGate`, or the MCP filter pipeline. `:30-33` — `Read` returns
before the settings lookup, so a `Read` requirement is a no-op. `src/AiRaccoon.Core/Memory/MemoryWriteRequest.cs:18`
and `src/AiRaccoon.Core/Memory/SearchQuery.cs:57` — `projectId` is validated as `NotNull().NotEmpty()`
only; no allowlist, no registry check. `GetProjectIdsAsync` exists (`SqliteMemoryStore.cs:350`) and is
never used to validate.

**Attack:** pin `victim-repo` to `ro`. A caller names `projectId: "anything-else"` and the guard
consults *that* project's mode — `victim-repo`'s `ro` setting is never read.

**Why it matters:** every other finding's severity depends on this. Access mode is a *per-project
configuration knob*, not a per-caller permission, and the codebase treats the two as
interchangeable. `SECURITY.md:85-87` calls it "defence-in-depth", which is the right register — but
ADR-0045's "context is a label not a boundary" is only half the story: **`projectId` is also a label,
not a boundary.**

**Fix:** document this explicitly in `SECURITY.md` alongside the existing access-mode paragraph
(cheapest, honest). The structural fix — deriving the effective project from the transport/session
rather than a tool argument — is a design change, not a patch.

---

### F3 — `memory_write` with `context: "shared"` writes the cross-project tier at the default `rw` mode [READ]
**Severity:** HIGH
**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/EntryBucket.cs:11-14` — `context == "shared"` maps
to `scope='shared'` and is **not** covered by the project check commit `7698dc63` added at
`:19-22`/`:41-44`. `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:126` — `WriteAsync`
inserts `scope = bucket.Scope` with no shared-tier check. `src/AiRaccoon/Tools/MemoryTools.cs:58` —
gated `Write`; default mode is `rw` (`AccessModePolicy.cs:13`).

**Attack:** `memory_write(projectId: "scratch", content: "<injected instruction>", context: "shared")`.
The row now returns from **every** project's `memory_search(scope="all")`, is exempt from the sweep
(`SweepService.cs:21-25`), and bypassed both the propose/review flow and `memory_share_extract`'s
`confirm=true` gate.

**Why it matters:** OWASP LLM01. The cheapest memory-poisoning primitive in the system — default
mode, one call, permanent, cross-project, skirting the entire promotion-review pipeline that exists
specifically to keep unvetted content out of the shared tier.

**Fix:** refuse `context == "shared"` in `EntryBucket.For` the way it already refuses a foreign
`project:`.

---

### F4 — `memory_promotion_list` skips the access gate entirely when `projectId` is omitted and returns every project's queued content [READ]
**Severity:** HIGH
**Evidence:** `src/AiRaccoon/Tools/PromotionTools.cs:37-40` — `if (projectId is not null) { await
gate.RequireAsync(…); }`. Omit it and no guard runs. `:42,48-50` — `queue.ListAsync(null, limit, …)`
(`PromotionQueueSql.List:54` is `WHERE (@ProjectId IS NULL OR project_id = @ProjectId)`), and
`ToView` returns `row.ProjectId`, `row.Path`, `row.SourceFile` and — with `includeFullValue: true` —
`row.Value` in full.

**Attack:** `memory_promotion_list(limit: 500, includeFullValue: true)` with no `projectId` returns
full entry text for every project on the machine, with no access check of any kind.

**Why it matters:** the tool description says "omit to see every project's queue", so the *behaviour*
is deliberate — but the deliberate design is a bank-wide read primitive that no access mode can
restrain, because omitting the argument omits the check. `memory_promotion_discard` (`:66`) is
correctly scoped, which shows the asymmetry was not intentional at the enforcement level.

**Fix:** require an explicit sentinel (`projectId: "*"`) that resolves the *global* access mode, so
the bank-wide read is still gated rather than ungated.

---

### F5 — `memory_sync` uploads the entire bank — every project — and `projectId` only names the object key [READ]
**Severity:** HIGH · **loaded, not fired** until `ai-raccoon sync add` is run
**Evidence:** `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:35` — `var key =
string.IsNullOrWhiteSpace(objectKey) ? $"memory-{projectId}.db" : objectKey;` — the only use of
`projectId`. `:70` — `VACUUM INTO '{localSnapshot}'` off the bank connection; the bank is one
install-wide file (`SqliteConnectionFactory.cs:11-13,152`). `:422-437` — `StripNonSyncableAsync`
removes only `entries WHERE workspace_id IS NOT NULL` and all of `settings`. **No `project_id` filter
anywhere.** `:277` — the merge predicate is `WHERE r.workspace_id IS NULL`, again no project
predicate. `src/AiRaccoon/Tools/SyncTools.cs:29` — gated `Write`, i.e. the default `rw`.

**Attack:** an agent working in project `acme` calls `memory_sync("acme")`. Every committed entry for
every other project on the machine, plus the whole shared tier, is uploaded to the configured
bucket. No human is in the loop.

**Why it matters:** when bank encryption is not configured (the default —
`EnvEncryptionKeyProvider.cs:20-27` returns a null passphrase when `AIRACCOON_DB_PASSPHRASE` is
unset), the uploaded snapshot is **plaintext SQLite**. Nothing on the sync path checks the encryption
state, warns, or refuses.

**Fix:** either filter the snapshot to `project_id = @projectId OR scope = 'shared'` before upload,
or rename/redescribe the tool so it is honest that it syncs the whole bank — and refuse (or require
an explicit acknowledgement flag) when the bank has no encryption key.

---

### F6 — A remote sync blob that parses as SQLite is trusted, so whoever can write it authors the agent's memory [READ]
**Severity:** HIGH · **loaded, not fired** without sync configured; substantially mitigated on encrypted banks
**Evidence:** `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:203-217` — the only integrity check is
`PRAGMA quick_check`, which returns `ok` for any well-formed database. `:248-257` checks only the
schema version. There is no hash, HMAC or signature; the ETag (`:87,119,124`) is a CAS token, never
compared against a computed digest. `:267-283` — `INSERT OR IGNORE INTO entries … SELECT r.value,
r.path, r.source_file, r.scope, r.project_id … FROM remote.entries r`, copying attacker-chosen values
verbatim, **including `scope`**. `:338-346` — `DELETE FROM entries WHERE (hash, scope) IN (SELECT
hash, scope FROM remote.sync_tombstones)`. `S3CloudStore.cs:49-55` and `AzureBlobCloudStore.cs:48` —
the whole blob is buffered in memory with no size cap.

**Attack:** an attacker with write access to `memory-<project>.db` in the bucket plants a valid SQLite
bank whose rows carry `scope = 'shared'`, a plausible `source_file`, and instruction text in `value`.
Next `memory_sync` merges them; `memory_search` then serves that text to the agent as trusted project
memory, in **every** project, complete with a citable source path. Planting tombstones deletes
arbitrary local memories.

**Genuine mitigation worth stating:** on an encrypted bank the `quick_check` open carries the
SQLCipher password (`AppRegistrations.cs:107-114`), so a foreign or differently-keyed file fails to
open and maps to `SyncCorruptFileException` — encrypted deployments get de-facto blob
authentication; unencrypted ones get none.

**Fix:** store a keyed digest of the snapshot alongside the blob (or in its object metadata) and
verify it before ATTACH; add a configurable size cap on the download.

---

### F7 — `ro` mode is not read-only: `memory_search` is gated `Read` but writes, including to shared rows [READ]
**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon/Tools/MemoryTools.cs:118` — gated `AccessRequirement.Read`, which
`MemoryAccessGuard.EnsureAsync:30-33` treats as always-allowed.
`src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:276` — `SearchAsync` calls
`BumpAccessAsync`. `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:393-400` — `UPDATE entries SET
access_count = access_count + 1, last_accessed_at = @now, rating = @rating WHERE hash = @hash AND
(project_id = @projectId OR scope = 'shared')`. `src/AiRaccoon/Tools/MemoryTools.cs:145` — also
inserts a `search_quality` row. Same shape at `ShareTools.cs:87-88`, where `mode=propose` is gated
`Read` but `SharedExtractionRunner` runs a `DELETE` (`:33`) and an `INSERT` (`:59`).

**Attack:** repeatedly `memory_search` from project A against content in the shared tier. Each call
rewrites `rating` on those shared rows — and `rating` is the sweep's deletion input
(`SweepService.cs:40-44`).

**Why it matters:** `SECURITY.md:85-86` states "`ro` mode allows only reads". That is false as
written, and the write it permits feeds the automatic deletion policy.

**Fix:** introduce a distinct `ReadWithBookkeeping` requirement, or skip
`BumpAccessAsync`/`RecordSearchAsync` when the resolved mode is `ro`. At minimum correct
`SECURITY.md`.

---

### F8 — Access enforcement is hand-repeated in all 26 tools, with no derived check, while the chokepoint mechanism already exists [MEASURED]
**Severity:** MEDIUM
**Evidence:** `rg -o "\[McpServerTool" src/AiRaccoon/Tools/ | wc -l` → **26**;
`rg -o "gate\.RequireAsync" src/AiRaccoon/Tools/ | wc -l` → **26**, matching per-file. Coverage is
complete *today*. `src/AiRaccoon/Setup/McpServerSetup.cs:168-169` — `AddCallToolFilter` is already
used twice (`ToolRefusals.Filter`, `ToolTelemetry.Filter`); the pipeline exists and access was not
put in it. `tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs:110` —
`EveryTool_NamesTheProjectIdParameter` derives over the tool surface by reflection for `projectId`,
but **no equivalent test asserts every tool calls the gate**.

**Why it matters:** the project's own `derive-or-delete-the-list` invariant. `ToolGate`'s doc comment
claims "One copy, so the seven tool classes cannot drift apart" — but the *copy* is centralised while
the *call* is not, which is the half that drifts. A 27th tool that omits the line compiles, passes
CI, and ships unenforced.

**Fix:** add an inventory-derived test asserting each `[McpServerTool]` method's guard requirement,
and move `RequireAsync` into a `CallToolFilter` beside the two already registered.

---

### F9 — `memory_search`'s `limit` has no upper bound and is amplified into an effectively unlimited SQL `LIMIT` [READ]
**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon.Core/Memory/SearchQuery.cs:59` — `RuleFor(x => x.Limit).GreaterThan(0)`.
Lower bound only. `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:814-817` —
`(int)Math.Clamp((long)limit * 3, 100, int.MaxValue)`; the `(long)` cast correctly prevents overflow,
then clamps *up to* `int.MaxValue` rather than to a ceiling. `:213,242,260` — that value is bound as
the `$limit` parameter for both the FTS5 and vec0 queries; `:205` accumulates every candidate's raw
value into `valueByHash`. `src/AiRaccoon.Core/Memory/MemoryWriteRequest.cs:19-21` — `Content` has no
`MaximumLength` (`SourceFile` and `Section` do); `SearchQuery.cs:58` — `Query` has none either.
`src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:59,83` — `File.ReadAllTextAsync` with no size
cap.

**Why it matters:** OWASP LLM10. Not a confidentiality break, but a context-window and cost amplifier
and an allocation DoS. The asymmetry is telling: `ContextLabel` is capped at 256 and `SourceFile` at
1024, so bounds were considered and the two unbounded fields were missed.

**Fix:** `RuleFor(x => x.Limit).InclusiveBetween(1, 200)`, a `MaximumLength` on `Query` and `Content`,
and a byte ceiling in `FileIngestor` before `ReadAllTextAsync`.

---

### F10 — The encryption key lives in managed strings for the whole process and the code that claims to zero it clears only one of four copies [READ]
**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/Encryption/EncryptionKeyResolver.cs:22,42-44` —
`_cachedKey` holds a `ResolvedKey` whose `Passphrase` is a `string`. Per ADR-0038 §3 the resolver is a
DI singleton, so this is process lifetime; a .NET `string` is immutable and GC-relocatable.
`src/AiRaccoon.Infrastructure/Sqlite/Encryption/Providers/BitwardenEncryptionKeyProvider.cs:43-54` —
the comment reads "then zeroes it — the seed must not outlive this call", and
`CryptographicOperations.ZeroMemory(seed)` does clear that array. But the same 32 seed bytes survive
un-zeroed in three other places: `result.Stdout` (the raw PEM, `:34`), its `.Trim()` copy, and the
base64-decoded blob returned by `OpenSshPrivateKeyParser.DecodePem`
(`src/AiRaccoon.Core/Encryption/OpenSshPrivateKeyParser.cs:41`) — `ParsePrivateSection` copies the
seed *out* of that blob (`:142`) and never clears it.
`src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:240-257` — the key is then embedded in
a connection string, another managed string, retained on every `SqliteConnection`.

**Why it matters:** no invariant is broken — HKDF, the SQLCipher rekey and the token flow all use
library primitives correctly. The defect is that the code *documents a guarantee it does not
provide*. A reader of `DeriveAndZeroSeed` concludes the seed is gone; it is not.

**Fix:** either drop the misleading comment and the `ZeroMemory` call (honest: .NET cannot hold this
key securely) or move the key to a `byte[]`/`SecureMemory` held end-to-end and zero every
intermediate. The first is smaller and probably right.

---

### F11 — OTLP export ships absolute filesystem paths and full stack traces, which `SECURITY.md`'s "what leaves the process" section does not mention [READ]
**Severity:** MEDIUM · **loaded, not fired** unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set
**Evidence:** `src/AiRaccoon/Observability/ToolExecutionActivity.cs:83,86` —
`_activity?.SetStatus(ActivityStatusCode.Error, exception.Message)` and
`_activity?.AddException(exception)`, which emits `exception.message` **and** `exception.stacktrace`
as span attributes. Same shape at `BackgroundTelemetry.cs:132,135`.
`src/AiRaccoon.Core/Ingestion/IngestExceptions.cs:4-5,8` — `PathOutsideScopeException(string path) :
InvalidOperationException($"Path '{path}' is outside the ingest scope.")`, interpolating the
caller-supplied absolute path. `ToolTelemetry` is registered *inside* `ToolRefusals`
(`ToolTelemetry.cs:10-11`) specifically so it sees the raw exception before mapping.
`SECURITY.md:123-126` — "Memory content never leaves. No entry text, no search queries, no file
contents… only the scope name (`project_id`) and call-shape telemetry"; the table never mentions
exception messages or stack traces.

**Fix:** set the span status to the mapped refusal category rather than `exception.Message`, drop the
`AddException` call or gate it behind a debug flag, and extend the `SECURITY.md` table.

---

### F12 — A 19 MB populated memory bank from a different private project is committed to this repo [MEASURED]
**Severity:** MEDIUM (policy call, not a credential leak)
**Evidence:** `tests/AiRaccoon.Tests/Resources/jsaa-memory.db`, tracked, 19,173,376 bytes. Lane's own
queries: `select count(*), count(distinct project_id), count(distinct source_file) from entries` →
**2518 | 1 | 195**; `select distinct project_id from entries` → **`job-search-ai-assistant`**;
`select count(*) from entries where value like '%araszkiewiczrafal%'` → **94**. Source files are
`.ai-badger/`, `docs/` and `infra/` paths from `/Users/arasz/RiderProjects/job-search-ai-assistant`
(root hardcoded at `scripts/src/jsaa_config.py:8`; pipeline `scripts/ingest-jsaa-docs.py`).
`entries.value` holds full document text plus FTS index and embeddings.

**Why it matters:** no credential *values* are present — the sweep for `AccountKey=`, `AKIA`, `ghp_`,
`BEGIN … PRIVATE KEY` and `client_secret` inside `entries.value` returns only prose *about* secret
management, so the "no hardcoded secrets" invariant holds. This is a cross-project content-disclosure
question that only the owner can settle. If this repo is or becomes public, so is that project's
complete internal documentation corpus, its deployment runbook (Key Vault *secret names*, OAuth
setup, RBAC assignments, the production hostname), and the owner's email in 94 rows.

**Fix:** owner decision. If `job-search-ai-assistant` is meant to stay private, regenerate the fixture
from synthetic documents and purge the blob from history; otherwise record the decision.

---

### F13 — The proxy client that carries the loopback token follows redirects; its hardened sibling does not [READ]
**Severity:** LOW · **loaded, not fired** on a single-user machine
**Evidence:** `src/AiRaccoon/Hosting/Proxy/ProxyRegistrations.cs:17-20` — `new SocketsHttpHandler()`
with `AllowAutoRedirect` left at its `true` default. This is the client that attaches the token
(`BackendSessions.cs:115`). `src/AiRaccoon/Hosting/Node/NodeRegistration.cs:21` — the `--restart`
client sets `AllowAutoRedirect = false`, and ADR-0022:135-137 explains exactly why: .NET strips
`Authorization` across a host hop but **not** a custom header like `X-AiRaccoon-Token`.

**Why it matters:** the redirect is not itself the vulnerability — the attacker already has the token
on the first request, and ADR-0022:128-138 explicitly accepts backend identity as "a heuristic, not a
security control". **The inconsistency is the bug**: one client was hardened against a named
token-exfiltration vector and its sibling was not.

**Fix:** add `AllowAutoRedirect = false` at `ProxyRegistrations.cs:19`. One line.

---

### F14 — The rekey migration's "read-only" probe opens the bank read-write-create [READ]
**Severity:** LOW
**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:174-176` — doc comment
states "Read-only and pool-free, so a refusal leaves the file untouched". `:183` — `Mode =
SqliteOpenMode.ReadWriteCreate`.

**Why it matters:** this is the function that decides whether a destructive `PRAGMA rekey` is
authorized (`:52-58`). Its safety property is asserted in a comment and contradicted one line down.

**Fix:** `Mode = SqliteOpenMode.ReadOnly`, or correct the comment.

---

### F15 — `ingest.scope.<projectId>` collides with `ingest.scope.global` for a project named `global`, where the sibling access-mode key scheme is immune [READ]
**Severity:** LOW
**Evidence:** `src/AiRaccoon.Core/Ingestion/IngestScopeKeys.cs:12,17` — `ScopeGlobal =
"ingest.scope.global"` and `ScopeProject(projectId) => $"ingest.scope.{projectId}"`. Contrast
`src/AiRaccoon.Core/Access/AccessModePolicy.cs:9,11` — `"access.mode.global"` vs
`$"access.mode.project:{projectId}"`; the `project:` infix makes the collision impossible.
`src/AiRaccoon/Setup/Cli/Commands/WatchCommands.cs:46` — `target == "*" ? ScopeGlobal :
ScopeProject(target)`.

**Fix:** `$"ingest.scope.project:{projectId}"`, with a migration alongside the `LegacyScopePrefix` one
already at `MemorySchema.cs:559`.

---

### F16 — Security-relevant documentation has drifted from the code in three places [READ]
**Severity:** LOW
**Evidence:** `SECURITY.md:44` says "Memory tools (**23** tools)"; the actual surface is **26**.
`docs/adr/0043-*.md:112-119` — the "Known gap" states `ServerRestart.WaitForPortToFreeAsync` still
treats "the probe stopped answering" as "the port freed"; `src/AiRaccoon/Hosting/Node/ServerRestart.cs:160`
now reads `if (await _probe.ProbeAsync(port, waiting.Token) is not ProbeVerdict.NotListening) {
continue; }` — it requires positive proof. **The gap is closed and the ADR was not updated.**
`docs/adr/0022-*.md:215-221` — the Evidence block points at `src/AiRaccoon/Setup/Serve/*`; those files
moved to `src/AiRaccoon/Hosting/{Common,Node}/`. Plus `SECURITY.md:85-86` contradicted by F7 and
`SECURITY.md:123-126` incomplete per F11.

**Fix:** update the four passages; derive the tool count in `SECURITY.md` from the same reflection the
`ToolInventoryTests` already run against the packaged README (`ToolInventoryTests.cs:124`).

---

## Still open

- **No exploit was executed.** Every finding is source-traced, not demonstrated. F1 deserves a red-first test: create two projects, write to `victim`, call `memory_delete_context(projectId: "attacker", context: "project:victim")` with global mode `full`, assert the deletion is refused. That test does not exist today.
- **`FilterFor` has four other callers the lane did not enumerate.** `DeleteContextAsync` reaches it with a caller-supplied context; `SearchContexts.For` never does. Whether any *other* caller passes untrusted context strings is unchecked.
- **`memory_record_grade`/`memory_record_followthrough` accept a `projectId` they never use** (`SqliteSearchQualityService.cs:79,90-94` — `WHERE correlation_id = @Id`). Not verified by this lane; cost of a cross-project grade overwrite not established.
- **S3 conditional-write support is unverified.** `S3CloudStore.cs:100` sets `If-Match` as a raw header; a MinIO/R2/Wasabi endpoint that ignores it returns 200, silently degrading CAS to last-writer-wins. All conflict tests use `FakeCloudStore`.
- **`MemorySchema.cs` (1,225 lines) and the DDL/migration path went unreviewed** by this lane — trigger fire-time and `ON CONFLICT` scope are where a data-integrity defect would hide. (Assigned to the data-access lane.)
- **Windows behaviour is untested.** `UnixFileMode` is POSIX-only, and `IngestPath`'s case-insensitive comparison on Windows may interact with 8.3 short names or alternate data streams.

## Grade mix

MEASURED 3 (F8 tool/guard counts, F12 database queries, the `--vulnerable` scan) · READ 13 ·
INFERRED 0 · UNVERIFIED 0. Nothing was demonstrated by execution, so no vulnerability here is
stronger than READ.

## Owner questions

1. Is `job-search-ai-assistant` a private repository, and is this repo intended to become public? (Settles F12 entirely.)
2. Is `access.mode.global = full` a configuration you expect real installs to run, given `memory_sweep` requires it? *(Orchestrator note: the live install is already `full`, so F1 is armed — this question is now about whether that should change, not whether it applies.)*
3. Should the shared promotion tier be writable directly via `memory_write(context: "shared")`, or only through `memory_share`? (F3 is a one-line fix if the answer is "only through share".)
4. Is `memory_sync`'s whole-bank behaviour intended, or was per-project sync the design? (Decides whether F5 is a code fix or a documentation fix.)
5. Should `memory_promotion_list` with no `projectId` remain on the MCP surface at all, or become CLI-only? (F4.)
6. Do you want `ro` to mean genuinely read-only, or is access-count bookkeeping an accepted exception? (F7.)

## Healthy

Controls checked and found genuinely sound — do not "fix" these:

- **FTS5 injection is structurally impossible.** `FtsQueryNormalizer.cs:79` builds every MATCH term from `[\p{L}\p{N}_]+` only; no quote, `*`, `^` or `:` survives tokenisation. Reserved words are stripped at `:37`.
- **Path containment is done properly.** `IngestPath.IsWithinScope` (`:48-60`) resolves symlinks *per path segment* (`:31-45`) on both sides, then does a prefix-plus-separator comparison — so `/scope-evil` does not pass for scope `/scope`.
- **Ingest scope fails closed.** An empty or absent allowlist makes `scope.Any(...)` false, so `RequireInScope` throws (`FileIngestor.cs:235-244`). Containment lives in the store, so the CLI and the watcher are bound by it too.
- **Key material is escaped by the database, not by string formatting.** Both `PRAGMA rekey` (`SqliteConnectionFactory.cs:120-127`) and `ATTACH … KEY` (`SyncService.cs:232-236`) obtain the literal via a parameterised `SELECT quote($key)`. `CommandText` is never logged anywhere in `src/`.
- **The loopback token flow is well built throughout:** 256 CSPRNG bits (`McpTokenFile.cs:160`), `CryptographicOperations.FixedTimeEquals` (`McpTokenGate.cs:95`), 0600 set atomically at `open(2)` via `UnixCreateMode` rather than chmod-after-create (`:167-171`), `FileShare.None` debris healing that cannot delete a live server's file (`:110-132`), and both credential envelopes evaluated independently.
- **Binding is hardened.** `IPAddress.Loopback` (IPv4 only — stricter than `ListenLocalhost`) at `McpServerSetup.cs:101`, with `builder.Configuration.Sources.Clear()` at `:88` so `ASPNETCORE_URLS` has no provider to arrive through. `ServerConfig.cs:23-29` hand-writes `PrintMembers` to keep the token out of the record's `ToString`.
- **The Bitwarden token goes via the environment, never argv** (`BitwardenCliSecretManager.cs:47`), with the `ps aux` threat named in the comment and a regression test asserting the `--version` probe carries no token.
- **No memory content reaches telemetry.** Across 111 `[LoggerMessage]` declarations, zero carry `{Query}`, `{Content}`, `{Value}` or any entry text; `SweepHostedService.cs:228` documents the rule at the point of enforcement.
- **Cloud credentials never leave the machine.** `StripNonSyncableAsync` (`SyncService.cs:432`) does `DELETE FROM settings` on every push path, and the pull side never reads `remote.settings` — ratified as ADR-0014 with named tests.
- **Workspace isolation genuinely holds.** `SqliteWorkspaceStore.cs:57` is `WHERE id = @workspaceId AND project_id = @projectId`. ADR-0046's `ProjectRows` refactor did its job on `memory_get`, `memory_delete`, `memory_set_ttl`, `memory_share` and the sweep.
- **`OpenSshPrivateKeyParser` is a careful binary parser.** Every read is bounds-checked (`:151-161`), a `uint` length above `int.MaxValue` casts negative and is caught, and all thirteen throw sites use fixed literal details.
- **ADR-0043 is honoured in code.** `ServerProbe` returns `NotListening` only on positive `ConnectionRefused` proof (`:85-96`).

## Disconfirmed

- **"The `NoWarn` suppression hides a live vulnerability."** No. `dotnet list package --vulnerable --include-transitive` returns clean for all five projects at this commit (MEASURED). `Directory.Build.props:7` suppresses only `NU1901;NU1903` — low and moderate. `NU1902`/`NU1904` (high/critical) still warn, and `TreatWarningsAsErrors=true` turns those into build failures. Given `SECURITY.md:156-160` states there is no Dependabot and no CodeQL, `NuGetAudit` is the only dependency signal — and it is still armed where it matters. **A defensible trade, not a hole.**
- **"`identifier.sqlite` and `dist/` are leaked artefacts."** No. `identifier.sqlite` is tracked but is git's canonical empty blob, 0 bytes. `dist/` does not exist and is not tracked. (`.gitignore` lacking `*.sqlite` is why the former happened; worth adding.)
- **"Hardcoded secrets are in the test fixtures."** No. The `AccountKey=Eby8vdM…` in `SyncCloudStoreFactoryTests.cs:22` is Microsoft's published Azurite emulator key, identical worldwide. The `BEGIN OPENSSH PRIVATE KEY` hits are frames built at runtime by `TestOpenSshKeyBuilder`. The 40- and 64-hex literals are git SHAs and model digests.
- **"HKDF is hand-rolled key derivation."** No. `SshKeyDerivation.cs:25` calls `HKDF.DeriveKey(HashAlgorithmName.SHA256, seed, key, default, LabelBytes)` — the BCL primitive, correct usage, with the label as `info` for domain separation. The empty salt is RFC 5869-compliant for a high-entropy IKM. The `no-hand-rolled-crypto` invariant holds.
- **"`AccessModePolicy.ProjectSettingKey` collides with the global key."** No — the `project:` infix prevents it. It was `IngestScopeKeys` that lacked the infix (F15).
- **"`memory_search`'s `contextLabel` is a cross-project read primitive."** No. `SearchContexts.For` (`:33-66`) builds every context string from `query.ProjectId`, so a hostile label can never reach `FilterFor`'s `project:` branch. **Read isolation holds** — but by construction, not by check, which is exactly why the delete path (F1) fails.
- **"The sync layer swallows exceptions."** No. Every handler either retries or rethrows. (There *is* a separate defect — the `finally` blocks at `SyncService.cs:405-410`, `:111-114`, `:167-170` can have a `DETACH`/`File.Delete` failure replace the in-flight exception — but that is error-masking, not swallowing.)
