# Lane report — Security (2026-08-21 delta campaign)

Lane: security · Base: `155f281e` · Read-only · 11 findings (5 MEASURED, 4 READ, 1 INFERRED,
1 MEASURED-disconfirmation; 3 HIGH, 5 MEDIUM, 2 LOW, 2 NIT). Two briefed leads disproven (F1, F9).

### F1 — Brief's `--endpoint` CLI override does not exist; download endpoint is pinned to huggingface.co [MEASURED — disconfirmation]
**Evidence:** `CliCommandTree.cs:165-174` — `model download` defines only `--revision/--file/--dir/--dry-run/--yes`; `ModelDownloadCommands.cs:21,41` `endpoint` is a constructor DI seam, only set to null (→ `https://huggingface.co`) at `CommandsRegistration.cs:35`. Not user-reachable; the self-referential-pin attack shape is not exposed at the CLI.

### F2 — Non-LFS provenance files are downloaded with no integrity pin, yet they author the manifest's security-relevant fields [READ]
**Severity:** MEDIUM
**Evidence:** `ModelDownloadService.cs:184-208` (no hash check); `ModelDownloadPlanner.cs:9,469` (`PinnedFile.LfsSha256` null for git blobs); `ModelDownloadService.cs:250,260` — `expected is null` ⇒ accepted unconditionally. `config.json`, `tokenizer_config.json`, `modules.json`, `1_Pooling/config.json` determine dims/window/pooling/normalization baked into `manifest.json` (:350-369). TLS is the only integrity control for the fields that steer embedding semantics.

### F3 — Manifest sha256 pins are never re-verified after download; a swapped model/tokenizer file is undetectable [READ]
**Severity:** MEDIUM
**Evidence:** `EmbeddingManifestLoader.cs:58-67` — load checks only `File.Exists`; `EmbeddingService.cs:239-253` — `EngineFingerprint` hashes only the manifest bytes, not the pinned files. Replacing `model.onnx` on disk with the manifest untouched: fingerprint unchanged → no re-embed → different model's embeddings silently mixed into existing vec tables. The download-time pins are decorative for tamper detection.

### F4 — Manifest edited in place is detected but the response is re-embed under the tampered config, not refusal [INFERRED]
**Severity:** MEDIUM
**Evidence:** `EmbeddingService.cs:239-253` + `VecDimensionReconciler.cs:40-90`. "Detected" means the bank is re-embedded under attacker-chosen dims/pooling/tokenizer — vector-store poisoning — not activation refusal. Same-dims semantic edits change ranking silently. Requires same-user write access to the model dir.

### F5 — Sync still uploads the whole bank and trusts the remote blob as SQLite — H9/H10 still open [READ]
**Severity:** HIGH
**Evidence:** `SyncService.cs:63-70` whole-bank `VACUUM INTO`, pushed under caller-named key; `:200-218` remote check is `PRAGMA quick_check` (integrity, not authenticity); `:229,236` remote blob `ATTACH`ed into the live bank. Cloud-store write access equals memory-bank takeover. Unchanged.

### F6 — H18 still open: retry pipeline string-matches exception type name [MEASURED]
`ResiliencePipelineFactory.cs:62` confirmed verbatim.

### F7 — H7 still open: access mode resolves from the caller-named project [MEASURED]
**Severity:** HIGH — `MemoryAccessGuard.cs:9-17` unchanged; no server-side project identity binding.

### F8 — H8 still open: `promotion_list` skips the gate when `projectId` is omitted [MEASURED]
**Severity:** MEDIUM — `PromotionTools.cs:36-40`; null = unscoped listing of cross-project queue rows.

### F9 — Settings/repair/prune endpoints are correctly gated; unauthenticated loopback write is not reachable [MEASURED — lead disproven]
**Evidence:** `McpServerSetup.cs:103,120-124,138-153`; `McpTokenGate.cs:31` default-closed; fixed-time compare; 0600 token. A tokenless host maps none of the write endpoints at all.

### F10 — Repair/prune POST endpoints are enqueue-only and token-gated; replay surface is bounded [READ]
**Severity:** LOW — outbox requests, no inline work; no unauthenticated trigger; only residue is no idempotency/dedup on repeated requests.

### F11 — `jsaa-memory.db` with personal data still committed [MEASURED]
**Severity:** MEDIUM — 19,173,576 bytes, 2,518 rows, 220 with `'@'` in value. Owner question 9 from the prior campaign remains unanswered.

## Still open
H7, H8 (residue), H9/H10, H18, jsaa-memory.db — all re-verified present.

## Owner questions
- Should activation re-verify manifest sha256 pins against on-disk files (F3)?
- Is TLS the acceptable sole integrity control for manifest-authoring config files, or do registry pins land before arbitrary-model ships (F2)?
- Is remote-blob authenticity (signature/keyed hash) planned for sync, or is cloud compromise accepted as game-over (F5)?
- Should `promotion_list` without `projectId` require a global/read-all mode (F8)?
- Is removing `jsaa-memory.db` (19 MB, PII) from git history scheduled, or only from HEAD?
