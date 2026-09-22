# Owner rulings — 2026-09-22 (14/14 answered, all APPROVE)

Source form: `docs/work/2026-09-22-project-scope-review/owner-decisions-review.html` → `owner-decisions-feedback.md`
Plan ruled on: `PHASE5-PLAN-v2.md` + `PHASE5-PLAN-v2.1-ADDENDUM.md`

## Blocking rulings

| id | ruling | implementation consequence |
|---|---|---|
| **K1** | **F70 → option (a): private spawn.** "lets do a" | The CLI/proxy never attaches to a pre-existing listener for a root it is about to use; it binds its own ephemeral port (or a `0600` unix socket) and attaches only behind an explicit flag. Gate: a squatter on the configured port never receives the token because **no attach happens**; positive control — the legacy attach path still works when explicitly asked for. Note the verifier's caveat: name-based identity was never a sufficient fix (the `/observability` name is self-asserted), which is why (a) is the only option that removes the exposure by construction. |
| **K2** | **F22 → stamp.** "stamp" | Agent-requested `QueueCandidate`s are stamped with the current `PromotionScorer.Version`, so `ClearStale` no longer deletes them and any re-score is honest. The write response's reason must reflect the real enqueue outcome (F25). |
| **K3** | **F31 → re-creation wins.** "wins" | Add the `created_at ≤ deleted_at` guard so a re-created fact is not re-deleted by its own tombstone. Gate: re-create → sync → content survives (red today: silently deleted, no error). |
| **K4** | **F37 → exit 130.** "130" | `catch (OperationCanceledException) when (Token.IsCancellationRequested) → 130` ahead of the catch-all, with a message saying the command changed nothing. Documented in the exit-code table. |
| **K5** | **F24 → `workspace_id` wins** (sandbox has priority). "workspace win - sandbox has priority" | When both are supplied inside an active workspace, the workspace wins and the row lands in the outbox. Gate asserts the **outbox**, and both tool descriptions are aligned to say so. |
| **K6** | **F52 → both.** "both" | Add an absolute-relevance floor **and** an explicit unranked marker. The marker's wire field is named in the implementation (no such field exists today), with a positive control: a genuine match returns unmarked results. |
| **K7** | **F7 → a distinct exit code, first available.** "BankCorrupteded error code, first available" | Codes 1–25 are taken and 8 is retired by design (ADR-0022), so the first available is **26**. Implement `public const int BankCorrupted = 26;` (spelling corrected from the ruling's "Corrupteded"), returned when the bank exists but is not a SQLite database; add it to the documented doctor table (which `HowToExitTableTests` asserts exactly) and stop the raw .NET parameter text escaping for the timestamp arm. |

## Scope and risk rulings

| id | ruling | consequence |
|---|---|---|
| **N1** | **F38 → leave the backend running.** "leave the backend running" | No lifetime change: the 4-hour idle watchdog is intended. The finding's residual is **disclosure** — the command must say the backend outlives it and how to stop it. Gate: the stderr line names the lifetime/stop path (red today: only "starting the backend on port N"). |
| **N2** | **F49 and F39 both in scope** (Wave 3). "both" | F49: move/document the project-scope token so it is not an unignored secret in a working tree. F39: stop a mistyped `--data-root` silently minting a bank. |
| **N3** | **F9/F10 → tighten.** "tighten" | Lower the size ratchets to today's measurements (1046 lines / 26 members) and add caps for `MemorySchema.cs` and `SyncService.cs`; and inject the alias map into the three Core key helpers and `ToolGate` instead of reading `ProcessIdAliasMap.Default`. |
| **N4** | **F63 → no tag↔package relation.** "no - there is no relation like that" | There is deliberately no relation between a GitHub release tag and a nuget package, so no reconciliation step is built. The package becomes a **documentation** change: state the release/publish model so a tag is not read as an installable version. F63's reconciliation gate collapses by ruling. |
| **N5** | **F8/F64 → remove both.** "remove both" | Delete the flake ledger machinery: `known-flakes.json`, `scripts/nightly-triage.py` and its pytest, and do **not** build a retry diagnostic. Consequence to note: this removes the last consumer of the ledger and the only documented use of `Speed=Nightly`'s unfiltered sweep; whether to restore a scheduled (labelless) Nightly is the one sub-question this ruling does not answer — recorded as still open. |
| **N6** | **F42 → the server owns `quiet.log`.** "server owns it" | Single writer: the `serve` process owns the file; the CLI's own logging does not append to it. Gate: a two-process run produces no torn lines (red today: 3 of 30 lines malformed in 5/5 runs). |
| **N7** | **F26 → `memory_delete` removes the whole memory (all N rows).** "memory_delete should remove whole memory - N rows" | Delete becomes whole-write-aware (by the write's path/hash set) rather than chunk-scoped; gate: a long write followed by `memory_delete(returnedHash)` leaves **zero** rows for that write (red today: 1 of N deleted, reported success). |

## Still open after the rulings

1. **Scheduled CI (F64's other half).** N5 removed the ledger and the triage script, but did not say whether a labelless scheduled Nightly should be restored. Today nothing unattended runs at all, and `Performance=Benchmark` has no backstop. Needs one more ruling.
2. **The 7721 server.** Asked twice, still unanswered: is `serve --data-root /Users/arasz/.ai-raccoon --port 7721` (PID 24554) the owner's? It has never been touched. Implementation must in any case never use the default root.
3. **K7's code name.** Implemented as `BankCorrupted` (26); if the owner wants the literal spelling from the ruling, say so.
## Rulings of 2026-09-22 evening (answered in chat)

| id | ruling | consequence |
|---|---|---|
| **K1a** | CLI settings verbs: "no own backend - attach - the same rules as usual - CLI - only proxy - if no server is running - we start it" | `CliSettingsBackend` keeps legacy attach-or-start on the configured port; its squatter token exposure is accepted and documented in ADR-0105. The MCP proxy keeps K1 private spawn, and must stop its private backend on shutdown (measured orphan defect: 3 commands → 3 live private backends). |
| **P1.2-a** | Provenance label: preserve | `agent-requested-share` survives a re-score (it is what marks the row as agent-requested). |
| **P1.2-b** | "yes it should have priority score" | `AgentRequestedScore` rises above the V2 scorer's real 0–4 range, so an agent request outranks every inference (ADR-0067:44 made true). |
| **W2-a** | Label-aware tombstones: "yes" | `sync_tombstones` gains `context_label`; a context delete stops reaching a peer's same-hash row under another label. |
| **N5-a** | Scheduled Nightly: "no nightly for now" | Nothing restored. |
