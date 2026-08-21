# Lane report — Retrieval/embedding-algorithm (2026-08-21 delta campaign)

Lane: retrieval/embedding (high-reasoning) · Base: `155f281e` · Read-only + scripted corpus
analysis · 11 findings — 1 HIGH, 3 MEDIUM, 4 LOW, 3 NIT; 5 MEASURED, 4 READ, 2 INFERRED.
One briefed lead partially refuted (F5 sextant circularity).

### F1 — Manifest fingerprint does not cover in-place weight swaps, contradicting the loader's own tampering claim [READ]
**Severity:** HIGH
**Evidence:** `EmbeddingManifestLoader.cs:19-22` claims tampering changes the fingerprint and re-embeds; `EmbeddingService.cs:239-249` hashes ONLY the manifest bytes; `Load` (:59-67) checks `File.Exists`, never the pinned sha256s. A weights file replaced in place leaves fingerprint unchanged → no re-embed → old-weight vectors silently scored against new weights. Converges with security lane F3.

### F2 — The held-out gate partition is document-disjoint and leakage-free [MEASURED — positive]
Scripted check: 19 gradeable queries, tuning docs = 10, held-out = exactly A8/A9/A10 with zero document overlap; S1/S3-S6 correctly land in the doc-leaked tier. Matches `RetrievalTuningSets.cs:44-75`.

### F3 — Out-of-sample control for the tuned parameters is thin: 3 pinned queries on a different corpus [INFERRED]
**Severity:** MEDIUM — M2's knob moves were selected on eval-set-100 (in-sample by construction), validated one-shot on test-set-10 with proxy grades. No held-out set exists for the ai-raccoon-docs corpus the knobs were tuned on; the Nightly gate cannot catch a corpus-specific overfit.

### F4 — eval-set-100 is one family, ~3 queries per document; harness has no internal split [MEASURED]
**Severity:** MEDIUM — all 75 gradeable queries are docs:-family ADR-derived, 3-per-document across 25 ADRs; every matrix/Optuna number in the report is in-sample to eval-100. Report is honest about it; the diversity ceiling stands.

### F5 — Sextant circularity lead partially refuted: table-chunking adjudication uses a vendored real-docs corpus [READ]
`TableCorpusCatalog.cs:12-14` + vendored real markdown + 40 graded queries; sextant-6.json is only a probe dataset in the tuning matrix, not the chunking adjudicator.

### F6 — "No fusion regression" is a proposal the second fusion overrides at shipped λ=0.1 — established, not hidden [READ]
`SqliteMemoryStore.cs:608` feeds Reorder output back through Merge which re-derives scores from positions (~7 positions per adjacent sibling at λ=0.1); flag ships default-off consistent with ADR-0078 and the tuning matrix. Honest; naming overstated.

### F7 — FusionDiff records a fully-dropped served list as no-change [READ]
**Severity:** LOW — `FusionDiff.cs:36-39`; latent (flag default off).

### F8 — Migration-window attack: outbox claim largely survives; residual TOCTOU fails loud, legacy path fails broken [INFERRED]
**Severity:** MEDIUM — ToolGate refuses all tools during migration; watch/sync writers leave rows pending. Residuals: (a) a tool passing the gate just before StartMigrationAsync commits can hit new-dim-blob→old-dim-table → loud sqlite-vec error, not mixed-dim serving; (b) `ConfigureAsync` (`EntryEmbedder.cs:26-53`) has no outbox/reconcile and throws mid-re-embed on dim change — production-dead but still exposed on the IMemoryStore port.

### F9 — ADR-0036 budget invariant holds across ingest, repair, backfill, and watch paths [MEASURED — positive]
All paths resolve budget via IEmbeddingService with engine tokenizer. Nit: openai budget hard-capped at 256 despite 8191 window (conservative, pre-existing, documented).

### F10 — Core ranking math verified correct [MEASURED]
Weighted RRF, SimFromDistance mapping, absent-structure cap, both nDCG implementations agree, TokenBudget.Trim valid, GH #371 guard present.

### F11 — Consolidation can return fewer than the requested limit [NIT]
Documented ADR-0005 behaviour; nothing tells the caller results were merged away.

## Still open
- Where to close the sha256 re-verification gap (load vs activation vs fingerprint).
- Whether eval-100 gets a second family or internal held-out split before the next tuning round.
- H8 residue not re-derived here.

## Owner questions
- Is the model directory inside a trust boundary where in-place weight swap is impossible, or should load-time sha256 verification be added?
- Pinned held-out floor over ai-raccoon-docs itself, or jsaa + TS-10 accepted as the control?
- Is 3-queries-per-ADR clustering acceptable for published means?
- Should FusionDiff.Between return an all-dropped signal when adjusted is empty?
- Remove ConfigureAsync/ConfigureEmbeddingAsync from IMemoryStore now that production routes through the migration outbox?
