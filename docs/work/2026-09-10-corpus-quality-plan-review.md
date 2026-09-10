# Plan review — `air-corpus-quality-clean-instrument`

Reviewer: independent `code-reviewer` lane (`d-803`), explicit model
`openrouter/meta/muse-spark-1.3-contributor`, 2026-09-10. Input: the plan at
`.ai-badger/task-tracking/plans/2026-09-10-air-corpus-quality-clean-instrument.md`
plus the research record and the actual code. Verdict: **REQUEST-CHANGES**
(four MUSTs), all folded into the plan before implementation dispatch.

| # | Finding | Folded as |
|---|---|---|
| MUST-1 | T8's ≤5 threshold made its own mutation proof false (a 2/100 mutation passes a ≤5 gate) | T8 now pins **exact zero** (`assert debris == []`); the AC's ≤5 remains the documented minimum if a named residue proves irreducible |
| MUST-2 | B1.3 promised GREEN at a point where T11(a) is necessarily RED (the artifact regenerates in B2) | B1.3 gate split: (b)/(c) green at B1.3, (a) stays RED until B2.1; listed in the serialisation note |
| MUST-3 | "`--repeats` can never diff CLEAN" is false: `repeats_diff` exact-compares only when the golden carries a block, and f1 carries none — so base mode is available and strictly better for B0 (adds the row-stability FAIL gate + `peakRssMb`). Measured this session: a base-mode `--repeats 1` frozen reproduction diffs CLEAN | B0.3 switched to base mode; B3 keeps the non-base pair (two blocked candidates would exact-DIFF) and recovers stability with a manual before/after `--stability` snapshot |
| MUST-4 | B3.3 claimed "the delta" but gated aggregates only; "clean-subset numbers" (the AC's own words) were missing | B3.3 now gates the 2×4 clean-subset table (old clean n=76 vs new clean n=99, `stratum_stats` over f1+f2 rows) plus the aggregates |
| SHOULD-1 | T7 guards code-sharing, not signature-targeting; a repair that strips exactly the regex classes could measure 0 while emitting junk | B2.1 must paste the 23 old→new topic texts for human review; T1–T5 assert exact expected strings, not predicate-negativity |
| SHOULD-2 | Collision fallout beyond the 23 rows was recorded but not gated | B2.1 preview asserts `set(changed) == set(legacy_debris)` strictly; a red is named and relaxed with the collision cause, never blind |
| SHOULD-3 | T11 could pass vacuously and its import path was unspecified | T11 pins the legacy output to exactly the 23 debris ids via `debris_query`, checks (a) first, and uses a repo-anchored import; pre-B `TestRefreshParity` dry-run as the oracle baseline |
| SHOULD-4 | "CI-enforced debris gate" is false unless T8/T9 are copy-free | T8/T9 read committed bytes + `debris_query` with no env var; regeneration equality stays in the copy-gated T12/T13 |
| SHOULD-5 | "The 23 ids appear nowhere else" was overbroad; `mmr_transfer_checks.py` owns a second debris predicate | Claim narrowed to `.py` logic/test/CI files; the three affected scripts are dispositioned in B4.3/lane report |
| SHOULD-6 | No order-jitter rule for the B3 pair under load | B3.2 carries the C12 documented tie-tolerant fallback: set-equal, hits unchanged, anchors present, both logs kept, jitter ids named — never a moved anchor |
| NOTE-2 | Two gate wordings would mislead (`stale=[C035]` vs `stale=1`; strata greps) | B0.3 greps the `WARNING … [C035]` line; B3.3 pins the exact rendered strata patterns |
| NOTE-3 | Preconditions, debris census, CI-safety, and base availability independently re-verified by the reviewer | No action |
