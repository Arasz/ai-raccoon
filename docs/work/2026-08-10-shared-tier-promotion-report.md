# Shared-tier promotion report — 2026-08-10

Curation pass over the ai-raccoon propose tier, done with MCP tools only (`memory_promotion_list` → content review → `memory_share`), 2026-08-10.

## Scope

- Queue at review time: **929 candidates** bank-wide (across ai-raccoon, ai-badger, jsaa, hermes-default, arasz-home-page).
- Reviewed: the **top 100 by score** with content previews. Below ~2.97 the queue is the same long tail of status notes, archived research and mid-sentence doc fragments.
- Decision rule: **content over score**. A candidate was promoted only if the *content*
  is a durable, cross-project fact/rule/pitfall with rediscovery cost — not because the mechanical scorer liked it. Status notes, in-flight progress, archived research, code-internal drift-prone facts and jsaa-specific design fragments were
  rejected even at 3.0–3.5.
- All promotions verified: every `memory_share` returned `shared:true`; a follow-up
  `memory_search(scope=shared)` confirms the rows in the shared tier.

## Promoted (30)

### ai-raccoon (12)

| #  | Score | Content                                                                                                                                                    | Score sync                                                           |
|----|-------|------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------|
| 1  | 4.00  | ADR-0020 framing: a stdio ai-raccoon process is a peer server (opens the same bank), not a client of `serve`                                               | sync                                                                 |
| 2  | 3.99  | MCP SDK 2.1.0 traps (verified by reflection): `PrependServerInfoFilter` is on the OUTGOING pipeline, not incoming                                          | sync                                                                 |
| 3  | 3.59  | stdio never wires OTLP by design (ADR-0009); dotnet-trace sees telemetry via EventPipe — "Rider shows no traces" is expected                               | sync                                                                 |
| 4  | 3.21  | Workspace is a context, not a flag: workspace rows are invisible to search/stats and degradation                                                           | sync                                                                 |
| 5  | 3.18  | dotnet-monitor: Prometheus yes, OTLP no (verified against MS docs)                                                                                         | sync                                                                 |
| 6  | 3.08  | HTTP serve probe recipe: `POST /mcp` with `Accept: application/json, text/event-stream`, recognized iff status ∈ {400,405,406} and body contains `jsonrpc` | sync                                                                 |
| 7  | 3.08  | Tool-call metrics: `project_id` on the COUNTER only, never the histogram (histograms can't carry high-cardinality tags)                                    | sync                                                                 |
| 8  | 3.07  | Rekey-an-encrypted-bank how-to (banks keyed via `encryption bitwarden` before ADR-0012 need a one-off rekey)                                               | sync                                                                 |
| 9  | 3.07  | `OTEL_SERVICE_NAME` is read but has no effect — `service.name` is a fixed product identity                                                                 | sync                                                                 |
| 10 | 3.01  | Three composition roots exist; a DI wiring change must hit all three or the modes diverge                                                                  | sync                                                                 |
| 11 | 2.97  | SecureString on macOS/Linux is a pinned managed buffer with no encryption — only a shorter plaintext window [READ]                                         | **out of sync** (promoted below 3.0: durable verified security fact) |
| 12 | 3.03  | Workspace rows are stripped before they leave the bank — never synced                                                                                      | sync                                                                 |

### ai-badger (5)

| #  | Score | Content                                                                                                                                                                 | Score sync                                                                         |
|----|-------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------|
| 13 | 3.84  | 0.86.0 learned-skills harvest was a COPY of one private project, not a generalisation; `features/dotnet/` auto-installs on any repo containing a .csproj with no opt-in | sync                                                                               |
| 14 | 3.82  | Three-way skill triplication = generated pipeline with a hard gate, NOT duplication to refactor away                                                                    | sync                                                                               |
| 15 | 3.65  | SECURITY: AWM away-mode gate auto-approves force-pushes and destructive commands, contradicting its own docstring                                                       | sync                                                                               |
| 16 | 3.42  | 20 SKILL.md files carry a DUPLICATE `description:` frontmatter key; skills-lint validates the wrong one                                                                 | sync                                                                               |
| 17 | 2.97  | "Disjoint files are not isolation" — the task skill's file-sharing split taught the failure it was supposed to prevent                                                  | **out of sync** (promoted below 3.0: generalisable multi-agent orchestration rule) |

### jsaa (11)

| #  | Score | Content                                                                                                                                                                         | Score sync                                                   |
|----|-------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------|
| 18 | 3.07  | GH Actions concurrency-group trap: `${{ github.workflow }}` names are only as unique as the workflow name (real incident in nightly.yml)                                        | sync                                                         |
| 19 | 3.04  | Google OAuth: an app in 'Testing' status gets refresh tokens expiring in 7 days unless scopes ⊆ {name, email, profile}                                                          | sync                                                         |
| 20 | 3.04  | QuestPDF HTML rendering: stylesheets are inert; a body-embedded `<style>` prints its text into the PDF [MEASURED]                                                               | sync                                                         |
| 21 | 3.04  | "A work record is never citable as a description of the running system" — docs-governance rule                                                                                  | sync                                                         |
| 22 | 3.02  | azurerm 4.x injects shadowing storage settings; pinning doesn't help; strip script must run after every apply                                                                   | sync                                                         |
| 23 | 3.02  | Never use `TransactionalBatch` for a delete sweep — all-or-nothing, one 404 from an already-deleted item kills the batch                                                        | sync                                                         |
| 24 | 3.00  | `[DurableClient]` on a method parameter → the isolated worker never registers the type → 500 in every real host; unit tests can't catch it                                      | sync                                                         |
| 25 | 3.00  | Vacuous-test traps: wait on a sibling field whose value proves the data landed; an audit matching a pattern production never uses reported "0 drift" over zero production calls | sync                                                         |
| 26 | 2.99  | Bun's native `--bun` flag for Playwright — rejected (reported hang/segfault); skipping E2E entirely also rejected                                                               | **out of sync** (verified tooling incompatibility)           |
| 27 | 2.98  | ai-raccoon watch does NOT backfill: jsaa's docs watch reported Healthy with 25 of 327 files indexed                                                                             | **out of sync** (measured behavior, expensive to rediscover) |
| 28 | 2.98  | happy-dom ignores CSS `content:` — unit suite stays green while real screen readers announce "left bracket save right bracket"                                                  | **out of sync**                                              |
| 29 | 2.97  | Never delegate a backgrounded git push to a subagent — it is killed when the subagent's turn ends                                                                               | **out of sync**                                              |

### hermes-default (1)

| #  | Score | Content                                                                                                                                       | Score sync |
|----|-------|-----------------------------------------------------------------------------------------------------------------------------------------------|------------|
| 30 | 3.56  | PeachPDF 0.9.9 is NOT byte-deterministic: /CreationDate, random 6-char font-subset tags, trailer /ID differ between renders of identical HTML | sync       |

## Score-sync verdict

- **In sync: 21 of 30.** The scorer's top band (3.2–4.0) is dominated by genuinely durable, cross-project facts (ai-raccoon/ai-badger internals, verified external tooling facts), and nearly all of it was kept.
- **Out of sync: 8 promotions at score < 3.0** (items 11, 17, 26–29 plus 24/25 at 3.00). These are exactly the cases the mechanical scorer under-ranks: lessons with high rediscovery cost that don't carry the "organic-note + tech-breadth"
  vocabulary the scorer rewards (test traps, subagent push, watch backfill, SecureString, isolation rule). Promoted on content despite the score.
- **Rejected despite 3.0–3.5 (~25 candidates):** in-flight status/progress notes (OTLP fix plan, "stream closed", CI-hardening in-flight), archived research (sqlite3-rsync, passphrase options, SearchValues-vs-HashSet), code-internal
  drift-prone facts (PromotionQueueMetrics instrument count, counter "multi" sentinel, badger_lib import cost), changelog entries, and jsaa-specific design fragments. The scorer rewards "durable-fact-language" mechanically even when the
  fact is a mid-sentence chunk of an archived doc.
- **Net:** the score is a decent first filter (high precision at the top), but it cannot tell durable lessons from status chatter — content review changed the selection in ~30% of cases.

## Follow-up: discard pass (same day, per owner f:)

Extraction was still enabled (`extract list`: enabled: True, mode: propose, interval: 30 min)
— it would have re-queued every discarded candidate within the hour. Per the documented cleanup order it was disabled FIRST (`ai-raccoon extract enable false`), then the dead candidates were dropped with `memory_promotion_discard`
(per-hash).

**27 dead candidates discarded** (all confirmed `discarded:1`, queue totals 929 → 902):

- ai-raccoon (19): archived research chunks from `docs/work/archive/` (sqlanalyze, sqlite3-rsync x3, sqlite3mc, SearchValues-vs-HashSet, encryption-at-rest, enforce-memory-mcp-first-hooks, db-passphrase-ssh x2, memory-model-evidence,
  http-serve-design x2, memory-usage-audit-v3), status/in-flight notes (OTLP fix plan progress, mem-test x2), code-internal drift-prone facts (PromotionQueueMetrics instrument count, counter "multi" sentinel).
- ai-badger (3): changelog 0.31.0 entry, badger_lib import-cost finding, "no .NET code"
  repo-inventory fact.
- jsaa (5): status/in-flight notes (ci-hardening in-flight, manual-test-feedback stream status, beta-epics backlog snapshot, linkedin preflight checklist), stale-prone Claude-model pricing note.

Not discarded: live-doc chunks, ADRs, design/spec work records and review docs that were rejected for promotion but remain legitimate project knowledge (still queued).

Side effect to be aware of: `extract.enabled.global` is now False — the propose queue will NOT refill automatically until re-enabled (`ai-raccoon extract enable true`). Re-enabling will re-queue eligible rows again, including some of the
discarded ones.

## Left in the queue

- After the discard pass: ~870 candidates remain queued (929 − 30 promoted − 27 discarded). These are the non-promoted live-doc chunks, ADRs, design/spec work records and review docs — legitimate project knowledge, just not shared-tier
  material. A full queue sweep is possible via per-hash `memory_promotion_discard` (no bulk tool) — needs explicit go-ahead.
- Oddity: the PeachPDF entry (30) sits under project `hermes-default` despite being jsaa research — promoted from there anyway; content is what matters.
