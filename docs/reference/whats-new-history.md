# What's new — history

Older release highlights, archived from the README. Newest at the top.

For current highlights, see [README.md](../../README.md#whats-new).

---

- **The default code model installs with one command.** `ai-raccoon model code set default` downloads and activates `faxenoff/code-daemon-embed-v1` (187 MB, 768-dim) into `<data-root>/models/`. Re-running against an already-downloaded directory only re-activates. (1.32.0) [How-to](../how-to/configure-embedding-engines.md#recipe-5-activate-the-code-corpuss-embedding-engine)
- **Cloud snapshots are authenticity-checked (HMAC) before attach, and model activation verifies sha256 pins.** (1.31.0)
- **A second corpus indexes your code, searchable via `memory_search kind=code`.** Never synced, never mixed with memory. Watches and file ingest feed it automatically; `code_get` reads a chunk's full source by hash. (1.30.0) [Feature](../features/code-corpus/) · [ADR-0085](../adr/0085-a-second-code-only-corpus-in-the-same-bank.md)
- **Bring your own embedding model.** Manifest-driven engines, `ai-raccoon model download` with SHA-256 pin verification, sentencepiece tokenizer support. (1.29.0) [ADR-0084](../adr/0084-arbitrary-embedding-models-are-manifest-described.md) · [How-to](../how-to/configure-embedding-engines.md)
- **Every search parameter is now configurable per call and per bank, no rebuild needed.** (1.28.0) [ADR-0083](../adr/0083-search-parameters-unified-source.md)
- **The CLI no longer opens the bank itself.** `noise entries` and `watch registered` reach the server too, completing the single-writer rule. (1.27.0) [ADR-0075](../adr/0075-only-the-server-writes-to-the-bank.md)
- **A repair now finishes on its own.** It embeds what it re-ingested, instead of leaving it unsearchable. (1.26.0) [ADR-0075](../adr/0075-only-the-server-writes-to-the-bank.md)
- **The memory now measures its own performance, and you can ask it.** (1.20.0) [ADR-0074](../adr/0074-a-capped-buffer-satisfies-the-channel-rule-and-reshapes-g4.md) · [How-to](../how-to/read-performance-metrics.md)
- **A long `memory_write` now matches on the chunk it stored, not the first page of the document.** (1.19.1) [ADR-0073](../adr/0073-a-write-embeds-the-chunk-it-stored.md)
- **The bank now compacts itself, and repairs entries too long to be searchable.** (1.17.0) [ADR-0070](../adr/0070-maintenance-is-a-list-of-jobs-with-a-ledger.md)
- **A long `memory_write` is now searchable across its whole length.** (1.15.0) [ADR-0064](../adr/0064-memory-write-chunks-like-everything-else.md)
- **Naming `shared` on a write asks for promotion instead of bypassing review.** (1.15.0) [ADR-0067](../adr/0067-naming-shared-asks-for-promotion.md)
- **Workspaces no longer require `full`.** (1.13.0) [ADR-0052](../adr/0052-the-workspace-lifecycle-is-a-write-not-a-destruction.md)
- **SECURITY: a delete could name another project and wipe the shared tier.** (1.13.0) [ADR-0051](../adr/0051-a-context-never-names-another-project.md)
- **BREAKING: `memory_search`'s `minScore` is now `minRelativeScore`, and defaults to off.** (1.12.0) [ADR-0047](../adr/0047-relative-score-floor.md)
- **Section-anchored search works, and ranking improved with it.** (1.12.0) [ADR-0044](../adr/0044-section-fts-weight.md)
- **Noise filtering, rebuilt around what could be measured.** (1.12.0) [ADR-0040](../adr/0040-read-path-query-guard.md)
- **Honest write outcomes and one explicit TTL path.** (1.12.0) [ADR-0032](../adr/0032-truthful-write-outcome.md) · [ADR-0034](../adr/0034-explicit-ttl-is-authoritative.md)
- **Semantic promotion classifier removed.** (1.11.0) [Why it was removed](../work/2026-08-13-fixing-zero-shot-promotion-classifier.md)
- **Semantic noise filtering and real-time TTLs.** (1.9.0) [ADR-0029](../adr/0029-pre-write-noise-filtering.md) · [ADR-0030](../adr/0030-realtime-heuristic-ttl.md)
- **Extensible file-type handlers and native JSON support.** (1.8.0) [ADR-0027](../adr/0027-extensible-file-type-handlers-and-json-support.md)
- **Search-quality metric system.** (1.7.0) [Plan](../plans/2026-08-11-search-quality-metric-plan.md)
- **Persistent propose-queue discards.** (1.6.5) [ADR-0026](../adr/0026-persistent-discards-and-shared-exclusion.md)
- **Always-on HTTP proxy.** (1.6.0) [ADR-0020](../adr/0020-always-on-http-stdio-proxy.md)
