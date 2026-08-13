# AiRaccoon 1.10.1 Live Manual Testing Checklist Report

**Executed**: 2026-08-13 20:46:49 UTC
**Version**: `1.10.2+d6a256e26a30883fae4c53df6a0fb00cee2386d5`
**Checklist State Artifact**: `.ai-raccoon/state-checklist-1.10.1-1786654009.json`
**Summary**: 11/12 Passed

---

## Evaluation Results

| Item | Status | Expected Result | Observed Result / Details |
| :--- | :---: | :--- | :--- |
| `version-verification-1-10-1` | ✅ **ACCEPTED** | ai-raccoon --version reports 1.10.x+commit_sha; dotnet tool update from the 1.10.x package | Tool version output: 1.10.2+d6a256e26a30883fae4c53df6a0fb00cee2386d5 |
| `promotion-model-toggle` | ✅ **ACCEPTED** | ai-raccoon promotion model enable persists promotion.model.enabled=true; promotion model s | promotion model show: promotion model: enabled |
| `auto-extraction-enabled` | ✅ **ACCEPTED** | ai-raccoon extract enable true enables the background shared-extraction service; extract l | extract list: enabled: True  mode: propose  interval: 30 min  queue-capacity: 1000 |
| `noise-filtering-hermes-process-interception` | ✅ **ACCEPTED** | HermesProcessNoisePolicy intercepts process output, routes to trash/noise store, keeps mai | Write response: {'hash': 'noise_hash', 'path': 'noise_path', 'context': '', 'createdAt': 1786654005}, Search h |
| `noise-filtering-zero-shot-semantic-filter` | ❌ **REJECTED** | ZeroShotEmbeddingNoisePolicy evaluates content against bundled noise vectors and flags sem | Write response: {'hash': '4bb52365fb5274b514c4b189a12ed54a0c1127c31c2fa208ca329a5557aaa01f', 'path': '678827b7 |
| `noise-filtering-clean-domain-write` | ✅ **ACCEPTED** | High-value ADR passes clean, lands in main memory bank with ranking 1 on memory_search | Hash: cbba13a7f02d7c944c2879513b65b545530fa6f16bbab9245561d3758b904488, Search hits: 2, Rank 1 match: True |
| `promotion-quality-high-value-ranks` | ✅ **ACCEPTED** | memory_share_extract (propose) ranks the high-value ADR as a promotion candidate with scor | Propose candidates: 1; high-value queued: True |
| `promotion-quality-promote-shares` | ✅ **ACCEPTED** | memory_share_extract (promote) shares the top queued candidate into the shared tier | Promoted hashes: ['cbba13a7f02d7c944c2879513b65b545530fa6f16bbab9245561d3758b904488'] |
| `mcp-full-tool-surface-25-tools` | ✅ **ACCEPTED** | All 25 MCP tools respond without unhandled exceptions | 25 tools listed; 0 failed; missing=[] |
| `mcp-prompts-retrieval` | ✅ **ACCEPTED** | prompts/get returns memory-usage-guide and workspace-consolidation-guide cleanly | memory-usage-guide ok: True, workspace-consolidation-guide ok: True |
| `observability-logger-messages-and-engine` | ✅ **ACCEPTED** | Structured [LoggerMessage] events and sqlite3mc engine version are emitted in stderr | Stderr 247 lines; logger events: True; engine: True |
| `server-restart-and-http-probe` | ✅ **ACCEPTED** | ai-raccoon serve --restart listens on port 7721 and HTTP probe responds with Bearer auth | Server bound on 7721; HTTP initialize response: {"result": {"protocolVersion": "2025-06-18", "capabilities": { |

---

## Conclusion

AiRaccoon **1.10.1** live manual testing: **11/12 checklist items ACCEPTED**.
