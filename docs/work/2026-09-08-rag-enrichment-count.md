# Research: RAG enrichment entry count

**Date:** 2026-09-08
**Question:** How many entries does a RAG enrichment return, and why does the card show only 3?

```chart:bars
title: entries at each stage of RAG enrichment
server default limit: 8
extension requests: 5
memories kept: 3
code kept: 2
```

## Findings

### F1 — The ai-raccoon server defaults to 8 memory hits per search [READ]

The default lives in one place (`SearchDefaults`) and the MCP tool binds to it with no literal duplication, so there is nothing else to keep in sync. The floor alongside it is 0.6 of the response's top hit. A caller that passes no `limit` gets up to 8 memory results (plus a separate code section when `kind=both`).

**Evidence:** `src/AiRaccoon.Core/Memory/SearchDefaults.cs:13-14` (`Limit = 8`, `MinRelativeScore = 0.6`); `src/AiRaccoon/Tools/MemoryTools.cs:139-140` (`Maximum results (default 8)`, `int limit = SearchDefaults.Limit`).

### F2 — The mem-based-rag extension asks the server for 5 hits [READ]

The `before_agent_start` handler calls `memory_search` with an explicit `limit: 5`, overriding the server default of 8. This is the raw candidate pool before any client-side pruning — at most 5 memory rows (plus the code section, which has no per-call override here so it uses its own default path).

**Evidence:** `/Users/arasz/RiderProjects/pi-badger-integration/extensions/mem-based-rag/index.ts:445` (`{ projectId, sessionId, query: decision.query, limit: 5 }`).

### F3 — The extension keeps 3 memories and 2 code hits, so the card shows 3 memory bullets [MEASURED]

Feeding 5 memory + 3 code hits into the default formatter yields a block with exactly `[m1]–[m3]` and `[c1]–[c2]`; the 4th/5th memory and 3rd code hits are dropped. The card renderer (`toCardLines`) is display-only — one `•` bullet per block hit, no further truncation — so the "only 3" the card shows is the block's 3, not a card bug. All three runs agreed: 3 mem, 2 code.

**Evidence:** `bun -e` script importing `toMemoryContext`/`toCardLines` from `extensions/mem-based-rag/rag-core.ts`, run three consecutive times 2026-09-08 on this machine (Darwin arm64, bun v1.4.2): run 1 full block + card output (3 `[mN]`, 2 `[cN]`), runs 2–3 count-only re-runs both `3 2`. Existing suite `bun test tests/mem-based-rag/rag-core.test.ts tests/mem-based-rag/wiring.test.ts` same machine: 70 pass, 0 fail, including `keeps top 3 memories and top 2 code hits`.

### F4 — The 3+2 cap is enforced twice: wiring slice and formatter default [READ]

The wiring prunes (drop `? ::` empties, dedupe by hash/snippet) then slices `mem.slice(0, 3)` / `code.slice(0, 2)` before the both-empty check. The formatter re-applies the same cap (`maxMem ?? 3`, `maxCode ?? 2`), so even a direct `toMemoryContext` call with 5+5 inputs renders 3+2. Raising only the request `limit` without raising these caps changes nothing visible.

**Evidence:** `/Users/arasz/RiderProjects/pi-badger-integration/extensions/mem-based-rag/index.ts:452-453` (`pruned.mem.slice(0, 3)`, `pruned.code.slice(0, 2)`); `/Users/arasz/RiderProjects/pi-badger-integration/extensions/mem-based-rag/rag-core.ts:395-396` (`slice(0, opts?.maxMem ?? 3)`, `slice(0, opts?.maxCode ?? 2)`); pinned by `/Users/arasz/RiderProjects/pi-badger-integration/tests/mem-based-rag/rag-core.test.ts:132-139`.

### F5 — The card showing only 3 is the designed two-stage cap, not data loss [INFERRED]

Reasoning from F2–F4: request 5 → prune/dedupe → keep 3+2. The 5-request exists to give the dedupe/prune step headroom (a `? ::` empty or duplicate burns a slot without burning a visible bullet). The memories header in the observed card says nothing about truncation beyond `(snippets truncated to 300 chars; hashes identify the full entries)` — it truncates snippet text, not hit count — so a reader counting bullets sees 3 with no on-card statement that 2 more were considered and dropped.

### F6 — Expanded mode still shows one line per kept hit, with full values behind it [UNVERIFIED]

Expanded mode (`PI_BADGER_MEM_RAG_MODE=expanded`) fans out `memory_get`/`code_get` per kept hit, but the per-hit fallback is the snippet and the pruned set is the same 3+2 — not re-checked here. What would settle it: one `/rag mode expanded` turn against a bank with 5+ retrievable hits, comparing the injected block's `[mN]`/`[cN]` count to default mode.

## Still open

- Should the request limit rise (e.g. 5 → 8 to match the server default) now that prune-then-slice headroom is the binding constraint, or is 5→3 the intended funnel?
- The card never states "showing 3 of 5 considered" — is that worth one clause in the block footer, or does it just cost prompt tokens?
- What score floor (cosine / minRelativeScore / fusionStrength) marks "thin, skip injection" for a production-size bank? The 2026-09-06 RAG study left this open and this investigation did not sweep it.
