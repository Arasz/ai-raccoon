# Improvement plan — 2026-08-14 MoE codebase review (rev 2)

Source: `docs/reviews/2026-08-14-moe-codebase-review.md`. Base commit `b4581717`.
Baseline to preserve: build 0/0; `dotnet test` 2277 passed / 0 failed / 6 skipped / 5m34s.

**Rev 2** folds in an architect review and an adversarial gate on rev 1. Both are reflected
throughout; the material changes are listed under *What rev 2 changed* at the end.

## Status — 2026-08-14, end of the autonomous run

Everything below landed on `task/code-quality-review` (PR #278) unless the row says otherwise.

| Package | State | Note |
|---|---|---|
| WP1 + WP2 | landed | blocker B1 closed; `Stored`/`Reason` on the record (ADR-0032) |
| WP3a | landed | blocker B2 closed; `memory_get` (ADR-0035) |
| WP4, WP4b, WP4c | landed | corpus regenerated through the production chunker: 761 rows/0 structure vectors → 2518/871, repo-relative paths, hashes derived from the corpus instead of a retired JSON map |
| WP5a, WP5b | landed | delete/tombstone transaction, workspace uniqueness + v7 ladder step (ADR-0037) |
| WP6 | landed | `BumpAccess` N+1 removed; per-open Bitwarden shell-out cached (ADR-0038) |
| WP7 | landed | engine-aware chunk budget; 0 of 4121 chunks over the window (ADR-0036) |
| WP8-ratchet | landed | the cap was **lowered**, not raised: `ISettingsStore` extracted, store 1315 → 1290 lines |
| WP8 (rest) | open | five seams remain: write, search, delete, ingest, embedding |
| WP9 | landed | watch-embedding retry sweep, Bitwarden caching, SIGINT/SIGTERM wiring |
| WP10, WP10b, WP10c | landed | vacuous gates replaced; test-hygiene consolidation; D21 concurrency gaps closed, one real `WatchPipeline` bug found by them |
| WP11 | landed | exit codes, parse-error triple print, the unknown-sync-provider fallthrough, doc drift |
| WP3b | **open** | four ranking regressions on the denser corpus stay pinned as characterization tests; two more were *not* ranking problems and are restored — see `docs/work/2026-08-14-retrieval-rank-regressions.md` |

Work identified during the run and also landed, outside the original plan: the read-path query
guard (ADR-0040), the structural/lexical detector (ADR-0041), the noise substrate and shadow mode
(ADR-0039, superseding ADR-0033 on the substrate), the `section` ingest defect, the dogfooding
audit of `scripts/` (ADR-0042), and derived gates for the ADR index and the event-id registry.

## Urgency calibration (measured against the live bank)

The deployed bank (15,236 entries, build 1.11.0, which *does* contain both ADRs) shows
`noise_entries = 0` and `ttl_days` NULL on every row. **Neither blocker has fired in
production.** Both code paths are exactly as described; the reason they have not bitten is
behavioural — the filter's own 0/50 recall, and real writes running 64–166 words.

- **This is not a hotfix.** Nothing is being lost right now, so the waves can be done properly.
- **But the reaper is armed:** `access.mode.global=full`, `sweep.enabled.global=true`.
- **And it is shipped:** `v1.10.0` and `v1.11.0` both contain it; `git diff v1.11.0..HEAD -- src/`
  is empty.
- **Hard constraint:** no work increasing the noise filter's recall may land before WP1.

## Sequencing rules

1. **WP1 and WP2 ship in ONE PR.** *(Adversarial finding 1 — this corrects rev 1.)* Rev 1 had
   WP2 delete `SqliteNoiseStore`/`noise_entries` while leaving the `"noise_hash"` fabrication for
   WP1. In that interval a rejected write would be fabricated-success **and** genuinely
   unrecoverable — strictly worse than today, where the content at least survives unreachably.
   Within the PR: land the honest `Stored`/`Reason` outcome **before** deleting the noise store.
2. **The measurement chain is WP7 → WP4 → WP3b.** *(Architect finding 1 — this corrects rev 1's
   wave order.)* WP7 changes chunk boundaries, so a corpus regenerated before it is invalidated
   by it; WP3b's ranking changes cannot be measured without that corpus. Rev 1 had this exactly
   backwards.
3. **`SqliteMemoryStore.cs` is a serialization point.** WP1, WP2, WP3a, WP5a and WP6 all edit it.
   This repo runs concurrent worktree sessions — no two packages touching it run in parallel.
4. Each package names acceptance criteria **and** a gate that is watched go red first.

## Wave 1 — The write path (blocker B1) · one PR · release as 1.12.0

### WP2 · Delete the unearned judgement layers
**Effort:** SMALL · **Net:** ~374 production lines + `EmbedContentAsync`/`IContentEmbedder`,
2 schema tables, **6** test files

**This is a behaviour change on a live path, not dead-code removal.** *(Architect finding 5.)*
`ZeroShotEmbeddingNoisePolicy` **is** registered (`AppRegistrations.cs:174`) and runs on every
write; writes rejected today will start being stored. Only the clustering trio
(`OnlineNoiseClusteringService`, `NoiseFeedbackCollector`, `INoiseClusterStore`) is genuinely
unreachable.

Delete: `ZeroShotEmbeddingNoisePolicy`, `BundledNoiseVectorProvider`, `INoiseVectorProvider`,
`ZeroShotEmbeddingFilter`, the clustering trio, `PromotionScorerTtlPolicy`, **`IAutoTtlPolicy`**
(verified: the TTL policy is its only implementer), `SqliteNoiseStore`, **`INoiseStore`** and
`NoiseFilteringService`'s `INoiseStore?` parameter, and — dead once their only three callers go —
**`EmbedContentAsync` + `IContentEmbedder` + its registration** *(architect finding 8; this moves
the "second ONNX session" item out of WP6)*.

Keep `HermesProcessNoisePolicy`, `INoiseFilterPolicy` and `NoiseFilteringService`.

**Call-site edits forced:** `SqliteMemoryStore` ctor drops `IEnumerable<IAutoTtlPolicy>`;
`WriteAsync` loses the TTL loop (`:46-54`); `AppRegistrations:173-177` loses three registrations;
`NoiseFilteringServiceTests`' `FakeNoiseStore` is reworked.

**Schema:** remove `noise_entries` / `noise_clusters` / `vec_noise` from the fresh-bank DDL.
**Leave `MigrateToV6Async` in place as a historical no-op — never renumber or delete a ladder
step** *(architect finding 5; rev 1 wrongly said "fresh banks only", but V6 runs on existing
banks).* Existing tables stay inert. WP6's index becomes v7.

**Before deleting the noise store** *(adversarial finding 2)*: run
`SELECT request_content, detected_by_policy, created_at FROM noise_entries` on any live bank and
record the result in ADR-0033. On the reference bank this is **verified 0 rows**, so nothing is
lost there — but the review's own top open question ("the filter's false-positive rate on real
traffic") dies with this table, and it is free to answer first.

**Acceptance criteria**
1. `grep -rnE 'ZeroShot|NoiseCluster|NoiseFeedback|NoiseVector|NoiseStore|PromotionScorerTtl|IAutoTtlPolicy|IContentEmbedder' src/`
   returns nothing. *(Rev 1's grep would have passed with five of these still present.)*
2. A Hermes background-process log is still rejected.
3. `"Push after every commit."` is **stored with no `ttl_days`** — the RED test today.
4. Full suite green; `CurrentVersion` unchanged.

**Gate:** `ShortOrganicWrite_IsStoredWithoutTtl` — RED on `main` today (it gets `ttl_days = 3`),
GREEN after. Paste both.

### WP1 · Make `memory_write` report the truth — **BLOCKER B1**
**Effort:** SMALL

Add `Stored` and `Reason` as **additive optional members on the `MemoryEntry` record — not a new
return type** *(architect finding 10)*. `IMemoryStore.WriteAsync` returns `MemoryEntry` and
**16 test files implement `IMemoryStore`** (measured); a new return type turns a SMALL package
into a 16-file edit. The interface stays at 25 members until WP8.

Surface `Stored`/`Reason` on the MCP `WriteResult`, update the tool description, delete the
`"noise_hash"`/`"noise_path"` sentinels, and add a `noise.enabled` settings key mirroring
`sweep.enabled`. Fix `WritePerformanceBenchmarkTests.cs:70`, which branches on the sentinel and
breaks the moment it goes.

**Acceptance criteria**
1. A rejected write returns `stored=false` with a `reason` naming the policy and **no fabricated
   hash**.
2. A stored write returns `stored=true` and a hash `memory_get` (WP3a) resolves.
3. `noise.enabled=false` disables rejection end to end.
4. The tool description states a write may be refused and how to tell.

**Gate — two-step, because the test cannot compile today** *(architect finding 11)*:
(1) land the `Stored` member hardcoded to the wrong value, run `RejectedWrite_ReportsNotStored`,
paste RED; (2) wire the behaviour, paste GREEN.

**Release:** cut **1.12.0** (minor, not patch — it removes a documented shipped feature).
Supersedes ADR-0029 and ADR-0030; restores ADR-0025's "Fact 1".

## Wave 2 — Blocker B2 + data integrity

### WP3a · Make search results readable — **BLOCKER B2**
**Effort:** SMALL · *No corpus needed — this is all of B2*

1. **`memory_get(projectId, hash)`** returning the full value.
2. Make the vector-hit snippet query-relevant instead of hash-seeded; raise the FTS snippet to
   ~40 tokens with real `…` delimiters.
3. Drop the dead `Seq` field and the redundant hash-derived `Path`.
4. **Dedup promoted copies** *(moved here — architect finding 4 caught that rev 1 mis-cited this
   as "FTS5 partitioning")*: a shared-tier promotion gets a different hash from its project
   original, so `scope=all` returns both copies. Dedup on `ContentHash.OfValue` in
   `ModalityCandidates`, preferring the project row.

**Gate:** two-step as WP1 (the `memory_get` test cannot compile today). Plus a dedup test that is
RED today on a promoted-and-original pair.

### WP5a · Data-integrity quick wins
**Effort:** QUICK

- Wrap `DeleteCoreAsync`'s entry-delete + tombstone-insert in one `BEGIN IMMEDIATE`/`COMMIT`.
  *(Verified safe: only two callers, both top-level, so no nested-transaction hazard.)*
- Wrap `RememberDiscardsAsync`'s per-hash loop in a transaction.
- Delete the dead `EntryExistsByPathInBucket` SQL constant.

**`PRAGMA synchronous=NORMAL` is deferred, not adopted** *(architect risk 2)*. Trading durability
for throughput in a product whose promise is not losing writes needs its own measurement and its
own decision; WP6's batching is pure upside and comes first.

**Gate** *(architect finding 6 — rev 1 added a rollback branch with no test)*: WP10b's
forced-failure test is extended to cover `DeleteCoreAsync`'s new rollback.

### WP5b · Data integrity — concurrency and uniqueness
**Effort:** SMALL · *(Restored: rev 1 dropped these three behind an orphan "WP5a" — architect
finding 3.)*

- **DA-F1 (HIGH):** workspace writes have no uniqueness guard — the partial indexes cannot match
  `scope IS NULL` and the value-dedup check is skipped for workspaces, so a retry silently
  duplicates. Add `uq_entries_workspace_bucket … WHERE workspace_id IS NOT NULL`.
- **A-F7:** `CloseAsync` is an unguarded UPDATE with a TOCTOU window; add `AND status='Active'`,
  return affected rows, and give `Workspace` real transitions.
- **A-F11:** the promotion queue claims by *delete*, so a transient `ShareAsync` failure destroys
  the candidate. Claim with `UPDATE … SET claimed_at WHERE claimed_at IS NULL` + a stale-claim
  sweep.

**Gate:** a concurrent consolidate+discard test that is RED today (both currently win).

### WP10b · Test the two things that guard data loss
**Effort:** QUICK–SMALL

- A unit test for `LikePattern.Escape` — **zero test references exist today** (verified).
- An integration test where one path is a literal-wildcard substring of another (`note_1.md` vs
  `noteX1.md`).
- A forced-failure-mid-transaction test covering `DeleteSourcePathAsync`, `ReplaceFileAsync` and
  WP5a's `DeleteCoreAsync`. **Mechanism, named so it is not quietly dropped** *(architect finding
  12)*: hold a write lock from a second connection so the second statement fails naturally, or
  use a temporary trigger that raises. **No test-only seam in production code.**

**Gate:** break `LikePattern.Escape` (drop the `_` escaping), watch it go red. This is the
plan's strongest gate — the type exists and has no tests, so break-it-and-watch works directly.

### WP8-ratchet · Stop the god class growing
**Effort:** QUICK · *(Architect finding 16 — instead of deferring WP8 with no brake.)*

A test that fails if `SqliteMemoryStore.cs` exceeds its current 1250 lines or `IMemoryStore`
exceeds its current 25 members. The file grew 1111→1250 in eight days from ordinary feature work
(verified: the noise/TTL commit contributed only 21 net lines), so deferral without a ratchet
means it keeps growing. This is a gate that can be watched go red.

## Wave 3 — The measurement foundation

### WP7 · Fix chunking so content reaches the embedding
**Effort:** QUICK–SMALL · **Must precede WP4**

**The requirement is a guarantee, not an improvement** *(owner directive, 2026-08-14)*. The
ratio-based stopgap this plan originally allowed — "set the local budget to 200, which the
measured p95 ratio of 1.217 covers" — is **withdrawn**. A ratio only moves the tail. The
WordPiece/BPE ratio is content-dependent and unbounded: 1.217 was measured on this repo's English
prose, and it blows out on exactly what this project stores — hex hashes and base64, long code
identifiers, URLs and paths, and any non-Latin script — because WordPiece shatters unknown
tokens into many subwords.

**How chunk size is calculated:**

1. **Budget = the engine's window minus its special tokens.** For the bundled model that is
   `MaxSequenceLength - 2 = 254`, **not 256**. `OnnxEmbeddingGenerator.Encode` calls
   `EncodeToIds(text, true, true, true)` — adding `[CLS]` and `[SEP]` — then truncates with
   `ids.Take(256)`, so an over-length input silently loses its `[SEP]`. Derive the window from
   the engine (`EmbeddingService.ContextTokensFor` already knows it per provider); OpenAI has a
   different window *and* a different tokenizer.
2. **Count with the tokenizer that will embed.** The `TokenCount` delegate the chunkers consume
   comes from the resolved engine, so budget and model cannot disagree. Today the budget is
   `o200k_base` and the model is BERT WordPiece.
3. **Tokenize once; split on offsets.** Encode the document once, take token→character offsets
   (`EncodeToTokens` → `EncodedToken.Offset`), and choose split points at semantic boundaries
   that land on token indices. Every span is then within budget **by construction**, it is O(n)
   rather than O(n²), and it avoids the trap that WordPiece is not composable across a join —
   `tokens("foo") + tokens("bar") ≠ tokens("foobar")` — which makes incremental re-counting
   subtly wrong.
4. **Nothing is atomic above the budget.** The fence bug is one instance of a general rule: a
   split-point ladder — heading → paragraph → sentence → line → token — falling through until
   one succeeds. The token level always succeeds, so termination is guaranteed and no unit can be
   emitted over budget. Tables, minified JSON, one long line and one very long word fail the same
   way today; fix the rule, not the case.
5. **Overlap counts against the same budget in the same tokens**, or it silently pushes a
   compliant chunk over.
6. **Defense in depth:** an EventId'd `[LoggerMessage]` counter in `Encode` so truncation is
   detectable rather than silent. After this change it should be provably zero.

**A second failure mode, found by independent verification and worse than truncation**
*(orchestrator, measured 2026-08-14 against the real vocab and production `BertOptions`)*:

`\n`, `\t` and `\r` are **not word separators** for this `Microsoft.ML.Tokenizers` BertTokenizer
configuration — only spaces and punctuation are. Two 60-character alphanumeric words:

| separator | ids |
|---|---|
| space | **62** |
| newline | **3** (`[CLS] [UNK] [SEP]`) |
| tab | 3 |
| CRLF | 3 |

So **any run of ≥100 characters containing no space and no punctuation — newlines freely
included — collapses to a single `[UNK]` and is embedded as nothing.** Confirmed on realistic
content: 60 newline-separated SHA-256 hex lines (3,899 chars) tokenize to **3 ids total**. File
paths and base64 escape only incidentally, because `/`, `.`, `+` and `=` are punctuation that
does split. This repo's own docs contain newline-separated hash tables.

**It is invisible to a budget check**, because the offending chunk reports a *tiny* token count
rather than a large one and sails through any `≤ 254` assertion. Therefore the counter must also
flag **implausibly low token-to-character ratios**, and the gate needs a *floor* as well as a
ceiling. Chunker-side remediation is optional for this wave; detection is not.

**Also measured: engine-aware counting alone does not deliver the guarantee.** Driving today's
chunker with the BERT counter at maxTokens=254 still leaves **127 of 4040** chunks over the
window across `docs/**/*.md` (1234 of 3583 with the legacy o200k counter), because an
over-budget unit is still admitted whole. The token-level split floor is what actually closes
it — counting correctly is necessary but not sufficient.

JSON key-path flattening → deferred to Wave 5.

**Gate — a ceiling *and* a floor.** A corpus combining this repo's `docs/**/*.md` with five
hostile cases — hex/base64 blob, minified JSON line, CJK paragraph, unbalanced fence, and
**newline-separated 64-char hex lines** — must satisfy both:
- **Ceiling:** zero chunks whose BERT WordPiece length exceeds 254. RED today — measured
  **127/4040** over even with the correct tokenizer, 1234/3583 with the legacy one, and the
  fence case yields a single 9,344-token chunk.
- **Floor:** no chunk collapses to `[UNK]`. RED today — the hex case yields **3 ids for 3,899
  characters**.

Both counters are non-zero today and zero after. Asserted as hard invariants, not percentage
improvements.

*Verification is independent of the implementation:* the orchestrator's harness
(`scratchpad/chunkverify/`) loads the compiled `AiRaccoon.Core`, invokes `MarkdownChunker` by
reflection and measures the **output** with the real tokenizer and vocab, so it is unaffected by
how the budget is computed internally.

### WP4 · Regenerate the retrieval gate corpus
**Effort:** MEDIUM · **Must follow WP7, precede WP3b**

Regenerate `jsaa-memory.db` through the production `FileIngestor`. The strongest reason is **not**
the one rev 1 gave: `source_file` is present on 761/761 rows already *(adversarial finding 9)*.
The real gaps are **0/761 structure vectors with `vec_structure` empty** (confirmed), and the
fixture sitting on an older schema without `chunk_index`/`total_chunks`/`source_id`. Source
affinity is a no-op only in the `RealWorldCorpus` harness (`ManagedHarness.cs:66`), which is the
thing to fix there.

Delete `GoldenFile_MatchesFreshReferenceRun` (tests a vendored legacy extension; cannot fail on
any retrieval change). **Do not hunt for ADR-0006's "lost 0.048"** — the re-pin's reason is
recorded at `RrfParameterSweepTests.cs:161-166` *(adversarial finding 6)*; just amend the ADR so
it and the gate agree.

**Note:** `RealWorldCorpus.cs` lives in `benchmarks/` and is `<Compile Include>`-linked into the
test project, so the blast radius includes the benchmarks project. A regenerated corpus also
permanently retires ADR-0006's 0.722 baseline as a comparand.

**Gate:** deliberately break the fusion; the regenerated corpus must make it go red. A corpus
that cannot do that has not fixed anything.

## Wave 4 — Ranking, performance, honest gates

### WP3b · Honest scores and unbiased fusion
**Effort:** SMALL · *Measured on WP4's corpus*

- Remove the degenerate second RRF pass (`SearchResultMerger.Merge([fused], …)`).
- Fix the missing-modality bias in `StructureFusion` (renormalise over observed modalities, or
  fuse the two vector legs by RRF and delete `Fused` and the alpha setting).
- Carry the best **absolute** signal (max cosine, bm25) and make `min_score` filter on it.

**Two corrections from review** *(adversarial finding 8, architect finding 7)*:
- The structure vector exists iff the **value contains an H1/H2**, not because a row was
  ingested — so the split is "has a markdown heading" vs not. An agent note starting `# Title`
  gets one. State this in the ADR; it changes who is penalised.
- **`ReciprocalRankFusion.Fuse` already normalises to max 1.0.** Removing the second pass will
  *not* by itself make top scores differ between searches — only the absolute-signal item can.
  Rev 1's criterion "two searches return different top scores" was therefore unachievable by the
  change it was attached to; it now belongs to the absolute-signal item alone.

**Gate:** a high-content-similarity heading-less note ranks above a low-content-similarity
heading-bearing chunk in a mixed bank — RED today.

### WP6 · Stop paying per-row costs on the hot paths
**Effort:** SMALL

- **Index — corrected** *(adversarial finding 3)*: use
  `ON entries(project_id, value) WHERE workspace_id IS NULL` (measured **11.3 ms → 0.007 ms**,
  because it converts the `value = ?` scan into a seek), plus a `(project_id, path)` variant for
  `DeleteBySourcePath`. **The rev-1 index `(project_id, workspace_id)` is near-worthless**:
  re-measured on an ANALYZE'd bank — the state this project's own maintenance service produces —
  it is 1.0×, because `idx_entries_embed_state` already skip-scans `project_id`. Ladder step v7.
- Batch `BumpAccessAsync` into one `WHERE hash IN @hashes` inside one transaction.
- Make `ToolGate`'s promotion-queue meta opt-in rather than running on all 25 tools.
- Drop `FileIngestor`'s redundant EXISTS pre-check; wrap a file's chunks in one transaction.
- Replace the O(n²) `Skip`/`Take` in the bulk re-embed loop; replace `EmbeddingBlob`'s per-float
  loop with `MemoryMarshal.Cast`.

**Gate** *(architect finding 6)*: a numeric floor, not "a measurement is recorded" — the dedup
query must fall below a stated threshold on an **ANALYZE'd** bank with the real index set, and
`BumpAccessAsync`'s statement count for a 20-result search must be ≤ 2.

### WP10 · Make the remaining gates honest
**Effort:** SMALL

- Fix the write-performance benchmark: assert the 50 valid notes were **stored**; assert *which
  policy* intercepted; stop writing into tracked `docs/`; fix or delete the allocation metric
  (`GC.GetAllocatedBytesForCurrentThread` read across `await` is why the committed report says
  `-549,67 KB`).
- **Guard against silently-dropped SQL parameters.** `MemorySql.InsertEntry` has no `ttl_days`
  column while `WriteAsync` passes `ttl_days` into the Dapper parameter object; Dapper silently
  ignores unmatched parameters, which is how ADR-0030's auto-TTL shipped with an ADR, a benchmark
  and green tests while never once persisting a TTL. Add a test that asserts, for every statement
  in `MemorySql`, that each property of its parameter object has a matching `@placeholder` in the
  SQL. Break it on purpose (add a bogus parameter) and watch it go red. This is a cheap gate
  against a whole class of silent failure.
- Commit a small paraphrased labelled fixture so the promotion scorer has a hardcoded floor.
  *(Framing corrected — the existing `measured ± 0.10` tests are documented gate-machinery tests
  for the no-fixture path, not a defect; the gap is only the absent fixture.)*
- **Skips** *(architect finding 13, adversarial finding 4)*: rev 1's "un-skip or delete" would
  have deleted the scorer's only real-data check, whose fixture is deliberately uncommittable.
  Correct criterion: **each skip either gains a committed fixture or gains a recorded reason it
  must stay env-gated.** `All 17 tools are still listed` pins a count that is now 25 — derive it
  from the `[McpServerTool]` attributes (`ToolInventoryTests` already does) or delete it.
- Correct `PromotionScoringRealDataTests`' stale prior table (its ordering is no longer monotone).
- Add `NoiseFilteringService`'s clean-path / short-circuit / null-store tests (QA-F3).

## Wave 5 — Structure, silent failure, polish

- **WP9:** watch-embedding retry sweep; Bitwarden key caching + async; SIGINT/SIGTERM wiring;
  the one direct `logger.LogWarning` on the search hot path.
- **WP8:** extract `ISettingsStore`; split `SqliteMemoryStore`; de-duplicate the access-mode
  resolver; remove the `IMemorySourceStore` downcast; split `Core/Memory/`; delete the duplicate
  DI registrations; move `Core/Resilience` out of Core. Sized by the WP8-ratchet's evidence.
- **WP11:** exit codes; triple-printed parse error; raw errno leak; logger category leak;
  `serve` ignoring `--data-root` on attach; `--json`; **the unknown-sync-provider fallthrough
  that silently selects S3** *(architect finding 14 — a config typo ships a bank to the wrong
  backend)*; reference doc missing 2 of 25 tools; tutorial mermaid + verify step; verb
  consistency; tool merges; ADR-0027/0029 name drift; CLAUDE.md casing (LOW).
- **WP10c:** `NodeRunnerTests`' 116s; `FakeTimeProvider` conversions; trait-convention fix on the
  three files surviving WP2; `ConfigCommands` harness extraction.
  **Do not scope a test-shrinking effort** — only ~180–220 lines are justifiably deletable.

## ADRs

| ADR | Decision | Supersedes / amends |
|---|---|---|
| 0032 | A write outcome is truthful: `Stored` + `Reason` on the record and the tool contract | supersedes ADR-0029 §Decision 2 |
| 0033 | Remove the zero-shot noise filter and the noise-learning subsystem; keep the deterministic policy. Records the pre-deletion `noise_entries` read | supersedes ADR-0029 |
| 0034 | An explicit TTL is authoritative; no heuristic assigns one at write time. **Records that the motivation is relocated, not abandoned — `memory_set_ttl` already provides the explicit path** | supersedes ADR-0030; restores ADR-0025 Fact 1 |
| 0035 | `memory_get`, plus search scores carrying absolute signal | amends ADR-0004, ADR-0006 |
| 0036 | Engine-aware chunk token budget with a guaranteed split floor | — |
| 0037 | Workspace and promotion-queue concurrency guards | — |
| 0038 | Cache the resolved encryption key per config fingerprint | — |
| 0039 | The noise-learning substrate and shadow mode — no detector yet | supersedes ADR-0033 on the substrate |
| 0040 | Read-path query guard | — |
| 0041 | Structural/lexical noise detector on the read path | extends ADR-0040 |
| 0042 | Fixtures are built by the product, not beside it | — |

## Owner decisions — resolved autonomously

No owner was available; each was decided and recorded. Reversible if the owner disagrees.

| Question | Decision | Reason |
|---|---|---|
| Patch release for Wave 1? | **1.12.0 minor** | removes a documented shipped feature, more than a bugfix |
| `PRAGMA synchronous=NORMAL`? | **Deferred** | durability trade in a durability product needs its own measurement |
| Dump `noise_entries` first? | **Not needed here** (0 rows measured), but the step is in WP2 for other deployments |
| Auto-TTL successor? | **Delete outright** | `memory_set_ttl` already provides the explicit path |
| Drop legacy noise tables? | **Leave inert**; never renumber `MigrateToV6Async` |
| Is `minScore=0.7` a bug? | **Bug** | documented as a 0..1 score floor, behaves as a rank cutoff |

## Risks

- **WP3b alters ranking for every existing bank.** It must follow WP4 or it is unmeasured.
- **WP2 deletes code with passing tests** — they pass over a live-but-useless policy and an
  unreachable subsystem. The ADR must record why the diff looks alarming.
- **WP5b's uniqueness index may find existing duplicates** on live workspace banks; the ladder
  step needs a dedupe like `MigrateToV1Async`'s.
- **`SqliteMemoryStore.cs` serialization** (sequencing rule 3).
- **WP8 is the package most likely to exceed estimate** — 16 test fakes × 25 members.
- **WP4 retires ADR-0006's 0.722 baseline permanently** and touches the benchmarks project.

## Explicitly not doing

- Shrinking the test suite (measured: earned breadth, not bloat).
- Rewriting the 319 long doc comments — an analyzer on *new* ones instead.
- ANN / vector-index work — `memory_search` is 94 ms p50 at 174 docs, dominated by fixed
  overhead. Revisit near ~50K entries per project.
- FTS5 project partitioning (**DA-F7**, not RAG-F7 — rev 1 mis-cited this) — accepted trade-off;
  record it as an ADR note.

## What rev 2 changed

1. **WP1+WP2 now ship as one PR** — rev 1's split would have made B1 strictly worse in between.
2. **Wave order corrected to WP7 → WP4 → WP3b** — rev 1 had the measurement chain backwards.
3. **WP3 split into WP3a (B2, no corpus) and WP3b (ranking, needs corpus)** so the blocker is not
   held hostage to a MEDIUM corpus regeneration.
4. **WP5b restored** — rev 1 silently dropped three data-integrity findings.
5. **The index was replaced** — rev 1's is ~1.0× on an ANALYZE'd bank; the new one is 11.3 ms →
   0.007 ms.
6. **Gates added to WP4, WP5a, WP6, WP7** — rev 1 promised every package had one and four did not.
7. **`PRAGMA synchronous` deferred**; **`EmbedContentAsync`/`IContentEmbedder` moved to WP2**;
   **`IAutoTtlPolicy`, `INoiseStore` added to the delete list**; grep widened; test-file count
   corrected 4 → 6.
8. **WP1 constrained to additive record members** so it stays SMALL against 16 test fakes.
9. **Two-step RED procedure** specified for gates that cannot compile today.
10. **A pre-deletion `noise_entries` read** added; **WP8-ratchet** added; **release step** added.
11. Corrected citations: RAG-F7 vs DA-F7, RAG-F8, RAG-F14, ML-F5 framing.
