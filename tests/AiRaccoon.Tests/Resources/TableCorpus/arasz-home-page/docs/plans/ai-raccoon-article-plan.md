# AiRaccoon Introduction Article — Writing Plan

**Goal:** A first-person engineering introduction to **AiRaccoon** (open-source MCP agent-memory server the author built), published on arasz.me as `content/articles/<slug>.md`. The article's spine is the measured story: baseline retrieval 60% → structure-aware search 6/6 section hits, a per-query-α mechanism that collapsed under measurement, an FTS "fix" that regressed the status quo, and an embedding benchmark that says the 21 MB local model is good enough.

**Writer:** one persona (technical expert + writer + redactor loop per `technical-article-writing` skill). **Style reference (read first):** `content/articles/code-review-graph-review.md` — first sentence is the hook, first person, numbers dated as snapshots, honest caveats, tables for comparisons, punchy last line, no engagement bait.

**Target length:** 10–15 min read (~2,200–3,000 words). Not a tutorial, not a spec dump (see Out of Scope).

---

## 0. Target article metadata (frontmatter)

| Field | Value |
|---|---|
| `id` | `12` (next after 11 — verified: ids 1–11 exist) |
| `title` | From Section 1 candidates (pick one) |
| `slug` | `introducing-ai-raccoon` (or matching chosen title) — must be unique |
| `publishedAt` / `updatedAt` | Same ISO timestamp, e.g. `2026-08-05T10:00:00Z` (updatedAt until real edits) |
| `author` | `"Rafał Araszkiewicz"` |
| `description` | ~25 words: what it is + the measured claim, e.g. "AiRaccoon is a local-first MCP memory server for AI agents — encrypted SQLite, hybrid FTS5+vec0 search, project/workspace isolation, cloud sync. Built on benchmarks: 60% baseline recall, 6/6 with structure-aware search." |
| `tags` | `[AI, Agents, MCP, Open Source, .NET, SQLite, Developer Tools]` |
| `status` | `draft` (flip to `published` only after build+lint pass) |
| `category` | `engineering` |

No `readingTimeMinutes` field — the site computes it. Site block syntax available: tables with `<!-- caption: ... -->` / `<!-- rowHeaders: true -->`, `<!-- CHART: id -->` (kinds: bar-horizontal, bar-vertical, donut, scatter, timeline; monochrome only), `<!-- DIAGRAM: id -->` (kind: flowchart, node:/edge: lines), `<!-- DETAILS: label --> … <!-- /DETAILS -->`, callouts (`> **Note:**`), code blocks, `<!-- IMAGE: -->` blocks (not needed here).

---

## 1. Article title candidates

Pattern from the site's best articles: `Project: what it does / what the data says` (e.g. "code-review-graph: When Your AI Agent Needs a Map of Your Codebase").

1. **AiRaccoon: When Your AI Agent Needs a Memory Bank** (safe, matches site pattern)
2. **AiRaccoon: Agent Memory That's Local, Scoped, and Benchmarked** (feature + data promise)
3. **AiRaccoon: An MCP Memory Server Where Measurement Beat the Plan** (the spine; most distinctive)
4. **Agent Memory, Measured: Building AiRaccoon's Structure-Aware Search** (data-first)
5. **AiRaccoon: 60% Baseline Recall, 6/6 After Structure-Aware Search** (most data-driven; numbers must stay accurate through review)
6. **I Built an MCP Memory Server. The Benchmarks Changed What It Is** (personal, honest)
7. **AiRaccoon: Local-First Agent Memory with a Search Engine That Listens to Structure** (feature-forward)

**Recommendation:** #3 with #5 as the SEO/description angle. All candidates are short, specific, and promise data — avoid "A Comprehensive Look at…".

---

## 2. The narrative spine (one paragraph the writer must internalize)

AiRaccoon is a local-first MCP server that gives AI agents persistent, project-scoped memory: one managed .NET SQLite bank per install scope, hybrid FTS5+vec0 search with reciprocal rank fusion, project partitioning with a shared promotion tier, workspace sandboxes, rating/degradation sweeps, opt-in cloud sync, optional AES-256-CBC at-rest encryption, access modes, file watchers, 19 MCP tools, 185+ tests, shipped as a NuGet tool with a single dotnet CLI config channel. The story is **not** the feature list — it's that every interesting decision was settled by measurement: the baseline (60% match at rank ≤3 on 35 queries) was bad enough to demand a fix; the dual-vector structure signal (content + heading-path embeddings fused at fixed α≈0.5) is the only thing that delivers section-targeted retrieval (6/6 vs 1/6 for FTS); the clever per-query α machinery collapsed (sigmoid ≡ fixed blend — the confidence signal is query-invariant); and the plan's cheap FTS query-construction fixes **regressed** 6/7 → 3/7. Plus an embedding benchmark: the bundled 21 MB local model (MRR 0.836, ~9 ms/query) matches 334–639 MB served models (0.854–0.858, 37–90 ms) on finding the right memory first. Honest framing throughout: what's shipped vs what's research/roadmap (especially secrets — Section 5.1).

---

## 3. Section-by-section outline

Each section: **Hook** (the sentence that opens it) · **Angle** (one line) · **Must include** · **Acceptance criteria** (all must be true for review sign-off). Sections can be reordered/merged by the writer only if the spine survives.

### S1 — The problem: agents forget everything
- **Hook (first sentence of the article):** an agent that just spent 200k tokens understanding your project remembers none of it tomorrow.
- **Angle:** every coding agent re-reads, re-derives, and re-forgets; persistent memory is the missing layer, and "persistent" must mean local, scoped, and safe.
- **Must include:** one concrete pain moment (the author's own multi-agent workflows, per style ref); one sentence defining what an MCP memory server is; the promise AiRaccoon makes.
- **Acceptance criteria:**
  - [ ] First sentence is the hook — no preamble, no "in today's world", no definition-of-MCP lecture longer than one sentence.
  - [ ] Pain is concrete and first-person, not generic ("agents re-read files" with a number if possible).
  - [ ] No engagement bait, no rhetorical questions as section openers elsewhere.

### S2 — What AiRaccoon is
- **Hook:** "AiRaccoon is an MCP server that gives agents persistent, project-scoped memory backed by a managed .NET SQLite store — local-first by default."
- **Angle:** one-bank-per-install-scope (user `~/.ai-raccoon` vs project), 19 tools (16 memory + 3 watcher) + 2 prompts, stdio default / opt-in HTTP, .NET 10 + ModelContextProtocol SDK 2.0.0, shipped as NuGet tool `ai-raccoon`, zero-config `.mcp.json`.
- **Must include:** the 3 launch flags (`--transport`, `--data-root`, `--install-scope`); the zero-config JSON snippet (≤6 lines); a compact "tools I use most" table (write/search/workspace/share/sweep/sync/watch — ~6 rows, link the full 19-tool contract to `docs/reference/agent-memory-server.md`).
- **Acceptance criteria:**
  - [ ] Every claim matches README.md verbatim (tool count, SDK version, package id, flags).
  - [ ] One code block max in this section (the `.mcp.json` snippet); no architecture dump.
  - [ ] Reader who has never heard of MCP can follow; reader who has isn't bored.

### S3 — Isolation: projects, a shared tier, and workspace sandboxes
- **Hook:** "Memory that every project can read is noise; memory that no other project can read is amnesia."
- **Angle:** three native isolation mechanisms doing three different jobs — `project:<id>` partitioning inside one bank, the flat `shared` promotion tier (via `memory_share`, exempt from degradation sweeps), and `workspace_id` sandboxes (outbox semantics: notes never enter committed memory until `memory_workspace_consolidate`; discard is free).
- **Must include:** the context-partitioning table (shared / project / workspace / custom — scope, synced?, swept?) from `docs/explanation/architecture.md`; the CHECK constraint that enforces workspace↔scope exclusivity (one line, not DDL); workspace lifecycle (begin → status → consolidate/discard, crash-safe `Active` row).
- **Acceptance criteria:**
  - [ ] The 4-context table carries `<!-- caption: -->` and matches architecture.md's table.
  - [ ] Reader can explain when to use a workspace vs a project after reading.
  - [ ] "Shared is exempt from sweeps" stated explicitly.

### S4 — Search: hybrid, structure-aware, and measured (the spine)
- **Hook:** "The baseline said my search was wrong 40% of the time. That was the easy part of the story."
- **Angle:** FTS5+vec0 fused by RRF (default `rrfK=60`, 1:1 weights) was the status quo; the baseline measured 60% match at rank ≤3 (6/10 expected-source on 35 queries, seeded 681 chunks / 166 files, jsaa docs corpus); the fix that worked is the structure signal — a second embedding of the heading path fused at fixed α≈0.5 (`score = α·content + (1−α)·structure`), which lifted section-targeted retrieval to 6/6 vs 1/6 for FTS and MRR(section) 0.37 → 0.46–0.56.
- **Must include:** baseline table (category breakdown: ADRs 43%, Invariants 100% — with caption); the comparison table of arms (content-only 4/6, structure-only 1/6, fixed-α 6/6, F1 FTS 6/7 file / 1/6 section, F2 3/7, F3 3/7); the honest cost (2× vector storage, ~81 s / 0.8 GB to embed 6,675 chunks); the corpus caveat.
- **Acceptance criteria:**
  - [ ] **Corpus discipline:** every number names its corpus and query set. The 60% is the 35-query baseline set; the 6/6 is the 7-query A1–A7 set on the 6,675-chunk harness corpus. Never juxtapose them without naming both (see 5.2).
  - [ ] Numbers match `baseline-retrieval-report.md` and `docs/work/2026-08-04-dual-vector-vs-plan-findings.md` (both re-read at write time).
  - [ ] "Structure is a fuse-able signal, not a standalone ranker" (structure-only = 1/6) is stated — it's the honest caveat that makes the section credible.
  - [ ] The bm25 source/section weighting (1.0/8.0/16.0) gets one line if mentioned — verify value first.

### S5 — What measurement overturned (the "plan was wrong" section)
- **Hook:** "My plan predicted the cheap fixes would win. The measurement disagreed — and it was right."
- **Angle:** two dead ends, both falsified by the harness: (a) per-query α via sigmoid confidence voting is query-invariant (confidence max−mean of structure sims ≈ 0.39–0.49; per-query α ≈ 0.58 always) — sigmoid T=0.1/0.5 is **identical** to fixed α=0.5 on every metric, so the machinery collapsed and the shipped design is a fixed-weight fusion with a tunable constant; (b) the plan's FTS query-construction fixes (stopword stripping, AND-for-short, bigrams) **regressed** file hit@5 from 6/7 (status quo F1) to 3/7 and MRR(file) 0.75 → 0.43, with 11 of 35 queries zero-matching.
- **Must include:** the "what actually shipped instead" line (fixed α + AND-with-OR-fallback on zero-match + FTS source column as the structural fix); the pre-registered win rule (≥2 section flips or MRR Δ ≥ 0.1 = win, else tie) so readers see it wasn't cherry-picked; the methodology DETAILS block lives here or in S4.
- **Acceptance criteria:**
  - [ ] The article's own claims never contradict the findings doc's verdict (α machinery does not earn its place; Wave-1 FTS fixes do not ship as specced).
  - [ ] "Measurement overturned the plan's prediction" is the emotional beat — one clear sentence, not buried.
  - [ ] No claim that per-query α was "removed from a shipped release" without checking git history (it may have shipped as fixed α only; verify).
  - [ ] 7-query low power is admitted (limitations line: "7 queries is small; the pre-registered rule and per-query tables mitigate").

### S6 — Embedding models: the 21 MB local model wins
- **Hook:** "Do you need a bigger embedding model? Measured answer: the smallest one is good enough for most uses."
- **Angle:** benchmark on 174 real documents / 68 judged queries: all-MiniLM-L6-v2 (in-process, ~21 MB) MRR 0.836 at ~9 ms/query vs EmbeddingGemma-300m 0.858 at ~37 ms vs Qwen3-0.6b 0.854 at ~90 ms; served models only win nDCG@10 (0.70 vs 0.61) — top-10 ordering, not first-hit.
- **Must include:** the 4-column model table (Model / Size / MRR / Speed) with caption; the recommendation verbatim in spirit: start local, move to served only if your corpus proves insufficient — you trade 4–10× latency and 15–30× disk for top-10 ordering.
- **Acceptance criteria:**
  - [ ] All six numbers match `docs/reference/embedding-benchmark.md` (R@5 0.325/0.343/0.326, nDCG 0.607/0.704/0.606 optional).
  - [ ] The "what each column means" is one sentence each max (MRR, nDCG) — the doc has full definitions, don't duplicate.
  - [ ] The recommendation is actionable (one command: `ai-raccoon model set local` / bundled default).

### S7 — Security & secrets (honesty section — see 5.1)
- **Hook:** "The security story has a shipped half and a researched half. This article only claims the shipped half."
- **Angle:** encryption at rest is opt-in (one env var `AIRACCOON_DB_PASSPHRASE` → AES-256-CBC page-level encryption via e_sqlite3mc; FTS5 and vec0 work unchanged; without it the bank is plaintext); secrets (OpenAI key, S3 keys, Azure connection string) live in the settings table — encrypted at rest when a passphrase is set — entered via interactive prompts (never argv; empty input aborts), or `--cli` machine-credential chains (DefaultAzureCredential / AWS default chain) that persist nothing long-lived; `sync show` redacts.
- **Must include:** the "what is NOT shipped" callout: keychain/SSH-key/Bitwarden/Azure-Key-Vault/AWS-Secrets-Manager key sources exist as **research records and owner decisions** (`docs/work/2026-08-05-*`), not as code — phrase as roadmap, or omit entirely if the writer can't keep it honest; the SECURITY.md threat-model line ("the dangerous direction is the client that launches the process"; ro/rw/full is defence-in-depth); the honest repo caveats (no CI secret scanning yet, one-maintainer).
- **Acceptance criteria:**
  - [ ] **No sentence implies a shipped vault/keychain integration.** If "secret store integration" appears at all, it must read "researched for a future release" or be dropped (see 5.1 gate).
  - [ ] "Without the passphrase the bank is plaintext" appears verbatim-ish — no overclaiming security.
  - [ ] Interactive-prompt and `--cli` chain claims match README (prompts on stderr, stdin input, exit 1 on empty; `--cli` stores only non-secret markers).
  - [ ] Env-var-only claim verified against `src/AiRaccoon.Infrastructure/Sqlite/` (EnvEncryptionKeyProvider is the sole provider at write time).

### S8 — Access control: ro / rw / full
- **Hook:** "Not every tool that can reach the bank should be able to delete it."
- **Angle:** three tiers (ro = read, rw = read+write default, full = destructive: delete, sweep, forget), global default + per-project overrides (`*` wildcard, more specific wins), enforced by MemoryAccessGuard; the file-watcher mirror runs regardless of tier.
- **Must include:** a 3-row table (tier / what it allows / example) with caption; the override-precedence rule in one sentence.
- **Acceptance criteria:**
  - [ ] Tier semantics match README exactly (full = "adds destructive operations"; watcher note included).
  - [ ] Table is small enough to be a table (3 rows), not prose.

### S9 — Cloud sync: S3 and Azure Blob
- **Hook:** "Sync is the correlation point between your machine-wide memory and a project's — and it's off until you turn it on."
- **Angle:** opt-in `memory_sync` pushes/pulls VACUUM-INTO snapshots with If-Match conflict detection (last-writer-wins with a guard, not a merge protocol); provider-selected via `sync add s3|azure`; credential modes table (prompted keys vs `--cli` chains); Azure needs the container pre-created; sync show redacts secrets.
- **Must include:** the credential-modes table from README (4 rows: Azure conn-string / az CLI / S3 keys / AWS chain — what's stored vs credential source) with caption; the "workspace and shared contexts sync; workspace scratch never does" nuance if it fits in one line.
- **Acceptance criteria:**
  - [ ] If-Match conflict description is accurate (guard, not merge; verify `SyncService` behavior in code or ADR docs before writing "last-writer-wins").
  - [ ] No claim of encryption-in-transit beyond what README/SECURITY.md support.
  - [ ] Container-must-exist caveat included (it's a real footgun, and honest).

### S10 — Configuration, operations, observability
- **Hook:** "The server reads exactly one environment variable. Everything else goes through the CLI."
- **Angle:** the dotnet CLI is the single config channel (verb commands against the bank; the running server hot-reloads rows; `--help` everywhere); sensible defaults (rw access, local 21 MB model, fixed α, RRF 1:1, sweep threshold 0.3 / age 30 days, watch concurrency 4, disabled-until-enabled watchers); OpenTelemetry metrics on all 19 tools (`ai_raccoon_tool_invocations`, `ai_raccoon_tool_duration_ms`, ActivitySource `AiRaccoon.MemoryTools`; live `dotnet-counters` monitor, no OTLP export yet); tests: 185+ cases (reconcile — see 5.2.6).
- **Must include:** a compact verb-command block (`ai-raccoon access/model/retrieval/sweep/sync/watch …`, ~8 lines); one sentence on hot-reload; the honest "no OTLP export yet" line.
- **Acceptance criteria:**
  - [ ] Command names/args match README's usage section exactly.
  - [ ] Test-count sentence passes the 5.2.6 gate (real number, dated).
  - [ ] Defaults stated only if verified (see 5.3): α default, rrfK=60, sweep threshold 0.3, age 30 days, watch concurrency 4.

### S11 — Design choices worth stealing (unique features wrap-up)
- **Hook:** "Four decisions I'd make again — and one I'd measure first."
- **Angle:** rapid-fire honest list: rating with half-life decay + access-count multiplier driving degradation sweeps (shared protected — memory that earns attention survives); content-hash dedup; thin-MCP-layer/pure-domain architecture with warnings-as-errors; measurement-first discipline (harness, pre-registered rules, memory caps — the 50 GB OOM that became an 0.8 GB run is a great detail); the fresh-start caveat (current release starts clean; migration deferred — P11).
- **Acceptance criteria:**
  - [ ] No new unverified numbers; each item traceable to README/architecture.md/ADR.
  - [ ] At least one item is a *negative* lesson (α collapse, F2 regression, or the OOM) — the section must not read as marketing.
  - [ ] OOM story verified (findings doc: prior prototype OOM-killed the machine at 50 GB; fixed by keying embeddings by hash + bounded batches).

### S12 — Verdict: when to use it
- **Hook:** "Use it if you're tired of your agents re-reading everything. Skip it if…"
- **Angle:** mirror the style ref's closing structure — "Yes, if you / Skip it if you" bullets; honest limitations (one-maintainer project, no CI secret scanning yet, fresh-start release, 7-query harness, corpus-conditional numbers); one final punchy line (e.g. "Memory is only useful if the agent finds the right piece of it — that part, I measured.").
- **Must include:** repo link (verify URL exists — see 5.2.11), license line (verify type), "built with TDD, 185+ tests" restated with the reconciled number.
- **Acceptance criteria:**
  - [ ] No overclaim ("production-ready", "battle-tested" forbidden — one-maintainer, no tagged releases yet per SECURITY.md).
  - [ ] Final line is short and memorable, no CTA spam.

---

## 4. Tables / charts / diagrams placement plan

| # | Where | Construct | Content |
|---|---|---|---|
| T1 | S2 | Table + caption | "The MCP tools I use most" (~6 rows: write, search, share, workspace_begin/consolidate, sweep, sync, watch) |
| T2 | S3 | Table + caption | Context partitioning: shared / project / workspace / custom (scope, synced, swept) |
| T3 | S4 | Table + caption | Baseline category breakdown (ADR 43%, Invariants 100%, 35 queries total, 60% headline) |
| T4 | S4 | Table + caption | Harness arms: content-only 4/6, structure-only 1/6, fixed-α 6/6, sigmoid T0.1/0.5 ≡ fixed, F1 6/7 & 1/6, F2 3/7, F3 3/7 + MRR(file/section) |
| T5 | S5 | Table + caption (optional, small) | "What the plan predicted vs what measured": 3 rows (per-query α → collapsed; FTS query fixes → regression 6/7→3/7; structure signal → kept, fixed-α) |
| T6 | S6 | Table + caption | Embedding benchmark: model / size / MRR / latency (+ nDCG@10 column if space) |
| T7 | S7 | Callout (not table) | `> **Note:**` — shipped vs researched secret sources (the honest-flag callout) |
| T8 | S8 | Table + caption | Access tiers ro/rw/full |
| T9 | S9 | Table + caption | Sync credential modes (4 rows: what's stored / credential source) |
| T10 | S4/S5 | DETAILS block | Methodology: corpora (681 chunks/166 files vs 6,675 chunks), query sets (35 vs 7 A1–A7), metrics (hit@5, MRR, section = heading-path segment match), pre-registered win rule, 6 GB memcap, scripts paths (`scripts/baseline-queries.json`, `scripts/compare-harnesses.py`) |
| D1 | S3 | DIAGRAM (flowchart) | Context partitioning: one bank → shared / project:<id> / workspace:<id> / custom (4 nodes, edges labeled synced/swept/exempt) |
| D2 | S4 | DIAGRAM (flowchart) | Search pipeline: query → FTS5 (bm25) + vec0 content + vec0 structure → RRF fusion (α=0.5) → ranked results |
| C1 | S4 | CHART (bar-horizontal) | Section hit@5: Content-only 4, Fixed α=0.5 6, FTS status quo 1, Structure-only 1 (monochrome; caption "Section-level hit@5 by search arm (7 queries)") |
| C2 | S6 | CHART (scatter or bar) | Latency vs MRR: local (9 ms, 0.836), gemma (37 ms, 0.858), qwen (90 ms, 0.854) — "the smallest model finds the right memory first for 4–10× less time" |

Rules: tables get `<!-- caption: -->`; CHART/DIAGRAM IDs must be unique site-wide; charts monochrome; nothing inside DETAILS except allowed blocks (no chart/diagram inside DETAILS — build fails).

---

## 5. Facts that MUST be verified against the repo before publishing

### 5.1 — The secret-store claim (highest priority gate)

The user's brief says "integration with secret stores". **The shipped code does NOT contain vault integrations.** Verified 2026-08-05: `src/` contains only `IEncryptionKeyProvider` + `EnvEncryptionKeyProvider` (env var); no Bitwarden/keychain/keyfile provider, no `encryption` config verb in `Setup/`. The `docs/work/2026-08-05-*.md` files are research records (macOS Keychain, 0600 key file, Bitwarden SM via `bws` CLI, SSH-ed25519 derivation, Azure Key Vault, AWS Secrets Manager as key sources) with owner decisions (f: 2026-08-05: env var stays; Bitwarden-via-bws is the owner's chosen source) — **decisions/research, not shipped features**.

Before publishing, the writer MUST:
- [ ] Re-grep `src/` for `Bitwarden|Keychain|KeyFile|bws|EncryptionKeyProvider` — if any provider beyond `EnvEncryptionKeyProvider` exists on the current branch, the article updates accordingly.
- [ ] Check `ai-raccoon --help` / `ConfigCommands.cs` for an `encryption` verb (none found at plan time).
- [ ] Phrase per the approved formula: *"Encryption at rest is opt-in via one environment variable; API keys and cloud credentials live in the settings table (encrypted at rest when a passphrase is set), entered through interactive prompts or machine `--cli` credential chains. OS-keychain, SSH-key, and cloud-vault key sources are researched and on the roadmap — they are not in the shipped binary."*
- [ ] If the writer can't keep that distinction airtight, **cut the roadmap sentence entirely** — a vague "integrates with secret stores" is a publish-blocking error.

### 5.2 — Numbers (re-read each source at write time; date every number as a snapshot)

- [ ] **Baseline:** 35 queries, 6/10 expected-source at rank ≤3 = 60%, 681 chunks / 166 files, generated 2026-08-04 (`baseline-retrieval-report.md`). Corpus = job-search-ai-assistant docs.
- [ ] **Dual-vector harness:** 6,675 chunks (71% docs/work pollution), 7-query primary set A1–A7, section hit@5: content-only 4/6, structure-only 1/6, fixed-α 6/6, sigmoid T0.1/0.5 ≡ fixed-α, T0.8/1.0 MRR(file) 0.743 but no section flips; F1 file hit@5 6/7 MRR 0.750; F2 3/7 MRR 0.429; F3 3/7; cost 81.5 s / ~0.8 GB; per-query α ≈ 0.58, confidence 0.39–0.49 (`docs/work/2026-08-04-dual-vector-vs-plan-findings.md`).
- [ ] **Corpus separation:** never state 60% and 6/6 in the same sentence without naming both corpora/query sets (they are different harnesses).
- [ ] **Embedding benchmark:** 174 docs, 68 queries; local MRR 0.836 / 9.2 ms / ~21 MB / R@5 0.325 / nDCG@10 0.607; gemma 0.858 / 36.8 ms / ~334 MB / 0.343 / 0.704; qwen 0.854 / 90.4 ms / ~639 MB / 0.326 / 0.606 (`docs/reference/embedding-benchmark.md`).
- [ ] **Tools/contract:** 19 tools (16 memory + 3 watcher), 2 prompts, every tool requires `projectId` (`docs/reference/agent-memory-server.md`).
- [ ] **Test count:** README says "185+"; a raw grep of `[Fact]/[Theory]` found 787 attributes — reconcile before publishing (run `dotnet test` on a clean build, cite the real passing number and date it; or quote README's "185+" with "as of").
- [ ] **Versions:** MCP SDK 2.0.0, net10.0, System.CommandLine 2.0.10, package id `ai-raccoon`, `.NET 10 SDK` requirement (README).
- [ ] **Repo URL & license:** LICENSE file exists — confirm the license type (MIT?) before claiming it; SECURITY.md says private reporting "once this repository is hosted" on GitHub — confirm the public repo URL exists and is correct before linking.
- [ ] **Fresh-start caveat:** P11 note — the current release drops existing-bank migration (bank starts clean); if the article mentions upgrading, this caveat is mandatory (`docs/reference/agent-memory-server.md`).
- [ ] **Sync details:** VACUUM INTO + ATTACH merge, If-Match conflict handling semantics (check `src/AiRaccoon.Infrastructure/Sync/SyncService.cs` or `docs/plans/sync-cloud-identity-creds.md` before writing "last-writer-wins"); Azure container must pre-exist; `sync show` redacts secrets; `--cli` stores only non-secret markers (README table).
- [ ] **Search defaults:** alpha default (README: "measured sweep optimum (ADR 0006)"; `agent-memory-server.md`: structureAlpha default 0.5; confirm numeric default in `StructureFusion.cs`/settings defaults); `rrfK=60`, `ftsWeight=1`, `vectorWeight=1`, `minScore=0.7`, `limit=20`; FTS bm25 weights 1.0/8.0/16.0 (body/source/section — verify in `MemorySchema.cs`/search construction); sweep candidate = rating < 0.3 AND age > 30 days, shared exempt, TTL knob removed.
- [ ] **Encryption scheme:** AES-256-CBC page-level via e_sqlite3mc (README); "FTS5 and vec0 work unchanged"; the pinned SQLitePCLRaw advisory (CVE-2025-6965) is an internal detail — include only if discussing the encryption stack's supply chain.
- [ ] **Observability:** instrument names `ai_raccoon_tool_invocations` / `ai_raccoon_tool_duration_ms` / ActivitySource `AiRaccoon.MemoryTools`; "no OTLP export yet; `project_id` plaintext tag".
- [ ] **Architecture claims:** thin MCP layer, pure Domain, Infrastructure SQLite adapter, TreatWarningsAsErrors, central package versions (README "Architecture" section).
- [ ] **OOM anecdote:** prior prototype OOM-killed the machine at 50 GB; fixed by hash-keyed embeddings + bounded batches (findings doc "Memory safety").

### 5.3 — Site-side gates

- [ ] `id: 12` is still the next free id at publish time.
- [ ] Build command confirmed in `frontend/package.json` (task says `npm run build-articles`; pipeline doc also names `scripts/md-to-article.py` — verify which is canonical) and run: `npm run build && npm run test:single && npm run lint` before flipping `status: published`.
- [ ] `BlogPost` entry added to `frontend/src/app/data/blog-posts.data.ts` if the article is to appear in listings.

---

## 6. Out of scope (the article will NOT cover)

- **Not a setup tutorial** — no step-by-step install walkthrough; at most one `.mcp.json` snippet and one command block; point to README.
- **Not a benchmark-methodology deep dive** — metrics definitions and harness details go in one DETAILS block; full methodology lives in the repo docs.
- **No full 19-tool API reference** — top tools table only; link `docs/reference/agent-memory-server.md`.
- **No competitive review** — sqlite-memory appears only as the baseline's method line, not as a reviewed product; no "vs Mem0/other MCP servers" comparisons.
- **No sync-protocol or SQLite-schema internals** — no DDL, no ATTACH/merge mechanics beyond one sentence.
- **No roadmap feature claims** — nothing about keychain/SSH/Bitwarden/Azure KV/AWS SM as shipped; no promises about OTLP export, release automation, or CI.
- **No security audit or threat-model review** — report the SECURITY.md posture, don't re-derive it.
- **No C#/.NET implementation patterns** — chunker internals, o200k tokenizer, Dapper, xunit setup are all out.
- **No pricing or LLM-provider cost analysis** — the embedding benchmark is quality/latency, not cost-per-token.
- **No job-search-ai-assistant feature coverage** — it appears only as corpus provenance.

---

## 7. Writer's checklist (pre-publish)

1. Re-read `content/articles/code-review-graph-review.md` and match its rhythm: hook-first, snapshot-dated numbers, tables with captions, honest caveats, strong close.
2. Re-read the four primary sources: `README.md`, `baseline-retrieval-report.md`, `docs/work/2026-08-04-dual-vector-vs-plan-findings.md`, `docs/reference/embedding-benchmark.md` (+ `SECURITY.md` for the threat-model paragraph).
3. Run the 5.1 secret-store gate and the 5.2 number checks; date every measured number ("measured on 2026-08-XX").
4. Draft with frontmatter from Section 0; `status: draft`.
5. Redactor pass (per `technical-article-writing`): structure, tone, headline, technical accuracy against this plan's acceptance criteria; iterate until clean.
6. Build: `npm run build-articles` (verify exact script), `npm run build`, `npm run test:single`, `npm run lint`; then flip `status: published` and add the BlogPost entry.
