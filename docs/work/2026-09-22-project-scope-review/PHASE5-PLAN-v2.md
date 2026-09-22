# AiRaccoon — fix plan v2 (reviewed)

Base `5bca1900` · supersedes `PHASE5-PLAN.md` · incorporates the **gate audit**
(`reviews/plan-gate-audit.md`: 7 gates that were not gates) and the **plan review**
(`reviews/plan-review.md`: R1–R12).

Every change below is a *delta* from v1; unchanged packages keep v1's text. Gate discipline stands:
a gate must be **watched red on the current build** before it is trusted, and must carry a **positive
control** so that suppressing the observable cannot satisfy it.

---

## Corrections to Wave 1

### P1.1 — revised (F6; F49/F39 explicitly deferred)
- **Scope claim fixed (R1).** v1 named F49 and F39; it fixed neither. F49 (token at `<data-root>/mcp-token`
  while bank/log live in `<data-root>/.ai-raccoon/`) and F39 (a mistyped `--data-root` mints a bank) move
  to **Wave 3** as their own packages, or are dropped explicitly — they are not warning strings.
- **The channel does not exist (R7).** `SearchResults`/`SearchDispatchResult` carry only `CodeWarning`,
  and `MemoryTools`' primary constructor has no settings port. The package must: add a memory-leg warning
  field, thread the settings/store into `MemoryTools` (26 test constructions touched), and update
  `tests/…/Unit/Mcp/MemorySearchKindToolTests.cs:272-280` — which today **asserts the red state**
  (`Warning.ShouldBeNull()` for `kind=memory`). Landing the fix without rewriting that test turns a green
  suite red; the rewrite is part of the package, and the old test is the *watched-red evidence*.
- **Add `src/AiRaccoon/Setup/McpServerInstructions.cs:14-25` to the file list (R8).** It is the text every
  agent receives and it pitches hybrid search while naming only the code remedy.
- **Gate (revised).** (a) memory-leg warning test — red today via the existing pin, now the *inverse*
  assertion; (b) positive control: a bank **with** an engine returns no memory warning (so "warn always"
  fails); (c) code-warning regression control. Golden-response churn: audit which committed goldens carry
  warnings *before* the change, not after.

### P1.2 — gates must leave the fake (gate audit + P1.2)
- Drive the **real** `SqlitePromotionQueueStore` + `MemoryWriteService` + `SharedExtractionRunner`
  (harness pattern `Integration/Storage/PromotionQueueDiscardTests.cs:24`). The existing
  `SharedWriteIsAPromotionRequestTests` uses `FakePromotionQueue`, through which both gates pass today —
  a vacuous variant. The gate wording must ban the fake and assert on `promotion_queue` rows.
- (b) asserts **both** the response reason and `count(*) = 0`.

### P1.3 — **withdrawn as written; replaced by an options package** (review R3 + gate audit)
The v1 fix cannot work: `server/discover` is absent from the shipped `ModelContextProtocol` 2.2.0
(0 hits), reading `serverInfo.name` needs a completed handshake (token already sent), and the only
pre-token identity — the `/observability` name — is self-asserted and already squat-able
(`ServerRestart.cs:69-70`). A gate written against `serve --restart` is **already green** (it refuses
foreign names today), so it would have been vacuous.

**Revised package = an owner decision with three implementable options**, each with its own gate:
1. **Private spawn (recommended).** The CLI/proxy never attaches to a pre-existing listener for a root
   it is about to use: it binds its own ephemeral port (or a unix socket with `0600`) and only falls back
   to attaching behind an explicit flag. *Gate:* a squatter on the configured port does not receive the
   token because no attach happens; positive control — the legacy attach path still works when asked for.
2. **Mutual proof.** Attach only after the listener proves possession of the bank (e.g. it must return a
   value derived from the token file), which a squatter that never read the file cannot produce.
   *Gate:* the squatter fails the proof and gets no token; a real backend passes.
3. **Accept and document.** "Loopback port = trusted" is written into SECURITY.md alongside the ADR-0043
   sentence the adversary falsifies. *Gate:* none (documentation change), and the finding stays open in
   the register with its measured evidence.
The shared `ServerProbe` verdict must **not** be narrowed globally — it also serves `serve` attach and
`WaitForPortToFreeAsync`'s `NotListening` requirement; the change belongs at the token-send decisions.

---

## Corrections to Wave 2

### P2.1 — the tombstone statement was wrong in two ways (review R4, R5)
- **Scope filter is mandatory.** The derived set must mirror `ProjectIdsRepair.cs:203-207`:
  `INSERT … SELECT DISTINCT project_id, hash, COALESCE(scope,'workspace'), @now FROM entries WHERE <delete predicate> AND scope IN ('project','custom','shared')`.
  Without it, one `memory_delete` of a workspace/committed twin tombstones the scratch row too, which
  deletes same-hash workspace rows on **other** replicas and pushes the scratch content hash off-machine.
  *This was a data-leak the fix would have introduced — it is now a gate of its own.*
- **Upsert, not `INSERT OR IGNORE`.** The existing writer refreshes
  (`Memory.sql:186-188 … ON CONFLICT DO UPDATE SET deleted_at = excluded.deleted_at`). `OR IGNORE` would
  keep the older `deleted_at`, and under P2.2's `created_at ≤ deleted_at` guard a later deletion could
  fail to suppress a peer's re-created row. The derived statement uses the same upsert shape.
- **Gate (b) rewritten (both reviews).** v1's "both stay deleted" passes on the **broken** build when the
  committed row is probed first. It now asserts the **tombstone set** covers both scopes after the delete
  — an assertion the single-probe bug cannot satisfy regardless of row order — plus the ordering-forced
  variant.
- **New gate:** a workspace write + a committed twin + a peer replica holding a same-hash workspace row —
  after the delete and sync, the **peer's workspace row still exists** and no `'workspace'` tombstone is
  pushed. (Red on v1's design; the reason this correction exists.)
- Gate (d) drives the digest/delete path (`WatchDigestExecutor`) rather than a real `FileSystemWatcher`
  event, to avoid timing flake.

### P2.2 — partial by design, and now stated (review R6)
The `created_at ≤ deleted_at` guard fixes the measured self-delete, but a replica that already holds the
tombstone still suppresses the re-created row through the merge's `NOT EXISTS` leg
(`SyncService.cs:450-455`), which has no age comparison; convergence then depends on tombstone GC in a
*later* pull (`:533-539`). The plan records this as **partial for multi-replica convergence**, and the
gate adds the second replica holding the tombstone as its own case.

---

## Corrections to Wave 3

- **R2 — the retrieval packages now exist.** Add **P3.1 (F19)**: carry the raw content cosine (or mark the
  structure-absent case) so `evidenceByHash.cosine` means one thing — gate: a bank with zero structure
  vectors returns an evidence cosine equal to the stored-blob cosine within 1e-6 (red today: exactly 0.5×).
  Add **P3.2 (F20)**: either exempt an explicitly raised `limit` from the default floor or report the
  truncation — gate: `limit=100` on a ≥70-candidate bank returns 100 or a truncation marker naming the
  floor (red today: 41, no marker). Both are default-firing.
- **R10 — every two-option row collapses to one asserted outcome**, each needing its owner ruling first:
  F24 (refuse **or** workspace wins), F52 (absolute floor **or** an explicit unranked marker), F7 (which
  exit code for a corrupt bank). Gates assert the chosen outcome only.
- **F52 positive control**: a genuine match must return unmarked results, so "warn always" cannot pass.
  The low-confidence marker must be **named in the plan** (wire field + doc row) before implementation.
- **F72 positive control**: the replacement diagnostic must carry policy + length/hash — suppression
  alone must not satisfy the gate.
- **F7** pins the exact code (`FailedToOpenEncryptedBank = 2`) or the owner's chosen alternative, and
  asserts a one-line message with no SQLite/parameter text (negative-only today).
- **F37** — the target exit code (130?) is **not ratified**; it joins the owner-decision list before the
  gate can name it. The gate is written deterministically (injected cancellation or direct handler call,
  per `AppRunnerShutdownCancellationTests.cs:23`) rather than by racing a real SIGINT.
- **F38 revised (R9)**: `ServeArguments` is **shared** by the proxy and the CLI verbs, so a short
  `--idle-timeout` there also stops the backend under an idle interactive client. Either scope it to the
  CLI path (a new argument), or take the ownership route (return the PID from `IBackendLauncher` and stop
  only what this command started — `BackendResult` carries no PID today). The gate states which.
- **F26** gate strengthens: the acceptance is "the whole write is deletable" (or the full hash set is
  reported), **not** "N was advertised" — the finding's cost is the rest staying searchable.

---

## Corrections to Wave 4

- **F9 — the gate was self-contradictory** (asserted a pass). Replacement: set caps to today's measured
  1046/26, prove load-bearing by lowering to 1045/25 and watching red, then restore; add cap facts for
  `MemorySchema.cs`/`SyncService.cs` set to today's counts, each watched red at count−1.
- **F10 — the red is compile-time**, not behavioural; label it so, and add the runtime half (with
  `Default` holding a conflicting map, the injected fixture decides the key).
- **F65** — the test must parse `CODE_REGEX` **out of `build.yml`** (not duplicate it) and must itself be
  in the CI pytest list, or it never runs.
- **F66** — the exclusion map is **bounded** (the chromadb collection-error files) or every entry carries
  a reason; the new test is wired into `build.yml` in the same change.
- **F67** — replace the one-off crash observation with a **workflow-structure gate** (YAML parse:
  `mkdir -p dumps` precedes the test step and `upload-artifact` runs on `failure()`), following
  `WorkflowActionPinTests.cs:15`; or add a deliberate crash-probe step that fails the job without a dump.
- **F63** — build the checker with an **offline fixture mode**; unit-test fixture-missing → nonzero,
  fixture-present → zero; wire into `release.yml`. The watched red is the fixture, not live nuget.org.
- **F64(a)** — gate on **workflow YAML** (a scheduled job exists and invokes `nightly-triage.py` with the
  ledger) — red today, no `schedule:` anywhere — plus the ledger dry run.
- **F64(b)** — **no seam exists** for retry diagnostics (retries are external `xRetry.v3` attributes).
  The fix must create the observable hook first; the gate is written against that hook and the plan names it.
- **F4** — add the two historical spellings to `PATHS` **and** to the test fixtures, and wire the guard
  into CI (it is also one of F66's ungated files).
- **Docs sweep — split per item** (it was vague and mislabelled): F43 → a test parsing
  `agent-memory-server.md`'s restart paragraph; F50 → a test parsing the tutorial's Step 4 for `sessionId`;
  F42 → a new two-process `quiet.log` test (none exists; `QuietLoggingTests` is single-process);
  F51 → a behaviour assertion on `--port` placement, not a doc-table one. **F61/F62/F71 are removed from
  this row** (they are not doc claims).
- **All test commands in the plan drop `--nologo`** — on this repo's MTP path
  `dotnet test --no-build --nologo` discovers 0 tests and exits 5, so a gate documented that way is vacuous.

---

## Owner decisions (revised, complete)

1. **F22** — stamp the surviving agent-requested row, or exempt it?
2. **F31** — does re-created content win over its tombstone, or is a deleted hash permanently poisoned?
3. **F38** — scope a short idle timeout to the CLI path, or plumb backend ownership and stop only what the
   command started?
4. **F70** — which of the three options: private spawn, mutual proof, or accept-and-document?
5. **F37** — is 130 the wanted interrupt code? *(new: v1 asserted it without a ruling)*
6. **F24** — refuse the `context`+`workspace_id` combination, or let `workspace_id` win?
7. **F52** — absolute-relevance floor, or an explicit unranked marker (named in the plan)?
8. **F7** — which exit code for a bank that exists but is not SQLite?
9. **F49/F39** — in scope for Wave 3, or explicitly out?
10. **F9/F10** — re-tighten the ratchets and inject the alias map, or accept the slack (ruled trades)?
11. **F63** — must a tag be installable, or is tagged-but-unpublished acceptable (21 cases)?

## Coverage matrix (post-revision)

| finding | package | sufficient? |
|---|---|---|
| F6 (+F49/F39 deferred by decision 9) | P1.1 revised | yes |
| F22, F25 | P1.2 revised | yes |
| F70 | P1.3 options (decision 4) | yes, conditional on the ruling |
| F29, F30, F35 | P2.1 corrected | yes, with the scope-filter gate |
| F31 | P2.2 + convergence note | yes for the measured case; partial multi-replica (stated) |
| F19, F20 | P3.1/P3.2 (new) | yes |
| F26, F37, F38, F52, F53, F24, F7, F72 | Wave 3 revised | yes, one ruling each for F24/F37/F52/F7 |
| F4, F5, F8, F9, F10, F59, F63, F64, F65, F66, F67, F42, F43, F50, F51 | Wave 4 revised | yes, gates corrected per the audit |
| F23, F32, F40, F21, F36, and the remaining LOW/NIT set | deferred with reasons in v1's "out of scope"; to be scheduled after Wave 2 | planned, not yet packaged |

## Still open

- No gate has been executed; every "red today" is the record's measured evidence plus the audited code
  trace. The first implementation wave must watch each gate red before trusting it.
- Decisions 1–11 are unruled; four of them (F24, F37, F52, F7) block their gate wording outright.
- `serve --restart`'s foreign-name refusal means any F70 gate written against restart is vacuous — noted
  so a later reviewer does not "fix" it back.
