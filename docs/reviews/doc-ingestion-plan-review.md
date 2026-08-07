# Implementation Plan Review: doc-ingestion-implementation-plan.md

**Reviewer:** Hermes subagent  
**Date:** 2026-08-04  
**Reviewed against:** Design doc (`job-search-ai-assistant-ingestion-pipeline.md`, held by the `job-search-ai-assistant` project; removed from this repo 2026-08-07), AiRaccoon actual API surface (`src/AiRaccoon/Tools/MemoryTools.cs`, `SqliteMemoryStore.cs`, core types)

---

## Verdict: NOT READY — requires 3 blocking fixes before implementation

The plan correctly identifies several API gaps but has one **critical flaw** and several significant weaknesses that would cause the test runner to produce false results and the quality gates to pass without real evidence of retrieval quality.

---

## BLOCKERS (must fix before implementation)

### B1. `isExpectedSource` detection is broken — will silently produce false negatives

**Source:** Plan §D1, §A1, §A4; AiRaccoon `MemoryTools.Write()` (line 50), `MemorySearchResult` (Hash, Seq, Ranking, Path, Snippet)

The plan's test runner checks `isExpectedSource` by matching `result.path` against `expectedSource` (e.g., `"docs:adr/0011-frontend-chassis-stack.md#decision"`). This will **never match** because:

1. `memory_write` has **no `path` parameter** (confirmed: only `projectId, content, workspaceId?, agentId?, context?`)
2. AiRaccoon auto-generates the `Path` column via `SHA256(content).hex + ".md"` (line 811-812 of `SqliteMemoryStore.cs`)
3. The search result's `Path` field is the SHA256 hash, not the structured path

The plan acknowledges the missing `path` parameter in §A1 and proposes two workarounds:
- (a) Embed `## Source: <path>` at top of content — **valid for retrieval relevance** but does NOT affect `result.path`
- (b) Pass structured path as `agentId` — **stored in DB** but `MemorySearchResult` does NOT return `agentId`

**Fix options (pick one):**
1. **Pre-compute SHA256 hashes** — For each chunk, compute `SHA256(chunk.content).hex + ".md"` and build a lookup table. The test runner matches `result.Hash` against expected hashes instead of matching `result.Path`.
2. **Use `memory_ingest_file`** — Write each pre-chunked piece as a temp file with a descriptive name, then call `memory_ingest_file(projectId, path, context)`. AiRaccoon's `IngestFileAsync` stores the **real file path** in the `path` column (line 324 → `InsertChunksAsync` with real path). But this gives up the plan's custom chunking strategies (AiRaccoon uses `MarkdownChunker`, not type-aware strategies).
3. **Check snippet content** — Instead of matching `result.Path`, grep `result.Snippet` for the expected structured path string. This is fragile (snippet is a ~200-char window, may not include the header).

**Recommended:** Option 1 (pre-compute hashes). It's deterministic, testable, and doesn't require AiRaccoon changes.

### B2. Quality gate D1 is too weak to catch the `isExpectedSource` bug

**Source:** Plan §D1 quality gate

```bash
# At least 20 of 35 queries have results (non-empty results arrays)
```

This gate would pass even if every result is wrong — it only checks that results exist, not that they're correct. Combined with B1, the entire test suite could pass while returning zero expected-source hits.

**Fix:** Replace with:
```bash
# At least X of 35 queries find their expected source at rank ≤ 3
# X depends on embedding quality but should be ≥ 25 initially
```

### B3. Idempotency section (E2) assumes `memory_delete_context` works in rw mode

**Source:** Plan §E2; AiRaccoon access modes (`AccessRequirement.Destructive`)

`memory_delete_context` requires `AccessRequirement.Destructive` (line 171 of `MemoryTools.cs`). If the project is in default `rw` mode (read+write), destructive operations are denied. The plan doesn't account for this. Either:
- Document that the project must be in `full` mode for re-ingestion
- Or add a setup step to switch to `full` mode before `--reset`

---

## HIGH-SEVERITY (should fix, won't block if documented)

### H1. `isExpectedSource` field in baseline JSON schema is misleading

**Source:** Plan §D1, design §4.4

The baseline schema stores `isExpectedSource` per result. But with the SHA256 path issue (B1), this flag will always be `false`. Even after fixing B1, the field name is misleading — it should be `isExpectedHash` or the matching logic should be renamed.

### H2. Plan uses stale design doc assumptions about `memory_embed_pending`

**Source:** Plan §A4; design §3.2; AiRaccoon `EmbedPending()` line 267-278

Design says `limit=0` means "all". AiRaccoon's actual API: `limit` is `int?` — omit/null means "all", not 0. The plan correctly notes this in §A1 (§A4 uses "omit limit to process all") but §A4's pseudocode still has `memory_embed_pending(projectId="job-search-ai-assistant")` (correct). The design doc's table in §2.1 is wrong — `limit=0` is not how it works.

### H3. Section B query count confusion is unresolved

**Source:** Plan §B1, design §8 summary vs §4.2 tables

- Design §8 says "43 test queries"
- Design §4.2 tables: A=7, B=6, C=6, D=4, E=3, F=3, G=3, H=3 = **35 total**
- Plan §B1 quality gate hardcodes `assert len(qs) == 35` but shows uncertainty in comments

The plan's own "Issues to File" section (#3) notes this as unresolved but then proceeds to hardcode 35 in the quality gate. This must be resolved before Section B implementation — either use 35 (tables are authoritative) or find the missing 8 queries.

**Verdict:** 35 is correct (tables are the source of truth). Remove the "43" number from the plan overview (§1) and design summary (§8), or add a note explaining the discrepancy.

---

## MEDIUM-SEVERITY

### M1. Chunking doesn't handle edge cases

**Source:** Plan §A3

Missing handling for:
- **Empty files** — Should produce 0 chunks (no error), not crash
- **Frontmatter-only files** — Files with only YAML frontmatter and no H2 sections (some `docs/explanation/` files). Heading chunker would produce 0 chunks.
- **Files with only H1** — No H2 sections. Heading chunker needs a fallback: emit the whole file as one chunk.
- **Binary files** — Not expected in this pipeline (all `.md` or `.json`), but no defensive check

**Fix:** Add in A3 acceptance criteria: "Empty and frontmatter-only files produce 0 chunks with a warning log, not an error"

### M2. Batch size mismatch with internal AiRaccoon batch size

**Source:** Plan §A4; `SqliteMemoryStore.EmbedBatchSize = 32` (line 32)

Plan uses batch=50. AiRaccoon internally processes embeddings in batches of 32. The plan's batch size of 50 is reasonable for controlling the pending queue but doesn't align. Consider using 32 or 64 for alignment. Non-blocking.

### M3. No integration test with real AiRaccoon server

**Source:** Plan quality gates

All quality gates use command-line assertions. No test actually verifies that the Python MCP client connects to AiRaccoon, configures embeddings, writes, embeds, and searches successfully. The "spot-checks" in A5 are manual verification, not automated.

**Fix:** Add a smoke test in Section D/E that actually calls `memory_configure` + `memory_write` + `memory_search` through the MCP client and asserts the round-trip.

### M4. HTML form has no automated test

**Source:** Plan §C1-C4

All HTML form quality gates are manual ("Open in Safari", "verify manually"). The form is self-contained HTML — it could have automated tests via Playwright or a simple headless check that it parses baseline JSON correctly.

---

## LOW-SEVERITY

### L1. MCP transport: stdio is fine for this use case

**Source:** Plan §A1; task question #3

The plan uses stdio transport. For a Python ingestion script spawning AiRaccoon as a subprocess, stdio is the simplest and most reliable choice. HTTP/SSE would add complexity (need to start the server separately, manage ports). stdio is correct.

### L2. `memory_search` has no `context` filter — plan correctly defers

**Source:** Plan §A1 gap analysis, §D1; AiRaccoon `Search()` line 67-103

Confirmed: `memory_search` has no `context` parameter. The plan correctly defers cross-cutting G1-G3 queries. The `scope` parameter (all/project/shared) is available and used. This is correctly handled.

### L3. Risk register missing: `isExpectedSource` detection failure

**Source:** Plan §Risk Register

The risk register lists "`memory_write` lacks `path` parameter" as **Low impact**. It's actually **High impact** for the test runner because it breaks the primary quality signal. The mitigation is only partial (embed in content, use agentId) and doesn't address the test runner's matching logic.

### L4. No handling for AiRaccoon server not running

**Source:** Plan §D1, §E, §F

No test or error handling for the case where AiRaccoon is not running when the scripts execute. The MCP client should handle connection failure with a clear error message.

### L5. Plan overview says "43 baseline queries" but should say "35"

**Source:** Plan §Overview line 11

Says "run 43 baseline queries" — should be 35 to match the actual query tables. Update when resolving H3.

---

## DESIGN DOC INACCURACIES (propagated into plan)

These are upstream issues in the design doc that the plan inherits. File issues against the design:

| Design Section | Inaccuracy | Actual |
|---|---|---|
| §2.1, memory_write table | Lists `path` as a parameter | No `path` param; path is auto-generated from content hash |
| §2.1, memory_search table | Lists `context` as a parameter | No `context` filter; only `scope` (all/project/shared) |
| §3.2, Phase 0 | `memory_embed_pending(limit=0)` means "all" | `limit=null/omit` means "all"; `limit=0` would process zero |
| §8, Summary | "43 test queries" | Tables show 35 (7+6+6+4+3+3+3+3) |
| §4.2, cross-cutting queries | Implies context-scoped search | Context filter doesn't exist on search; correctly deferred by plan |

---

## QUALITY GATE STRENGTH ASSESSMENT

| Gate | Section | Current Strength | Issue |
|---|---|---|---|
| `--dry-run` shows 150-180 files | A2 | ✅ Adequate | |
| `--chunk-only` shows ~770 chunks | A3 | ⚠️ Weak | Counts chunks but doesn't verify chunk quality (self-contained, metadata present) |
| `--ingest-only` → stats near 770 | A4 | ⚠️ Weak | Doesn't verify entries are searchable, only that they exist |
| Spot-checks: 2/4 at rank #1 | A5 | ❌ Too weak | "At least 2 of 4" is a 50% pass rate on a tiny sample |
| JSON validation: 35 queries | B1 | ✅ Adequate | |
| HTML form: manual QA | C1-C4 | ❌ No automation | All manual; no automated test |
| Test runner: 35 results, exit 0 | D1 | ❌ Broken (B1) | `isExpectedSource` will always be false |
| Stats assertions ±5% | E1 | ✅ Adequate | |
| Re-ingestion idempotency | E2 | ⚠️ Weak | Doesn't account for access mode requirement |
| E2E gate | F1 | ⚠️ Weak | Manual scoring step; no automated pass/fail |

---

## RECOMMENDED FIXES SUMMARY

1. **[BLOCKER]** Fix `isExpectedSource` detection (B1) — pre-compute SHA256 hashes or switch to `memory_ingest_file` approach
2. **[BLOCKER]** Strengthen quality gate D1 to check source-match rate, not just result existence (B2)  
3. **[BLOCKER]** Document or handle access mode for re-ingestion (B3)
4. **[HIGH]** Resolve query count discrepancy: use 35 throughout (H3)
5. **[HIGH]** Fix design doc inaccuracies in the design doc itself (upstream)
6. **[MEDIUM]** Add edge-case handling for chunking (M1)
7. **[MEDIUM]** Add integration smoke test with real AiRaccoon (M3)
8. **[LOW]** Add connection-failure handling (L4)
9. **[LOW]** Fix plan overview to say "35" not "43" (L5)
