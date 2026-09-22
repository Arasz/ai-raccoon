# Phase 4 — severity calibration against the live system

Question per finding: **does it fire in the configuration a user actually runs**, or is it loaded but
unfired? Calibration changed a severity only where the firing condition demands it; the code path is
wrong either way, so most grades stand with an explicit precondition recorded.

**Shipped default** = a fresh install: no embedding engine configured, unencrypted bank, no sync
configured, access mode `rw`, default port 7721, watch off, bundled local ONNX model present but not
activated.

## The corrected HIGH set (6 after F71 moves to the ratified-residual register)

| id | fires in the default install? | precondition | calibrated |
|---|---|---|---|
| **F6** fresh install searches keyword-only, only warning blames the code corpus | **YES — every fresh install** | none; "no engine" *is* the default | **HIGH — fix first** |
| **F22** agent-requested-share destroyed by the next propose pass | **YES — any `context:"shared"` write** | none; the auto-promote loop or any manual propose triggers it | **HIGH — fix first** |
| **F70** squatting process receives the token and tool payloads, forges results | **YES, code-path-wise** | a local process/user binds the port first **and** the data root has been served once (token file exists — otherwise the proxy exits 6 and leaks nothing) | **HIGH — fix first** (local adversary required) |
| **F31** re-created content is silently deleted by the tombstone | **NO — loaded, not fired** | sync configured (opt-in); then any re-write of deleted content | **HIGH when sync is on** — silent data loss is the worst consequence in the set |
| **F29** `memory_delete_context` undone by the next sync | **NO — loaded, not fired** | sync configured; then a delete of committed content | **HIGH when sync is on** (retention/privacy failure) |
| **F30** `memory_delete` writes ≤1 tombstone for N rows | **NO — loaded, not fired** | sync configured; the workspace-first half is additionally planner-order dependent | **HIGH when sync is on** |

**Reading:** three HIGHs (F6, F22, F70) fire for every user of the shipped default; three (F29–F31) are
one settings-row away and are silent when they fire. Nothing in the set needs an exotic configuration.

## Firing conditions for the MEDIUM set (grouped by what gates them)

**Fire by default (no configuration needed):**
- **F19** evidence cosine is α×content-cosine — the default write path creates heading-less rows with no
  structure vector, so the halving is the norm, not the exception.
- **F20** the default 0.6 floor caps an explicit `limit` — needs a bank with ≥42 fusion candidates.
- **F24** `context`+`workspace_id` silently bypasses an active workspace.
- **F25** a post-discard rewrite reports `queued-for-promotion` with nothing queued.
- **F26** a multi-chunk write reports one hash; `memory_delete` removes 1 of N.
- **F37** Ctrl-C exits 15 (the "you mistyped" code) and leaves the cancelled backend running.
- **F38** any successful one-shot CLI command can leave a backend alive up to 4 h.
- **F52** a query with no lexical or semantic overlap returns every entry with `ranking: 1.0`.
- **F53** access-denied refusals on `rw` name no remedy (corrected: the dry-run listing is permitted).
- **F72** a refused `memory_search` query reaches the log **and the OTLP exporter** — the query guard is
  armed by default.
- **F3** `memory_promotion_list(allProjects=true)` returns every project's queue to any token holder
  (accepted-residual record).
- **F7** `doctor` exits 15 on a corrupt bank / out-of-range timestamp (needs a damaged bank, which is
  exactly when `doctor` is run).

**Fire on a documented, commonly-enabled feature (not the bare default):**
- **F23** rating/`created_at` sweep — needs a per-entry TTL, which needs `full` access mode.
- **F35** watcher deletes return on the next pull — needs watch enabled **and** sync.
- **F40** `WatchScanGuard` race — needs watch enabled; ~2–6×10⁻⁵ per racing call.
- **F42** `quiet.log` tearing — needs `--quiet` plus the auto-started backend (the normal quiet-mode path).
- **F50/F51** tutorial Step 4 fails; `model embedding set local` exits 17 when the default port belongs to
  another bank — hits new users following the docs.
- **F32** `doctor` reports HEALTHY with uniqueness gone — needs a damaged index.
- **F8** flake tolerance has no live mechanism — affects the ~1000-test integration/E2E surface.
- **F4** #414 PII guard unwired and missing two path spellings — matters at the next rewrite.
- **F5** golden-vector gate — red on arm64 dev machines; inert on x64 CI.

**Process/CI findings, live but not user-facing:**
- **F59** trait gates check the name not the value (latent — no current offender).
- **F63** 21 releases with no nuget package (5× the filed magnitude).
- **F64** no scheduled CI; the last Nightly verdict was superseded without re-verification.
- **F65** `global.json`/`benchmarks` PRs skip every dotnet lane.
- **F66** 33 python test files named by no gate (401 tests pass in ~12s).
- **F67** `build-fast` cannot produce its crash dump.

**Structural/quality, no firing event:**
- **F9** ratchets pass with slack while growth lands elsewhere; **F10** the alias map is a mutable
  process-static. Both are ruled trades that need re-tightening rather than a bug fix.

## What the calibration changes

1. **F71 leaves the scored HIGH set** (ratified acceptance; residual recorded) — the actionable HIGH count
   is **6**, of which **3 fire for every default user**.
2. **F31, F29, F30 gain an explicit precondition** ("sync configured") without a severity change: silent
   correctness failures on a supported feature keep their grade; the plan must not present them as
   default-config fires.
3. **F53 is corrected, not downgraded** — the refusal-wording gap is real; only its scope shrank.
4. **F63's magnitude grew 5×** (4 → 21), which raises, not lowers, its process risk.
5. **Nothing was found to be non-reproducible** — the adversarial pass confirmed every mechanism; the
   calibration only narrows or widens the population each one reaches.
