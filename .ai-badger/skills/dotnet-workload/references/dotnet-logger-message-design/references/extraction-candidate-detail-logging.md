# the project S4 — propose-mode candidate-detail logging (plan digest, 2026-08-06)

Finding: `CliCommandTree.cs:189` + class doc + README promised "propose logs ranked candidates";
`ExtractionHostedService.RunOnceAsync` discarded `result.Candidates` and `Log.Pass` (EventId 502) logged counts only.

## Decision: implement the logging (option a), not correct the docs
- The promise lived on FOUR surfaces (CLI help :189, class doc comment, README:100, ExtractCommands prose) — implementing was cheaper than correcting all four.
- The owner-approved follow-up list itself named the task "S4 (propose logs candidate details)" — framing is evidence of intent.
- The review's cited docs line (agent-memory-server.md:229-231) did NOT actually promise ranked candidates — the finding mis-cited; re-verify citations before planning.

## LoggerMessage design (landed in the plan)
- EventId 507, LogLevel.Information, propose mode only. Message:
  `"Extraction candidate #{Rank} for {ProjectId}: {Path} ({Reasons}) — {Preview}"`
  params: `int rank, string projectId, string path, string reasons, string preview`.
- `reasons = string.Join(", ", candidate.Reasons)` at the call site (template can't format collections).
- `preview = candidate.ValuePreview` — already truncated to 300 chars by `SharedExtractionService.Truncate`; reuse, no second constant.
- No Score field added to `ShareCandidate`: it would ripple into the MCP tool response (`MemoryTools` returns the records directly) — reasons encode the score breakdown; 1-based Rank conveys ordering.
- Promote mode stays counts-only (EventId 502/504): its outcome is the shared tier itself.
- Loop placement: inside the per-project try, after `Log.Pass`; no awaits → no new cancellation surface.
- Class EventId range: 500-506 used; 507 free. EventIds are per-category.

## RED tests (tests/the project.Tests/Unit/Extraction/ExtractionHostedServiceTests.cs)
- `RunOnce_ProposeMode_LogsRankedCandidateDetails` (RED): FakeLogger; two rows (one below the 1.0 floor); assert exactly one EventId-507 record, level Information, message contains `#1`, projectId, path, "organic-write", "cross-project", preview text; and a 502 record also present.
- `RunOnce_PromoteMode_LogsCountsOnly_NoCandidateDetails` (guard): 502 present + no 507 — non-vacuous via the 502 assertion.
- `NewStack` helper gets a `FakeLogger` overload (FakeLogger implements ILogger; don't replace NullLogger default).
- Precedent: `Unit/Mcp/LoggerMessageTests.cs` (`Collector.LatestRecord`, `record.Id.Id/Level/Message`).

## Docs sync under (a)
- No changes to CliCommandTree.cs:189, README:100, ExtractCommands.cs, class doc — they become true.
- One-line addition to agent-memory-server.md extract block stating propose logs the ranked candidates (path, preview, reasons).

## Dependencies / risks
- No Core changes; no MCP tool changes; no new packages (FakeLogger already referenced).
- Same-file coordination: S5 (OCE filter, :121-124) and S10 (CandidateLimit const, :20) also touch ExtractionHostedService.cs — different lines, trivial rebases; suggested order S5 → S4 → S10. F3 (UNIQUE index) unrelated.
- Risks: bounded console volume (≤20 × 300-char lines/project/30 min on stderr); previews are memory content but identical to the MCP Read-tier response (no new exposure); joined reasons lose per-reason structured granularity (accepted LoggerMessage limitation).
- `ExtractionConfigKeys.ParseMode`: unknown/null → Propose (fail-safe), so the propose logging path covers unknown modes too.
