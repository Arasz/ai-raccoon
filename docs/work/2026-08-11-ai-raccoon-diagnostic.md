# AiRaccoon diagnostic — 2026-08-11

Task: `ai-raccoon-diagnostic`. Check all logs and reports (memory first, then docs), take a
copy of the live store and analyze its content, and report on perceived memory-search quality,
perceived promotion-queue ranking quality, and memory usage per project / per tool.

## Method and evidence

- Store copy: `~/.ai-raccoon/memory.db` (149,708,800 B, WAL mode, live `serve` on 7721, PID
  64668) backed up via the SQLite online-backup API to `/tmp/ai-raccoon-diagnostic/memory-copy.db`
  — WAL-consistent snapshot, `PRAGMA integrity_check` = ok, taken 2026-08-11 ~13:35Z.
  Analysis scripts: `/tmp/ai-raccoon-diagnostic/{schema,analyze,analyze2}.py`.
- Logs: `~/.ai-raccoon/memory-operations.jsonl` (818 ops, 2026-08-06 → 08-11, hermes provider
  channel only), `~/.ai-badger/memory-grade/memory-quality.jsonl` (167 graded-search records),
  `~/.ai-raccoon/{quiet,serve,rollout}.log`, `maintenance-stats.json`.
- Reports: `docs/work/2026-08-09-ai-raccoon-mem-test.md`, `2026-08-10-shared-tier-promotion-report.md`,
  `2026-08-10-ai-raccoon-1-6-3-manual-test.md`, promotion-scoring measurement/round2/round3,
  ADR-0018, ADR-0023, ADR-0025.
- Memory first: `memory_search` surfaced the promotion-report trail, the mem-test report, and the
  memory-quality log path before any file search.

## 1. Logs and reports inventory

| Source | What it holds | Coverage |
|---|---|---|
| `memory-operations.jsonl` | tool ops: ts, tool, project_id, status, duration_ms, agent_id, session_id | hermes provider channel only; 818 ops; 4 tools |
| `memory-quality.jsonl` (+ `pending.json`) | graded `memory_search` calls (usefulness 1-5 + note) | 167 records; only 20 rated; 2 pending |
| `quiet.log` | extraction passes, serve lifecycle | 08-09 14:59 → 08-10 08:26 only (current serve does not append) |
| `serve.log` | serve lifecycle, 7721 | 08-10 08:21 start + shutdown |
| `maintenance-stats.json` | db/wal bytes | single sample 08-09 |
| docs/work reports | mem-test (08-09), shared-tier promotion (08-10), 1.6.3 manual test (08-10), scoring evals (08-09) | committed on main |

**Finding 1 — phantom report path.** Memory entries from the 2026-08-11 11:54 promotion audit
(`@session:default/20260811_115421_28290f`) cite `sourceFile: docs/work/2026-08-11-memory-promotion-report.md`
as "the canonical report". No such file exists anywhere in the repo (verified by file search and
`git log --all`). The audit outcome lives only as two memory rows. A memory entry citing a file
that was never written is a provenance trap for anyone who tries to open the report later.

## 2. Store copy — content analysis

### 2.1 Bank state (vs mem-test baseline 2026-08-09)

| Metric | 08-09 (mem-test) | 08-11 (this copy) |
|---|---|---|
| entries | 13,523 | **14,069** (+546) |
| pending embeddings | 0 | 0 (all rows `embed_state=embedded`) |
| shared tier | 1 | **103** |
| promotion queue | 998 | **1,000** |
| TTL rows | 0 | 0 |
| stale Active workspaces | 8 | 8 (unchanged: acme×2, manual-13x-probe×5, manual-d1d2d3-verify×1) |
| workspaces total | – | 36 |
| watches / watch_files | 7 / – | 7 / 1,038 (jsaa 461, ai-badger 292, ai-raccoon 197, arasz-home-page 88) |
| db size | 142.4 MB | 149.7 MB |

- Queue orphans: **0** (ADR-0023 delete-trigger + 1.6.0 restore holding; was 19 on 08-08).
- All queue rows are scorer v2; oldest queued 08-09 (~41 h), avg wait 27–33 h per project.
- Settings: `access.mode=full`, `embedding=local:bundled`, `extract.enabled=true` **mode=propose**
  (nothing auto-promotes), `extract.exclude.prefixes=hermes/`, sweep on, vacuum 1d,
  `ingest.scope.global=/Users/arasz/RiderProjects`, per-project scopes for jsaa/ai-raccoon/
  ai-badger/arasz-home-page (incl. `.claude/.../memory` dirs).
- Structure modality: 5,258 rows carry `heading_path`; **all 5,258 (100%) also carry
  `structure_embedding`** — Wave 6 backfill complete for eligible rows; the 37 % overall coverage
  is just rows without headings (manual writes, session transcripts), not a defect.
- Shared-tier formats: 44 value-addressed (`shared/<sha256(value)>.md`, post-1.6.3) + **59
  path-addressed rows left over from the pre-1.6.3 file-addressed format** (08-09 promote-mode
  era). Never migrated.
- Shared-tier growth: 08-09: 68 rows · 08-10: 30 (matches the curation report) · 08-11: 5.
  `sync_meta` empty (no cloud sync configured); 32 stale `sync_tombstones` from a past attempt.
- Access stats: only 1,170/14,069 rows (8.3 %) ever accessed; 12,789 total accesses; avg rating
  0.54 — nothing near sweepable, and 0 TTLs, so the reaper has nothing to do.

### 2.2 Memory usage per project (project scope)

| Project | entries | shared | queue | watch_files |
|---|---|---|---|---|
| jsaa | 8,037 | 53 | 279 | 461 |
| ai-raccoon | 3,111 | 24 | 278 | 197 |
| ai-badger | 1,764 | 12 | 278 | 292 |
| arasz-home-page | 829 | 10 | 154 | 88 |
| hermes-default | 214 | 4 | 11 | – |

(+ 8 custom-scope rows, 3 workspace rows, 3 NULL-scope.) vs 08-09: hermes-default grew the most
relatively (95 → 214, +126 % — the hermes provider's session-memory channel); jsaa +250 (watch
re-ingests: 08-10: 532, 08-11: 291 rows); ai-badger unchanged.

Growth per day (project scope): 08-05: 374 · 08-06: 179 · 08-07: 2,388 · 08-08: 8,908 ·
08-09: 1,112 · 08-10: 609 · 08-11: 385. No churn signature (per-path chunk counts == distinct
hashes).

### 2.3 Memory usage per tool

`memory-operations.jsonl` records **only the hermes provider channel** — the bridge (Claude etc.)
and HTTP tool calls are not logged, so per-tool usage is partial by design.

| Tool | ops | share | p50 ms | p95 ms | max ms |
|---|---|---|---|---|---|
| memory_search | 430 | 52.6 % | 105 | 1,878 | 15,005 |
| memory_write | 311 | 38.0 % | 78 | 591 | 6,861 |
| memory_stats | 62 | 7.6 % | 0 | 26 | 277 |
| memory_share | 15 | 1.8 % | 0* | 0* | 0* |

\* share ops record duration 0.0 ms — the provider's share path doesn't time itself.
Errors: 27/818 (3.3 %) — 3 × TypeError (client-side, 0 ms), 24 × AiRaccoonError; burst of 10 on
08-07 10:00–10:02 (server restart window era), 1 × 15 s search timeout on 08-06; 08-10: 1, 08-11: 0.
Ops per day: 167/111/74/201/155/110. Session ids `s1` in early rows are placeholders.

The other 19 tools (ingest, watch, workspace, ttl, delete, promotion, sweep, sync) are exercised
through the bridge/CLI but leave **no per-tool usage record** — only entries-side traces
(`agent_id` column: hermes-default 216, claude-moe-review 11, integration-maintenance 4,
claude-fable-otlp-task 4, claude-code-orchestrator 4, …) and quiet.log extraction lines.

## 3. Perceived memory search quality

**Graded log (memory-quality.jsonl):** 167 searches logged since 08-05, but only **20 rated
(12 %)** — grading fatigue: 08-10: 1/37, 08-11: 6/59. Of the rated: 9×5, 9×4, 1×3, 1×2, 0×1 —
**avg 4.3**. Per project: jsaa 4×5.0, ai-raccoon 5×4.0, ai-badger 1×4.0, arasz-home-page 1×4.0.
Notes show decisive hits ("recovered prior Google OAuth work", "surfaced jsaa as the project id",
"found the plan + status") and the two weak spots:
- u=2 08-06: "no sqlanalyze knowledge in bank; top hits were unrelated **session transcripts**
  (low precision)" — negative signal still served the memory-first gate.
- u=3 08-06: "surfaced ConfigCommands lead, **rest session-transcript noise**".

**Live probes (this diagnostic):**
- ai-raccoon, "What does ADR-0020 decide about the loopback token gate?" → top-5 all relevant
  (design plan, ADR-0020 chunk, regression report); no header-first miss. Score: 5.
- jsaa, "Which CI gate runs on docs-only changes and what does it cost?" → canonical ADR-0016 at
  **rank 3** (two pre-push-gate plan chunks above it); all top-5 gate-relevant. Score: 3–4.
- shared scope, "ai-raccoon watch does not backfill files" → rank 1 = the exact reference note. Score: 5.

**Patterns:**
- The noise source is the **hermes/ session-transcript channel**: 201 project rows + 153 queue
  rows come from `hermes/<session>` transcripts; searches over hermes-default (the hermes bridge's
  default project, 214 rows) mix session dumps into results. The graded u=2/u=3 hits are exactly this.
- `memory_search` returns queue meta on every call (project-scoped: hermes-default shows
  `used:11/reserved:200`, ai-raccoon `used:278/borrowing:true`) — a free health signal.

## 4. Perceived promotion queue ranking quality

**Scorer pedigree (ADR-0018 + round 2/3):** live scorer is v2 per DB (`scorer_version=2`).
Round-3 tournament (08-09, 406 jury-labeled rows): the incumbent scored **+0.602 holdout / +0.710
vs owner labels** — "at the level of an independent expert rater". Winner A (recentred evidence +
refitted priors + portability term) scored **+0.683 holdout / +0.720 vs owner-57**, with ADR bias
falling from **+1.35 to +0.03**. PR #249 ported winner A, and it is the scorer v2 in release 1.6.4.
The earlier "never shipped" wording was stale and is corrected by the owner gate in
`docs/work/2026-08-11-round3-owner-gate-feedback.md`.

**Curation verdicts:**
- 08-10 report (top-100 reviewed, 30 promoted, 27 discarded): *"the score is a decent first
  filter (high precision at the top), but it cannot tell durable lessons from status chatter —
  content review changed the selection in ~30 % of cases."*
- 08-11 audit (11:54 session): 16 intended promotions + 1 accidental; **only 5 new shared rows
  were actually created** — the other ~12 returned `shared:true` because `memory_share` is
  idempotent and reports `{shared:true}` unconditionally (known, 1.6.3 manual test), so the audit
  over-counted re-promotions of already-shared content. One unknown-hash refusal; no discards.

**Live queue audit (this diagnostic, 1,000 rows):**
- **Top-50 by score: 19 rows (38 %) whose exact value is already in the shared tier** (38/1,000
  bank-wide). Extraction re-queues already-shared chunks — the queue's own top candidate (4.00,
  ADR-0020 framing) carries the `superseded` reason tag.
- **19/50 top rows carry noise tags**: `changelog-entry` (213 rows bank-wide), `mid-sentence`
  (371), `status-vocabulary` (50), `superseded` (19), `ephemera` (46); 78 rows from
  `docs/work/archive/`, 153 from session transcripts/.claude memory. The 3.0–3.5 band is full of
  mid-sentence doc fragments, in-flight status notes, code-internal facts ("PromotionQueueMetrics
  has SEVEN instruments", "counter sentinel multi") and stale-prone pricing notes — the exact
  classes the 08-10 curation rejected, now re-queued.
- Genuinely new durable candidates in the top-50: roughly a third (e.g. PeachPDF determinism,
  badger_lib import cost, both shared on 08-11 but still queued).
- Score distribution: ≥3.5: 8 · 3.0–3.5: 77 · 2.5–3.0: 474 · 2.0–2.5: 373 · <2.0: 68. The top
  band is small; the long tail is the same status/archive chatter the 08-10 pass described.
- Capacity: 4 of 5 projects borrow over the 200 reserved (278–279 vs 200); only hermes-default
  (11) is under. Oldest wait 41 h; extraction runs every 30 min in propose mode, so the queue
  only grows and nothing promotes.

## 5. Findings (evidence-backed)

1. **Phantom report path** — the 08-11 promotion audit's "canonical report" file was never
   created; only memory entries cite it.
2. **Queue top is ~⅓ already-shared, ~⅓ previously-rejected noise** — extraction re-queues
   promoted AND discarded content (no exclusion of shared values, no discard memory); `shared:true`
   idempotency hides re-promotion (08-11 audit over-counted 17 → 5).
3. **Scorer ceiling known; lane A is shipped** — round-3 winner A removes the ADR bias and beats
   the incumbent on owner labels; the live queue still shows the ADR/durable-language bias
   (superseded ADR chunk at score 4.0), but queue hygiene confounds a direct scorer judgment.
4. **Search quality is perceived good (avg 4.3) but the evidence base is thin** — 88 % of
   searches ungraded; the two weak grades point at session-transcript noise (hermes/ channel),
   which also feeds 153 queue rows.
5. **59 path-addressed shared rows from the pre-1.6.3 format were never migrated** — mixed
   formats in the shared tier; the 1.6.3 manual test verified only new-format writes.
6. **Per-tool usage is only measurable for 4 tools on one channel** — 19 tools leave no
   per-tool record anywhere (ops log is hermes-provider-only; agent_id column is the only
   entries-side attribution).
7. **Debris unchanged since 08-09**: 8 stale Active workspaces, 32 sync tombstones with no sync
   configured, early ops-log placeholder session ids (`s1`).
8. Health positives: 0 queue orphans, 0 pending embeddings, 100 % structure coverage of eligible
   rows, errors trending to zero (08-10: 1, 08-11: 0), extraction mode stays propose (no
   auto-promotions since the 08-09 promote-mode window — the 08-09 evening's 68-row shared burst
   is curation sessions (rows created 14:40–21:33; the extraction pass at 17:30 reported "0
   promoted" in propose mode and mem-test saw shared=1 at 20:06, so the burst came from manual
   promotion, not the hosted service).

## 6. Recommendations

1. Decide the phantom report: materialize `docs/work/2026-08-11-memory-promotion-report.md` from
   the audit memory entries, or fix the memory entries' sourceFile.
2. Queue hygiene (highest leverage): exclude already-shared values from propose (or mark
   `already-shared` in the row), and persist discards so extraction does not re-queue rejected
   rows — the 08-10 pass becomes meaningless the moment extraction re-runs. This is a code change
   with a clear gate: re-run the top-50 audit and expect ~0 already-shared and ~0 re-discarded.
3. The owner gate approved retaining lane A, including recentred evidence, refitted priors, and
   portability limited to the six considered-document channels. It deferred measurement-prior
   retuning from the two owner-labeled rows and approved judging scorer quality only after queue
   hygiene; see `docs/work/2026-08-11-round3-owner-gate-feedback.md`.
4. Raise grading coverage: the 12 % rate makes "perceived quality" a 20-sample opinion. Options:
   prompt less (aggregate per-day), grade in-session (dogfood rule), or auto-grade with the
   retrieval-harness metrics.
5. Migrate the 59 old-format shared rows (re-addressing to value format) or document them as
   legacy; note that `shared//<path>` rows also fail the "path = shared/<hash>.md" convention
   consumers may assume.
6. Log tool calls from all channels (the bridge) into memory-operations.jsonl, or state in docs
   that per-tool usage is hermes-provider-only — the current report's tool table is partial and
   easy to misread as global.
7. Sweep the 8 stale Active workspaces + 32 tombstones (one-off cleanup, harmless but permanent
   noise in every workspace listing).

— Evidence files: `/tmp/ai-raccoon-diagnostic/memory-copy.db` (integrity ok),
`analysis-output.txt`, `analysis-output2.txt`. All counts re-derivable from the copy.

## Resolution (2026-08-11, task mem-cleanup, PR #259)

Recommendations 5–7 are closed by `docs/work/2026-08-11-mem-cleanup.md`:

1. **Rec 5 — shared-row migration done.** All 59 legacy `shared//<path>` rows were
   re-addressed to the value format (`shared/<sha256(value)>.md`, hash recomputed) on the
   live bank via `scripts/migrate-shared-legacy-rows.py` (dry-run + apply + verify; backup
   at `/tmp/mem-cleanup/pre-migration.db`). The tier is 103/103 value-addressed,
   `integrity_check` ok, formula holds on every row. Documented in ADR-0007.
2. **Rec 6 — file logging removed, not widened.** Owner decision: remove memory logging to
   file from ai-raccoon and ai-badger (approach changes later). The provider's
   `memory-operations.jsonl` writer (`MemoryOperationLog` + `AIRACCOON_MEMORY_LOG`) is
   deleted from `integrations/hermes/ai-raccoon/` and the live plugin; the file was
   deleted (backup at `/tmp/mem-cleanup/memory-operations.jsonl.bak`). ai-badger's
   `memory-grade` feature (`memory-quality.jsonl`/`pending.json`, `AI_BADGER_MEMORY_GRADE`)
   is removed in its repo (PR #373). Per-tool usage is no longer recorded anywhere —
   the tool table in this report is historical (hermes-provider-only by construction).
   The search-quality-metric plan is the replacement.
3. **Rec 7 — debris swept.** The 8 stale Active workspaces (acme ×2, manual-13x-probe ×5,
   manual-d1d2d3-verify ×1) were discarded via `memory_workspace_discard` and their rows
   deleted (3 workspace entries removed with them); the 32 `sync_tombstones` rows deleted.
   `workspaces` is now 24 Closed + 4 Discarded; `sync_tombstones` 0; integrity ok.
