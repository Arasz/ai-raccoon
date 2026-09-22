# AiRaccoon — project-scope code review (assembled findings)

**Base commit:** `5bca19000d127547ddbc56e2a464113c844334cc` (== `origin/main`, clean)

**Lanes:** 10 (A–J), read-only, each in its own worktree at the base commit.

## Executive summary

72 findings across 10 lanes: **31 MEDIUM**, 27 LOW, **7 HIGH**, 7 NIT.

Grades: MEASURED=62, READ=10.

The seven HIGH findings cluster into four themes: **sync/delete correctness** (untombstoned deletes are resurrected by the next sync), **agent-facing silent degradation** (a fresh install searches keyword-only with no warning), **loopback trust** (a local squatter receives the token and tool payloads), and **unauthenticated sync on an unencrypted bank** (bucket-write access equals memory takeover). The remaining findings are MEDIUM/LOW contract, observability, CI and documentation defects, several of which are live-confirmed with transcripts in the evidence lines.

## Scope & method

Every lane worked read-only in its own worktree at the base commit, drove the installed product only against `--data-root docs/work/2026-09-22-project-scope-review/<lane>/…` scratch roots, and never read or wrote the owner's bank. Grades follow the campaign contract: MEASURED (reproduced live or read from a real artifact), READ (traced in source), INFERRED, UNVERIFIED. Findings that two lanes filed independently are consolidated in the cross-cutting section with a provenance note naming the source lanes; the originals are not repeated.

## Findings

#### Cross-cutting (filed independently by two or more lanes)

### F1 — A committed merge-conflict marker (`>>>>>>> origin/main`) sits inside the canonical agent-facing contract reference [MEASURED]

**Severity:** LOW

**Evidence:** Lane F: `docs/reference/agent-memory-server.md:212` is exactly `>>>>>>> origin/main`, between "…on its own on-demand" and "cadence, rather than re-embedding inline itself"; `git blame` attributes it to `2a55334a`; a repo-wide grep for `^<<<<<<<|^=======$|^>>>>>>>` finds this line and nothing else — the paragraph reads "on its own on-demand >>>>>>> origin/main cadence". Lane G (independently): `git show HEAD:docs/reference/agent-memory-server.md | grep -n '>>>>>>>'` → `212:>>>>>>> origin/main`; the 2026-08-26 ops review already prescribed the fix as OPS-17 (SHOULD) and it has missed four weeks of releases.
Two lanes filed this independently, on the file `docs/reference/README.md:8` calls "the MCP server's complete agent-facing contract". Cost: the primary mid-task document looks untrustworthy, and a future conflict resolution in this file has a decoy marker to key off. Fix: delete line 212.
*Provenance: filed independently by Lane F and Lane G.*

### F2 — The only tool-parity BDD scenario is `@ignore`d, asserts 17 tools including a removed one, binds 16 names, and would pass if re-enabled; the product exposes 29 [MEASURED]

**Severity:** LOW

**Evidence:** Lane F: `docs/work/features-native-memory/native-memory.feature:200-206` (`Scenario: All 17 tools are still listed`, listing the removed `memory_configure`); `native-memory.feature.cs:1756` `Skip="Ignored"`; the `Then` step binds a 16-name list (`tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs:903-928`). Lane H (independently): removing only the `@ignore`, rebuilding and running the scenario → `total: 1, failed: 0, succeeded: 1` — **it passes** — while `grep -rc "McpServerTool(" src/AiRaccoon/Tools/*.cs` = 29 and the reference's `## Tools (29)` heading plus its 29 rows match live `tools/list`.
Un-ignoring as-is ships a green test whose title and step text are false; keeping it ignored means the project's named tool-parity scenario never gates a PR (its replacement, `ToolInventoryTests`, has no count assertion). `TestHelpers/RegisteredTools.cs:11-13` cites the repo invariant "Never write this number into a test", which this scenario violates twice. Fix: derive the assertion from the registered surface, or delete the scenario in favour of the passing inventory tests.
*Provenance: filed independently by Lane F and Lane H.*

### F3 — `memory_promotion_list(allProjects=true)` names no project and runs no access check: any token holder reads every project's promotion queue, with absolute paths and full values [MEASURED]

**Severity:** MEDIUM

**Evidence:** Lane J (measured live on a scratch bank holding one queue row per project): `memory_promotion_list {"allProjects":true,"includeFullValue":true}` returned both projects' rows, including `path: /home/owner/acme-corp/clients/jane-doe/creds.md` and `value: PROJECT TWO INTERNAL: Acme Corp staging AWS keys rotate every 90 days`; `PromotionTools.cs:52-56` calls only `gate.RequireBankAvailableAsync` (the migration-lock check) and never `RequireAsync`. Lane C: the branch needs only the explicit `allProjects=true` flag; ruling S1's "read-all mode" was realized as that boolean consent flag, and the mode lattice has no read-all member (`AccessModePolicy.cs:28-36` — `Read => true` in every mode).
**Verifier-corrected (G3) — accepted-residual record, not a fix-now defect.** ADR-0089 decision 9 explicitly declines to introduce a global mode ("a real global mode is access control and access control is outside the threat model. O6's shape is revisited only when an ADR lands caller identity"), so the previously drafted "defeats ADR-0089's unguessable-id mechanism" framing overstated and the proposed "consult a mode/consent setting" fix would re-open a ratified ruling. What survives as the recordable residual — measured live by the verifier exactly as filed — is the reach: the consent flag is the only gate, and the branch returns every project's queue with absolute paths and full values while naming no project id. Revisit trigger: the caller-identity ADR.
*Provenance: filed independently by Lane C and Lane J.*

### F4 — The #414 PII-history guard is wired to nothing, exits 1 today, misses two historical path spellings, and its subject (a 19 MB blob) is still reachable in history [MEASURED]

**Severity:** MEDIUM

**Evidence:** Lane I: `python3 scripts/verify-history-scrubbed.py --repo .` → exit 1 naming `jsaa-memory.db: 7 commit(s)`, `RealWorldCorpus.cs: 2`, `Unit/Retrieval/assets/reference-topk.json: 2`; that asset existed under three spellings (`Retrieval/assets/…`, `unit/retrieval/assets/…`, `Unit/Retrieval/assets/…`) while `verify-history-scrubbed.py:20-24` checks only the last, and the runbook passes the other two by hand. Lane J (independently): `git ls-tree -r -l 00d381dd^ -- …jsaa-memory.db` → `19173376` bytes; `gh issue view 414` → OPEN; the guard appears in no workflow, so nothing re-notices after the rewrite.
The rewrite itself is a recorded owner-only decision (S6b, gated on #455); the finding is about the gate. A bare run of the script can print `PASS` for a scrub that is not complete, because two historical spellings are outside `PATHS`. Fix: fold the two spellings into `PATHS`, and wire the guard into a gate until #414 lands.
*Provenance: filed independently by Lane I and Lane J.*

### F5 — The MiniLM golden-vector gate is red on arm64 with a message that cannot separate runtime drift from a code regression, and inert on x64 where merges are gated [MEASURED]

**Severity:** MEDIUM

**Evidence:** Lane B: `dotnet test … --filter "FullyQualifiedName~BundledMiniLm_Embeddings_MatchCommittedGoldens"` → `total: 1, failed: 1`; `entry E001: 384/384 float32 values differ bit-for-bit from the golden capture — the WP3 refactor changed engine output`; the golden `Provenance` records architecture, capture date and model/vocab hashes but **no ONNX Runtime version**, while `Directory.Packages.props:34` pins ORT 1.30.0 and the on-disk model and vocab still hash to the golden's pins — so a dependency bump is accused as a code regression. Lane H (independently): the bit-identity and `L2 ≤ 1e-6` assertions run only when `sameArchitecture` is true (`MiniLmGoldenVectorTests.cs:90-122`); the committed capture is Arm64, so on the x64 CI runners the strongest engine-output check never executes (off-arch the pass condition degrades to `cosine ≥ 0.95` plus token-id equality).
Together the gate is red where it runs and absent where merges are gated — consistent with the ORT 1.29→1.30 bump merging green on x64 while this arm64 machine reddens. Fix: record the ORT version in `GoldenProvenance`, name the runtime in the mismatch message, and either recapture under 1.30.0 or re-pin to 1.29.0.
*Provenance: filed independently by Lane B and Lane H.*

### F6 — A fresh install runs memory search keyword-only, and the only warning available blames the code corpus and prescribes a code-only fix [MEASURED]

**Severity:** HIGH

**Evidence:** Lane G: `doctor` on a fresh bank → `memory engine: not configured — run 'ai-raccoon model embedding set local' to enable semantic memory search`; `memory_search kind=memory` on that bank returns `fusionStats.participatingLegs:["fts"]` with **no `warning`** — while README.md:132, `docs/how-to/configure-embedding-engines.md:13,31` and `src/AiRaccoon/Setup/McpServerInstructions.cs:14-25` all present hybrid semantic memory as the default. Lane F (independently): on a never-configured bank a semantic-only query returns `results: 1` with `warning: (none)`, while the identical call with `kind:code`/`kind:both` returns `warning: "code engine not configured — FTS5-only results; run 'ai-raccoon model code set default' …"`. Lane G: that warning's "FTS5-only results" reads as the whole response and its only remedy fixes the secondary corpus, though the memory leg is FTS-only too (`CodeSearchWarnings.cs:15-16`; `docs/reference/agent-memory-server.md:1028` says the memory and code engines are independent).
Cost: the advertised core capability is silently absent on a fresh install, the single signal a caller sees points away from the actual gap, and an agent instructed to relay the remedy verbatim tells its human the problem is code-only. Fix: emit the analogous warning on the memory leg while `embedding.provider` is unset, say "the code section is FTS5-only" in the code warning, and add the activation step to the first-run flow.
*Provenance: filed independently by Lane F, Lane G and Lane G.*

### F7 — `doctor`'s failure paths bypass its own exit-code contract: a corrupt bank and an out-of-range timestamp both exit 15, the second with a raw .NET parameter message [MEASURED]

**Severity:** MEDIUM

**Evidence:** Lane E: `printf 'this is not a sqlite database at all, just text' > …/corrupt/memory.db; ai-raccoon --data-root …/corrupt doctor` → empty stdout, `EXITCODE=15`, stderr `ai-raccoon: SQLite Error 26: 'file is not a database'` — `DoctorCommands.cs:41-51` wraps only `OpenBankReadOnlyAsync`, and Microsoft.Data.Sqlite defers that error to the first statement (`SchemaDoctor.DiagnoseAsync`, `:55`), which escapes to the catch-all at `ConfigCommands.cs:168-172`; `ExitCode.cs:44` defines 15 as "a CLI verb's own argument failed validation", and the derived doctor table (`docs/how-to/configure-ai-raccoon-server.md:377-385`, asserted exact by `HowToExitTableTests`) lists doctor's codes as {0,1,2,19,20,22,24}. Lane F (independently): with `model_migration.started_at = 1758538800000` (milliseconds in a seconds column), `doctor` prints `Valid values are between -62135596800 and 253402300799, inclusive. (Parameter 'seconds') Actual value was 1758538800000.` and exits 15 — `DoctorCommands.cs:317-319`'s `DateTimeOffset.FromUnixTimeSeconds` is unguarded, while the same function degrades other malformed migration reads to `model migration: unreadable` with `status: HEALTHY` and exit 0.
Both replace the diagnosis with the wrong signal in the tool an operator runs *because* the bank looks wrong. Fix: catch the deferred open/diagnose failure and return the documented open-failure code, and format the timestamp defensively in the spirit of the guard-tripped arm.
*Provenance: filed independently by Lane E and Lane F.*

### F8 — Flake tolerance has no live mechanism at either end: retry-recovered failures leave no trace, and the ledger they would feed has no consumer [MEASURED]

**Severity:** MEDIUM

**Evidence:** Lane H: a temporary `[RetryFact]` probe reported a failed run with 3 attempts recorded only in a side file, and an existing retry-recovered test (`RetriesUntilItPasses`, throws on attempt 1, passes on 2) reports `total: 1, failed: 0, succeeded: 1` with **0** occurrences of `retry|transient|attempt` in the log; `tests/AiRaccoon.Tests/Unit/RetrySurfaceGateTests.cs:20-38` mandates retry attributes for every file under `E2E/` and `Integration/` and every Slow/Nightly file (1835 `RetryFact`/`RetryTheory` hits in the tree). Lane I (independently): `known-flakes.json` is referenced outside `docs/` only by `scripts/nightly-triage.py:41,94` and its test; no workflow invokes that script (`build.yml:305` deliberately does not), so the ledger's single entry (`BackendLauncherTests…`, #395) can no longer tolerate anything, and `.ai-badger/status-notes.json:206` already records the script as "orphaned … revive or remove".
Together: for the ~1000-test integration/E2E surface the run cannot distinguish "passed" from "failed twice, passed on the third try", while the record-and-tolerate policy has no runner — so a genuinely intermittent defect can keep a PR green indefinitely. Fix: emit a retry counter/diagnostic on recovery and give the ledger a runner (or remove both halves deliberately).
*Provenance: filed independently by Lane H and Lane I.*

#### Lane A — Architecture & layering

### F9 — The two size ratchets no longer bind, and the delta's growth landed predominantly outside them [MEASURED]

**Severity:** MEDIUM
**Evidence:** `tests/AiRaccoon.Tests/Unit/Storage/SqliteMemoryStoreSizeRatchetTests.cs:80-81` pins `MaxLines = 1066`, `MaxMembers = 27`; measured `wc -l src/…/Memory/SqliteMemoryStore.cs` = **1046**, and the port's declared members = **26** (was 27 at `155f281e`; the delta removed `ConfigureEmbeddingAsync` per ruling D4). Both tests pass: `dotnet test --project tests/AiRaccoon.Tests --no-build --filter "FullyQualifiedName~SqliteMemoryStoreSizeRatchetTests"` → `Passed! total 2, failed 0`. Per-file line growth `155f281e..HEAD` (recomputed from both blobs): `MemorySchema.cs` 1446→**2216 (+770, now the largest source file in the repo)**, `SyncService.cs` 469→**916 (+447)**, `SettingsCommands.cs` +168→769, `CliCommandTree.cs` +63→679; the gated `SqliteMemoryStore.cs` grew +65 and stayed under its cap.

The file whose whole raise history is "LOWERED, not raised" (eight `LOWERED` entries, not five — **verifier-corrected, G5**) is now the file that grew least, and the D4 removal freed exactly the member slot this gate exists to protect. `MemorySchema`
(DDL + 14 migration rungs + one-shot repairs + `AsyncLocal` test hooks in one static class) has no
cap at all, so the delta's +770 lines there were invisible to every gate. Smallest fix: lower
`MaxLines`/`MaxMembers` to today's measurements and add one cap for `MemorySchema.cs` and `SyncService.cs`.

### F10 — Project-id enforcement rides a mutable process-static global that Core's own key factories read [READ]

**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon.Core/Projects/ProjectIdAliasMap.cs:33-60` (`static _default`, `Lock`, `Default`, `ReplaceDefault`); readers `src/AiRaccoon/Tools/ToolGate.cs:68-69`, `src/AiRaccoon.Infrastructure/Watch/WatchService.cs:122`, `WatchDigestExecutor.cs:39`, `Sync/SyncService.cs:29,62`, `src/AiRaccoon/Observability/ToolTelemetry.cs:116,125`, `src/AiRaccoon/Setup/Cli/Commands/WatchCommands.cs:149`, and three Core key factories: `Core/Access/AccessModePolicy.cs:15`, `Core/Watch/WatchConfigKeys.cs:15,19`, `Core/Ingestion/IngestScopeKeys.cs:24`; the sole writer is `Infrastructure/Sqlite/ProjectIdAliases.cs:125`, reached from three reload legs (repair job `:109`, startup warm, sync pull).

ADR-0102 rules the cached `Default` and even records that "three missing test teardowns bit once", so
this is a ruled trade — but the *shape* is an ambient global rather than a port: `AccessModePolicy.ProjectKey`,
`WatchConfigKeys.EnabledKey` and `IngestScopeKeys.ScopeKey` are stateless key helpers that now read a
mutable global; substitution requires mutating the process-wide static (`ProjectIdAliasMap.ReplaceDefault`,
as the repo's own `ToolGateRetiredIdTests.cs:53,79,101,126` does under a collection fixture) rather
than injection; and enforcement state is per-process rather than per-bank. **Verifier-corrected (G5):**
the previously filed "a unit test cannot substitute a map" is false, and no doc calls these helpers
"pure". Smallest fix: take the map as a parameter in those three factories and in `ToolGate`,
leaving `Default` as the process cache behind a port.

### F11 — `ISearchParametersSettings` is a dead nine-member port in Core; its implementation never landed [MEASURED]

**Severity:** LOW
**Evidence:** `src/AiRaccoon.Core/Memory/ISearchParametersSettings.cs:10` — exhaustive search,
`grep -rn "SearchParametersSettings" src tests benchmarks --include=*.cs` → **1 hit (the declaration)**;
`grep -rn "ISearchParametersSettings" .` → the file, `docs/adr/0083-search-parameters-unified-source.md:49`, `docs/work/2026-08-20-search-parameters-plan.md:107,129,182`.
The plan specifies `SqliteSearchParametersSettings : ISearchParametersSettings`; the interface's own
doc says the per-search path uses the store's batched snapshot "instead of this interface". So the
shipped seam is the plan's abandoned first draft: it compiles, it invites a future contributor to
wire to it, and nothing answers. Smallest fix: delete the file, or implement the plan's class.

### F12 — No gate bounds Core's third-party surface: the reference allowlist the repo's own invariant asks for does not exist [READ]

**Severity:** LOW
**Evidence:** `LayeringRulesTests.cs:39-49` (Rule 1) asserts only that the names of the two sibling *project* assemblies are absent; `:63-88` (Rule 2) filters dependency targets starting with `System.Net.`. The invariant `.ai-badger/invariants/clean-architecture-layering.md` states the preferred shape: "Prefer a **reference allowlist**: assert the domain assembly's `GetReferencedAssemblies()` is a subset of an approved set … it rejects the next infrastructure package nobody thought to deny". Measured current state (metadata of the built dll, `strings src/AiRaccoon.Core/bin/Debug/net10.0/AiRaccoon.Core.dll`): exactly three non-BCL references — `CommunityToolkit.Diagnostics`, `FluentValidation`, `System.Numerics.Tensors` (ADR-0001, ADR-0017) — plus BCL.

Core is clean today; the gap is that `PackageReference Include="Azure.Storage.Blobs"` in
`AiRaccoon.Core.csproj` would fail nothing (its dependency targets are `Azure.*`, not `System.Net.*`).
Smallest fix: one `[Fact]` asserting `CoreAssembly.GetReferencedAssemblies()` ⊆ the three approved
names plus a BCL prefix, with the empty-set positive control the file already uses elsewhere.

### F13 — `ProjectIdAliasMap.LoadFromFile` is production-dead, test-only, and the only file read in Core [MEASURED]

**Severity:** LOW
**Evidence:** `src/AiRaccoon.Core/Projects/ProjectIdAliasMap.cs:176-190` (`File.ReadAllText`); `grep -rn LoadFromFile src tests benchmarks` → declaration + 3 tests in `tests/…/Unit/Projects/ProjectIdAliasMapTests.cs:120,160,170`, **no production caller** (the CLI reads the map itself: `Setup/Cli/Commands/ProjectIdsRepairCommands.cs:90` `File.ReadAllText(mapPath)` then `FromJson`). Whole-Core filesystem census: `grep -rn "\bFile\.\|Directory\.Exists" src/AiRaccoon.Core` → 2 hits (this one, plus `Ingestion/IngestPath.cs:102` `File.ResolveLinkTarget`).

The domain assembly performs filesystem I/O for a method nobody calls, and three tests exist to keep
that dead path covered — the D4-style fix is deletion, after which Core's remaining filesystem touch
is the symlink check the ingest containment rule genuinely needs.

### F14 — A test double is shipped in the production Infrastructure assembly, and the tests don't use it as the single fake [MEASURED]

**Severity:** LOW
**Evidence:** `src/AiRaccoon.Infrastructure/Sync/FakeCloudStore.cs:7` — `public sealed class FakeCloudStore : ICloudStore`, the **only** `Fake*` type under `src/`; no production caller (`SyncCloudStoreFactory` resolves S3/Azure/Null), consumers are tests. Four test files nevertheless define their own private copies (`Unit/Mcp/MemoryToolsAccessModeTests.cs:365`, `Unit/Mcp/MemoryToolsTests.cs:1087`, `Unit/Observability/MemoryToolsInstrumentationTests.cs:304`, `McpExceptionPathInstrumentationTests.cs:240`), while the shipped one is used by the sync merge tests.

Cost: the packed `ai-raccoon` tool publishes a store whose purpose is to lie about the real one, and
because four private fakes coexist, an `ICloudStore` contract change can leave the shipped fake
silently wrong with no production caller to notice. Smallest fix: move it to the tests project and
fold the four private copies into it.

### F15 — `IMemoryStore` still carries the four duplicated settings members, and its effective surface is 28 methods on a ten-dependency class [MEASURED]

**Severity:** LOW
**Evidence:** `src/AiRaccoon.Core/Memory/IMemoryStore.cs:77,80,103,107` (the four settings members), each delegating at `Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:572-580`; `ISettingsStore.cs`'s own doc still says "`IMemoryStore`, which still exposes the same four members and delegates here". Declared members 26 + 2 inherited from `IModelMigrationStore` = **28**. `SqliteMemoryStore.cs:23-33` is a 10-parameter primary constructor (plus a 12-parameter overload at `:40` that chains to it); the partial family is 2539 lines over 21 files, with 24 of the 26 declared members still implemented in the 1046-line main partial.

The 0821 lane's F15 is unchanged and its "CLI never writes the bank through `IMemoryStore`" guarantee
is still convention-only — `Settings/SettingsEndpoint.cs:72` documents the bypass it would create.
Removing the four members costs ~8 lines at 572-580 plus call sites that reach settings through the
store — `Access/MemoryAccessGuard.cs:11` is the sharpest (`IMemoryStore store` whose only use in the
class is `GetSettingsByPrefixAsync`, so `ISettingsStore` drops in unchanged); it is also the seam
that would let the port fall back under a tightened cap.

### F16 — Two classes keep a narrow constructor whose hidden behaviour is "does nothing" [MEASURED]

**Severity:** NIT
**Evidence:** `Infrastructure/Maintenance/BankMaintenanceHostedService.cs:25-54` — the primary constructor leaves `_jobRunner = null` / `_jobs = []`; the 9-arg overload at `:40` chains in (`6` test constructions). `Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:23-58` — the 10-arg primary substitutes `NoOpNoiseEntryStore.Instance` / `NoOpNoiseShadowObserver.Instance`; the 12-arg overload adds the real ports (`12` test constructions). Production takes the wide path: `Setup/AppRegistrations.cs:335` `AddRequiredSingleton<IMemoryStore, SqliteMemoryStore>()`, `:218` `AddHostedService<BankMaintenanceHostedService>()`.

The delta documented the maintenance service's version as an intentional test seam; the store's is
undocumented, and nothing in `LayeringRulesTests` covers either. A test built narrowly exercises a
graph that silently has no noise filtering (store) or zero jobs (maintenance) — the exact way a
green suite can describe a product that does not exist. Smallest fix: document the store's narrow
constructor like the maintenance one, or make the no-op ports explicit arguments.

### F17 — ADR-0104's removal contract is implemented as written and holds live; the residue is exactly what the ADR accepted [MEASURED]

**Severity:** NIT (positive result)
**Evidence:** installed tool `1.42.5+8a1f1dde` (== HEAD product code) against `--data-root docs/work/2026-09-22-project-scope-review/a-architecture/data`:
`--transport stdio` → exit **9**, stderr `Hint: --transport stdio was removed; run bare 'ai-raccoon' for the proxy or 'ai-raccoon serve' for HTTP.` + `Cannot parse argument 'stdio' as --transport: expected proxy|http.`; `--transport https` → exit **9** with the same rejection; `serve --transport stdio` → exit **15** `Unrecognized command or argument '--transport'.`; `--help` → `--transport <proxy|http>`. `--transport proxy serve --port 8912 --idle-timeout 5s` → `ai-raccoon: serve ignoring --transport Proxy; serve always uses http` (EventId 602, twice: stream + logger), then a clean exit 0.
Code matches: `Setup/McpTransport.cs:3-9` keeps `Stdio`/`Https` as parse-rejected members (the ADR's keep-enum mechanics constraint), `Setup/McpServerSetup.cs:43-47` throws for them defensively, `Setup/Cli/CliArgs.cs:67-99` carries hint and rejection.

The residual `ServerConfig.Transport` plumbing is 3 read sites and one warning; that is ADR-0104 Q3's
documented long-term exception, not drift. Nothing to fix — recording it as the lane's live check
that the removal landed rather than as a defect.

### F18 — The 0821 port-placement lead is refuted by measurement; what remains is the missing generic rule [MEASURED]

**Severity:** NIT (withdrawn lead)
**Evidence:** both ports the 0821 architecture lane flagged have moved — `IPromotionQueuePruneStore` → `src/AiRaccoon.Core/Memory/`, `IWatchRegisteredStore` → `src/AiRaccoon.Core/Watch/` (the **only** file deleted in the entire `src/` delta). Tool-layer dependency census: **15** Core-declared ports reach `src/AiRaccoon/Tools` (`IMemoryStore`, `IPromotionQueue`, `IWatchService`, `ICodeSearchService`, `ISearchDispatcher`, …) against **3** Infrastructure-declared interfaces (`ISyncService`, `ISyncCloudStoreFactory`, `ISweepService`, all pre-existing); of the **6** interfaces the delta added to Infrastructure (`ICodeEmbedder`, `ICodeTokenizer`, `ICodeIngestor`, `IWatchScanInitiator`, `ISyncBlobAuthenticator`, `IReportsOutstandingRows`) **zero** reach the tool layer, while the delta added 8 Core ports. `LayeringRulesTests.cs:151-160` (Rule 4) still pins only `IMetricsReportService` by name.

So F9 of the 0821 lane is fixed and its successor pattern did not recur: new cross-layer contracts
went to Core. The gap is that the outcome is unguarded — Rule 4 remains a single-name pin, and the
owner's 0821 question ("add a generic port-placement rule instead of per-name pins") still has no
answer because the delta's ruling set (D1-D6, S1-S3, Q1-Q2, C1-C3) never included it.

#### Lane B — Retrieval, embedding & ranking (the domain algorithm)

### F19 — `evidenceByHash.cosine` is `alpha × (content cosine)` whenever a row has no structure vector, so the one "cross-query-comparable" signal is not cross-row comparable [MEASURED]
**Severity:** MEDIUM
**Evidence:** Live bank had `vec_entries_rowids` = 67, `vec_structure_rowids` = 0. Query `source affinity ranking`, an entry written with exactly the query text as its content, then cosine of the two stored `entries.embedding` blobs vs the served evidence:
`affinity ADR: content cosine=0.607094263 reported=0.3035471588373184 ratio=0.500000`; `fusion note: content cosine=0.340864489 reported=0.17043226957321167 ratio=0.500000`. Code: `StructureFusion.Fused` scores an absent structure sim as `0.0` (`src/AiRaccoon.Infrastructure/Embedding/StructureFusion.cs:26-31`, alpha default 0.5), `SqliteMemoryStore.cs:834-841` feeds that fused score as the vector leg's `Ranking`, and `ReciprocalRankFusion.cs:69,100-108` copies it into `RetrievalEvidence.Cosine`.
`memory_search`'s own description calls this field "cosine (the fused vector similarity when a vector leg participated)" (`src/AiRaccoon/Tools/MemoryTools.cs:113-120`) and the Stage-1 plan justifies the field as "the only cross-query-comparable magnitude in the pipeline"; for any row without a structure embedding, a consumer reading it as a similarity is off by 2× with no marker (`participatingLegs`/`legs` only ever name `fts`/`vector`). The partial case is real, not hypothetical: `EntryEmbedder.EmbedIfConfiguredAsync` (`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:240-248`) creates a structure embedding only when the value's parsed heading path is non-empty, so headed ingested chunks blend the structure half while heading-less `memory_write` rows (and every row of a bank created before structure vectors) are halved. Smallest fix: carry the raw content cosine in the evidence (structure contribution tracked separately), or mark the absent-structure case so a threshold on `cosine` means one thing.

### F20 — The new default relative floor silently caps an explicitly raised `limit`: `limit=100` returns 41 with no truncation signal [MEASURED]
**Severity:** MEDIUM
**Evidence:** one project, one query, 66 fusion candidates: `memory_search(limit=100, minRelativeScore=0.0)` → 66 served; `memory_search(limit=100)` (floor defaults to 0.6) → **41 served**, last ranking `0.60396` (= 61/101); `limit=50` → 41; `limit=45` → 41. Response keys are `results`/`evidenceByHash`/`fusionStats` only — no warning or truncation marker (`MemoryTools.SearchResultList`, `MemoryTools.cs:478-486`), and `SqliteSearchQualityService.RecordSearchAsync` stores `result_count` but never `limit` (the column does not exist).
ADR-0096 (commit `9d2a9849`, in this delta) turned the floor on by default; because the served ranking is rank-derived (`(k+1)/(k+rank)`, ADR-0047), floor 0.6 at k=60 is exactly "keep ranks ≤ 41", so a caller who explicitly asks for 50/100 gets a short response it cannot distinguish from "the bank has 41 matches". ADR-0096 documents `minRelativeScore=0` as the full-recall escape hatch and lists cap-vs-exact as "not addressed" — but the `limit` schema text ("Maximum results (default 8)") still promises the cap, and no response field reports the cut. Smallest fix: state the interaction in the `limit` description and mark the truncation (count of candidates dropped by the floor), or exempt explicitly raised limits from the default floor.

### F21 — The bundled model file can be loaded for embedding without its sha256 ever being enforced; only manifest directories re-verify at activation [READ]
**Severity:** LOW
**Evidence:** `EmbeddingService.CreateLocal` sends a model **file** path to `new OnnxEmbeddingGenerator(modelPath, bundledTokenizer, BundledDescriptor, …)` with no hash check (`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:342-369`), while the manifest **directory** branch calls `manifestDescriptor.Load`, which hashes every pinned file and refuses on mismatch (`EmbeddingManifestLoader.cs:63-77` — the D1 fix, commit `ccf21813`).
Bundled file resolution only walks parent directories for the first existing `Models/model_qint8_arm64.onnx` (`BundledModel.cs:70-85,139-155`); the pinned-hash check exists in `EnsureAsync`/`LocateVerified` (`BundledModel.cs:37-46,131-137`) but it is invoked best-effort at boot and a failure only logs a warning (`Setup/Models/EmbeddingAvailability.cs:14-28`, called from `NodeRunner.cs:115`). Concrete scenario: with the tool directory writable (it is, in a user install) and no network, replace `Models/model_qint8_arm64.onnx` in place; the boot check tries to re-download, fails, logs "Bundled embedding model unavailable", the server boots, and every subsequent write and query is embedded with the tampered weights while the manifest path would have refused. Smallest fix: verify `BundledModel.ModelSha256` in `ResolveModelPath` (or fail closed when `EnsureAsync` cannot restore the pinned bytes and the on-disk hash differs).

**Verified sound (positive checks, not findings).** Stage-1 shipped math matches the plan to the last digit on live traffic: for the 2-leg k=60 defaults, `fusionStrength` = `raw/maxPossible` reproduces exactly (`vector rank 2 → 61/124 = 0.491935483…`, rank 6 → `61/132 = 0.462121…`), `maxPossible = 2/61 = 0.032786885…`, `topMargin = (raw₁−raw₂)/raw₁ = 0.508064516…`, even-count median = mean of the two middles, and `fusionStats` was byte-identical across `limit` 8/34/50/100/200 and floors 0.0/0.6/0.9. The evidence's `fts` ranks reproduce sqlite's own `bm25` order (`MATCH 'bank'` → `91f13a24…` rank 1 at −1.530556). `SimFromDistance = 1 − distance` is correct for this bank's actual DDL (`vec0(… distance_metric=cosine)`, read from `sqlite_master`), and the delta's BLOCKER B1′ is genuinely closed for manifest directories while the on-disk model/vocab match the golden pins. On evaluation honesty I found no undocumented in-sample default: the tuned point (rrfK 17, weights 7:2, λ 0.115, α 0.99) was **not** shipped (`SearchParameterSettingsKeys.cs:28-36` still k=60/1:1/0.1/max), the tuning report flags its own 14/100 regressions for owner review, ADR-0090 discloses that the held-out partition is now the whole catalog (and why three queries were revised), and ADR-0096 admits its default rests on one probe query with validation still owed. Retrieval pytest subset in the worktree: `111 passed, 3 skipped` (`test_llamaindex_harness_fts/fusion/evaluate`, `test_retrieval_tuning_scopes`, `test_retrieval_tuning_eval_corpus`).

#### Lane C — Memory lifecycle: ingest → extract → promote → rate → degrade → share

### F22 — A pending `agent-requested-share` promotion request is destroyed by the next propose pass's scorer-version auto-clear and is never re-admitted [MEASURED]
**Severity:** HIGH
**Evidence:** `memory_write(context:"shared")` → `promotion_queue` row `{score 1.0, scorer_version 0, reasons ["agent-requested-share"]}`; `memory_share_extract(mode:"propose")` → `{candidates:[], meta.waitingPromotionsCount:0}` and `SELECT count(*) FROM promotion_queue` → 0. Code: `SharedExtractionRunner.cs:34` clears every row with `scorer_version != PromotionScorer.Version` (`PromotionScorer.cs:16` = 2) before ranking; the write path stamps the default 0 (`PromotionQueue.cs:15`, `MemoryWriteService.cs:57-60`).
The re-rank that is supposed to re-admit cleared rows scores *entries*, not queue rows, so a short agent note below the 0.4 floor never comes back — the clearing is terminal for exactly the candidates the ADR calls strongest.
Cost: the write response (`Reason = "queued-for-promotion: agent-requested-share"`, `MemoryWriteService.cs:65`) claims a review that a later routine propose silently cancels; the memory row survives in the project, the agent's explicit intent does not.
`docs/adr/0067-naming-shared-asks-for-promotion.md:45-47` names this as the failure mode the feature exists to avoid; the guarding test (`SharedWriteIsAPromotionRequestTests.cs:36-40`) stops at a `FakePromotionQueue`, so the real queue interaction is untested.
Smallest fix: stamp agent-requested rows with the current `PromotionScorer.Version` (or exempt the reason from `ClearStale`).

### F23 — `rating` is recomputed only on a search hit and from `created_at`; the sweep branches on that stale, age-driven cache, so the reaper does not measure "not used" [MEASURED]
**Severity:** MEDIUM
**Evidence:** TTL'd entry backdated 200 days with stored rating 0.5 → `memory_sweep(dryRun:true)` `{candidates:[]}`; one search later (`access_count 1`, `rating 0.00541372`) → the same sweep lists it, and a real sweep deletes it. An entry searched 5× with `last_accessed_at` 0 s old but `created_at` 200 d old → `rating 0.00738234` → candidate. The only writer of `entries.rating` in `src/` is `MemorySql.cs:625-630` (`BumpAccess`: `pow(0.5,(now-created_at)/86400/halfLife)*(1+(access_count+1)*0.1)`); `SweepService.cs:40` reads the stored value and nothing reads `last_accessed_at` for it.
Consequence: a TTL'd entry nobody ever touches can never expire (`canEverExpire:false` is the only warning), while an entry used minutes ago can be swept once it is old enough — creation age, not disuse, drives the rating.
The ratified plan `docs/plans/2026-08-09-memory-decay-implementation-plan.md:3` says "planned, not started"; its WP1 (`:59`, `:68` — "treat the stored rating column as a cache … nothing may branch on it without recomputing") describes this exact state. Report is "live, known, unshipped", not novel; WP1 is the fix.

### F24 — Supplying `context` together with `workspace_id` silently overrides the sandbox: the row is committed to the project, not the outbox [MEASURED]
**Severity:** MEDIUM
**Evidence:** inside an active workspace, `memory_write(workspaceId=W, context:"design-notes")` returned `context:"design-notes"` and the row landed `scope='custom', context_label='design-notes', workspace_id NULL` (id 11); `memory_workspace_status` reported outbox count 0. With `context:"shared"` the row landed `scope='project'` (id 84) plus a promotion candidate, again outbox count 0. `ContextResolver.cs:10-17` returns `request.Context` whenever it is non-empty regardless of `workspaceId`; `MemoryWriteService.cs:44-49` rewrites `shared` to the project context while leaving `workspaceId` to be ignored.
Cost: notes the caller believed were soaking in the outbox are committed and project-searchable, and consolidation/discard never sees them. The tool descriptions conflict for this combination (`MemoryTools.cs:57` "naming a workspace_id routes them into that isolated workspace" vs `:62` "context … instead of the default project/workspace context").
Graded MEDIUM rather than HIGH because the response's `context` field does disclose where the row went, so an attentive caller can notice. Smallest fix: make `workspace_id` win when both are set (or refuse the combination) and align the two descriptions.

### F25 — After a permanent discard, rewriting the same content as a shared request reports `queued-for-promotion` while nothing is queued [MEASURED]
**Severity:** MEDIUM
**Evidence:** write `context=shared` → queue 1; `memory_promotion_discard(hash)` → `{'discarded':1}`, queue 0, `promotion_discards` 1; rewrite the identical content with `context=shared` → response `reason: 'queued-for-promotion: agent-requested-share'`, queue count still 0. `MemoryWriteService.cs:65` sets the reason unconditionally; `PromotionQueueSql.cs:9-13` refuses the upsert via `NOT EXISTS (… promotion_discards …)` so nothing is enqueued.
Cost: the response asserts queue state that does not exist; an agent that trusts it looks for a candidate in `memory_promotion_list`, finds none, and cannot tell a deliberate rejection from a lost write. Smallest fix: return `ProposeOutcome.Upserted` per candidate, or an explicit "discarded-earlier" reason.

### F26 — One multi-chunk `memory_write` creates N rows but reports one hash and one path; `memory_delete` on the reported hash removes exactly one row [MEASURED]
**Severity:** MEDIUM
**Evidence:** a 140-paragraph write produced 140 rows under one content-addressed path (ids 85-224, `SELECT count(*) … WHERE path='727944d5….md'` → 140); `memory_delete(returnedHash)` → `{deleted:1}` and the count fell to 139; a follow-up search returned 8 hits from the remaining rows. The write inserts every chunk (`SqliteMemoryStore.cs:125-145`) and returns only `chunks[0]`; `MemorySql.cs:167-168` deletes `WHERE hash = @hash AND project_id = @projectId`; the MCP surface has no delete-by-path verb (`MemoryTools.cs:41-42`).
Cost: "delete what I wrote" removes 1/N and reports success; the rest stays searchable and promotable with no supported single verb to remove it (only context-wide delete). The MCP contract's per-chunk wording mitigates this, but the write result gives the caller no way to learn N.
All 140 rows carry `chunk_index=-1/total_chunks=0` because the write-time recompute is gated on `sourceFile` (`SqliteMemoryStore.cs:160-164`) — expected (the sentinel is handled in `SourceAffinityRanker.cs:73`), not the finding. Smallest fix: report the chunk count/hashes in `WriteResult`, or let delete target the write's path.

### F27 — `memory_set_ttl` on a hash this project can read from the shared tier refuses with a factually false "No entry with hash …" [MEASURED]
**Severity:** LOW
**Evidence:** on one shared row, `memory_get(projectId, hash)` returned the entry, then `memory_set_ttl(projectId, hash, 1)` → `unknown-hash: No entry with hash '690d2306884e…' in project 'c-lane-p1'`. `UpdateEntryTtl`/`SelectEntryMetadata` filter through `ProjectRows.Of()` (`MemorySql.cs:669-686`, `ProjectRows.cs:24-26`), which excludes shared rows by design; `ForgettingPolicyService.cs:56-66` maps the resulting `false` to `UnknownHashException`.
Cost: the caller is told the hash is unknown while it is readable, which invites destructive "fix it" reactions; the accurate answer is "shared entries are sweep-exempt and carry no TTL". Smallest fix: word the refusal from the found row's scope.

### F28 — `promotedHashes` reports the source (project) hash; the created shared row has a different, unreported hash [MEASURED]
**Severity:** NIT
**Evidence:** promote returned `promotedHashes: ['f3e399a780…']` while the shared row created is hash `5393d4b0489b…` with path `shared/e0d91590….md` (`SELECT hash, path FROM entries WHERE scope='shared'`). `PromotionQueueService.cs:144-148` adds `row.Hash` (the queue/source hash) and ignores `shared.Hash`; the project row's hash derives from `<valuehash>.md` and the shared row's from `shared/<valuehash>.md`, so they can never coincide.
Nothing breaks (the value is identical and searchable via `scope:"shared"`), but the field cannot address the artifact it names.

#### Lane D — Data access, schema & persistence

### F29 — `memory_delete_context` deletes committed content with no tombstone, so the next sync resurrects it [MEASURED]
**Severity:** HIGH
**Evidence:** live, on a scratch bank (`data-d4`) with my own server and the real MCP tools:
`memory_write {projectId: lane-d, content: "context-delete resurrection probe", context: ctxA}` → hash `b06dcfbd…`; `dotnet run -- sync <bank> lane-d <cloud>` → `sent=1 received=0` (row on the remote); `memory_delete_context {projectId: lane-d, context: ctxA}` → `{"deleted": 1}`; `sqlite3` → `SELECT count(*) FROM entries` = `0`, `SELECT count(*) FROM sync_tombstones` = `0`; `sync` again → `received=1`, and the row is back with the same hash/scope/label.
`SqliteMemoryStore.DeleteContextAsync` (src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:302-321) is a bare `DELETE FROM entries WHERE {filter}` — it writes no `sync_tombstones` row, unlike its sibling `DeleteCoreAsync` (:744-785, which does). The merge's entries leg (SyncService.cs:440-455) re-inserts any remote row without a matching tombstone, so the documented "delete every entry under this context" (the tool's own description, and ADR-0052 says `Destructive` means "reaching committed memory" and names `memory_delete_context` explicitly) does not stick as soon as sync is configured.
Smallest fix: in the same transaction, `INSERT OR IGNORE INTO sync_tombstones SELECT project_id, hash, scope, @now FROM entries WHERE {filter}` before the delete, exactly as `ProjectIdsRepair.FoldEntriesAsync` does for its dropped ids (ProjectIdsRepair.cs:202-213: `INSERT OR IGNORE INTO sync_tombstones … SELECT … FROM entries WHERE project_id = @dropped …` then the DELETE).

### F30 — `memory_delete` writes at most one tombstone for N deleted rows, and none when the first row is workspace-scoped [MEASURED]
**Severity:** HIGH
**Evidence:** live end-to-end on `data-d3` (my server, real tools): workspace write of content C → row id 5 (`scope NULL, workspace_id 01a0c93c…`); committed write of the same C → row id 6 (`scope project`), **same hash** `09b78d26…` (the write path's hash is `ContentHash.Of(path, value)` and both writes derive the same value-addressed path — `memory_write` accepts a workspace write and later a committed write of the same content, they are separate buckets); `memory_sync` → push (the workspace row is stripped from the snapshot, the committed twin reaches the remote); `memory_delete {projectId: lane-d, hash: 09b78d26…}` → `{"deleted": 1}`, `SELECT count(*) FROM entries WHERE hash='09b78d26…'` = `0` (both rows gone), `SELECT * FROM sync_tombstones` = **empty**; `memory_sync` again → `received=1` and `hash=09b78d26c3 scope=project` is back.
`DeleteCoreAsync` reads `rowScope` with `QueryFirstOrDefault` (`SELECT scope FROM entries WHERE hash = @hash AND project_id = @projectId AND (@scope IS NULL OR scope IS @scope)`, :753 — **verifier-corrected (G1)**: there is no `LIMIT` or `ORDER BY`; `QueryFirstOrDefault` takes whichever row SQLite returns first) and then `DELETE FROM entries WHERE hash=@hash AND project_id=@projectId AND (@scope IS NULL OR scope IS @scope)` (:761) can delete many rows; only one tombstone is written, and only `if (deleted > 0 && rowScope is not null)` (:765-772). A workspace row's scope is NULL, so selecting it first writes zero tombstones for a committed row that was just deleted. With two *committed* rows deleted, the single tombstone carries one of the scopes, so the other scope's deletion is unrecorded (the merge's suppression compares `t.scope = COALESCE(r.scope,'workspace')`).
Smallest fix: derive the tombstone set from the rows actually deleted (`INSERT … SELECT DISTINCT project_id, hash, COALESCE(scope,'workspace'), @now FROM entries WHERE <same predicate>` before the DELETE), not from one arbitrarily-ordered probe.

### F31 — Re-creating deleted content with the same hash is silently deleted again by the next sync (D27, still live) [MEASURED]
**Severity:** HIGH
**Evidence:** full statement-level and end-to-end repro in `docs/work/2026-09-22-project-scope-review/d-data-access/harness` (real `SyncService`, `FakeCloudStore`): push a fact; delete it locally and write its tombstone (the shape `DeleteCoreAsync` produces); `memory_sync` (push the tombstone); re-insert the identical content (identical hash); `memory_sync` again → `local rows BEFORE sync 3: 1`, `local rows AFTER sync 3: 0`, `RESULT: the re-added fact was silently deleted by the sync merge`.
`SyncService.cs:518-523` — `DELETE FROM entries WHERE (hash, COALESCE(scope,'workspace'), project_id) IN (SELECT hash, scope, <folded project_id> FROM remote.sync_tombstones)` has no `created_at`/`deleted_at` comparison. This is `docs/work/2026-08-07-moe-d-tests.md` D27 (Medium) and item 1c of `docs/work/2026-08-07-moe-integrated-plan.md`; nothing changed it in the 441-commit delta (git log on that range shows only the project-fold edits), and `SyncServiceTests.MemorySync_TombstonePropagation_NoResurrection` (tests/…/Integration/Sync/SyncServiceTests.cs:235) never re-creates the tombstoned hash. Smallest fix: `AND entries.created_at <= (SELECT t.deleted_at …)`, or delete the tombstone when a write re-creates that (project, hash, scope).

### F32 — `doctor` reports HEALTHY for a bank whose bucket index lost its uniqueness, and nothing ever repairs it [MEASURED]
**Severity:** MEDIUM
**Evidence:** on a copy of a healthy scratch bank: `sqlite3 exp2/memory.db "DROP INDEX uq_entries_committed_bucket; CREATE INDEX uq_entries_committed_bucket ON entries(path,hash,project_id,scope,COALESCE(context_label,''));"` → `ai-raccoon --data-root …/exp2 doctor` → `status: HEALTHY`; two identical bucket inserts then both succeed (`SELECT count(*) … WHERE hash='dup'` → `2`), i.e. the uniqueness FR-NM-7 relies on is gone.
`SchemaDoctor` diffs object *names* and table *columns* (`SchemaDoctor.cs:70-125`) but never an index's definition/uniqueness/partial flag, and `EnsureAsync`/`MigrateToV1Async` only probe existence (`SELECT 1 FROM sqlite_master WHERE … name='uq_entries_shared_bucket'|'uq_entries_committed_bucket'`, MemorySchema.cs:1782-1790; `CREATE UNIQUE INDEX IF NOT EXISTS`, MemorySchema.cs:1822-1827). The same blindness covers a same-named index built over different columns — including the `COALESCE(context_label,'')` term whose absence re-admits duplicate NULL-label buckets. Smallest fix: compare `PRAGMA index_list` (`unique`, `partial`) and `PRAGMA index_xinfo` columns against the in-memory expected bank `SchemaDoctor` already builds, and rebuild a mismatched index in `EnsureAsync`.

### F33 — A tracked C# file contains literal NUL bytes, so git treats it as binary and its diffs are unreviewable [MEASURED]
**Severity:** LOW
**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/Encryption/EncryptionKeyResolver.cs` has `0x00` at offsets 2737/2754 (inside `Fingerprint`: `$"{data.Source}\x00{data.ProjectId}\x00{data.SecretId}"` written with raw NULs); `git show --stat 6750b041 -- <file>` → `Bin 4225 -> 4224 bytes`; `git grep -n Fingerprint -- <file>` → `Binary file … matches` with no line numbers. A `git ls-files -z` byte sweep of every tracked file found NULs in exactly four: this file plus the two model binaries and `tests/AiRaccoon.Tests/Resources/docs-memory.db`.
Every change to this key-resolution source since 2026-08-25 (`6750b041`) has been invisible as a diff in PR review and invisible to `git grep`/`git log -S`. Fix is mechanical: literal `"\0"` escapes produce the same string without making the file binary.

### F34 — The `entries` workspace-XOR CHECK admits a row with neither a scope nor a workspace [MEASURED]
**Severity:** LOW
**Evidence:** `INSERT INTO entries(hash,path,value,scope,project_id,workspace_id,created_at,updated_at) VALUES('h3','p3','v3',NULL,'proj',NULL,1,1);` succeeds; the both-set case (`scope='project', workspace_id='ws-x'`) fails with `CHECK constraint failed: (workspace_id IS NULL AND scope IN ('shared','project','custom')) OR (workspace_id IS NOT NULL AND scope IS NULL)`.
The DDL comment (MemorySchema.cs:126-128) claims the CHECK makes an entry "either workspace-scratch or one of shared/project/custom, never both" — but SQLite passes a CHECK that evaluates to NULL, so the *neither* case is legal. Such a row is in no tier (`ProjectRows` predicates never match it) yet counts in stats and re-keys as `'workspace'` in the sync tombstone comparison (SyncService.cs:451-453, 521-522), and it is exactly the shape the remote-merge `WHERE r.workspace_id IS NULL` guard admits (SyncService.cs:440-451). Fix: `CHECK ((workspace_id IS NULL) <> (scope IS NULL))`.

### F35 — The watcher's file-delete path shares F29's missing-tombstone shape, so a deleted file's chunks return on the next pull [MEASURED]
**Severity:** MEDIUM
**Evidence:** `SqliteMemoryStore.DeleteSourcePathAsync` (:329-368) deletes `entries` rows for the path and its subtree (plus `code_entries` and `watch_files`) inside one transaction and writes **no** `sync_tombstones` rows — the only writers of `sync_tombstones` in `src/` are `DeleteCoreAsync`, `ProjectIdsRepair`, `MemorySchema`'s v11 ladder step and `SyncService` (verified by `grep -rn sync_tombstones src/`). Its only production caller is `WatchDigestExecutor.cs:138` (file removed → cascade delete).
Consequence, by the same merge predicate F29 measured live: the remote snapshot still holds those chunk rows, the entries leg re-inserts them (no tombstone suppresses), and the miss is then pushed. No test in `tests/` writes or asserts a tombstone for this path (grep: only `SyncServiceTests`' hand-seeded tombstones and the v11 schema tests). **Verifier-upgraded to MEASURED (G1):** the verifier registered an ingest scope and a watch, wrote and synced a file, deleted it, confirmed the entries row was gone with `sync_tombstones` still 0, and watched the row return (`received=1`) on the next sync — so this path is measured end-to-end, no longer inferred from F29.

### F36 — Remote tombstones are garbage-collected against the *local* pull watermark, so a fresh remote deletion can be dropped on arrival [READ]
**Severity:** LOW
**Evidence:** `SyncService.cs:501-539` — the merge inserts remote tombstones, then runs `DELETE FROM sync_tombstones WHERE deleted_at < @watermark` where `@watermark` is this bank's *previous* `last_pull_at`, while `deleted_at` is the deleting replica's clock; `last_pull_at` is only then advanced.
A remote tombstone whose `deleted_at` predates this bank's last pull is inserted and destroyed in the same pass, so it can never suppress the hash's re-arrival — the entries leg's `NOT EXISTS … sync_tombstones` check (SyncService.cs:448-455) already ran, and the next push from a replica that still holds the row re-inserts it. The native-memory plan mandates "GC below min(last_pull watermark)", so the *rule* is intended; what is unintended is comparing two machines' clocks: a remote clock behind ours turns a fresh deletion into an instantly-GC'd tombstone. I did not build the two-machine, skewed-clock experiment (I have no way to set the two processes' clocks), hence READ.

#### Lane E — .NET language quality & reliability

### F37 — the same catch-all absorbs `OperationCanceledException`: Ctrl-C is reported as an error and exits 15 [MEASURED]
**Severity:** MEDIUM
**Evidence:** `ConfigCommands.cs:168` is a bare `catch (Exception ex)` — read in the file, not inferred from a
grep. Transcript, SIGINT at t=2.5 s while the CLI was inside the auto-start acquire:
```
$ ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/e-dotnet-quality/data5 --port 7911 settings maintenance list
stdout: (empty)      EXITCODE=15
stderr: info: AiRaccoon.Hosting.Proxy.BackendLauncher[633]
              ai-raccoon: starting the backend on port 7911
        ai-raccoon: The operation was canceled.
```
The codebase's convention is the filtered form — 14 catches carry `when (ex is not OperationCanceledException)`
(`SettingsCommands.cs:181`, `DoctorCommands.cs:252/303`, `ProxyForwarder.cs:59/81/94`, …) — and the
process-exit-code decision is the one place that does not. Costs: a script cannot tell an interrupt from a
typo (it gets the "you mistyped" code), and the user is told a cancellation is an error. The interrupt also
leaves the freshly auto-started backend running (observed PID 187 alive on port 7911, reparented to PID 1)
with nothing said about it — see F38. Smallest fix: `catch (OperationCanceledException) when
(Token.IsCancellationRequested) { return 130; }` ahead of the catch-all.

### F38 — a successful one-shot CLI command leaves an orphaned `serve` backend running for up to 4 hours, started with no `--idle-timeout` [MEASURED]
**Severity:** MEDIUM
**Evidence:** `BackendLaunchArguments.ServeArguments` (`BackendLaunchArguments.cs:46-59`) builds
`--data-root … --install-scope … serve --port N` and never passes `--idle-timeout`, so the child runs under
`DefaultOptions.IdleTimeout = TimeSpan.FromHours(4)` (`src/AiRaccoon/Setup/DefaultOptions.cs:14`); the launcher
"never kills, signals or terminates the backend — lifetime belongs to IdleWatchdog alone"
(`BackendLauncher.cs:12-16`). Transcript after a *successful* read (exit 0, 6.5 s):
```
$ ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/e-dotnet-quality/data6 --port 7912 settings maintenance list
CLI exit: 0    stderr: info: … ai-raccoon: starting the backend on port 7912
--- backend processes alive after the CLI exited ---
2433  1  …/AiRaccoon --data-root docs/work/2026-09-22-project-scope-review/e-dotnet-quality/data6 --install-scope user serve --port 7912
```
It was still alive and serving 25 s later (POST /mcp → 401 token refusal, i.e. a working endpoint).
The user's only disclosure is one `info:` line saying a backend *started*; nothing says it outlives the
command, and nothing names a stop command. What the orphan then runs unattended is the point: per the CLI's
own help the sweep reaper is ON by default, and the extractor, watch, embed drain and metrics flusher are all
live in it. ADR-0075:64-66 ratified auto-*start* and says nothing about lifetime; `docs/adr/0075` has no
occurrence of "idle"/"lifetime"/"terminate". A read-only command therefore buys up to four hours of background
writes — including deletions — on the user's bank. Smallest fix: pass a short `--idle-timeout` for
CLI-acquired backends, or stop the backend the CLI itself started when the command returns.

### F39 — a mistyped `--data-root` on any settings verb silently *creates* a bank there; the `NoBank` guard that exists for exactly this is doctor-only [MEASURED]
**Severity:** LOW
**Evidence:** fresh empty directory, read-only verb:
```
before: []
$ ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/e-dotnet-quality/data7 --port 7913 settings access show
exit: 0     stdout: default: rw     stderr: … starting the backend on port 7913
after: ['mcp-token', 'memory.db', 'memory.db-shm', 'memory.db-wal']
```
`ExitCode.NoBank`'s doc says the code exists "so a wrong `--data-root` cannot read as a healthy bank"
(`ExitCode.cs:64-66`) — that is true for `doctor` only. Every other settings verb is routed to the
auto-started server (ADR-0075 §5.1; `CliWriteOptOuts.cs:16` exempts only `encryption`), and the server's first
open runs `MemorySchema.EnsureAsync`, minting a full bank plus token file in the directory the typo named.
Cost: a typo leaves bank files behind at a path the user believes does not exist (and, per F38, a server on
it), and the next run against that path looks healthy. Smallest fix: refuse (or warn loudly on stderr) when
the CLI is about to auto-start a server against a data root whose bank does not exist.

### F40 — `WatchScanGuard.Cancel`/`CancelAll` can throw `ObjectDisposedException`: the in-flight entry's CTS is disposed concurrently with the cancel it is called on [MEASURED]
**Severity:** MEDIUM
**Evidence:** scratch harness `docs/work/2026-09-22-project-scope-review/e-dotnet-quality/harness` (200,000 iterations of
`Run(…, _ => Task.CompletedTask)` immediately followed by `Cancel`/`CancelAll`, referencing the reviewed
build's `AiRaccoon.Infrastructure.dll`) — reproduced twice, with stacks:
```
FIRST ObjectDisposedException stack:
   at System.Threading.CancellationTokenSource.Cancel()
   at AiRaccoon.Infrastructure.Watch.WatchScanGuard.Cancel(String projectId, String path) …WatchScanGuard.cs:line 74
iterations=200000 ObjectDisposed=4 otherExceptions=0 joined=200000
FIRST CancelAll ObjectDisposedException stack:
   at System.Threading.CancellationTokenSource.Cancel()
   at AiRaccoon.Infrastructure.Watch.WatchScanGuard.CancelAll() …WatchScanGuard.cs:line 88
iterations=200000 CancelAll-ObjectDisposed=9
```
Mechanism: `Cancel`/`CancelAll` capture the entry under `_gate`, release the lock, then call
`entry.Cts.Cancel()` (`WatchScanGuard.cs:66-75`, `:78-90`) — while the scan's completion path
(`Complete`, `:107-131`) removes the entry and calls `entry.Cts.Dispose()` (`:117`) after its own lock
release; a `Cancel` that captured the entry first then cancels a disposed source. Both entry points are the
product's real callers: `WatchPipeline.UnregisterWatch:124` (from `WatchService`'s `memory_watch_remove` and
the stale-registration reconcile) and `WatchHostedService.StopAsync:97` → `WatchCatchUp.CancelAllScans()`.
Measured hit rate is ~2–5e-5 per racing call (it needs the cancel to land inside the completion's disposal
window), so production frequency is low — but the consequences are not: in `StopAsync` the throw skips
`_eventSource.StopAll()` (`:98`) and `base.StopAsync` (`:105`, which awaits the pipeline loop) and surfaces on
the host's shutdown path, and in `UnregisterWatch` it skips `Unregistered?.Invoke` (`:125`), leaving
`WatchHostedService._active` holding the key — the exact D-1 invariant ("a remove-then-re-add never reads as
continuously active") the removal choke point exists to keep. Smallest fix: dispose the CTS inside the same
`_gate` critical section that removes the entry, or guard `Cancel`/`CancelAll` with
`catch (ObjectDisposedException)`.

### F41 — the #636–#640 "one line, no stack for transient contention" convention is applied to 8 call sites but not to the background pass failures that hit the same lock [READ]
**Severity:** LOW
**Evidence:** `ExceptionExtensions.IsBankBusy` (`ExceptionExtensions.cs:10-19`) is used by the extraction
service (3), the tool filter, the best-effort search-quality write, the rating bump, the VACUUM swallow and
the metrics retry. The drain pass instead logs the raw exception at Warning for the same condition —
`EmbedDrainService.DrainOnceAsync`'s `catch (Exception ex)` → `pass.Failed(ex)` +
`reporter.PassFailed(logger, corpus, ex)`, whose declaration is
`EmbedDrainReporter.cs:80-81` `[LoggerMessage(EventId = 1005, Level = LogLevel.Warning, Message = "Embed drain
pass failed for {Corpus}")] … (ILogger logger, EmbedCorpus corpus, Exception exception)`; likewise
`SweepHostedService.cs:146-149` → `ProjectFailed` (533, Warning + exception) and
`MaintenanceJobRunner.cs:135-141` → `JobRowsCountFailed` (528, Warning + exception).
Name the wrong outcome: a BUSY/LOCKED convoy on the bank while rows are pending produces a full stack trace
(and, for the drain, a failed `embed.drain` OTLP span) at the poll cadence, where #639's own rationale —
"a raw SqliteException with a stack for it is the noise class the tool path's 912 already dropped" — says the
transient case should be one line. I did not reproduce a live convoy, so this is traced, not observed; the
pass does genuinely fail and retry, which is the counter-argument, and the owner may rule the stack wanted
here. Smallest fix: classify busy first in these three catches (`when (ex.IsBankBusy())` → the deferred line),
keeping the exception for genuine faults, as `SqliteSearchQualityService.cs:36-43` already does.

#### Lane F — Consumer surface: MCP tools & CLI contracts

### F42 — `--quiet` writes `quiet.log` from two processes at once, producing torn, interleaved lines [MEASURED]
**Severity:** MEDIUM
**Evidence:** `docs/work/2026-09-22-project-scope-review/f-consumer-surface/data6/quiet.log:03,05,06` (30 lines, 3 malformed; reproduced in `data3` and `data4`, 3 malformed each); command `ai-raccoon --data-root …/dataN --port 784N --quiet settings retrieval list`; fragments observed: `f-consumer-surface/data6/mcp-token`, `2026-09-22T13:06:12026-09-22T13:06:17.9423060+00:00 [Debug] …`. Paths: `src/AiRaccoon/Hosting/Common/BackendLaunchArguments.cs:53` (the spawned backend inherits `--quiet`), `src/AiRaccoon/Setup/Logging/QuietLogging.cs:30` (one fixed path for every process), `QuietFileLoggerProvider.cs:33-34,61` (per-process lock only; two processes share the append handle).
A `--quiet` CLI invocation auto-starts `--quiet serve`, and **both** processes log to the same `quiet.log` with nothing but an in-process lock, so lines interleave mid-write. The reference makes this file the only diagnostic channel in quiet mode ("leaves no trace on the console — check `quiet.log` first"), and it is also the file the upgrade guidance tells you to read. Smallest fix: one writer per bank (serve owns the file), or an OS-level exclusive lock / per-PID file.

### F43 — the reference says every `serve --restart` failure exits `8`; the live code returns 10–14/16 [MEASURED]
**Severity:** MEDIUM
**Evidence:** `docs/reference/agent-memory-server.md:565` — "Every way the cycle can fail exits `8` with a line naming the port and the manual escape". `src/AiRaccoon/ExitCode.cs:19-23` — "8 is retired rather than narrowed"; live: `ai-raccoon --data-root …/data2 serve --restart --port 7831` → the "holds no token … may serve another data root" line, `EXIT=11` (`RestartNoToken`).
A consumer script written from the reference can never match the documented code, and the retirement's stated purpose ("a script that tested for it fails to match rather than matching the wrong case", ADR-0022) is defeated by the doc naming the retired value. The 10–16 codes all carry one-line XML docs ready to lift into the table.

### F44 — `memory_write`'s documented return shape omits `stored`/`reason`, so a refused write looks like success to a doc-driven client [MEASURED]
**Severity:** LOW
**Evidence:** live `memory_write` of a noise-policy body → `{"data":{"hash":"","path":"","context":"","createdAt":1790082461,"stored":false,"reason":"rejected by noise policy 'HermesBackgroundProcessLog'"},"meta":{…}}`, **no `isError`**; the same call with clean content → `stored:true`. Doc row `docs/reference/agent-memory-server.md:43` says the return is `{hash, path, context, createdAt}`; the Error-shapes section (line 993+) says a refusal "comes back as a normal MCP tool error (`CallToolResult.IsError = true`)". Code: `src/AiRaccoon/Tools/MemoryTools.cs:465` (`WriteResult(…, bool Stored = true, string? Reason = null)`).
The tool's own description (the agent's prompt) documents `stored`; the reference does not, and its blanket "refusal ⇒ `isError`" rule points the other way for this one case. A client that checks `isError` and then reads `hash` counts a rejected write as stored (empty hash). One-line fix in the doc row plus a sentence in Error shapes.

### F45 — `model code set default` downloads 194 MB **before** it discovers it cannot reach its settings server [MEASURED]
**Severity:** LOW
**Evidence:** `ai-raccoon --data-root …/data --quiet model code set default` (no `--port`; the only listener on 7721 was the stale scratch server described above, whose token does not match this root — the same shape as a machine whose primary bank owns 7721) → `downloaded faxenoff/code-daemon-embed-v1@main … (4 file(s))` then `the settings server at http://127.0.0.1:7721/mcp refused this credential`, `EXIT=17`; the same command with `--port 7841` → `EXIT=0`, engine activated. `du -sh data/models` = 194M. Order in code: `src/AiRaccoon/Setup/Cli/Commands/SettingsCommands.cs:~238` (`DownloadDefaultCodeModelAsync` first) then `ActivateCodeDirectoryAsync` (settings write through the HTTP server).
`model code set default` is the one command the `initialize` instructions tell every agent to have the user run ("it downloads and activates"). On a machine whose default port 7721 belongs to the user's primary bank — the normal multi-bank case — the user waits for a large download and then gets a credentials error; a re-run skips the download and fails identically until `--port` is added. Nothing is permanently lost (the model is on disk and `model code set local <dir>` activates it), hence LOW. Fix: resolve/print the settings-server target before the download.

### F46 — `docs/reference/README.md`'s contents list omits `search-parameters.md` [MEASURED]
**Severity:** LOW
**Evidence:** `ls docs/reference/` includes `search-parameters.md`; `docs/reference/README.md:8-18` lists only `agent-memory-server.md`, `embedding-benchmark.md`, `logging-event-ids.md`, `whats-new-history.md`. The omitted file is the only place the per-call search knobs `sourceLambda`, `consolidationThreshold`, `docScoreFormula` and `candidateWindow` are defined, and `agent-memory-server.md`'s tool table does not list them (`grep -rn "search-parameters" docs/` shows no link from the server reference either).
So four live `memory_search` parameters have a documented contract that is unreachable from both the reference index and the tool table. Fix: one line in the reference README (and ideally a cross-link from the tool row).

### F47 — the embedding how-to calls the bundled local engine "the Default"; a fresh bank has **no** memory engine [MEASURED]
**Severity:** LOW
**Evidence:** `docs/how-to/configure-embedding-engines.md:13` (`Local ONNX Engine (Default)`), `:31` (`| **Local (Default)** | all-MiniLM-L6-v2 (int8) |`), `:39` (`Recipe 1: Use local bundled ONNX model (Default)`); live on a never-touched root: `ai-raccoon --data-root …/fresh --port 7871 --quiet settings model show` → `provider: (none — FTS5-only search)`. The product itself calls no-engine "the default" (`CliCommandTree.cs:228`, `SettingsCommands.cs:283`), and the first-run tutorial's verification step (`docs/tutorials/get-started-with-ai-raccoon.md:105-106`) writes and searches without ever configuring an engine.
A reader is told semantic search works out of the box; it does not, and the tutorial's keyword-overlapping verification query passes anyway. Fix: label recipe 1 "the recommended engine", and add the one-line `model embedding set local` step to the first-run flow (pairs with F6).

### F48 — the stdio proxy exits 0 with empty stdout when the client closes stdin right after its batch [MEASURED]
**Severity:** LOW
**Evidence:** `printf …initialize…\n…tools/list…\n` piped into `ai-raccoon --data-root …/data --port 7841` → `EXIT 0`, stdout `''`; the same messages with stdin held open (Python `Popen` + `readline()` before EOF) return the initialize result and `tools/list` = 29 tools. Code: `src/AiRaccoon/Hosting/Proxy/ProxyRunner.cs:40-41` (`await server.RunAsync(ctx)`), EOF ends the stdio transport while the HTTP forward is still in flight.
`echo … | ai-raccoon` is the natural first probe for an integrator; it reports success with silence. Interactive MCP clients keep the pipe open, so real clients are unaffected — hence LOW. Fix: drain in-flight responses before honouring EOF, or emit one stderr line on early EOF.

### F49 — project scope splits the bank and the log into `<data-root>/.ai-raccoon/` but leaves the token at `<data-root>/`, unignored and outside the "state" directory [MEASURED]
**Severity:** LOW
**Evidence:** fresh root, `ai-raccoon --data-root …/scope-test --install-scope project --port 7864 --quiet settings retrieval list` → bank `scope-test/.ai-raccoon/memory.db`, log `scope-test/.ai-raccoon/quiet.log`, token **`scope-test/mcp-token`** (0600). Code: `SqliteConnectionFactory.cs:167-176` (`<dataRoot>/.ai-raccoon` for project scope) vs `McpTokenFile.cs:41-44` (`Path.Combine(dataRoot, "mcp-token")`). The repo's `.gitignore` has no `mcp-token` entry, and the README recommends exactly this layout ("Local banks in `~/.ai-raccoon` or `<project>/.ai-raccoon`").
A user who points `--data-root` at a repository gets an unignored secret file in the working tree (one `git add -A` from being committed), and a backup of `<project>/.ai-raccoon/` silently omits the credential — the reference warns about the `.mcp.json` version of this accident but not the token. Fix: mint the token in the bank directory, or document and ignore the path.

#### Lane G — Product design: the agent-facing experience

### F50 — The tutorial's final verification call fails on the current version [MEASURED]
**Severity:** MEDIUM
**Evidence:** the tutorial's exact Step 4 call, `memory_search {"projectId":"get-started","query":"install verification"}`, → `invalid-argument: The arguments dictionary is missing a value for the required parameter 'sessionId'. (Parameter 'arguments')`; docs/tutorials/get-started-with-ai-raccoon.md:106; `sessionId` became required in 1.38.0 (README.md:37, commit 5f76197f 2026-09-03) and the tutorial was edited after that (aa4eb110, 2026-09-09) without it.
Running product. The documented install-verification step cannot succeed as written, and the
tutorial's own failure advice ("If it comes back empty, re-check Step 3's `.mcp.json`") sends the
reader to debug the connection instead of adding a parameter. Cost: the first-run success criterion
is unreachable as documented. Prior-ruling check: none found beyond the README breaking-change line;
the tutorial records no update task. Smallest fix: add `sessionId` to the Step 4 call.

### F51 — With another bank's server on 7721, the documented activation command exits 17 with no remedy named [MEASURED]
**Severity:** MEDIUM
**Evidence:** `ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/g-product-design/data model embedding set local` → `ai-raccoon: the settings server at http://127.0.0.1:7721/mcp refused this credential — it may serve another data root`, exit 17; the same command with `--port 7811` → `embedding engine set to local (bundled ONNX model); re-embedding in the background`, exit 0. `lsof -iTCP:7721` showed the user's AiRaccoon process holding the port; src/AiRaccoon/Settings/ServerSettingsStore.cs:231, src/AiRaccoon/Settings/CliSettingsBackend.cs:44-75.
Running product. Settings verbs always acquire the default port unless `--port` precedes the verb, so
any second bank (another project, a reviewer's scratch root) hits a credential refusal whose text
names what happened but not what to do. Cost: the first-run activation step of F6 is blocked in a
common multi-bank setup and the fix is in neither the message nor the tutorial. Prior-ruling check:
the manual-checklist skill's "Never bind the default port" rule (why maintainers always pass `--port`)
is the closest thing found; no doc pairs `--data-root` with `--port` for settings verbs. Smallest
fix: append "pass `--port <n>` to reach this data root's server" to the refusal.

### F52 — A query with no lexical or semantic overlap returns every entry as a confident top hit [MEASURED]
**Severity:** MEDIUM
**Evidence:** live `memory_search kind=memory` for "quantum chromodynamics lattice gauge" against 4 unrelated entries → 4/4 returned, top `ranking: 1`, `warning: null`, `fusionStats{topMargin:0.0161, maxPossible:0.0164, participatingLegs:["vector"]}`, top `fusionStrength: 1.0`, cosines 0.028/0.022/−0.016/−0.034; a genuinely matching query on the same bank measured `topMargin: 0.508`.
Running product. There is no absolute relevance floor (the 0.6 `minRelativeScore` is relative to the
top hit), so a nonsense query normalizes to `ranking: 1.0`; the Stage-1 signals that decode the state
are optional fields, and `fusionStrength: 1.0` (single vector leg at rank 1) points the opposite way
from the flat margin. Cost: an agent that does not inspect `fusionStats` reports relevant memories
for a query that matched nothing. Prior-ruling check: signals-only without verdicts is deliberate
(docs/plans/2026-09-03-search-signal-preservation-plan.md §3 and G7); no ruling covers whether the
flat-margin single-leg state should carry a `warning`. Smallest fix: warn when
`participatingLegs.length == 1` and `topMargin` is below a small threshold.

### F53 — The refusals that gate the degradation lifecycle name no remedy, while sibling refusals do [MEASURED]
**Severity:** MEDIUM
**Evidence:** live on a default (`rw`) bank — `memory_set_ttl`/`memory_sweep{dryRun:false}`/`memory_delete` → `access-denied: <tool> requires mode full (current rw)`; `memory_ingest_file` → `path-outside-scope: Path '…' is outside the ingest scope.`; `memory_watch_add` → `watching-disabled: Watching is disabled for project 'lane-g'.` Contrast the sibling refusals: `invalid-params: projectId is required (… pass projectId explicitly, or register this directory with memory_watch_add / settings ingest scope add)` and `… unless allProjects=true; pass projectId … or allProjects=true …`. src/AiRaccoon.Core/Access/AccessModePolicy.cs:26 (default `Rw`); src/AiRaccoon/Access/MemoryAccessGuard.cs (message has tool + modes only).
Running product. **Verifier-corrected (G4):** on a default bank the *destructive* lifecycle arms refuse — `memory_set_ttl`, `memory_sweep{dryRun:false}` and `memory_delete` — while the sweep's default `dryRun=true` listing is **permitted** on `rw` (`SweepTools.cs:35` requires Destructive only for the delete; measured `candidates:[], deleted:[]`, no error), so "the whole surface refuses" overstated. The refusal message names neither the setting nor the CLI verb that changes it; the same is true of the scope and watch
refusals. Cost: an agent cannot tell its human what to run, on exactly the tier where the lifecycle
is unreachable by default. Prior-ruling check: the three-tier access model is deliberate (FR-NM-2;
docs/reference/agent-memory-server.md:430-442) but no ruling was found on the refusal text. Smallest
fix: append the `settings access` remedy to the access-denied message (and the CLI pointer to the
other two).

### F54 — The explanation of deferred embeddings misstates fresh-bank search and names retired commands [MEASURED]
**Severity:** LOW
**Evidence:** docs/explanation/agent-memory-architecture.md:101-102 — "search only returns embedded content, so a fresh bank needs an engine configured via the CLI (`ai-raccoon model set local` or `model set openai …`) plus `memory_embed_pending` to become searchable". Live: a bank with `pending: 1` and no engine returned the pending entry through FTS; `ai-raccoon model set local` → `Unrecognized command or argument 'set'` (exit 15), and the 1.35.0 rename to `model embedding set` is recorded at README.md:38.
Running product. The one page that explains deferred embeddings tells a reader the bank is not
searchable before the engine and pending drain — false for keyword search — and points at commands
that no longer parse. Cost: a reader debugging "why didn't my write come back" is sent the wrong way
and then to a CLI error. Prior-ruling check: the current verbs are documented at
docs/reference/agent-memory-server.md:178,730; this page was not updated by the rename. Smallest fix:
correct the sentence and the two command names.

### F55 — `memory_performance` hand-lists 6 search phases while the live report returns 10 plus 3 fusion series [MEASURED]
**Severity:** LOW
**Evidence:** live call → 42 series, including `search.open`, `search.embed`, `search.adjustment`, `search.total` and `search.fusion.top_strength/legs_fired/top_margin`; src/AiRaccoon/Tools/PerformanceTools.cs:17 names only `search.fts, search.vector, search.fusion, search.affinity, search.snippets, search.bump`; the derived lists are `SearchResults.SeriesNames` (src/AiRaccoon.Core/Memory/SearchResults.cs) and `FusionStats.MetricNames` (src/AiRaccoon.Core/Memory/Fusion/FusionStats.cs).
Running product and read the source. The description also promises "a tool or phase never recorded
still appears, at count 0", which makes its six-name list read as exhaustive. Cost: an agent mapping
the response finds seven unexplained series, and this is the project's own derive-or-delete-the-list
invariant applied to a tool description. Prior-ruling check: none; no test pins the description
string. Smallest fix: drop the enumeration or quote the derived constants.

### F56 — `memory_stats` says it reports "the bank's committed contexts" but reports only the project's [MEASURED]
**Severity:** NIT
**Evidence:** src/AiRaccoon/Tools/MemoryTools.cs:361; live `memory_stats {"projectId":"lane-g"}` → `contexts:["project:lane-g"]`, and for a project with no rows → `contexts:[]` although the bank holds another project's entries; docs/explanation/agent-memory-architecture.md:94 states stats count only the caller's project context.
Running product. The wording over-claims scope in the direction of cross-project visibility; there
is no functional leak. Smallest fix: say "this project's committed contexts".

### F57 — `memory_sweep` documents a `dry_run` parameter that does not exist, and the spelling is silently ignored [MEASURED]
**Severity:** NIT
**Evidence:** src/AiRaccoon/Tools/SweepTools.cs:25 "(dry_run, default)"; live schema property is `dryRun`; live call `{"projectId":"lane-g","dry_run":false}` returned the dry-run shape (`candidates:[], deleted:[]`) with no error.
Running product. A caller copying the description's spelling gets a listing while asking for a
deletion; the fail direction is safe but silent. Smallest fix: rename to `dryRun` in the description.

### F58 — README's test count is ~1,500 below this base's discovery count [READ]
**Severity:** NIT
**Evidence:** README.md:160 `# xunit.v3 test suite (~3700 tests)`; docs/work/2026-09-22-project-scope-review/GROUND-TRUTH.md Phase-0 measurement: `dotnet test --list-tests` → `Discovered 5210 tests.`
Read, not re-measured: the full suite was in flight and the review's rules forbid re-running it, so
the 5210 figure is this review's own Phase-0 number. Cost: a credibility nit on the README's
architecture map; nothing else depends on it.

#### Lane H — Test-suite QA

### F59 — The trait-coverage gates check the trait's NAME but not its VALUE, so a class tagged `Speed=Medium` is blessed green while no CI lane selects it [MEASURED]
**Severity:** MEDIUM
**Evidence:** planted `tests/AiRaccoon.Tests/Unit/ZZProbeBadSpeedValueTests.cs` (`[Trait(Category,Unit)] [Trait(Speed,"Medium")]`) alongside a no-trait probe, then M3: `dotnet test … --filter "FullyQualifiedName~SpeedGateCoverageTests"` → `total: 3, failed: 1`, offender list `["AiRaccoon.Tests.Unit.ZZProbeNoTraitTests"]` **only**. `--list-tests --filter "Speed=Medium"` → `Discovered 1` (`ZZProbeBadSpeedValueTests.Probe_SpeedTraitValueIsNotALane`); the same probe under `Speed=Fast&Performance!=Benchmark`, `Speed=Slow&Performance!=Benchmark`, `Speed=Nightly&Performance!=Benchmark`, `Category=bdd` → **0 matches each**. Both probes deleted; `SpeedGateCoverageTests` back to 3/3 green.
`SpeedGateCoverageTests.cs:22-29` and `CategoryGateCoverageTests.cs:22-29` filter on `trait.Name != TestCategories.Speed|Category`; nothing compares the value to the closed set in `TestCategories.cs:24-30`. The guard's stated purpose (its own docstring `:12-15`) is that "a test class with no Speed trait … compiles, runs locally, and never gates a merge" — a typo'd or unknown *value* (`"Medium"`, `"fast "`, a bare literal) reproduces exactly that outcome while both guards and `TheGuardSeesTheTestClasses` (`:35-39`) stay green. The Category half is the same expression (read, not separately planted). No live offender exists today; the hole is latent.

### F60 — The CI lane partition is not exact: two test cases carry both `Speed=Fast` and `Speed=Slow` and run in two lanes [MEASURED]
**Severity:** LOW
**Evidence:** my own per-lane discovery in this worktree: Fast **3788** + bdd **184** + Slow **1073** + Nightly **164** + Benchmark **3** = **5212** against the assembly total, and `comm -12` of the sorted Fast and Slow name lists returns exactly `ThreadResolutionTests.Generator_ReportsTheIntraOpThreadsItWasBuiltWith(threads: 0)` and `(threads: 4)`.
`tests/AiRaccoon.Tests/Unit/Embedding/ThreadResolutionTests.cs:8-10` carries class-level `Category=Unit` + `Speed=Fast`; `:62-68` adds method-level `Category=Integration` + `Speed=Slow` on that one theory ("Constructs a real ONNX session, so this one leg is Integration/Slow").
Consequence: `build-fast` and `build-slow` both execute it (duplicated CI work for a real-ONNX test inside the fast gate), and `TestCategories.cs:11-13`'s definition of Unit ("pure logic / fakes") is false for it. The 2026-08-14 test-QA lane measured this partition as exact with 0 pairwise intersections; this is new drift, not a coverage hole.

### F61 — Documented gate commands are inert: `dotnet test … --no-build --nologo` discovers 0 tests and exits 5 [MEASURED]
**Severity:** LOW
**Evidence:** `dotnet test tests/AiRaccoon.Tests --no-build --list-tests --nologo` → `Zero tests ran` / `Discovered 0 tests` / exit 5, twice (with and without a `--filter`); the byte-identical command **without** `--nologo` → `Discovered 5212 tests` (5210 at base; two temporary probe classes were present during the measurement).
`docs/plans/2026-08-08-embedding-perf.md:312-314,495-497` and `docs/plans/2026-08-16-bank-open-cost-implementation.md:452` hand agents gates as `dotnet test --filter … --no-build --nologo -v m`.
On .NET 10's Microsoft.Testing.Platform path the unrecognised switch produces a run over zero subjects reported with a non-success exit code and the words "Zero tests ran" — a reader scanning for red finds none, and the plan's acceptance gate never executed. CI itself is unaffected (`-v m`, no `--nologo`); the hazard is agent- and plan-directed commands. Fix: drop `--nologo` from test commands (it is a build switch), or wrap documented gates in a non-zero-subject assertion (T1-PRF-03).

### F62 — `FakeMemoryStore.DeleteInScopeAsync` drops the `scope` argument, so the unit lane is blind to the sweep's scoped-delete invariant; only the real-store lane holds it [MEASURED]
**Severity:** LOW
**Evidence:** M4 — with `SweepService` deleting scope `"workspace"` instead of `"project"`: `--filter "FullyQualifiedName~SweepServiceTests"` → `total: 7, failed: 0, succeeded: 7` (all green), while `--filter "FullyQualifiedName~SweepScopeSiblingTests"` → `total: 3, failed: 3` including the negative control `Sweep_ProjectRowWithNoSibling_IsStillDeleted` ("the scoped delete must not become inert for the ordinary case"). Reverted; both green.
`tests/AiRaccoon.Tests/TestHelpers/FakeMemoryStore.cs:34-36` forwards to `DeleteAsync(projectId, hash)` and discards `scope`; `tests/AiRaccoon.Tests/Unit/TestHelpers/FakeMemoryStoreTests.cs:33-39` pins that forwarding as a contract. The production invariant (`SweepService.cs:52-57` plus `SqliteMemoryStore.DeleteInScopeAsync`'s H2 doc, `:285-299`) is therefore unobservable through the fake: any future unit test asserting "the sweep deletes only this scope" passes vacuously, and the *real* guarantee lives entirely in `Integration/Storage/SweepScopeSiblingTests.cs`. Not a current hole — the integration lane covers it — but the fake is an unverified claim about production on exactly the parameter the invariant depends on (T0-05/T1-DBL-02).

#### Lane I — Operations, CI/CD, packaging & Python tooling

### F63 — 21 of 101 tagged GitHub releases have no nuget.org package; a cancelled pack is never retried and nothing reconciles the two halves of a release [MEASURED]
**Severity:** MEDIUM
**Evidence:** `curl -o /dev/null -w '%{http_code}' https://api.nuget.org/v3-flatcontainer/ai-raccoon/<v>/ai-raccoon.<v>.nupkg` → `1.42.4 = 404`, `1.41.1 = 404` (controls `1.42.3 = 200`, `1.42.5 = 200`); tag-set minus nuget-versions (script over `git tag -l 'v*'` and the flat-container index) = `v1.42.4, v1.41.1, v1.38.0, v1.34.4`. `gh release view v1.42.4` → published 2026-09-14T16:20:17Z. `gh api repos/Arasz/ai-raccoon/actions/runs/34868224773/jobs` → `pack (linux-x64) cancelled (944s)`, `publish skipped`. `git diff --stat v1.42.3..v1.42.4 -- src` → `1 file changed, 29 insertions(+)`; `v1.37.0..v1.38.0 -- src` → `65 files changed, 2683 insertions(+)`.
`release.yml` tags and creates a GitHub release the instant `VERSION` changes on `main`; `publish.yml` is a separate manual dispatch that packs **the default-branch tip** and pushes with `--skip-duplicate`. So the release page and the installable artifact are produced by two systems that never check each other: a dispatch whose `pack` job is cancelled leaves a public release announcing a version `dotnet tool install --version 1.42.4` cannot resolve, and the cancelled 1.42.4 pack was never retried. **Verifier-corrected (G6):** the true count is **21 of 101** releases with no `ai-raccoon` package on nuget.org (15 with no package under either id; six 1.0.x live under the former `arasz.ai-raccoon`) — the lane's "four" came from holding only the last 20 tags in its `tags.txt`. Its "a later dispatch under the same VERSION would have silently no-op'd" is **falsified**: the observed next dispatch packed main's then-current 1.42.5 and succeeded, so the 1.42.4 pack was abandoned rather than no-op'd. Four of the missing versions had a cancelled dispatch (1.42.4, 1.34.4, 1.33.6, 1.31.2), never retried; the rest have no publish run at all. Smallest fix: a `publish` step asserting every RID payload and the shell of that VERSION resolve on nuget.org (or gate `release.yml` on the package existing).

### F64 — `Speed=Nightly` has only an opt-in runner, and its last execution (2026-09-09) was red and merged; no scheduled CI of any kind exists [MEASURED]
**Severity:** MEDIUM
**Evidence:** over the last 500 `build.yml` runs, the `build-nightly-gates` job is non-skipped **14×** (5 success / 2 failure / 7 cancelled — **verifier-corrected, G6**: the lane counted 12); last non-skipped = 2026-09-09T12:36Z, `conclusion=failure`, and it is that run's **only** red job (`build-fast/bdd/slow` all success). Failing test: `AiRaccoon.Tests.Integration.Setup.CliBankWriteTests.ApplyCommand_OnlyCommitsAnOutboxRequest_NeverTheDomainTableDirectly(label: "repair project-ids --apply" …)` — `Shouldly.ShouldAssertException: ApplyCommitAssertions.IsHonestApplyCommit(`. The test is `[Trait(Speed, Nightly)]` (`CliBankWriteTests.cs:44-46`) and was then fixed by PR #629 `fix: seed alias map for project-ids apply honesty gate` (759fa2da, ancestor of HEAD, merged 15:09Z) — **~2h34m** after the red run and ~23 min after PR #627, whose head the run tested (**verifier-corrected, G6**: "20 minutes" was wrong). No Nightly job has run since, so the fix's verdict exists nowhere. `grep -rn "schedule\|cron" .github/workflows/` → only comments; `nightly.yml` was deleted in `7e141c59`.
`tests/AiRaccoon.Tests/TestCategories.cs:8-12` documents this exposure honestly, and the static `SpeedGateCoverageTests`/`CategoryGateCoverageTests` still stop an *untagged* class escaping every lane — so the loss is not silent-typo coverage, it is that nothing unattended runs at all: no nightly, and `Performance=Benchmark` (3 tests) is excluded from every lane by design with "no scheduled backstop" (`build.yml:138`). The previous unfiltered backstop's replacement, `nightly-triage.py`, has no runner (F8).

### F65 — The diff classifier matches neither `global.json` nor `benchmarks/**`, so such a PR is classified "no code-relevant paths" and every dotnet lane skips [MEASURED]
**Severity:** MEDIUM
**Evidence:** running the workflow's own regex (`build.yml:75`) over single-path diffs: `global.json → code=false`, `benchmarks/AiRaccoon.Benchmarks/Program.cs → code=false` (positive controls `src/AiRaccoon/Program.cs`, `tests/.../SpeedGateCoverageTests.cs`, `VERSION`, `nuget.config` → `code=true`). Neither is hypothetical: `global.json`'s last two edits (`dfd9c0a5` #589, `7bf58f8c` #342 `fix: unbreak CI — revert the test-package bump and pin the SDK`) both also touched matched paths and were covered only incidentally, and `global.json` is the SDK pin the workflow's own `setup-dotnet` comment calls a mutable dependency it refuses to float. `benchmarks/AiRaccoon.Benchmarks.csproj` is in `AiRaccoon.slnx`, `TreatWarningsAsErrors`, and is matched by no lane, so a benchmark-only PR can merge non-compiling while `changes` reports `code=false` (only `scripts-harness` runs). Smallest fix: add `(^|/)global\.json$|^benchmarks/` to `CODE_REGEX`.
*(Withdrawn lead: the 2026-08-26 OPS-16 finding that `VERSION` was missing from `CODE_REGEX` has landed — `^VERSION$` is present at `build.yml:75`.)*

### F66 — CI's python harness runs 14 of 51 test files; 35 of the files it never runs collect and pass 401 tests in ~12s under exactly the CI dependency set — including the tests for the PII guard, the pack gate and the tool-shell patcher [MEASURED]
**Severity:** MEDIUM
**Evidence:** `build.yml:275` names 14 files; `scripts/tests/` holds 51 (`python3 -m pytest scripts/tests -q` → **1 failed, 637 passed, 26 skipped in 53.03s**; the single failure is `test_threshold_eval_integration.py::test_merge_hygiene_pr_branch_has_empty_src_diff` failing on `assert ([])` because a detached-HEAD worktree at main's tip yields an empty merge-base diff — a lane artifact, not a defect). A venv with **only** `pip install pytest httpx` (the exact CI install line, `build.yml:271`) ran the 37 excluded files: **401 passed, 20 skipped in ~12s** (**verifier-corrected, G6**: 20, not 19; one further test, `test_llamaindex_harness_cli.py::test_ingest_runs_as_module`, fails under that set — also chromadb), with exactly 2 collection errors, both `ModuleNotFoundError: No module named 'chromadb'` (`test_llamaindex_harness_ingest.py`, `..._retrieve.py`) — the two the comment's rationale covers. Among the 35 collected files under that set (34 fully green — **verifier-corrected, G6**): `test_verify_history_scrubbed.py` (the S3 guard, F4), `test_package_verify.py` (the pack gate whose `CODE_REGEX` entry exists to force CI attention), `test_tool_shell.py` (the patcher whose 1.0.2 bug shipped an uninstallable shell), `test_download.py`, `test_coredump.py`, `test_nightly_triage.py`. The comment names only the 4 heavy files as "a documented local gate"; the remaining **33** are named by no gate at all (51 − 14 CI − 4 documented — **verifier-corrected, G6**). Smallest fix: extend the harness list (it costs ~12s) or record why each stays out.

### F67 — `build-fast`'s crash-dump configuration cannot produce a dump — the directory it names is never created — and no job uploads it [MEASURED]
**Severity:** MEDIUM
**Evidence:** `build-fast` sets `DOTNET_DbgEnableMiniDump/…Type/…Name: ${{ github.workspace }}/dumps/coredump.%p.dmp` (`build.yml:99-103`) with the comment "build-slow captures dumps; fast must too … a host crash here previously left no evidence to triage", but it has no `mkdir -p dumps` step and no upload/triage step (those exist only in `build-slow`, `:218-243`); `build-bdd` has no dump config at all. Measured with a real crashing console app: `DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=2 DOTNET_DbgMiniDumpName=…/nodir/coredump.%p.dmp dotnet app.dll` → `[createdump] Could not create output file '…/nodir/coredump.20928.dmp': No such file or directory (2)`, exit 134, no file written; the control against an existing directory → `Dump successfully written in 644ms`, 314 MB file. So on the next fast-lane SIGSEGV the runtime aborts with no evidence, exactly as before the fix. Smallest fix: `mkdir -p dumps` before the test step plus `actions/upload-artifact` on `failure()`, mirroring `build-slow`.

### F68 — The post-publish fresh-install gate still defaults to version 1.6.0 — a recurrence of the same drift a prior review fixed [READ]
**Severity:** LOW
**Evidence:** `scripts/manual-fresh-install-test.py:53` `VERSION = os.environ.get("AI_RACCOON_VERSION", "1.6.0")` (docstring line 8 offers the same value to the operator), while `VERSION` at HEAD is `1.42.5`; `grep -rn "AI_RACCOON_VERSION"` finds no caller that sets it (the 1.42.3 checklist installs the version explicitly by hand instead). `curl -o /dev/null -w '%{http_code}' …/ai-raccoon/1.6.0/ai-raccoon.1.6.0.nupkg` → `200`, so the stale default resolves and the script would go green while proving nothing about the release under test. The identical defect was raised and "fixed" on 2026-08-06 (`docs/reviews/2026-08-06-integration-review-1-0-8.md:93`: "defaulted to 1.0.6 while the release was at 1.0.7/1.0.8"); the fix was a hand-bump, so it drifted back. Smallest fix: derive the default from the repo `VERSION` file (or fail when the env var is unset).

### F69 — The only unpinned dependency in a pin-everything pipeline is the harness's own test toolchain [READ]
**Severity:** LOW
**Evidence:** `build.yml:271` `run: python3 -m pip install pytest httpx` — no version, no hash, no lockfile — while the repo pins actions to commit SHAs (7/7, verified below), the SDK via `global.json`, and the python deps in `uv.lock` (`pytest 9.1.1`, `uv.lock:405-406`) and `pyproject.toml` (`pytest`, `httpx` plus the pinned heavy set). The venv used for F66 resolved pytest 9.1.1 by luck today; a future pytest major reaches CI with no repo change, breaking the harness on a PR that touched nothing python. Smallest fix: `pip install -r` a pinned requirements file, or `uv sync --frozen` from the committed lock.

#### Lane J — Security & privacy

### F70 — The proxy hands the agent's token and tool payload to any local process that holds the configured port, and the agent receives that process's forged replies [MEASURED]
**Severity:** HIGH
**Evidence:** `ServerProbe.cs:69` — `ProbeVerdict.Answered` means *the response body contains the string "jsonrpc"*, nothing else; `BackendLauncher.cs:53-56` — an answering port short-circuits the launcher with no spawn; `NodeRunner.cs:71-83` — `serve` without `--restart` attaches to it and prints its URL as the MCP entry; `BackendSessions.cs:40-43,107-121` — the proxy reads `<data-root>/mcp-token` and sends it as `X-AiRaccoon-Token` on every backend request. Measured with a scratch python squatter on 127.0.0.1:7806 and `ai-raccoon --data-root …/data5 --port 7806`; the squatter's log recorded, in order:
`POST /mcp | token: 7DDJwG9WNxsjzvTjJHSxCmBmJR3mybXxpixdwq-gwPc | Mcp-Method: server/discover`,
`… | Mcp-Method: initialize`, and
`… | Mcp-Method: tools/call` with body
`{"method":"tools/call","params":{"name":"memory_write","arguments":{"projectId":"secret-project","content":"CONFIDENTIAL user memory content"}…`.
The proxy's stdout — what the agent sees — was `{"result":{"content":[{"type":"text","text":"squatter-owned response"}]},"id":2}`. `serve` against the same squatter exited 0 with "attached to the server already listening on …".
So a local process that binds the port first (on a shared machine: a *different user*, since loopback ports are machine-global) gets the loopback token, every memory write/search payload, and control of every tool result the agent sees — response forgery, i.e. a prompt-injection channel into the agent's context. The `--restart` path is the only one that identifies the listener (`ServerRestart.cs:69`, `/observability` `name == "ai-raccoon"`, a string a squatter can return) and it too sends the token to it (`:134`). ADR-0043:49 states the inference that fails: "a JSON-RPC reply on `/mcp` ⇒ an ai-raccoon server holds the port". Smallest fix: require the listener to identify as ai-raccoon (`server/discover` `_meta.serverInfo.name`) before the proxy hands over the token.

### F71 — On an unencrypted bank (the default) sync has no authenticity control, and one `memory_sync` call merges plus tombstones rows for *every* project [READ]
**Severity:** HIGH
**Evidence:** `SyncService.cs:227-229` — the HMAC wrap is skipped when the bank has no password; `:843-848` — the pull side then returns the remote bytes unverified (`Log.SkippingAuthenticityCheckForUnencryptedBank`); two tests pin exactly that behaviour: `tests/AiRaccoon.Tests/Integration/Sync/SyncServiceRemoteBlobTests.cs:363-380` ("unencrypted bank … must push raw, unwrapped bytes") and `:382-416` ("must skip the check"). The merge is bank-wide: `:440-468` inserts `FROM remote.entries` for all projects; `:520-522` applies remote tombstones as `DELETE FROM entries` for all projects; `projectId` only picks the object key (`:128-133`). `SyncTools.cs:29` gates the call at `AccessRequirement.Write`.
Failure scenario: with sync configured on an unencrypted bank (encryption is opt-in per SECURITY.md) an attacker with bucket write access substitutes a snapshot; any `memory_sync` from any project then (a) injects entries into any project *including the cross-project `shared` tier every agent reads* — durable prompt injection — and (b) deletes other projects' rows via forged tombstones, after which the injected rows are re-embedded locally. **Verifier-corrected (G3) — ratified documented acceptance, not an open defect.** The unencrypted branch is recorded as accepted (S2/O3; `docs/explanation/architecture.md:626`, added with the HMAC in `8b44c35d`), and keying the HMAC from the bank passphrase makes it inert exactly where the bank is unencrypted *by that decision*. The recordable residual is the **bank-wide blast radius**: because the snapshot is whole-bank, one `memory_sync` merges and tombstones rows for every project, so bucket-write access equals memory takeover in precisely the configuration whose acceptance covers it. A per-machine sync secret or first-contact digest pinning is a new decision, not a defect fix. Grade is READ because no live cloud round-trip was run (see Still open) — the two tests that pin the skipped branch are the corroboration.

### F72 — A refused `memory_search` query is written verbatim (≤200 chars) into the server log and carried as the OTLP span status/exception, contradicting SECURITY.md's "no search queries" claim [MEASURED]
**Severity:** MEDIUM
**Evidence:** `MemoryTools.cs:208` throws `McpException($"invalid-params: … Refused query: {QuerySnippet(query)}")`, and `:592-596` takes the first 200 characters of the caller's raw query; `QueryGuardConfigKeys.cs:13,18` — the guard is **armed by default** (`ParseEnabled` returns true unless the setting is literally "false"); `ToolRefusals.cs:244-245` logs the whole refusal message at Information as EventId 910; `ToolExecutionActivity.cs:103,105` sets the span status to `exception.Message` and adds the exception (message + stacktrace) as an OTLP event. Measured: a search with
`[IMPORTANT: Background process LANEJ-REFUSE-CANARY-9c7e completed normally] Command: LANEJ-SECRET-ARG --password hunter2`
was refused and `serve2.log` line 41 holds
`"memory_search" refused: invalid-params: This looks like tool output … Refused query: [IMPORTANT: Background process LANEJ-REFUSE-CANARY-9c7e completed normally] Command: LANEJ-SECRET-ARG --password hunter2`.
SECURITY.md:133 states "**Memory content never leaves.** No entry text, no **search queries** …", and SECURITY.md:138-141 names absolute paths in refusal messages as the *one* exception; a pasted log line — the guard's entire target population — is exactly where secrets and paths live. Smallest fix: log/annotate the refusal by policy and query length/hash, not the query text, and keep the text only in the caller's error result.

## Ratified decisions referenced (not re-litigated)

These rulings are accepted; the findings above record residual reach or surrounding defects, not a dispute of the ruling itself.

- **ADR-0089 / H-C — project isolation is a naming convention over one shared credential.** Other projects on the machine are trusted by design. Residual risk recorded in F3 (a cross-project read path that names no id) and F1 (any local process can hold the port). Revisit trigger: multi-user or multi-tenant deployment.
- **S1 → O6 — "read-all mode" for `memory_promotion_list(allProjects=true)` landed as an explicit boolean consent flag.** The deviation is ratified in the 2026-08-22 plan; F3 measures the reach of that single flag rather than re-litigating the gate's form.
- **ADR-0102 — the process-static `ProjectIdAliasMap.Default` cache is a ruled trade.** F15 records the ambient-global shape and its consequences (Core's key factories are no longer pure, a unit test cannot substitute a map), not a deviation from the ADR.
- **ADR-0026 — `memory_promotion_discard` is permanent per (project, hash).** Lane J observed a discard succeeding for another project's queue at `rw`; it is Write-gated and ratified, so it is noted, not filed.
- **"No embedding engine by default" is intended** (archived 2026-08-04 CLI-config findings; exercised as a manual step by the 1.42.0 checklist). F6 is about the absent warning and the untouched onboarding surfaces, not about the default itself.
- **The S6b history rewrite is an owner-only decision gated on #455.** F4 records the guard's reach, its two uncovered path spellings and its missing CI wiring — not a demand to rewrite now.
- **`Speed=Nightly` being opt-in is a recorded trade** (`tests/AiRaccoon.Tests/TestCategories.cs:8-12`). Lane I's finding records that nothing unattended runs at all, not that the label is wrong.
- **Retry attributes on the integration/E2E surface are deliberate.** The consolidated flake finding records the missing reporting and the absent ledger consumer, not a demand to remove retries.
- **Delta BLOCKER B1′ and its F2/F3 leads are closed** (D1/D2 landed): the manifest loader hashes every pinned file and rejects rooted or `..`-bearing paths; the ingest/watch containment checks refuse symlinked escapes. Recorded because the campaign asked whether these were re-verified.
- **The 0821 architecture lane's port-placement finding is fixed** (both ports moved to Core; no new Infrastructure-declared port reaches the tool layer). Its open follow-up — one generic port-placement rule instead of `LayeringRulesTests` Rule 4's single-name pin — remains an owner question, not a defect.


## Still open

**Lane A** — - **`WarnOnNonHttpTransport`'s reachable surface.** I measured it firing via `--transport proxy serve`; since `serve` rejects `--transport` after the verb and stdio/https die at parse, that is the *only* live spelling. Whether any launcher uses it was not determined.
- **`MemorySchema.cs` decomposition** — I read its 40-member inventory (DDL, 14 rungs, one-shot column/trigger repairs, `TestOnly*` `AsyncLocal` hooks) and the +770 delta, but did not evaluate which of its concerns could be split, so F9 names the size, not a split.
- **Two reds quoted from GROUND-TRUTH** (arm64 MiniLm golden, `ParityGateTests` p95) were not re-run here — retrieval and test lanes own them.
- **`StructuralNoiseModel.Trees.g.cs` (1881 lines)** excluded from the growth analysis: generated, and I did not verify the generator is checked in and reproducible, so it may be a source-of-truth question rather than a growth one.
- **Maintenance pass log** ("… job 'vacuum' ran in 7 ms" for all 7 jobs on a fresh bank, including `vec0-reclaim` and `vacuum`) — I did not establish whether `ran` means work happened or only that the guard let the job run; that changes how the log reads, and it belongs to the data/QA lanes.
- **Tool-count arithmetic is not reconciled**: `ToolMethodSizeTests` asserts the scan finds ≥26 tool bodies, `McpToolInventory.Names()` derives from reflection, the `@ignore`d BDD scenario says "17 tools" and is annotated in-file as stale against a "22-tool surface", and I counted 30 `[McpServerTool` occurrences. Three or four numbers, one surface — Lane F's to close.
- **ADR-0102's "watch boundaries refuse retired ids"** is looser than the code: watch boundaries *fold*, the refusal lives in `ToolGate`, and the repair deletes dropped ids' watch rows (`ProjectIdsRepair.cs:458`). I refuted the resurrection hazard I suspected (`WatchDigestExecutor` folding a dropped id) by reading `FoldWatchesAsync`, so this is wording, not behaviour — reported nowhere because I could not show harm.
- **`IMaintenanceJob.HasWorkAsync` DIM default `false`** (M4 lead): all 8 on-demand jobs override it and the 4 cadence jobs correctly rely on the default — no defect found, so no finding; the shape is still the "silent default" class and would break if an on-demand job forgot.
- **Isolation note:** the `dotnet test` filter needed `--no-build`; without it discovery reported "Zero tests ran" in this worktree. I did not chase it (not my lane) but every test measurement here used `--no-build`.

**Grade mix:** MEASURED 8 (F9, F11, F13, F14, F15, F16, F17, F18) · READ 2 (F10, F12) · INFERRED 0 · UNVERIFIED 0.

**Lane B** — - The ORT-1.29.0 re-pin experiment behind F5's cause is Phase-0's measurement, not re-run in this worktree; I verified only the failure, the unchanged model bytes, and the missing provenance field.
- F19's structure-absent case was measured on a bank with **zero** structure vectors; the mixed case (some rows headed, some not) follows from `EntryEmbedder.cs:240-248` but was not measured on a bank with populated `vec_structure`.
- `fusionStats` invariance could not be stress-tested against a candidate-window change: my bank's population (6–66) never exceeded the 100-candidate floor of `max(3·limit, 100)`, so the window was constant in every run.
- The code-search leg has its own k+rank/max-normalize fusion with no Stage-1 evidence (plan §8); not audited here. Neither was `VecDimensionReconciler`'s open-time behaviour (Lane D's surface).
- A non-384 single-file `.onnx` was not available, so the legacy-file path's 384/WordPiece assumption (`EmbeddingService.cs:342-369`, `ResolveDimensions` → `BundledDescriptor.Dimensions`) was only read, not exercised.
- The Python fusion port (`scripts/retrieval_tuning/llamaindex_harness/fusion.py`) documents "the C# source wins on any conflict" and mirrors `Merge`'s re-fuse; I read it against `ReciprocalRankFusion`/`SearchResultMerger` but ran no differential fixture.

Grade mix: 4 findings — 3 MEASURED, 1 READ, 0 INFERRED, 0 UNVERIFIED.

**Lane C** — - **Watch was not run live** (no watcher process registered): rename/delete/hash-skip semantics are read-only evidence (`WatchDigestExecutor.cs:36-96`); deleted-file chunk removal and the ignore-file rescan were not exercised.
- **M4 (`HasWorkAsync` guard) and D3 (reconcile-at-open)** were not re-verified — Lane D owns them; only the watch analogue of "stamp last" was checked.
- **Extraction scoring/capacity** beyond the measured propose/promote: floor 0.4 and per-source cap 3 read (`SharedExtractionService.cs:16-24`); no content in my bank scored ≥0.4, so `includeTtlRows` exclusion (`:77-80`) could not be discriminated live.
- **`ReclaimStaleClaims` has no owner column** (`PromotionQueueSql.cs:134-138`): a promote pass running longer than 5 minutes could have its claim reclaimed by a concurrent pass. No failing scenario was produced, so this stays an open question, not a finding.
- **Harness note:** the session's `grep` tool is intercepted by a memory-first gate that demands `memory_search` (projectId=ai-raccoon), which this lane's contract forbids (`mcp_ai-raccoon_*` points at the live bank). All searching used `rg`/`read`; the gate could not be satisfied from inside the lane.
- **Ambiguity resolved in favour of evidence:** F24 was graded MEDIUM (not HIGH) because the response `context` discloses the redirect; F26 keeps the per-chunk contract's wording in view before calling the asymmetry a defect.

Grade mix: MEASURED 7 (F22, F23, F24, F25, F26, F27, F28) · READ 1 (F3) · INFERRED 0 · UNVERIFIED 0.

**Lane D** — - **F35's live arm** — the watcher file-delete → sync resurrection was not driven end-to-end (needs an `ingest.scope` registration, a watch and an FS event); the sibling F29 measurement is the strongest available proof of the mechanism.
- **F36's experiment** — I could not construct the two-replica clock-skew case (no way to skew a process's clock here); the finding stays READ and low.
- **Encryption wrong-key / Bitwarden paths** — read-only: `SqliteConnectionFactory.DiagnoseAsync`/`LegacyKeyOpensHealthyBankAsync`/`RekeyBankAsync` were inspected (open → diagnose → refuse, never writes) but I did not create an encrypted scratch bank or exercise a wrong key or the Bitwarden provider. Lane J owns the security judgement.
- **Sync upload/merge atomicity** — `MergeRemoteAsync` (SyncService.cs:310-600) runs its whole merge as a sequence of autocommits with no surrounding transaction (aliases → entries → memory_source → tombstones → watermark → reindex → recompute). The protocol is monotone (INSERT OR IGNORE union + tombstone deletes) so a partial merge looks retry-safe, which is why I did not raise it; the exception is the alias-conflict probe, which deliberately runs first. Flagging it as unverified, not as sound.
- **`MemorySchema` v6's `noise_clusters`/`vec_noise`** are still created for legacy banks while fresh banks never get them; SchemaDoctor ignores extra objects, so this is inert — noted, not a finding.
- **`RepairCommands` chunk-index/reingest SQL** and `ChunkBackfill`'s re-chunk predicate were read but not exercised against a scratch DB (Lane C owns the lifecycle; #585's dimension reconciliation looked careful — `NeedsRecreateAsync` parses `float[N]` from `sqlite_master` and treats a missing table as needing recreation).

**Lane E** — - **Hypotheses I checked and refuted (recorded as results, not findings).**
  (a) *`FileSystemWatcher.Dispose` deadlock*: `WatchEventSource.Stop`/`StopAll` hold `_gate` across
  `watcher.Dispose()` (`WatchEventSource.cs:109-131`) while `Translate` takes `_gate` on the watcher's event
  thread (`:163`), which would deadlock if the runtime's `Dispose` waited for an in-flight callback. Measured
  with `docs/work/2026-09-22-project-scope-review/e-dotnet-quality/disposeblock`: `FileSystemWatcher.Dispose()` returned in **14 ms**
  while the `Created` handler was still blocked — Dispose does not join the event thread on this runtime, so
  there is no deadlock. Hypothesis withdrawn.
  (b) *Broken stdout pipe*: `ai-raccoon --help | head -1` → both sides exit 0, no trace.
  (c) *Test-support probes that report pass instead of skip*: a scan of `tests/` found 3 unfiltered
  `catch (Exception)` bodies that return without failing, all evidence collectors or fakes
  (`EncryptionBitwardenFeatureContext.cs:223`, `ModelMigrationCrashRecoveryE2ETests.cs:386`,
  `FakeHfServer.cs:186`) — the documented trap is absent from this suite.
  (d) *Language traps absent*: no `async void`; no `.Result`/`.Wait()`/`GetAwaiter().GetResult()` outside the
  one deliberate engine-construction read; `GC.GetAllocatedBytesForCurrentThread` never used; no lock held
  across an `await`; all four `SemaphoreSlim` gates and all 46 `lock` statements balanced, and every
  `CancellationToken.None` site (14) is deliberate post-cancel cleanup (rollback, lease release,
  busy-timeout restore).
- **`ILogger<T>?`/`ISettingsStore?` optional-dependency trap, present but not (yet) biting.** Three instances
  on DI-registered types: `SqliteConnectionFactory.cs:19` and `ProjectIdsRepairJob.cs:36` (null → `NullLogger`,
  so an initialization failure inside `InitializeAsync` is silent), and `EmbeddingService.cs:30`
  (`ISettingsStore? settingsStore = null`, whose null path silently ignores `embedding.threads` in favour of
  the halved-core default at `:339-341`); `ServerProbe.cs:23` adds a fourth. Production DI supplies all of
  them (`AppRegistrations.cs:203-205/308-311` and `RegisterEmbeddingServices`), so no production behaviour
  changes today — I could not point at a wrong production outcome, which is why this is not filed as a defect
  rather than because the trap is absent. 12 test constructions of `EmbeddingService` omit the store; that is
  the population the trap exists for.
- **F40's hit rate is measured, its production trigger rate is not.** I did not run a live watch server long
  enough to observe an `ObjectDisposedException`; the "~2–5e-5 per racing call" figure is the harness's rate
  under a maximally tight race, so the real-world number is lower.
- **Whether a `WatchHostedService.StopAsync` throw also skips the *other* hosted services' stops** (metrics
  final flush, shutdown WAL checkpoint, migration-lease release) depends on the generic host's per-service
  exception aggregation, which I did not measure. The guaranteed part is stated in F40; the aggregate part is
  deliberately not claimed.
- **F7's fix ownership** overlaps Lane F (exit-code contract) — reported here because it is a missing catch,
  not because the contract is mine to change.

**Lane F** — - **Withdrawn lead**: the 1.42.0 checklist's "refused query over HTTP has no `isError` flag" does **not** reproduce on 1.42.5 — the query-guard refusal, unknown-hash and invalid-argument all came back `isError: true` over raw HTTP. I could not re-run the stdio-proxy variant of that exact observation for every prefix, so the transport-envelope question is answered only for HTTP plus one proxy `tools/call`.
- Two `--install-scope` details I did not resolve: whether `<data-root>/.ai-raccoon/mcp-token` would break `serve --restart`/proxy token handoff if the token were moved (I only proved where each artefact lands), and whether the project scope is ever used with a data root that is not a repo (no evidence either way).
- The busy-port `serve` run also prints a ~30-line fail-level framework stack trace and does a `Bank WAL checkpoint complete` before failing to bind (observed in full, lines 00–39 of stderr); the documented exit 3 + `--port 0` hint do appear, so I left it as an observation rather than a finding — it is Lane E's logging territory and may already be known.
- I did not exercise `memory_sync` against a real remote, `model embedding set openai`, encryption verbs, or the code-engine-unloadable/missing-manifest refusals (they need an external endpoint or a mutated install). The `initialize` instructions/protocol-version contract is not covered by any doc I could find; I measured the strings but did not decide whether that warrants a finding (owner question Q4).
- Inventory/refusal parity was checked against the installed 1.42.5 binary, which GROUND-TRUTH pins as identical to HEAD product code; I did not rebuild the worktree to re-derive it from source at HEAD (the prebuilt test DLL in the worktree was used for the two filtered test runs).
- Grade mix: 11 MEASURED, 1 READ, 0 INFERRED, 0 UNVERIFIED.
**Procedural note (self-reported):** one early command (`ai-raccoon doctor` with no `--data-root`) read the default root (`/Users/arasz/.ai-raccoon`) before I caught it; `doctor` is read-only and opened nothing read-write, and every subsequent command carried an explicit scratch `--data-root`.

**Lane G** — - I did not run the full suite or a build in this worktree (review ground rules); build/test status
  is taken from GROUND-TRUTH.
- I did not activate the **code** engine (`model code set default`, a 187 MB download), so the
  post-activation code-search journey and the two-corpus warning state are read from the plan and
  docs, not seen.
- Busy-bank, migration-in-progress, and access-mode `ro` states were not exercised live; only the
  default `rw` denials were.
- `memory_share_extract` propose returned all-empty for a 1-entry bank and I did not determine
  whether worthiness scoring or rating excluded it — Lane C owns promotion eligibility.
- `chunkIndex: -1, totalChunks: 0` appears for entries written without `sourceFile`; the sentinel's
  documented meaning was not found, so I filed nothing.
- Stage-1 arithmetic verified incidentally and matches the plan: `maxPossible` measured 1/61 for one
  leg and 2/61 for two (k=60, w=1), and `ranking` values are the `(k+1)/(k+rank)` normalization —
  Lane B owns the deeper ranking checks.
- F58 is the only READ finding; the README count may legitimately mean something narrower than
  discovered tests, so I did not raise its severity.

Grade mix: 12 findings — 11 MEASURED, 1 READ, 0 INFERRED, 0 UNVERIFIED.

**Lane H** — - **Seed-embed full-suite slowdown (carried item)**: not reproduced. I did not run the full suite (campaign rule) and did not isolate `WatchIntegrationTests`' seed budget under load; the delta lane's F61 (arrange-phase seed timeout under CPU contention, first red tolerated) stands unverified by me.
- **TRX representation of retried attempts**: I measured the console/MTP summary only. Whether a `*.trx` records per-attempt failures (which `scripts/nightly-triage.py` reads) is unverified; no workflow command I read generates a TRX.
- **`--nologo` mechanism**: I measured the correlation (0 tests vs full discovery, twice) but did not trace whether it is MTP argument parsing or the xunit v3 adapter swallowing it.
- **The 4 other `@ignore`d BDD scenarios** (MRTR, cloud RLS, metrics/tracing, `memory_inspect`) — the delta lane examined them; I only un-ignored the tool-inventory one.
- **The 2 double-counted lane cases**: whether `build-fast` or `build-slow` is the intended owner is an ownership call I cannot make from the tests alone.

**Lane I** — - **Why the four versions are missing from nuget.org** is inferred from run history, not from a written decision: for 1.42.4 the cancelled `pack` job and skipped `publish` are on the record, but I found no owner statement that skipping a tag's publish is acceptable (1.38.0 carried 2,683 lines of product code). If a ruling exists, F63 downgrades to a documentation gap.
- **My worktree's `git status` went dirty and clean again during the run** — `.ai-badger/agents/code-reviewer.md` and `.ai-badger/agents/dotnet-engineer.md` showed as modified at ~15:0x and were clean again by 15:4x; this matches GROUND-TRUTH's note about a temporary persona-pin edit that is reverted after dispatch, so it is campaign machinery, not my doing. Final state is `git status --porcelain` **empty**. I left both files untouched throughout. `test_threshold_eval_integration`'s merge-hygiene failure is likewise an artifact of running in a detached-HEAD lane worktree, not a repo defect.
- **I did not run the fresh-install protocol end to end** (F68): it installs a global-tool copy into a temp dir and downloads a ~70 MB package; I measured the stale default and its resolvability instead. The 1.6.0 → 1.42.5 gap is therefore demonstrated statically, not by a green run against the wrong version.
- **`manual-fresh-install-test.py` is not exercised by any workflow or checklist file I could find**; whether the owner's ritual uses it or the hand-written `dotnet tool install` steps in `docs/work/checklist/*.json` is a process question I cannot settle from the repo.
- **`scripts/triage-coredump.py` is ELF-only**: on a real macOS Mach-O dump it prints `not an ELF file` and exits 0. Fine for the Linux runners that call it, unhelpful locally; I did not check whether its `test_coredump.py` covers that path.
- **No dependency/CVE scanning and no WIP/retention policy** exist anywhere in `.github/`; out of scope for the evidence I gathered, noted so the assembler does not read silence as coverage.
- I did not attempt to reproduce the 1.42.4 publish failure or dispatch any workflow (that would mutate the repo's state); every CI conclusion comes from `gh` read-only queries and the workflow sources.

Grade mix: MEASURED 6 (F63–F4), READ 3 (F68–F69), INFERRED 0, UNVERIFIED 0.

**Lane J** — - F71's live cloud round-trip was not run (no object store in this environment). The two tests pinning the skipped branch are read, not re-run: `dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~SyncServiceRemoteBlobTests"` returned "Zero tests ran" (exit 5) under this repo's MTP test platform, and I did not spend the remaining budget on the filter syntax.
- Not examined: `dotnet list package --vulnerable` (dependency CVE scan — no restore/network run), the `bws` binary's own behaviour, DNS-rebinding/`Host`-header handling of `/observability` from a real browser (a same-origin-policy read would be blocked by the absent CORS headers; not measured), and `ProxyForwarder`'s error-rewriting path.
- `--port 0`/random-port serve was not probed for the F70 squat shape; the default fixed port 7721 is the exploitable configuration either way.
- The `serve` attach warning says the process "never opened that bank to check" (`NodeRunner.cs:249-259`), so at least the operator gets a line — for the proxy path (`BackendLauncher`) there is no such line at all; I did not check whether that asymmetry is deliberate.

## Owner questions

**Lane A** — - Lower `MaxLines`/`MaxMembers` to today's measurements (1046/26), and add a cap for `MemorySchema.cs` and `SyncService.cs`, or accept the slack?
- Should the alias map be injected into `ToolGate` and Core's three key factories instead of read as `ProjectIdAliasMap.Default`?
- Delete `ISearchParametersSettings`, or implement the plan's `SqliteSearchParametersSettings`?
- Adopt the clean-layering invariant's reference allowlist as a layering rule for `AiRaccoon.Core`?
- Delete `ProjectIdAliasMap.LoadFromFile` and its three tests?
- Move `FakeCloudStore` into the tests project (and fold the four private copies into it)?
- Drop the four settings members from `IMemoryStore` now that D4 has been paid down?
- Answer the 0821 question that is still open: one generic port-placement rule instead of Rule 4's single-name pin?

**Lane B** — - Recapture `minilm-eval-set-100.json` under ORT 1.30.0 (with the ORT version added to `Provenance`), or re-pin `Microsoft.ML.OnnxRuntime` to 1.29.0?
- Should `evidenceByHash.cosine` carry the raw content cosine (structure blend kept separately), or keep the structure-penalized fused score and label the absent-structure rows?
- Should the default 0.6 floor stop capping an explicitly raised `limit`, or should the response mark the truncation and `search_quality` start storing `limit`?
- Is `Models/model_qint8_arm64.onnx` inside the trust boundary (no activation hash check needed), or should the runtime enforce the pinned sha256 before it embeds?

**Lane C** — 1. May any propose pass (manual or the auto-promote loop) delete an agent-requested candidate whose scorer version is stale, or must `reason=agent-requested-share` be exempt from `ClearStale`? (F22)
2. Is the reaper allowed to remain gated on a rating that only changes when a memory is read, or must WP1 of the decay plan ship before TTL expiry is trusted? (F23)
3. Are `workspace_id` and `context` a supported combination — if so which wins, and should a `shared`/custom context be refused inside an active workspace? (F24)
4. After a permanent discard, should a later explicit `context=shared` write report success, a refusal, or deliberately resurrect the candidate? (F25)
5. Is the chunk the intended unit of `memory_delete`; if yes, how is a caller expected to delete the rest of a long write it just made? (F26)
6. Does S1 require an access-mode check on `allProjects=true`, or is the explicit consent flag the accepted realization of "read-all mode"? (F3)
7. Should `memory_set_ttl` on a shared-tier hash answer "shared entries are sweep-exempt" instead of `unknown-hash`? (F27)
8. Should `promotedHashes` name the shared rows created, or stay a list of source hashes? (F28)

**Lane D** — - **F29/F30/F35**: are un-tombstoned deletes (`memory_delete_context`, workspace-twin `memory_delete`, `DeleteSourcePathAsync`) a bug to fix uniformly, or is there a ruling that only hash-addressed `memory_delete` propagates through sync? If the latter, the tool description and ADR-0052's "Destructive = reaching committed memory" are misleading and should say the delete is local-only.
- **F31**: should re-created content survive its own tombstone (`created_at` guard — the fix D27 proposed in 2026-08-07), or is "delete wins forever for a given hash" the intended semantic? If the latter, resurrecting a fact is currently impossible without a new hash/path.
- **F32**: is `doctor` contracted as existence-only ("verifies schema shape", its own footer), or should it verify index definitions/uniqueness the way it verifies table columns? A HEALTHY verdict currently cannot be distinguished from "uniqueness silently gone".
- **F34**: should the entries CHECK be tightened to exactly-one (`(workspace_id IS NULL) <> (scope IS NULL)`), or is the neither-case a shape some legacy/remote data still uses?

**Lane E** — 1. **F7** — for a bank that exists but is not a SQLite database, should `doctor` return 2
   (`FailedToOpenEncryptedBank`) to match its documented table, or should there be a distinct
   infra-failure code (i.e. is `InvalidArgument` ever allowed to mean "the bank is broken")?
2. **F37** — is exit 130 the wanted interrupt code for one-shot CLI commands, or should Ctrl-C keep a distinct
   ai-raccoon code (and must the message also say nothing was changed)?
3. **F38** — should a CLI command stop a backend *it* started when it exits, or is leaving it to the 4-hour
   watchdog intended (and if intended, is a short `--idle-timeout` the wanted middle ground)?
4. **F39** — should any verb other than `doctor`/`encryption` refuse to run against a data root with no bank, or
   is silently minting one the accepted cost of "the server auto-starts"?
5. **F40** — is an `ObjectDisposedException` guard in `Cancel`/`CancelAll` acceptable, or should the CTS
   disposal move inside the entry-removal critical section (which is the fix that removes the window)?
6. **F41** — does the #636–#640 "one line, no stack" rule apply to a background *pass* failure, or only to
   per-row best-effort writes (in which case the three sites stay as they are)?

**Lane F** — 1. **`--quiet` `quiet.log` ownership**: single writer (serve owns it) or an OS-level lock? F42 is a real corruption of the only quiet-mode trace and the fix choice changes who can log.
2. **Memory-engine warning**: should a fresh bank's `kind=memory` search carry the same `warning` the code corpus gets (F6), and if the silence is deliberate, which ADR says so?
3. **`--install-scope project` layout**: token beside the bank (`<data-root>/.ai-raccoon/mcp-token`) or keep `<data-root>/mcp-token` and document + ship an ignore entry (F49)?
4. **MCP `initialize` contract**: the `instructions` string and protocol-version negotiation appear in no reference doc — is that deliberate (client-layer text, not contract) or should `agent-memory-server.md` document them?
5. **Reference exit-code table**: `docs/reference/agent-memory-server.md` names retired exit `8` and has no full exit-code table (F43); should the new 10–16/17–25 codes get one table owned by a test, the way the refusal prefixes do?

**Lane G** — - Should the first-run path (Quick Start/tutorial/server instructions, or a `memory_search` warning)
  name memory-engine activation, or are `doctor` and `settings model show` the intended discovery
  surface?
- Should the code-corpus warning say "the code section is FTS5-only", given both corpora are
  keyword-only on a fresh bank?
- Should a flat-margin single-leg response carry a `warning`, or is signals-only final until the
  Stage-2 relevance map ships?
- Should `access-denied` (and `path-outside-scope` / `watching-disabled`) repeat the CLI remedy the
  sibling refusals already give?
- Is the explanation's "search only returns embedded content" sentence to be corrected, or does it
  mean something narrower than it reads?
- Should a docs-lint or gate reject committed merge markers in reference documents, so OPS-17 cannot
  recur as a silent four-week miss?

**Lane H** — - Should the trait guards validate values against the closed set (`Fast|Slow|Nightly`, `Unit|Integration|E2E|Retrieval|bdd`) instead of name-presence (F59)?
- Should the retry surface emit a retry counter/diagnostic so a retry-recovered failure is visible and ledgerable, rather than only its final verdict (F8)?
- Which lane owns `ThreadResolutionTests`' split-leg theory — split it into a Fast/Unit case and a Slow/Integration case, or accept the duplicate (F60)?
- Should `RetrySurfaceGateTests`' retry mandate be narrowed to tests that hold a real external resource, rather than every Integration/ file (F8)?
- Delete the `@ignore`d 17-tool scenario or rewrite it to 29/derived, now that un-ignoring passes (F2)?

**Lane I** — 1. Should a release tag require its package to be resolvable on nuget.org — add a post-publish reconciliation step, or accept tagged-but-unpublished versions (F63: `v1.42.4`, `v1.41.1`, `v1.38.0`, `v1.34.4`)?
2. Is label-only `Speed=Nightly` final, or should the labelless backstop come back as a scheduled job (and with it the unfiltered trait-typo run that `nightly-triage.py` documents)? (F64)
3. Add `(^|/)global\.json$` and `^benchmarks/` to `CODE_REGEX`? (F65)
4. Extend `scripts-harness` to the 35 excluded files that pass under the CI dependency set in ~12s, or record a per-file reason for keeping each out? (F66)
5. Add `mkdir -p dumps` + crash-dump upload to `build-fast`, or drop the dump env vars as decorative? (F67)
6. Wire `scripts/verify-history-scrubbed.py` into a gate, and add the two historical `reference-topk.json` spellings to its `PATHS` before the S6b rewrite? (F4)
7. Derive `manual-fresh-install-test.py`'s default version from `VERSION` (or fail when `AI_RACCOON_VERSION` is unset) instead of hand-bumping it? (F68)
8. Revive or delete `nightly-triage.py` and `known-flakes.json` — and if revived, give it a runner? (F8)

**Lane J** — - Should the probe/launcher require the listener to identify itself as ai-raccoon before the proxy hands it the loopback token and the agent's traffic (F70), or is "the port is on this machine" an accepted part of the threat model?
- For unencrypted banks, is a per-machine sync secret (or a first-contact digest pin) wanted, or is "bucket write access = memory takeover" accepted for the default configuration (F71)?
- Should `memory_promotion_list(allProjects=true)` keep returning absolute paths and full values with no mode check (F3), or should that branch consult a mode/consent setting as ruling S1 originally asked?
- Is the refused-query text allowed to reach the log file and OTLP (F72), and should SECURITY.md's "no search queries" claim be narrowed to match?
- Should `verify-history-scrubbed.py` run in CI until #414 lands, so the rewrite cannot be forgotten (F4)?
