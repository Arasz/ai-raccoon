# Review — delta-review fix plan, pre-landing verification

**Date:** 2026-08-21 · **Subject:** `docs/work/2026-08-21-delta-review-fix-plan.md` (draft `493e567d`)
**Reviewer:** high-reasoning code-review lane, read-only (owner order: no code runs tonight —
every check below is a file read or git/gh metadata read, nothing executed).

## What was verified clean (citable)

- **Coverage:** all 14 approved rulings appear exactly once (S3 split into S3a done / S3b
  deferred is intentional). None dropped, none duplicated.
- **Anchors:** 22 file:line anchors checked against main `4cbce9d0`; 18 exact, 4 corrected
  (findings 5, 6, 10, and D2's semantic mismatch).
- **Collision claims:** all 17 "in #405" claims and all 10 Wave-1 "start now" free-of-#405
  claims verified against `git diff --name-only main...origin/task/code-mem-implementation`
  (167 files, head `d16e4bdc`). No "start now" item is secretly in #405. Restart PR 1/2 file
  ownership cross-checked against that session's own plan.
- **RED claims:** S1 (`PromotionTools.cs:37-42` null branch reaches `ListAsync` ungated), D6
  (`MaintenanceJobRunner.cs:38-46` reads sit above the try at `:56`), C1 (no test references
  `BackendLaunchArguments`; `InternalsVisibleTo` makes the test feasible) — all genuinely RED
  today. D2's RED claim was the exception — finding 2.
- **Honesty:** the unexplained `server-lifecycle` checklist failure is disclaimed in two places;
  D3's numbers are labelled analytic/hypothesis; issue #414 is real and matches.
- **Invariants:** proof-of-done, TDD-RED-first, no-hand-rolled-crypto (S2 escalates instead of
  inventing), cli-asks-the-server-acts (D3's site argument independently corroborated via
  `SqliteConnectionFactory.cs:252` + `EncryptionCommands.cs` CLI bank opens).

## Findings → dispositions (all applied to the plan before landing)

| # | Sev | Finding | Disposition |
|---|---|---|---|
| 1 | HIGH | D2 named `modules.json`/`1_Pooling/config.json` as pinnable, but they are consumed in-memory (`ModelDownloadPlanner.cs:304-310`) and never reach disk — nothing to hash or verify | D2 rescoped to the two on-disk provenance files; the in-memory pair explicitly out of scope with the reason |
| 2 | HIGH | D2's RED reason ("git blobs get null") is false — `ModelDownloadService.cs:366,369` already TOFU-hashes downloaded bytes; the real gap is `ProvenanceFiles` never entering the manifest's file lists | Gate restated to assert presence-in-manifest; would otherwise have been green on day one, silently voiding `prove-the-check-fails` |
| 3 | HIGH | S1's "read-all mode" does not exist (`AccessMode` = `{Ro,Rw,Full}`; `MemoryAccessGuard.cs:11` requires a projectId to resolve at all) — item unbuildable as written | S1 rewritten to the simpler explicit `allProjects=true` consent argument; deviation from the ruling's wording flagged as owner flag O6 |
| 4 | HIGH | D1's injectable hash helper implies a registration in `AppRegistrations.cs`, which IS in #405, while D1 says "free of #405" | DI note added: inject via the existing `EmbeddingManifestLoader` chain (registered at `AppRegistrations.cs:279`) or accept a declared one-line merge |
| 5 | MED | C3 anchor off by 4 (`:22-26` → `:18-22`) | Corrected |
| 6 | MED | C1's drain anchor wrong (`:139-146` → `Hosting/Proxy/BackendLauncher.cs:151-152`) and the `Proxy/` path unstated | Corrected |
| 7 | MED | #405 is 167 files, not 169 | Corrected |
| 8 | LOW | jsaa-memory.db byte count off by 200 (19,173,376) though labelled "measured"; the 220-`'@'`-rows and 94-owner-email-rows counts read as contradictory | Corrected; the two measures now labelled distinctly |
| 9 | LOW | Rulings doc's own H1 says "12 owner rulings" while carrying 14 answered headings | Flagged in the plan's owner-flags so the plan doesn't read as over-counting |
| 10 | LOW | Carried-table anchor `MemorySchema.cs:478-482` / `>` is actually `:485-488` / `>=` | Corrected |

One addition beyond the findings: C2 now states it rebases onto C3's `ExitCode.cs` /
`ExitCodeTests.cs` / docs-table additions (cross-wave, serialized by construction — noted so the
Wave 3 implementer expects the base).
