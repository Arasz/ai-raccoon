# QA REVIEW — implemented diff (final round before PR)

QA lane output, 2026-08-26, on the 6-commit implemented diff (45542480..HEAD).

---

---

# QA REVIEW — `doctor-feature-match-cont` implemented diff (final round before PR)

**Scope reviewed:** 6 commits (`45542480..HEAD`), 17 files. I wrote none of it; no files edited. Binding spec: `docs/work/2026-08-26-followups-brief.md` (WP1–WP4, all acceptance criteria), plus R1/R2 review findings it folds in.

## What I ran, first-hand (this worktree, `TESTINGPLATFORM_TELEMETRY_OPTOUT=1`, never `--nologo`)

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build` | **Clean, 0 warnings / 0 errors** |
| 2 | Guard gate `FullyQualifiedName~ModelResetGuardTests\|...EndpointTests` `--minimum-expected-tests 8` | **total 8, Passed** |
| 3 | Same gate with `9` | **Fails loudly** ("Minimum expected tests policy violation, tests ran 8, minimum expected 9", run exit 9) |
| 4 | Derive gate `~HowToExitTableTests\|~HowToSettingsExitTableTests` `--minimum-expected-tests 4` | **total 4, Passed** |
| 5 | Same gate with `5` | **Fails loudly** (4 ran, 5 expected) |
| 6 | `~VersionContractTests` (no floor) and with `--minimum-expected-tests 7` / `8` | **7/7 green at 1.36.1**; 8 → violation (loud) |
| 7 | `~CliCommandsDoNotOpenTheBankTests` | **1/1 green** (acceptance f) |
| 8 | `~SettingsEndpointTests\|~SettingsChannelExitCodeTests` `--minimum-expected-tests 18` | **18/18 green** — pre-existing endpoint/CLI suites unaffected by the MapDelete change |
| 9 | **RED witness (R2 bar)**: new guard tests copied into a scratch worktree at parent `45542480` (pre-change production code), built and run | **4 of 8 RED**: refused×2 verbs (exit 0 vs 25), StoreThrows (exit 15 vs 25), client-409-mapping (wrong exception type). The 4 unchanged-behavior pins (no-open reset, non-provider delete, embedding-set-15) pass on old code — exactly as they must. Every new test is a real behavior pin |
| 10 | **Live E2E** (scratch data root, real server, seeded open migration): `settings model reset` / `settings model embedding reset` → **EXIT=25 both verbs, frozen message verbatim on stderr**; 6 keys intact; row still open (lease blocked the relay — drain passes observed in serve log, `finished_at` still NULL). After `UPDATE ... SET finished_at`: **EXIT=0**, success line, all 6 keys cleared |
| 11 | Checklist gate one-liner | Parses, **exactly 12 ids**, 7 fields each |
| 12 | The two new checklist seed INSERTs run against the real DDL in a scratch DB | **Both fail**: "6 values for 7 columns" (row 11), "8 values for 9 columns" (row 12) |

## What I verified statically (read test + production it asserts against)

- **All six brief tests exist with the exact required names/assertions**: verbatim stderr via `ShouldBe(frozen + Environment.NewLine)` (not `ShouldContain`) in both Fast and Integration refused tests; stdout-negative for the success line; six-key presence via real HTTP GETs; migration-row survival via raw SQL; `[RetryTheory]` over **both** verbs on both reset tests; `[Integration][Slow]`/`[Unit][Fast]` traits correct; literal `25` at RED time with the const in the same commit (R2 F1) ✓.
- **Derive gates**: anchored after the `composes into a script:` / `never reports success:` sentences (both unique in the doc) then to the `| Exit code | Meaning |` header (R2 F12); row format regex matches every real row; set-equality vs consts **by name** (R1 F6); 0/1/2 table-own literals (R1 F1/R2 F4); 19/20/22/24 phrases asserted on **both** sides and every pinned phrase provably occurs in both the table cell and the const's doc comment (checked all 11 cross-checked phrases); settings table 17/18/23/25 same pattern; the `15` note is prose, not a row, so set-equality holds ✓. RED mutations: delete-row/renumber → set inequality; reword → phrase assertion (both sides). Const-rename is compile-time protection, correctly labeled (R2 F7).
- **Client 409 mapping** (Fast test) → RED on parent (witnessed); mutation = remove the Conflict branch or hardcode the message (both caught by message-equality).
- **Embedding-set-stays-15 pin** → passes on parent (today's behavior) and on branch; only a dispatcher-level catch can redden it — the exact out-of-scope change it exists to block (R2 F3) ✓.
- **ConfigCommands.cs untouched** (no diff) ✓; **no event-id/logging changes** (diff scan) ✓; `VERSION=1.36.1`, README What's-new entry (defect-fix voice, names exit 25), settings-table 25 row landed **in the same commit as the const** (R1 F2), doctor prose "non-zero on a mismatch" fixed with the 19/20/22 enumeration (R1 F13), `docs/work/README.md` index rows for all five follow-up docs (links resolve) ✓. Checklist: 12 ids = 7 kept + 4 R3 rows + the new guard row; `_note`/`run`/`derived.version` all 1.36.1; blank-provider row reworded per R1 F5 (with out-of-scope remediation note); row 3's copy-path/data-root mismatch fixed; row 12's close-the-row UPDATE and exit-0 leg spelled out ✓. `ModelResetRefused` doc comment notes the latent server-side bypass (R1 F12) ✓.

## Findings

**MAJOR-1 — checklist row 11's seed INSERT is broken; the row cannot produce its evidence.** (a) `docs/work/checklist/2026-08-26-1.36.0-doctor-memory-engine.json:104`. (b) Criterion: brief WP2 (R2 F10c) — every row's commands executable as written, the blank-provider row keeping the raw-SQL seed. `INSERT INTO model_migration(id, provider, model, base_url, engine, started_at, finished_at) VALUES (1, 'openai', 'text-embedding-3-small', NULL, 0, NULL)` is **6 values for 7 columns** (verified: sqlite errors, exit 1), and even padded, `started_at` is `INTEGER NOT NULL` (MemorySchema.cs:405) — a NULL fails. With no migration row, the 1012 warning never fires and `settings model reset` exits 0, not 25 — the stated evidence ("EXIT=25; 1012 count = 1; 526 count = 0") is unproducible. Secondary: the 1012 warning fires on EmbedDrainService's **15s poll**, but the command greps ~4s after serve start — likely 0 even with the INSERT fixed. (c) Fix: `VALUES (1, 'openai', 'text-embedding-3-small', NULL, 'openai', 0, NULL)` (7 values, `started_at` = 0), and extend the pre-grep window past a drain pass (sleep ≥20 or a wait-for-`1012` loop).

**MAJOR-2 — checklist row 12's seed INSERT is broken; the refusal evidence cannot be produced.** (a) `docs/work/checklist/2026-08-26-1.36.0-doctor-memory-engine.json:113`. (b) Criterion: brief WP2 — "spell out the new row's exit-0 leg" / commands executable as written. The 9-column `model_migration` INSERT (MemorySchema.cs:399-409) lists 9 columns but supplies **8 values** (verified: "8 values for 9 columns", exit 1); if padded naively, `started_at` would be NULL (NOT NULL violation) and `'test-holder'` would land in `finished_at`. The settings rows insert, the migration row does not → both refused verbs silently exit **0**, contradicting the expected "EXIT=25 with the frozen message on both refused verbs". (c) Fix: mirror the integration twin's seed — `VALUES (1, 'openai', 'text-embedding-3-small', NULL, 'openai', 0, NULL, 'test-holder', strftime('%s','now') + 3600)`.

**MINOR-1 — deviation from the brief's letter that is *correct* but should be called out in the PR.** (a) `src/AiRaccoon/Settings/SettingsEndpoint.cs:84-87`. (b) The brief says "mirror MapPost :84-87 → `Results.Conflict(ex.Message)`"; the implementation returns `Results.Text(ex.Message, "text/plain", statusCode: 409)` instead. This deviation is **required** — `Results.Conflict(string)` JSON-quotes the body (the R1/R2 "byte-for-byte through Conflict" trace was wrong; the existing 400-precedent test only survives because it uses `ShouldContain`), and the verbatim-stderr AC + client-mapping test depend on the plain body. Live E2E confirms byte-for-byte fidelity. (c) Fix: none in code; add a one-line note in the PR body that the 409 arm deliberately uses `Results.Text` over the MapPost mirror so the brief's letter isn't treated as gospel later.

**MINOR-2 — the lane's RED-witness captures are not in the repo.** (a) brief :20/:54 acceptance (e) ("both RED runs MUST be run and captured"). (b) No capture artifact for the lane's pre-change runs exists in the diff. Substance is proven — my own parent-commit witness (4 RED of 8, #9 in the table) reproduces both claimed mechanisms — but the lane's process claim is unverifiable from the repo. (c) Fix: paste both RED run summaries into the PR body (or accept my witness as the record).

## Verdict

**SHIP-AFTER-FIXES.** The code, tests, and gates are honest and verified end-to-end: build clean; both class-filter gates pass at exact counts and fail loudly at count+1; VersionContractTests green at 1.36.1 with its exact count; every new test proven RED against pre-change code (4/8); the live server refuses both reset verbs with exit 25 + the frozen message verbatim, deletes nothing, and resets cleanly once the row closes. The two MAJORs are confined to the manual-checklist JSON's seed SQL — the exact "executability un-gated" gap R2 F10c existed to catch — and are two one-line INSERT fixes before the PR opens. No BLOCKER: there is no path where the suite is all-green while the strand defect remains (the refused test was RED on parent and the guard is live-verified), and the guard has no hole beyond the documented, accepted check-then-delete residual.

## Findings table (schema-last)

| id | severity | file:line | failing criterion | finding | fix |
|---|---|---|---|---|---|
| F1 | MAJOR | docs/work/checklist/2026-08-26-1.36.0-doctor-memory-engine.json:104 | WP2 (R2 F10c): commands executable as written; row keeps the raw-SQL seed | Row 11's `model_migration` INSERT is 6 values for 7 columns (sqlite-verified exit 1); `started_at` NOT NULL would also reject a NULL. Row never exists → no 1012 warning, reset exits 0 not 25 — evidence unproducible; 15s drain poll also outruns the 4s grep window | `VALUES (1, 'openai', 'text-embedding-3-small', NULL, 'openai', 0, NULL)`; wait ≥20s (or wait-for-1012) before grepping |
| F2 | MAJOR | docs/work/checklist/2026-08-26-1.36.0-doctor-memory-engine.json:113 | WP2: new row's exit-0 leg spelled out / executable as written | Row 12's seed lists 9 columns with 8 values (sqlite-verified exit 1); naive padding hits NOT NULL/wrong-column. Settings rows insert, migration row doesn't → both verbs exit 0, expected EXIT=25 evidence unproducible | `VALUES (1, 'openai', 'text-embedding-3-small', NULL, 'openai', 0, NULL, 'test-holder', strftime('%s','now') + 3600)` (mirror the integration twin) |
| F3 | MINOR | src/AiRaccoon/Settings/SettingsEndpoint.cs:84-87 | Brief :11 "mirror MapPost :84-87 (`Results.Conflict`)" | Deviation to `Results.Text(..., text/plain, 409)` is correct and required (Conflict JSON-quotes; the R1/R2 byte-for-byte trace was wrong) but the brief's letter is now stale | One-line PR-body note that the 409 arm intentionally uses `Results.Text` for a verbatim body |
| F4 | MINOR | docs/work (no capture artifact); brief :20/:54 | WP1 acceptance (e): both RED runs "run and captured" | Lane's RED captures are not committed; unverifiable from the diff (my parent-commit witness substitutes for substance) | Paste both RED summaries into the PR body, or adopt this review's witness as the record |

**Verified good (no findings):** guard placement + 409 mapping + inner catch + exit 25 (live-verified); all 6 tests honest with real RED mutations (witnessed 4/8 on parent); both-verbs `[RetryTheory]` coverage; verbatim-stderr equality assertions; embedding-set-stays-15 pin; both derive gates (set + 11 pinned phrases, both sides); `--minimum-expected-tests` honest (loud at wrong counts); VersionContractTests 1.36.1; CliCommandsDoNotOpenTheBankTests green; no event-id changes; ConfigCommands untouched; checklist 12 ids/7 fields/parses; row 3 path mismatch fixed; blank-provider rewording + close-row SQL + exit-0 leg present; settings-table 25 row with the const commit; README/VERSION/docs index; `ModelResetRefused` doc comment carries the R1 F12 residual.