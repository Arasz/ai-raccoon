# MoE whole-codebase review — AiRaccoon

Date: 2026-08-14 · Base commit: `b4581717` · Reviewers: seven parallel expert lanes + orchestrator

## Method

Seven read-only expert lanes ran in parallel, each in its own git worktree, each given the same
verified ground-truth brief and told to file findings with `path:line` evidence and an explicit
grade (`MEASURED` / `READ` / `INFERRED` / `UNVERIFIED`). Lanes: **architect** (opus),
**RAG/retrieval** (opus), **ML/LLM scoring** (opus), **.NET code quality** (sonnet),
**data access/SQLite** (sonnet), **QA/test suite** (sonnet), **UX — CLI and agent surface**
(sonnet). The orchestrator established the build/test baseline, contributed its own findings,
and re-verified every claim that drives expensive work.

Total: **119 findings** across the lanes, of which **31 are MEASURED**. Every finding below that
survived into a work package was re-checked by the orchestrator against the code at `path:line`.

**Priority rule applied throughout** (from the task brief): the server exists for *efficient
memory storage and retrieval* and *agent assistance*. Where a recorded decision works against
that, the goal wins and the decision is superseded. Three ADRs are contradicted on that basis:
**0029**, **0030**, and **0025**'s central safety argument.

### Ground truth (measured, not assumed)

- `dotnet build` → **0 warnings, 0 errors**.
- `dotnet test` → **2277 passed, 0 failed, 6 skipped, 5m34s**. The suite is green. Prior task
  notes claiming "2 load flakes" and "the OTLP env flake" are stale on this machine at this commit.
- CI's three trait filters partition the suite exactly: Fast 1658 + bdd 142 + Slow 483 = 2283.
  No test escapes CI. All third-party Actions are SHA-pinned.
- Production ≈ 22,400 lines (Core 4,021 / Infrastructure 10,608 / host 7,735). Tests **56,108
  lines** across 277 files — a **2.5:1 test-to-production ratio**.

## Verdict

**The architecture is sound and the write and read paths are both broken.**

The compiler-enforced half of this system is genuinely healthy, and that should not be lost in
what follows: `AiRaccoon.Core` has no project references and no framework leakage, there are
zero dependency cycles, zero captive dependencies, the SQLite migration ladder is versioned and
guarded with a forward-compatibility check, vec0 KNN is correctly partitioned by context, index-
time and query-time embedding preprocessing are symmetric (checked explicitly — a mismatch there
is the most common silent RAG killer and this codebase does not have it), and the project has a
demonstrated willingness to delete a subsystem that stopped paying (ADR-0016).

What is not healthy is everything layered onto the write path in the last release, plus a read
path that cannot return what it finds. Two blockers, and they are mirror images:

1. **A write the noise filter rejects is destroyed, and the agent is told it succeeded.**
2. **There is no tool that reads a memory.** Retrieval can be perfect and the agent still cannot
   see the answer.

Both were found independently by more than one lane and re-verified by the orchestrator. A
memory server whose `memory_write` may quietly not write, and whose `memory_search` returns 200
arbitrary characters with no way to read the rest, is failing at the two things it exists to do.

The single highest-leverage action is **deletion**. Roughly 400 lines of production code, two
schema tables, six of the ten uncalibrated thresholds, and four test files can go — and doing so
removes most of the blocker surface rather than patching it.

---

## The two blockers

### B1 — `memory_write` reports a fabricated success for content it discarded

**Severity:** BLOCKER · **Effort:** SMALL · **Surface:** `SqliteMemoryStore.WriteAsync`,
`MemoryTools.Write`, `WriteResult`
*Found independently by: architect (F1), ML/LLM (F1), orchestrator (O6).*

`src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:39-44`:

```csharp
var isNoise = await noiseFilteringService.EvaluatePreWriteAsync(request, cancellationToken)...
if (isNoise)
{
    // Dummy entry. In C# 10+, MemoryEntry record has 5 properties: Hash, Path, Context, Value, CreatedAt
    return new MemoryEntry("noise_hash", "noise_path", request.Context ?? string.Empty, request.Content, ...);
}
```

Nothing is persisted. `MemoryTools.cs:60-63` maps that fabricated entry straight into a success
envelope, under a tool description that reads *"Returns the stored entry."*

Every rejected write returns **the same** hash and path, so an agent cannot distinguish two
dropped writes from each other or from a real one. `memory_delete("noise_hash")` reports
`deleted=0` — indistinguishable from an idempotent no-op. The content goes to a `noise_entries`
table that **no tool, CLI verb, or SELECT anywhere in `src/` can read** (verified: the only
references are one INSERT and one CREATE TABLE). ADR-0029 §Decision 2 ratifies this explicitly
("we return a dummy success entry") and claims the table builds "a high-quality dataset of true
negatives" — it cannot, because nothing reads it. There is no kill switch: `grep -rn -iE
'noise.*enabled' src/` returns nothing, and both policies are registered unconditionally.

**Why it outranks everything else:** this is the one path in a memory server that destroys an
agent's memory and tells it the opposite.

**Fix:** make refusal an outcome, not a lie. Add `Stored: bool` and `Reason: string?` to the
result type and the MCP `WriteResult`; say so in the tool description. Add a `noise.enabled`
settings key mirroring the existing `sweep.enabled` convention. Combined with B3's deletion, the
only remaining rejector is the deterministic Hermes policy, which can honestly name what it
matched.

### B2 — No tool returns a memory's content; search hands back a random 200-character window

**Severity:** BLOCKER · **Effort:** QUICK · **Surface:** `src/AiRaccoon/Tools/`, `SnippetFallback`
*Found by: RAG (F5); tool inventory verified by orchestrator (O13).*

The complete `[McpServerTool]` inventory is 25 tools. **None of them reads an entry.** There is
no `memory_get`, no `memory_read`. The only content an agent ever receives is `memory_search`'s
snippet, and for a vector hit — i.e. every semantic-search win — that snippet comes from
`SnippetFallback.From`:

```csharp
public const int WindowChars = 200;
var maxStart = value.Length - WindowChars;
var start = (int)(BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(hash)), 0)
                  % (uint)(maxStart + 1));
```

The window opens at an offset derived from the entry's hash — deterministic, and entirely
unrelated to the query. Mean entry length in the committed gate corpus is ~2,850 characters, so
the agent sees roughly **7% of the entry, chosen arbitrarily**. FTS-originated hits fare little
better: a 12-token `snippet()` with **empty** match delimiters, so the agent cannot even see
which terms matched. For a `memory_write` note there is no `source_file` to go read instead.

**Fix:** add `memory_get(projectId, hash)`. That is the whole fix for the read path and it is a
small tool. Separately make the vector-hit snippet query-relevant rather than hash-seeded — the
randomisation has no stated benefit and costs relevance on every semantic hit.

---

## The pattern behind the blockers

Three changes landed between 2026-08-12 and 2026-08-13 — ADR-0027, ADR-0029, ADR-0030 — and two
of them put heuristic gates on `memory_write` that can silently discard or silently expire the
agent's content. Each shipped with green tests and a benchmark. The measurements below show why
the tests and the benchmark could not catch what was wrong.

**The zero-shot noise filter does not work, and the number that justified it measures something
else.** [MEASURED, ML lane F3]

| Measurement | Result |
|---|---|
| Recall on the 50 noise strings ADR-0029 credits it with | **0/50** |
| Recall on 12 realistic tool-noise strings at the shipped `t=0.20` | **2/12** |
| …of which already caught free by the deterministic Hermes policy | 1 of the 2 |
| Cost added to every **valid** write | **~6.9 ms** (vs 0.014 ms for Hermes alone) |
| Rename `proc_98765` → `bash_7` | distance 0.155 → **0.425**, no longer rejected |
| Mutual distance between the three hardcoded "noise" anchors | 0.757 / 0.821 / 0.881 |

The three anchors are near-orthogonal, so "noise" is not one region of embedding space and no
single global threshold can cover it. ADR-0029's table attributes "100% rejection recall (50/50)"
and a "12.5× speedup" to the zero-shot policy — **all of those numbers belong to the deterministic
regex policy**, which is registered first and short-circuits. Its "< 1 ms overhead" claim is off
by about 7×. The shipped configuration makes every valid write ~60% slower than the entire
baseline write the ADR quotes, to buy a fuzzy exact-match on three strings.

**The auto-TTL policy arms the reaper on ordinary writes.** [MEASURED, ML lane F2/F6]

`PromotionScorer.cs:18-19` sets `MinWordsFloor = 8`, `MinWordsCap = 0.50`; any content under 8
words returns `min(prior, 0.50)`. `PromotionScorerTtlPolicy` marks anything scoring `< 0.6` as
transient with a 3-day TTL. **Every memory under 8 words therefore gets a deletion clock,
unconditionally** — verified constants, verified threshold. Measured: `"Push after every
commit."` → score 0.500 → `ttl=3`.

It is worse than word count. The policy passes `Path: request.SourceFile ?? "memory_write"`,
which defeats the classifier's own documented rule that a hex path means "organic write, not
document chunk". Measured on identical 55-word text, varying only `sourceFile`:

| `sourceFile` | score | ttl |
|---|---|---|
| *(none)* | 1.529 | none |
| `docs/adr/0025-the-sweep-reaper.md` | 1.025 | none |
| `docs/README.md` | 0.452 | **3 days** |
| `README.md` | 0.452 | **3 days** |

CLAUDE.md instructs agents to "write the finding back with `memory_write` including the source
path". **Following the project's own documented practice arms the reaper on the memory.**

**And the "3-day TTL" is fiction anyway.** [MEASURED, ML lane F4] `ShouldDegrade` ANDs
`ageDays > ttlDays` with `rating < threshold`; with base 0.5, a 30-day half-life and a 0.3
threshold the rating gate alone needs 22.1 days:

| nominal `ttl_days` | accessCount | actual first sweepable day |
|---|---|---|
| 3 | 0 | **22.2** |
| 3 | 1 | 26.3 |
| 3 | 10 | 52.2 |

Any TTL below ~22 days is dead text. An operator setting `memory_set_ttl(hash, 1)` gets three
weeks. Two subsystems disagree about the same memory and nothing reconciles them.

**This also voids ADR-0025's central safety argument.** That ADR rests on "an entry with no
per-entry TTL can never be a candidate… setting a TTL is itself Destructive-gated, so whoever did
that already had `full` access." ADR-0030 now sets TTLs automatically on the **ungated**
`memory_write` path. The reaper's "armed but inert" property is gone. Blast radius is limited to
`full`-mode projects — which is exactly the power-user configuration.

**Why the gates missed all of it.** The noise-filter tests use a `FakeEmbedder` that returns a
fixed vector **ignoring its input** — swap the "noise" and "clean" strings between the two tests
and both still pass. They assert cosine arithmetic, nothing about noise. The
`ZeroShotEmbeddingFilter` tests exercise a default threshold of `0.5` that is **not** the shipped
`0.20`; nothing anywhere tests the shipped value. And the write-performance benchmark asserts
only that 50 noise strings were intercepted — which the free regex does alone — while writing 50
*valid* notes and **discarding every return value**, so a filter that rejected 100% of all input
would pass it unchanged. Its report nonetheless prints `Rejection Accuracy: 100%`.

This is the same failure the project already diagnosed and fixed for the sibling promotion
classifier (`docs/work/2026-08-13-fixing-zero-shot-promotion-classifier.md`), which was correctly
deleted. The noise filter is the piece that survived the cull, and its own recorded open question
— "whether the zero-shot noise filter holds up to the same labelled-data scrutiny" — is now
answered: it does not.

---

## Findings by surface — the work packages

Packaged so that everything touching one surface lands in one change.

### WP1 · Make the write path honest — **BLOCKER**
*Surface: `SqliteMemoryStore.WriteAsync`, `MemoryTools.Write`, `WriteResult`, `MemoryEntryResult`*

| # | Finding | Sev | Effort |
|---|---|---|---|
| B1 | Rejected write returns fabricated success (`"noise_hash"`) | BLOCKER | SMALL |
| A-F2 | Auto-TTL silently attaches a 3-day expiry; not in the response or the description | HIGH | SMALL |
| — | No kill switch for either subsystem (`noise.enabled`, `ttl.auto.enabled`) | HIGH | QUICK |

Return `Stored`, `Reason`, `TtlDays`. Supersedes ADR-0029 §Decision 2; softens ADR-0030.

### WP2 · Delete the unearned judgement layers — **highest leverage**
*Surface: `Core/Memory/Filtering/`, `SqliteNoiseStore`, `MemorySchema` DDL*

| # | Finding | Sev | Effort |
|---|---|---|---|
| ML-F3 | Zero-shot noise policy: 0/50 recall on its own benchmark, ~7 ms per valid write | HIGH | QUICK |
| ML-F2/F6 | Auto-TTL fires on every <8-word write and on any write citing a README | HIGH | QUICK |
| ML-F9 / A-F4 / O10 | Noise-clustering subsystem: ~150 lines, unimplemented port, 2 tables, **2 test files**, never registered | MEDIUM | QUICK |
| ML-F8 | `noise_entries.expires_at` never purged — rejected writes accumulate forever | MEDIUM | QUICK |
| ML-F7 | Noise tests use a content-ignoring fake and an unshipped threshold | MEDIUM | QUICK |
| RAG-F16 | `ZeroShotEmbeddingFilter` hand-rolls cosine over already-normalised vectors | NIT | QUICK |

**~400 lines and two schema tables out; six of the ten uncalibrated thresholds gone.** Note the
dead clustering subsystem has *two dedicated test files* — green tests over unreachable code are
why it looked maintained. Delete them together.

### WP3 · Make retrieval return usable, honestly-scored results — **BLOCKER**
*Surface: search path, `SearchResultMerger`, `StructureFusion`, `MemorySearchResult`, tool surface*

| # | Finding | Sev | Effort |
|---|---|---|---|
| B2 | No read tool exists; snippet is a hash-seeded 200-char window | BLOCKER | QUICK |
| RAG-F1 | Dual-vector fusion caps heading-less rows at α×score — agent notes systematically outranked by ingested docs in mixed banks | BLOCKER | SMALL |
| RAG-F6 | `Ranking` is rank-position, not relevance: a degenerate second RRF pass over a one-element list discards the fused magnitudes; `min_score=0.7` is a rank cutoff | HIGH | SMALL |
| RAG-F7 | A promoted memory gets a different hash from its project original → `scope=all` returns both copies | MEDIUM | QUICK |
| RAG-F11 | `rating`/`access_count` are written on every search and read by **nothing that orders results** — the signal is trusted enough to delete but not to rank | MEDIUM | SMALL |
| RAG-F15 / UX-F6 | Dead `Seq` field and a redundant 64-hex `Path` on every hit | LOW | QUICK |

**RAG-F1 and RAG-F6 were both re-verified by the orchestrator at `path:line`.** F1's arithmetic:
`Fused` computes `alpha * contentSim + (1 - alpha) * (structureSim ?? 0.0)`, and rows without a
heading path have no structure vector — so a note at content-sim 0.95 scores 0.475 while a doc
chunk at content-sim 0.50 with structure-sim 0.90 scores 0.70 and wins. ADR-0004 claims this is
safe because "alpha scales every score identically" — true only for a *uniformly* heading-less
bank, not the mixed bank the product actually produces.

### WP4 · Give the retrieval gates a corpus that can fail
*Surface: `tests/.../Resources/jsaa-memory.db`, `RealWorldCorpus`, retrieval gate tests*

| # | Finding | Sev | Effort |
|---|---|---|---|
| RAG-F2 | **0 of 761 rows** in the gate corpus have a structure vector; `vec_structure` is empty. No gate exercises the structure modality at all | HIGH | MEDIUM |
| RAG-F8 | Corpus is ingested without `sourceFile`, so source-affinity ranking is structurally a no-op in the parity gate | MEDIUM | SMALL |
| RAG-F14 | nDCG floor was ratcheted down from ADR-0006's measured 0.722 to 0.674 with no recorded reason | LOW | QUICK |
| RAG-F9 | `GoldenFile_MatchesFreshReferenceRun` tests a vendored legacy extension, not AiRaccoon's search path — cannot fail on any retrieval change, and burns a Speed=Slow CI slot | MEDIUM | QUICK |

**This is the prerequisite for proving WP3 red.** Until it lands, every retrieval number this
project quotes describes a pipeline it does not ship.

### WP5 · Close the data-integrity gaps in the store
*Surface: `Sqlite/`, `WorkspaceService`, `PromotionQueueService`*

| # | Finding | Sev | Effort |
|---|---|---|---|
| DA-F2 | `DeleteCoreAsync` writes the entry-delete and its sync tombstone as two separate autocommits — a crash between them resurrects "deleted" content on next sync | HIGH | QUICK |
| DA-F1 | Workspace writes have no uniqueness guard: the partial indexes can't match `scope IS NULL`, and the value-dedup check is skipped for workspaces → retries silently duplicate | HIGH | SMALL |
| A-F7 | `CloseAsync` is an unguarded UPDATE with no `status='Active'` clause and a TOCTOU window; concurrent consolidate+discard both win | MEDIUM | SMALL |
| A-F11 | Promotion queue claims by **delete**, so a transient failure during `ShareAsync` destroys the candidate permanently | MEDIUM | SMALL |
| A-F6 | Access-mode resolution is duplicated **verbatim** between `MemoryAccessGuard` and `SweepHostedService` to satisfy a layering rule — and both copies gate destructive deletion | MEDIUM | SMALL |
| DA-F8 | `RememberDiscardsAsync` loops per-hash with no transaction (its sibling three lines away does it right) | MEDIUM | QUICK |
| DA-F10 | Dead SQL constant `EntryExistsByPathInBucket` | NIT | QUICK |

### WP6 · Stop paying per-row costs on the hot paths
*Surface: `BumpAccessAsync`, `ToolGate`, `FileIngestor`, `EntryEmbedder`, `EmbeddingBlob`*

| # | Finding | Sev | Effort |
|---|---|---|---|
| DA-F4 | **MEASURED:** no index supports the `memory_write` dedup check or the watch replace-by-path delete. Adding `idx_entries_project_committed` took 200 calls from 5.19→1.88 ms and 5.78→2.46 ms (**2.3–2.8×**) at only 20K rows, and the gap widens with bank size | HIGH | SMALL |
| DA-F5 / RAG-F10 / UX-F5 | `BumpAccessAsync` issues a SELECT + UPDATE **per result** on every search — up to 40 extra statements on a read, which also takes a write lock | MEDIUM | SMALL |
| UX-F5 | `ToolGate.WrapAsync` runs an unconditional promotion-queue query on **all 25 tools**, including `memory_delete` and `memory_stats` | MEDIUM | SMALL |
| DA-F3 | Ingestion does 3+ round trips per chunk with no batching and no transaction; the EXISTS pre-check duplicates the `ON CONFLICT DO NOTHING` already in the INSERT | HIGH | SMALL |
| DA-F6 | `PRAGMA synchronous` is never set, so it defaults to FULL — every un-batched autocommit above pays a full fsync | MEDIUM | QUICK |
| A-F3 / RAG-F13 / ML-F11 | `EmbedContentAsync` bypasses the bank's configured engine, opening a **second 23 MB ONNX session** over the same file; an `openai`-configured bank still loads the local model | HIGH | QUICK |
| .NET-F4 | O(n²) `Skip`/`Take` slicing in the bulk re-embed loop | MEDIUM | QUICK |
| .NET-F5 | `EmbeddingBlob` serialises float-by-float instead of a block reinterpret cast | LOW | QUICK |

`RAG-F10` measured `memory_search` at **94 ms p50 / 306 ms p95** on a 174-document corpus —
dominated by fixed overhead, not the vector scan. The budget is spent before the corpus grows.

### WP7 · Fix chunking so content reaches the embedding
*Surface: `MarkdownChunker`, `JsonFileTypeChunker`, `O200kTokenizer`*

| # | Finding | Sev | Effort |
|---|---|---|---|
| RAG-F3 | **MEASURED: 37.5% of chunks (1331/3552) exceed the model's 256-token window and are silently truncated.** The budget counts `o200k` tokens; the model tokenizes BERT WordPiece (measured ratio p95 **1.217**). No log, no counter — the chunk still reads `embed_state='embedded'` | HIGH | SMALL |
| RAG-F4 | **MEASURED:** one unbalanced code fence makes the rest of a document a single atomic chunk — 5621 tokens, of which **95% never reaches the embedding**. A stray ` ``` ` in an agent note removes the document from semantic search | HIGH | QUICK |
| RAG-F12 | JSON chunks carry no key-path context; a minified JSON file degrades to one unbounded line chunk. ADR-0027 promises "key-aware structural chunks" | MEDIUM | MEDIUM |

### WP8 · Structural decomposition and the growing god class
*Surface: `IMemoryStore`, `SqliteMemoryStore`, `Core/Memory/`*

| # | Finding | Sev | Effort |
|---|---|---|---|
| A-F5 | `IMemoryStore` is a **25-member god port with 25 dependent files**, conflating the memory bank with a global settings key-value store (111 settings call sites). Every test fake must stub 25 methods | HIGH | MEDIUM |
| DA-F9 / A-F17 | `SqliteMemoryStore` is 1250 lines doing six orthogonal jobs; ~150 lines of it are pure domain rules that belong in Core | MEDIUM | MEDIUM |
| **O3** | **Growing debt, MEASURED:** the file went 1111 → 1250 lines (**+12.5%**) in eight days while its decomposition work item (WI-8a/8d, open since 2026-08-07) stayed open | MEDIUM | — |
| A-F9 | `SqliteMemoryStore` downcasts an injected `IMemorySourceStore` to its concrete type — any alternative implementation throws at runtime | MEDIUM | QUICK |
| A-F10 / A-F16 | `Core/Memory/` is a 41-file grab bag holding five distinct concepts; Infrastructure separates them and Core does not. `Setup/` is a technical bucket holding the CLI | MEDIUM | SMALL |
| A-F12 / O12 | `ExtractCommands` registered twice verbatim; `EncryptionCommands` registered twice (the plain one unreachable); `AddRequiredSingleton` silently publishes every concrete type | LOW | QUICK |
| A-F14 | `Core/Resilience` matches an Infrastructure exception **by type-name string** to dodge the layering rule; a rename silently disables the retry | LOW | QUICK |
| A-F8 | Adding a sync backend touches six files and an unknown provider silently falls through to S3; adding a file type touches one (ADR-0027's seam is genuinely good) | MEDIUM | SMALL |

### WP9 · Silent failure and lifecycle correctness
*Surface: `WatchDigestExecutor`, `SqliteConnectionFactory`, `AppRunner`, `MemoryTools`*

| # | Finding | Sev | Effort |
|---|---|---|---|
| .NET-F1 | Watch-triggered embedding failures are caught and dropped with **no retry path**; the row stays `pending` forever, FTS-searchable but invisible to semantic ranking, with no proactive signal | HIGH | SMALL |
| .NET-F2 | Bitwarden key resolution shells out to the `bws` CLI **synchronously on every bank open** — i.e. every memory read and write — blocking a pool thread for up to 15 s | HIGH | MEDIUM |
| .NET-F3 | `AppRunner`'s `CancellationTokenSource` is never cancelled for the CLI and proxy paths; no SIGINT/SIGTERM handler exists, so the threaded token is a false promise | MEDIUM | SMALL |
| .NET-F6 | One direct `logger.LogWarning` call on the `memory_search` hot path, against 40+ correct `[LoggerMessage]` sites | LOW | QUICK |

### WP10 · Gates that cannot fail
*Surface: test fixtures and gate assertions*

| # | Finding | Sev | Effort |
|---|---|---|---|
| **O2** | **6 tests are skipped**, including `PromotionScoringRealDataTests.ScoresCorrelateWithHandLabeledUsefulness` — the scorer's only real-data correlation check — and `All 17 tools are still listed`, a contract test on a tool count that is now 25 | HIGH | SMALL |
| ML-F5 | The promotion scorer's running gates derive their own thresholds from the code's output at test time (`minSpearman = measured + 0.10`). Zeroing every prior leaves all six green. ADR-0018's headline Spearman figures are unverifiable on `main` by anyone | HIGH | SMALL |
| **O7** | The write-performance benchmark writes 50 valid notes and **discards every return value** — a filter rejecting 100% of input passes unchanged — while reporting "Rejection Accuracy: 100%" | HIGH | QUICK |
| **O8** | That test writes into tracked `docs/` on every run (observed dirtying a concurrent session's worktree), and publishes `Allocated Memory per Write: -549,67 KB` — a negative allocation, because `GC.GetAllocatedBytesForCurrentThread()` is read across `await` boundaries | MEDIUM | QUICK |
| ML-F13 | Same test carries `Speed=Slow`, so CI runs it on every PR | MEDIUM | SMALL |
| ML-F12 | `PromotionScoringRealDataTests`' documented prior table is stale since the round-3 refit and its archetype ordering is **no longer monotone** (Plan 0.755 > Explanation 0.615 > Reference 0.475 > Adr 0.425) | MEDIUM | QUICK |

### WP10b · Close the test gaps that guard data loss
*Surface: `LikePattern`, `DeleteSourcePathAsync`/`ReplaceFileAsync`, `NoiseFilteringService` tests*

| # | Finding | Sev | Effort |
|---|---|---|---|
| QA-F1 | **`LikePattern` has zero test references in the entire suite** (verified: `grep -rn "LikePattern" tests/` → 0). It escapes `\`, `%`, `_` for the `LIKE … ESCAPE` cascade delete — i.e. it is the only thing standing between a path containing `_` and the exact cross-content data-loss defect this repo has already shipped once | HIGH | QUICK |
| QA-F2 | The `catch { ROLLBACK; throw; }` branch in `DeleteSourcePathAsync` and `ReplaceFileAsync` is **never exercised**. The transaction is the only thing preventing a half-deleted bank, and it is untested | HIGH | SMALL |
| QA-F3 | `NoiseFilteringService` — the orchestrator actually wired into the write path — has exactly one test, for the reject case. Nothing proves a legitimate write returns `false`, that first-match short-circuits, or that the null-store branch works | MEDIUM | QUICK |
| QA-F9 | Two tests promise specific behaviour but assert only `ShouldNotBeNull()`: `CreateGenerator_UsesSettingsModelPath` never proves the custom path was used; `BothTransports_CreateWebHostWithStdio` never proves both transports registered | LOW | QUICK |

**QA-F1 and QA-F2 are the same method and the same half-day.** Per the QA lane's verdict, this
is the suite's highest-leverage change: it converts "we assume the escaping and the transaction
work" into "we have watched them fail and recover."

### The test suite is *not* the problem — a disconfirmed hypothesis

The review opened suspecting the 2.5:1 ratio (56,108 test lines vs 22,400 production) meant
bloat. **The QA lane measured it and the hypothesis is wrong**, which is worth recording as
plainly as the defects:

- A scan of all **1,958** `[Fact]`/`[Theory]` methods outside `E2E/` found **zero confirmed
  assertion-free tests**. The lane's first pass flagged ~90-102 candidates and it traced every
  one back to a false negative in its own regex (generic `Should.Throw<T>()` calls) or a
  legitimate implicit-non-throw test — and said so rather than reporting the inflated number.
- **No `Skip` attributes exist outside `E2E/` at all.** (The 6 skips the orchestrator measured
  are all E2E or env-gated.)
- The two largest files earn their size: `SyncServiceTests.cs` is 1,463 lines of *distinct*
  merge/tombstone/schema-version conflict scenarios and runs in **2 seconds**;
  `CliArgsTests.cs` is a real flag matrix.
- `DeleteSourcePathAsync`'s non-transactional properties are covered *deeply* — five tests
  pinning cross-project isolation, workspace-scratch survival, watch-fingerprint cascade, and
  the subtle path-vs-`source_file` manual-row distinction.

Total deletable test code the lane could actually justify: **~180-220 lines** — F7's six
duplicated `ConfigCommands` harnesses, F6's one duplicated access-mode guarantee, and (pending a
scenario diff it declined to shortcut) F12's four watch happy-paths. That is noise against 56k.
**The size is earned; the gaps are the problem.** Do not scope a test-shrinking effort.

### WP10c · Test-suite hygiene (low priority, mostly self-resolving)

| # | Finding | Sev | Effort |
|---|---|---|---|
| QA-F4 | **MEASURED:** `NodeRunnerTests` is the slowest non-E2E class — **116s for 14 tests** (~8.3s each), driven by two hard `Task.Delay(2s)` waits and real `node` child processes. For contrast, 24 real-SQLite `SyncServiceTests` run in 2s | MEDIUM | SMALL |
| QA-F8 | 56 real wall-clock `Thread.Sleep`/`Task.Delay` sites outside E2E; `IdleWatchdogTests` alone burns 1.3s of pure sleep. 69 other files *do* inject `FakeTimeProvider` correctly, so the pattern is available | MEDIUM | MEDIUM |
| QA-F5 | 9 files use raw `[Trait("Speed","Fast")]` with **no `Category` trait**, so `--filter Category=Unit` silently skips them | LOW | QUICK |
| QA-F7 | Six `ConfigCommands*Tests.cs` files hand-roll a near-identical `Run()` harness | LOW | SMALL |
| QA-F6 / QA-F12 | One access-mode guarantee pinned at both BDD and Unit layers; four watch happy-paths pinned at both BDD and Integration | LOW | SMALL |

**Two reconciliations the orchestrator verified:**

1. QA-F5 does **not** contradict O9. CI filters on `Speed=Fast|Slow` and `Category=bdd`, and the
   nine files all carry a `Speed` trait — so CI runs them and the partition really is exact
   (1658+142+483 = 2283). The gap is only for a developer filtering on `Category=Unit` locally.
2. **8 of those 9 files are the noise-filter and zero-shot tests.** WP2 deletes them, so QA-F5
   shrinks to a one-file fix once WP2 lands. Sequence WP2 first and do not spend a sweep on it.

### WP11 · CLI and agent-surface polish
*Surface: `Setup/Cli/`, tool descriptions, docs*

| # | Finding | Sev | Effort |
|---|---|---|---|
| UX-F3 | ~20 validation sites `return 1`, colliding with the named `ExitCode.FailedToResolveEncryptionKey = 1` — a script cannot tell a typo from a broken bank key | HIGH | SMALL |
| UX-F10 | `serve` silently attaches to whatever server owns the port, **ignoring a different `--data-root`** with no warning | MEDIUM | SMALL |
| UX-F4 | A missing-argument error is printed **three times** | MEDIUM | SMALL |
| UX-F8 | Default (non-quiet) runs double-print failures with the internal class name and numeric event id | MEDIUM | SMALL |
| UX-F7 | The single global catch-all leaks raw OS errno text (`Read-only file system : '/nonexistent'`) with no guidance | MEDIUM | SMALL |
| UX-F9 | No `--json` mode anywhere in the CLI | MEDIUM | MEDIUM |
| UX-F2 | The "complete tool contract" reference lists 23 of 25 tools; `memory_record_grade` and `memory_record_followthrough` appear nowhere | HIGH | QUICK |
| UX-F13 | Merge candidates: `record_grade`+`record_followthrough`, `ingest_file`+`ingest_directory` (dispatch on `Directory.Exists` server-side) | LOW | SMALL |
| UX-F11 | "Revert to default" is spelled `reset` / `unset` / `remove` across sibling groups; "show config" is `list` / `show` | LOW | SMALL |
| UX-F14 | CLI `extract` vs tool `memory_share_extract` name the same feature differently | LOW | QUICK |
| UX-F15 | The onboarding tutorial has an invalid mermaid block (`}` instead of `end`) and never shows or verifies a memory write | LOW | QUICK |
| UX-F17 | `--help`/`--version` go to **stderr**, so `ai-raccoon --help \| grep` returns nothing. Plausibly deliberate (protects the stdio channel) but unstated | LOW | QUICK |
| A-F13 | ADR-0027 names three interfaces, one of which (`IFileTypeChunker`) does not exist; ADR-0029's benchmark is mislabelled | LOW | QUICK |
| O14 | CLAUDE.md writes `project_id`; the schema is `projectId`. **Downgraded from the UX lane's HIGH to LOW** — agents bind to the JSON schema at call time, not to prose, and this session's own calls succeeded | LOW | QUICK |

---

## What is healthy — do not "improve" these away

Recorded so a simplification pass does not sweep them up.

- **Core layer purity.** No project references, no framework/persistence/SDK leakage, zero
  dependency cycles, zero captive dependencies. Only `Core/Resilience`'s stray `Polly` reference
  (A-F14) breaks it.
- **The migration ladder.** Versioned, ordered, guarded steps v1–v6 with a forward-version write
  guard that throws on a newer bank — exactly what ADR-0019 promised, and correctly implemented,
  with a deliberate soft/hard distinction per step.
- **vec0 partitioning.** KNN is partitioned by `ctx`, so search cost scales with the project's
  rows, not the whole install. Brute-force within a partition is the right call at this scale.
- **Embedding symmetry.** Index-time and query-time preprocessing are identical; vectors are L2-
  normalised and the distance conversion assumes exactly that. MiniLM needs no query/passage
  prefix, and none is used. This clears the most common silent RAG defect and means WP3/WP7's
  recall losses are correctly attributed to fusion and chunking.
- **The promotion scorer itself.** Fitted rather than guessed, ported in lockstep with a Python
  prototype, honest about its weak channels, and hand-verifiable to three decimals. It is the one
  component here that earns its complexity — which is why WP10's fixture gap matters.
- **The hosting/proxy layer.** ADR-0020 documents four measured correctness defects that
  multi-process fan-out caused, plus a 484–620 MB → ~193 MB memory result. The complexity is repaid.
- **The file-type handler seam.** ADR-0027's extensibility claim holds: the extension map is
  derived from the injected handler collection with no hardcoded list.
- **Bank maintenance.** `VACUUM`, `ANALYZE` (correctly ordered after), and `wal_checkpoint(TRUNCATE)`
  all exist in a dedicated hosted service.
- **CI trait partition and SHA-pinned Actions** (O9).
- **MCP error prefixes.** `invalid-argument:`, `access-denied:` — parseable, consistent, worth
  codifying.

---

## Still open

- **Whether the shipped scorer achieves ADR-0018's Spearman figures.** The fixture is
  deliberately uncommitted and the env var is unset; verifying needs the private labelled pool.
- **The noise filter's false-positive rate on real traffic.** The `noise_entries` table would
  answer it directly — if anything could read it.
- **Whether any live project is in `full` access mode**, which decides whether the auto-TTL is
  currently deleting or merely armed.
- **The ANN cliff.** RAG-F10 extrapolated from a 174-doc measurement; the knee was not measured.
- **Real-world chunk-size distribution.** The 37.5% truncation figure is measured on this repo's
  docs; a bank of short notes would be far below it.
- **Test-suite blast radius of WP8.** At 2.5:1, splitting `IMemoryStore` touches far more test
  code than production code. Not sized.
