# Integrated implementation plan: search-quality follow-ups (kind/strip/session/rank) — v2 REVIEWED-FOLDED

Task: `air-search-quality-followup-kind-strip` (high effort). Date: 2026-09-03.
Status: plan-review complete — 3 × APPROVE-WITH-CHANGES, all MUSTs folded below.
v3 2026-09-03 (owner feedback): Package 6 agent-facing surface added; integration renumbered P6→P7.
No implementation dispatches until owner sign-off on this version.

Planners: d-389 (architect), d-390 (dotnet-engineer), d-391 (test-engineer).
Reviewers: d-392 (code-reviewer), d-393 (qa), d-394 (qa-backend). All lanes ran on the
opus/sonnet-fallback model — cited line numbers are re-verified at build time wherever
marked (fallback staleness is proven: d-393 verification note, d-394 H1).

Sources (paths relative to the task worktree unless absolute):
- Owner rulings: `docs/work/2026-09-03-search-quality-consequences-moe.md`
- Current behavior: `docs/adr/0094-search-quality-records-every-kind.md`
- Research: `/Users/arasz/.pi/agent/subagent-logs/d-386.jsonl` (session/consumers),
  `d-387.jsonl` (strip safety), `d-388.jsonl` (migration recipe)

## Join rulings (1–6 original, 7–13 added by the review fold; all attackable)

1. **ADR numbers (AMENDED 2026-09-03 — 0096 taken by another lane's #600 search-defaults
   record):** P2 takes `0095` (telemetry never syncs), P3 takes `0097` (kind column).
   Each with README row in the same commit. P1 decision-note only, no ADR.
2. **Signatures (AMENDED twice — contradiction fix + owner gate D1):** `sessionId` is
   **required at every layer** (owner-gate 2026-09-03, D1 CHANGE: "if optional, removable;
   every agent has a session"). `MemoryTools.Search` gains required `string sessionId`
   (trailing, before `ct`) with blank/whitespace rejected fail-fast at the tool boundary;
   `DispatchAsync` + both record methods take required `sessionId` alongside (d-392 M4:
   `SearchQuery` untouched). This is a breaking wire change: every existing caller is
   updated in P1, `ExpectedContract` updated red-first. Asymmetry is deliberate: P4's
   `servedRank` stays optional (some follow-through callers never saw a rank; owner ruled
   session only). Exact per-package signatures (d-392 M1, d-393 MUST-6, d-394 M1):
   - P1: `DispatchAsync(…, string sessionId, ct)` alongside-param (d-392 M4:
     `SearchQuery` untouched — it feeds backends that must ignore attribution);
     `MemoryTools.Search` gains trailing required `string sessionId` (blank rejected).
     `Safe` gains `sessionId` (it is the only live record path; d-394 M2).
   - P3: `kind` is **required `string`** (`"memory"|"code"|"both"`, dispatcher maps
     `SearchKind.ToString().ToLowerInvariant()`, service-side guard), placed after
     `projectId`/before optional `sessionId`; string-over-enum (storage encoding
     grep-able; avoids `Core.SearchQuality`→`Core.Memory` coupling). Added to
     **both** `Safe` and `Async` (kind on `Async` alone leaves the live path
     kind-blind; d-394 M2). P1 tests use **named args** throughout so P3's
     insertion cannot silently rebind (d-393 MUST-1/6).
3. **Grade fail-fast is P5-write-optional, default no-change.** Decider: owner-gate at
   P5 closeout. Bar: search tool-error logs for SQLite CHECK-violation errors
   surfacing through `QualityTools.RecordGrade` (the only observable misuse signal —
   bad rows are DB-blocked, never stored; d-393 SHOULD-3, d-392 S8). Absence → no
   guard, pins-only.
4. **Unknown-correlation-id pins live in P4, both paths** (d-393 MUST-11):
   follow-through unknown-id + grade unknown-id silent no-ops, one test each
   (mutation: throw on 0-row UPDATE → fails). P5 must not re-pin either way.
5. **No product-VERSION bump in any package** (unanimous). Only the schema ladder
   moves (11→12, P3).
6. **Lane structure:** Lane-W serial spine P1→P3→P4; Lane-S parallel P2 + P5-audit;
   P5-write (if any) sequenced; P6-surface after P1+P4 merge (parallel-safe vs
   P2/P3/P5-audit otherwise); P7 strictly last.
7. **MCP contract ownership per break** (d-393 MUST-12, d-392 S10, d-394 S1): the
   package that changes the wire updates `ExpectedContract` red-first in the same
   commit — P1 (`sessionId:string!`, required per owner gate D1), P4 (`servedRank:integer|null?`, which
   also finally names servedRank's type/optionality). P5 verifies the freeze (no
   edits). P7 asserts final-contract green (added to AC3).
8. **BDD restored for P1/P3** (d-393 MUST-3, d-394 S10 overruled toward the
   plan's own invariant): one correlation-filtered scenario each (session-attributed
   search; kind-value pin), red-proofed (pre-P1 `session_id IS NULL` fails; pre-P3
   `no such column: kind` fails loudly). Bare `COUNT(*)` never pins new behavior.
   BDD edits touch **both** `docs/features/code-corpus/code-corpus.feature` and
   `CodeCorpusSteps.cs:808-836` (d-392 S2: d-388's "no *.feature" claim searched
   `tests/` only).
9. **Release rule** (d-392 S6, owner-APPROVED 2026-09-03, no waiver): one release ships
P1–P7 together; any split release
   rides P2 first-or-together with P1/P3 (strip-before-richness; otherwise new
   session/kind values sync in the interim); all syncing hosts upgrade together
   (mixed-version fleets fail closed both ways — verified). Owner may waive
   explicitly (asked 2026-09-03, APPROVED as written — ruling stands).
14. **Agent surface is its own package (P6).** Behavior packages write first-draft
descriptions for their own new params; P6 owns cross-surface consistency (skill +
reference + all descriptions) after P1+P4 merge, parallel-safe vs P2/P3/P5-audit
otherwise. Framework catalog sync is report-only.
10. **P4 shape is d-390 verbatim** (d-392 M2, d-393 MUST-10): uniform object rows
    `{ "path": "…", "rank": 3|null }`, `FollowThroughEntry(Path, Rank)` Core
    record, legacy-`List<string>` fallback with in-memory upgrade, dedupe rule =
    ordinal by path, existing non-null rank never clobbered, null filled once by a
    later non-null (overrides "first-supplied wins"); `RecordFollowThroughAsync(
    correlationId, filePath, int? servedRank = null, ct)` + `QualityTools`
    passthrough; rank guard `>= 1` (`ArgumentOutOfRangeException`, no upper bound —
    result-set size unknowable at write time); no `section` qualifier (deferred;
    rank-only under `kind=both` is section-ambiguous by design, stated in the tool
    description).
11. **P5 expected outcome: record pooled semantics + pin absence, no split**
    (d-393 SHOULD-4, d-394 S8): with zero production `GetMetricsAsync` callers the
    blended-vs-split debate is moot; split only on a named new consumer. P5 also
    records zero `session_id` readers and the correlationId-only-keying ruling
    (d-392 S4: recorded, out of scope unless the audit finds a live cross-project
    grade).
12. **Grade-guard default** per ruling 3 (see above).
13. **Cite hygiene:** every planner-report line number re-grepped at build
    (proven stale); plan's own cites use ranges (`SearchDispatcher.cs:44-53`,
    push paths `:76,107,184`).

## Package 1 — session_id decision + plumbing

- **P1.0 decision: CLOSED by owner gate 2026-09-03** (form `docs/work/2026-09-03-session-gate-review.html`,
  feedback `docs/work/2026-09-03-session-gate-feedback.md`, D1 CHANGE). Candidate A wins **as
  REQUIRED**, not optional: optionality is removability, and every agent has a session.
  B/C stay rejected (ambient: no source, wrong principal; heuristic: silent corruption).
  Criteria held: Core infra-free, MCP thin (alongside-params, never tool-body resolution —
  d-394 S7). Re-grep session-concept drift at build.
- **P1.1/P1.2 plumbing** per ruling 2/4 (alongside-params, named-args tests).
  Spies gain dumb `LastSessionId` captures (record-and-return; logic in a fake is
  a tautology veto — d-393 MUST-1); same for `TestData.cs` `NoOpSearchQualityService`.
- **Tests (3 red-proofed):** present→stored verbatim (row-read through the real
  service, not spy-only); blank→rejected fail-fast (mutation: accept whitespace →
  fails); throwing-service-with-session still returns envelope with correlation id
  (mutation: propagate → fails). Contract: wire changes — `ExpectedContract` updated
  red-first (`sessionId` required). BDD scenario restored (ruling 8, passes explicit
  session) +
  `SearchDispatcherTests.cs` in gate (d-394 M6, d-392 S3).
- **Docs:** MoE-note item 2; XML comments; no ADR.
- **Gate:** build + quality/MCP unit + `McpToolContractTests`/`ToolInventoryTests`
  (always-run, d-394 S1) + `SearchDispatcherTests` + BDD corpus; rest to CI.

## Package 2 — strip telemetry from the sync snapshot (parallel with P1)

- **Change:** DROP `search_quality` + `metrics` in `StripNonSyncableAsync` on all
  three push paths, `IF EXISTS`, keeping `application_id = 0` (restore path).
  Rationale recorded: no FTS/shadow tables or triggers on telemetry, so DROP buys
  table-absence + index shed + restore-via-digest-DDL over DELETE (d-393 SHOULD-7;
  resolves the d-390 DELETE disagreement).
- **Tests (each path red-proofed — d-393 MUST-5):** local/merged/retry push
  absence with **populated** tables; oracle = table names absent from
  `sqlite_master` incl. index remnants (row counts cannot distinguish DROP from
  DELETE — d-393 MUST-4; mutation: DROP→DELETE fails). Pull-leaves-local asserts
  **content equality**, not counts (d-393 SHOULD-1). Legacy genuinely-old-shape
  fixture (tables missing, mirroring pre-corpus shape): bare DROP without
  `IF EXISTS` fails (d-393 MUST-4). Encrypted-bank twin (d-392 S9, d-394 S2).
  Flake traits follow precedent: Fast for exclusion-shape, Retry/Slow where the
  template uses them (d-393 SHOULD-6).
- **Docs:** `docs/adr/0095-*.md` (ADR-0014 shape) + README row same commit; flip
  `SearchDispatcher.cs:44-53` privacy comment **and** `MemoryTools.cs:199-201`
  (+`:454-458`) leak witness (d-392 S9).
- **Gate:** exact filters named (exclusion Fast lane + Slow `SyncServiceTests` +
  encrypted), plus `AdrIndexTests`; rest to CI.

## Package 3 — kind-column migration (serial after P1)

- **Change:** v11→v12 bump only (never digest-only — digest edits lack forward
  tripwires and can't backfill). Nullable `kind TEXT` + `CHECK(kind IN
  ('memory','code','both'))`. Rung wording (d-392 M3): `hasTable` early-return
  covers TABLE absence only; column presence must NOT early-return; backfill
  `UPDATE … WHERE kind IS NULL AND created_at < cutoff` runs every entry
  (idempotent by WHERE). Crash-dirty: `BEGIN IMMEDIATE` (v11 precedent) or
  IS-NULL-rerunnable backfill + crash-dirty rerun test, or explicit deferral
  (d-394 S5); duplicate-column catch→re-probe→continue or documented
  single-writer acceptance (d-392 S13). Backfill boundary (d-393 MUST-7):
  cutoff = `git show -s --format=%ct 356afe95` with resolved value cited in the
  rung comment; ties→NULL (honest side); seeds both sides **plus the boundary
  row**; approximation stated openly (skew bounded by #580→#596 window; skew
  mislabels NULL-ward, never wrong-ward — d-392 S11). Fixture carries grades +
  follow-through populated, asserted unchanged (d-393 SHOULD-9). Empty old-shape
  variant required; backfill read avoids Dapper typed materialization on empty
  results (1.33.8 lesson — d-394 M5). No index (revisit bar: `EXPLAIN QUERY
  PLAN` + named consumer, partial preferred — d-393 SHOULD-2).
  `search_quality_eval.json` 5-minute check with both outcomes stated (sensitive →
  fixture+test or new package; insensitive → ADR no-op note).
- **Tests:** column+CHECK behavior test (`kind='bogus'` fails — string-match DDL
  assert insufficient; d-393 MUST-8; state service-guard vs DB-CHECK placement);
  ladder rung; backfill both sides + boundary; staged real-absence test (v11-
  stamped + current-digest + dropped table — plain V1 fixture passes vacuously,
  d-392 S12 + d-394 M5); rerun idempotency; dispatcher forwards kind per
  `SearchKind` (spy `LastKind`); round-trip persists kind. Red-proof minimum:
  backfill-both-sides, rerun, CHECK-rejection. BDD kind scenario restored
  (ruling 8). Literals: **no pin-number updates** — 58 stays, digest
  self-consistent, ahead-version self-updating (d-393 MUST-9).
- **Docs:** `docs/adr/0097-*.md` (backfill rule, nullable choice, no-index
  rationale) + README row same commit; rung doc comment. Merge-order rule with
  P2 (ruling 9 mechanics: P2's 0095 commit merges first or both land atomically
  at P7 — d-394 M4).
- **Gate:** migration/version/DDL-count/stamp-order + kind-tool tests +
  `SearchDispatcherTests` + `AdrIndexTests` + `SyncServiceCodeExclusionTests`
  (+`SyncServiceTests` — P3's digest change can break P2's restore assertions;
  d-394 M7) + BDD corpus; rest to CI.

## Package 4 — rank-aware follow-through without DDL (serial after P3)

- **Change** per ruling 10 (d-390 shape verbatim). Blast radius is one method
  (only `follow_through_files` reader in the repo is the writer itself; probe
  line struck — d-392 S5, d-393 MUST-13, d-394 S4 unanimous).
- **Tests:** legacy-append (old `["a.md"]` upgrades losslessly); rank round-trip
  over **≥2 files with distinct ranks** with section stated per assertion
  (d-393 SHOULD-10); mixed coexistence byte-identical oracle (rewriter fails);
  dedupe per exact rule + `count == distinct paths` secondary; rank guard;
  unknown-id pins **both** paths (ruling 4); no-DDL-change pin. Contract:
  `ExpectedContract` updated red-first (`servedRank:integer|null?` — ruling 7).
  BDD decider + criterion named before P4 merges (not at P7 — d-394 S8).
  Gate: `SearchQualityServiceTests` (Slow) + contract tests (d-394 S4); no
  probe work; rest to CI.
- **Docs:** service contract + codec comments; MoE point-1 closure. No ADR.

## Package 5 — consumer review (read-only; parallel with anything)

- **Audit in writing** per d-386's inventory + d-393 SHOULD-4 additions:
  follow-through/grade projectId-forwarding gap ruled accepted-out-of-scope or
  pinned (stated, not silent); `GetMetricsAsync` zero callers recorded → pooled
  semantics documented + absence pinned, no split; zero `session_id` readers
  recorded (P1 changed no consumer — d-394 S8); kind-blindness of `GetMetrics`
  deliberately pinned (a future change trips a test instead of shifting
  dashboards silently — d-393 MUST-15).
- **Ships pins, not code**, unless a defect → new serialized package behind the
  spine. Grade fail-fast per ruling 3.
- **Docs:** MoE item 6 closure (+ ADR consequences if behavior changed).

## Package 6 — agent-facing surface: skill + docs + tool descriptions (after P1+P4 merge)

Owner-ordered 2026-09-03 (feedback): behavior changes mean nothing if every agent keeps
following stale guidance. First drafts ride the behavior commits (a new param needs some
description for contract tests to pass); this package owns cross-surface consistency.

- **Sweep (verify-then-update, file:line-grounded):**
  a. `.ai-badger/skills/ai-raccoon-memory/SKILL.md` — `memory_search` usage: kind
  default both + what each kind records; correlationId always present
  (grade/follow-through key); sessionId REQUIRED (every agent passes its session id,
  blank rejected); servedRank on follow-through (1-based rank in the serving
  response's section). Drop/replace anything describing the pre-#596 exclusion.
  b. Ripgrep the rest of `.ai-badger/skills/` + `docs/` for
  `memory_search|correlationId|search_quality|memory_record` guidance contradicting
  the new surface; update each hit or record it explicitly accepted.
  c. `docs/reference/agent-memory-server.md` — params, recording rules,
  correlationId, grade/follow-through semantics verified against the shipped tools.
  d. MCP tool descriptions (`MemoryTools.cs` incl. the new sessionId text AND the
  `:454-458` second leak witness — d-396 left it saying "syncs off-machine" while
  `:199-201` says stripped; consolidate both,
  `QualityTools.cs` incl. servedRank, kind/correlationId behavior text): accuracy
  pass — every new/changed param described truthfully; pre-existing text corrected
  where the behavior moved. Descriptions only: no logic, no signatures.
  e. Framework catalog check: does the `~/RiderProjects/ai-badger` checkout ship a
  copy of this guidance needing the same update? REPORT only — framework PRs go
  through `feed-badger` with explicit owner approval, never smuggled into this task.
- **Tests/gates:** no behavior reds (docs package). Gates: `McpToolContractTests` +
  `ToolInventoryTests` green (descriptions pinned) + `dotnet build`; rest to CI.
  Lane report carries a checklist: every new/changed param described; skill updated;
  ripgrep deltas listed as updated-or-accepted; framework-copy verdict.
- **Docs:** the package IS docs; MoE note gets an agent-surface closure paragraph.
- **Files owned:** `.ai-badger/skills/**` (this repo), `docs/reference/*.md`,
  `Description` attributes in `src/AiRaccoon/Tools/*.cs`. Must NOT touch: behavior,
  tests, ADR files, the framework checkout.

## Package 7 (LAST) — integration

- **AC1 (restricted — d-394 S6):** shape-presence + writability + re-strip across
  the legacy→push→restore→repush journey with step oracles named (d-393
  SHOULD-8); backfill-correctness stays with P3's fixture.
- **AC2 (rewritten — d-393 MUST-14):** per-column row assertions (`kind`,
  `session_id`, grade, ranked files each a separate `ShouldBe` — the isolation
  matrix: disable one package → exactly its assertion fails) + `GetMetrics`
  regression (Total/Graded/FollowThrough == 1). No dimension-breakdown
  assertions unless P5 splits metrics.
- **AC3 join review:** `review-tests` on every changed test file against the
  per-file failure-mode × mutation table (d-393 MUST-16: adopt d-390§2 + d-391
  lists with the MUST-6/9/12/13 deltas; no-double-pin scope incl.); BDD green;
  `AdrIndexTests` + DDL-count + version + **`McpToolContractTests`
  final-contract** green (d-392 S10); P7 entry re-greps the three inventories
  (d-392 S7); rest to CI, no local re-runs. P7 rebases onto current main FIRST — main
  moved under the lanes (#600 changed search defaults: new `SearchDefaults.cs`,
  `SearchQuery.cs`/`MemoryTools.cs` defaults, floor contract tests; #599 docs;
  framework refresh): contract + floor + BDD corpus re-run after the final rebase.
- **Docs:** MoE note closed with PR links; ADR consequences updated if found
  anything. **On merge to main:** send a 1:1 bus message to session
  `bee69600-35f0-4af1-9509-962d8b8052e0` (owner order 2026-09-03) stating merged
  commit + what shipped — do not close the task without it.

## Parallelism map (amended)

- **Lane-W serial: P1 → P3 → P4** (collision set += `SearchDispatcherTests.cs`,
  d-394 M6). Signatures per ruling 2; named-args rule for all test callers.
- **Lane-S parallel: P2 + P5-audit** (disjoint; contracts checked at P7).
- **P5-write (if any) sequenced; P6-surface after P1+P4 merge; P7 strictly last.**
- **ADR merge order** (d-394 M4, renumbered 2026-09-03): P2's 0095 commit before P3's 0097 commit, or
  both atomically at P7.
- Flake traits per precedent (d-393 SHOULD-6); exact lane filters named per
  package (Fast exclusion vs Slow sync — d-394 S2).

## Global rules (amended)

TDD with **per-AC witnessed red** (minimum list retired — d-393 MUST-15; P5 pins
need mutation proof too). `review-tests` per the mutation table. Docs ride the
commit. Economy: touched surface once locally + behavior consumers; CI does the
rest. Cite hygiene on all inherited line numbers.

## Open unknowns (carried, implementer resolves)

Session source CLOSED (P1.0 owner gate 2026-09-03: required explicit param); cutoff derivation at build; inventories +
eval-json re-verified at build; rank section-deferral; contract-test constraints
on new optional params; backfill ties→NULL already ruled.
