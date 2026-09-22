# Phase 3 — adversarial verification verdicts

38 HIGH/MEDIUM findings verified by 6 independent verifiers (G1–G6), each required to re-derive the
claim from source and, where a live behaviour was claimed, to re-run the measurement in its own
scratch root — attempting falsification, not agreement.

**Tally: 30 CONFIRMED · 8 CORRECTED · 0 REFUTED.**

Every finding survived as a real observation; the corrections change wording, magnitude or
classification, not existence. Three corrections are load-bearing enough to change what should be
done (F71, F3, F63); the rest are precision repairs.

## Corrections to apply to the assembled record

| id | verdict | correction |
|---|---|---|
| F3 | CORRECTED | "defeats ADR-0089's unguessable-id mechanism" overstates: ADR-0089 decision 9 **explicitly declines** a global mode and parks O6's shape behind a future caller-identity ADR. Reclassify as an accepted-residual record (revisit trigger = the caller-identity ADR); the proposed "consult a mode" fix re-opens a ratified ruling. The measured reach itself (both projects' rows, absolute paths, full values) stands. |
| F9 | CORRECTED | "the delta's growth landed **entirely** outside them" is false: +65 of ~1513 lines landed inside the capped file. The raise history is **eight** `LOWERED` entries, not five. Severity stands. |
| F10 | CORRECTED | "a unit test cannot substitute a map" is **false** — the repo's own tests call `ProjectIdAliasMap.ReplaceDefault(FixtureMap())` (`ToolGateRetiredIdTests.cs:53,79,101,126`). Accurate claim: substitution requires mutating the process-wide static rather than injection. Drop "documented as pure key factories" (no such doc); say "stateless key helpers that now read a mutable global". |
| F30 | CORRECTED (evidence line) | The quoted SQL text is wrong: the constant is `SELECT scope FROM entries WHERE hash = @hash AND project_id = @projectId AND (@scope IS NULL OR scope IS NOT @scope)` — no `LIMIT 1`. The "arbitrary first row" mechanism and severity stand; both halves were re-measured live. |
| F35 | **upgraded READ → MEASURED** | The verifier drove the watcher end-to-end: file deleted → entries row gone with zero tombstones → next sync restored it. The finding's own caveat ("not driven end-to-end") no longer applies. |
| F53 | CORRECTED | Overstated by including the sweep's **dry-run listing**, which is *permitted* on `rw` (`SweepTools.cs:35` requires Destructive only for the delete). Replacement sentence supplied by the verifier; headline and access-denied evidence otherwise accurate. |
| F63 | CORRECTED | "Four tagged releases have no nuget.org package" is **21 of 101** GitHub releases (15 with no package under either id; six 1.0.x live under the former `arasz.ai-raccoon`). The lane's "four" came from having only the last 20 tags. Its "a later dispatch under the same VERSION would have silently no-op'd" is **falsified** — the observed next dispatch packed 1.42.5 and succeeded. Severity stands. |
| F64 | CORRECTED | 12 → **14** non-skipped Nightly runs (5 success / 2 failure / 7 cancelled); the fix landed ~**2h34m** after the red run (and ~23 min after the PR it fixed), not 20 minutes. Core claim (no scheduled CI; the last verdict was superseded without re-verification) stands. |
| F66 | CORRECTED | 19 → **20** skipped; one test fails under the CI dependency set, so "35 green" is **34 fully green of 35 collected**; files named by no gate = **33** (51−14−4), not 31. |
| F59 | CORRECTED (cosmetic) | The cited predicate line is `:21`, not `:22`. |
| F8 | CORRECTED (cosmetic) | Retry-attribute token count is ~1838, not 1835. |
| F71 | CORRECTED | Not an open defect: the unencrypted-bank branch is a **ratified documented acceptance** (S2/O3 — `docs/explanation/architecture.md:626`, added with the HMAC in `8b44c35d`). The recordable residual is the bank-wide blast radius of one `memory_sync`; a per-machine sync secret or digest pinning is a new decision, not a fix. |

## Per-group detail

- **G1 (data/sync/delete) — 5/5 CONFIRMED.** F29/F30/F31 re-measured end-to-end including a real
  two-replica push/pull; F32 re-measured with a non-unique same-named index; F35 upgraded to MEASURED.
- **G2 (lifecycle + retrieval) — 7/7 CONFIRMED.** All re-run live: the agent-requested-share row is
  destroyed by the next propose (a long note returns only as `organic-note`, the explicit reason erased);
  rating/sweep, workspace+context override, discard-then-rewrite, chunk-delete asymmetry, the 0.5×
  cosine factor and the 41-row floor cap all reproduced exactly.
- **G3 (security) — 3 CONFIRMED, 2 CORRECTED.** F70 re-measured with the verifier's own squatter
  (token + payload + forged result), plus a measured precondition: a never-served root exits 6 and
  leaks nothing, so the attack needs a pre-existing token file. F72 captured the canary in the OTLP
  body after decompiling the SDK filter order. F71/F3 corrected to ratified-acceptance records.
- **G4 (consumer/CLI/UX) — 8 CONFIRMED, 1 CORRECTED.** F6/F42/F43/F50/F51/F52 re-measured live;
  F37/F38 reproduced including the orphaned backend still answering 20 s after exit. F53 corrected.
- **G5 (quality/architecture) — 5 CONFIRMED, 2 CORRECTED.** F40 reproduced with the verifier's own
  1,000,000-iteration harness (21/1e6 Cancel, 34/1e6 CancelAll, 59/1e6 dispose-race throws at the
  cited lines); F7 both arms re-measured live; F5 red reproduced with the model/vocab hashes still
  matching the golden pins. F9/F10 corrected.
- **G6 (ops/CI) — 2 CONFIRMED, 3 CORRECTED.** F65 re-run over the workflow's own regex; F67
  re-measured with a crashing app under the documented `DOTNET_Dbg*` variables.

## Carried into Phase 4 (live calibration)

- **Fitting to fire?** The 7 HIGH findings are all reachable in the shipped default configuration
  (unencrypted bank, fresh install, default port) — none requires a non-default setting, which is the
  "loaded, not fired" question Phase 4 must settle with live evidence where a live system exists.
- **F70's precondition** (pre-existing token file) narrows its exposure window; the verifier measured it.
- **F71/F3** move to the ratified-residual register, which lowers the corrected HIGH count to 5
  (F6, F22, F29, F30, F31) plus F70/F72 among the security set.
